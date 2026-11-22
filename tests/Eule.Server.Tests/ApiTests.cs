using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Eule.Server.Auth;
using Eule.Server.Data;
using Eule.Server.Notes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Eule.Server.Tests;

/// <summary>A clock the tests move by hand.</summary>
public sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>The real API on a throw-away SQLite file, with a fixed clock: Wed 18 Nov 2026, 10:00 in Istanbul.</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    readonly string dbPath = Path.Combine(Path.GetTempPath(), $"eule-api-{Guid.NewGuid():N}.db");
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 11, 18, 7, 0, 0, TimeSpan.Zero));

    /// <summary>The tests in one class share a server and all sign in; the real limit has its own test.</summary>
    protected virtual int LoginPerMinute => 1000;

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        b.UseEnvironment("Testing");
        b.UseSetting("RateLimit:LoginPerMinute", LoginPerMinute.ToString());
        b.UseSetting("Database:Provider", "sqlite");
        b.UseSetting("ConnectionStrings:Default", $"Data Source={dbPath};Pooling=False");
        b.UseSetting("Jwt:SigningKey", "test-signing-key-test-signing-key-0123456789");
        b.UseSetting("Jwt:Issuer", "https://test.local");
        b.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Clock);
        });
    }

    public async Task AddUserAsync(string email, string password, bool active = true)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EuleDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher<User>>();
        var user = new User { Email = email, DisplayName = email.Split('@')[0], IsActive = active, CreatedAt = Clock.Now.UtcDateTime };
        user.PasswordHash = hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(dbPath); } catch (IOException) { }
    }
}

public class ApiTests : IClassFixture<ApiFactory>
{
    readonly ApiFactory factory;
    static int counter;

    public ApiTests(ApiFactory factory) => this.factory = factory;

    async Task<(HttpClient Client, TokenResponse Tokens)> SignedIn(string? email = null)
    {
        email ??= $"user{Interlocked.Increment(ref counter)}@test.local";
        await factory.AddUserAsync(email, "correct horse battery");
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "correct horse battery", device = "test" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var tokens = (await res.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return (client, tokens);
    }

    static Task<HttpResponseMessage> Dictate(HttpClient c, string text, Guid? id = null) =>
        c.PostAsJsonAsync("/api/dictations", new { clientId = id ?? Guid.NewGuid(), text, timeZone = "Europe/Istanbul" });

    [Fact]
    public async Task Health_is_public()
    {
        var res = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Web_app_is_served_at_root_with_a_strict_policy()
    {
        var c = factory.CreateClient();
        var res = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        string html = await res.Content.ReadAsStringAsync();
        Assert.Contains("login-form", html);
        // Asset links carry a content hash so Cloudflare never serves an old copy.
        Assert.DoesNotContain("__V__", html);
        Assert.Matches(@"app\.js\?v=[0-9a-f]{10}""", html);
        Assert.Contains("script-src 'self'", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/app.js")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_look_the_same()
    {
        await factory.AddUserAsync("known@test.local", "correct horse battery");
        var c = factory.CreateClient();
        var wrong = await c.PostAsJsonAsync("/api/auth/login", new { email = "known@test.local", password = "nope-nope" });
        var unknown = await c.PostAsJsonAsync("/api/auth/login", new { email = "ghost@test.local", password = "nope-nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Email_is_case_insensitive_and_disabled_users_cannot_sign_in()
    {
        await factory.AddUserAsync("case@test.local", "correct horse battery");
        await factory.AddUserAsync("off@test.local", "correct horse battery", active: false);
        var c = factory.CreateClient();
        var ok = await c.PostAsJsonAsync("/api/auth/login", new { email = "  CASE@Test.Local ", password = "correct horse battery" });
        var off = await c.PostAsJsonAsync("/api/auth/login", new { email = "off@test.local", password = "correct horse battery" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, off.StatusCode);
    }

    [Fact]
    public async Task Malformed_body_is_a_bad_request_not_a_server_error()
    {
        var c = factory.CreateClient();
        var res = await c.PostAsync("/api/auth/login",
            new StringContent("""{"email":"a@b.c","password":{"value":"x"}}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Api_needs_a_token()
    {
        var c = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Dictate(c, "test")).StatusCode);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/notes")).StatusCode);
    }

    [Fact]
    public async Task Me_returns_the_signed_in_user()
    {
        var (c, tokens) = await SignedIn();
        var me = await c.GetFromJsonAsync<UserDto>("/api/me");
        Assert.Equal(tokens.User.Email, me!.Email);
    }

    [Fact]
    public async Task Access_token_expires_after_fifteen_minutes()
    {
        var (c, _) = await SignedIn();
        factory.Clock.Now = factory.Clock.Now.AddMinutes(16);
        try
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/me")).StatusCode);
        }
        finally { factory.Clock.Now = factory.Clock.Now.AddMinutes(-16); }
    }

    [Fact]
    public async Task Refresh_rotates_and_a_reused_token_ends_every_session()
    {
        var (c, first) = await SignedIn();
        var r1 = await c.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        var second = (await r1.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        // The old token again: looks stolen. It fails, and so does the new one now.
        var reuse = await c.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        var afterReuse = await c.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = second.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReuse.StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var (c, tokens) = await SignedIn();
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync("/api/auth/logout", new { refreshToken = tokens.RefreshToken })).StatusCode);
        var res = await c.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Dictation_saves_a_note_and_replies_with_what_was_understood()
    {
        var (c, _) = await SignedIn();
        var res = await Dictate(c, "Cumartesi saat 4'te toplantı var, unutma");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<DictationResponse>())!;

        Assert.Equal("note.created", body.Action);
        Assert.Equal("Kaydettim: Toplantı · Cumartesi 21 Kasım, 16:00", body.Reply);
        Assert.Equal("Toplantı", body.Note!.Title);
        Assert.Equal("Cumartesi saat 4'te toplantı var, unutma", body.Note.Text);
        // 16:00 in Istanbul (UTC+3) is 13:00 UTC.
        Assert.Equal(new DateTime(2026, 11, 21, 13, 0, 0, DateTimeKind.Utc), body.Note.DueAt);
        Assert.Equal(DateTimeKind.Utc, body.Note.DueAt!.Value.Kind);
        Assert.True(body.Note.HasTime);
        Assert.Equal("Europe/Istanbul", body.Note.TimeZone);
    }

    [Fact]
    public async Task Plain_note_has_no_time()
    {
        var (c, _) = await SignedIn();
        var body = (await (await Dictate(c, "süt ve ekmek al")).Content.ReadFromJsonAsync<DictationResponse>())!;
        Assert.Equal("Kaydettim: Süt ve ekmek al", body.Reply);
        Assert.Null(body.Note!.DueAt);
    }

    [Fact]
    public async Task Same_client_id_twice_saves_once()
    {
        var (c, _) = await SignedIn();
        var id = Guid.NewGuid();
        var a = (await (await Dictate(c, "yarın 10:00 rapor", id)).Content.ReadFromJsonAsync<DictationResponse>())!;
        var second = await Dictate(c, "yarın 10:00 rapor", id);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var b = (await second.Content.ReadFromJsonAsync<DictationResponse>())!;
        Assert.Equal(a.Note!.Id, b.Note!.Id);
        Assert.Single((await c.GetFromJsonAsync<List<NoteDto>>("/api/notes"))!);
    }

    [Fact]
    public async Task Invalid_dictations_are_rejected()
    {
        var (c, _) = await SignedIn();
        Assert.Equal(HttpStatusCode.BadRequest, (await Dictate(c, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Dictate(c, new string('a', 2001))).StatusCode);
        var noId = await c.PostAsJsonAsync("/api/dictations", new { text = "test" });
        Assert.Equal(HttpStatusCode.BadRequest, noId.StatusCode);
    }

    [Fact]
    public async Task Users_only_see_their_own_notes()
    {
        var (alice, _) = await SignedIn();
        var (bob, _) = await SignedIn();
        var note = (await (await Dictate(alice, "gizli not")).Content.ReadFromJsonAsync<DictationResponse>())!.Note!;

        Assert.Empty((await bob.GetFromJsonAsync<List<NoteDto>>("/api/notes"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PatchAsJsonAsync($"/api/notes/{note.Id}", new { done = true })).StatusCode);
        Assert.Single((await alice.GetFromJsonAsync<List<NoteDto>>("/api/notes"))!);
    }

    [Fact]
    public async Task Undo_deletes_and_sync_reports_the_deletion()
    {
        var (c, _) = await SignedIn();
        var note = (await (await Dictate(c, "yanlış not")).Content.ReadFromJsonAsync<DictationResponse>())!.Note!;
        var before = factory.Clock.Now.UtcDateTime.AddSeconds(-1);
        factory.Clock.Now = factory.Clock.Now.AddSeconds(5);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/notes/{note.Id}")).StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<List<NoteDto>>("/api/notes"))!);
        var changes = (await c.GetFromJsonAsync<List<NoteDto>>($"/api/notes?since={before:O}"))!;
        Assert.Contains(changes, n => n.Id == note.Id && n.Deleted);
    }

    [Fact]
    public async Task Notes_can_be_completed_and_renamed()
    {
        var (c, _) = await SignedIn();
        var note = (await (await Dictate(c, "yarın sabah koşu")).Content.ReadFromJsonAsync<DictationResponse>())!.Note!;
        var res = await c.PatchAsJsonAsync($"/api/notes/{note.Id}", new { title = "Sabah koşusu", done = true });
        var updated = (await res.Content.ReadFromJsonAsync<NoteDto>())!;
        Assert.Equal("Sabah koşusu", updated.Title);
        Assert.True(updated.Done);
        Assert.Equal(note.DueAt, updated.DueAt);
    }
}

/// <summary>The production limit: 10 sign-in attempts a minute.</summary>
public sealed class RealLimitFactory : ApiFactory
{
    protected override int LoginPerMinute => 10;
}

public class RateLimitTests : IClassFixture<RealLimitFactory>
{
    readonly RealLimitFactory factory;
    public RateLimitTests(RealLimitFactory factory) => this.factory = factory;

    [Fact]
    public async Task Login_is_limited_to_ten_attempts_a_minute()
    {
        var c = factory.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (int i = 0; i < 11; i++)
            codes.Add((await c.PostAsJsonAsync("/api/auth/login", new { email = "x@test.local", password = "wrong-guess" })).StatusCode);
        Assert.All(codes.Take(10), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[10]);
    }
}

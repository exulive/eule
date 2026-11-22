using Eule.Core.Cloud;
using Eule.Core.Storage;
using Eule.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eule.Server.Tests;

sealed class MemoryAccountStore : IAccountStore
{
    public Account? Saved;
    public Account? Load() => Saved;
    public void Save(Account account) => Saved = account;
    public void Clear() => Saved = null;
}

/// <summary>The desktop client (Eule.Core.Cloud) against the real API running in-process.</summary>
public class ClientTests : IClassFixture<ApiFactory>
{
    const string Password = "correct horse battery";
    readonly ApiFactory factory;
    static int counter;

    public ClientTests(ApiFactory factory) => this.factory = factory;

    async Task<string> NewUser()
    {
        string email = $"client{Interlocked.Increment(ref counter)}@test.local";
        await factory.AddUserAsync(email, Password);
        return email;
    }

    EuleApiClient Client(MemoryAccountStore store) => new(factory.CreateClient(), store, factory.Clock);

    /// <summary>A client whose server address goes nowhere: every call is a network failure.</summary>
    EuleApiClient OfflineClient(MemoryAccountStore store) =>
        new(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/"), Timeout = TimeSpan.FromSeconds(2) }, store, factory.Clock);

    [Fact]
    public async Task Wrong_password_is_reported_as_such()
    {
        string email = await NewUser();
        var api = Client(new MemoryAccountStore());
        var ex = await Assert.ThrowsAsync<ApiException>(() => api.LoginAsync(email, "wrong-password", "test"));
        Assert.Equal(ApiError.InvalidCredentials, ex.Kind);
        Assert.False(api.IsSignedIn);
    }

    [Fact]
    public async Task Sign_in_is_remembered_by_the_store()
    {
        string email = await NewUser();
        var store = new MemoryAccountStore();
        await Client(store).LoginAsync(email, Password, "test");
        Assert.Equal(email, store.Saved!.Email);

        // A new client (next app start) is signed in from the store alone.
        var again = Client(store);
        Assert.True(again.IsSignedIn);
        var notes = await again.GetNotesAsync();
        Assert.Empty(notes);
    }

    [Fact]
    public async Task Dictation_is_saved_and_the_outbox_is_emptied()
    {
        string email = await NewUser();
        using var db = new EuleDatabase(":memory:");
        var api = Client(new MemoryAccountStore());
        await api.LoginAsync(email, Password, "test");
        var service = new DictationService(api, db, factory.Clock);

        var outcome = await service.SubmitAsync("Cumartesi saat 4'te toplantı var, unutma", "voice");

        Assert.Equal(DictationStatus.Saved, outcome.Status);
        Assert.StartsWith("Kaydettim: Toplantı", outcome.Result!.Reply);
        Assert.Equal(0, service.PendingCount);
    }

    [Fact]
    public async Task At_the_monthly_limit_the_dictation_is_kept_and_the_reason_shown()
    {
        string email = await NewUser();
        using (var scope = factory.Services.CreateScope())
        {
            var sdb = scope.ServiceProvider.GetRequiredService<EuleDbContext>();
            (await sdb.Users.SingleAsync(u => u.Email == email)).DictationLimit = 1;
            await sdb.SaveChangesAsync();
        }
        using var db = new EuleDatabase(":memory:");
        var api = Client(new MemoryAccountStore());
        await api.LoginAsync(email, Password, "test");
        var service = new DictationService(api, db, factory.Clock);

        var first = await service.SubmitAsync("Süt al", "text");
        Assert.Equal(DictationStatus.Saved, first.Status);

        var second = await service.SubmitAsync("Ekmek al", "text");
        Assert.Equal(DictationStatus.LimitReached, second.Status);
        Assert.Contains("1 dikte hakkın doldu", second.Error);
        Assert.Equal(1, service.PendingCount); // not lost

        // History and undo through the client.
        var history = await api.GetDictationsAsync();
        var entry = Assert.Single(history);
        Assert.Equal("Süt al", entry.Text);
        Assert.Equal("todo", Assert.Single(entry.Notes).Kind);
        await api.DeleteDictationAsync(first.Result!.DictationId);
        Assert.Empty(await api.GetDictationsAsync());
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_silently_and_the_refresh_token_rotates()
    {
        string email = await NewUser();
        var store = new MemoryAccountStore();
        var api = Client(store);
        await api.LoginAsync(email, Password, "test");
        string firstRefresh = store.Saved!.RefreshToken;

        factory.Clock.Now = factory.Clock.Now.AddMinutes(20);
        try
        {
            var r = await api.DictateAsync(Guid.NewGuid(), "süt al", "text", "Europe/Istanbul");
            Assert.Equal("note.created", r.Action);
            Assert.NotEqual(firstRefresh, store.Saved!.RefreshToken);
        }
        finally { factory.Clock.Now = factory.Clock.Now.AddMinutes(-20); }
    }

    [Fact]
    public async Task Ended_session_signs_out_and_keeps_the_note_for_later()
    {
        string email = await NewUser();
        using var db = new EuleDatabase(":memory:");
        var store = new MemoryAccountStore();
        var api = Client(store);
        await api.LoginAsync(email, Password, "test");
        bool signedOut = false;
        api.SignedOut += () => signedOut = true;

        // The account is disabled on the server: every session ends.
        using (var scope = factory.Services.CreateScope())
        {
            var sdb = scope.ServiceProvider.GetRequiredService<EuleDbContext>();
            var user = await sdb.Users.FirstAsync(u => u.Email == email);
            user.IsActive = false;
            await sdb.SaveChangesAsync();
        }
        factory.Clock.Now = factory.Clock.Now.AddMinutes(20); // force a refresh
        try
        {
            var outcome = await new DictationService(api, db, factory.Clock).SubmitAsync("önemli not", "voice");
            Assert.Equal(DictationStatus.NeedsSignIn, outcome.Status);
            Assert.True(signedOut);
            Assert.Null(store.Saved);
            Assert.Single(db.OutboxPending()); // nothing lost
        }
        finally { factory.Clock.Now = factory.Clock.Now.AddMinutes(-20); }
    }

    [Fact]
    public async Task Offline_dictation_is_queued_and_delivered_once_later()
    {
        string email = await NewUser();
        using var db = new EuleDatabase(":memory:");
        var store = new MemoryAccountStore();
        await Client(store).LoginAsync(email, Password, "test");

        // No connection.
        var offline = new DictationService(OfflineClient(store), db, factory.Clock);
        var outcome = await offline.SubmitAsync("yarın 10:00 rapor", "voice");
        Assert.Equal(DictationStatus.Queued, outcome.Status);
        Assert.Equal(1, offline.PendingCount);

        // Back online: the queued note goes through, exactly once even if flushed twice.
        var online = new DictationService(Client(store), db, factory.Clock);
        var delivered = new List<DictationResult>();
        online.DeliveredLater += delivered.Add;
        Assert.Equal(1, await online.FlushAsync());
        Assert.Equal(0, await online.FlushAsync());
        Assert.Single(delivered);
        Assert.Equal(0, online.PendingCount);
        Assert.Single(await Client(store).GetNotesAsync());
    }

    [Fact]
    public async Task Undo_deletes_the_note()
    {
        string email = await NewUser();
        var api = Client(new MemoryAccountStore());
        await api.LoginAsync(email, Password, "test");
        var r = await api.DictateAsync(Guid.NewGuid(), "yanlış not", "text", "Europe/Istanbul");
        await api.DeleteNoteAsync(r.Note!.Id);
        Assert.Empty(await api.GetNotesAsync());
        await api.DeleteNoteAsync(r.Note.Id); // twice is fine
    }

    [Fact]
    public async Task Logout_forgets_the_account_and_ends_the_session_on_the_server()
    {
        string email = await NewUser();
        var store = new MemoryAccountStore();
        var api = Client(store);
        await api.LoginAsync(email, Password, "test");
        string refresh = store.Saved!.RefreshToken;

        await api.LogoutAsync();
        Assert.Null(store.Saved);
        Assert.False(api.IsSignedIn);

        // The old refresh token no longer works.
        var stolen = new MemoryAccountStore { Saved = new Account(email, "x", refresh, DateTime.UtcNow.AddDays(1)) };
        var ex = await Assert.ThrowsAsync<ApiException>(() => Client(stolen).GetNotesAsync());
        Assert.Equal(ApiError.SignedOut, ex.Kind);
    }

    [Fact]
    public void Time_zone_is_sent_as_an_iana_id()
    {
        string id = DictationService.LocalTimeZoneId();
        Assert.True(TimeZoneInfo.TryFindSystemTimeZoneById(id, out _));
        Assert.Contains('/', id.Replace("UTC", "Etc/UTC"));
    }
}

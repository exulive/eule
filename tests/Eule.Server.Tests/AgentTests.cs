using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eule.Server.Agents;
using Eule.Server.Auth;
using Eule.Server.Data;
using Eule.Server.Notes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eule.Server.Tests;

/// <summary>Stands in for api.openai.com: records the requests and answers with whatever the test sets.</summary>
public sealed class FakeOpenAi : HttpMessageHandler
{
    public List<JsonNode> Requests { get; } = [];
    public List<string?> AuthHeaders { get; } = [];
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    /// <summary>The items the "model" returns, as the JSON text inside the Responses API message.</summary>
    public string ItemsJson { get; set; } = """{"items":[]}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!);
        AuthHeaders.Add(request.Headers.Authorization?.ToString());
        if (Status != HttpStatusCode.OK)
            return new HttpResponseMessage(Status) { Content = new StringContent("""{"error":{"message":"boom"}}""") };
        var body = new JsonObject
        {
            ["id"] = "resp_test",
            ["status"] = "completed",
            ["output"] = new JsonArray(
                new JsonObject { ["type"] = "reasoning", ["summary"] = new JsonArray() },
                new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = ItemsJson }),
                }),
            ["usage"] = new JsonObject { ["input_tokens"] = 900, ["output_tokens"] = 120, ["total_tokens"] = 1020 },
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
}

/// <summary>The API with an OpenAI key, a fake OpenAI and a monthly limit of 3 dictations.</summary>
public sealed class AiApiFactory : ApiFactory
{
    public FakeOpenAi OpenAi { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        base.ConfigureWebHost(b);
        b.UseSetting("OpenAI:ApiKey", "sk-test-not-real");
        b.UseSetting("Limits:DictationsPerMonth", "3");
        b.ConfigureTestServices(s =>
            s.AddHttpClient(OpenAiDictationAgent.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => OpenAi));
    }
}

public class AgentTests : IClassFixture<AiApiFactory>
{
    readonly AiApiFactory factory;
    static int counter;

    public AgentTests(AiApiFactory factory) => this.factory = factory;

    async Task<HttpClient> SignedIn()
    {
        string email = $"ai{Interlocked.Increment(ref counter)}@test.local";
        await factory.AddUserAsync(email, "correct horse battery");
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "correct horse battery" });
        var tokens = (await res.Content.ReadFromJsonAsync<TokenResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    static Task<HttpResponseMessage> Dictate(HttpClient c, string text, Guid? id = null) =>
        c.PostAsJsonAsync("/api/dictations", new { clientId = id ?? Guid.NewGuid(), text, timeZone = "Europe/Istanbul", source = "voice" });

    [Fact]
    public async Task The_model_rewrites_and_files_each_item()
    {
        var c = await SignedIn();
        factory.OpenAi.Status = HttpStatusCode.OK;
        factory.OpenAi.ItemsJson = """
            {"items":[
              {"kind":"event","title":"Ayşe'yi ara","body":"","date":"2026-11-19","time":"15:00"},
              {"kind":"todo","title":"Süt al","body":"Yarım yağlı olsun.","date":null,"time":null}
            ]}
            """;

        var res = await Dictate(c, "ee yarın saat üçte ayşeyi aramam lazım bir de süt al yarım yağlı olsun");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<DictationResponse>())!;

        Assert.Equal(2, body.Notes.Count);
        var call = body.Notes[0];
        Assert.Equal("event", call.Kind);
        Assert.Equal("Ayşe'yi ara", call.Title);
        Assert.True(call.HasTime);
        Assert.Equal(new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc), call.DueAt); // 15:00 Istanbul
        Assert.Equal("todo", body.Notes[1].Kind);
        Assert.Equal("Yarım yağlı olsun.", body.Notes[1].Body);
        Assert.All(body.Notes, n => Assert.Equal(body.DictationId, n.DictationId));
        Assert.StartsWith("2 şey ekledim: Ayşe'yi ara · ", body.Reply);
        Assert.Equal(1, body.Usage.Used);
        Assert.Equal(3, body.Usage.Limit);

        // What went to OpenAI: the configured model, no deliberation, strict JSON, the local time and the words.
        var sent = factory.OpenAi.Requests[^1];
        Assert.Equal("gpt-6-luna", sent["model"]!.GetValue<string>());
        Assert.Equal("none", sent["reasoning"]!["effort"]!.GetValue<string>());
        Assert.Equal("json_schema", sent["text"]!["format"]!["type"]!.GetValue<string>());
        Assert.False(sent["store"]!.GetValue<bool>());
        Assert.Contains("2026-11-18 Çarşamba 10:00", sent["input"]!.GetValue<string>());
        Assert.Contains("süt al", sent["input"]!.GetValue<string>());
        Assert.Equal("Bearer sk-test-not-real", factory.OpenAi.AuthHeaders[^1]);

        // Tokens are kept for cost tracking.
        using var scope = factory.Services.CreateScope();
        var d = await scope.ServiceProvider.GetRequiredService<EuleDbContext>().Dictations.FindAsync(body.DictationId);
        Assert.Equal((900, 120), (d!.InputTokens, d.OutputTokens));
        Assert.Equal("ai:gpt-6-luna", d.Agent);
    }

    [Fact]
    public async Task An_event_without_a_day_becomes_a_todo()
    {
        var c = await SignedIn();
        factory.OpenAi.Status = HttpStatusCode.OK;
        factory.OpenAi.ItemsJson = """{"items":[{"kind":"event","title":"Toplantı","body":"","date":null,"time":"10:00"}]}""";
        var body = (await (await Dictate(c, "toplantı")).Content.ReadFromJsonAsync<DictationResponse>())!;
        Assert.Equal("todo", body.Note!.Kind);
        Assert.Null(body.Note.DueAt);
        Assert.Equal("Yapılacaklara ekledim: Toplantı", body.Reply);
    }

    [Fact]
    public async Task When_the_model_fails_the_rules_still_save_it()
    {
        var c = await SignedIn();
        factory.OpenAi.Status = HttpStatusCode.InternalServerError;
        try
        {
            var res = await Dictate(c, "Cumartesi saat 4'te toplantı");
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            var body = (await res.Content.ReadFromJsonAsync<DictationResponse>())!;
            Assert.Equal("Kaydettim: Toplantı · Cumartesi 21 Kasım, 16:00", body.Reply);
            Assert.Equal("event", body.Note!.Kind);
        }
        finally { factory.OpenAi.Status = HttpStatusCode.OK; }
    }

    [Fact]
    public async Task Empty_model_answer_falls_back_to_the_rules()
    {
        var c = await SignedIn();
        factory.OpenAi.Status = HttpStatusCode.OK;
        factory.OpenAi.ItemsJson = """{"items":[{"kind":"todo","title":"   ","body":"","date":null,"time":null}]}""";
        var body = (await (await Dictate(c, "Süt ve ekmek al")).Content.ReadFromJsonAsync<DictationResponse>())!;
        Assert.Equal("Kaydettim: Süt ve ekmek al", body.Reply);
    }

    [Fact]
    public async Task The_monthly_limit_refuses_more_and_deleting_does_not_give_it_back()
    {
        var c = await SignedIn();
        factory.OpenAi.Status = HttpStatusCode.OK;
        factory.OpenAi.ItemsJson = """{"items":[{"kind":"todo","title":"Bir iş","body":"","date":null,"time":null}]}""";
        var first = Guid.NewGuid();
        var firstBody = (await (await Dictate(c, "bir", first)).Content.ReadFromJsonAsync<DictationResponse>())!;
        await Dictate(c, "iki");
        await Dictate(c, "üç");

        int calls = factory.OpenAi.Requests.Count;
        var refused = await Dictate(c, "dört");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = (await refused.Content.ReadFromJsonAsync<ProblemDetails>())!;
        Assert.Equal("dictation_limit", problem.Title);
        Assert.Contains("3 dikte hakkın doldu", problem.Detail);
        Assert.Equal(calls, factory.OpenAi.Requests.Count); // the model is not asked

        // A retry of an accepted dictation is still answered.
        Assert.Equal(HttpStatusCode.OK, (await Dictate(c, "bir", first)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/dictations/{firstBody.DictationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Dictate(c, "beş")).StatusCode);

        var me = await c.GetFromJsonAsync<JsonElement>("/api/me?timeZone=Europe/Istanbul");
        Assert.Equal(3, me.GetProperty("usage").GetProperty("used").GetInt32());
        Assert.Equal(3, me.GetProperty("usage").GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task A_users_own_limit_overrides_the_default()
    {
        var c = await SignedIn();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EuleDbContext>();
            var u = await db.Users.SingleAsync(x => x.Email == $"ai{counter}@test.local");
            u.DictationLimit = 1;
            await db.SaveChangesAsync();
        }
        factory.OpenAi.ItemsJson = """{"items":[{"kind":"note","title":"Fikir","body":"","date":null,"time":null}]}""";
        Assert.Equal(HttpStatusCode.Created, (await Dictate(c, "bir fikir")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Dictate(c, "bir fikir daha")).StatusCode);
    }

    [Fact]
    public async Task History_lists_dictations_with_their_items_and_delete_removes_both()
    {
        var c = await SignedIn();
        factory.OpenAi.Status = HttpStatusCode.OK;
        factory.OpenAi.ItemsJson = """
            {"items":[{"kind":"todo","title":"A","body":"","date":null,"time":null},
                      {"kind":"note","title":"B","body":"","date":null,"time":null}]}
            """;
        var body = (await (await Dictate(c, "a ve b")).Content.ReadFromJsonAsync<DictationResponse>())!;

        var history = (await c.GetFromJsonAsync<List<DictationDto>>("/api/dictations"))!;
        var entry = Assert.Single(history);
        Assert.Equal("a ve b", entry.Text);
        Assert.Equal(new[] { "A", "B" }, entry.Notes.Select(n => n.Title));

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/dictations/{body.DictationId}")).StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<List<DictationDto>>("/api/dictations"))!);
        Assert.Empty((await c.GetFromJsonAsync<List<NoteDto>>("/api/notes"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/dictations/{body.DictationId}")).StatusCode);
    }

    [Fact]
    public async Task Someone_elses_dictation_cannot_be_deleted()
    {
        var owner = await SignedIn();
        factory.OpenAi.ItemsJson = """{"items":[{"kind":"todo","title":"Gizli","body":"","date":null,"time":null}]}""";
        var body = (await (await Dictate(owner, "gizli")).Content.ReadFromJsonAsync<DictationResponse>())!;
        var other = await SignedIn();
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/dictations/{body.DictationId}")).StatusCode);
        Assert.Empty((await other.GetFromJsonAsync<List<DictationDto>>("/api/dictations"))!);
    }
}

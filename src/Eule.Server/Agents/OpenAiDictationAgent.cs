using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eule.Parsing;
using Eule.Server.Data;

namespace Eule.Server.Agents;

/// <summary>OpenAI settings, one key for the whole system (OpenAI__ApiKey in production). No key = no AI.</summary>
public sealed class OpenAiOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gpt-6-luna";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    /// <summary>Luna reasons at "medium" by default; a dictation needs no deliberation and should answer fast.</summary>
    public string ReasoningEffort { get; set; } = "none";
    public int TimeoutSeconds { get; set; } = 12;
    public int MaxOutputTokens { get; set; } = 1000;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// The personal note agent: the language model reads the dictation, splits it into items, rewrites each one
/// clearly and decides where it belongs: the agenda (a date), the to-do list or plain notes. Dates are resolved
/// against the user's local time. If the model is unreachable, slow or answers nonsense, the rule-based
/// <see cref="SaveNoteAgent"/> saves the dictation instead, so nothing is ever lost.
/// </summary>
public sealed class OpenAiDictationAgent(
    IHttpClientFactory httpFactory, OpenAiOptions options, EuleDbContext db, SaveNoteAgent fallback,
    ILogger<OpenAiDictationAgent> log) : IDictationAgent
{
    public const string HttpClientName = "openai";
    public const int MaxItems = 5;

    public async Task<AgentOutcome> HandleAsync(DictationContext ctx, CancellationToken ct)
    {
        ModelAnswer answer;
        try
        {
            answer = await AskAsync(ctx, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Dil modeli kullanılamadı, kurallı ayrıştırıcıya geçildi");
            return await fallback.HandleAsync(ctx, ct);
        }

        var notes = new List<Note>();
        foreach (var item in answer.Items.Take(MaxItems))
        {
            var note = ToNote(item, ctx);
            if (note != null) notes.Add(note);
        }
        if (notes.Count == 0)
        {
            log.LogWarning("Dil modeli kullanılabilir bir öğe döndürmedi, kurallı ayrıştırıcıya geçildi");
            var rules = await fallback.HandleAsync(ctx, ct);
            return rules with { InputTokens = answer.InputTokens, OutputTokens = answer.OutputTokens };
        }
        db.Notes.AddRange(notes);
        return new AgentOutcome("note.created", notes, Reply(notes, ctx), "ai:" + options.Model,
            answer.InputTokens, answer.OutputTokens);
    }

    // ------------------------------------------------------------------ the model

    public sealed record Item(string Kind, string Title, string Body, string? Date, string? Time);
    sealed record ModelAnswer(List<Item> Items, int InputTokens, int OutputTokens);

    public const string Instructions = """
        Sen Eule'nin kişisel not ve ajanda asistanısın. Kullanıcı sesle ya da yazıyla kısa bir dikte gönderir.
        Sesli diktede metin bilgisayarda otomatik yazıya çevrilmiştir; küçük tanıma hataları olabilir.

        Görevin diktedeki her ayrı işi bir öğe olarak çıkarmak (çoğu zaman tek öğe, en fazla 5) ve her birini:
        - title: Türkçe, kısa ve anlaşılır biçimde yeniden yaz (en fazla 80 karakter). Örnek: "Ayşe'yi ara",
          "Diş hekimi randevusu", "Faturayı öde". "Şey", "ee", "bana hatırlat", "not al" gibi dolgu ve komut
          sözcüklerini at. Anlamı değiştirme, bilgi uydurma. Açık bir tanıma hatası varsa düzelt. Kişi ve yer
          adlarını koru.
        - body: Başlığa sığmayan önemli ayrıntı varsa temiz, tam cümlelerle yaz; yoksa boş dize.
        - kind: Belirli bir güne ya da saate bağlı randevu, toplantı, etkinlik, son tarihli iş → "event".
          Yapılacak bir iş ama belirli bir zamanı yok → "todo". Yapılacak iş olmayan bilgi, fikir, alıntı → "note".
        - date (YYYY-MM-DD) ve time (HH:mm, 24 saat): "Şu an" bilgisine göre hesapla. "Yarın", "haftaya salı",
          "ayın 5'i", "akşam", "öğlen" gibi ifadeleri çöz. Saat yoksa time null; tarih yoksa ikisi de null.
          Konuşan kendini düzeltirse ("salı, yok yok çarşamba") son söyleneni al. Sabah/akşam belirtilmeyen
          1 ile 7 arası saatler öğleden sonradır ("saat 3'te" → 15:00). Yalnızca saat söylendiyse ve o saat
          bugün geçtiyse yarını al. "event" için mutlaka date ver.

        Yalnızca verilen JSON şemasına uygun yanıt ver.
        """;

    static readonly JsonObject Schema = JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["items"],
          "properties": {
            "items": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["kind", "title", "body", "date", "time"],
                "properties": {
                  "kind": { "type": "string", "enum": ["event", "todo", "note"] },
                  "title": { "type": "string" },
                  "body": { "type": "string" },
                  "date": { "type": ["string", "null"] },
                  "time": { "type": ["string", "null"] }
                }
              }
            }
          }
        }
        """)!.AsObject();

    public static string Input(DictationContext ctx)
    {
        var now = ctx.LocalNow;
        string day = now.ToString("dddd", CultureInfo.GetCultureInfo("tr-TR"));
        string how = ctx.Source == "voice" ? "sesle" : "yazıyla";
        return $"Şu an: {now:yyyy-MM-dd} {day} {now:HH:mm} ({ctx.Zone.Id})\nDikte ({how}): {ctx.Text}";
    }

    async Task<ModelAnswer> AskAsync(DictationContext ctx, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["instructions"] = Instructions,
            ["input"] = Input(ctx),
            ["store"] = false,
            ["max_output_tokens"] = options.MaxOutputTokens,
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema", ["name"] = "dictation_items", ["strict"] = true, ["schema"] = Schema.DeepClone(),
                },
            },
        };
        if (!string.IsNullOrWhiteSpace(options.ReasoningEffort))
            body["reasoning"] = new JsonObject { ["effort"] = options.ReasoningEffort };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        var http = httpFactory.CreateClient(HttpClientName);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.BaseUrl), "responses"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);

        var started = DateTime.UtcNow;
        using var res = await http.SendAsync(req, timeout.Token);
        string json = await res.Content.ReadAsStringAsync(timeout.Token);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenAI {(int)res.StatusCode}: {Shorten(json)}");

        var answer = Parse(json);
        log.LogInformation("Dil modeli: {Items} öğe, {In}+{Out} token, {Ms} ms", answer.Items.Count,
            answer.InputTokens, answer.OutputTokens, (int)(DateTime.UtcNow - started).TotalMilliseconds);
        return answer;
    }

    /// <summary>Reads a Responses API answer: the JSON text of the message, and the token usage.</summary>
    static ModelAnswer Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int input = 0, output = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var i)) input = i.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var o)) output = o.GetInt32();
        }

        string? text = null;
        foreach (var item in root.GetProperty("output").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "message") continue;
            foreach (var part in item.GetProperty("content").EnumerateArray())
            {
                string? type = part.GetProperty("type").GetString();
                if (type == "refusal") throw new InvalidOperationException("Model reddetti: " + part.GetProperty("refusal").GetString());
                if (type == "output_text") text = part.GetProperty("text").GetString();
            }
        }
        if (text == null) throw new InvalidOperationException("Yanıtta metin yok (status: " +
            (root.TryGetProperty("status", out var s) ? s.GetString() : "?") + ")");

        var parsed = JsonSerializer.Deserialize<ItemsDto>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                     ?? throw new InvalidOperationException("Boş öğe listesi");
        return new ModelAnswer(parsed.Items ?? [], input, output);
    }

    sealed record ItemsDto(List<Item>? Items);

    // ------------------------------------------------------------------ items → notes

    Note? ToNote(Item item, DictationContext ctx)
    {
        string title = Clean(item.Title, 500);
        if (title.Length == 0) return null;
        string kind = NoteKinds.All.Contains(item.Kind) ? item.Kind : NoteKinds.Todo;

        DateTime? dueUtc = null;
        bool hasTime = false;
        if (DateOnly.TryParseExact(item.Date ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var local = date.ToDateTime(TimeOnly.MinValue);
            if (TimeOnly.TryParseExact(item.Time ?? "", ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                local = date.ToDateTime(time);
                hasTime = true;
            }
            dueUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), ctx.Zone);
        }
        // An agenda item needs a day; without one it is a to-do.
        if (kind == NoteKinds.Event && dueUtc == null) kind = NoteKinds.Todo;

        return new Note
        {
            UserId = ctx.User.Id,
            DictationId = ctx.DictationId,
            Kind = kind,
            Text = ctx.Text,
            Title = title,
            Body = Clean(item.Body, 2000),
            DueAt = dueUtc,
            HasTime = hasTime,
            TimeZone = ctx.Zone.Id,
            Source = ctx.Source,
            CreatedAt = ctx.UtcNow,
            UpdatedAt = ctx.UtcNow,
        };
    }

    static string Reply(List<Note> notes, DictationContext ctx)
    {
        string One(Note n) => n.DueAt is DateTime due
            ? $"{n.Title} · {NoteParser.Describe(TimeZoneInfo.ConvertTimeFromUtc(due, ctx.Zone), n.HasTime, ctx.LocalNow)}"
            : n.Title;
        if (notes.Count == 1)
        {
            var n = notes[0];
            string where = n.Kind switch
            {
                NoteKinds.Event => "Ajandaya ekledim",
                NoteKinds.Todo => "Yapılacaklara ekledim",
                _ => "Not aldım",
            };
            return $"{where}: {One(n)}";
        }
        return $"{notes.Count} şey ekledim: " + string.Join("; ", notes.Select(One));
    }

    static string Clean(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length > max ? s[..max].TrimEnd() : s;
    }

    static string Shorten(string s) => s.Length > 300 ? s[..300] + "…" : s;
}

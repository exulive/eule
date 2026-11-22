using Eule.Server.Agents;
using Eule.Server.Auth;
using Eule.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Eule.Server.Notes;

public sealed record DictationRequest(Guid ClientId, string Text, string? TimeZone, string? Source);
public sealed record NoteDto(
    Guid Id, string Text, string Title, DateTime? DueAt, bool HasTime, string TimeZone, bool Done, string Source,
    DateTime CreatedAt, DateTime UpdatedAt, bool Deleted, string Kind, string Body, Guid? DictationId)
{
    public static NoteDto From(Note n) => new(n.Id, n.Text, n.Title, n.DueAt, n.HasTime, n.TimeZone, n.Done, n.Source,
        n.CreatedAt, n.UpdatedAt, n.DeletedAt != null, n.Kind, n.Body, n.DictationId);
}
public sealed record DictationResponse(Guid DictationId, string Action, string Reply, NoteDto? Note,
    IReadOnlyList<NoteDto> Notes, UsageDto Usage);
/// <summary>One entry of the dictation history: what was said, when, and what became of it.</summary>
public sealed record DictationDto(Guid Id, string Text, string Source, DateTime ReceivedAt, string Agent, string Reply,
    IReadOnlyList<NoteDto> Notes);
public sealed record NotePatch(string? Title, DateTime? DueAt, bool? HasTime, bool? Done, bool? ClearDue);

public static class NoteEndpoints
{
    public const string DefaultTimeZone = "Europe/Istanbul";
    public const int MaxTextLength = 2000;

    public static void MapNotes(this WebApplication app)
    {
        // Dictation: the client sends what the user said (already turned into text on the user's computer).
        app.MapPost("/api/dictations", async (DictationRequest req, HttpContext http, EuleDbContext db,
            IDictationAgent agent, TimeProvider time, IConfiguration config, ILogger<DictationRequest> log,
            CancellationToken ct) =>
        {
            string text = (req.Text ?? "").Trim();
            if (req.ClientId == Guid.Empty || text.Length == 0 || text.Length > MaxTextLength)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["text"] = [$"1–{MaxTextLength} karakter olmalı; clientId gerekli."],
                });

            var userId = http.User.UserId();
            var user = await db.Users.FindAsync([userId], ct);
            if (user == null || !user.IsActive) return Results.Unauthorized();

            var zone = FindZone(req.TimeZone);
            var now = time.GetUtcNow().UtcDateTime;

            // Retried request (lost connection, double click): return what we did the first time.
            var previous = await db.Dictations.FirstOrDefaultAsync(d => d.UserId == userId && d.ClientId == req.ClientId, ct);
            if (previous != null)
            {
                var prevNotes = await ItemsOf(db, previous, ct);
                return Results.Ok(new DictationResponse(previous.Id, previous.Action, previous.Reply,
                    prevNotes.FirstOrDefault(), prevNotes, await Usage.ForAsync(db, user, config, zone, now, ct)));
            }

            // Monthly allowance. Checked before the model runs, so a refused dictation costs nothing.
            var usage = await Usage.ForAsync(db, user, config, zone, now, ct);
            if (usage.Used >= usage.Limit)
            {
                log.LogInformation("Dikte limiti doldu: {User} ({Used}/{Limit})", userId, usage.Used, usage.Limit);
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: Usage.LimitTitle,
                    detail: $"Bu ayki {usage.Limit} dikte hakkın doldu. " +
                            $"{usage.ResetsAt.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} tarihinde yenilenir.",
                    extensions: new Dictionary<string, object?> { ["usage"] = usage });
            }

            string source = req.Source is "text" ? "text" : "voice";
            var dictationId = Guid.NewGuid();
            var outcome = await agent.HandleAsync(new DictationContext(user, dictationId, text, zone, source, now), ct);

            var dictation = new Dictation
            {
                Id = dictationId,
                UserId = userId,
                ClientId = req.ClientId,
                Text = text,
                TimeZone = zone.Id,
                Source = source,
                ReceivedAt = now,
                Agent = outcome.Agent,
                Action = outcome.Action,
                NoteId = outcome.Note?.Id,
                Reply = outcome.Reply.Length > 1000 ? outcome.Reply[..1000] : outcome.Reply,
                InputTokens = outcome.InputTokens,
                OutputTokens = outcome.OutputTokens,
            };
            db.Dictations.Add(dictation);
            await db.SaveChangesAsync(ct);
            log.LogInformation("Dikte: {User} → {Action} ({Agent}, {Count} öğe)", userId, outcome.Action, outcome.Agent,
                outcome.Notes.Count);

            var notes = outcome.Notes.Select(NoteDto.From).ToList();
            return Results.Created($"/api/dictations/{dictation.Id}",
                new DictationResponse(dictation.Id, outcome.Action, dictation.Reply, notes.FirstOrDefault(), notes,
                    usage with { Used = usage.Used + 1 }));
        }).RequireAuthorization().RequireRateLimiting("user");

        // The dictation history, newest first, each with the items it produced (deleted items left out).
        app.MapGet("/api/dictations", async (int? take, HttpContext http, EuleDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            int n = Math.Clamp(take ?? 500, 1, 1000);
            var list = await db.Dictations.Where(d => d.UserId == userId && d.DeletedAt == null)
                .OrderByDescending(d => d.ReceivedAt).Take(n).ToListAsync(ct);
            var ids = list.Select(d => d.Id).ToList();
            var noteIds = list.Where(d => d.NoteId != null).Select(d => d.NoteId!.Value).ToList();
            var notes = await db.Notes.Where(x => x.UserId == userId && x.DeletedAt == null &&
                    ((x.DictationId != null && ids.Contains(x.DictationId.Value)) || noteIds.Contains(x.Id)))
                .ToListAsync(ct);
            return Results.Ok(list.Select(d => new DictationDto(d.Id, d.Text, d.Source, d.ReceivedAt, d.Agent, d.Reply,
                notes.Where(x => x.DictationId == d.Id || x.Id == d.NoteId).OrderBy(x => x.CreatedAt)
                    .Select(NoteDto.From).ToList())));
        }).RequireAuthorization().RequireRateLimiting("user");

        // Removes a dictation from the history together with everything it produced ("Geri al" lands here too).
        app.MapDelete("/api/dictations/{id:guid}", async (Guid id, HttpContext http, EuleDbContext db, TimeProvider time,
            CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var d = await db.Dictations.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId && x.DeletedAt == null, ct);
            if (d == null) return Results.NotFound();
            var now = time.GetUtcNow().UtcDateTime;
            d.DeletedAt = now;
            foreach (var note in await ItemsQuery(db, d).ToListAsync(ct))
            {
                note.DeletedAt = now;
                note.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization().RequireRateLimiting("user");

        var notes = app.MapGroup("/api/notes").RequireAuthorization().RequireRateLimiting("user");

        // Without "since": the current notes. With "since": every change after that moment, deletions included (sync).
        notes.MapGet("/", async (DateTime? since, HttpContext http, EuleDbContext db, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var q = db.Notes.Where(n => n.UserId == userId);
            if (since is DateTime s)
            {
                var sinceUtc = s.Kind == DateTimeKind.Utc ? s : s.ToUniversalTime();
                q = q.Where(n => n.UpdatedAt > sinceUtc);
            }
            else
            {
                // The current notes: the newest 1000, so a long history never hides today's notes.
                var current = await q.Where(n => n.DeletedAt == null).OrderByDescending(n => n.CreatedAt).Take(1000).ToListAsync(ct);
                return Results.Ok(current.Select(NoteDto.From));
            }
            var list = await q.OrderBy(n => n.UpdatedAt).Take(1000).ToListAsync(ct);
            return Results.Ok(list.Select(NoteDto.From));
        });

        notes.MapGet("/{id:guid}", async (Guid id, HttpContext http, EuleDbContext db, CancellationToken ct) =>
        {
            var note = await Own(db, http, id, ct);
            return note == null ? Results.NotFound() : Results.Ok(NoteDto.From(note));
        });

        notes.MapPatch("/{id:guid}", async (Guid id, NotePatch patch, HttpContext http, EuleDbContext db,
            TimeProvider time, CancellationToken ct) =>
        {
            var note = await Own(db, http, id, ct);
            if (note == null) return Results.NotFound();
            if (patch.Title is string title)
            {
                title = title.Trim();
                if (title.Length is 0 or > 500) return Results.ValidationProblem(new Dictionary<string, string[]> { ["title"] = ["1–500 karakter"] });
                note.Title = title;
            }
            if (patch.ClearDue == true) { note.DueAt = null; note.HasTime = false; }
            else if (patch.DueAt is DateTime due) note.DueAt = due.Kind == DateTimeKind.Utc ? due : due.ToUniversalTime();
            if (patch.HasTime is bool hasTime) note.HasTime = hasTime;
            if (patch.Done is bool done) note.Done = done;
            note.UpdatedAt = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return Results.Ok(NoteDto.From(note));
        });

        notes.MapDelete("/{id:guid}", async (Guid id, HttpContext http, EuleDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var note = await Own(db, http, id, ct);
            if (note == null) return Results.NotFound();
            var now = time.GetUtcNow().UtcDateTime;
            note.DeletedAt = now;
            note.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    /// <summary>The live items a dictation produced (older dictations are linked only through NoteId).</summary>
    static IQueryable<Note> ItemsQuery(EuleDbContext db, Dictation d) =>
        db.Notes.Where(n => n.UserId == d.UserId && n.DeletedAt == null && (n.DictationId == d.Id || n.Id == d.NoteId));

    static async Task<List<NoteDto>> ItemsOf(EuleDbContext db, Dictation d, CancellationToken ct) =>
        (await ItemsQuery(db, d).OrderBy(n => n.CreatedAt).ToListAsync(ct)).Select(NoteDto.From).ToList();

    /// <summary>A note of the signed-in user; someone else's note is "not found", never "forbidden".</summary>
    static async Task<Note?> Own(EuleDbContext db, HttpContext http, Guid id, CancellationToken ct)
    {
        var userId = http.User.UserId();
        return await db.Notes.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId && n.DeletedAt == null, ct);
    }

    public static TimeZoneInfo FindZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) return zone;
        return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZone);
    }
}

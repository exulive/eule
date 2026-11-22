using Eule.Server.Agents;
using Eule.Server.Auth;
using Eule.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Eule.Server.Notes;

public sealed record DictationRequest(Guid ClientId, string Text, string? TimeZone, string? Source);
public sealed record NoteDto(
    Guid Id, string Text, string Title, DateTime? DueAt, bool HasTime, string TimeZone, bool Done, string Source,
    DateTime CreatedAt, DateTime UpdatedAt, bool Deleted)
{
    public static NoteDto From(Note n) => new(n.Id, n.Text, n.Title, n.DueAt, n.HasTime, n.TimeZone, n.Done, n.Source,
        n.CreatedAt, n.UpdatedAt, n.DeletedAt != null);
}
public sealed record DictationResponse(Guid DictationId, string Action, string Reply, NoteDto? Note);
public sealed record NotePatch(string? Title, DateTime? DueAt, bool? HasTime, bool? Done, bool? ClearDue);

public static class NoteEndpoints
{
    public const string DefaultTimeZone = "Europe/Istanbul";
    public const int MaxTextLength = 2000;

    public static void MapNotes(this WebApplication app)
    {
        // Dictation: the client sends what the user said (already turned into text on the user's computer).
        app.MapPost("/api/dictations", async (DictationRequest req, HttpContext http, EuleDbContext db,
            IDictationAgent agent, TimeProvider time, ILogger<DictationRequest> log, CancellationToken ct) =>
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

            // Retried request (lost connection, double click): return what we did the first time.
            var previous = await db.Dictations.FirstOrDefaultAsync(d => d.UserId == userId && d.ClientId == req.ClientId, ct);
            if (previous != null)
            {
                var prevNote = previous.NoteId is Guid nid ? await db.Notes.FindAsync([nid], ct) : null;
                return Results.Ok(new DictationResponse(previous.Id, previous.Action, previous.Reply,
                    prevNote == null ? null : NoteDto.From(prevNote)));
            }

            var zone = FindZone(req.TimeZone);
            string source = req.Source is "text" ? "text" : "voice";
            var now = time.GetUtcNow().UtcDateTime;
            var outcome = await agent.HandleAsync(new DictationContext(user, text, zone, source, now), ct);

            var dictation = new Dictation
            {
                UserId = userId,
                ClientId = req.ClientId,
                Text = text,
                TimeZone = zone.Id,
                Source = source,
                ReceivedAt = now,
                Agent = agent.Name,
                Action = outcome.Action,
                NoteId = outcome.Note?.Id,
                Reply = outcome.Reply,
            };
            db.Dictations.Add(dictation);
            await db.SaveChangesAsync(ct);
            log.LogInformation("Dikte: {User} → {Action}", userId, outcome.Action);

            return Results.Created($"/api/notes/{outcome.Note?.Id}",
                new DictationResponse(dictation.Id, outcome.Action, outcome.Reply,
                    outcome.Note == null ? null : NoteDto.From(outcome.Note)));
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

        // "Geri al" on the client lands here.
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

    /// <summary>A note of the signed-in user; someone else's note is "not found", never "forbidden".</summary>
    static async Task<Note?> Own(EuleDbContext db, HttpContext http, Guid id, CancellationToken ct)
    {
        var userId = http.User.UserId();
        return await db.Notes.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId && n.DeletedAt == null, ct);
    }

    static TimeZoneInfo FindZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) return zone;
        return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZone);
    }
}

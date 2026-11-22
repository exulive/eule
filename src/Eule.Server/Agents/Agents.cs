using Eule.Parsing;
using Eule.Server.Data;

namespace Eule.Server.Agents;

/// <summary>A dictated sentence on its way to an agent, with everything needed to act on it.</summary>
public sealed record DictationContext(User User, Guid DictationId, string Text, TimeZoneInfo Zone, string Source, DateTime UtcNow)
{
    public DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(UtcNow, Zone);
}

/// <summary>
/// What an agent did. <see cref="Action"/> is a stable name the client can react to ("note.created").
/// <see cref="Notes"/> are added to the database context but not saved; the endpoint saves them with the dictation.
/// </summary>
public sealed record AgentOutcome(string Action, IReadOnlyList<Note> Notes, string Reply, string Agent,
    int InputTokens = 0, int OutputTokens = 0)
{
    public Note? Note => Notes.Count > 0 ? Notes[0] : null;
}

/// <summary>
/// Decides what to do with a dictation and does it. With an OpenAI key the <see cref="OpenAiDictationAgent"/>
/// understands and rewrites it; without one (or when the model fails) the <see cref="SaveNoteAgent"/> does.
/// The client only ever sends text and shows the reply, so agents can change without touching it.
/// </summary>
public interface IDictationAgent
{
    Task<AgentOutcome> HandleAsync(DictationContext ctx, CancellationToken ct);
}

/// <summary>Understands the time in Turkish with rules and saves the sentence as one item. Free and offline.</summary>
public sealed class SaveNoteAgent(EuleDbContext db) : IDictationAgent
{
    public const string AgentName = "save-note";

    public Task<AgentOutcome> HandleAsync(DictationContext ctx, CancellationToken ct)
    {
        var parsed = NoteParser.Parse(ctx.Text, ctx.LocalNow);
        DateTime? dueUtc = parsed.DueLocal is DateTime local
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), ctx.Zone)
            : null;

        var note = new Note
        {
            UserId = ctx.User.Id,
            DictationId = ctx.DictationId,
            Kind = dueUtc != null ? NoteKinds.Event : NoteKinds.Todo,
            Text = ctx.Text,
            Title = parsed.Title,
            DueAt = dueUtc,
            HasTime = parsed.HasTime,
            TimeZone = ctx.Zone.Id,
            Source = ctx.Source,
            CreatedAt = ctx.UtcNow,
            UpdatedAt = ctx.UtcNow,
        };
        db.Notes.Add(note);

        string reply = parsed.DueLocal is DateTime due
            ? $"Kaydettim: {parsed.Title} · {NoteParser.Describe(due, parsed.HasTime, ctx.LocalNow)}"
            : $"Kaydettim: {parsed.Title}";
        return Task.FromResult(new AgentOutcome("note.created", [note], reply, AgentName));
    }
}

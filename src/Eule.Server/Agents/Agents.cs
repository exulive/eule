using Eule.Parsing;
using Eule.Server.Data;

namespace Eule.Server.Agents;

/// <summary>A dictated sentence on its way to an agent, with everything needed to act on it.</summary>
public sealed record DictationContext(User User, string Text, TimeZoneInfo Zone, string Source, DateTime UtcNow)
{
    public DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(UtcNow, Zone);
}

/// <summary>What an agent did. <see cref="Action"/> is a stable name the client can react to ("note.created").</summary>
public sealed record AgentOutcome(string Action, Note? Note, string Reply);

/// <summary>
/// Decides what to do with a dictation and does it. Today there is one agent, <see cref="SaveNoteAgent"/>.
/// Later an AI agent (an LLM with tools: create note, add calendar event, send message…) plugs in here
/// without changing the endpoint or the client: the client only ever sends text and shows the reply.
/// </summary>
public interface IDictationAgent
{
    string Name { get; }
    Task<AgentOutcome> HandleAsync(DictationContext ctx, CancellationToken ct);
}

/// <summary>Understands the time in Turkish and saves the sentence as a note.</summary>
public sealed class SaveNoteAgent(EuleDbContext db) : IDictationAgent
{
    public string Name => "save-note";

    public async Task<AgentOutcome> HandleAsync(DictationContext ctx, CancellationToken ct)
    {
        var parsed = NoteParser.Parse(ctx.Text, ctx.LocalNow);
        DateTime? dueUtc = parsed.DueLocal is DateTime local
            ? TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), ctx.Zone)
            : null;

        var note = new Note
        {
            UserId = ctx.User.Id,
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
        return await Task.FromResult(new AgentOutcome("note.created", note, reply));
    }
}

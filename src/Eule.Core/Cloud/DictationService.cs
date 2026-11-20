using Eule.Core.Storage;

namespace Eule.Core.Cloud;

public enum DictationStatus
{
    /// <summary>The server saved it; <see cref="DictationOutcome.Result"/> has its reply.</summary>
    Saved,
    /// <summary>No connection or a server problem: kept on this computer and sent automatically later.</summary>
    Queued,
    /// <summary>Not signed in: kept and sent after signing in.</summary>
    NeedsSignIn,
    /// <summary>The server refused it (e.g. empty); it was not kept.</summary>
    Rejected,
}

public sealed record DictationOutcome(DictationStatus Status, DictationResult? Result = null, string? Error = null);

/// <summary>
/// Sends what the user said or typed, and never loses it: every dictation is written to the local outbox first
/// and only removed once the server has confirmed it. Resending is safe because the server recognises the
/// client id and never saves the same dictation twice.
/// </summary>
public sealed class DictationService(EuleApiClient api, EuleDatabase db, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly SemaphoreSlim flushLock = new(1, 1);

    /// <summary>Fires when a queued dictation reaches the server later.</summary>
    public event Action<DictationResult>? DeliveredLater;

    public int PendingCount => db.OutboxPending().Count;

    /// <summary>The user's time zone as an IANA id ("Europe/Istanbul"), which the server understands on Linux.</summary>
    public static string LocalTimeZoneId()
    {
        string id = TimeZoneInfo.Local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out string? iana) ? iana! : id;
    }

    public async Task<DictationOutcome> SubmitAsync(string text, string source, CancellationToken ct = default)
    {
        text = text.Trim();
        if (text.Length == 0) return new DictationOutcome(DictationStatus.Rejected, Error: "Boş not.");

        var item = new OutboxItem(Guid.NewGuid(), text, source, clock.GetUtcNow().UtcDateTime);
        db.OutboxAdd(item);
        return await TrySend(item, ct);
    }

    async Task<DictationOutcome> TrySend(OutboxItem item, CancellationToken ct)
    {
        if (!api.IsSignedIn) return new DictationOutcome(DictationStatus.NeedsSignIn);
        try
        {
            var result = await api.DictateAsync(item.ClientId, item.Text, item.Source, LocalTimeZoneId(), ct);
            db.OutboxRemove(item.ClientId);
            return new DictationOutcome(DictationStatus.Saved, result);
        }
        catch (ApiException ex) when (ex.Kind == ApiError.SignedOut)
        {
            return new DictationOutcome(DictationStatus.NeedsSignIn, Error: ex.Message);
        }
        catch (ApiException ex) when (ex.IsTransient)
        {
            db.OutboxFailed(item.ClientId, ex.Message);
            return new DictationOutcome(DictationStatus.Queued, Error: ex.Message);
        }
        catch (ApiException ex)
        {
            db.OutboxRemove(item.ClientId);
            return new DictationOutcome(DictationStatus.Rejected, Error: ex.Message);
        }
    }

    /// <summary>Tries every waiting dictation once (oldest first). Stops at the first connection problem.</summary>
    public async Task<int> FlushAsync(CancellationToken ct = default)
    {
        if (!api.IsSignedIn || !await flushLock.WaitAsync(0, ct)) return 0;
        int sent = 0;
        try
        {
            foreach (var item in db.OutboxPending())
            {
                var outcome = await TrySend(item, ct);
                if (outcome.Status == DictationStatus.Saved)
                {
                    sent++;
                    DeliveredLater?.Invoke(outcome.Result!);
                }
                else if (outcome.Status is DictationStatus.Queued or DictationStatus.NeedsSignIn) break;
            }
        }
        finally
        {
            flushLock.Release();
        }
        return sent;
    }
}

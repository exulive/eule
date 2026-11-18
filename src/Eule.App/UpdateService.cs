using Serilog;
using Velopack;
using Velopack.Sources;

namespace Eule.App;

/// <summary>
/// Self-update through Velopack. Works only in an installed copy (Setup.exe) and only once a release
/// feed is configured in <see cref="RepoUrl"/>; a copy run from a build folder never updates itself.
/// </summary>
public sealed class UpdateService
{
    /// <summary>
    /// The public GitHub repository whose Releases carry Eule packages (made with <c>vpk upload github</c>),
    /// e.g. "https://github.com/owner/eule". Empty = updates switched off.
    /// </summary>
    public const string RepoUrl = "";

    readonly UpdateManager? manager;

    public UpdateService()
    {
        if (string.IsNullOrWhiteSpace(RepoUrl)) return;
        try
        {
            manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Güncelleme yöneticisi kurulamadı");
        }
    }

    public static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public bool HasFeed => manager != null;
    public bool CanUpdate => manager?.IsInstalled == true;

    /// <summary>Checks the feed and downloads a newer version if there is one. Never throws.</summary>
    public async Task<(UpdateInfo? Info, bool Error)> CheckAndDownloadAsync()
    {
        if (!CanUpdate) return (null, false);
        try
        {
            var info = await manager!.CheckForUpdatesAsync();
            if (info == null) return (null, false);
            await manager.DownloadUpdatesAsync(info);
            Log.Information("Güncelleme indirildi: {Version}", info.TargetFullRelease.Version);
            return (info, false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Güncelleme denetlenemedi");
            return (null, true);
        }
    }

    public void ApplyAndRestart(UpdateInfo info) => manager?.ApplyUpdatesAndRestart(info.TargetFullRelease);

    /// <summary>Installs the downloaded update quietly once Eule exits.</summary>
    public void ApplyOnExit(UpdateInfo info) =>
        manager?.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false);
}

using System.Runtime.InteropServices;
using Eule.Core.Engine;
using Microsoft.Win32;
using Serilog;

namespace Eule.App.Interop;

/// <summary>
/// Tells whether interrupting now would be rude: presentation mode, a full-screen app or game,
/// or a call (microphone or camera in use). Results are cached for a few seconds because the
/// microphone/camera check walks the registry.
/// </summary>
public sealed class BusyDetector
{
    const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);

    DateTime checkedAt = DateTime.MinValue;
    BusyReason cached;

    public BusyReason Current
    {
        get
        {
            if (DateTime.UtcNow - checkedAt < CacheFor) return cached;
            checkedAt = DateTime.UtcNow;
            var next = Detect();
            if (next != cached) Log.Information("Meşguliyet: {Busy}", next);
            cached = next;
            return cached;
        }
    }

    static BusyReason Detect()
    {
        var r = BusyReason.None;
        try
        {
            if (SHQueryUserNotificationState(out int state) == 0)
            {
                r |= state switch
                {
                    QUNS_PRESENTATION_MODE => BusyReason.Presentation,
                    QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_APP => BusyReason.FullScreen,
                    _ => BusyReason.None,
                };
            }
            if (InUse("microphone") || InUse("webcam")) r |= BusyReason.Call;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Meşguliyet kontrolü başarısız");
        }
        return r;
    }

    /// <summary>
    /// Windows records, per app, when it last started and stopped using a device.
    /// A start time with no stop time means the app is using it right now.
    /// </summary>
    static bool InUse(string capability)
    {
        using var root = Registry.CurrentUser.OpenSubKey($@"{ConsentStore}\{capability}");
        return root != null && AnyActive(root, depth: 0);
    }

    static bool AnyActive(RegistryKey key, int depth)
    {
        foreach (string name in key.GetSubKeyNames())
        {
            // Eule's own dictation is not a call. Desktop apps are listed by path with '#' for '\'.
            if (name.EndsWith("#Eule.exe", StringComparison.OrdinalIgnoreCase)) continue;
            using var sub = key.OpenSubKey(name);
            if (sub == null) continue;
            if (sub.GetValue("LastUsedTimeStart") is long start && start > 0 &&
                sub.GetValue("LastUsedTimeStop") is long stop && stop == 0)
                return true;
            // Desktop apps live one level deeper, under "NonPackaged".
            if (depth == 0 && name == "NonPackaged" && AnyActive(sub, depth + 1)) return true;
        }
        return false;
    }

    const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4, QUNS_APP = 7;

    [DllImport("shell32.dll")]
    static extern int SHQueryUserNotificationState(out int state);
}

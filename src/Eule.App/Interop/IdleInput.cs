using System.Runtime.InteropServices;

namespace Eule.App.Interop;

/// <summary>Seconds since the last keyboard or mouse input, system-wide.</summary>
static partial class IdleInput
{
    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static int Seconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        // GetTickCount wraps every ~49 days; unsigned subtraction handles the wrap.
        uint idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return (int)(idleMs / 1000);
    }
}

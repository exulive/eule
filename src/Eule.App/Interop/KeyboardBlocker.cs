using System.Runtime.InteropServices;
using Serilog;

namespace Eule.App.Interop;

/// <summary>
/// During a strict break, swallows the shortcuts that would leave the break screen:
/// Windows key, Alt+Tab, Alt+Esc, Alt+F4, Alt+Space and Ctrl+Esc.
/// Ctrl+Alt+Del (and so Task Manager) cannot be blocked by any app, by Windows' design.
/// Shift still passes, so the emergency exit keeps working.
/// </summary>
sealed class KeyboardBlocker : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int VK_TAB = 0x09, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_F4 = 0x73, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_CONTROL = 0x11;
    const int LLKHF_ALTDOWN = 0x20;

    delegate nint HookProc(int code, nint wParam, nint lParam);

    readonly HookProc proc; // kept alive for as long as the hook is installed
    nint hook;

    public KeyboardBlocker()
    {
        proc = Callback;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        if (hook == 0) Log.Warning("Klavye kancası kurulamadı: {Error}", Marshal.GetLastWin32Error());
        else Log.Information("Zorunlu mola: kısayollar engellendi");
    }

    nint Callback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool alt = (k.flags & LLKHF_ALTDOWN) != 0;
            bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            int vk = (int)k.vkCode;
            bool block = vk is VK_LWIN or VK_RWIN
                         || (alt && vk is VK_TAB or VK_ESCAPE or VK_F4 or VK_SPACE)
                         || (ctrl && !alt && vk == VK_ESCAPE);
            if (block) return 1;
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (hook == 0) return;
        UnhookWindowsHookEx(hook);
        hook = 0;
        Log.Information("Zorunlu mola: kısayollar serbest");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookEx(int id, HookProc fn, nint mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
}

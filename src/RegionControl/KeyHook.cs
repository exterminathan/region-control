using System.Runtime.InteropServices;

namespace RegionControl;

/// <summary>
/// Low-level keyboard hook that swallows Esc. Installed only while the picker is open during a drag,
/// so Esc reaches no other app then, and is never touched at any other time. Runs on the UI thread.
/// </summary>
sealed class EscHook : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    const int WH_KEYBOARD_LL = 13, VK_ESCAPE = 0x1B, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;

    readonly HookProc _proc;   // kept alive so the GC does not collect the callback
    IntPtr _hook;

    /// <summary>Set when Esc went down while the hook was installed. The owner reads and clears it.</summary>
    public bool Pressed;

    public EscHook() { _proc = OnKey; }

    public bool Installed => _hook != IntPtr.Zero;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        Pressed = false;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) DragWatcher.Log("Esc hook failed; Esc cancel falls back to polling");
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Marshal.ReadInt32(lParam) == VK_ESCAPE)
        {
            int msg = (int)wParam;
            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) Pressed = true;
            return (IntPtr)1; // swallow key down and key up
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}

using System.Runtime.InteropServices;

namespace RegionControl;

/// <summary>
/// Remembers each snapped window's size from before the snap, keyed by window handle.
/// Dragging the window out of its zone by the title bar restores that size under the cursor.
/// </summary>
public partial class DragWatcher
{
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);

    sealed record SavedSize(RECT Original, RECT ZoneFrame);
    readonly Dictionary<IntPtr, SavedSize> _saved = new();

    static bool Near(RECT a, RECT b, int tol = 2) =>
        Math.Abs(a.Left - b.Left) <= tol && Math.Abs(a.Top - b.Top) <= tol &&
        Math.Abs(a.Right - b.Right) <= tol && Math.Abs(a.Bottom - b.Bottom) <= tol;

    void PruneSaved()
    {
        foreach (var h in _saved.Keys.Where(h => !IsWindow(h)).ToList()) _saved.Remove(h);
    }

    /// <summary>Call before a snap, after any un-maximize. Saves the size only if no entry exists, so zone-to-zone moves keep the first size.</summary>
    void RememberBeforeSnap(IntPtr hwnd)
    {
        PruneSaved();
        if (!_saved.ContainsKey(hwnd)) { var f = Native.FrameBounds(hwnd); _saved[hwnd] = new SavedSize(f, f); }
    }

    /// <summary>Call after a snap with the frame the window reached.</summary>
    void RememberZoneFrame(IntPtr hwnd, RECT zoneFrame)
    {
        if (_saved.TryGetValue(hwnd, out var s)) _saved[hwnd] = s with { ZoneFrame = zoneFrame };
    }

    /// <summary>Call at the start of a move (not a resize). Restores the saved size if the window is still at its zone; otherwise drops the entry.</summary>
    void RestoreOnDragOut(IntPtr hwnd)
    {
        if (!_saved.Remove(hwnd, out var s)) return;
        if (!IsWindow(hwnd)) return;
        var now = Native.FrameBounds(hwnd);
        if (Native.IsZoomed(hwnd) || !Near(now, s.ZoneFrame))
        {
            Log("restore skipped: window was moved or resized since the snap");
            return;
        }
        Native.GetCursorPos(out var p);
        int ow = s.Original.Right - s.Original.Left, oh = s.Original.Bottom - s.Original.Top;
        int zw = Math.Max(1, now.Right - now.Left);
        // Keep the cursor at the same relative point along the title bar; keep its distance from the top.
        double fx = Math.Clamp((p.X - now.Left) / (double)zw, 0, 1);
        int left = p.X - (int)Math.Round(fx * ow);
        int top = p.Y - Math.Min(p.Y - now.Top, oh - 1);
        Native.GetWindowRect(hwnd, out var w);
        int l = now.Left - w.Left, t = now.Top - w.Top, r = w.Right - now.Right, b = w.Bottom - now.Bottom;
        Native.SetWindowPos(hwnd, IntPtr.Zero, left - l, top - t, ow + l + r, oh + t + b, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Log($"restored size {ow}x{oh} on drag-out");
    }
}

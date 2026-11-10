using System.Runtime.InteropServices;

namespace RegionControl;

/// <summary>One connected monitor. All rectangles are physical pixels.</summary>
public class MonitorInfo
{
    public IntPtr Handle;
    public string Device = "";      // GDI name, for example \\.\DISPLAY1 (can change after docking)
    public string Id = "";          // stable hardware id from the monitor device path
    public string Friendly = "";    // EDID name, for example "DELL U2720Q"
    public bool Internal;           // built-in laptop panel
    public RECT Bounds;
    public RECT Work;
    public double Scale = 1;
    public bool Primary;
    public int Number;              // 1-based, left to right

    /// <summary>Monitor key: hardware id plus resolution. One monitor at another resolution is another key.</summary>
    public string Key => $"{Id}@{Bounds.Width}x{Bounds.Height}";

    public bool Ultrawide => Bounds.Height > 0 && (double)Bounds.Width / Bounds.Height >= 2.2;

    public string Label
    {
        get
        {
            string name = Friendly.Length > 0 ? Friendly : Internal ? "Built-in display" : Device.TrimStart('\\', '.');
            return $"{name} {Bounds.Width}x{Bounds.Height}{(Ultrawide ? " ultrawide" : "")} {Math.Round(Scale * 100)}%";
        }
    }

    public override string ToString() => $"monitor {Number}: {Label}";
}

public static class Monitors
{
    /// <summary>Setup key: sorted monitor keys. Scale is not part of it; pixels are physical.</summary>
    public static string SetupKey(IEnumerable<MonitorInfo> mons) =>
        string.Join("|", mons.Select(m => m.Key).OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>Short id from a device path such as \\?\DISPLAY#DELA0C3#5&amp;1a2b&amp;0&amp;UID4352#{e6f07b5f-...}.</summary>
    public static string ShortId(string devicePath)
    {
        var parts = devicePath.Split('#');
        if (parts.Length >= 3) return parts[1] + "#" + parts[2];
        return devicePath;
    }

    public static List<MonitorInfo> Enumerate()
    {
        var names = TargetNames();
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (!Native.GetMonitorInfoEx(h, ref mi)) return true;
            var m = new MonitorInfo
            {
                Handle = h, Device = mi.szDevice ?? "", Bounds = mi.rcMonitor, Work = mi.rcWork,
                Primary = (mi.dwFlags & 1) != 0,
            };
            if (Native.GetDpiForMonitor(h, Native.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0) m.Scale = dx / 96.0;
            if (names.TryGetValue(m.Device, out var t))
            {
                m.Friendly = t.friendly; m.Internal = t.isInternal;
                m.Id = t.path.Length > 0 ? ShortId(t.path) : "";
            }
            if (m.Id.Length == 0) m.Id = m.Device.TrimStart('\\', '.');
            list.Add(m);
            return true;
        }, IntPtr.Zero);
        Number(list);
        return list;
    }

    public static void Number(List<MonitorInfo> list)
    {
        list.Sort((a, b) => a.Bounds.Left != b.Bounds.Left ? a.Bounds.Left.CompareTo(b.Bounds.Left) : a.Bounds.Top.CompareTo(b.Bounds.Top));
        for (int i = 0; i < list.Count; i++) list[i].Number = i + 1;
    }

    /// <summary>GDI device name to monitor name and device path, through the DisplayConfig API.</summary>
    static Dictionary<string, (string friendly, string path, bool isInternal)> TargetNames()
    {
        var map = new Dictionary<string, (string, string, bool)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return map;
                var paths = new DISPLAYCONFIG_PATH_INFO[np];
                IntPtr modes = Marshal.AllocHGlobal((int)Math.Max(1, nm) * 64);
                try
                {
                    int rc = Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
                    if (rc == 122) continue; // ERROR_INSUFFICIENT_BUFFER: the setup changed, try again
                    if (rc != 0) return map;
                }
                finally { Marshal.FreeHGlobal(modes); }

                for (int i = 0; i < np; i++)
                {
                    var src = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    src.header.type = 1; // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                    src.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                    src.header.adapterId = paths[i].sourceInfo.adapterId;
                    src.header.id = paths[i].sourceInfo.id;
                    if (Native.DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                    var tgt = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    tgt.header.type = 2; // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
                    tgt.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                    tgt.header.adapterId = paths[i].targetInfo.adapterId;
                    tgt.header.id = paths[i].targetInfo.id;
                    if (Native.DisplayConfigGetDeviceInfo(ref tgt) != 0) continue;

                    // Output technology: 0x80000000 internal, 11 embedded DisplayPort, 13 UDI embedded.
                    uint tech = tgt.outputTechnology;
                    bool isInternal = tech == 0x80000000 || tech == 11 || tech == 13;
                    map.TryAdd(src.viewGdiDeviceName ?? "", (tgt.monitorFriendlyDeviceName ?? "", tgt.monitorDevicePath ?? "", isInternal));
                }
                break;
            }
        }
        catch (Exception) { /* fall back to GDI names */ }
        return map;
    }
}

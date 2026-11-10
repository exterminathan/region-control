using System.Text.Json;
using System.Text.Json.Serialization;

namespace RegionControl;

/// <summary>How a zone stores its position. Each zone chooses its own mode.</summary>
public enum ZoneMode
{
    Percent,   // X, Y, W, H are 0..100 percent of the work area
    Pixels,    // X, Y, W, H are physical pixels from the work-area top-left; clamped to fit
    Grid,      // X, Y, W, H are cells of the layout grid
}

public class GridSize
{
    public int Cols { get; set; } = 12;
    public int Rows { get; set; } = 8;
}

/// <summary>One zone of a layout. Zones may overlap.</summary>
public class Zone
{
    public string Name { get; set; } = "";
    public ZoneMode Mode { get; set; } = ZoneMode.Percent;
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    public Zone Clone() => (Zone)MemberwiseClone();

    /// <summary>Zone as fractions (0..1) of a work area of the given size, clamped inside it.</summary>
    public (double l, double t, double r, double b) ToFraction(GridSize grid, double ww, double wh)
    {
        double l, t, r, b;
        switch (Mode)
        {
            case ZoneMode.Pixels:
                {
                    double w = Math.Min(Math.Max(W, 1), ww), h = Math.Min(Math.Max(H, 1), wh);
                    double x = Math.Clamp(X, 0, ww - w), y = Math.Clamp(Y, 0, wh - h);
                    l = x / ww; r = (x + w) / ww; t = y / wh; b = (y + h) / wh;
                    break;
                }
            case ZoneMode.Grid:
                {
                    double c = Math.Max(1, grid.Cols), rw = Math.Max(1, grid.Rows);
                    l = X / c; r = (X + W) / c; t = Y / rw; b = (Y + H) / rw;
                    break;
                }
            default:
                l = X / 100; r = (X + W) / 100; t = Y / 100; b = (Y + H) / 100;
                break;
        }
        l = Math.Clamp(l, 0, 1); r = Math.Clamp(r, 0, 1); t = Math.Clamp(t, 0, 1); b = Math.Clamp(b, 0, 1);
        return (l, t, r, b);
    }

    /// <summary>Zone on screen, inside the given work area.</summary>
    public RECT ToScreen(RECT work, GridSize grid)
    {
        var (l, t, r, b) = ToFraction(grid, work.Width, work.Height);
        int L = (int)Math.Round(l * work.Width), T = (int)Math.Round(t * work.Height);
        int R = (int)Math.Round(r * work.Width), B = (int)Math.Round(b * work.Height);
        if (R <= L) R = Math.Min(work.Width, L + 1);
        if (B <= T) B = Math.Min(work.Height, T + 1);
        return new RECT(work.Left + L, work.Top + T, work.Left + R, work.Top + B);
    }
}

/// <summary>A layout covers one monitor.</summary>
public class ZoneLayout
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public GridSize Grid { get; set; } = new();
    public List<Zone> Zones { get; set; } = new();

    public ZoneLayout Clone() => new()
    {
        Id = Id, Name = Name, Grid = new GridSize { Cols = Grid.Cols, Rows = Grid.Rows },
        Zones = Zones.Select(z => z.Clone()).ToList(),
    };

    public RECT ZoneRect(int i, RECT work) => Zones[i].ToScreen(work, Grid);

    /// <summary>Index of the zone under a screen point, or -1. With overlap, the smallest zone wins.</summary>
    public int HitTest(RECT work, POINT p)
    {
        int best = -1; long bestArea = long.MaxValue;
        for (int i = 0; i < Zones.Count; i++)
        {
            var r = ZoneRect(i, work);
            long area = (long)r.Width * r.Height;
            if (r.Contains(p) && area < bestArea) { best = i; bestArea = area; }
        }
        return best;
    }

    /// <summary>Zone indexes from largest to smallest: the paint order, so small zones sit on top.</summary>
    public int[] PaintOrder(RECT work) => Enumerable.Range(0, Zones.Count)
        .OrderByDescending(i => { var r = ZoneRect(i, work); return (long)r.Width * r.Height; }).ToArray();
}

/// <summary>The picker strip of one monitor (one monitor key = hardware id plus resolution).</summary>
public class MonitorEntry
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string> Layouts { get; set; } = new();
}

/// <summary>A set: one main layout per monitor, for one monitor setup.</summary>
public class LayoutSet
{
    public string Key { get; set; } = "";     // setup key: sorted monitor keys joined by "|"
    public string Name { get; set; } = "";
    public Dictionary<string, string> Main { get; set; } = new(); // monitor key -> layout id
}

public class LibraryData
{
    public int Version { get; set; } = 1;
    public List<ZoneLayout> Layouts { get; set; } = new();
    public List<MonitorEntry> Monitors { get; set; } = new();
    public List<LayoutSet> Sets { get; set; } = new();
}

/// <summary>The saved layout library: %APPDATA%\RegionControl\layouts.json.</summary>
public class Library
{
    public string Path { get; }
    public LibraryData Data { get; private set; } = new();
    public string? LoadNote { get; private set; }
    public DateTime LastWriteUtc { get; private set; }

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RegionControl", "layouts.json");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    Library(string path) { Path = path; }

    public static Library Load(string path)
    {
        var lib = new Library(path);
        lib.Reload();
        return lib;
    }

    /// <summary>Read the file again. A missing file gets the seed layouts. A broken file is moved aside.</summary>
    public void Reload()
    {
        LoadNote = null;
        if (!File.Exists(Path))
        {
            Data = new LibraryData { Layouts = Seeds() };
            Save();
            LoadNote = $"created {Path} with {Data.Layouts.Count} seed layouts";
            return;
        }
        try
        {
            Data = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(Path), Json) ?? throw new JsonException("empty file");
            LastWriteUtc = File.GetLastWriteTimeUtc(Path);
            int fixes = Normalize();
            LoadNote = $"loaded {Data.Layouts.Count} layouts, {Data.Monitors.Count} monitors, {Data.Sets.Count} sets from {Path}"
                + (fixes > 0 ? $" ({fixes} invalid entries fixed or dropped)" : "");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            string bad = System.IO.Path.ChangeExtension(Path, $".bad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(Path, bad);
            Data = new LibraryData { Layouts = Seeds() };
            Save();
            LoadNote = $"could not read the layouts file ({ex.Message}). Moved it to {bad} and started from the seed layouts";
        }
    }

    public bool ChangedOnDisk => File.Exists(Path) && File.GetLastWriteTimeUtc(Path) != LastWriteUtc;

    /// <summary>Write the file atomically. The previous version stays as layouts.json.bak.</summary>
    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Data, Json));
        if (File.Exists(Path)) File.Copy(Path, Path + ".bak", true);
        File.Move(tmp, Path, true);
        LastWriteUtc = File.GetLastWriteTimeUtc(Path);
    }

    /// <summary>The three built-in starter layouts.</summary>
    public static List<ZoneLayout> Seeds() => new()
    {
        new() { Id = "seed-70-30", Name = "70 | 30", Zones = {
            new() { Name = "Left 70%", X = 0, Y = 0, W = 70, H = 100 },
            new() { Name = "Right 30%", X = 70, Y = 0, W = 30, H = 100 } } },
        new() { Id = "seed-25-50-25", Name = "25 | 50 | 25", Zones = {
            new() { Name = "Left 25%", X = 0, Y = 0, W = 25, H = 100 },
            new() { Name = "Centre 50%", X = 25, Y = 0, W = 50, H = 100 },
            new() { Name = "Right 25%", X = 75, Y = 0, W = 25, H = 100 } } },
        new() { Id = "seed-floating-30", Name = "Floating 30%", Zones = {
            new() { Name = "Floating 30%", X = 35, Y = 20, W = 30, H = 60 } } },
    };

    /// <summary>Fix what a hand edit can break. Returns the number of fixes.</summary>
    int Normalize()
    {
        int fixes = 0;
        Data.Layouts ??= new(); Data.Monitors ??= new(); Data.Sets ??= new();
        var ids = new HashSet<string>();
        foreach (var l in Data.Layouts.ToList())
        {
            if (l == null) { Data.Layouts.Remove(l!); fixes++; continue; }
            if (string.IsNullOrWhiteSpace(l.Id) || !ids.Add(l.Id)) { l.Id = NewId(); ids.Add(l.Id); fixes++; }
            l.Name ??= l.Id;
            l.Grid ??= new();
            if (l.Grid.Cols < 1 || l.Grid.Cols > 96) { l.Grid.Cols = 12; fixes++; }
            if (l.Grid.Rows < 1 || l.Grid.Rows > 96) { l.Grid.Rows = 8; fixes++; }
            l.Zones ??= new();
            fixes += l.Zones.RemoveAll(z => z == null || !(z.W > 0) || !(z.H > 0) || double.IsNaN(z.X) || double.IsNaN(z.Y));
            foreach (var z in l.Zones) z.Name ??= "";
        }
        fixes += Data.Layouts.RemoveAll(l => l.Zones.Count == 0);
        var have = Data.Layouts.Select(l => l.Id).ToHashSet();
        foreach (var m in Data.Monitors)
        {
            m.Layouts ??= new();
            fixes += m.Layouts.RemoveAll(id => !have.Contains(id));
            m.Layouts = m.Layouts.Distinct().ToList();
        }
        foreach (var s in Data.Sets)
        {
            s.Main ??= new();
            foreach (var k in s.Main.Where(kv => !have.Contains(kv.Value)).Select(kv => kv.Key).ToList()) { s.Main.Remove(k); fixes++; }
        }
        return fixes;
    }

    public static string NewId() => "L" + Guid.NewGuid().ToString("N")[..8];

    public ZoneLayout? Find(string? id) => id == null ? null : Data.Layouts.FirstOrDefault(l => l.Id == id);
    public MonitorEntry? Monitor(string key) => Data.Monitors.FirstOrDefault(m => m.Key == key);
    public LayoutSet? Set(string setupKey) => Data.Sets.FirstOrDefault(s => s.Key == setupKey);

    /// <summary>Make sure every monitor and the setup have entries. Returns true when something was added.</summary>
    public bool EnsureSetup(IReadOnlyList<MonitorInfo> mons)
    {
        bool changed = false;
        foreach (var m in mons)
        {
            var e = Monitor(m.Key);
            if (e == null)
            {
                Data.Monitors.Add(new MonitorEntry { Key = m.Key, Label = m.Label, Layouts = DefaultStrip(m) });
                changed = true;
            }
            else if (e.Label != m.Label) { e.Label = m.Label; changed = true; }
        }
        string key = Monitors.SetupKey(mons);
        var set = Set(key);
        if (set == null)
        {
            set = new LayoutSet { Key = key, Name = AutoName(mons) };
            Data.Sets.Add(set);
            changed = true;
        }
        foreach (var m in mons)
        {
            if (set.Main.ContainsKey(m.Key)) continue;
            // The main layout this monitor has in another set, else the first of its strip.
            string? main = Data.Sets.Where(s => s != set).Select(s => s.Main.GetValueOrDefault(m.Key)).FirstOrDefault(id => Find(id) != null)
                ?? Monitor(m.Key)!.Layouts.FirstOrDefault();
            if (main != null) { set.Main[m.Key] = main; changed = true; }
        }
        return changed;
    }

    /// <summary>Strip for a monitor seen for the first time.</summary>
    List<string> DefaultStrip(MonitorInfo m)
    {
        // The same monitor at another resolution: copy its strip.
        string prefix = m.Id + "@";
        var same = Data.Monitors.FirstOrDefault(e => e.Key.StartsWith(prefix, StringComparison.Ordinal) && e.Layouts.Count > 0);
        if (same != null) return same.Layouts.ToList();
        var seeds = Seeds().Select(s => s.Id).Where(id => Find(id) != null).ToList();
        if (seeds.Count > 0) return seeds;
        return Data.Layouts.Select(l => l.Id).ToList();
    }

    public static string AutoName(IReadOnlyList<MonitorInfo> mons)
    {
        if (mons.Count == 1)
            return mons[0].Internal ? "Laptop alone" : $"{mons[0].Label} alone";
        return $"{mons.Count} monitors: " + string.Join(" + ", mons.Select(m => m.Label));
    }

    public string? MainFor(string setupKey, string monitorKey)
    {
        var id = Set(setupKey)?.Main.GetValueOrDefault(monitorKey);
        return Find(id) != null ? id : null;
    }

    /// <summary>Layouts for a monitor's picker strip: the set's main layout first, then the strip order.</summary>
    public List<ZoneLayout> StripFor(string monitorKey, string setupKey)
    {
        var ids = new List<string>();
        var main = MainFor(setupKey, monitorKey);
        if (main != null) ids.Add(main);
        foreach (var id in Monitor(monitorKey)?.Layouts ?? new()) if (!ids.Contains(id)) ids.Add(id);
        var list = ids.Select(Find).Where(l => l != null).Select(l => l!).ToList();
        return list.Count > 0 ? list : Data.Layouts.ToList(); // never an empty picker
    }

    public bool IsShown(string layoutId, string monitorKey) => Monitor(monitorKey)?.Layouts.Contains(layoutId) == true;

    /// <summary>Show or hide a layout in a monitor's strip (sharing between monitors).</summary>
    public void SetShown(string layoutId, string monitorKey, bool on, string label = "")
    {
        var e = Monitor(monitorKey);
        if (e == null)
        {
            if (!on) return;
            e = new MonitorEntry { Key = monitorKey, Label = label };
            Data.Monitors.Add(e);
        }
        if (on && !e.Layouts.Contains(layoutId)) e.Layouts.Add(layoutId);
        if (!on) e.Layouts.Remove(layoutId);
    }

    /// <summary>Set or clear the main layout of a monitor in a set. Setting it also shows it in the strip.</summary>
    public void SetMain(string setupKey, string monitorKey, string? layoutId, string setName = "")
    {
        var set = Set(setupKey);
        if (set == null)
        {
            if (layoutId == null) return;
            set = new LayoutSet { Key = setupKey, Name = setName };
            Data.Sets.Add(set);
        }
        if (layoutId == null) set.Main.Remove(monitorKey);
        else { set.Main[monitorKey] = layoutId; SetShown(layoutId, monitorKey, true); }
    }

    public void Upsert(ZoneLayout layout)
    {
        if (string.IsNullOrEmpty(layout.Id)) layout.Id = NewId();
        int i = Data.Layouts.FindIndex(l => l.Id == layout.Id);
        if (i >= 0) Data.Layouts[i] = layout; else Data.Layouts.Add(layout);
    }

    public void Delete(string id)
    {
        Data.Layouts.RemoveAll(l => l.Id == id);
        foreach (var m in Data.Monitors) m.Layouts.Remove(id);
        foreach (var s in Data.Sets)
            foreach (var k in s.Main.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) s.Main.Remove(k);
    }
}

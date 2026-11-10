using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace RegionControl;

/// <summary>
/// Every colour, radius, blur, shadow, font and motion value in one place.
/// Follows the Windows light or dark mode, accent colour, "Transparency effects" and "Animation effects".
/// Optional overrides, read only: %APPDATA%\RegionControl\theme.json, for example
/// { "mode": "dark", "glass": false, "animations": true, "accent": "#4CC2FF" }.
/// Sizes are logical pixels at 100 % scale: multiply by the monitor scale.
/// </summary>
public static class Theme
{
    // ---------- what Windows says ----------
    public static bool Dark { get; private set; } = true;
    public static bool Glass { get; private set; } = true;      // blur behind panels
    public static bool Animate { get; private set; } = true;
    public static bool TaskbarDark { get; private set; } = true;
    public static int Version { get; private set; }             // changes on every reload, for caches

    /// <summary>Raised on the UI thread after Windows changes mode, accent or effects.</summary>
    public static event Action? Changed;

    // ---------- accent ----------
    public static Color Accent { get; private set; }            // fills and rings in the current mode
    public static Color AccentBase { get; private set; }        // the user's accent itself
    public static Color AccentSoft { get; private set; }        // lighter accent for glows and text on dark
    public static Color OnAccent { get; private set; }          // text on an accent fill

    // ---------- surfaces ----------
    public static Color Panel { get; private set; }             // solid panel (no glass)
    public static Color GlassTint { get; private set; }         // tint laid over the blur
    public static Color PanelBorder { get; private set; }
    public static Color PanelHighlight { get; private set; }    // top inner light line
    public static Color Card { get; private set; }
    public static Color CardHot { get; private set; }
    public static Color Control { get; private set; }           // toolbar button
    public static Color ControlHot { get; private set; }
    public static Color ControlPressed { get; private set; }
    public static Color Field { get; private set; }             // text box
    public static Color Divider { get; private set; }
    public static Color Scrim { get; private set; }             // dims the screen behind the zones overlay
    public static Color Shadow { get; private set; }
    public static Color Danger { get; private set; }
    public static Color Success { get; private set; }

    // ---------- text ----------
    public static Color Text { get; private set; }
    public static Color Dim { get; private set; }
    public static Color Faint { get; private set; }

    // ---------- zones ----------
    public static Color MiniZone { get; private set; }          // zone in a thumbnail
    public static Color MiniZoneEdge { get; private set; }
    public static Color ZoneFill { get; private set; }          // zones overlay
    public static Color ZoneEdge { get; private set; }
    public static Color ZoneHotFill { get; private set; }
    public static Color ZoneHotEdge { get; private set; }
    public static Color EditorTint { get; private set; }        // over the blurred desktop in the editor
    public static Color EditorSolid { get; private set; }
    public static Color GridLine { get; private set; }

    // ---------- geometry (logical px) ----------
    public const float RadiusPanel = 12;
    public const float RadiusCard = 7;
    public const float RadiusMini = 3;
    public const float RadiusZone = 10;
    public const float RadiusControl = 6;
    public const float ZoneGap = 8;           // between zones in the zones overlay
    public const float EditorZoneInset = 3;   // half the gap between zones in the editor
    public const float Border = 1;
    public const float ShadowSize = 22;       // soft shadow around panels
    public const float ShadowOffsetY = 6;

    // ---------- blur ----------
    public const int BlurDownscale = 8;       // blur runs on a copy this many times smaller
    public const int BlurPasses = 3;          // box blur passes: 3 is close to a Gaussian
    public const int BlurRadiusSmall = 3;     // box radius on the small copy, for panels
    public const int BlurRadiusEditor = 4;    // box radius on the small copy, for the editor backdrop
    /// <summary>Strength of the acrylic grain: 0 is off, 255 is the most. Kept very low so panels read as clean glass.</summary>
    public const int Grain = 2;

    // ---------- motion (ms), all under 200 ----------
    public const int FadeInMs = 120;
    public const int SlideInMs = 160;
    public const float SlidePx = 10;
    public const int HighlightMs = 90;
    public const int LayoutSwitchMs = 90;
    public const int SnapFlashMs = 190;
    public const int ToastMs = 140;

    // ---------- mode colours (editor) ----------
    public static Color ModeColor(ZoneMode m) => m switch
    {
        ZoneMode.Pixels => Dark ? Color.FromArgb(255, 176, 82) : Color.FromArgb(214, 120, 18),
        ZoneMode.Grid => Dark ? Color.FromArgb(72, 214, 150) : Color.FromArgb(18, 150, 96),
        _ => Dark ? Color.FromArgb(98, 170, 255) : Color.FromArgb(28, 110, 220),
    };

    public static string ModeText(ZoneMode m) => m switch { ZoneMode.Pixels => "px", ZoneMode.Grid => "grid", _ => "%" };

    public static string ModeName(ZoneMode m) => m switch { ZoneMode.Pixels => "Pixels", ZoneMode.Grid => "Grid", _ => "Percent" };

    // ---------- fonts ----------
    static string? _family, _display;

    /// <summary>Segoe UI Variable on Windows 11, Segoe UI elsewhere. Size in pixels.</summary>
    public static Font UiFont(double px, bool bold = false) => MakeFont(TextFamily, px, bold ? FontStyle.Bold : FontStyle.Regular);

    /// <summary>Font for big numbers and titles.</summary>
    public static Font DisplayFont(double px, bool bold = true) => MakeFont(DisplayFamily, px, bold ? FontStyle.Bold : FontStyle.Regular);

    static Font MakeFont(string family, double px, FontStyle style) =>
        new(family, (float)Math.Max(6, px), style, GraphicsUnit.Pixel);

    static string TextFamily => _family ??= Installed("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    static string DisplayFamily => _display ??= Installed("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI Semibold";

    static bool Installed(string name)
    {
        try
        {
            using var f = new Font(name, 10, GraphicsUnit.Pixel);
            return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------- helpers ----------
    public static Color A(Color c, double alpha) => Color.FromArgb(Math.Clamp((int)Math.Round(alpha * 255), 0, 255), c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.A + (b.A - a.A) * t), (int)Math.Round(a.R + (b.R - a.R) * t),
        (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>Colour <paramref name="top"/> laid over an opaque <paramref name="under"/>: an opaque result for GDI text.</summary>
    public static Color Over(Color top, Color under)
    {
        double a = top.A / 255.0;
        return Color.FromArgb(255, (int)Math.Round(top.R * a + under.R * (1 - a)),
            (int)Math.Round(top.G * a + under.G * (1 - a)), (int)Math.Round(top.B * a + under.B * (1 - a)));
    }

    /// <summary>Rounded rectangle path. Radius is clamped to half the shorter side.</summary>
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static float Ease(double t) { t = Math.Clamp(t, 0, 1); return (float)(1 - Math.Pow(1 - t, 3)); } // ease-out cubic

    // ---------- loading ----------
    static bool _watching;

    static Theme() => Load();

    /// <summary>Start following Windows setting changes. Call once from the UI thread.</summary>
    public static void Watch()
    {
        if (_watching) return;
        _watching = true;
        try
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color or UserPreferenceCategory.Desktop)
                    Reload();
            };
        }
        catch { }
    }

    public static void Reload()
    {
        Load();
        Changed?.Invoke();
    }

    /// <summary>Force a mode, for render checks. Null follows Windows again.</summary>
    public static void ForceMode(bool? dark, bool? glass = null)
    {
        _forceDark = dark; _forceGlass = glass;
        Load();
    }

    static bool? _forceDark, _forceGlass;

    static void Load()
    {
        bool dark = true, transparency = true, taskbarDark = true;
        Color? accentDark = null, accentLight = null, accentBase = null, accentSoft = null;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("AppsUseLightTheme") is int apps) dark = apps == 0;
            if (k?.GetValue("SystemUsesLightTheme") is int sys) taskbarDark = sys == 0;
            if (k?.GetValue("EnableTransparency") is int tr) transparency = tr != 0;
        }
        catch { }
        try
        {
            // AccentPalette: 8 RGBA colours, light 3, light 2, light 1, base, dark 1, dark 2, dark 3, unused.
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (k?.GetValue("AccentPalette") is byte[] p && p.Length >= 28)
            {
                Color At(int i) => Color.FromArgb(p[i * 4], p[i * 4 + 1], p[i * 4 + 2]);
                accentSoft = At(0); accentDark = At(1); accentBase = At(3); accentLight = At(4);
            }
        }
        catch { }
        bool animate = true;
        try { if (SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out int on, 0)) animate = on != 0; } catch { }

        // Optional overrides file.
        string? accentOverride = null;
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RegionControl", "theme.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var r = doc.RootElement;
                if (r.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    if (m.GetString() == "dark") dark = true;
                    else if (m.GetString() == "light") dark = false;
                }
                if (r.TryGetProperty("glass", out var g) && g.ValueKind is JsonValueKind.True or JsonValueKind.False) transparency = g.GetBoolean();
                if (r.TryGetProperty("animations", out var an) && an.ValueKind is JsonValueKind.True or JsonValueKind.False) animate = an.GetBoolean();
                if (r.TryGetProperty("accent", out var ac) && ac.ValueKind == JsonValueKind.String) accentOverride = ac.GetString();
            }
        }
        catch { }
        if (_forceDark.HasValue) dark = _forceDark.Value;
        if (_forceGlass.HasValue) transparency = _forceGlass.Value;

        Dark = dark; Glass = transparency; Animate = animate; TaskbarDark = taskbarDark;
        Version++;

        if (accentOverride != null && TryParseColor(accentOverride, out var ov))
        {
            accentBase = ov;
            accentDark = Mix(ov, Color.White, 0.35); accentSoft = Mix(ov, Color.White, 0.6); accentLight = Mix(ov, Color.Black, 0.12);
        }
        AccentBase = accentBase ?? Color.FromArgb(0, 120, 212);
        Accent = dark ? accentDark ?? Color.FromArgb(76, 194, 255) : accentLight ?? Color.FromArgb(0, 95, 184);
        AccentSoft = accentSoft ?? Mix(Accent, Color.White, 0.45);
        OnAccent = Luma(Accent) > 0.55 ? Color.FromArgb(10, 10, 12) : Color.White;

        if (dark)
        {
            Panel = Color.FromArgb(40, 40, 44);
            GlassTint = Color.FromArgb(176, 30, 31, 36);
            PanelBorder = Color.FromArgb(38, 255, 255, 255);
            PanelHighlight = Color.FromArgb(22, 255, 255, 255);
            Card = Color.FromArgb(16, 255, 255, 255);
            CardHot = Color.FromArgb(30, 255, 255, 255);
            Control = Color.FromArgb(14, 255, 255, 255);
            ControlHot = Color.FromArgb(28, 255, 255, 255);
            ControlPressed = Color.FromArgb(10, 255, 255, 255);
            Field = Color.FromArgb(70, 0, 0, 0);
            Divider = Color.FromArgb(30, 255, 255, 255);
            Scrim = Color.FromArgb(70, 0, 0, 0);
            Shadow = Color.FromArgb(120, 0, 0, 0);
            Danger = Color.FromArgb(255, 99, 99);
            Success = Color.FromArgb(108, 203, 95);
            Text = Color.FromArgb(244, 244, 246);
            Dim = Color.FromArgb(172, 174, 180);
            Faint = Color.FromArgb(120, 122, 130);
            MiniZone = Color.FromArgb(62, 255, 255, 255);
            MiniZoneEdge = Color.FromArgb(30, 255, 255, 255);
            ZoneFill = Color.FromArgb(34, 255, 255, 255);
            ZoneEdge = Color.FromArgb(120, 255, 255, 255);
            EditorTint = Color.FromArgb(170, 16, 18, 24);
            EditorSolid = Color.FromArgb(24, 25, 30);
            GridLine = Color.FromArgb(34, 255, 255, 255);
        }
        else
        {
            Panel = Color.FromArgb(243, 243, 245);
            GlassTint = Color.FromArgb(184, 246, 246, 248);
            PanelBorder = Color.FromArgb(30, 0, 0, 0);
            PanelHighlight = Color.FromArgb(150, 255, 255, 255);
            Card = Color.FromArgb(120, 255, 255, 255);
            CardHot = Color.FromArgb(210, 255, 255, 255);
            Control = Color.FromArgb(110, 255, 255, 255);
            ControlHot = Color.FromArgb(200, 255, 255, 255);
            ControlPressed = Color.FromArgb(70, 255, 255, 255);
            Field = Color.FromArgb(220, 255, 255, 255);
            Divider = Color.FromArgb(26, 0, 0, 0);
            Scrim = Color.FromArgb(40, 0, 0, 0);
            Shadow = Color.FromArgb(70, 0, 0, 0);
            Danger = Color.FromArgb(196, 43, 28);
            Success = Color.FromArgb(15, 123, 15);
            Text = Color.FromArgb(26, 26, 28);
            Dim = Color.FromArgb(92, 94, 100);
            Faint = Color.FromArgb(140, 142, 150);
            MiniZone = Color.FromArgb(46, 0, 0, 0);
            MiniZoneEdge = Color.FromArgb(24, 0, 0, 0);
            ZoneFill = Color.FromArgb(60, 255, 255, 255);
            ZoneEdge = Color.FromArgb(170, 255, 255, 255);
            EditorTint = Color.FromArgb(150, 236, 238, 243);
            EditorSolid = Color.FromArgb(234, 236, 240);
            GridLine = Color.FromArgb(34, 0, 0, 0);
        }
        ZoneHotFill = A(Accent, dark ? 0.42 : 0.38);
        ZoneHotEdge = dark ? AccentSoft : Accent;
    }

    static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    static bool TryParseColor(string s, out Color c)
    {
        c = default;
        s = s.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int v)) return false;
        c = Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
        return true;
    }

    [DllImport("user32.dll")] static extern bool SystemParametersInfo(int action, int param, out int value, int winIni);
}

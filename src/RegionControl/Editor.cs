using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace RegionControl;

/// <summary>
/// Full-screen layout editor on one monitor's work area, at real size, over a blurred copy of the desktop.
/// Split tool: click a zone to split it. Draw tool: drag to draw a zone. Move tool: drag a zone.
/// In every tool: drag an edge to resize, double-click an edge to merge, right-click a zone for more.
/// </summary>
public class EditorForm : Form
{
    enum Tool { Split, Draw, Move }
    enum DragKind { None, Divider, Draw, Move, Split, Bar }
    enum Kind { Button, Label, Sep, Field, Primary }

    class Item
    {
        public string Id = "", Text = "", Glyph = "", Key = "";
        public Kind Kind;
        public bool On, Enabled = true, Dot;
        public int MinW;
        public Rectangle R;
    }

    readonly Library _lib;
    readonly MonitorInfo _mon;
    readonly List<MonitorInfo> _monitors;
    readonly string _setKey, _setName;
    readonly EditorModel _m;
    readonly double _scale;
    readonly HashSet<string> _showOn = new();

    Tool _tool = Tool.Split;
    DragKind _drag;
    Divider? _dv, _dh;               // dividers being dragged
    Divider? _hv, _hh;               // dividers under the cursor
    int _hoverZone = -1, _hoverChip = -1;
    string? _hoverItem;
    Point _mouse, _down;
    int _dragZone = -1;
    EditZone? _dragStart;
    (double l, double t, double r, double b) _drawRect;
    bool _drawing;
    readonly List<(bool vertical, double pos)> _guides = new();   // snap guides while dragging

    List<Item> _items = new();
    List<Item> _bottomItems = new();
    Rectangle _bar, _bottom, _helpRect;
    string? _tipFor;                 // toolbar item whose hint shows
    DateTime _hoverSince;
    Point? _barPos;
    Point _barGrab;
    bool _naming;
    bool _helpOn;                    // shortcuts panel, opened with ? or H

    bool _openPanel;
    Rectangle _panel;
    readonly List<(Rectangle cell, ZoneLayout? layout)> _openCells = new();
    int _openStrip;          // number of strip cards (they follow the Blank card)
    int _cardDown = -1;      // cell index under the mouse button, -1 when none
    bool _cardDragging;      // true once the mouse moved far enough to lift the card
    Point _cardGrab;         // mouse offset inside the lifted card
    int _cardTarget = -1;    // cell index the card would drop on, -1 for no valid drop

    readonly Dictionary<int, Rectangle> _chips = new();
    string? _toast;
    bool _toastWarn;
    float _toastT = 1;
    Rectangle _toastRect;
    readonly Animator _toastAnim = new();
    DateTime _toastUntil, _escArmedUntil, _deleteArmedUntil;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    int _caretTick;
    readonly Font _fBig, _fMed, _fSmall, _fZone, _fZoneSmall;

    // Backdrop and cached scene (backdrop + grid + zones), redrawn only when they change.
    Bitmap? _blurSmall;
    Surface? _backdrop, _scene;
    TextureBrush? _backdropBrush;
    string _sceneKey = "";

    public EditorForm(Library lib, MonitorInfo mon, List<MonitorInfo> monitors, string setKey, string setName)
    {
        _lib = lib; _mon = mon; _monitors = monitors; _setKey = setKey; _setName = setName;
        _scale = mon.Scale;
        _m = new EditorModel(mon.Work.Width, mon.Work.Height) { MinPx = S(32), MagnetPx = S(10) };
        _showOn.Add(mon.Key);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = true;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Theme.EditorSolid;
        Text = "Snap Layouts editor";
        try { Icon = Icons.AppIcon(32); } catch { }
        Location = new Point(mon.Work.Left, mon.Work.Top);
        Size = new Size(mon.Work.Width, mon.Work.Height);

        _fBig = Theme.DisplayFont(17 * _scale, true);
        _fMed = Theme.UiFont(13.5 * _scale);
        _fSmall = Theme.UiFont(12 * _scale);
        _fZone = Theme.DisplayFont(19 * _scale, true);
        _fZoneSmall = Theme.UiFont(12.5 * _scale);

        // Blurred copy of the desktop behind the editor, taken before the editor covers it. Read only.
        _blurSmall = Glass.Backdrop(new Rectangle(mon.Work.Left, mon.Work.Top, mon.Work.Width, mon.Work.Height), Theme.BlurRadiusEditor);
        Theme.Changed += OnThemeChanged;

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Toast("Click a zone to split it. Drag an edge to resize. Press ? for all shortcuts.", 6);
    }

    internal EditorModel Model => _m;
    internal void DemoOpenPanel() => OpenPanel();
    internal void DemoHover(Point p) { UpdateHover(p); Invalidate(); }
    internal void DemoHelp(bool on) { _helpOn = on; Invalidate(); }
    internal void DemoTip(string id)
    {
        var it = _items.Concat(_bottomItems).FirstOrDefault(i => i.Id == id);
        if (it == null) return;
        UpdateHover(new Point(it.R.X + it.R.Width / 2, it.R.Y + it.R.Height / 2));
        _tipFor = id;
        Invalidate();
    }
    internal Rectangle BottomBarArea => Rectangle.Union(_bottom, _helpOn ? _helpRect : _bottom);

    int S(double v) => (int)Math.Round(v * _scale);
    float F(double v) => (float)(v * _scale);
    double FX(int x) => x / (double)Math.Max(1, ClientSize.Width);
    double FY(int y) => y / (double)Math.Max(1, ClientSize.Height);
    int PX(double f) => (int)Math.Round(f * ClientSize.Width);
    int PY(double f) => (int)Math.Round(f * ClientSize.Height);
    Rectangle ZoneRect(EditZone z) => Rectangle.FromLTRB(PX(z.L), PY(z.T), PX(z.R), PY(z.B));
    bool Magnet => (ModifierKeys & Keys.Alt) == 0;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _mon.Work.Left, _mon.Work.Top, _mon.Work.Width, _mon.Work.Height, Native.SWP_SHOWWINDOW);
        Activate();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_DPICHANGED = 0x02E0;
        const int WM_SYSCOMMAND = 0x0112, SC_KEYMENU = 0xF100;
        if (m.Msg == WM_DPICHANGED) { m.Result = IntPtr.Zero; return; } // we place ourselves in physical pixels
        if (m.Msg == WM_SYSCOMMAND && ((int)m.WParam & 0xFFF0) == SC_KEYMENU) { m.Result = IntPtr.Zero; return; } // Alt is our snap-off key, not a menu
        base.WndProc(ref m);
    }

    void OnThemeChanged()
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            _backdropBrush?.Dispose(); _backdropBrush = null;
            _backdrop?.Dispose(); _backdrop = null; _sceneKey = "";
            BackColor = Theme.EditorSolid;
            Invalidate();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged;
            _timer.Dispose(); _toastAnim.Dispose();
            _fBig.Dispose(); _fMed.Dispose(); _fSmall.Dispose(); _fZone.Dispose(); _fZoneSmall.Dispose();
            _blurSmall?.Dispose(); _backdropBrush?.Dispose(); _backdrop?.Dispose(); _scene?.Dispose(); _barCache?.Dispose();
        }
        base.Dispose(disposing);
    }

    public void Toast(string text, double seconds = 3.5) => Toast(text, seconds, false);

    public void Toast(string text, double seconds, bool warn)
    {
        bool fresh = _toast == null;
        _toast = text; _toastWarn = warn;
        _toastUntil = DateTime.Now.AddSeconds(seconds);
        if (fresh && IsHandleCreated)
            _toastAnim.Start(Theme.ToastMs, t => { _toastT = Theme.Ease(t); Invalidate(Rectangle.Inflate(_toastRect, S(4), S(16))); });
        else _toastT = 1;
        Invalidate();
    }

    void Warn(string text, double seconds = 3.5) => Toast(text, seconds, true);

    void Tick()
    {
        var now = DateTime.Now;
        bool repaint = false;
        if (_toast != null && now > _toastUntil) { _toast = null; repaint = true; }
        if (_deleteArmedUntil != default && now > _deleteArmedUntil) { _deleteArmedUntil = default; repaint = true; }
        if (_naming && (++_caretTick % 3 == 0)) Invalidate(_bottom);
        if (_hoverItem != null && _tipFor != _hoverItem && (now - _hoverSince).TotalMilliseconds > 450) { _tipFor = _hoverItem; repaint = true; }
        if (repaint) Invalidate();
    }

    // ---------- toolbar ----------

    List<Item> BuildItems()
    {
        var list = new List<Item>
        {
            new() { Id = "grip", Glyph = "grip", MinW = S(22) },
            new() { Id = "tool.split", Glyph = "split", Text = "Split", Key = "S", On = _tool == Tool.Split, Enabled = _m.Zones.Count > 0 },
            new() { Id = "tool.draw", Glyph = "draw", Text = "Draw", Key = "D", On = _tool == Tool.Draw },
            new() { Id = "tool.move", Glyph = "move", Text = "Move", Key = "M", On = _tool == Tool.Move, Enabled = _m.Zones.Count > 0 },
            new() { Kind = Kind.Sep },
            new() { Id = "undo", Glyph = "undo", Enabled = _m.CanUndo },
            new() { Id = "redo", Glyph = "redo", Enabled = _m.CanRedo },
            new() { Kind = Kind.Sep },
            new() { Text = "New zones", Kind = Kind.Label },
            new() { Id = "new.percent", Text = "%", On = _m.NewMode == ZoneMode.Percent },
            new() { Id = "new.pixels", Text = "px", On = _m.NewMode == ZoneMode.Pixels },
            new() { Id = "new.grid", Text = "grid", On = _m.NewMode == ZoneMode.Grid },
            new() { Id = "cols-", Glyph = "minus", Enabled = _m.Cols > 1 },
            new() { Id = "cols", Text = $"Cols {_m.Cols}" },
            new() { Id = "cols+", Glyph = "plus" },
            new() { Id = "rows-", Glyph = "minus", Enabled = _m.Rows > 1 },
            new() { Id = "rows", Text = $"Rows {_m.Rows}" },
            new() { Id = "rows+", Glyph = "plus" },
            new() { Kind = Kind.Sep },
            new() { Id = "open", Glyph = "open", Text = "Open…" },
            new() { Id = "savecopy", Glyph = "copy", Text = "Save copy" },
            new() { Id = "delete", Glyph = "delete", Text = _deleteArmedUntil != default ? "Click again to delete" : "Delete", Enabled = _lib.Find(_m.LayoutId) != null },
            new() { Kind = Kind.Sep },
        };
        list.Add(new Item { Text = "Show on", Kind = Kind.Label });
        foreach (var mon in _monitors)
            list.Add(new Item { Id = "mon:" + mon.Key, Glyph = "monitor", Text = mon.Key == _mon.Key ? $"{mon.Number} here" : $"{mon.Number}", On = _showOn.Contains(mon.Key) });
        list.Add(new Item { Kind = Kind.Sep });
        list.Add(new Item { Id = "close", Glyph = "close" });
        return list;
    }

    int ItemWidth(Item it)
    {
        int glyph = it.Glyph.Length > 0 ? S(16) : 0;
        int text = it.Text.Length > 0 ? TextRenderer.MeasureText(it.Text, _fMed, Size.Empty, TextFormatFlags.NoPadding).Width : 0;
        int key = it.Key.Length > 0 ? TextRenderer.MeasureText(it.Key, _fSmall, Size.Empty, TextFormatFlags.NoPadding).Width + S(10) : 0;
        int w = it.Kind switch
        {
            Kind.Label => text + S(6),
            Kind.Field => text + S(24),
            _ => (glyph > 0 && text > 0 ? glyph + S(7) + text : glyph + text) + key + (text > 0 ? S(22) : S(16)),
        };
        if (it.Dot) w += S(12);
        return Math.Max(Math.Max(it.MinW, w), S(32));
    }

    void LayoutBar()
    {
        _items = BuildItems();
        int h = S(34), gap = S(4), pad = S(8), sep = S(13);
        int maxW = ClientSize.Width - S(48);
        int x = 0, y = 0, totalW = 0;
        foreach (var it in _items)
        {
            int w;
            if (it.Kind == Kind.Sep) { if (x == 0) { it.R = Rectangle.Empty; continue; } w = sep; }
            else w = ItemWidth(it);
            if (x > 0 && x + w > maxW) { x = 0; y += h + gap; if (it.Kind == Kind.Sep) { it.R = Rectangle.Empty; continue; } }
            it.R = new Rectangle(x, y, w, h);
            x += w + gap;
            totalW = Math.Max(totalW, x - gap);
        }
        int bw = totalW + 2 * pad, bh = y + h + 2 * pad;
        var origin = _barPos ?? new Point((ClientSize.Width - bw) / 2, S(14));
        origin.X = Math.Clamp(origin.X, 0, Math.Max(0, ClientSize.Width - bw));
        origin.Y = Math.Clamp(origin.Y, 0, Math.Max(0, ClientSize.Height - bh));
        _bar = new Rectangle(origin, new Size(bw, bh));
        foreach (var it in _items) if (it.R != Rectangle.Empty) it.R.Offset(origin.X + pad, origin.Y + pad);
    }

    Item? ItemAt(Point p) => _items.Concat(_bottomItems).FirstOrDefault(i => i.Kind != Kind.Label && i.Kind != Kind.Sep && i.R.Contains(p));

    /// <summary>True over the toolbar, the bottom bar or the open shortcuts panel.</summary>
    bool InUi(Point p) => _bar.Contains(p) || _bottom.Contains(p) || (_helpOn && _helpRect.Contains(p));

    /// <summary>Bottom bar: only the layout name, Save, and ? for shortcuts.</summary>
    void LayoutBottom()
    {

        _bottomItems = new List<Item>
        {
            new() { Id = "name", Kind = Kind.Field, Text = _m.Name, MinW = S(260), On = _naming },
            new() { Id = "save", Glyph = "save", Text = "Save", Kind = Kind.Primary, Dot = _lib.Find(_m.LayoutId) == null || _m.Dirty },
            new() { Id = "help", Text = "?", On = _helpOn, MinW = S(34) },
        };
        int h = S(34), gap = S(6), pad = S(8);
        int x = 0;
        foreach (var it in _bottomItems) { int w = ItemWidth(it); it.R = new Rectangle(x, 0, w, h); x += w + gap; }
        int bw = x - gap + 2 * pad, bh = h + 2 * pad;
        _bottom = new Rectangle((ClientSize.Width - bw) / 2, ClientSize.Height - bh - S(18), bw, bh);
        foreach (var it in _bottomItems) it.R.Offset(_bottom.X + pad, _bottom.Y + pad);
        _helpRect = _helpOn ? HelpRect() : Rectangle.Empty;
    }

    string Hint(string id) => id switch
    {
        "grip" => "Drag to move the toolbar.",
        "tool.split" => "Split (S): click a zone to cut it in two. Shift cuts the other way.",
        "tool.draw" => "Draw (D): drag anywhere to draw a new zone. Zones may overlap.",
        "tool.move" => "Move (M): drag a zone to move it.",
        "undo" => "Undo.  Ctrl+Z",
        "redo" => "Redo.  Ctrl+Y",
        "new.percent" => "New zones use percent: they scale with any monitor.",
        "new.pixels" => "New zones use pixels: a fixed size, clamped to fit smaller monitors.",
        "new.grid" => "New zones use the grid: edges snap to grid lines.",
        "cols" or "rows" => "Grid size. Click adds, right-click removes, or scroll.",
        "cols-" => "One grid column less.",
        "cols+" => "One grid column more.",
        "rows-" => "One grid row less.",
        "rows+" => "One grid row more.",
        "name" => "Layout name. Click and type; Enter to finish.",
        "help" => "Shortcuts and tips.  ? or H",
        "open" => "Start from a saved layout, or from one blank zone.  O",
        "save" => "Save this layout (overwrites the opened one).  Ctrl+S",
        "savecopy" => "Save as a new layout and keep the original.  Ctrl+Shift+S",
        "delete" => "Delete the opened layout from the library. Click twice.",
        "close" => "Close the editor.  Esc",
        _ when id.StartsWith("mon:") => "Show this layout in that monitor's picker. Applies on Save.",
        _ => "",
    };

    void OnItem(Item it, MouseButtons button)
    {
        int step = button == MouseButtons.Right ? -1 : 1;
        switch (it.Id)
        {
            case "tool.split": _tool = Tool.Split; break;
            case "tool.draw": _tool = Tool.Draw; break;
            case "tool.move": _tool = Tool.Move; break;
            case "undo": _m.Undo(); break;
            case "redo": _m.Redo(); break;
            case "new.percent": _m.NewMode = ZoneMode.Percent; break;
            case "new.pixels": _m.NewMode = ZoneMode.Pixels; break;
            case "new.grid": _m.NewMode = ZoneMode.Grid; break;
            case "cols": _m.SetGrid(_m.Cols + step, _m.Rows); break;
            case "rows": _m.SetGrid(_m.Cols, _m.Rows + step); break;
            case "cols-": _m.SetGrid(_m.Cols - 1, _m.Rows); break;
            case "cols+": _m.SetGrid(_m.Cols + 1, _m.Rows); break;
            case "rows-": _m.SetGrid(_m.Cols, _m.Rows - 1); break;
            case "rows+": _m.SetGrid(_m.Cols, _m.Rows + 1); break;
            case "name": _naming = true; _caretTick = 0; break;
            case "help": _helpOn = !_helpOn; break;
            case "open": OpenPanel(); break;
            case "save": Save(false); break;
            case "savecopy": Save(true); break;
            case "delete": DeleteLayout(); break;
            case "close": TryClose(); break;
            default:
                if (it.Id.StartsWith("mon:"))
                {
                    string key = it.Id[4..];
                    if (!_showOn.Remove(key)) _showOn.Add(key);
                }
                break;
        }
        Invalidate();
    }

    // ---------- open, save, delete ----------

    void OpenPanel()
    {
        _openPanel = true;
        _openCells.Clear();
        var all = new List<ZoneLayout?> { null };
        // This monitor's strip first, then the rest of the library.
        var strip = _lib.StripFor(_mon.Key, _setKey);
        all.AddRange(strip);
        _openStrip = strip.Count;
        _cardDown = -1; _cardDragging = false; _cardTarget = -1;
        all.AddRange(_lib.Data.Layouts.Where(l => !strip.Contains(l)));
        int W = ClientSize.Width, H = ClientSize.Height;
        int pad = S(24), gap = S(18), labelH = S(26), titleH = S(64);
        int cellW = S(180), cellH = Math.Max(S(30), (int)Math.Round(cellW * (double)H / W));
        int perRow = Math.Max(1, Math.Min(all.Count, (Math.Min(W - S(80), S(1300)) - 2 * pad + gap) / (cellW + gap)));
        int rows = (all.Count + perRow - 1) / perRow;
        int pw = 2 * pad + perRow * cellW + (perRow - 1) * gap;
        int ph = titleH + 2 * pad + rows * (cellH + labelH + gap) - gap;
        ph = Math.Min(ph, H - S(40));
        _panel = new Rectangle((W - pw) / 2, Math.Max(S(20), (H - ph) / 2), pw, ph);
        for (int i = 0; i < all.Count; i++)
        {
            int r = i / perRow, c = i % perRow;
            var cell = new Rectangle(_panel.X + pad + c * (cellW + gap), _panel.Y + titleH + pad + r * (cellH + labelH + gap), cellW, cellH);
            if (cell.Bottom + labelH > _panel.Bottom) break; // more layouts than fit: the rest stay in the file
            _openCells.Add((cell, all[i]));
        }
        Invalidate();
    }

    // Drag a card in the Open panel to set the picker order of this monitor.

    /// <summary>
    /// The slot a drop at p would use: insert before cell s (1.._openStrip), or s = _openStrip + 1 for after the last strip card.
    /// Picks the nearest strip card (gaps and the panel edges count), and the side from the cursor. -1 outside the panel.
    /// </summary>
    int CardSlotAt(Point p)
    {
        if (_openStrip < 1 || !Rectangle.Inflate(_panel, S(40), S(40)).Contains(p)) return -1;
        int best = -1; double bestD = double.MaxValue;
        for (int i = 1; i <= _openStrip && i < _openCells.Count; i++)
        {
            var c = _openCells[i].cell;
            // The label under a card counts as part of it.
            var box = new Rectangle(c.X, c.Y, c.Width, c.Height + S(26));
            double dx = Math.Max(0, Math.Max(box.Left - p.X, p.X - box.Right));
            double dy = Math.Max(0, Math.Max(box.Top - p.Y, p.Y - box.Bottom));
            double d = dx * dx + 4 * dy * dy; // a row is much farther than a neighbour in the same row
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best < 0) return -1;
        var bc = _openCells[best].cell;
        return p.X < bc.X + bc.Width / 2 ? best : best + 1;
    }

    /// <summary>Final cell position of the dragged card for a slot, or -1 when it would not move.</summary>
    int CardFinal(int down, int slot)
    {
        if (slot < 0) return -1;
        int f = down >= 1 && down <= _openStrip && down < slot ? slot - 1 : slot;
        return f == down ? -1 : f;
    }

    void OpenCardMove(Point p)
    {
        _mouse = p;
        var (cell, layout) = _openCells[_cardDown];
        if (!_cardDragging && layout != null && Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > S(6))
        {
            _cardDragging = true;
            _cardGrab = new Point(_down.X - cell.X, _down.Y - cell.Y);
        }
        if (_cardDragging)
        {
            _cardTarget = CardSlotAt(p);
            if (CardFinal(_cardDown, _cardTarget) < 0) _cardTarget = -1; // lands in its own place: nothing to do
            if (Cursor != Cursors.SizeAll) Cursor = Cursors.SizeAll;
        }
        Invalidate();
    }

    void OpenCardUp(Point p)
    {
        int down = _cardDown, target = _cardTarget;
        bool dragging = _cardDragging;
        _cardDown = -1; _cardDragging = false; _cardTarget = -1;
        if (down < 0 || down >= _openCells.Count) return;
        var layout = _openCells[down].layout;
        if (!dragging)
        {
            // A click without a move: open it, but only when the button went up on the same card.
            if (_openCells.FindIndex(o => o.cell.Contains(p)) == down) LoadLayout(layout);
            else Invalidate();
            return;
        }
        int final = layout == null ? -1 : CardFinal(down, target);
        if (layout == null || final < 0) { Invalidate(); return; }
        // Work on the ids the panel shows (StripFor is what OpenPanel listed after Blank), so the slot matches the screen.
        var ids = _lib.StripFor(_mon.Key, _setKey).Select(l => l.Id).ToList();
        bool wasIn = ids.Remove(layout.Id);
        ids.Insert(Math.Clamp(final - 1, 0, ids.Count), layout.Id);
        var r = wasIn ? StripMove.Moved : StripMove.Added;
        if (!_lib.SetStripOrder(_mon.Key, ids)) { Toast("Order unchanged."); Invalidate(); return; }
        try { _lib.Save(); }
        catch (Exception ex) { Warn("Could not save the layouts file: " + ex.Message, 8); OpenPanel(); return; }
        // A layout that was only on another monitor is now shown here too: keep the editor's Show on in step.
        if (r == StripMove.Added && layout.Id == _m.LayoutId) _showOn.Add(_mon.Key);
        OpenPanel();
        int pos = _lib.StripFor(_mon.Key, _setKey).FindIndex(l => l.Id == layout.Id) + 1;
        string msg = r == StripMove.Added
            ? $"Added \"{layout.Name}\" to this monitor's picker at position {pos}."
            : $"Moved \"{layout.Name}\" to position {pos} in the picker.";
        Toast(msg, 5);
        DragWatcher.Log($"editor reordered strip: \"{layout.Name}\" ({layout.Id}) to position {pos}{(r == StripMove.Added ? " (added)" : "")}");
    }

    void LoadLayout(ZoneLayout? layout)
    {
        _openPanel = false;
        if (layout == null)
        {
            _m.NewBlank();
            _tool = Tool.Draw;
            _showOn.Clear(); _showOn.Add(_mon.Key);
            Toast("Blank layout: no zones yet. Drag on the screen to draw a zone.", 5);
        }
        else
        {
            _m.Load(layout);
            _showOn.Clear();
            foreach (var mon in _monitors) if (_lib.IsShown(layout.Id, mon.Key)) _showOn.Add(mon.Key);
            Toast($"Editing \"{layout.Name}\". Save overwrites it; Save copy keeps the original.");
        }
        Invalidate();
    }

    /// <summary>
    /// Start on a new, unsaved layout copied from <paramref name="src"/> (the "Layout" pill in the picker).
    /// Save stores it as a new layout, shown on this monitor; the original stays as it is.
    /// </summary>
    public void StartFromCopy(ZoneLayout src)
    {
        _openPanel = false;
        _m.Load(src, undoable: false);
        _m.LayoutId = null;
        string name = src.Name + " copy";
        for (int k = 2; _lib.Data.Layouts.Any(l => l.Name == name); k++) name = $"{src.Name} copy {k}";
        _m.Name = name;
        _showOn.Clear(); _showOn.Add(_mon.Key);
        Toast($"New layout from \"{src.Name}\". Save keeps it as a new layout.", 5);
        Invalidate();
    }

    void Save(bool copy)
    {
        if (_m.Zones.Count == 0) { Warn("Split or draw at least one zone first."); return; }
        if (copy)
        {
            _m.LayoutId = null;
            if (_lib.Data.Layouts.Any(l => l.Name == _m.Name.Trim())) _m.Name = _m.Name.Trim() + " copy";
        }
        var layout = _m.ToLayout();
        if (string.IsNullOrEmpty(layout.Id) || _lib.Find(layout.Id) == null) layout.Id = string.IsNullOrEmpty(layout.Id) ? Library.NewId() : layout.Id;
        _m.LayoutId = layout.Id;
        _m.Name = layout.Name;
        _lib.Upsert(layout);
        foreach (var mon in _monitors) _lib.SetShown(layout.Id, mon.Key, _showOn.Contains(mon.Key), mon.Label);
        try { _lib.Save(); }
        catch (Exception ex) { Warn("Could not save the layouts file: " + ex.Message, 8); return; }
        _m.MarkSaved();
        var on = _monitors.Where(m => _showOn.Contains(m.Key)).Select(m => m.Number.ToString()).ToList();
        if (on.Count == 0) Warn($"Saved \"{layout.Name}\", but it shows on no monitor. Turn on a monitor under Show on, then save again.", 5);
        else Toast($"Saved \"{layout.Name}\" ({layout.Zones.Count} zones). In the picker on monitor {string.Join(", ", on)}.", 5);
        DragWatcher.Log($"editor saved \"{layout.Name}\" ({layout.Id}), {layout.Zones.Count} zones, shown on monitor {string.Join(", ", on)}");
    }

    void DeleteLayout()
    {
        var existing = _lib.Find(_m.LayoutId);
        if (existing == null) { Warn("This layout is not saved, so there is nothing to delete."); return; }
        if (_deleteArmedUntil == default) { _deleteArmedUntil = DateTime.Now.AddSeconds(3); return; }
        _deleteArmedUntil = default;
        _lib.Delete(existing.Id);
        try { _lib.Save(); }
        catch (Exception ex) { Warn("Could not save the layouts file: " + ex.Message, 8); return; }
        _m.LayoutId = null;
        Toast($"Deleted \"{existing.Name}\" from the library. The zones stay here; Save keeps them as a new layout. The previous file is in layouts.json.bak.", 6);
        DragWatcher.Log($"editor deleted \"{existing.Name}\" ({existing.Id})");
    }

    void TryClose()
    {
        if (_m.Dirty && DateTime.Now > _escArmedUntil)
        {
            _escArmedUntil = DateTime.Now.AddSeconds(3);
            Warn("Unsaved changes. Press Esc again to close without saving, or Ctrl+S to save.");
            return;
        }
        Close();
    }

    // ---------- mouse ----------

    void UpdateHover(Point p)
    {
        string? before = _hoverItem;
        try { UpdateHoverCore(p); }
        finally
        {
            if (_hoverItem != before) { _hoverSince = DateTime.Now; if (_tipFor != null) { _tipFor = null; Invalidate(); } }
        }
    }

    void UpdateHoverCore(Point p)
    {
        _mouse = p;
        _hoverItem = null; _hoverZone = -1; _hoverChip = -1; _hv = _hh = null;
        Cursor c = Cursors.Default;
        if (_openPanel) { c = _cardDragging ? Cursors.SizeAll : _openCells.Any(o => o.cell.Contains(p)) ? Cursors.Hand : Cursors.Default; }
        else if (InUi(p))
        {
            var it = ItemAt(p);
            _hoverItem = it?.Id;
            if (it != null) c = it.Id == "grip" ? Cursors.SizeAll : it.Id == "name" ? Cursors.IBeam : Cursors.Hand;
        }
        else
        {
            foreach (var kv in _chips) if (kv.Value.Contains(p)) { _hoverChip = kv.Key; break; }
            double fx = FX(p.X), fy = FY(p.Y);
            _hoverZone = _m.HitZone(fx, fy);
            if (_hoverChip >= 0) c = Cursors.Hand;
            else
            {
                bool join = (ModifierKeys & Keys.Control) != 0;
                _hv = _m.FindDivider(fx, fy, true, S(7), join);
                _hh = _m.FindDivider(fx, fy, false, S(7), join);
                if (_hv != null && _hh != null) c = Cursors.SizeAll;
                else if (_hv != null) c = Cursors.SizeWE;
                else if (_hh != null) c = Cursors.SizeNS;
                else if (_tool == Tool.Draw || _hoverZone < 0) c = Icons.Cursor(Icons.CursorKind.Draw, _scale);
                else if (_tool == Tool.Move) c = Cursors.SizeAll;
                else c = Icons.Cursor(SplitVertical(_hoverZone) ? Icons.CursorKind.SplitVertical : Icons.CursorKind.SplitHorizontal, _scale);
            }
        }
        if (Cursor != c) Cursor = c;
    }

    bool SplitVertical(int zone) => _m.DefaultVertical(zone) ^ ((ModifierKeys & Keys.Shift) != 0);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        UpdateHover(e.Location);
        _down = e.Location;
        if (_naming && ItemAt(e.Location)?.Id != "name") _naming = false;

        if (_openPanel)
        {
            int hi = _openCells.FindIndex(o => o.cell.Contains(e.Location));
            if (hi >= 0) { _cardDown = hi; _cardDragging = false; _cardTarget = -1; }
            else if (!_panel.Contains(e.Location)) { _openPanel = false; Invalidate(); }
            return;
        }
        if (InUi(e.Location))
        {
            var it = ItemAt(e.Location);
            if (it == null) return;
            if (it.Id == "grip") { _drag = DragKind.Bar; _barGrab = new Point(e.X - _bar.X, e.Y - _bar.Y); return; }
            if (it.Enabled) OnItem(it, e.Button);
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            if (_hoverZone >= 0) ZoneMenu(_hoverZone, e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (_hoverChip >= 0)
        {
            _m.SetMode(_hoverChip, EditorModel.NextMode(_m.Zones[_hoverChip].Mode));
            Invalidate();
            return;
        }
        if (_hv != null || _hh != null)
        {
            _m.Push();
            _dv = _hv; _dh = _hh;
            _drag = DragKind.Divider;
            return;
        }
        double fx = FX(e.X), fy = FY(e.Y);
        if (_tool == Tool.Draw || _hoverZone < 0)
        {
            _drag = DragKind.Draw; _drawing = false;
            _drawRect = _m.SnapRect(fx, fy, fx, fy, Magnet);
        }
        else if (_tool == Tool.Move)
        {
            _m.Push();
            _drag = DragKind.Move; _dragZone = _hoverZone; _dragStart = _m.Zones[_hoverZone].Clone();
        }
        else { _drag = DragKind.Split; _dragZone = _hoverZone; }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        double fx = FX(e.X), fy = FY(e.Y);
        const double eps = 1e-7;
        if (_openPanel && _cardDown >= 0) { OpenCardMove(e.Location); return; }
        _guides.Clear();
        switch (_drag)
        {
            case DragKind.Bar:
                _barPos = new Point(e.X - _barGrab.X, e.Y - _barGrab.Y);
                break;
            case DragKind.Divider:
                if (_dv != null)
                {
                    double p = _m.SnapDivider(_dv, fx, Magnet);
                    _m.MoveDivider(_dv, p);
                    if (Math.Abs(p - fx) > eps) _guides.Add((true, p));
                }
                if (_dh != null)
                {
                    double p = _m.SnapDivider(_dh, fy, Magnet);
                    _m.MoveDivider(_dh, p);
                    if (Math.Abs(p - fy) > eps) _guides.Add((false, p));
                }
                break;
            case DragKind.Draw:
                {
                    if (Math.Abs(e.X - _down.X) + Math.Abs(e.Y - _down.Y) > S(4)) _drawing = true;
                    double x0 = FX(_down.X), y0 = FY(_down.Y);
                    _drawRect = _m.SnapRect(x0, y0, fx, fy, Magnet);
                    var (l, t, r, b) = _drawRect;
                    double rl = Math.Min(x0, fx), rr = Math.Max(x0, fx), rt = Math.Min(y0, fy), rb = Math.Max(y0, fy);
                    if (_drawing)
                    {
                        if (Math.Abs(l - rl) > eps) _guides.Add((true, l));
                        if (Math.Abs(r - rr) > eps) _guides.Add((true, r));
                        if (Math.Abs(t - rt) > eps) _guides.Add((false, t));
                        if (Math.Abs(b - rb) > eps) _guides.Add((false, b));
                    }
                    break;
                }
            case DragKind.Move:
                {
                    double dx = fx - FX(_down.X), dy = fy - FY(_down.Y);
                    _m.MoveZone(_dragZone, _dragStart!, dx, dy, Magnet);
                    var z = _m.Zones[_dragZone];
                    // When the zone snapped, show each of its edges that now lines up with something.
                    if (Math.Abs(z.L - (_dragStart!.L + dx)) > eps)
                        foreach (var p in new[] { z.L, z.R }) if (p > eps && p < 1 - eps && IsEdge(p, true)) _guides.Add((true, p));
                    if (Math.Abs(z.T - (_dragStart.T + dy)) > eps)
                        foreach (var p in new[] { z.T, z.B }) if (p > eps && p < 1 - eps && IsEdge(p, false)) _guides.Add((false, p));
                    break;
                }
            case DragKind.Split:
                break;
            default:
                UpdateHover(e.Location);
                break;
        }
        Invalidate();
    }

    /// <summary>True when a position lines up with another zone's edge, the centre or a border.</summary>
    bool IsEdge(double pos, bool vertical)
    {
        const double eps = 1e-6;
        if (Math.Abs(pos) < eps || Math.Abs(pos - 1) < eps || Math.Abs(pos - 0.5) < eps) return true;
        for (int i = 0; i < _m.Zones.Count; i++)
        {
            if (i == _dragZone) continue;
            var z = _m.Zones[i];
            if (vertical ? Math.Abs(z.L - pos) < eps || Math.Abs(z.R - pos) < eps : Math.Abs(z.T - pos) < eps || Math.Abs(z.B - pos) < eps) return true;
        }
        return false;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_cardDown >= 0) { OpenCardUp(e.Location); return; }
        double fx = FX(e.X), fy = FY(e.Y);
        switch (_drag)
        {
            case DragKind.Divider:
            case DragKind.Move:
                _m.PopIfUnchanged();
                break;
            case DragKind.Draw:
                if (_drawing)
                {
                    var (l, t, r, b) = _drawRect;
                    if (_m.AddZone(l, t, r, b) < 0) Warn($"Too small. A zone needs at least {S(32)} x {S(32)} px.");
                }
                break;
            case DragKind.Split:
                {
                    int z = _m.HitZone(fx, fy);
                    if (z >= 0 && z == _dragZone)
                    {
                        bool v = SplitVertical(z);
                        double pos = _m.SnapSplit(z, v, v ? fx : fy, Magnet);
                        if (!_m.Split(z, v, pos)) Warn($"Too small to split here. Each part needs at least {S(32)} px.");
                    }
                    break;
                }
        }
        _drag = DragKind.None; _dv = _dh = null; _dragZone = -1; _drawing = false;
        _guides.Clear();
        UpdateHover(e.Location);
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || _openPanel || InUi(e.Location)) return;
        var v = _dv ?? _hv; var h = _dh ?? _hh;
        if (_drag == DragKind.Divider) { _m.PopIfUnchanged(); _drag = DragKind.None; _dv = _dh = null; }
        var d = v != null && h == null ? v : h != null && v == null ? h : null;
        if (d == null) return;
        if (!_m.Merge(d, d.Vertical ? FY(e.Y) : FX(e.X), out string why)) Warn(why);
        UpdateHover(e.Location);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int step = Math.Sign(e.Delta);
        if (_hoverItem is "cols" or "cols-" or "cols+") _m.SetGrid(_m.Cols + step, _m.Rows);
        else if (_hoverItem is "rows" or "rows-" or "rows+") _m.SetGrid(_m.Cols, _m.Rows + step);
        Invalidate();
    }

    void ZoneMenu(int i, Point at)
    {
        var cm = new ContextMenuStrip { Renderer = new GlassMenuRenderer(), Font = _fMed, ShowImageMargin = true };
        cm.Items.Add("Delete zone\tDel", null, (_, _) => { _m.Delete(i); Invalidate(); });
        cm.Items.Add("Duplicate zone", null, (_, _) => { _m.Duplicate(i); Invalidate(); });
        cm.Items.Add(new ToolStripSeparator());
        void Mode(string text, ZoneMode m) =>
            cm.Items.Add(new ToolStripMenuItem(text, null, (_, _) => { _m.SetMode(i, m); Invalidate(); }) { Checked = _m.Zones[i].Mode == m });
        Mode("Percent: scales with the monitor\t1", ZoneMode.Percent);
        Mode("Pixels: fixed size\t2", ZoneMode.Pixels);
        Mode("Grid: snaps to grid lines\t3", ZoneMode.Grid);
        cm.Closed += (_, _) => BeginInvoke(() => cm.Dispose());
        cm.Show(this, at);
    }

    // ---------- keyboard ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_naming)
        {
            if (e.KeyCode is Keys.Enter or Keys.Escape) _naming = false;
            else if (e.KeyCode == Keys.Back && _m.Name.Length > 0) _m.Name = e.Control ? "" : _m.Name[..^1];
            _caretTick = 0;
            e.Handled = true;
            Invalidate();
            return;
        }
        bool ctrl = e.Control, shift = e.Shift;
        switch (e.KeyCode)
        {
            case Keys.Escape:
                if (_drag is DragKind.Divider or DragKind.Move) _m.CancelDrag();
                if (_cardDown >= 0) { _cardDown = -1; _cardDragging = false; _cardTarget = -1; }
                else if (_drag != DragKind.None) { _drag = DragKind.None; _dv = _dh = null; _drawing = false; _guides.Clear(); }
                else if (_openPanel) _openPanel = false;
                else if (_helpOn) _helpOn = false;
                else TryClose();
                break;
            case Keys.Z when ctrl && shift: _m.Redo(); break;
            case Keys.Z when ctrl: _m.Undo(); break;
            case Keys.Y when ctrl: _m.Redo(); break;
            case Keys.S when ctrl: Save(shift); break;
            case Keys.S: if (_m.Zones.Count > 0) _tool = Tool.Split; break;
            case Keys.D: _tool = Tool.Draw; break;
            case Keys.M: if (_m.Zones.Count > 0) _tool = Tool.Move; break;
            case Keys.O: OpenPanel(); break;
            case Keys.H or Keys.F1: _helpOn = !_helpOn; break;
            case Keys.OemQuestion: _helpOn = !_helpOn; break;
            case Keys.Delete or Keys.Back: if (_hoverZone >= 0 && _drag == DragKind.None) { _m.Delete(_hoverZone); _hoverZone = -1; } break;
            case Keys.D1 or Keys.NumPad1: _m.SetMode(_hoverZone, ZoneMode.Percent); break;
            case Keys.D2 or Keys.NumPad2: _m.SetMode(_hoverZone, ZoneMode.Pixels); break;
            case Keys.D3 or Keys.NumPad3: _m.SetMode(_hoverZone, ZoneMode.Grid); break;
        }
        if (_drag == DragKind.None) UpdateHover(_mouse);
        e.Handled = true;
        Invalidate();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_drag == DragKind.None) UpdateHover(_mouse);
        Invalidate();
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (_naming && !char.IsControl(e.KeyChar) && _m.Name.Length < 60) { _m.Name += e.KeyChar; _caretTick = 0; Invalidate(); }
        e.Handled = true;
    }

    // ---------- paint ----------

    bool ShowGrid => (_tool == Tool.Draw && _m.NewMode == ZoneMode.Grid)
        || (_hoverZone >= 0 && _hoverZone < _m.Zones.Count && _m.Zones[_hoverZone].Mode == ZoneMode.Grid)
        || (_drag == DragKind.Divider && ((_dv != null && _m.UsesGrid(_dv)) || (_dh != null && _m.UsesGrid(_dh))))
        || (_hv != null && _m.UsesGrid(_hv)) || (_hh != null && _m.UsesGrid(_hh));

    protected override void OnPaintBackground(PaintEventArgs e) { } // the scene covers everything

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        LayoutBar();
        LayoutBottom();
        double t0 = sw.Elapsed.TotalMilliseconds;
        EnsureScene();
        double t1 = sw.Elapsed.TotalMilliseconds;
        var clip = Rectangle.Intersect(e.ClipRectangle, ClientRectangle);
        IntPtr hdc = g.GetHdc();
        try { Gdi.BitBlt(hdc, clip.X, clip.Y, clip.Width, clip.Height, _scene!.Dc, clip.X, clip.Y, 0x00CC0020); }
        finally { g.ReleaseHdc(hdc); }
        double t2 = sw.Elapsed.TotalMilliseconds;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        if (!_openPanel)
        {
            PaintGuides(g);
            PaintHandles(g);
            PaintPreview(g);
        }
        double t3 = sw.Elapsed.TotalMilliseconds;
        PaintBarCached(g);
        double t4 = sw.Elapsed.TotalMilliseconds;
        PaintBottom(g);
        double t5 = sw.Elapsed.TotalMilliseconds;
        if (_openPanel) PaintOpenPanel(g);
        if (_toast != null) PaintToast(g);
        if (_tipFor != null && _tipFor == _hoverItem && !_openPanel) PaintTip(g);
        double t6 = sw.Elapsed.TotalMilliseconds;
        LastPaint = $"layout {t0:0.0}, scene {t1 - t0:0.0}, blit {t2 - t1:0.0}, overlays {t3 - t2:0.0}, bar {t4 - t3:0.0}, footer {t5 - t4:0.0}, panel+toast {t6 - t5:0.0}, total {t6:0.0}";
    }

    /// <summary>Where the last frame's time went, for the render check.</summary>
    internal string LastPaint = "";

    /// <summary>Render the cached scene (backdrop, grid, zones) when anything in it changed.</summary>
    void EnsureScene()
    {
        int w = Math.Max(1, ClientSize.Width), h = Math.Max(1, ClientSize.Height);
        bool hotNone = _drag == DragKind.None;
        string key = $"{_m.Signature()}|{(hotNone ? _hoverZone : -1)}|{_hoverChip}|{(_drag == DragKind.Move ? _dragZone : -1)}|{ShowGrid}|{_openPanel}|{Theme.Version}|{w}x{h}";
        if (_scene != null && _scene.Width == w && _scene.Height == h && key == _sceneKey) return;
        // What each zone looks like now. When only some zones changed, repaint just their area.
        var looks = new List<(Rectangle r, ZoneMode mode, bool hot, bool chip)>();
        for (int i = 0; i < _m.Zones.Count; i++)
        {
            var z = _m.Zones[i];
            bool hot = i == _hoverZone && hotNone || i == _dragZone && _drag == DragKind.Move;
            looks.Add((ZoneRect(z), z.Mode, hot, _hoverChip == i));
        }
        string baseKey = $"{_m.Zones.Count}|{_m.Cols}x{_m.Rows}|{ShowGrid}|{_openPanel}|{Theme.Version}|{w}x{h}";
        var full = new Rectangle(0, 0, w, h);
        var dirty = new List<Rectangle>();
        bool partial = _scene != null && _scene.Width == w && _scene.Height == h && baseKey == _sceneBase && _sceneLooks.Count == looks.Count;
        if (partial)
        {
            // One rectangle per changed zone (old and new place), merged where they overlap.
            for (int i = 0; i < looks.Count; i++)
            {
                if (looks[i] == _sceneLooks[i]) continue;
                var u = Rectangle.Union(looks[i].r, _sceneLooks[i].r);
                u.Inflate(S(6), S(6));
                u.Intersect(full);
                for (int j = dirty.Count - 1; j >= 0; j--)
                    if (dirty[j].IntersectsWith(u)) { u = Rectangle.Union(u, dirty[j]); dirty.RemoveAt(j); }
                if (!u.IsEmpty) dirty.Add(u);
            }
        }
        else dirty.Add(full);
        if (_scene == null || _scene.Width != w || _scene.Height != h) { _scene?.Dispose(); _scene = new Surface(w, h); }
        EnsureBackdrop(w, h);
        long area = 0;
        foreach (var d in dirty)
        {
            area += (long)d.Width * d.Height;
            Gdi.BitBlt(_scene.Dc, d.X, d.Y, d.Width, d.Height, _backdrop!.Dc, d.X, d.Y, 0x00CC0020);
            using var g = _scene.CreateGraphics();
            g.SetClip(d);
            if (ShowGrid) PaintGrid(g);
            PaintZones(g, partial ? d : null);
            if (_openPanel) using (var dim = new SolidBrush(Color.FromArgb(Theme.Dark ? 130 : 90, 0, 0, 0))) g.FillRectangle(dim, d);
            if (d.IntersectsWith(Rectangle.Inflate(_bar, S(4), S(4)))) _underBar++;
        }
        _sceneKey = key; _sceneBase = baseKey; _sceneLooks = looks;
        LastSceneArea = area;
    }

    int _underBar;                   // changes when the scene under the toolbar changes

    string _sceneBase = "";
    List<(Rectangle r, ZoneMode mode, bool hot, bool chip)> _sceneLooks = new();
    internal long LastSceneArea;

    void EnsureBackdrop(int w, int h)
    {
        if (_backdrop != null && _backdrop.Width == w && _backdrop.Height == h) return;
        _backdropBrush?.Dispose(); _backdropBrush = null;
        _backdrop?.Dispose();
        _backdrop = new Surface(w, h);
        using var g = _backdrop.CreateGraphics();
        if (_blurSmall != null && Theme.Glass)
        {
            Glass.DrawStretched(g, _blurSmall, new RectangleF(0, 0, w, h));
            using (var t = new SolidBrush(Theme.EditorTint)) g.FillRectangle(t, 0, 0, w, h);
            if (Theme.Grain > 0) g.FillRectangle(Glass.Noise, 0, 0, w, h);
        }
        else
        {
            using var b = new SolidBrush(Theme.EditorSolid);
            g.FillRectangle(b, 0, 0, w, h);
        }
        // Vignette: a touch darker at the edges, so the centre reads as the stage.
        using var path = new GraphicsPath();
        path.AddEllipse(-w * 0.25f, -h * 0.35f, w * 1.5f, h * 1.7f);
        using var vb = new PathGradientBrush(path) { CenterColor = Color.FromArgb(0, 0, 0, 0), SurroundColors = new[] { Color.FromArgb(Theme.Dark ? 70 : 30, 0, 0, 0) } };
        g.FillRectangle(vb, 0, 0, w, h);
    }

    void PaintGrid(Graphics g)
    {
        using var pen = new Pen(Theme.GridLine, 1) { DashStyle = DashStyle.Dot };
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        for (int c = 1; c < _m.Cols; c++) { int x = PX((double)c / _m.Cols); g.DrawLine(pen, x, 0, x, ClientSize.Height); }
        for (int r = 1; r < _m.Rows; r++) { int y = PY((double)r / _m.Rows); g.DrawLine(pen, 0, y, ClientSize.Width, y); }
        g.SmoothingMode = old;
    }

    /// <summary>Paint the zones, all of them or only those touching <paramref name="only"/>.</summary>
    void PaintZones(Graphics g, Rectangle? only = null)
    {
        if (only == null) _chips.Clear();
        var order = Enumerable.Range(0, _m.Zones.Count).OrderByDescending(i => _m.Zones[i].Area).ToList();
        float inset = F(Theme.EditorZoneInset), radius = F(Theme.RadiusZone * 0.8);
        var fmt = StringFormat.GenericTypographic;
        foreach (int i in order)
        {
            var z = _m.Zones[i];
            var r = ZoneRect(z);
            var rf = RectangleF.Inflate(new RectangleF(r.X, r.Y, r.Width, r.Height), -inset, -inset);
            if (only.HasValue && !r.IntersectsWith(only.Value)) continue;
            _chips.Remove(i);
            if (rf.Width < 2 || rf.Height < 2) continue;
            var mc = Theme.ModeColor(z.Mode);
            bool hot = i == _hoverZone && _drag == DragKind.None || i == _dragZone && _drag == DragKind.Move;
            using var path = Theme.Round(rf, radius);
            // A zone that floats over a bigger one: restore the backdrop under it, so it reads as on top.
            if (OverlapsBigger(i) && _scene != null && _backdrop != null)
            {
                g.Flush(FlushIntention.Sync);
                var rr = Rectangle.Round(rf);
                if (only.HasValue) rr.Intersect(only.Value); // stay inside the area being repainted
                if (rr.Width > 0 && rr.Height > 0) Gdi.BitBlt(_scene.Dc, rr.X, rr.Y, rr.Width, rr.Height, _backdrop.Dc, rr.X, rr.Y, 0x00CC0020);
            }
            using (var fill = new SolidBrush(Theme.A(mc, hot ? (Theme.Dark ? 0.32 : 0.30) : (Theme.Dark ? 0.18 : 0.16)))) Glass.FillRound(g, fill, rf, radius);
            using (var hl = new Pen(Color.FromArgb(hot ? 60 : 34, 255, 255, 255), F(1.5)))
            {
                var clip = g.Clip;
                g.SetClip(new RectangleF(rf.X, rf.Y, rf.Width, radius * 1.5f), CombineMode.Intersect);
                using var inner = Theme.Round(RectangleF.Inflate(rf, -F(2.5), -F(2.5)), radius - F(2));
                g.DrawPath(hl, inner);
                g.Clip = clip;
            }
            using (var pen = new Pen(Theme.A(mc, hot ? 1 : 0.78), hot ? F(2.2) : F(1.4)) { Alignment = PenAlignment.Inset }) g.DrawPath(pen, path);

            int pxW = (int)Math.Round(z.W * _m.WorkW), pxH = (int)Math.Round(z.H * _m.WorkH);
            string size = $"{pxW} × {pxH}";
            string detail = z.Mode switch
            {
                ZoneMode.Percent => $"{z.W * 100:0.#}% × {z.H * 100:0.#}%",
                ZoneMode.Grid => $"{Math.Round(z.W * _m.Cols)} × {Math.Round(z.H * _m.Rows)} cells",
                _ => "fixed size in pixels",
            };
            var (cx, cy) = LabelPoint(i, r);
            bool roomy = r.Width >= S(150) && r.Height >= S(110);
            string chipText = rf.Width >= S(110) ? Theme.ModeName(z.Mode) : Theme.ModeText(z.Mode);
            var ct = g.MeasureString(chipText, _fSmall, PointF.Empty, fmt);
            float chipW = ct.Width + F(12 + 6 + 14 + 10), chipH = ct.Height + F(10);
            RectangleF chip;
            using var tb = new SolidBrush(Theme.Text);
            if (roomy)
            {
                var sz = g.MeasureString(size, _fZone, PointF.Empty, fmt);
                var dz = g.MeasureString(detail, _fZoneSmall, PointF.Empty, fmt);
                float blockH = sz.Height + F(4) + dz.Height + F(10) + chipH;
                float y0 = cy - blockH / 2;
                // Labels sit on a soft, smooth panel so they read on any backdrop.
                float bw = Math.Max(sz.Width, Math.Max(dz.Width, chipW)) + F(32);
                using (var bb = new SolidBrush(Theme.A(BarBack, Theme.Dark ? 0.62 : 0.72)))
                    Glass.FillRound(g, bb, new RectangleF(cx - bw / 2, y0 - F(12), bw, blockH + F(24)), F(12));
                g.DrawString(size, _fZone, tb, cx - sz.Width / 2, y0, fmt);
                using (var db = new SolidBrush(Theme.Dim)) g.DrawString(detail, _fZoneSmall, db, cx - dz.Width / 2, y0 + sz.Height + F(4), fmt);
                chip = new RectangleF(cx - chipW / 2, y0 + sz.Height + F(4) + dz.Height + F(10), chipW, chipH);
            }
            else chip = new RectangleF(cx - chipW / 2, cy - chipH / 2, chipW, chipH);
            if (rf.Width >= chip.Width + F(6) && rf.Height >= chip.Height + F(6))
            {
                bool chipHot = _hoverChip == i;
                using (var cp = Theme.Round(chip, chip.Height / 2))
                {
                    using (var cb = new SolidBrush(chipHot ? mc : Theme.Over(Theme.A(mc, Theme.Dark ? 0.30 : 0.20), BarBack))) g.FillPath(cb, cp);
                    using (var ce = new Pen(Theme.A(mc, chipHot ? 1 : 0.7), F(1))) g.DrawPath(ce, cp);
                }
                var fore = chipHot ? Color.FromArgb(16, 16, 18) : Theme.Text;
                using (var fb = new SolidBrush(fore)) g.DrawString(chipText, _fSmall, fb, chip.X + F(12), chip.Y + (chip.Height - ct.Height) / 2, fmt);
                Icons.Glyph(g, "chevron", new RectangleF(chip.Right - F(24), chip.Y + (chip.Height - F(14)) / 2, F(14), F(14)), fore, F(1.5));
                _chips[i] = Rectangle.Round(chip);
            }
        }
    }

    bool OverlapsBigger(int i)
    {
        var z = _m.Zones[i];
        for (int j = 0; j < _m.Zones.Count; j++)
        {
            if (j == i) continue;
            var o = _m.Zones[j];
            if (o.Area < z.Area || (o.Area == z.Area && j > i)) continue;
            if (Math.Min(z.R, o.R) - Math.Max(z.L, o.L) > 1e-6 && Math.Min(z.B, o.B) - Math.Max(z.T, o.T) > 1e-6) return true;
        }
        return false;
    }

    /// <summary>Label centre for a zone: its centre, or the first quarter point no smaller zone covers.</summary>
    (int x, int y) LabelPoint(int i, Rectangle r)
    {
        var z = _m.Zones[i];
        var pts = new[] { (0.5, 0.5), (0.5, 0.2), (0.5, 0.8), (0.2, 0.5), (0.8, 0.5), (0.2, 0.2), (0.8, 0.8) };
        foreach (var (ax, ay) in pts)
        {
            double fx = z.L + z.W * ax, fy = z.T + z.H * ay;
            bool covered = false;
            for (int j = 0; j < _m.Zones.Count && !covered; j++)
                covered = j != i && _m.Zones[j].Area < z.Area && _m.Zones[j].Contains(fx, fy);
            if (!covered) return (PX(fx), PY(fy));
        }
        return (r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    void PaintGuides(Graphics g)
    {
        if (_guides.Count == 0) return;
        using var pen = new Pen(Theme.A(Theme.AccentSoft, 0.85), Math.Max(1, F(1))) { DashStyle = DashStyle.Dash };
        foreach (var (vertical, pos) in _guides)
        {
            if (vertical) { int x = PX(pos); g.DrawLine(pen, x, 0, x, ClientSize.Height); }
            else { int y = PY(pos); g.DrawLine(pen, 0, y, ClientSize.Width, y); }
        }
    }

    void PaintHandles(Graphics g)
    {
        var v = _drag == DragKind.Divider ? _dv : _drag == DragKind.None ? _hv : null;
        var h = _drag == DragKind.Divider ? _dh : _drag == DragKind.None ? _hh : null;
        using var glow = new Pen(Theme.A(Theme.Accent, 0.30), F(9)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var pen = new Pen(Theme.Accent, F(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        void Handle(PointF a, PointF b, bool vertical)
        {
            g.DrawLine(glow, a, b);
            g.DrawLine(pen, a, b);
            // Grip in the middle of the divider: a pill that says "drag me".
            var mid = new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            float len = Math.Min(F(40), Math.Max(F(16), (vertical ? b.Y - a.Y : b.X - a.X) / 3)), th = F(8);
            var gr = vertical ? new RectangleF(mid.X - th / 2, mid.Y - len / 2, th, len) : new RectangleF(mid.X - len / 2, mid.Y - th / 2, len, th);
            using var gp = Theme.Round(gr, th / 2);
            using (var gb = new SolidBrush(Color.White)) g.FillPath(gb, gp);
            using (var ge = new Pen(Theme.Accent, F(1.5))) g.DrawPath(ge, gp);
        }
        float inset = F(Theme.EditorZoneInset);
        if (v != null) Handle(new PointF(PX(v.Pos), PY(v.Lo) + inset * 2), new PointF(PX(v.Pos), PY(v.Hi) - inset * 2), true);
        if (h != null) Handle(new PointF(PX(h.Lo) + inset * 2, PY(h.Pos)), new PointF(PX(h.Hi) - inset * 2, PY(h.Pos)), false);
        if (_drag == DragKind.Divider)
        {
            var parts = new List<string>();
            if (v != null) parts.Add($"x {PX(v.Pos)} px · {v.Pos * 100:0.#}%");
            if (h != null) parts.Add($"y {PY(h.Pos)} px · {h.Pos * 100:0.#}%");
            CursorLabel(g, string.Join("    ", parts));
        }
    }

    void PaintPreview(Graphics g)
    {
        if (_drag == DragKind.Draw && _drawing)
        {
            var (l, t, r, b) = _drawRect;
            var rc = RectangleF.FromLTRB(PX(l), PY(t), PX(r), PY(b));
            bool ok = _m.BigEnough(l, t, r, b);
            var mc = ok ? Theme.ModeColor(_m.NewMode) : Theme.Danger;
            using var path = Theme.Round(RectangleF.Inflate(rc, -F(Theme.EditorZoneInset), -F(Theme.EditorZoneInset)), F(Theme.RadiusZone * 0.8));
            using (var fill = new SolidBrush(Theme.A(mc, 0.28))) g.FillPath(fill, path);
            using (var pen = new Pen(mc, F(2)) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
            CursorLabel(g, $"{(int)rc.Width} × {(int)rc.Height} px   at {(int)rc.X}, {(int)rc.Y}");
            return;
        }
        if (_drag == DragKind.Move && _dragZone >= 0)
        {
            var rc = ZoneRect(_m.Zones[_dragZone]);
            CursorLabel(g, $"{rc.Width} × {rc.Height} px   at {rc.X}, {rc.Y}");
            return;
        }
        bool splitting = _drag == DragKind.Split || (_drag == DragKind.None && _tool == Tool.Split && _hoverZone >= 0 && _hv == null && _hh == null && _hoverChip < 0 && !InUi(_mouse) && !_openPanel);
        if (!splitting) return;
        int zi = _drag == DragKind.Split ? _dragZone : _hoverZone;
        if (zi < 0 || zi >= _m.Zones.Count) return;
        var z = _m.Zones[zi];
        bool vert = SplitVertical(zi);
        double pos = _m.SnapSplit(zi, vert, vert ? FX(_mouse.X) : FY(_mouse.Y), Magnet);
        bool can = _m.CanSplit(zi, vert, pos);
        float inset = F(Theme.EditorZoneInset) * 2;
        PointF pa = vert ? new PointF(PX(pos), PY(z.T) + inset) : new PointF(PX(z.L) + inset, PY(pos));
        PointF pb = vert ? new PointF(PX(pos), PY(z.B) - inset) : new PointF(PX(z.R) - inset, PY(pos));
        var lineColor = can ? Color.White : Theme.Danger;
        using (var glow = new Pen(Theme.A(can ? Theme.Accent : Theme.Danger, 0.35), F(8)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(glow, pa, pb);
        using (var p = new Pen(lineColor, F(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, pa, pb);
        // Size of each part, on each side of the cut, level with the cursor.
        int first = vert ? PX(pos) - PX(z.L) : PY(pos) - PY(z.T);
        int second = vert ? PX(z.R) - PX(pos) : PY(z.B) - PY(pos);
        float off = F(16);
        if (vert)
        {
            SizePill(g, $"{first} px", new PointF(PX(pos) - off, _mouse.Y), true);
            SizePill(g, $"{second} px", new PointF(PX(pos) + off, _mouse.Y), false);
        }
        else
        {
            SizePillV(g, $"{first} px", new PointF(_mouse.X, PY(pos) - off), true);
            SizePillV(g, $"{second} px", new PointF(_mouse.X, PY(pos) + off), false);
        }
        CursorLabel(g, vert ? "Click to cut   ·   Shift: cut across" : "Click to cut   ·   Shift: cut down", below: true);
    }

    void SizePill(Graphics g, string text, PointF anchor, bool rightAligned)
    {
        var sz = TextRenderer.MeasureText(text, _fSmall);
        var r = new Rectangle(0, 0, sz.Width + S(14), sz.Height + S(8));
        r.X = rightAligned ? (int)anchor.X - r.Width : (int)anchor.X;
        r.Y = (int)anchor.Y - r.Height / 2;
        PillBox(g, r, text);
    }

    void SizePillV(Graphics g, string text, PointF anchor, bool above)
    {
        var sz = TextRenderer.MeasureText(text, _fSmall);
        var r = new Rectangle(0, 0, sz.Width + S(14), sz.Height + S(8));
        r.X = (int)anchor.X - r.Width / 2;
        r.Y = above ? (int)anchor.Y - r.Height : (int)anchor.Y;
        PillBox(g, r, text);
    }

    void PillBox(Graphics g, Rectangle r, string text, Color? textColor = null)
    {
        var back = Theme.Dark ? Color.FromArgb(32, 33, 38) : Color.FromArgb(252, 252, 253);
        using (var p = Theme.Round(r, r.Height / 2f))
        {
            using (var b = new SolidBrush(back)) g.FillPath(b, p);
            using (var e = new Pen(Theme.Dark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0), 1)) g.DrawPath(e, p);
        }
        TextRenderer.DrawText(g, text, _fSmall, r, textColor ?? Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    void CursorLabel(Graphics g, string text, bool below = false)
    {
        var sz = TextRenderer.MeasureText(text, _fSmall);
        var r = new Rectangle(_mouse.X + S(20), _mouse.Y + S(20), sz.Width + S(16), sz.Height + S(8));
        if (below) r.Y = _mouse.Y + S(28);
        if (r.Right > ClientSize.Width) r.X = _mouse.X - S(20) - r.Width;
        if (r.Bottom > ClientSize.Height) r.Y = _mouse.Y - S(20) - r.Height;
        PillBox(g, r, text);
    }

    Color BarBack => Theme.Dark ? Color.FromArgb(34, 35, 40) : Color.FromArgb(246, 246, 248);

    Surface? _barCache;
    string _barKey = "";

    /// <summary>The toolbar changes rarely: paint it into a cache and copy it in each frame.</summary>
    void PaintBarCached(Graphics g)
    {
        float radius = F(Theme.RadiusPanel);
        var bar = new RectangleF(_bar.X, _bar.Y, _bar.Width, _bar.Height);
        Glass.Shadow(g, bar, radius, F(Theme.ShadowSize), F(Theme.ShadowOffsetY));
        var area = Rectangle.Intersect(_bar, ClientRectangle);
        if (area.IsEmpty || _scene == null) return;
        string key = string.Join(";", _items.Select(i => $"{i.Id}|{i.Text}|{i.On}|{i.Enabled}|{i.R}")) + $"|{_hoverItem}|{_naming}|{_deleteArmedUntil != default}|{_underBar}|{_bar}|{Theme.Version}";
        if (_barCache == null || _barCache.Width != _scene.Width || _barCache.Height != _scene.Height || key != _barKey)
        {
            if (_barCache == null || _barCache.Width != _scene.Width || _barCache.Height != _scene.Height) { _barCache?.Dispose(); _barCache = new Surface(_scene.Width, _scene.Height); }
            Gdi.BitBlt(_barCache.Dc, area.X, area.Y, area.Width, area.Height, _scene.Dc, area.X, area.Y, 0x00CC0020);
            // A Graphics on the DIB's own DC: text drawing then writes in place instead of copying the whole bitmap.
            using (var bg = Graphics.FromHdc(_barCache.Dc))
            {
                bg.SmoothingMode = SmoothingMode.AntiAlias;
                bg.PixelOffsetMode = PixelOffsetMode.Half;
                bg.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                PaintBar(bg, shadow: false);
            }
            _barKey = key;
        }
        IntPtr hdc = g.GetHdc();
        try { Gdi.BitBlt(hdc, area.X, area.Y, area.Width, area.Height, _barCache.Dc, area.X, area.Y, 0x00CC0020); }
        finally { g.ReleaseHdc(hdc); }
    }

    void PaintBar(Graphics g, bool shadow = true)
    {
        float radius = F(Theme.RadiusPanel);
        var bar = new RectangleF(_bar.X, _bar.Y, _bar.Width, _bar.Height);
        if (shadow) Glass.Shadow(g, bar, radius, F(Theme.ShadowSize), F(Theme.ShadowOffsetY));
        Glass.Frosted(g, bar, radius, Theme.A(BarBack, Theme.Glass ? 0.86 : 1), (float)_scale);
        PaintItems(g, _items);
    }

    void PaintItems(Graphics g, List<Item> items)
    {
        var back = BarBack;
        foreach (var it in items)
        {
            if (it.R == Rectangle.Empty) continue;
            var r = new RectangleF(it.R.X, it.R.Y, it.R.Width, it.R.Height);
            switch (it.Kind)
            {
                case Kind.Sep:
                    using (var sp = new Pen(Theme.Divider, 1)) g.DrawLine(sp, r.X + r.Width / 2, r.Y + F(7), r.X + r.Width / 2, r.Bottom - F(7));
                    continue;
                case Kind.Label:
                    TextRenderer.DrawText(g, it.Text, _fSmall, it.R, Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    continue;
            }
            bool hot = it.Id == _hoverItem && it.Enabled;
            bool armed = it.Id == "delete" && _deleteArmedUntil != default;
            Color fill, fore, glyphColor, edge = Color.Empty;
            if (it.Kind == Kind.Primary)
            {
                fill = hot ? Theme.Mix(Theme.Accent, Color.White, 0.12) : Theme.Accent;
                fore = glyphColor = Theme.OnAccent;
            }
            else if (armed)
            {
                fill = Theme.Danger; fore = glyphColor = Color.White;
            }
            else if (it.Kind == Kind.Field)
            {
                fill = Theme.Field; fore = glyphColor = Theme.Text;
                edge = it.On ? Theme.Accent : hot ? Theme.A(Theme.Text, 0.35) : Theme.A(Theme.Text, 0.16);
            }
            else if (it.On)
            {
                fill = Theme.A(Theme.Accent, hot ? 0.30 : 0.22);
                edge = Theme.A(Theme.Accent, 0.6);
                fore = Theme.Text; glyphColor = Theme.Accent;
            }
            else
            {
                fill = hot ? Theme.ControlHot : Theme.Control;
                fore = Theme.Text; glyphColor = Theme.Text;
            }
            if (!it.Enabled) { fore = glyphColor = Theme.Faint; fill = Theme.ControlPressed; }
            using (var p = Theme.Round(r, F(Theme.RadiusControl)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                if (edge != Color.Empty) using (var e = new Pen(edge, it.Kind == Kind.Field && it.On ? F(1.5) : F(1))) g.DrawPath(e, p);
            }
            var opaque = Theme.Over(fill, back);
            if (it.Kind == Kind.Field)
            {
                var tr = Rectangle.Inflate(it.R, -S(10), 0);
                string text = it.Text.Length == 0 && !it.On ? "Name this layout" : it.Text;
                var color = it.Text.Length == 0 && !it.On ? Theme.Faint : fore;
                TextRenderer.DrawText(g, text, _fMed, tr, color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (it.On && _caretTick % 10 < 5)
                {
                    int tw = Math.Min(tr.Width, TextRenderer.MeasureText(it.Text, _fMed, Size.Empty, TextFormatFlags.NoPadding).Width + S(3));
                    using var cp = new Pen(Theme.Accent, F(1.5));
                    g.DrawLine(cp, tr.X + tw, it.R.Y + S(8), tr.X + tw, it.R.Bottom - S(8));
                }
                continue;
            }
            int textW = it.Text.Length > 0 ? TextRenderer.MeasureText(it.Text, _fMed, Size.Empty, TextFormatFlags.NoPadding).Width : 0;
            int keyW = it.Key.Length > 0 ? TextRenderer.MeasureText(it.Key, _fSmall, Size.Empty, TextFormatFlags.NoPadding).Width + S(10) : 0;
            int glyphW = it.Glyph.Length > 0 ? S(16) : 0;
            int contentW = glyphW + (glyphW > 0 && textW > 0 ? S(7) : 0) + textW + keyW + (it.Dot ? S(12) : 0);
            float x = r.X + (r.Width - contentW) / 2;
            if (glyphW > 0)
            {
                Icons.Glyph(g, it.Glyph, new RectangleF(x, r.Y + (r.Height - glyphW) / 2, glyphW, glyphW), glyphColor, F(1.5));
                x += glyphW + (textW > 0 ? S(7) : 0);
            }
            if (textW > 0)
            {
                TextRenderer.DrawText(g, it.Text, _fMed, new Point((int)x, it.R.Y + (it.R.Height - TextRenderer.MeasureText("Ag", _fMed, Size.Empty, TextFormatFlags.NoPadding).Height) / 2),
                    fore, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                x += textW;
            }
            if (it.Dot)
            {
                // Unsaved changes: a small dot after the label.
                float d = F(6);
                using var dot = new SolidBrush(fore);
                g.FillEllipse(dot, x + S(7), r.Y + (r.Height - d) / 2, d, d);
            }
            if (keyW > 0)
            {
                var kr = new Rectangle((int)x + S(6), it.R.Y + (it.R.Height - S(18)) / 2, keyW - S(6), S(18));
                using (var kp = Theme.Round(kr, F(4)))
                using (var kb = new Pen(Theme.A(fore, 0.3), 1))
                    g.DrawPath(kb, kp);
                TextRenderer.DrawText(g, it.Key, _fSmall, kr, Theme.Mix(fore, opaque, 0.35), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }

    void PaintBottom(Graphics g)
    {
        float radius = F(Theme.RadiusPanel);
        var r = new RectangleF(_bottom.X, _bottom.Y, _bottom.Width, _bottom.Height);
        Glass.Shadow(g, r, radius, F(Theme.ShadowSize * 0.8), F(Theme.ShadowOffsetY * 0.6));
        Glass.Frosted(g, r, radius, Theme.A(BarBack, Theme.Glass ? 0.9 : 1), (float)_scale);
        PaintItems(g, _bottomItems);
        if (_helpOn) PaintHelp(g);
    }

    static readonly (string keys, string what)[] Shortcuts =
    {
        ("Click", "Split a zone (Shift: the other way)"),
        ("Drag an edge", "Resize (Ctrl: lined-up edges too)"),
        ("Double-click an edge", "Merge two zones"),
        ("Drag in empty space", "Draw a zone"),
        ("Right-click", "Delete, duplicate or change a zone"),
        ("Alt", "Hold to place without snapping"),
        ("S  D  M", "Split, Draw, Move tool"),
        ("1  2  3", "Zone under the cursor: %, px, grid"),
        ("Del", "Delete the zone under the cursor"),
        ("Ctrl+Z  Ctrl+Y", "Undo, Redo"),
        ("Ctrl+S", "Save (Ctrl+Shift+S: Save copy)"),
        ("O", "Open a saved layout"),
        ("Esc", "Close this, then the editor"),
    };

    Rectangle HelpRect()
    {
        int rowH = S(30), half = (Shortcuts.Length + 1) / 2;
        int w = Math.Min(ClientSize.Width - S(48), S(860));
        int h = S(58) + half * rowH + S(40);
        return new Rectangle((ClientSize.Width - w) / 2, _bottom.Y - h - S(12), w, h);
    }

    void PaintHelp(Graphics g)
    {
        var r = _helpRect;
        float radius = F(Theme.RadiusPanel);
        var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
        Glass.Shadow(g, rf, radius, F(Theme.ShadowSize), F(Theme.ShadowOffsetY));
        Glass.Frosted(g, rf, radius, Theme.A(BarBack, Theme.Glass ? 0.95 : 1), (float)_scale);
        TextRenderer.DrawText(g, "Shortcuts", _fBig, new Point(r.X + S(22), r.Y + S(16)), Theme.Text, TextFormatFlags.NoPrefix);
        var hint = "? or Esc closes";
        var hs = TextRenderer.MeasureText(hint, _fSmall);
        TextRenderer.DrawText(g, hint, _fSmall, new Point(r.Right - S(22) - hs.Width, r.Y + S(20)), Theme.Faint, TextFormatFlags.NoPrefix);
        int rowH = S(30), half = (Shortcuts.Length + 1) / 2;
        int colW = (r.Width - S(44)) / 2, keyW = S(170);
        for (int i = 0; i < Shortcuts.Length; i++)
        {
            int col = i / half, row = i % half;
            int x = r.X + S(22) + col * colW, y = r.Y + S(56) + row * rowH;
            var (keys, what) = Shortcuts[i];
            var ks = TextRenderer.MeasureText(keys, _fSmall, Size.Empty, TextFormatFlags.NoPadding);
            var kr = new Rectangle(x, y + (rowH - S(22)) / 2, Math.Min(keyW - S(12), ks.Width + S(16)), S(22));
            using (var kp = Theme.Round(kr, F(5)))
            {
                using (var kb = new SolidBrush(Theme.Control)) g.FillPath(kb, kp);
                using (var ke = new Pen(Theme.A(Theme.Text, 0.18), 1)) g.DrawPath(ke, kp);
            }
            TextRenderer.DrawText(g, keys, _fSmall, kr, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, what, _fSmall, new Rectangle(x + keyW, y, colW - keyW - S(8), rowH), Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
        string saved = _lib.Find(_m.LayoutId) == null ? "not saved yet" : _m.Dirty ? "unsaved changes" : "saved";
        string info = $"Monitor {_mon.Number} of {_monitors.Count}: {_mon.Label}   ·   set \"{_setName}\"   ·   {_m.Zones.Count} zones, {saved}";
        TextRenderer.DrawText(g, info, _fSmall, new Rectangle(r.X + S(22), r.Bottom - S(34), r.Width - S(44), S(24)), Theme.Faint,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Hint for the toolbar item under the cursor, after a short pause.</summary>
    void PaintTip(Graphics g)
    {
        var it = _items.Concat(_bottomItems).FirstOrDefault(i => i.Id == _tipFor);
        if (it == null) return;
        string text = Hint(it.Id);
        if (text.Length == 0) return;
        var sz = TextRenderer.MeasureText(text, _fSmall);
        var r = new Rectangle(0, 0, sz.Width + S(18), sz.Height + S(10));
        r.X = Math.Clamp(it.R.X + it.R.Width / 2 - r.Width / 2, S(8), ClientSize.Width - r.Width - S(8));
        bool below = _bar.Contains(it.R.Location);
        r.Y = below ? _bar.Bottom + S(8) : _bottom.Y - r.Height - S(8);
        Glass.Shadow(g, new RectangleF(r.X, r.Y, r.Width, r.Height), r.Height / 2f, F(12), F(3));
        PillBox(g, r, text);
    }

    int FooterTop() => (_helpOn && !_helpRect.IsEmpty ? _helpRect.Y : _bottom.Y > 0 ? _bottom.Y : ClientSize.Height);

    void PaintOpenPanel(Graphics g)
    {
        float radius = F(14);
        var pr = new RectangleF(_panel.X, _panel.Y, _panel.Width, _panel.Height);
        Glass.Shadow(g, pr, radius, F(30), F(10));
        Glass.Frosted(g, pr, radius, Theme.A(BarBack, Theme.Glass ? 0.92 : 1), (float)_scale);
        var back = BarBack;
        TextRenderer.DrawText(g, "Start from", _fBig, new Point(_panel.X + S(24), _panel.Y + S(18)), Theme.Text);
        TextRenderer.DrawText(g, "Click a layout to edit it. Drag one to change its place in this monitor's picker. Esc or a click outside closes this.",
            _fSmall, new Point(_panel.X + S(24), _panel.Y + S(44)), Theme.Dim);
        int W = ClientSize.Width, H = ClientSize.Height;
        var blank = new ZoneLayout { Name = "Blank: no zones" };
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        int ci = -1;
        foreach (var (cell, layout) in _openCells)
        {
            ci++;
            if (_cardDragging && ci == _cardDown)
            {
                // The slot the card was lifted from: an empty dashed outline.
                using var dash = new Pen(Theme.A(Theme.Dim, 0.5), F(1.5)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                g.DrawRectangle(dash, cell);
                continue;
            }
            bool hot = !_cardDragging && cell.Contains(_mouse);
            bool current = layout != null && layout.Id == _m.LayoutId;
            Thumb.Draw(g, layout ?? blank, cell, W, H, _scale, -1, hot ? 1 : current ? 0.6f : 0, back);
            string name = layout == null ? blank.Name : layout.Name;
            if (layout != null && !_lib.IsShown(layout.Id, _mon.Key)) name += " (other monitor)";
            Thumb.Label(g, name, false, new RectangleF(cell.X - S(6), cell.Bottom + S(4), cell.Width + S(12), S(20)), _fSmall, hot ? Theme.Text : Theme.Dim, _scale);
        }
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        if (_cardDragging && _cardDown >= 0 && _openCells[_cardDown].layout is { } lifted)
        {
            if (_cardTarget >= 0)
            {
                // Drop marker: a bar at the slot the dragged card would land in.
                int slot = _cardTarget;
                slot = Math.Min(slot, _openStrip + 1);
                Rectangle t = slot <= _openStrip ? _openCells[slot].cell : _openCells[_openStrip].cell;
                int bx = slot <= _openStrip ? t.X - S(9) : t.Right + S(6);
                var bar = new RectangleF(bx, t.Y - S(4), S(4), t.Height + S(8));
                using var path = Theme.Round(bar, bar.Width / 2f);
                using var fill = new SolidBrush(Theme.Accent);
                g.FillPath(fill, path);
            }
            var c0 = _openCells[_cardDown].cell;
            int grow = S(6);
            var lr = new Rectangle(_mouse.X - _cardGrab.X - grow, _mouse.Y - _cardGrab.Y - grow, c0.Width + 2 * grow, c0.Height + 2 * grow);
            Glass.Shadow(g, new RectangleF(lr.X, lr.Y, lr.Width, lr.Height), F(6), F(22), F(8));
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Thumb.Draw(g, lifted, lr, W, H, _scale, -1, 1f, back);
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        }
    }

    void PaintToast(Graphics g)
    {
        int icon = S(18);
        var sz = TextRenderer.MeasureText(_toast, _fMed);
        int w = Math.Min(ClientSize.Width - S(48), sz.Width + icon + S(48));
        int h = sz.Height + S(20);
        int bottom = FooterTop() - S(14);
        float t = _toastT;
        int lift = (int)Math.Round((1 - t) * S(12));
        var r = new Rectangle((ClientSize.Width - w) / 2, bottom - h + lift, w, h);
        _toastRect = r;
        var back = Theme.Dark ? Color.FromArgb(44, 45, 52) : Color.FromArgb(255, 255, 255);
        var screen = Theme.Dark ? Color.FromArgb(24, 25, 30) : Color.FromArgb(234, 236, 240);
        var b0 = Theme.Mix(screen, back, t);
        var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
        if (t > 0.5f) Glass.Shadow(g, rf, h / 2f, F(18), F(5));
        using (var p = Theme.Round(rf, h / 2f))
        {
            using (var b = new SolidBrush(b0)) g.FillPath(b, p);
            using (var e = new Pen(Theme.A(_toastWarn ? Theme.Danger : Theme.Accent, 0.55 * t), F(1))) g.DrawPath(e, p);
        }
        var accent = _toastWarn ? Theme.Danger : Theme.Accent;
        Icons.Glyph(g, _toastWarn ? "warn" : "info", new RectangleF(r.X + S(16), r.Y + (h - icon) / 2f, icon, icon), Theme.Mix(b0, accent, t), F(1.6));
        TextRenderer.DrawText(g, _toast, _fMed, new Rectangle(r.X + S(16) + icon + S(10), r.Y, r.Width - icon - S(42), r.Height), Theme.Mix(b0, Theme.Text, t),
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Right-click menu in the editor's look: rounded highlight, theme colours.</summary>
sealed class GlassMenuRenderer : ToolStripProfessionalRenderer
{
    public GlassMenuRenderer() : base(new MenuColors()) { RoundedEdges = true; }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var p = Theme.Round(r, 4);
        using var b = new SolidBrush(Theme.Over(Theme.ControlHot, Theme.Panel));
        g.FillPath(b, p);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Faint;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        var r = e.ImageRectangle;
        Icons.Glyph(g, "check", new RectangleF(r.X, r.Y, r.Width, r.Height), Theme.Accent, 1.6f);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Over(Theme.Divider, Theme.Panel));
        e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Panel;
        public override Color ImageMarginGradientBegin => Theme.Panel;
        public override Color ImageMarginGradientMiddle => Theme.Panel;
        public override Color ImageMarginGradientEnd => Theme.Panel;
        public override Color MenuBorder => Theme.Over(Theme.PanelBorder, Theme.Panel);
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Theme.Over(Theme.ControlHot, Theme.Panel);
        public override Color CheckBackground => Theme.Panel;
        public override Color CheckSelectedBackground => Theme.Panel;
        public override Color CheckPressedBackground => Theme.Panel;
        public override Color SeparatorDark => Theme.Over(Theme.Divider, Theme.Panel);
        public override Color SeparatorLight => Theme.Panel;
    }
}

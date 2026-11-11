namespace RegionControl;

/// <summary>Tray icon, tray menu, Quit and the "already running" balloon.</summary>
public partial class DragWatcher
{
    NotifyIcon CreateTray()
    {
        var tray = new NotifyIcon { Icon = Icons.TrayIcon(), Text = "Snap Layouts", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        tray.ContextMenuStrip!.Opening += (_, e) => { BuildTrayMenu(); e.Cancel = false; };
        tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenEditor(null); };
        return tray;
    }

    void BuildTrayMenu()
    {
        RefreshMonitors();
        FillTrayMenu(_tray.ContextMenuStrip!, _monitors, Startup.IsEnabled, this);
    }

    /// <summary>
    /// Tray menu: short labels, about 200 px wide at 100 %. Longer help sits in each item's tooltip.
    /// <paramref name="app"/> is null in the render check: the items then do nothing.
    /// </summary>
    internal static void FillTrayMenu(ContextMenuStrip menu, IReadOnlyList<MonitorInfo> monitors, bool autostart, DragWatcher? app)
    {
        menu.Renderer = new GlassMenuRenderer();
        menu.ShowItemToolTips = true;
        menu.Items.Clear();
        ToolStripMenuItem Item(string text, string tip, Action? click)
        {
            var it = new ToolStripMenuItem(text) { ToolTipText = tip };
            if (click != null) it.Click += (_, _) => click();
            menu.Items.Add(it);
            return it;
        }
        if (monitors.Count <= 1)
            Item("Edit layouts", "Open the layout editor on this monitor.", () => app?.OpenEditor(monitors.FirstOrDefault()));
        else
            foreach (var m in monitors)
            {
                var mm = m;
                Item($"Edit monitor {m.Number}", $"Open the layout editor on {m.Label}.", () => app?.OpenEditor(mm));
            }
        menu.Items.Add(new ToolStripSeparator());
        Item("Layouts folder", "Open the folder with layouts.json.", () =>
            System.Diagnostics.Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(app?.Lib.Path)}\""));
        Item("Reload layouts", "Read layouts.json again after an edit by hand.", () =>
        {
            if (app == null) return;
            app.Lib.Reload(); Log(app.Lib.LoadNote ?? "reloaded"); app.RefreshMonitors(true);
        });
        Item("Logs folder", "Open the folder with the daily log files.", () =>
            System.Diagnostics.Process.Start("explorer.exe", $"\"{LogDir}\""));
        menu.Items.Add(new ToolStripSeparator());
        var auto = Item("Start with Windows", "Start Snap Layouts when you sign in.", null);
        auto.Checked = autostart;
        auto.Click += (_, _) => app?.ToggleAutostart(!auto.Checked);
        menu.Items.Add(new ToolStripSeparator());
        Item("About", $"Snap Layouts {AppVersion}", () => app?.ShowAbout());
        Item("Quit", "Quit Snap Layouts.", () => app?.Quit());
    }

    void ShowAbout() =>
        _tray.ShowBalloonTip(4000, $"Snap Layouts {AppVersion}", "Drag a window to the top edge to pick a zone.", ToolTipIcon.Info);

    /// <summary>Version from -p:Version=x.y.z at build time (InformationalVersion, without the +commit suffix).</summary>
    static string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "";
            int plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    void ToggleAutostart(bool on)
    {
        string? err = Startup.Set(on);
        if (err != null)
        {
            Log($"start with Windows: failed to {(on ? "enable" : "disable")}: {err}");
            _tray.ShowBalloonTip(4000, "Snap Layouts", "Start with Windows failed. See the log.", ToolTipIcon.Warning);
            return;
        }
        Log($"start with Windows: {(on ? "on" : "off")}");
    }

    /// <summary>Called when a second copy starts and exits: tell the user the app is already here.</summary>
    public void ShowAlreadyRunning() =>
        _tray.ShowBalloonTip(3000, "Snap Layouts", "Already running. Right-click the tray icon.", ToolTipIcon.Info);

    /// <summary>Clean exit: close the editor, then stop the message loop. Dispose removes the hook, overlays and tray icon.</summary>
    public void Quit()
    {
        Log("quit requested from the tray");
        if (_editor != null && !_editor.IsDisposed) _editor.Close();
        ExitThread();
    }
}

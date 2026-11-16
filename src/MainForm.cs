// Copyright (c) 2026 Adel Naim. Licensed under the MIT License (see LICENSE).
using System.Drawing;
using Microsoft.Win32;

namespace TSP100Bridge;

public sealed class MainForm : Form
{
    readonly NotifyIcon _tray;
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    readonly Label _status = new() { AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Padding = new Padding(0, 6, 0, 6) };

    readonly ComboBox _conn = Combo<ConnectionType>();
    readonly TextBox _host = new() { Width = 140, PlaceholderText = "e.g. 192.168.1.50" };
    readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 70 };
    readonly TextBox _com = new() { Width = 70 };
    readonly NumericUpDown _baud = new() { Minimum = 1200, Maximum = 921600, Width = 80 };

    readonly ComboBox _paper = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    readonly ComboBox _cut = Combo<CutMode>();
    readonly ComboBox _dither = Combo<DitherMode>();
    readonly NumericUpDown _thr = new() { Minimum = 1, Maximum = 254, Width = 60 };
    readonly CheckBox _trim = new() { Text = "Trim blank paper", AutoSize = true };
    readonly CheckBox _actual = new() { Text = "Print at actual size (works with A4/Letter pages)", AutoSize = true };
    readonly CheckBox _drawer = new() { Text = "Open cash drawer on every job", AutoSize = true };

    readonly CheckBox _watch = new() { Text = "Print jobs from the \"Star TSP100 (ARM)\" Windows printer", AutoSize = true };
    readonly CheckBox _raw = new() { Text = "Accept raw Star jobs on TCP port", AutoSize = true };
    readonly NumericUpDown _rawPort = new() { Minimum = 1, Maximum = 65535, Width = 70 };
    readonly CheckBox _autostart = new() { Text = "Start with Windows (in tray)", AutoSize = true };

    SpoolWatcher? _watcher;
    RawForwarder? _forwarder;
    readonly bool _startHidden;
    bool _exiting;

    public MainForm(bool startHidden)
    {
        _startHidden = startHidden;
        Text = "TSP100 Bridge — Star TSP100 for Windows on ARM  ·  © 2026 Adel Naim (MIT)";
        Icon = MakeIcon();
        ClientSize = new Size(760, 720);
        MinimumSize = new Size(700, 600);
        Font = new Font("Segoe UI", 9.5f);

        _paper.Items.AddRange(new object[] { "80 mm paper (576 dots)", "58 mm paper (406 dots)" });

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        root.Controls.Add(_status);

        root.Controls.Add(Group("Connection",
            Row(Lbl("Connect via"), _conn, Btn("Detect printer", (_, _) => Detect())),
            Row(Lbl("Network IP"), _host, Lbl("port"), _port),
            Row(Lbl("Bluetooth/serial"), _com, Lbl("baud"), _baud)));

        root.Controls.Add(Group("Output",
            Row(Lbl("Paper"), _paper, Lbl("After job"), _cut),
            Row(Lbl("Images"), _dither, Lbl("darkness threshold"), _thr, _trim),
            Row(_actual, _drawer)));

        root.Controls.Add(Group("Integration",
            Row(_watch),
            Row(_raw, _rawPort),
            Row(_autostart)));

        root.Controls.Add(Row(
            Btn("Save settings", (_, _) => { SaveFromUi(); Log.Write("Settings saved."); }),
            Btn("Print test page", async (_, _) => await Guard(async () => { SaveFromUi(); using var b = PrintService.BuildTestPage(); await PrintService.PrintBitmapsAsync(new[] { b }, "test page"); })),
            Btn("Open drawer", async (_, _) => await Guard(() => PrintService.SendRawAsync(StarRaster.DrawerKick(), "drawer kick"))),
            Btn("Print a file…", async (_, _) => await PrintFileDialog()),
            Btn("Add Windows printer", (_, _) => InstallPrinter(true)),
            Btn("Remove Windows printer", (_, _) => InstallPrinter(false)),
            Btn("Open data folder", (_, _) => System.Diagnostics.Process.Start("explorer.exe", Settings.DataDir))));

        root.Controls.Add(_log);

        _tray = new NotifyIcon { Icon = Icon, Text = "TSP100 Bridge", Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open TSP100 Bridge", null, (_, _) => ShowWindow());
        menu.Items.Add("Print test page", null, async (_, _) => await Guard(async () => { using var b = PrintService.BuildTestPage(); await PrintService.PrintBitmapsAsync(new[] { b }, "test page"); }));
        menu.Items.Add("Open cash drawer", null, async (_, _) => await Guard(() => PrintService.SendRawAsync(StarRaster.DrawerKick(), "drawer kick")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();

        Log.Line += line => BeginInvokeSafe(() => { _log.AppendText(line + Environment.NewLine); });
        SpoolWatcher.Notify += (title, msg) => BeginInvokeSafe(() => _tray.ShowBalloonTip(5000, title, msg, ToolTipIcon.Error));

        LoadToUi(Settings.Load());
        ApplyServices();
        Detect();
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && !IsHandleCreated) { CreateHandle(); value = false; }
        base.SetVisibleCore(value);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(3000, "TSP100 Bridge", "Still running in the tray so print jobs keep working.", ToolTipIcon.Info);
            return;
        }
        _watcher?.Dispose();
        _forwarder?.Dispose();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    void BeginInvokeSafe(Action a)
    {
        if (IsDisposed) return;
        if (!IsHandleCreated) return;
        try { BeginInvoke(a); } catch { }
    }

    // ---- settings <-> UI --------------------------------------------------------
    void LoadToUi(Settings s)
    {
        _conn.SelectedItem = s.Connection;
        _host.Text = s.NetworkHost; _port.Value = s.NetworkPort;
        _com.Text = s.SerialPort; _baud.Value = s.SerialBaud;
        _paper.SelectedIndex = s.PrintWidthDots <= 450 ? 1 : 0;
        _cut.SelectedItem = s.Cut; _dither.SelectedItem = s.Dither; _thr.Value = s.Threshold;
        _trim.Checked = s.TrimWhitespace; _actual.Checked = s.ActualSize; _drawer.Checked = s.OpenDrawerOnJob;
        _watch.Checked = s.WatchSpool; _raw.Checked = s.RawForwarderEnabled; _rawPort.Value = s.RawForwarderPort;
        _autostart.Checked = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("TSP100Bridge") != null;
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    void SaveFromUi()
    {
        var s = Settings.Load();
        s.Connection = (ConnectionType)_conn.SelectedItem!;
        s.NetworkHost = _host.Text.Trim(); s.NetworkPort = (int)_port.Value;
        s.SerialPort = _com.Text.Trim(); s.SerialBaud = (int)_baud.Value;
        s.PrintWidthDots = _paper.SelectedIndex == 1 ? 406 : 576;
        s.Cut = (CutMode)_cut.SelectedItem!; s.Dither = (DitherMode)_dither.SelectedItem!; s.Threshold = (int)_thr.Value;
        s.TrimWhitespace = _trim.Checked; s.ActualSize = _actual.Checked; s.OpenDrawerOnJob = _drawer.Checked;
        s.WatchSpool = _watch.Checked; s.RawForwarderEnabled = _raw.Checked; s.RawForwarderPort = (int)_rawPort.Value;
        try { s.Save(); } catch (Exception ex) { Log.Write("Could not save settings: " + ex.Message); }

        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (_autostart.Checked) k.SetValue("TSP100Bridge", $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue("TSP100Bridge", false);
        }
        ApplyServices();
    }

    void ApplyServices()
    {
        var s = Settings.Load();
        if (s.WatchSpool && _watcher == null) _watcher = new SpoolWatcher();
        if (!s.WatchSpool && _watcher != null) { _watcher.Dispose(); _watcher = null; }

        _forwarder?.Dispose(); _forwarder = null;
        if (s.RawForwarderEnabled)
        {
            try { _forwarder = new RawForwarder(s.RawForwarderPort, s.RawForwarderLocalOnly); }
            catch (Exception ex) { Log.Write($"Cannot listen on port {s.RawForwarderPort}: {ex.Message}"); }
        }
    }

    // ---- actions ------------------------------------------------------------------
    void Detect()
    {
        try
        {
            var found = DeviceFinder.FindAll();
            foreach (var d in found) Log.Write($"Found {d.Kind}: {d.Note}  [{d.Path}]");
            var usable = found.FirstOrDefault(d => d.Note is "Star device" or "WinUSB ready");
            var unbound = found.FirstOrDefault(d => d.Note == "no WinUSB driver bound");
            var s = Settings.Load();
            if (usable != null) SetStatus($"✔ Star printer connected ({usable.Kind})", Color.DarkGreen);
            else if (unbound != null) SetStatus("⚠ Star printer plugged in, but it needs the WinUSB driver — see README step 3", Color.DarkOrange);
            else if (s.Connection == ConnectionType.Network && s.NetworkHost.Length > 0) SetStatus($"Network printer: {s.NetworkHost}:{s.NetworkPort}", Color.DarkBlue);
            else if (s.Connection == ConnectionType.Serial) SetStatus($"Serial/Bluetooth printer: {s.SerialPort}", Color.DarkBlue);
            else SetStatus("✖ No Star USB printer found — check cable and power", Color.Firebrick);
        }
        catch (Exception ex) { SetStatus("Detection error: " + ex.Message, Color.Firebrick); }
    }

    void SetStatus(string t, Color c) { _status.Text = t; _status.ForeColor = c; }

    async Task PrintFileDialog()
    {
        using var dlg = new OpenFileDialog { Filter = "Printable files|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await Guard(() => PrintService.PrintFileAsync(dlg.FileName));
    }

    void InstallPrinter(bool install)
    {
        SaveFromUi();
        UseWaitCursor = true;
        string result = Installer.IsAdmin()
            ? (install ? Installer.Install() : Installer.Uninstall())
            : Installer.RunElevated(install ? "--install" : "--uninstall");
        UseWaitCursor = false;
        foreach (var l in result.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log.Write(l.TrimEnd());
        MessageBox.Show(this, result, install ? "Add Windows printer" : "Remove Windows printer",
            MessageBoxButtons.OK, result.Contains("ERROR") ? MessageBoxIcon.Error : MessageBoxIcon.Information);
    }

    async Task Guard(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex)
        {
            Log.Write("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "TSP100 Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- small UI helpers -------------------------------------------------------
    static ComboBox Combo<T>() where T : struct, Enum
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        foreach (var v in Enum.GetValues<T>()) c.Items.Add(v);
        return c;
    }

    static Label Lbl(string t) => new() { Text = t, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };

    static Button Btn(string t, EventHandler h)
    {
        var b = new Button { Text = t, AutoSize = true, Padding = new Padding(4, 2, 4, 2) };
        b.Click += h;
        return b;
    }

    static FlowLayoutPanel Row(params Control[] cs)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top, Margin = new Padding(0, 2, 0, 2) };
        p.Controls.AddRange(cs);
        return p;
    }

    static GroupBox Group(string title, params Control[] rows)
    {
        var inner = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        foreach (var r in rows) inner.Controls.Add(r);
        var g = new GroupBox { Text = title, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8) };
        g.Controls.Add(inner);
        return g;
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            g.FillRectangle(new SolidBrush(Color.FromArgb(40, 40, 48)), 3, 14, 26, 14);   // printer body
            g.FillRectangle(Brushes.White, 8, 2, 16, 18);                                  // receipt
            g.DrawRectangle(Pens.Gray, 8, 2, 16, 18);
            for (int y = 6; y < 18; y += 4) g.DrawLine(Pens.Black, 11, y, 21, y);
            g.FillEllipse(Brushes.LimeGreen, 23, 21, 4, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

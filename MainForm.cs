using QRCoder;

namespace PcDash;

public sealed class MainForm : Form
{
    static readonly Color Bg = Color.FromArgb(18, 20, 26);
    static readonly Color Fg = Color.FromArgb(230, 232, 238);
    static readonly Color Muted = Color.FromArgb(140, 146, 160);
    static readonly Color Accent = Color.FromArgb(217, 119, 87);

    readonly ServerState _state;
    readonly PictureBox _qr = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    readonly ComboBox _ips = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    readonly LinkLabel _url = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter };
    readonly Label _status = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Muted };
    readonly NotifyIcon _tray = new();
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 1000 };
    readonly CheckBox _autostart = new() { Text = "Start with Windows (quietly, in the tray)", AutoSize = true };
    ToolStripMenuItem? _autostartMenu;
    bool _exiting;
    bool _allowShow;

    public MainForm(ServerState state, bool startInTray = false)
    {
        _state = state;
        _allowShow = !startInTray;
        Text = "PC Dash";
        BackColor = Bg; ForeColor = Fg;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(420, 730);
        MinimumSize = new Size(360, 650);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = MakeIcon();

        var title = new Label { Text = "Scan with your phone", Font = new Font("Segoe UI Semibold", 15f), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Top, Height = 48 };
        var hint = new Label { Text = "Phone and PC must be on the same network.", ForeColor = Muted, AutoSize = false, TextAlign = ContentAlignment.TopCenter, Dock = DockStyle.Top, Height = 26 };

        var qrHost = new Panel { Dock = DockStyle.Top, Height = 330, Padding = new Padding(55, 6, 55, 6) };
        _qr.Dock = DockStyle.Fill;
        qrHost.Controls.Add(_qr);

        _url.Dock = DockStyle.Top; _url.Height = 30;
        _url.LinkColor = Accent; _url.ActiveLinkColor = Fg;
        _url.LinkClicked += (_, _) => OpenInBrowser();

        var ipLabel = new Label { Text = "Network address (pick another if the phone can't connect):", ForeColor = Muted, Dock = DockStyle.Top, Height = 24, Padding = new Padding(20, 4, 20, 0) };
        var ipHost = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(20, 2, 20, 2) };
        _ips.Dock = DockStyle.Fill; _ips.BackColor = Color.FromArgb(32, 35, 44); _ips.ForeColor = Fg;
        ipHost.Controls.Add(_ips);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(14, 8, 14, 0), WrapContents = false };
        buttons.Controls.Add(MakeButton("Open on this PC", OpenInBrowser));
        buttons.Controls.Add(MakeButton("Copy link", () => Clipboard.SetText(CurrentUrl())));
        buttons.Controls.Add(MakeButton("New key", RegenerateKey));

        var setupHost = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(20, 8, 20, 2) };
        var setupBtn = MakeButton("Set up deck && weather…", OpenSetup);
        setupBtn.AutoSize = false; setupBtn.Dock = DockStyle.Fill;
        setupBtn.BackColor = Accent; setupBtn.ForeColor = Color.White; setupBtn.FlatAppearance.BorderColor = Accent;
        setupHost.Controls.Add(setupBtn);

        _status.Dock = DockStyle.Top; _status.Height = 40;

        var autoHost = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(20, 4, 20, 0) };
        _autostart.ForeColor = Muted;
        _autostart.Checked = Autostart.IsEnabled;
        _autostart.CheckedChanged += (_, _) => SetAutostart(_autostart.Checked);
        autoHost.Controls.Add(_autostart);

        // Dock=Top stacks in reverse add order.
        Controls.AddRange([autoHost, _status, setupHost, buttons, ipHost, ipLabel, _url, qrHost, hint, title]);

        LoadIps();
        _ips.SelectedIndexChanged += (_, _) =>
        {
            if (_ips.SelectedItem is not LanAddresses.Candidate) return;
            _state.Settings.PreferredIp = SelectedIp();
            _state.Settings.Save();
            RefreshQr();
        };

        // Tray: closing the window hides it; the server keeps running until "Exit".
        _tray.Icon = Icon;
        _tray.Text = "PC Dash";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show QR code", null, (_, _) => ShowFromTray());
        menu.Items.Add("Open dashboard", null, (_, _) => OpenInBrowser());
        menu.Items.Add("Set up deck && weather…", null, (_, _) => OpenSetup());
        _autostartMenu = new ToolStripMenuItem("Start with Windows") { Checked = _autostart.Checked, CheckOnClick = true };
        _autostartMenu.CheckedChanged += (_, _) => SetAutostart(_autostartMenu.Checked);
        menu.Items.Add(_autostartMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
        _tray.ContextMenuStrip = menu;

        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        RefreshQr();
        UpdateStatus();
    }

    Button MakeButton(string text, Action onClick)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, Height = 34,
            BackColor = Color.FromArgb(32, 35, 44), ForeColor = Fg, Margin = new Padding(6, 0, 6, 0), Padding = new Padding(6, 0, 6, 0),
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(60, 64, 76);
        b.Click += (_, _) => onClick();
        return b;
    }

    void SetAutostart(bool on)
    {
        try { Autostart.Set(on); }
        catch (Exception ex) { MessageBox.Show("Couldn't change the startup setting:\n" + ex.Message, "PC Dash"); }
        bool actual = Autostart.IsEnabled;
        if (_autostart.Checked != actual) _autostart.Checked = actual;
        if (_autostartMenu != null && _autostartMenu.Checked != actual) _autostartMenu.Checked = actual;
    }

    /// <summary>(Re)reads network addresses; at boot the network may not be up yet when the app starts.</summary>
    void LoadIps()
    {
        var keep = (_ips.SelectedItem as LanAddresses.Candidate)?.Ip ?? _state.Settings.PreferredIp;
        _ips.BeginUpdate();
        _ips.Items.Clear();
        foreach (var c in LanAddresses.Get()) _ips.Items.Add(c);
        if (_ips.Items.Count == 0) _ips.Items.Add(new LanAddresses.Candidate("127.0.0.1", "loopback only", 0));
        int pref = _ips.Items.Cast<LanAddresses.Candidate>().ToList().FindIndex(c => c.Ip == keep);
        _ips.SelectedIndex = pref >= 0 ? pref : 0;
        _ips.EndUpdate();
        RefreshQr();
    }

    // Keeps the window hidden when launched with --tray, until the user opens it from the tray.
    protected override void SetVisibleCore(bool value)
    {
        if (!_allowShow)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    string SelectedIp() => (_ips.SelectedItem as LanAddresses.Candidate)?.Ip ?? "127.0.0.1";
    string CurrentUrl() => _state.UrlFor(SelectedIp());

    void RefreshQr()
    {
        var url = CurrentUrl();
        _url.Text = url.Replace("?k=" + _state.Settings.Key, "?k=" + _state.Settings.Key[..4] + "…");
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(12);
        var old = _qr.Image;
        _qr.Image = Image.FromStream(new MemoryStream(png));
        old?.Dispose();
    }

    void RegenerateKey()
    {
        var r = MessageBox.Show("Create a new access key? Phones using the old QR code will stop working until they scan again.", "PC Dash", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (r != DialogResult.OK) return;
        _state.Settings.Key = AppSettings.NewKey();
        _state.Settings.Save();
        RefreshQr();
    }

    void OpenInBrowser()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_state.UrlFor("localhost")) { UseShellExecute = true }); }
        catch { }
    }

    void OpenSetup()
    {
        try
        {
            var url = $"http://localhost:{_state.Port}/setup?k={_state.Settings.Key}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    void UpdateStatus()
    {
        var hit = _state.LastRemoteHit;
        if (hit is { } h && (DateTime.Now - h.time).TotalSeconds < 10)
        {
            _status.ForeColor = Color.FromArgb(110, 200, 140);
            _status.Text = $"● Phone connected ({h.ip})";
        }
        else
        {
            _status.ForeColor = Muted;
            _status.Text = $"Serving on port {_state.Port}, waiting for a phone…";
        }
    }

    void ShowFromTray()
    {
        _allowShow = true;
        LoadIps();
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(2500, "PC Dash is still running", "The dashboard stays available. Right-click the tray icon to exit.", ToolTipIcon.Info);
            return;
        }
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Accent);
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var bar = new SolidBrush(Color.White);
            g.FillRectangle(bar, 8, 17, 4, 8);
            g.FillRectangle(bar, 14, 11, 4, 14);
            g.FillRectangle(bar, 20, 7, 4, 18);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

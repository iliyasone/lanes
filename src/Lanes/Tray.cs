using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using static Lanes.Native;

namespace Lanes;

// The tray icon and its menu, drawn like a Windows 11 flyout.
sealed class Tray : IDisposable
{
    readonly NotifyIcon _icon = new();
    readonly Flyout _flyout;
    readonly Engine _engine;
    readonly Settings _settings;
    readonly Updater _updater;

    public static Tray? Instance;

    public void ShowMenu(Point near) => _flyout.ShowNear(near);

    public Tray(Engine engine, Settings settings, Updater updater)
    {
        Instance = this;
        _engine = engine;
        _settings = settings;
        _updater = updater;
        _flyout = new Flyout(this);
        _icon.Text = "Lanes";
        _icon.MouseUp += (_, e) => { if (e.Button is MouseButtons.Left or MouseButtons.Right) _flyout.ShowNearCursor(); };
        _updater.Found += () => _icon.Icon = MakeIcon();
        engine.Status += text => _icon.ShowBalloonTip(5000, "Lanes", text, ToolTipIcon.Warning);
        _icon.Icon = MakeIcon();
        _icon.Visible = true;
        _watchdog.Tick += (_, _) => Watch();
        _watchdog.Start();
    }

    // If the engine thread stops going round (stuck in a Windows call), its picture must not
    // stay over the screen: hide it from here and give the swipes back to Windows.
    readonly System.Windows.Forms.Timer _watchdog = new() { Interval = 1000 };
    bool _stuck;

    void Watch()
    {
        long beat = Interlocked.Read(ref _engine.Heartbeat);
        if (beat == 0 || Environment.TickCount64 - beat < 3000)
        {
            if (_stuck) { _stuck = false; Log.Write("engine running again"); if (_settings.Gestures) WindowsGestures.TakeOver(); }
            return;
        }
        if (_stuck) return;
        _stuck = true;
        Log.Write("engine stuck: overlay hidden, swipes given back to Windows");
        if (_engine.OverlayHandle != IntPtr.Zero)
        {
            int cloak = 1;
            DwmSetWindowAttribute(_engine.OverlayHandle, 13 /* DWMWA_CLOAK */, ref cloak, 4);
        }
        WindowsGestures.GiveBack();
        _icon.ShowBalloonTip(5000, "Lanes", "Lanes got stuck; Windows' own swipes are back. Restart it from this menu.", ToolTipIcon.Warning);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);

    public bool Gestures
    {
        get => _settings.Gestures;
        set
        {
            _settings.Gestures = value;
            _settings.Save();
            if (value) WindowsGestures.TakeOver(); else WindowsGestures.GiveBack();
            _icon.Icon = MakeIcon();
        }
    }

    public bool Natural
    {
        get => _settings.Natural;
        set { _settings.Natural = value; _settings.Save(); }
    }

    public string? Update => _updater.Available;
    public void ShowAll() => _engine.OpenOverviewFromMenu();
    public void OpenLog() => Process.Start(new ProcessStartInfo(Log.File) { UseShellExecute = true });
    public void InstallUpdate() => _updater.Install();
    public void Restart() => Program.Restart();
    public void Quit() => Application.Exit();

    Icon MakeIcon()
    {
        int size = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = size / 18f;
            var on = _settings.Gestures ? Color.White : Color.FromArgb(150, 150, 150);
            using var full = new SolidBrush(on);
            using var dim = new SolidBrush(Color.FromArgb(140, on));
            Bar(g, dim, 2 * s, 2.5f * s, 14 * s, 3 * s);
            Bar(g, full, 2 * s, 7.5f * s, 14 * s, 3 * s);
            Bar(g, dim, 2 * s, 12.5f * s, 14 * s, 3 * s);
            if (_updater.Available != null)
            {
                using var accent = new SolidBrush(Color.FromArgb(0x4C, 0xC2, 0xFF));
                g.FillEllipse(accent, size - 6 * s, 0, 6 * s, 6 * s);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    static void Bar(Graphics g, Brush b, float x, float y, float w, float h)
    {
        using var path = Rounded(x, y, w, h, h / 2);
        g.FillPath(b, path);
    }

    internal static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _watchdog.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _flyout.Dispose();
    }

    sealed class Flyout : Form
    {
        readonly Tray _tray;
        record Item(string Text, string? Hint, Func<bool>? Toggle, Action Act, bool Accent = false);
        List<Item?> _items = new();
        int _hover = -1;
        float S => DeviceDpi / 96f;

        public Flyout(Tray tray)
        {
            _tray = tray;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(0x2B, 0x2B, 0x2B);
            DoubleBuffered = true;
            KeyPreview = true;
            Deactivate += (_, _) => Hide();
            KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= (int)WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2, dark = 1, border = 0x00404040;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, 4);
        }

        void Build()
        {
            _items = new List<Item?>
            {
                new("Gestures", null, () => _tray.Gestures, () => _tray.Gestures = !_tray.Gestures),
                new("Content follows fingers", null, () => _tray.Natural, () => _tray.Natural = !_tray.Natural),
                new("Show all lanes", "3 fingers up", null, () => { Hide(); _tray.ShowAll(); }),
                new("Open log", null, null, () => { Hide(); _tray.OpenLog(); }),
                null,
            };
            if (_tray.Update is string v) _items.Add(new($"Update to {v}", $"Installed {Program.Version}", null, () => { Hide(); _tray.InstallUpdate(); }, true));
            _items.Add(new("Restart", null, null, () => { Hide(); _tray.Restart(); }));
            _items.Add(new("Quit", null, null, () => { Hide(); _tray.Quit(); }));
        }

        // 16 px line icons for the rows, drawn with the pen of the text.
        static void Glyph(Graphics g, string row, float x, float y, float size, bool accent)
        {
            float k = size / 16;
            using var pen = new Pen(accent ? Color.FromArgb(0x4C, 0xC2, 0xFF) : Color.FromArgb(0xE6, 0xE6, 0xE6), 1.2f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            PointF P(float a, float b) => new(x + a * k, y + b * k);
            switch (row.Split(' ')[0])
            {
                case "Gestures":
                    using (var r = Rounded(x + 1.5f * k, y + 3 * k, 13 * k, 10 * k, 2 * k)) g.DrawPath(pen, r);
                    foreach (var cx in new[] { 5.5f, 8f, 10.5f }) g.FillEllipse(new SolidBrush(pen.Color), x + (cx - 0.9f) * k, y + (cx == 8f ? 5.9f : 6.6f) * k, 1.8f * k, 1.8f * k);
                    break;
                case "Content":
                    g.DrawLine(pen, P(8, 2), P(8, 14)); g.DrawLines(pen, new[] { P(4.5f, 5.5f), P(8, 2), P(11.5f, 5.5f) }); g.DrawLines(pen, new[] { P(4.5f, 10.5f), P(8, 14), P(11.5f, 10.5f) });
                    break;
                case "Show":
                    using (var r = Rounded(x + 2 * k, y + 2.5f * k, 12 * k, 11 * k, 2 * k)) g.DrawPath(pen, r);
                    g.DrawLine(pen, P(2, 6.5f), P(14, 6.5f)); g.DrawLine(pen, P(2, 9.5f), P(14, 9.5f));
                    break;
                case "Open":
                    g.DrawRectangle(pen, x + 4 * k, y + 3 * k, 8 * k, 10 * k); g.DrawLine(pen, P(6, 6), P(10, 6)); g.DrawLine(pen, P(6, 8.5f), P(10, 8.5f)); g.DrawLine(pen, P(6, 11), P(8, 11));
                    break;
                case "Update":
                    g.DrawLine(pen, P(8, 2.5f), P(8, 10.5f)); g.DrawLines(pen, new[] { P(4.5f, 7), P(8, 10.5f), P(11.5f, 7) }); g.DrawLine(pen, P(3, 13.5f), P(13, 13.5f));
                    break;
                case "Restart":
                    g.DrawArc(pen, x + 3 * k, y + 3 * k, 10 * k, 10 * k, -60, 300); g.DrawLines(pen, new[] { P(13, 2.5f), P(13, 5), P(10.5f, 5) });
                    break;
                case "Quit":
                    g.DrawLine(pen, P(4, 4), P(12, 12)); g.DrawLine(pen, P(12, 4), P(4, 12));
                    break;
            }
        }

        float Header => 48 * S;
        float RowH => 36 * S;
        float SepH => 9 * S;

        public void ShowNearCursor() => ShowNear(Cursor.Position);

        public void ShowNear(Point cur)
        {
            Build();
            float h = 4 * S + Header + 9 * S + _items.Sum(i => i == null ? SepH : RowH) + 4 * S;
            Size = new Size((int)(300 * S), (int)h);
            var wa = Screen.FromPoint(cur).WorkingArea;
            Location = new Point(Math.Clamp(cur.X - Width / 2, wa.Left + 8, wa.Right - Width - 8), wa.Bottom - Height - (int)(12 * S));
            _hover = -1;
            Show();
            Activate();
            Invalidate();
        }

        IEnumerable<(int Index, RectangleF Rect)> Rows()
        {
            float y = 4 * S + Header + 9 * S;
            for (int i = 0; i < _items.Count; i++)
            {
                float h = _items[i] == null ? SepH : RowH;
                yield return (i, new RectangleF(4 * S, y, Width - 8 * S, h));
                y += h;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Rows().Where(r => _items[r.Index] != null && r.Rect.Contains(e.Location)).Select(r => r.Index).DefaultIfEmpty(-1).First();
            if (h != _hover) { _hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { _hover = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            foreach (var (i, r) in Rows())
                if (_items[i] is { } item && r.Contains(e.Location))
                {
                    item.Act();
                    Invalidate();
                    return;
                }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            float s = S;
            using var text = new Font("Segoe UI Variable Text", 10f, FontStyle.Regular);
            using var bold = new Font("Segoe UI Variable Display", 10.5f, FontStyle.Bold);
            using var hint = new Font("Segoe UI Variable Text", 9f, FontStyle.Regular);
            using var white = new SolidBrush(Color.FromArgb(0xF0, 0xF0, 0xF0));
            using var grey = new SolidBrush(Color.FromArgb(0x8A, 0x8F, 0x98));
            using var accent = new SolidBrush(Color.FromArgb(0x4C, 0xC2, 0xFF));
            using var line = new SolidBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            using var hot = new SolidBrush(Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));

            // Header: the app's mark and name.
            float hy = 4 * s;
            using (var b = new SolidBrush(Color.White))
            {
                Bar(g, new SolidBrush(Color.FromArgb(140, 255, 255, 255)), 16 * s, hy + 15 * s, 18 * s, 3.5f * s);
                Bar(g, b, 16 * s, hy + 21.5f * s, 18 * s, 3.5f * s);
                Bar(g, new SolidBrush(Color.FromArgb(140, 255, 255, 255)), 16 * s, hy + 28 * s, 18 * s, 3.5f * s);
            }
            g.DrawString("Lanes", bold, white, 44 * s, hy + Header / 2 - bold.Height / 2f);
            g.FillRectangle(line, 6 * s, 4 * s + Header + 4 * s, Width - 12 * s, 1);

            foreach (var (i, r) in Rows())
            {
                var item = _items[i];
                if (item == null) { g.FillRectangle(line, r.X + 2 * s, r.Y + r.Height / 2, r.Width - 4 * s, 1); continue; }
                if (i == _hover) using (var p = Rounded(r.X, r.Y, r.Width, r.Height, 4 * s)) g.FillPath(hot, p);
                float ty = r.Y + (r.Height - text.Height) / 2f;
                Glyph(g, item.Text, r.X + 12 * s, r.Y + r.Height / 2 - 8 * s, 16 * s, item.Accent);
                g.DrawString(item.Text, text, white, r.X + 40 * s, ty);
                if (item.Toggle is { } on)
                {
                    float tw = 40 * s, th = 20 * s, tx = r.Right - 12 * s - tw, tyy = r.Y + (r.Height - th) / 2;
                    if (on())
                    {
                        using var p = Rounded(tx, tyy, tw, th, th / 2);
                        g.FillPath(accent, p);
                        g.FillEllipse(Brushes.Black, tx + tw - 16 * s, tyy + 4 * s, 12 * s, 12 * s);
                    }
                    else
                    {
                        using var p = Rounded(tx + 0.5f, tyy + 0.5f, tw - 1, th - 1, th / 2);
                        using var pen = new Pen(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), 1);
                        g.DrawPath(pen, p);
                        g.FillEllipse(new SolidBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), tx + 5 * s, tyy + 5 * s, 10 * s, 10 * s);
                    }
                }
                else if (item.Hint != null)
                {
                    var size = g.MeasureString(item.Hint, hint);
                    g.DrawString(item.Hint, hint, grey, r.Right - 12 * s - size.Width, r.Y + (r.Height - hint.Height) / 2f);
                }
            }
        }
    }
}

using System.Drawing;
using System.Runtime.InteropServices;
using Lanes.Core;

namespace Lanes;

// "Lanes.exe --demo <folder>": sets up desktops with sample windows, then performs the swipes
// with made-up touchpad reports and saves screenshots along the way. For machines without a
// touchpad, such as the CI runner; never run on a machine someone is using.
static class Demo
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);

    public static async Task Run(Engine engine, string dir)
    {
        try
        {
            timeBeginPeriod(1);
            Directory.CreateDirectory(dir);
            for (int i = 0; i < 100 && !engine.Ready; i++) await Task.Delay(100);
            if (!engine.Ready) throw new Exception("engine not ready");
            await Task.Delay(1500);

            // Main gets a second desktop; then each desktop gets a window to recognise it by.
            var lanes = await engine.Ask(() => engine.AddDesktop(1));
            Guid top = lanes.Lanes[0].Desktops[0], editor = lanes.Lanes[1].Desktops[0],
                research = lanes.Lanes[1].Desktops[1], agent = lanes.Lanes[2].Desktops[0];
            Open("Messages", Color.FromArgb(0x17, 0x21, 0x2B), Color.FromArgb(0x2A, 0xAB, 0xEE), new Rectangle(150, 60, 1200, 700), top);
            Open("Editor", Color.FromArgb(0x1E, 0x1E, 0x1E), Color.FromArgb(0x56, 0x9C, 0xD6), new Rectangle(60, 40, 1000, 640), editor);
            Open("Notes", Color.FromArgb(0x1F, 0x1F, 0x1F), Color.FromArgb(0xF2, 0xC9, 0x4C), new Rectangle(1100, 80, 380, 320), editor);
            Open("Terminal", Color.FromArgb(0x0C, 0x0C, 0x0C), Color.FromArgb(0x3F, 0xB9, 0x50), new Rectangle(880, 440, 560, 320), editor);
            Open("Research", Color.FromArgb(0xF3, 0xF3, 0xF3), Color.FromArgb(0x3B, 0x5B, 0xDB), new Rectangle(120, 40, 900, 720), research);
            Open("Agent", Color.FromArgb(0x15, 0x15, 0x15), Color.FromArgb(0xB0, 0x8C, 0xFF), new Rectangle(90, 40, 1300, 720), agent);
            await Task.Delay(800);
            foreach (var d in new[] { top, research, agent, editor })
            {
                Desktops.Switch(d);
                // Windows' own "Desktop N" pill stays a while; the picture is taken after it.
                await Task.Delay(3000);
                engine.Snapshot();
                await Task.Delay(400);
            }

            // Windows' own pill right after a switch, then Lanes' copy of it during a swipe:
            // both say the same, in the same place, and the log compares them.
            Desktops.Switch(research);
            await Task.Delay(2800);
            Desktops.Switch(editor);
            await Task.Delay(350);
            engine.Screenshot(Path.Combine(dir, "pill-windows.png"));
            await Task.Delay(3200);
            await Swipe(engine, 4, -1, 0, 0.42 * 0.15, hold: async () => { engine.Screenshot(Path.Combine(dir, "pill-lanes.png")); await Task.Delay(400); });
            await Task.Delay(1500);
            ComparePills(dir);

            // Four fingers down, held at about 40%: the Top lane comes down; then on and let go.
            var rec = new Recorder();
            rec.Start();
            await Swipe(engine, 4, 0, 1, 0.42 * 0.42, hold: async () => { engine.Screenshot(Path.Combine(dir, "1-vertical-swipe.png")); await Task.Delay(600); }, release: false);
            await Swipe(engine, 4, 0, 1, 0.42 * 0.8, continueFrom: 0.42 * 0.42);
            await Task.Delay(1500);
            // Back down to Main with four fingers up, a short flick.
            await Swipe(engine, 4, 0, -1, 0.42 * 0.3, speed: 1.6);
            await Task.Delay(1500);
            // Four fingers left in Main: Editor to Research, held at about 60%, then let go there.
            await Swipe(engine, 4, -1, 0, 0.42 * 0.58, hold: async () => { engine.Screenshot(Path.Combine(dir, "2-horizontal-swipe.png")); await Task.Delay(600); });
            await Task.Delay(1500);
            rec.Stop(dir, "frames-switch");
            // Right, past the end of the lane... first back to Editor, then once more into the stretch.
            await Swipe(engine, 4, 1, 0, 0.42 * 0.8, earlyShot: Path.Combine(dir, "7-switch-early.png"));
            await Task.Delay(1500);
            await Swipe(engine, 4, 1, 0, 0.42 * 0.5, hold: async () => { engine.Screenshot(Path.Combine(dir, "2b-end-of-lane.png")); await Task.Delay(300); });
            await Task.Delay(1500);

            // Three fingers up: the overview, held at 45%, then opened fully.
            rec = new Recorder();
            rec.Start();
            await Swipe(engine, 3, 0, -1, 0.28 * 0.45, earlyShot: Path.Combine(dir, "8-overview-early.png"), hold: async () => { engine.Screenshot(Path.Combine(dir, "3-overview-opening.png")); await Task.Delay(600); }, release: false);
            await Swipe(engine, 3, 0, -1, 0.28 * 0.7, continueFrom: 0.28 * 0.45);
            await Task.Delay(1200);
            engine.Screenshot(Path.Combine(dir, "4-overview-open.png"));
            await Task.Delay(600);

            // Four fingers inside the overview: to Research and held halfway, then up to the
            // top lane and back down, without leaving it.
            await Swipe(engine, 4, -1, 0, 0.42 * 0.5, hold: async () => { engine.Screenshot(Path.Combine(dir, "9-overview-swipe.png")); await Task.Delay(500); }, release: false);
            await Swipe(engine, 4, -1, 0, 0.42 * 0.8, continueFrom: 0.42 * 0.5);
            await Task.Delay(1200);
            await Swipe(engine, 4, 0, 1, 0.42 * 0.8);
            await Task.Delay(1200);
            await Swipe(engine, 4, 0, -1, 0.42 * 0.8);
            await Task.Delay(1200);
            rec.Stop(dir, "frames-overview");
            engine.Screenshot(Path.Combine(dir, "10-overview-after-swipes.png"));
            Log.Write($"after the swipes in the overview: on {(Desktops.Current == research ? "Research" : "another desktop")}");

            // Drag the Agent desktop into the middle lane, between Editor and Research.
            var (tiles, _) = await engine.OverviewLayout();
            var agentTile = tiles.First(t => t.Desktop == agent).Rect;
            await PressAndPull(agentTile, 30);
            (tiles, _) = await engine.OverviewLayout();
            var ed = tiles.First(t => t.Desktop == editor).Rect;
            var re = tiles.First(t => t.Desktop == research).Rect;
            await Glide(new Point((ed.Right + re.Left) / 2, ed.Top + ed.Height / 2));
            engine.Screenshot(Path.Combine(dir, "11-drag-desktop.png"));
            await Task.Delay(400);
            Release();
            await Task.Delay(900);
            engine.Screenshot(Path.Combine(dir, "12-after-drag-desktop.png"));
            Log.Write("lanes now: " + await LanesNow(engine));

            // And back into a lane of its own, through "New lane".
            (tiles, _) = await engine.OverviewLayout();
            await PressAndPull(tiles.First(t => t.Desktop == agent).Rect, 30);
            (tiles, _) = await engine.OverviewLayout();
            var newLane = tiles.Last(t => t.Kind == TileKind.NewLane).Rect;
            await Glide(new Point(newLane.Left + newLane.Width / 2, newLane.Top + newLane.Height / 2));
            await Task.Delay(300);
            Release();
            await Task.Delay(900);
            Log.Write("lanes now: " + await LanesNow(engine));

            // Drag the Research window onto the "+" at the end of the top lane: a new desktop there.
            var (tiles2, windows) = await engine.OverviewLayout();
            var researchWindow = windows.First(w => w.Title == "Research").Rect;
            var researchHandle = Application.OpenForms.Cast<Form>().First(f => f.Text == "Research").Handle;
            await PressAndPull(researchWindow, 40);
            (tiles2, _) = await engine.OverviewLayout();
            var plus = tiles2.First(t => t.Kind == TileKind.Plus && t.Lane == 0).Rect;
            await Glide(new Point(plus.Left + plus.Width / 2, plus.Top + plus.Height / 2));
            await Task.Delay(300);
            engine.Screenshot(Path.Combine(dir, "13-drag-window.png"));
            Release();
            await Task.Delay(900);
            var landed = Desktops.WindowDesktop(researchHandle);
            Log.Write($"the Research window is now on a desktop in lane {(await engine.Ask(() => engine.LaneOf(landed)))}; lanes now: {await LanesNow(engine)}");
            engine.Screenshot(Path.Combine(dir, "14-after-drag-window.png"));

            // A middle click closes the Research desktop, which the window just left.
            int before = Desktops.Count;
            (tiles2, _) = await engine.OverviewLayout();
            var r2 = tiles2.First(t => t.Desktop == research).Rect;
            MiddleClick(r2.Left + r2.Width / 2, r2.Top + r2.Height / 2);
            await Task.Delay(900);
            Log.Write($"middle click on a desktop: {before} desktops before, {Desktops.Count} after");
            engine.Screenshot(Path.Combine(dir, "15-after-middle-click.png"));
            await Swipe(engine, 3, 0, 1, 0.28 * 0.8);
            await Task.Delay(1500);
            engine.Screenshot(Path.Combine(dir, "5-after.png"));
            await Task.Delay(1000);

            // The overlay, cloaked between swipes, must let clicks through to the windows below.
            var editorWindow = Application.OpenForms.Cast<Form>().First(f => f.Text == "Editor");
            bool clicked = false;
            editorWindow.MouseClick += (_, _) => clicked = true;
            var c = editorWindow.RectangleToScreen(editorWindow.ClientRectangle);
            Click(c.Left + c.Width / 2, c.Top + c.Height / 2);
            await Task.Delay(500);
            Log.Write(clicked ? "click reached the window under the overlay" : "CLICK BLOCKED by the overlay");
            Tray.Instance?.ShowMenu(new Point(1800, 1000));
            await Task.Delay(800);
            engine.Screenshot(Path.Combine(dir, "6-tray-menu.png"));
            await Task.Delay(500);
            Log.Write("demo done");
        }
        catch (Exception e) { Log.Write($"demo: {e}"); }
        finally
        {
            await Task.Delay(500);
            Application.Exit();
        }
    }

    static double _x0 = 0.3, _y0 = 0.3;

    // Fingers land, travel `distance` pad widths in the direction (dx, dy) at about 0.6 pad widths/s,
    // optionally stop and hold, then lift (unless `release` is false, to carry on in the next call).
    static async Task Swipe(Engine engine, int fingers, int dx, int dy, double distance, Func<Task>? hold = null, bool release = true, double continueFrom = 0, double speed = 0.6, string? earlyShot = null)
    {
        const int stepMs = 8;
        int steps = (int)Math.Ceiling((distance - continueFrom) / (speed * stepMs / 1000.0));
        double Pos(int i) => continueFrom + (distance - continueFrom) * i / Math.Max(1, steps);
        Finger[] At(double t) => Enumerable.Range(0, fingers).Select(f => new Finger((uint)(f + 1), _x0 + 0.08 * f + dx * t, _y0 + 0.02 * (f % 2) + dy * t)).ToArray();
        for (int i = 0; i <= steps; i++)
        {
            engine.Feed(new TouchFrame(0, At(Pos(i))));
            // About 60 ms after the swipe is recognised: the first frames must already be ours.
            if (earlyShot != null && i == 12) engine.Screenshot(earlyShot);
            await Task.Delay(stepMs);
        }
        if (hold != null)
        {
            // A touchpad keeps reporting while fingers rest on it.
            var still = At(distance);
            using var cts = new CancellationTokenSource();
            var keep = Task.Run(async () => { while (!cts.IsCancellationRequested) { engine.Feed(new TouchFrame(0, still)); await Task.Delay(stepMs); } });
            await Task.Delay(300);
            await hold();
            cts.Cancel();
            await keep;
        }
        if (release) engine.Feed(new TouchFrame(0, Array.Empty<Finger>()));
    }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(0x2, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x4, 0, 0, 0, IntPtr.Zero);
    }

    static async Task<string> LanesNow(Engine engine) =>
        await engine.Ask(() => engine.LanesText());

    // Presses on a box and pulls far enough for a drag to start.
    static async Task PressAndPull(Rectangle box, int pull)
    {
        var at = new Point(box.Left + box.Width / 2, box.Top + box.Height / 2);
        SetCursorPos(at.X, at.Y);
        await Task.Delay(100);
        mouse_event(0x2, 0, 0, 0, IntPtr.Zero);
        await Task.Delay(100);
        for (int i = 1; i <= 6; i++) { SetCursorPos(at.X, at.Y - pull * i / 6); await Task.Delay(16); }
        _pointer = new Point(at.X, at.Y - pull);
        await Task.Delay(300);
    }

    static Point _pointer;

    static async Task Glide(Point to)
    {
        var from = _pointer;
        for (int i = 1; i <= 30; i++)
        {
            SetCursorPos(from.X + (to.X - from.X) * i / 30, from.Y + (to.Y - from.Y) * i / 30);
            await Task.Delay(16);
        }
        _pointer = to;
    }

    static void Release() => mouse_event(0x4, 0, 0, 0, IntPtr.Zero);

    static void MiddleClick(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(0x20, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x40, 0, 0, 0, IntPtr.Zero);
    }

    // Finds Windows' "Desktop N" pill (or Lanes' copy) in a screenshot and logs where it is and
    // where its text is, for both, side by side.
    static void ComparePills(string dir)
    {
        foreach (var name in new[] { "pill-windows", "pill-lanes" })
        {
            string path = Path.Combine(dir, name + ".png");
            if (!File.Exists(path)) { Log.Write($"{name}: no screenshot"); continue; }
            using var b = new Bitmap(path);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (int y = b.Height - 300; y < b.Height; y++)
                for (int x = b.Width / 2 - 300; x < b.Width / 2 + 300; x++)
                {
                    var c = b.GetPixel(x, y);
                    if (Math.Abs(c.R - 44) <= 2 && Math.Abs(c.G - 44) <= 2 && Math.Abs(c.B - 44) <= 2)
                    { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
                }
            if (maxX < 0) { Log.Write($"{name}: no pill found"); continue; }
            int tx0 = int.MaxValue, ty0 = int.MaxValue, tx1 = -1, ty1 = -1;
            for (int y = minY + 2; y < maxY - 1; y++)
                for (int x = minX + 2; x < maxX - 1; x++)
                    if (b.GetPixel(x, y).R > 90) { tx0 = Math.Min(tx0, x); tx1 = Math.Max(tx1, x); ty0 = Math.Min(ty0, y); ty1 = Math.Max(ty1, y); }
            Log.Write($"{name}: pill {minX},{minY} {maxX - minX + 1}x{maxY - minY + 1}; text {tx0},{ty0} {tx1 - tx0 + 1}x{ty1 - ty0 + 1}");
            using var crop = b.Clone(new Rectangle(minX - 6, minY - 6, maxX - minX + 13, maxY - minY + 13), b.PixelFormat);
            crop.Save(Path.Combine(dir, name + "-crop.png"));
        }
    }

    // Takes pictures of the screen while a swipe plays, for the animations in the README.
    sealed class Recorder
    {
        readonly List<(long Ms, Bitmap Frame)> _frames = new();
        Thread? _thread;
        volatile bool _on;

        public void Start()
        {
            _on = true;
            var screen = Native.PrimaryMonitor().Monitor;
            _thread = new Thread(() =>
            {
                long t0 = Environment.TickCount64;
                while (_on)
                {
                    long t = Environment.TickCount64;
                    using var p = Capture.Screen(screen);
                    var small = new Bitmap(960, 540);
                    using (var src = new Bitmap(p.Width, p.Height, p.Width * 4, System.Drawing.Imaging.PixelFormat.Format32bppRgb, p.Data))
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g.DrawImage(src, 0, 0, 960, 540);
                    }
                    lock (_frames) _frames.Add((t - t0, small));
                    int wait = (int)(40 - (Environment.TickCount64 - t));
                    if (wait > 0) Thread.Sleep(wait);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Stop(string dir, string name)
        {
            _on = false;
            _thread?.Join();
            string folder = Path.Combine(dir, name);
            Directory.CreateDirectory(folder);
            var times = new List<string>();
            for (int i = 0; i < _frames.Count; i++)
            {
                _frames[i].Frame.Save(Path.Combine(folder, $"{i:D4}.png"));
                times.Add(_frames[i].Ms.ToString());
                _frames[i].Frame.Dispose();
            }
            File.WriteAllLines(Path.Combine(folder, "times.txt"), times);
            Log.Write($"recorded {name}: {_frames.Count} frames");
        }
    }

    static void Open(string title, Color back, Color accent, Rectangle at, Guid desktop)
    {
        // Laid out for 1536x864 (1920x1080 at 125%); scaled to the real screen.
        var wa = Screen.PrimaryScreen!.WorkingArea;
        double k = Math.Min(wa.Width / 1536.0, (wa.Height + 48) / 864.0);
        at = new Rectangle((int)(at.X * k), (int)(at.Y * k), (int)(at.Width * k), (int)(at.Height * k));
        var f = new Form
        {
            Text = title,
            BackColor = back,
            StartPosition = FormStartPosition.Manual,
            Bounds = at,
            ShowInTaskbar = true,
        };
        f.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            bool light = back.GetBrightness() > 0.5f;
            using var line = new SolidBrush(light ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(40, 255, 255, 255));
            using var acc = new SolidBrush(accent);
            using var font = new Font("Segoe UI", 28, FontStyle.Bold);
            g.FillRectangle(acc, 32, 32, 56, 56);
            g.DrawString(title, font, light ? Brushes.Black : Brushes.White, 104, 34);
            for (int i = 0; i < 14; i++) g.FillRectangle(line, 32, 120 + i * 28, 140 + (i * 97) % (Math.Max(200, f.ClientSize.Width - 200)), 10);
        };
        f.Show();
        Desktops.MoveWindow(f.Handle, desktop);
    }
}

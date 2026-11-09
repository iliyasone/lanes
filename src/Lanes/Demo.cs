using System.Drawing;
using System.Runtime.InteropServices;
using Lanes.Core;

namespace Lanes;

// "Lanes.exe --demo <folder>": sets up desktops with sample windows, then performs the swipes
// with made-up touchpad reports and saves screenshots along the way. For machines without a
// touchpad, such as the CI runner; never run on a machine someone is using.
static class Demo
{
    // Finger travel that moves the picture by a whole screen.
    static readonly double FullSwipe = new Tuning().FullSwipe;

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
            await Swipe(engine, 4, -1, 0, FullSwipe * 0.15, hold: async () => { engine.Screenshot(Path.Combine(dir, "pill-lanes.png")); await Task.Delay(400); });
            await Task.Delay(1500);
            ComparePills(dir);

            // Four fingers down, held at about 40%: the Top lane comes down; then on and let go.
            var rec = new Recorder();
            rec.Start();
            await Swipe(engine, 4, 0, 1, FullSwipe * 0.42, hold: async () => { engine.Screenshot(Path.Combine(dir, "1-vertical-swipe.png")); await Task.Delay(600); }, release: false);
            await Swipe(engine, 4, 0, 1, FullSwipe * 0.8, continueFrom: FullSwipe * 0.42);
            await Task.Delay(1500);
            // Back down to Main with four fingers up, a short flick.
            await Swipe(engine, 4, 0, -1, FullSwipe * 0.3, speed: 1.6);
            await Task.Delay(1500);
            // Four fingers left in Main: Editor to Research, held at about 60%, then let go there.
            await Swipe(engine, 4, -1, 0, FullSwipe * 0.58, hold: async () => { engine.Screenshot(Path.Combine(dir, "2-horizontal-swipe.png")); await Task.Delay(600); });
            await Task.Delay(1500);
            rec.Stop(dir, "frames-switch");
            // Right, past the end of the lane... first back to Editor, then once more into the stretch.
            await Swipe(engine, 4, 1, 0, FullSwipe * 0.8, earlyShot: Path.Combine(dir, "7-switch-early.png"));
            await Task.Delay(1500);
            await Swipe(engine, 4, 1, 0, FullSwipe * 0.5, hold: async () => { engine.Screenshot(Path.Combine(dir, "2b-end-of-lane.png")); await Task.Delay(300); });
            await Task.Delay(1500);

            // Three fingers up: the overview, held at 45%, then opened fully.
            rec = new Recorder();
            rec.Start();
            await Swipe(engine, 3, 0, -1, 0.28 * 0.45, earlyShot: Path.Combine(dir, "8-overview-early.png"), hold: async () => { engine.Screenshot(Path.Combine(dir, "3-overview-opening.png")); await Task.Delay(600); }, release: false);
            await Swipe(engine, 3, 0, -1, 0.28 * 0.7, continueFrom: 0.28 * 0.45);
            await Task.Delay(1200);
            engine.Screenshot(Path.Combine(dir, "4-overview-open.png"));

            // The pointer over a window: its shadow grows and deepens, still right under it.
            var (_, shown) = await engine.OverviewLayout();
            var editorBox = shown.First(w => w.Title == "Editor").Rect;
            SetCursorPos(editorBox.Left + editorBox.Width / 2, editorBox.Top + editorBox.Height / 2);
            await Task.Delay(500);
            engine.Screenshot(Path.Combine(dir, "4a-hover.png"));
            // Notes dragged onto Terminal: Terminal goes to Notes' place already; let go, the
            // two change places and sizes on the desktop.
            var notesForm = Application.OpenForms.Cast<Form>().First(f => f.Text == "Notes");
            var terminalForm = Application.OpenForms.Cast<Form>().First(f => f.Text == "Terminal");
            string Frames() => $"Notes {notesForm.Bounds}, Terminal {terminalForm.Bounds}";
            Log.Write("before the swap: " + Frames());
            await PressAndPull(shown.First(w => w.Title == "Notes").Rect, 30);
            var terminalBox = shown.First(w => w.Title == "Terminal").Rect;
            await Glide(new Point(terminalBox.Left + terminalBox.Width / 2, terminalBox.Top + terminalBox.Height / 2));
            await Task.Delay(500);
            engine.Screenshot(Path.Combine(dir, "4b-swap-hold.png"));
            Release();
            await Task.Delay(900);
            engine.Screenshot(Path.Combine(dir, "4c-after-swap.png"));
            Log.Write("after the swap: " + Frames());
            await Task.Delay(600);

            // Four fingers inside the overview: to Research and held halfway, then up to the
            // top lane and back down, without leaving it.
            await Swipe(engine, 4, -1, 0, FullSwipe * 0.5, hold: async () => { engine.Screenshot(Path.Combine(dir, "9-overview-swipe.png")); await Task.Delay(500); }, release: false);
            await Swipe(engine, 4, -1, 0, FullSwipe * 0.8, continueFrom: FullSwipe * 0.5);
            await Task.Delay(1200);
            await Swipe(engine, 4, 0, 1, FullSwipe * 0.8);
            await Task.Delay(1200);
            await Swipe(engine, 4, 0, -1, FullSwipe * 0.8);
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
            await Task.Delay(400);
            engine.Screenshot(Path.Combine(dir, "12a-drag-desktop-new-lane.png"));
            Release();
            await Task.Delay(900);
            Log.Write("lanes now: " + await LanesNow(engine));

            // Drag the Research window onto the place for a new desktop at the right end of the
            // top lane: a new desktop there.
            var (tiles2, windows) = await engine.OverviewLayout();
            var researchWindow = windows.First(w => w.Title == "Research").Rect;
            var researchHandle = Application.OpenForms.Cast<Form>().First(f => f.Text == "Research").Handle;
            await PressAndPull(researchWindow, 40);
            (tiles2, _) = await engine.OverviewLayout();
            // Held a moment by the gap between two desktops of a lane, it opens a place there;
            // moved on, the place closes.
            var pair = tiles2.Where(t => t.Kind == TileKind.Desktop).GroupBy(t => t.Lane).Select(g => g.OrderBy(t => t.Rect.X).ToList()).FirstOrDefault(r => r.Count >= 2);
            if (pair != null)
            {
                await Glide(new Point((pair[0].Rect.Right + pair[1].Rect.Left) / 2, pair[0].Rect.Top + pair[0].Rect.Height / 2));
                await Task.Delay(700);
                engine.Screenshot(Path.Combine(dir, "13a-drag-window-between.png"));
                await Glide(new Point(pair[0].Rect.Left + pair[0].Rect.Width / 2, pair[0].Rect.Bottom + 200));
                await Task.Delay(500);
            }
            (tiles2, _) = await engine.OverviewLayout();
            var plus = tiles2.Where(t => t.Kind == TileKind.NewDesk && t.Lane == 0).MaxBy(t => t.Rect.X).Rect;
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
            await WindowButtons(engine, dir, editor);
            await AppOverview(engine, dir, editor, agent);
            await FullScreenTaskbar(engine, dir);
            // With Windows' setting that Lanes turns on, the taskbar keeps the apps of other desktops.
            {
                // A File Explorer window (another program than these test windows) moved to the
                // desktop below: its taskbar button shows only with the setting on.
                System.Diagnostics.Process.Start("explorer.exe", @"/n,C:\Windows");
                IntPtr fx = IntPtr.Zero;
                for (int i = 0; i < 60 && fx == IntPtr.Zero; i++) { await Task.Delay(100); fx = FindWindow("CabinetWClass", null); }
                Desktops.MoveWindow(fx, agent);
                await Task.Delay(1000);
                Log.Write($"File Explorer window {(fx == IntPtr.Zero ? "not found" : Desktops.WindowDesktop(fx) == agent ? "on the desktop below" : "still here")}");
                // First the taskbar as Windows ships it on a PC: this desktop's windows only.
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                    k.SetValue("VirtualDesktopTaskbarFilter", 1, Microsoft.Win32.RegistryValueKind.DWord);
                long t0 = Environment.TickCount64;
                SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "TraySettings", 2, 3000, out _);
                Log.Write($"demo: taskbar told of this desktop's windows only in {Environment.TickCount64 - t0} ms");
                await Task.Delay(2000);
                using var shotBefore = TaskbarShot(Path.Combine(dir, "16-taskbar-this-desktop.png"));
                t0 = Environment.TickCount64;
                TaskbarSetting.ShowAllDesktops();
                Log.Write($"demo: taskbar told of all desktops' windows in {Environment.TickCount64 - t0} ms");
                await Task.Delay(2000);
                using var shotAfter = TaskbarShot(Path.Combine(dir, "17-taskbar-all-desktops.png"));
                int changed = 0;
                for (int y = 0; y < shotBefore.Height; y++)
                    for (int x = shotBefore.Width / 4; x < shotBefore.Width * 3 / 4; x++)
                        if (shotBefore.GetPixel(x, y) != shotAfter.GetPixel(x, y)) changed++;
                Log.Write($"taskbar: {changed} pixels in the middle changed when windows of all desktops were turned on");
                TaskbarSetting.Restore();
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                    k.DeleteValue("VirtualDesktopTaskbarFilter", false);
                if (fx != IntPtr.Zero) PostMessage(fx, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            }
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(0x2, 0, 0, 0, IntPtr.Zero);
        mouse_event(0x4, 0, 0, 0, IntPtr.Zero);
    }

    // The test windows' taskbar button (they are all one program's): the pointer resting on it
    // brings up Lanes' map of the desktops, with the windows where they are, before Windows' own
    // previews would come (the log says whether they did), and the map goes once the pointer
    // leaves it. The one on the Agent desktop clicked, Lanes goes there and brings it to the front.
    static async Task AppOverview(Engine engine, string dir, Guid editor, Guid agent)
    {
        Desktops.Switch(editor);
        await Task.Delay(2500);
        engine.Snapshot();
        await Task.Delay(400);
        // Another app's window, in colour, on this desktop: the map shows it in grey, as all but
        // the app's own windows.
        string? chrome = new[] { @"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" }.FirstOrDefault(File.Exists);
        System.Diagnostics.Process? browser = null;
        IntPtr colours = IntPtr.Zero;
        if (chrome != null)
        {
            string profile = Path.Combine(Path.GetTempPath(), "lanes-demo-colours");
            browser = System.Diagnostics.Process.Start(chrome, $"--user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --window-position=40,600 --window-size=760,400 \"data:text/html,<title>Colours</title><body style='margin:0;background:linear-gradient(90deg,%23e84a5f,%23ffb400,%2337b24d,%233b82f6)'>\"");
            for (int i = 0; i < 100 && colours == IntPtr.Zero; i++)
            {
                await Task.Delay(100);
                colours = TopWindows().FirstOrDefault(h => Native.ClassName(h) == "Chrome_WidgetWin_1" && Native.WindowText(h).StartsWith("Colours"));
            }
            await Task.Delay(1500);
            engine.Snapshot();
            await Task.Delay(400);
            Log.Write($"app map: a Chrome window {(colours == IntPtr.Zero ? "NOT FOUND" : "open here")}");
        }
        var mine = Application.OpenForms.Cast<Form>().First(f => f.Text == "Editor").Handle;
        var button = engine.Buttons == null ? null : await engine.Buttons.ButtonOf(mine);
        if (button == null) { Log.Write("app map: no taskbar button found for the test windows"); return; }
        var r = button.Rect;
        Log.Write($"app map: the test windows' button is \"{button.App.Name}\" ({button.App.Id}) at {r.Left},{r.Top} {r.Width}x{r.Height}, {button.Windows} windows");
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        // Rests on the button: when Windows starts its previews, if it does, and when Lanes' map is up.
        async Task Rest()
        {
            var before = TopWindows();
            SetCursorPos(cx - 20, cy);
            long t0 = Environment.TickCount64;
            long? previews = null, map = null;
            for (int i = 0; Environment.TickCount64 - t0 < 1800; i++)
            {
                if (i < 20) mouse_event(0x1, 1, 0, 0, IntPtr.Zero);
                if (previews == null && TopWindows().Any(w => !before.Contains(w) && Native.ClassName(w) is "Xaml_WindowedPopupClass" or "ThumbnailDeviceHelperWnd")) previews = Environment.TickCount64 - t0;
                if (map == null && (await engine.OverviewLayout()).Windows.Count > 0) map = Environment.TickCount64 - t0;
                await Task.Delay(20);
            }
            Log.Write($"app map: resting on the button, Windows' previews {(previews == null ? "never started" : $"STARTED after {previews} ms")}; Lanes' map {(map == null ? "NOT OPEN" : $"up after {map} ms")}");
        }
        await Rest();
        engine.Screenshot(Path.Combine(dir, "18c-map-on-hover.png"));
        await Task.Delay(300);
        // The pointer goes up the screen, away from the map and the taskbar.
        SetCursorPos(cx, 120);
        mouse_event(0x1, 0, 1, 0, IntPtr.Zero);
        await Task.Delay(1200);
        Log.Write($"app map: after the pointer left it, it is {((await engine.OverviewLayout()).Windows.Count == 0 ? "closed" : "STILL OPEN")}");
        await Rest();
        engine.Screenshot(Path.Combine(dir, "18-app-overview.png"));
        var (_, windows) = await engine.OverviewLayout();
        var other = windows.FirstOrDefault(w => w.Title == "Agent").Rect;
        if (other.Width == 0) return;
        // Up off the taskbar first, as a hand goes, then over to the window.
        _pointer = new Point(cx, cy);
        await Glide(new Point(cx, cy - 120));
        await Glide(new Point(other.Left + other.Width / 2, other.Top + other.Height / 2));
        await Task.Delay(600);
        engine.Screenshot(Path.Combine(dir, "18a-app-overview-pointer.png"));
        Click(other.Left + other.Width / 2, other.Top + other.Height / 2);
        await Task.Delay(2500);
        Log.Write($"after a click on Agent there: {(Desktops.Current == agent ? "on its desktop" : "on another desktop")}, \"{Native.WindowText(GetForegroundWindow())}\" in front");
        engine.Screenshot(Path.Combine(dir, "18b-after-app-click.png"));
        // A click on Chrome's button through the map: the map goes and Chrome comes to the front,
        // as it does with no map (tried first, to compare). The buttons' places are read again,
        // after the switch.
        Desktops.Switch(editor);
        await Task.Delay(2000);
        var chromeButton = colours == IntPtr.Zero ? null : await engine.Buttons!.ButtonOf(colours);
        if (await engine.Buttons!.ButtonOf(mine) is { } again) r = again.Rect;
        (cx, cy) = ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
        if (chromeButton == null) Log.Write("app map: no taskbar button of the Chrome window");
        else
        {
            var b = chromeButton.Rect;
            int bx = (b.Left + b.Right) / 2, by = (b.Top + b.Bottom) / 2;
            // A click on the Editor's title bar puts it in front.
            Click(700, 60);
            await Task.Delay(800);
            string before = Describe(GetForegroundWindow());
            Click(bx, by);
            await Task.Delay(1500);
            Log.Write($"app map: with no map, a click on Chrome's button ({chromeButton.Windows} windows): in front {before}, then {Describe(GetForegroundWindow())}");
            Click(700, 60);
            await Task.Delay(800);
            await Rest();
            before = Describe(GetForegroundWindow());
            SetCursorPos(bx, by);
            mouse_event(0x2, 0, 0, 0, IntPtr.Zero);
            await Task.Delay(150);
            mouse_event(0x4, 0, 0, 0, IntPtr.Zero);
            await Task.Delay(1500);
            Log.Write($"app map: a click on Chrome's button through it: the map is {((await engine.OverviewLayout()).Windows.Count == 0 ? "closed" : "STILL OPEN")}; in front {before}, then {Describe(GetForegroundWindow())}");
        }
        engine.Screenshot(Path.Combine(dir, "18d-taskbar-click-through-map.png"));
        SendKeys.SendWait("{ESC}");
        await Task.Delay(800);
        try { browser?.Kill(entireProcessTree: true); } catch { }
    }

    // The top-level windows shown now.
    static List<IntPtr> TopWindows()
    {
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) => { if (Native.IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    static string Describe(IntPtr h)
    {
        var f = Native.Frame(h);
        return $"{Native.ClassName(h)} \"{Native.WindowText(h)}\" {f.Left},{f.Top} {f.Width}x{f.Height}";
    }

    // The buttons over a window in the overview: Notes to full screen and back, then minimized
    // into its taskbar button (a picture as it goes).
    static async Task WindowButtons(Engine engine, string dir, Guid editor)
    {
        Desktops.Switch(editor);
        await Task.Delay(2500);
        engine.Snapshot();
        await Task.Delay(400);
        var notes = Application.OpenForms.Cast<Form>().First(f => f.Text == "Notes");
        var was = notes.Bounds;
        await Swipe(engine, 3, 0, -1, 0.28 * 0.7);
        await Task.Delay(1500);
        async Task<List<Rectangle>> Hover()
        {
            var (_, windows) = await engine.OverviewLayout();
            var box = windows.First(w => w.Title == "Notes").Rect;
            _pointer = new Point(box.Left + box.Width / 2, box.Bottom + 40);
            SetCursorPos(_pointer.X, _pointer.Y);
            await Glide(new Point(box.Left + box.Width / 2, box.Top + box.Height / 2));
            await Task.Delay(400);
            return await engine.HoverButtons();
        }
        async Task Press(List<Rectangle> buttons, int k, int wait = 1500)
        {
            var b = buttons[k];
            await Glide(new Point(b.Left + b.Width / 2, b.Top + b.Height / 2));
            await Task.Delay(300);
            Click(b.Left + b.Width / 2, b.Top + b.Height / 2);
            await Task.Delay(wait);
        }
        var buttons = await Hover();
        if (buttons.Count != 3) { Log.Write("window buttons: none over Notes"); return; }
        engine.Screenshot(Path.Combine(dir, "4d-window-buttons.png"));
        await Press(buttons, 1);
        var screen = Screen.FromHandle(notes.Handle).Bounds;
        Log.Write($"window buttons: after full screen, Notes is at {notes.Bounds} ({(notes.Bounds == screen ? "the whole screen" : "NOT THE WHOLE SCREEN")}), {(AppWindows.InFullScreen(notes.Handle) ? "in" : "NOT IN")} full screen");
        buttons = await Hover();
        engine.Screenshot(Path.Combine(dir, "4e-full-screen.png"));
        if (buttons.Count == 3) await Press(buttons, 1);
        Log.Write($"window buttons: back from full screen, Notes is at {notes.Bounds} ({(notes.Bounds == was ? "where it was" : $"NOT WHERE IT WAS, {was}")})");
        buttons = await Hover();
        if (buttons.Count == 3)
        {
            await Press(buttons, 0, 120);
            engine.Screenshot(Path.Combine(dir, "4f-minimizing.png"));
            await Task.Delay(1400);
        }
        Log.Write($"window buttons: after minimize, Notes is {(Native.IsIconic(notes.Handle) ? "minimized" : "NOT MINIMIZED")}; the overview shows {(await engine.OverviewLayout()).Windows.Count} windows");
        await Swipe(engine, 3, 0, 1, 0.28 * 0.8);
        await Task.Delay(1500);
        notes.WindowState = FormWindowState.Normal;
        await Task.Delay(500);
        Log.Write("window buttons: done");
    }

    // A browser in full screen, as with a video, lies over the taskbar; in the overview the real
    // taskbar is in front of it (what is under the pointer there is the taskbar), and after the
    // overview the browser is in front again. Chrome tells the taskbar itself that it is in full
    // screen, which a plain window does not.
    static async Task FullScreenTaskbar(Engine engine, string dir)
    {
        var screen = Screen.PrimaryScreen!;
        string? chrome = new[] { @"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe" }.FirstOrDefault(File.Exists);
        System.Diagnostics.Process? browser = null;
        Form? form = null;
        IntPtr full = IntPtr.Zero;
        if (chrome != null)
        {
            string profile = Path.Combine(Path.GetTempPath(), "lanes-demo-chrome");
            browser = System.Diagnostics.Process.Start(chrome, $"--user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --start-fullscreen \"data:text/html,<title>Video</title><body style='background:%23302040'>\"");
            for (int i = 0; i < 100 && full == IntPtr.Zero; i++)
            {
                await Task.Delay(100);
                full = TopWindows().FirstOrDefault(h => Native.ClassName(h) == "Chrome_WidgetWin_1" && Native.WindowText(h).StartsWith("Video"));
            }
            Log.Write($"full screen: Chrome's window {(full == IntPtr.Zero ? "not found" : Describe(full))}");
        }
        if (full == IntPtr.Zero)
        {
            form = new Form { Text = "Video", BackColor = Color.FromArgb(0x20, 0x10, 0x30), FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = screen.Bounds, ShowInTaskbar = true };
            form.Show();
            full = form.Handle;
        }
        await Task.Delay(1000);
        AppWindows.Activate(full);
        await Task.Delay(1500);
        engine.Snapshot();
        await Task.Delay(500);
        var b = screen.Bounds; var w = screen.WorkingArea;
        var bar = w.Bottom < b.Bottom ? Rectangle.FromLTRB(b.Left, w.Bottom, b.Right, b.Bottom) : Rectangle.FromLTRB(b.Left, b.Top, b.Right, w.Top);
        if (bar.Height == 0) bar = Rectangle.FromLTRB(b.Left, b.Bottom - 48, b.Right, b.Bottom);
        var at = new Native.POINT { X = bar.Left + 8, Y = (bar.Top + bar.Bottom) / 2 };
        string Under() => Native.GetAncestor(WindowFromPoint(at), 2) is var h ? $"{Native.ClassName(h)} \"{Native.WindowText(h)}\"" : "";
        string Front() => Native.ClassName(GetForegroundWindow());
        Log.Write($"full screen: under the taskbar's place before the overview: {Under()}; in front: {Front()}");
        await Swipe(engine, 3, 0, -1, 0.28 * 0.7);
        await Task.Delay(1500);
        Log.Write($"full screen: in the overview: {Under()}; in front: {Front()}");
        engine.Screenshot(Path.Combine(dir, "19-full-screen-overview.png"));
        await Swipe(engine, 3, 0, 1, 0.28 * 0.8);
        await Task.Delay(1500);
        Log.Write($"full screen: after the overview closed: {Under()}; in front: {Front()}");
        form?.Close();
        try { browser?.Kill(entireProcessTree: true); } catch { }
        await Task.Delay(800);
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Native.POINT pt);

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
            int bottom = Screen.PrimaryScreen!.WorkingArea.Bottom;
            for (int y = bottom - 120; y < bottom; y++)
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
            var box = Rectangle.Intersect(new Rectangle(minX - 6, minY - 6, maxX - minX + 13, maxY - minY + 13), new Rectangle(0, 0, b.Width, b.Height));
            using var crop = b.Clone(box, b.PixelFormat);
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

    static Bitmap TaskbarShot(string path)
    {
        var screen = Screen.PrimaryScreen!;
        var b = screen.Bounds; var w = screen.WorkingArea;
        var r = w.Bottom < b.Bottom ? Rectangle.FromLTRB(b.Left, w.Bottom, b.Right, b.Bottom) : Rectangle.FromLTRB(b.Left, b.Top, b.Right, w.Top);
        var bmp = new Bitmap(r.Width, Math.Max(1, r.Height));
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, bmp.Size);
        bmp.Save(path);
        return bmp;
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

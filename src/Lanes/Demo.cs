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
            var lanes = await engine.Ask(() => engine.AddDesktop(LaneMap.Main));
            Guid top = lanes.Lanes[LaneMap.Top].Desktops[0], editor = lanes.Lanes[LaneMap.Main].Desktops[0],
                research = lanes.Lanes[LaneMap.Main].Desktops[1], agent = lanes.Lanes[LaneMap.Bottom].Desktops[0];
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
                await Task.Delay(500);
                if (d == top) engine.Post(() => SwitchIndicator.Describe("500 ms after a switch"));
                // Windows' own "Desktop N" pill stays a while; the picture is taken after it.
                await Task.Delay(2500);
                if (d == top) engine.Post(() => SwitchIndicator.Describe("3 s after a switch"));
                engine.Snapshot();
                await Task.Delay(400);
            }

            // Four fingers down, held at about 40%: the Top lane comes down; then on and let go.
            await Swipe(engine, 4, 0, 1, 0.42 * 0.42, hold: async () => { engine.Screenshot(Path.Combine(dir, "1-vertical-swipe.png")); await Task.Delay(600); }, release: false);
            await Swipe(engine, 4, 0, 1, 0.42 * 0.8, continueFrom: 0.42 * 0.42);
            await Task.Delay(1500);
            // Back down to Main with four fingers up, a short flick.
            await Swipe(engine, 4, 0, -1, 0.42 * 0.3, speed: 1.6);
            await Task.Delay(1500);
            // Four fingers left in Main: Editor to Research, held at about 60%, then let go there.
            await Swipe(engine, 4, -1, 0, 0.42 * 0.58, hold: async () => { engine.Screenshot(Path.Combine(dir, "2-horizontal-swipe.png")); await Task.Delay(600); });
            await Task.Delay(1500);
            // Right, past the end of the lane... first back to Editor, then once more into the stretch.
            await Swipe(engine, 4, 1, 0, 0.42 * 0.8);
            await Task.Delay(1500);
            await Swipe(engine, 4, 1, 0, 0.42 * 0.5, hold: async () => { engine.Screenshot(Path.Combine(dir, "2b-end-of-lane.png")); await Task.Delay(300); });
            await Task.Delay(1500);
            // Three fingers up: the overview, held at 45%, then opened fully.
            await Swipe(engine, 3, 0, -1, 0.28 * 0.45, hold: async () => { engine.Screenshot(Path.Combine(dir, "3-overview-opening.png")); await Task.Delay(600); }, release: false);
            await Swipe(engine, 3, 0, -1, 0.28 * 0.7, continueFrom: 0.28 * 0.45);
            await Task.Delay(1200);
            engine.Screenshot(Path.Combine(dir, "4-overview-open.png"));
            await Task.Delay(800);
            await Swipe(engine, 3, 0, 1, 0.28 * 0.8);
            await Task.Delay(1500);
            engine.Screenshot(Path.Combine(dir, "5-after.png"));
            await Task.Delay(1000);
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
    static async Task Swipe(Engine engine, int fingers, int dx, int dy, double distance, Func<Task>? hold = null, bool release = true, double continueFrom = 0, double speed = 0.6)
    {
        const int stepMs = 8;
        int steps = (int)Math.Ceiling((distance - continueFrom) / (speed * stepMs / 1000.0));
        double Pos(int i) => continueFrom + (distance - continueFrom) * i / Math.Max(1, steps);
        Finger[] At(double t) => Enumerable.Range(0, fingers).Select(f => new Finger((uint)(f + 1), _x0 + 0.08 * f + dx * t, _y0 + 0.02 * (f % 2) + dy * t)).ToArray();
        for (int i = 0; i <= steps; i++)
        {
            engine.Feed(new TouchFrame(0, At(Pos(i))));
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

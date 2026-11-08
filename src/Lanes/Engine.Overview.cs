using System.Runtime.InteropServices;
using Lanes.Core;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using static Lanes.Native;

namespace Lanes;

// The overview on a three-finger swipe up: the lanes at the top, the current desktop's windows
// spread out below. The windows are live DWM thumbnails that travel from where they are to
// their place in the spread as the fingers move.
sealed partial class Engine
{
    sealed record Tile(int Lane, int Index, Guid Desktop, Box Rect, bool Plus);

    sealed class Zoom
    {
        public Guid Target;
        public Box From;
        public Spring Spring = null!;
        public double Start;
        public double Z;
    }

    sealed class OverviewState
    {
        public List<AppWindow> Windows = new();
        public List<Thumbnail> Thumbs = new();
        public List<ID2D1Bitmap1?> Icons = new();
        public Box[] From = Array.Empty<Box>(), To = Array.Empty<Box>();
        public List<Tile> Tiles = new();
        public float LabelX, LabelW;
        public double P;
        public bool Tracking;
        public OverviewTrack Track = null!;
        public Spring? Spring;
        public double SpringStart;
        public Zoom? Zoom;
        public int Hover = -1;
        public Tile? HoverTile;
        public bool Open => !Tracking && Spring == null && Zoom == null && P > 0.99;
    }

    void BeginOverview()
    {
        if (_overview is { } open)
        {
            if (open.Zoom != null) { _ignoring = true; return; }
            open.Tracking = true;
            open.Spring = null;
            open.Track = new OverviewTrack(false, _tuning);
            return;
        }
        if (_switch != null) { _ignoring = true; return; }
        _current = Desktops.Current;
        var snap = TakeTouchCapture();
        RefreshTitles();
        if (snap.Width != _gfx.Width || snap.Height != _gfx.Height) _gfx.Attach(_overlay.Handle, snap.Width, snap.Height);
        Remember(_current, snap);
        var area = _overlay.Area;
        float s = _overlay.Scale;
        var o = new OverviewState { Tracking = true, Track = new OverviewTrack(true, _tuning) };
        o.Windows = AppWindows.OnScreen(area, _overlay.Handle);
        o.From = o.Windows.Select(w => new Box(w.Frame.Left - area.Left, w.Frame.Top - area.Top, w.Frame.Width, w.Frame.Height)).ToArray();
        float spreadTop = LayoutTiles(o);
        var spread = new Box(72 * s, spreadTop, _gfx.Width - 144 * s, _gfx.Height - spreadTop - 32 * s);
        o.To = Spread.Layout(o.From, spread, 32 * s, 52 * s, 200 * s);
        foreach (var w in o.Windows)
        {
            o.Thumbs.Add(new Thumbnail(_overlay.Handle, w.Handle));
            var icon = AppWindows.Icon(w.Handle, (int)(16 * s));
            o.Icons.Add(icon == null ? null : _gfx.Bitmap(icon));
            icon?.Dispose();
        }
        _overview = o;
        RenderOverview();
        _overlay.Show();
        Log.Write($"overview: {o.Windows.Count} windows");
    }

    // Places the lane rows at the top; returns where the window spread may start.
    float LayoutTiles(OverviewState o)
    {
        float s = _overlay.Scale;
        float tw = 184 * s, th = tw * _gfx.Height / _gfx.Width, gap = 16 * s, plusW = 56 * s;
        float rowH = th + 6 * s + 18 * s, rowGap = 16 * s, top = 18 * s, labelW = 56 * s, colGap = 18 * s;
        float widest = _lanes.Lanes.Max(l => l.Desktops.Count * (tw + gap) + plusW);
        float total = labelW + colGap + widest + colGap + labelW;
        float x0 = (_gfx.Width - total) / 2 + labelW + colGap;
        o.Tiles.Clear();
        for (int l = 0; l < _lanes.Lanes.Count; l++)
        {
            float y = top + l * (rowH + rowGap), x = x0;
            var lane = _lanes.Lanes[l];
            for (int i = 0; i < lane.Desktops.Count; i++, x += tw + gap)
                o.Tiles.Add(new Tile(l, i, lane.Desktops[i], new Box(x, y, tw, th), false));
            o.Tiles.Add(new Tile(l, lane.Desktops.Count, Guid.Empty, new Box(x, y, plusW, th), true));
        }
        o.LabelX = x0 - colGap - labelW;
        o.LabelW = labelW;
        return top + 3 * rowH + 2 * rowGap + 40 * s + 28 * s;
    }

    void MoveOverview(double dy)
    {
        var o = _overview!;
        if (!o.Tracking) return;
        o.P = o.Track.ProgressFor(dy);
    }

    void EndOverview(double vy)
    {
        var o = _overview!;
        if (!o.Tracking) return;
        double speed = o.Track.SpeedFor(vy);
        bool open = o.Track.Release(o.P, speed);
        SettleOverview(open ? 1 : 0, speed);
    }

    void SettleOverview(double target, double speed)
    {
        var o = _overview!;
        o.Tracking = false;
        o.Spring = new Spring(o.P, speed, target, _tuning.OverviewResponse);
        o.SpringStart = Now;
    }

    void CloseOverview()
    {
        if (_overview is { Open: true }) SettleOverview(0, 0);
    }

    void StepOverview(double now)
    {
        var o = _overview!;
        if (o.Zoom is { } z)
        {
            double t = now - z.Start;
            z.Z = z.Spring.At(t);
            if (z.Spring.Done(t, 0.002)) FinishZoom();
            return;
        }
        if (o.Spring == null) return;
        double st = now - o.SpringStart;
        o.P = o.Spring.At(st);
        if (!o.Spring.Done(st, 0.002)) return;
        o.P = o.Spring.Target;
        o.Spring = null;
        if (o.P < 0.5) CloseOverviewNow();
        else
        {
            Hook(true);
            _redraw = true;
        }
    }

    void CloseOverviewNow()
    {
        var o = _overview;
        if (o == null) return;
        _overview = null;
        _overlay.Hide();
        Hook(false);
        Log.Write($"overview drawn: {_gfx.FrameStats()}");
        foreach (var t in o.Thumbs) t.Dispose();
        foreach (var i in o.Icons) i?.Dispose();
    }

    void ZoomInto(Guid target, Box from)
    {
        var o = _overview!;
        Hook(false);
        o.Zoom = new Zoom { Target = target, From = from, Spring = new Spring(0, 0, 1, _tuning.OverviewResponse), Start = Now };
    }

    void FinishZoom()
    {
        var z = _overview!.Zoom!;
        z.Z = 1;
        RenderOverview();
        if (z.Target != _current)
        {
            Desktops.Switch(z.Target);
            _current = z.Target;
            _lanes.Visited(z.Target);
            Settings.SaveLanes(_lanes);
            DwmFlush();
            DwmFlush();
        }
        CloseOverviewNow();
    }

    // ---------- pointer and keys ----------

    void OnMouseMove(int x, int y)
    {
        var o = _overview;
        if (o is not { Open: true }) return;
        int hover = HitWindow(o, x, y);
        var tile = hover >= 0 ? null : HitTile(o, x, y);
        if (hover != o.Hover || tile != o.HoverTile)
        {
            o.Hover = hover;
            o.HoverTile = tile;
            _redraw = true;
        }
    }

    void OnLeftClick(int x, int y)
    {
        var o = _overview;
        if (o is not { Open: true }) return;
        int w = HitWindow(o, x, y);
        if (w >= 0)
        {
            var hwnd = o.Windows[w].Handle;
            AppWindows.Activate(hwnd);
            // Its picture goes in front of the others, as the window itself just did.
            o.Thumbs[w].Dispose();
            o.Thumbs[w] = new Thumbnail(_overlay.Handle, hwnd);
            CloseOverview();
            return;
        }
        if (HitTile(o, x, y) is { } tile)
        {
            if (tile.Plus)
            {
                var made = Desktops.Create();
                if (made == Guid.Empty) return;
                _lanes.Add(made, tile.Lane);
                SyncOrder();
                _lanes.Reconcile(Desktops.All(), _current);
                Settings.SaveLanes(_lanes);
                ZoomInto(made, tile.Rect with { W = tile.Rect.H * _gfx.Width / _gfx.Height });
            }
            else if (tile.Desktop == _current) CloseOverview();
            else ZoomInto(tile.Desktop, tile.Rect);
            return;
        }
        CloseOverview();
    }

    void OnRightClick(int x, int y)
    {
        var o = _overview;
        if (o is not { Open: true } || HitTile(o, x, y) is not { Plus: false } tile) return;
        var lane = _lanes.Lanes[tile.Lane];
        IntPtr menu = CreatePopupMenu();
        for (int l = 0; l < 3; l++)
            if (l != tile.Lane) AppendMenu(menu, 0, (UIntPtr)(1 + l), $"Move to {LaneMap.Names[l]} lane");
        AppendMenu(menu, tile.Index > 0 ? 0u : 1u, (UIntPtr)10, "Move left");
        AppendMenu(menu, tile.Index < lane.Desktops.Count - 1 ? 0u : 1u, (UIntPtr)11, "Move right");
        AppendMenu(menu, 0x800, UIntPtr.Zero, null);
        AppendMenu(menu, Desktops.Count > 1 && tile.Desktop != _current ? 0u : 1u, (UIntPtr)20, "Close desktop");
        SetForegroundWindow(_overlay.Handle);
        GetCursorPos(out var pt);
        int cmd = TrackPopupMenuEx(menu, 0x0100 /* TPM_RETURNCMD */, pt.X, pt.Y, _overlay.Handle, IntPtr.Zero);
        DestroyMenu(menu);
        switch (cmd)
        {
            case 1 or 2 or 3: _lanes.Add(tile.Desktop, cmd - 1); break;
            case 10: _lanes.Add(tile.Desktop, tile.Lane, tile.Index - 1); break;
            case 11: _lanes.Add(tile.Desktop, tile.Lane, tile.Index + 1); break;
            case 20:
                var fallback = _lanes.Neighbor(tile.Desktop, Direction.Left) ?? _lanes.Neighbor(tile.Desktop, Direction.Right) ?? _current;
                Desktops.Remove(tile.Desktop, fallback);
                break;
            default: return;
        }
        SyncOrder();
        _lanes.Reconcile(Desktops.All(), _current);
        Settings.SaveLanes(_lanes);
        LayoutTiles(o);
        o.HoverTile = null;
        _redraw = true;
    }

    static int HitWindow(OverviewState o, int x, int y)
    {
        for (int i = o.To.Length - 1; i >= 0; i--)
        {
            var b = o.To[i];
            if (x >= b.X && x < b.Right && y >= b.Y && y < b.Bottom) return i;
        }
        return -1;
    }

    static Tile? HitTile(OverviewState o, int x, int y) =>
        o.Tiles.FirstOrDefault(t => x >= t.Rect.X && x < t.Rect.Right && y >= t.Rect.Y && y < t.Rect.Bottom);

    IntPtr _hook;
    HookProc? _hookProc;

    void Hook(bool on)
    {
        if (on && _hook == IntPtr.Zero)
        {
            _hookProc = KeyHook;
            _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, GetModuleHandle(null), 0);
        }
        else if (!on && _hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == (IntPtr)WM_KEYDOWN && Marshal.ReadInt32(lParam) == 0x1B)
        {
            CloseOverview();
            return 1;
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    // ---------- drawing ----------

    void RenderOverview()
    {
        var o = _overview!;
        var g = _gfx;
        float s = _overlay.Scale;
        float p = (float)o.P, pc = Math.Clamp(p, 0, 1);
        float z = (float)(o.Zoom?.Z ?? 0), keep = 1 - Math.Clamp(z, 0, 1);
        g.Begin(Gfx.Color(0xFF000000));

        if (Wallpaper(_current) is var (sharp, blurred))
        {
            g.Image(sharp, 0, 0, g.Width, g.Height);
            g.Image(blurred, 0, 0, g.Width, g.Height, pc);
        }
        g.Rect(0, 0, g.Width, g.Height, A(0xFF000000, 0.4f * pc));

        // Lane rows.
        float sp = Math.Clamp(p / 0.6f, 0, 1), sq = 1 - MathF.Pow(1 - sp, 3);
        float a = sq * keep, dy = (1 - sq) * -40 * s;
        if (a > 0.002f)
        {
            var labelFont = g.Font(11 * s, FontWeight.SemiBold);
            var capFont = g.Font(14 * s, FontWeight.Normal, centred: true);
            for (int l = 0; l < _lanes.Lanes.Count; l++)
            {
                var first = o.Tiles.First(t => t.Lane == l);
                g.Dc.DrawText(LaneMap.Names[l].ToUpperInvariant(), labelFont,
                    new Vortice.Mathematics.Rect(o.LabelX, (float)first.Rect.Y + dy, o.LabelW, (float)first.Rect.H), g.Brush(A(0xFF8A8F98, a)), DrawTextOptions.Clip);
            }
            foreach (var t in o.Tiles)
            {
                float x = (float)t.Rect.X, y = (float)t.Rect.Y + dy, w = (float)t.Rect.W, h = (float)t.Rect.H;
                bool hover = o.HoverTile == t && o.Open;
                if (t.Plus)
                {
                    g.RoundRect(x, y, w, h, 8 * s, A(hover ? 0x14FFFFFFu : 0x0AFFFFFFu, a));
                    g.RoundRectOutline(x + 0.5f, y + 0.5f, w - 1, h - 1, 8 * s, A(0x1FFFFFFF, a), 1);
                    float cx = x + w / 2, cy = y + h / 2, arm = 7 * s;
                    g.Rect(cx - arm, cy - 0.8f * s, 2 * arm, 1.6f * s, A(0xFF9AA0A6, a));
                    g.Rect(cx - 0.8f * s, cy - arm, 1.6f * s, 2 * arm, A(0xFF9AA0A6, a));
                    continue;
                }
                g.Shadow(x, y, w, h, 8 * s, 18 * s, 6 * s, 0.4f * a);
                if (Picture(t.Desktop) is { } pic) g.RoundImage(pic, x, y, w, h, 8 * s, a);
                g.RoundRectOutline(x + 0.5f, y + 0.5f, w - 1, h - 1, 8 * s, A(0x1FFFFFFF, a), 1);
                if (t.Desktop == _current) g.RoundRectOutline(x - 4 * s, y - 4 * s, w + 8 * s, h + 8 * s, 11 * s, A(0xFF4CC2FF, a), 2 * s);
                else if (hover) g.RoundRectOutline(x - 4 * s, y - 4 * s, w + 8 * s, h + 8 * s, 11 * s, A(0xD9FFFFFF, a), 2 * s);
                string title = Title(t.Desktop);
                bool remembered = _lanes.CurrentOf(t.Lane) == t.Desktop;
                float tw = Math.Min(g.TextWidth(title, capFont), w + 16 * s);
                float tx = x + (w - tw) / 2 - (remembered ? 5 * s : 0), ty = y + h + 6 * s;
                g.Text(title, capFont, tx, ty, tw, 18 * s, A(0xFFE6E6E6, a));
                if (remembered) g.RoundRect(tx + tw + 6 * s, ty + 9 * s - 2 * s, 4 * s, 4 * s, 2 * s, A(0xFF4CC2FF, a));
            }
        }

        // Windows, with their names once they have nearly arrived.
        float la = Math.Clamp((p - 0.7f) / 0.3f, 0, 1) * keep;
        var nameFont = g.Font(15 * s);
        for (int i = 0; i < o.Thumbs.Count; i++)
        {
            var b = o.From[i].Lerp(o.To[i], Math.Clamp(p, -0.05, 1.08));
            o.Thumbs[i].Place((float)b.X, (float)b.Y, (float)b.W, (float)b.H, keep);
            if (la > 0.002f)
            {
                string title = o.Windows[i].Title;
                float icon = 16 * s, gap = 8 * s;
                float tw = Math.Min(g.TextWidth(title, nameFont), (float)b.W - icon - gap);
                float x = (float)(b.X + (b.W - (icon + gap + tw)) / 2), y = (float)b.Y - 28 * s;
                if (o.Icons[i] is { } ic) g.Image(ic, x, y, icon, icon, la);
                g.Text(title, nameFont, x + icon + gap, y - 2 * s, tw, 20 * s, A(0xFFFFFFFF, la));
            }
            if (o.Hover == i && o.Open)
                g.RoundRectOutline((float)b.X - 5 * s, (float)b.Y - 5 * s, (float)b.W + 10 * s, (float)b.H + 10 * s, 11 * s, 0xD9FFFFFF, 2 * s);
        }

        if (o.Zoom is { } zoom && Picture(zoom.Target) is { } target)
        {
            var b = zoom.From.Lerp(new Box(0, 0, g.Width, g.Height), Math.Clamp(z, 0, 1));
            g.RoundImage(target, (float)b.X, (float)b.Y, (float)b.W, (float)b.H, 8 * s * keep, 1);
        }
        g.End();
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
}

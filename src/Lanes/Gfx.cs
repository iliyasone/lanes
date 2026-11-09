using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using D2DAlpha = Vortice.DCommon.AlphaMode;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;

namespace Lanes;

// Direct2D on a flip-model swap chain: every frame is drawn on the GPU and shown at the next
// vertical blank, with at most one frame queued so the picture is never behind the fingers.
sealed class Gfx : IDisposable
{
    public readonly ID3D11Device Device;
    readonly IDXGIFactory2 _factory;
    readonly ID2D1Factory1 _d2dFactory;
    readonly ID2D1Device _d2dDevice;
    public readonly ID2D1DeviceContext Dc;
    public readonly IDWriteFactory Write;
    IDXGISwapChain1? _swap;
    IntPtr _waitable;
    ID2D1Bitmap1? _target;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public readonly bool Software;
    readonly Dictionary<(float, FontWeight, bool, string?), IDWriteTextFormat> _formats = new();
    readonly Dictionary<uint, ID2D1SolidColorBrush> _brushes = new();

    public Gfx()
    {
        Vortice.Direct3D.FeatureLevel[] levels = { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0, Vortice.Direct3D.FeatureLevel.Level_10_1, Vortice.Direct3D.FeatureLevel.Level_10_0 };
        if (D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var device).Failure || device == null)
        {
            D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device).CheckError();
            Software = true;
        }
        Device = device!;
        using var dxgiDevice = Device.QueryInterface<IDXGIDevice1>();
        dxgiDevice.MaximumFrameLatency = 1;
        using var adapter = dxgiDevice.GetAdapter();
        _factory = adapter.GetParent<IDXGIFactory2>();
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        Dc = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        Dc.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        Write = DWrite.DWriteCreateFactory<IDWriteFactory>();
        Log.Write($"graphics: {adapter.Description.Description}{(Software ? " (software)" : "")}");
    }

    public void Attach(IntPtr hwnd, int width, int height)
    {
        if (_swap != null && width == Width && height == Height) return;
        Dc.Target = null;
        _target?.Dispose();
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        if (_swap == null)
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)Width,
                Height = (uint)Height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
                Scaling = Scaling.Stretch,
                Flags = SwapChainFlags.FrameLatencyWaitableObject,
            };
            _swap = _factory.CreateSwapChainForHwnd(Device, hwnd, desc, null, null);
            _factory.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
            using var swap2 = _swap.QueryInterface<IDXGISwapChain2>();
            swap2.MaximumFrameLatency = 1;
            _waitable = swap2.FrameLatencyWaitableObject;
        }
        else _swap.ResizeBuffers(2, (uint)Width, (uint)Height, Format.B8G8R8A8_UNorm, SwapChainFlags.FrameLatencyWaitableObject).CheckError();
        using var surface = _swap.GetBuffer<IDXGISurface>(0);
        _target = Dc.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        Dc.Target = _target;
    }

    // Waits until the swap chain can take a frame, so input read after this is as fresh as possible.
    public void WaitForFrame()
    {
        if (_waitable != IntPtr.Zero) WaitForSingleObjectEx(_waitable, 100, true);
    }

    public void Begin(Color4 clear)
    {
        Dc.BeginDraw();
        Dc.Transform = Matrix3x2.Identity;
        Dc.Clear(clear);
    }

    public void End()
    {
        Dc.EndDraw();
        _swap!.Present(1, PresentFlags.None);
        Frames.Add(System.Diagnostics.Stopwatch.GetTimestamp());
    }

    // When each frame was handed over, for the timing line in the log after each animation.
    public readonly List<long> Frames = new();

    public string FrameStats()
    {
        if (Frames.Count < 3) { Frames.Clear(); return "no frames"; }
        var ms = Frames.Zip(Frames.Skip(1), (a, b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).OrderBy(x => x).ToList();
        Frames.Clear();
        return $"{ms.Count + 1} frames, median {ms[ms.Count / 2]:0.0} ms, 95% under {ms[(int)(ms.Count * 0.95)]:0.0} ms, longest {ms[^1]:0.0} ms";
    }

    public ID2D1Bitmap1 Bitmap(Pixels p) =>
        Dc.CreateBitmap(new SizeI(p.Width, p.Height), p.Data, (uint)(p.Width * 4),
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, p.Opaque ? D2DAlpha.Ignore : D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));

    // Only the part of the pixels inside `crop` (clamped to them).
    public ID2D1Bitmap1 Bitmap(Pixels p, Native.RECT crop)
    {
        int l = Math.Clamp(crop.Left, 0, p.Width - 1), t = Math.Clamp(crop.Top, 0, p.Height - 1);
        int r = Math.Clamp(crop.Right, l + 1, p.Width), b = Math.Clamp(crop.Bottom, t + 1, p.Height);
        return Dc.CreateBitmap(new SizeI(r - l, b - t), p.Data + (nint)(((long)t * p.Width + l) * 4), (uint)(p.Width * 4),
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, p.Opaque ? D2DAlpha.Ignore : D2DAlpha.Premultiplied), 96, 96, BitmapOptions.None));
    }

    // A smaller copy made once with high-quality filtering, so small pictures cost nothing per frame.
    // Call between frames only.
    public ID2D1Bitmap1 Scaled(ID2D1Bitmap1 src, int w, int h, float radius = 0)
    {
        var bmp = Dc.CreateBitmap(new SizeI(Math.Max(1, w), Math.Max(1, h)), IntPtr.Zero, 0,
            new BitmapProperties1(new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied), 96, 96, BitmapOptions.Target));
        Dc.Target = bmp;
        Dc.BeginDraw();
        Dc.Clear(new Color4(0, 0, 0, 0));
        RoundImage(src, 0, 0, w, h, radius, 1);
        Dc.EndDraw();
        Dc.Target = _target;
        return bmp;
    }

    // Draws into memory instead of the screen: `draw` uses the overlay's coordinates, and the
    // pixels returned are the w x h of them from (x, y), premultiplied. Call between frames only.
    (int W, int H, ID2D1Bitmap1 Target, ID2D1Bitmap1 Cpu)? _offscreen;

    public Pixels Offscreen(int x, int y, int w, int h, Action draw)
    {
        var format = new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied);
        // Kept for the next call: Lanes' pill is drawn this way on every frame of a swipe.
        if (_offscreen is not { } off || off.W != w || off.H != h)
        {
            _offscreen?.Target.Dispose();
            _offscreen?.Cpu.Dispose();
            _offscreen = off = (w, h,
                Dc.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.Target)),
                Dc.CreateBitmap(new SizeI(w, h), IntPtr.Zero, 0, new BitmapProperties1(format, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw)));
        }
        var (bmp, cpu) = (off.Target, off.Cpu);
        Dc.Target = bmp;
        Dc.BeginDraw();
        Dc.Transform = Matrix3x2.CreateTranslation(-x, -y);
        Dc.Clear(new Color4(0, 0, 0, 0));
        draw();
        Dc.EndDraw();
        Dc.Transform = Matrix3x2.Identity;
        Dc.Target = _target;
        cpu.CopyFromBitmap(bmp);
        var map = cpu.Map(MapOptions.Read);
        var p = new Pixels(w, h, opaque: false);
        unsafe
        {
            for (int row = 0; row < h; row++)
                Buffer.MemoryCopy((void*)(map.Bits + row * map.Pitch), (void*)(p.Data + row * w * 4), w * 4, w * 4);
        }
        cpu.Unmap();
        return p;
    }

    public ID2D1SolidColorBrush Brush(uint argb)
    {
        if (!_brushes.TryGetValue(argb, out var b))
            _brushes[argb] = b = Dc.CreateSolidColorBrush(Color(argb));
        return b;
    }

    public static Color4 Color(uint argb) =>
        new(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, ((argb >> 24) & 0xFF) / 255f);

    public IDWriteTextFormat Font(float size, FontWeight weight = FontWeight.Normal, bool centred = false, string? family = null)
    {
        if (_formats.TryGetValue((size, weight, centred, family), out var f)) return f;
        string name = family ?? (size >= 14 ? "Segoe UI Variable Display" : "Segoe UI Variable Text");
        try { f = Write.CreateTextFormat(name, null, weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size, "en-us"); }
        catch { f = Write.CreateTextFormat("Segoe UI", null, weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size, "en-us"); }
        f.WordWrapping = WordWrapping.NoWrap;
        f.ParagraphAlignment = ParagraphAlignment.Center;
        // Text too long for its place ends in "…".
        using (var sign = Write.CreateEllipsisTrimmingSign(f))
            f.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, sign);
        if (centred) f.TextAlignment = TextAlignment.Center;
        return _formats[(size, weight, centred, family)] = f;
    }

    readonly Dictionary<(string, IDWriteTextFormat), float> _widths = new();

    public float TextWidth(string text, IDWriteTextFormat format)
    {
        if (_widths.TryGetValue((text, format), out float w)) return w;
        if (_widths.Count > 2000) _widths.Clear();
        using var layout = Write.CreateTextLayout(text, format, 4000, 200);
        return _widths[(text, format)] = layout.Metrics.WidthIncludingTrailingWhitespace;
    }

    // A pixel to spare: text given exactly its own width would sometimes end in "…" as it moves,
    // the box's width coming out a hair shorter at some places on the screen.
    public void Text(string text, IDWriteTextFormat format, float x, float y, float w, float h, uint argb) =>
        Dc.DrawText(text, format, new Rect(x - 0.5f, y, w + 1, h), Brush(argb), DrawTextOptions.Clip);

    public void RoundRect(float x, float y, float w, float h, float r, uint argb) =>
        Dc.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), Brush(argb));

    public void RoundRectOutline(float x, float y, float w, float h, float r, uint argb, float stroke) =>
        Dc.DrawRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), Brush(argb), stroke);

    ID2D1StrokeStyle? _dashes;

    public void DashedRoundRectOutline(float x, float y, float w, float h, float r, uint argb, float stroke)
    {
        _dashes ??= _d2dFactory.CreateStrokeStyle(new StrokeStyleProperties { DashStyle = DashStyle.Dash, DashCap = CapStyle.Round });
        Dc.DrawRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), Brush(argb), stroke, _dashes);
    }

    public void Rect(float x, float y, float w, float h, uint argb) => Dc.FillRectangle(new Vortice.RawRectF(x, y, x + w, y + h), Brush(argb));

    // A soft shadow made of stacked translucent rounded rectangles; cheap and good enough at UI sizes.
    public void Shadow(float x, float y, float w, float h, float r, float blur, float dy, float alpha)
    {
        const int steps = 8;
        for (int i = steps; i >= 1; i--)
        {
            float g = blur * i / steps;
            float a = alpha / steps * 1.6f * (1 - (float)(i - 1) / steps);
            uint argb = (uint)(Math.Clamp(a, 0, 1) * 255) << 24;
            RoundRect(x - g / 2, y + dy - g / 2, w + g, h + g, r + g / 2, argb);
        }
    }

    // A window's shadow, cheap at any size: the shadow of a rounded square with corners of
    // radius `r`, blurred once (PrepareShadow, outside a frame) as light spreads, a Gaussian of
    // width `sigma`, is cut into corners and sides that are stretched along the window's edges;
    // the middle, under the window, is left out. `grow` draws it larger (a wider, softer shadow).
    readonly Dictionary<(int R, int Sigma), ID2D1Bitmap1> _shadows = new();

    public void PrepareShadow(float r, float sigma)
    {
        var key = ((int)MathF.Ceiling(r), Math.Max(1, (int)MathF.Ceiling(sigma)));
        if (_shadows.ContainsKey(key)) return;
        var (rr, sg) = key;
        int m = 3 * sg, c = 2 * rr + 2 * m + 4, t = c + 2 * m;
        var a = new float[t * t];
        // The square, its corners rounded and their edges smoothed.
        for (int y = 0; y < c; y++)
            for (int x = 0; x < c; x++)
            {
                float dx = Math.Max(0, Math.Max(rr - x - 0.5f, x + 0.5f - (c - rr))), dy = Math.Max(0, Math.Max(rr - y - 0.5f, y + 0.5f - (c - rr)));
                a[(y + m) * t + x + m] = Math.Clamp(rr + 0.5f - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
            }
        // Three box blurs one after another come within a hair of a Gaussian.
        var tmp = new float[t * t];
        foreach (int box in Boxes(sg))
        {
            Blur(a, tmp, t, (box - 1) / 2, true);
            Blur(tmp, a, t, (box - 1) / 2, false);
        }
        using var p = new Pixels(t, t, opaque: false);
        unsafe
        {
            var px = (uint*)p.Data;
            for (int i = 0; i < t * t; i++) px[i] = (uint)Math.Clamp(a[i] * 255 + 0.5f, 0, 255) << 24;
        }
        _shadows[key] = Bitmap(p);
    }

    static int[] Boxes(double sigma)
    {
        double ideal = Math.Sqrt(12 * sigma * sigma / 3 + 1);
        int wl = (int)Math.Floor(ideal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        int mm = (int)Math.Round((12 * sigma * sigma - 3 * wl * wl - 12 * wl - 9) / (-4.0 * wl - 4));
        return new[] { 0, 1, 2 }.Select(i => i < mm ? wl : wu).ToArray();
    }

    // A running average over 2r+1 pixels along rows (or columns) of a t x t square; outside it is empty.
    static void Blur(float[] src, float[] dst, int t, int r, bool rows)
    {
        float k = 1f / (2 * r + 1);
        for (int line = 0; line < t; line++)
        {
            float sum = 0;
            int At(int i) => rows ? line * t + i : i * t + line;
            for (int i = 0; i < Math.Min(r, t); i++) sum += src[At(i)];
            for (int i = 0; i < t; i++)
            {
                if (i + r < t) sum += src[At(i + r)];
                dst[At(i)] = sum * k;
                if (i - r >= 0) sum -= src[At(i - r)];
            }
        }
    }

    public void WindowShadow(float x, float y, float w, float h, float r, float sigma, float dy, float alpha, float grow = 1)
    {
        var key = ((int)MathF.Ceiling(r), Math.Max(1, (int)MathF.Ceiling(sigma)));
        if (!_shadows.TryGetValue(key, out var bmp)) return;
        float t = bmp.PixelSize.Width, m = 3 * key.Item2, k = 2 * m + key.Item1;
        float mg = m * grow, x0 = x - mg, x1 = x + w + mg, y0 = y + dy - mg, y1 = y + h + dy + mg;
        // A window smaller than the corners squeezes them.
        float kg = Math.Min(k * grow, Math.Min(x1 - x0, y1 - y0) / 2);
        float[] sx = { 0, k, t - k, t }, sy = sx, dx = { x0, x0 + kg, x1 - kg, x1 }, dyy = { y0, y0 + kg, y1 - kg, y1 };
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                if (i == 1 && j == 1) continue;
                if (dx[i + 1] - dx[i] < 0.01f || dyy[j + 1] - dyy[j] < 0.01f) continue;
                Image(bmp, dx[i], dyy[j], dx[i + 1] - dx[i], dyy[j + 1] - dyy[j], new Vortice.RawRectF(sx[i], sy[j], sx[i + 1], sy[j + 1]), alpha);
            }
    }

    public void Image(ID2D1Bitmap1 bmp, float x, float y, float w, float h, float opacity = 1) =>
        Dc.DrawBitmap(bmp, new Vortice.RawRectF(x, y, x + w, y + h), opacity, Vortice.Direct2D1.InterpolationMode.Linear, null, null);

    public void Image(ID2D1Bitmap1 bmp, float x, float y, float w, float h, Vortice.RawRectF source, float opacity = 1) =>
        Dc.DrawBitmap(bmp, new Vortice.RawRectF(x, y, x + w, y + h), opacity, Vortice.Direct2D1.InterpolationMode.Linear, source, null);

    // A picture with rounded corners, scaled with high-quality filtering.
    public void RoundImage(ID2D1Bitmap1 bmp, float x, float y, float w, float h, float r, float opacity)
    {
        if (r < 0.5f)
        {
            Dc.DrawBitmap(bmp, new Vortice.RawRectF(x, y, x + w, y + h), opacity, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, null, null);
            return;
        }
        using var mask = _d2dFactory.CreateRoundedRectangleGeometry(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r));
        Dc.PushLayer(new LayerParameters1
        {
            ContentBounds = new Vortice.RawRectF(x, y, x + w, y + h),
            GeometricMask = mask,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = opacity,
            LayerOptions = LayerOptions1.None,
        }, null);
        Dc.DrawBitmap(bmp, new Vortice.RawRectF(x, y, x + w, y + h), 1, Vortice.Direct2D1.InterpolationMode.HighQualityCubic, null, null);
        Dc.PopLayer();
    }

    public void Clip(float x, float y, float w, float h) => Dc.PushAxisAlignedClip(new Vortice.RawRectF(x, y, x + w, y + h), AntialiasMode.Aliased);
    public void Unclip() => Dc.PopAxisAlignedClip();

    public void Dispose()
    {
        foreach (var b in _brushes.Values) b.Dispose();
        foreach (var f in _formats.Values) f.Dispose();
        _target?.Dispose();
        _swap?.Dispose();
        Dc.Dispose();
        _d2dDevice.Dispose();
        _d2dFactory.Dispose();
        _factory.Dispose();
        Write.Dispose();
        Device.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint WaitForSingleObjectEx(IntPtr handle, uint ms, bool alertable);
}

// A picture in memory: 32-bit BGRA, premultiplied (opaque here), top row first.
sealed class Pixels : IDisposable
{
    public readonly int Width, Height;
    public readonly IntPtr Data;
    public readonly bool Opaque;

    public Pixels(int width, int height, bool opaque = true)
    {
        Width = width;
        Height = height;
        Opaque = opaque;
        Data = System.Runtime.InteropServices.Marshal.AllocHGlobal(width * height * 4);
    }

    public Pixels Copy()
    {
        var p = new Pixels(Width, Height, Opaque);
        unsafe { Buffer.MemoryCopy((void*)Data, (void*)p.Data, (long)Width * Height * 4, (long)Width * Height * 4); }
        return p;
    }

    // The part `r` of a picture of the same size, copied in.
    public void Paste(Pixels from, Native.RECT r)
    {
        if (from.Width != Width || from.Height != Height) return;
        int l = Math.Max(0, r.Left), t = Math.Max(0, r.Top), right = Math.Min(Width, r.Right), b = Math.Min(Height, r.Bottom);
        if (right <= l) return;
        unsafe
        {
            for (int y = t; y < b; y++)
                Buffer.MemoryCopy((byte*)from.Data + ((long)y * Width + l) * 4, (byte*)Data + ((long)y * Width + l) * 4, (right - l) * 4, (right - l) * 4);
        }
    }

    public void Fill(Native.RECT r, uint argb)
    {
        int l = Math.Max(0, r.Left), t = Math.Max(0, r.Top), right = Math.Min(Width, r.Right), b = Math.Min(Height, r.Bottom);
        unsafe
        {
            for (int y = t; y < b; y++)
            {
                uint* row = (uint*)Data + (long)y * Width;
                for (int x = l; x < right; x++) row[x] = argb;
            }
        }
    }

    // The colour most of this rectangle has (a bar's own colour, under what is on it).
    public uint Plain(Native.RECT r)
    {
        var count = new Dictionary<uint, (int N, long B, long G, long R)>();
        int l = Math.Max(0, r.Left), t = Math.Max(0, r.Top), right = Math.Min(Width, r.Right), b = Math.Min(Height, r.Bottom);
        unsafe
        {
            for (int y = t + 1; y < b - 1; y += 3)
                for (int x = l + 1; x < right - 1; x += 3)
                {
                    uint c = ((uint*)Data)[(long)y * Width + x];
                    // Nearly equal colours count as one (a bar can have a faint texture).
                    uint key = c & 0xF8F8F8;
                    count.TryGetValue(key, out var n);
                    count[key] = (n.N + 1, n.B + (c & 0xFF), n.G + ((c >> 8) & 0xFF), n.R + ((c >> 16) & 0xFF));
                }
        }
        if (count.Count == 0) return 0xFF202020;
        var best = count.Values.MaxBy(v => v.N);
        return 0xFF000000 | (uint)(best.R / best.N) << 16 | (uint)(best.G / best.N) << 8 | (uint)(best.B / best.N);
    }

    public void Dispose() => System.Runtime.InteropServices.Marshal.FreeHGlobal(Data);
}

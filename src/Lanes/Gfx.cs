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
    readonly Dictionary<(float, FontWeight, bool), IDWriteTextFormat> _formats = new();
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

    public ID2D1SolidColorBrush Brush(uint argb)
    {
        if (!_brushes.TryGetValue(argb, out var b))
            _brushes[argb] = b = Dc.CreateSolidColorBrush(Color(argb));
        return b;
    }

    public static Color4 Color(uint argb) =>
        new(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, ((argb >> 24) & 0xFF) / 255f);

    public IDWriteTextFormat Font(float size, FontWeight weight = FontWeight.Normal, bool centred = false)
    {
        if (_formats.TryGetValue((size, weight, centred), out var f)) return f;
        string family = size >= 14 ? "Segoe UI Variable Display" : "Segoe UI Variable Text";
        try { f = Write.CreateTextFormat(family, null, weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size, "en-us"); }
        catch { f = Write.CreateTextFormat("Segoe UI", null, weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size, "en-us"); }
        f.WordWrapping = WordWrapping.NoWrap;
        f.ParagraphAlignment = ParagraphAlignment.Center;
        // Text too long for its place ends in "…".
        using (var sign = Write.CreateEllipsisTrimmingSign(f))
            f.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, sign);
        if (centred) f.TextAlignment = TextAlignment.Center;
        return _formats[(size, weight, centred)] = f;
    }

    readonly Dictionary<(string, IDWriteTextFormat), float> _widths = new();

    public float TextWidth(string text, IDWriteTextFormat format)
    {
        if (_widths.TryGetValue((text, format), out float w)) return w;
        if (_widths.Count > 2000) _widths.Clear();
        using var layout = Write.CreateTextLayout(text, format, 4000, 200);
        return _widths[(text, format)] = layout.Metrics.WidthIncludingTrailingWhitespace;
    }

    public void Text(string text, IDWriteTextFormat format, float x, float y, float w, float h, uint argb) =>
        Dc.DrawText(text, format, new Rect(x, y, w, h), Brush(argb), DrawTextOptions.Clip);

    public void RoundRect(float x, float y, float w, float h, float r, uint argb) =>
        Dc.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), Brush(argb));

    public void RoundRectOutline(float x, float y, float w, float h, float r, uint argb, float stroke) =>
        Dc.DrawRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), Brush(argb), stroke);

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

    // A strip fading from a colour to transparent, for the shadow on a moving desktop's edge.
    public void Fade(float x, float y, float w, float h, bool horizontal, bool toEnd, float alpha)
    {
        var stops = new[] { new GradientStop(0, new Color4(0, 0, 0, toEnd ? alpha : 0)), new GradientStop(1, new Color4(0, 0, 0, toEnd ? 0 : alpha)) };
        using var coll = Dc.CreateGradientStopCollection(stops, ExtendMode.Clamp);
        var start = new Vector2(x, y);
        var end = horizontal ? new Vector2(x + w, y) : new Vector2(x, y + h);
        using var brush = Dc.CreateLinearGradientBrush(new LinearGradientBrushProperties(start, end), coll);
        Dc.FillRectangle(new Vortice.RawRectF(x, y, x + w, y + h), brush);
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

    public void Dispose() => System.Runtime.InteropServices.Marshal.FreeHGlobal(Data);
}

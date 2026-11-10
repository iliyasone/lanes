using System.Runtime.InteropServices;
using Lanes.Core;
using static Lanes.Native;

namespace Lanes;

// The overview's window shadows are live pictures, as the windows are, not drawn by Lanes:
// everything Lanes draws lies under every live picture (see Thumbnail), so a shadow it drew
// would show on the desktop but not on a window behind. This small window of Lanes', cloaked
// and never seen, holds the shadow (ShadowLook) of a window just big enough for its corners, a
// little darker than it is. A window's shadow is eight pieces of it round the window (corners as
// they are, sides stretched along the window's), each a live picture registered just before the
// window's own: over the windows behind it, under the window.
sealed class ShadowSprite : NativeWindow, IDisposable
{
    // How much darker the picture is than the shadow, so a lifted window's can be darker.
    public const float Strength = 1.6f;

    public readonly double Scale;
    // From a corner of the window the picture is the shadow of: how far the shadow reaches past
    // its sides, above and below it, and how far in from each side it is not the same all along.
    readonly int _side, _over, _below, _in, _inTop, _inBottom, _w, _h;
    // The eight pieces, on the screen (where this window is).
    public readonly RECT[] Pieces = new RECT[8];

    public ShadowSprite(double scale)
    {
        Scale = scale;
        var look = new ShadowLook(scale);
        int r = look.Radius;
        _side = look.Side;
        _over = look.Top;
        _below = look.Bottom;
        _in = Math.Max(look.Soft.Margin, look.Tight.Margin) + r;
        _inTop = Math.Max(look.Soft.Margin + look.SoftDy, look.Tight.Margin + look.TightDy) + r;
        _inBottom = Math.Max(r, Math.Max(look.Soft.Margin - look.SoftDy, look.Tight.Margin - look.TightDy) + r);
        // A few pixels between the corners where the shadow is the same all along.
        _w = 2 * _in + 8;
        _h = _inTop + _inBottom + 8;
        int pw = _side + _w + _side, ph = _over + _h + _below;
        var frame = new PixelRect(_side, _over, _w, _h);
        var shade = new uint[pw * ph];
        look.Draw(shade, pw, new PixelRect(0, 0, pw, ph), frame);
        for (int i = 0; i < shade.Length; i++) shade[i] = (uint)Math.Min(255, (int)((shade[i] >> 24) * Strength + 0.5f)) << 24;

        CreateHandle(new CreateParams
        {
            Caption = "Lanes shadow picture",
            Style = unchecked((int)WS_POPUP),
            ExStyle = (int)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
            Width = 1,
            Height = 1,
        });
        int cloak = 1;
        DwmSetWindowAttribute(Handle, 13 /* DWMWA_CLOAK */, ref cloak, 4);
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = pw, Height = -ph, Planes = 1, BitCount = 32 };
        IntPtr dib = CreateDIBSection(mem, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib != IntPtr.Zero)
        {
            Marshal.Copy(Array.ConvertAll(shade, v => unchecked((int)v)), 0, bits, shade.Length);
            IntPtr old = SelectObject(mem, dib);
            var at = new POINT();
            var size = new SIZE { Cx = pw, Cy = ph };
            var from = new POINT();
            var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(Handle, screen, ref at, ref size, mem, ref from, 0, ref blend, 2 /* ULW_ALPHA */);
            SelectObject(mem, old);
            DeleteObject(dib);
        }
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);

        // Across: the left corners, a stretch of the middle, the right corners; down the same.
        int[] xs = { 0, _side + _in, _side + _in + 2, _side + _in + 6, _side + _w - _in, pw };
        int[] ys = { 0, _over + _inTop, _over + _inTop + 2, _over + _inTop + 6, _over + _h - _inBottom, ph };
        int k = 0;
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
            {
                if (i == 1 && j == 1) continue;
                int c = i == 0 ? 0 : i == 1 ? 2 : 4, d = j == 0 ? 0 : j == 1 ? 2 : 4;
                Pieces[k++] = new RECT(xs[c], ys[d], xs[c + 1], ys[d + 1]);
            }
    }

    // Where the pieces go round a window drawn in `b`, in the order of Pieces: the column edges
    // across and the row edges down. `size` scales the shadow (a window made smaller than in the
    // spread); `lift` (0..2) spreads it wider and further down. The window's own edges stay
    // where the pieces leave the shadow out (under the window), so none shows between them.
    public (float[] Across, float[] Down) Round(Box b, double lift, double size)
    {
        float k = (float)Math.Clamp(size, 0.15, 1), l = (float)lift;
        float top = k * (1 + 0.1f * l), side = k * (1 + 0.3f * l), bottom = k * (1 + 0.6f * l);
        float x = (float)b.X, y = (float)b.Y, w = (float)b.W, h = (float)b.H;
        float[] across = { x - _side * side, x + _in * side, x + w - _in * side, x + w + _side * side };
        float[] down = { y - _over * top, y + _inTop * top, y + h - _inBottom * bottom, y + h + _below * bottom };
        // A window too small for both corners squeezes them.
        if (across[1] > across[2]) across[1] = across[2] = x + w / 2;
        if (down[1] > down[2]) down[1] = down[2] = y + h * _inTop / (_inTop + _inBottom);
        return (across, down);
    }

    public void Dispose() => DestroyHandle();
}

// One window's shadow in the overlay: the sprite's eight pieces round it.
sealed class ShadowPictures : IDisposable
{
    readonly ShadowSprite _sprite;
    readonly Thumbnail[] _pieces;

    // Registered now: over every live picture registered before, under every one after.
    public ShadowPictures(IntPtr overlay, ShadowSprite sprite)
    {
        _sprite = sprite;
        GetWindowRect(sprite.Handle, out var at);
        _pieces = sprite.Pieces.Select(p => new Thumbnail(overlay, sprite.Handle, new RECT(at.Left + p.Left, at.Top + p.Top, at.Left + p.Right, at.Top + p.Bottom))).ToArray();
    }

    // Round a window drawn in `b`, as dark as `opacity` (1: as on the desktop); `lift` 1 under
    // the pointer, 2 picked up: wider, further down and darker. Above `clipTop` it is left out.
    public void Place(Box b, double lift, float opacity, double size = 1, float clipTop = float.NegativeInfinity)
    {
        var (across, down) = _sprite.Round(b, lift, size);
        float alpha = opacity * (1 + 0.4f * (float)Math.Min(lift, 1.5)) / ShadowSprite.Strength;
        int k = 0;
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
            {
                if (i == 1 && j == 1) continue;
                float w = across[i + 1] - across[i], h = down[j + 1] - down[j];
                _pieces[k++].Place(across[i], down[j], Math.Max(1, w), Math.Max(1, h), w >= 0.5f && h >= 0.5f ? alpha : 0, clipTop);
            }
    }

    public void Hide()
    {
        foreach (var p in _pieces) p.Place(0, 0, 1, 1, 0);
    }

    public void Dispose()
    {
        foreach (var p in _pieces) p.Dispose();
    }
}

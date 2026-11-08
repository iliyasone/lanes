using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using static Lanes.Native;

namespace Lanes;

static class Capture
{
    // What is on the screen in this rectangle now. Lanes' own overlay is excluded from capture,
    // so this is always the desktop underneath.
    public static Pixels Screen(RECT area)
    {
        var p = new Pixels(area.Width, area.Height);
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = area.Width, Height = -area.Height, Planes = 1, BitCount = 32 };
        IntPtr dib = CreateDIBSection(mem, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, dib);
        BitBlt(mem, 0, 0, area.Width, area.Height, screen, area.Left, area.Top, SRCCOPY);
        unsafe { Buffer.MemoryCopy((void*)bits, (void*)p.Data, (long)area.Width * area.Height * 4, (long)area.Width * area.Height * 4); }
        SelectObject(mem, old);
        DeleteObject(dib);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        return p;
    }

    // Windows that stay live over Lanes' animations, such as a status pill shown on every
    // desktop. Right before a picture of the screen, Lanes sends the registered message
    // "Lanes.ScreenPicture" with wParam 1 to each visible topmost window. A window that wants to
    // stay over the animation leaves itself out of screen pictures before it returns
    // (SetWindowDisplayAffinity with WDA_EXCLUDEFROMCAPTURE); Lanes then keeps it above the
    // overlay until the gesture ends. After the picture, wParam 0 tells it that it may show in
    // pictures again, so screenshots and screen sharing keep it.
    public static readonly int ScreenPictureMessage = RegisterWindowMessage("Lanes.ScreenPicture");

    // A picture of `area` without the windows that asked to stay live; `keepUnder` is the lowest
    // of those, for the overlay to go just under.
    public static Pixels ScreenWithout(RECT area, IntPtr own, out IntPtr keepUnder)
    {
        var asked = new List<IntPtr>();
        IntPtr lowest = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (h == own || !IsWindowVisible(h) || Cloaked(h)) return true;
            if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) == 0) return true;
            var f = Frame(h);
            if (f.Right <= area.Left || f.Left >= area.Right || f.Bottom <= area.Top || f.Top >= area.Bottom) return true;
            SendMessageTimeout(h, ScreenPictureMessage, (IntPtr)1, IntPtr.Zero, 0x2 /* SMTO_ABORTIFHUNG */, 50, out _);
            asked.Add(h);
            // Front to back, so the last one found is the lowest.
            if (GetWindowDisplayAffinity(h, out uint a) && a == WDA_EXCLUDEFROMCAPTURE) lowest = h;
            return true;
        }, IntPtr.Zero);
        if (lowest != IntPtr.Zero) DwmFlush();
        var p = Screen(area);
        foreach (var h in asked) PostMessage(h, ScreenPictureMessage, IntPtr.Zero, IntPtr.Zero);
        keepUnder = lowest;
        return p;
    }

    public static Pixels FromBitmap(Bitmap bmp)
    {
        var p = new Pixels(bmp.Width, bmp.Height);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        unsafe
        {
            for (int y = 0; y < bmp.Height; y++)
                Buffer.MemoryCopy((void*)(data.Scan0 + y * data.Stride), (void*)(p.Data + y * bmp.Width * 4), bmp.Width * 4, bmp.Width * 4);
        }
        bmp.UnlockBits(data);
        return p;
    }

    // The desktop's wallpaper as Windows draws it on a monitor of this size (Fill, Fit, Stretch,
    // Centre or Tile), or a dark plain colour when there is none.
    // The picture file Windows shows on this desktop: its own, else the one for all desktops.
    public static string? WallpaperPath(Guid desktop)
    {
        string? path = OwnWallpaperPath(desktop) ?? Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;
        return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path;
    }

    public static string? OwnWallpaperPath(Guid desktop)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops\{{{desktop.ToString().ToUpperInvariant()}}}");
            return key?.GetValue("Wallpaper") is string path && File.Exists(path) ? path : null;
        }
        catch { return null; }
    }

    public static Bitmap Wallpaper(string? path, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(BackgroundColour());
        if (path == null) return bmp;
        string style = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallpaperStyle", "10") as string ?? "10";
        string tile = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "TileWallpaper", "0") as string ?? "0";
        try
        {
            using var img = Image.FromFile(path);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            double sx = (double)width / img.Width, sy = (double)height / img.Height;
            if (tile == "1")
            {
                using var brush = new TextureBrush(img);
                g.FillRectangle(brush, 0, 0, width, height);
            }
            else
            {
                double s = style switch { "6" => Math.Min(sx, sy), "2" => double.NaN, "0" => 1, _ => Math.Max(sx, sy) };
                if (double.IsNaN(s)) g.DrawImage(img, 0, 0, width, height);
                else
                {
                    float w = (float)(img.Width * s), h = (float)(img.Height * s);
                    g.DrawImage(img, (width - w) / 2, (height - h) / 2, w, h);
                }
            }
        }
        catch (Exception e) { Log.Write($"wallpaper {path}: {e.Message}"); }
        return bmp;
    }

    static Color BackgroundColour()
    {
        try
        {
            var parts = (Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Colors", "Background", "0 0 0") as string ?? "0 0 0").Split(' ');
            return Color.FromArgb(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
        }
        catch { return Color.Black; }
    }

    // A small, heavily blurred copy; drawn stretched it reads as a frosted background.
    public static Pixels Blurred(Bitmap src, int factor = 12)
    {
        int w = Math.Max(1, src.Width / factor), h = Math.Max(1, src.Height / factor);
        using var small = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, w, h);
        }
        var p = FromBitmap(small);
        unsafe
        {
            var px = (uint*)p.Data;
            var tmp = new uint[w * h];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxBlur(px, tmp, w, h, 2, true);
                BoxBlur(px, tmp, w, h, 2, false);
            }
        }
        return p;
    }

    static unsafe void BoxBlur(uint* px, uint[] tmp, int w, int h, int r, bool horizontal)
    {
        int len = horizontal ? w : h, lines = horizontal ? h : w;
        for (int line = 0; line < lines; line++)
        {
            for (int i = 0; i < len; i++)
            {
                int b = 0, gr = 0, rd = 0, n = 0;
                for (int k = -r; k <= r; k++)
                {
                    int j = Math.Clamp(i + k, 0, len - 1);
                    uint c = horizontal ? px[line * w + j] : px[j * w + line];
                    b += (int)(c & 0xFF); gr += (int)((c >> 8) & 0xFF); rd += (int)((c >> 16) & 0xFF); n++;
                }
                tmp[horizontal ? line * w + i : i * w + line] = 0xFF000000u | (uint)(rd / n) << 16 | (uint)(gr / n) << 8 | (uint)(b / n);
            }
        }
        for (int i = 0; i < w * h; i++) px[i] = tmp[i];
    }
}

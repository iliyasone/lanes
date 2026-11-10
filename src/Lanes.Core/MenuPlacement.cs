namespace Lanes.Core;

// Where the tray menu opens: against the taskbar, wherever it is. The taskbar is the side of the
// screen the work area leaves out (top, bottom, left or right); the menu keeps a margin from it
// and stays near the pointer along it.
public static class MenuPlacement
{
    public static (int X, int Y) Place(Rect screen, Rect work, int width, int height, int pointerX, int pointerY, int margin)
    {
        int alongX = Math.Clamp(pointerX - width / 2, work.Left + margin, work.Right - width - margin);
        int alongY = Math.Clamp(pointerY - height / 2, work.Top + margin, work.Bottom - height - margin);
        if (work.Top > screen.Top) return (alongX, work.Top + margin);
        if (work.Left > screen.Left) return (work.Left + margin, alongY);
        if (work.Right < screen.Right) return (work.Right - width - margin, alongY);
        return (alongX, work.Bottom - height - margin);
    }
}

public readonly record struct Rect(int Left, int Top, int Right, int Bottom);

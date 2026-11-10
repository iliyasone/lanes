namespace Lanes.Core;

// What Windows calls an app's taskbar button ("Google Chrome - 5 running windows pinned",
// "On-Screen Keyboard pinned"), and whether a program's name is that app's.
public static class TaskbarNames
{
    // The app's name in the button's name: what is before the count of its windows.
    public static string App(string button)
    {
        int dash = button.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) return button[..dash].Trim();
        return button.EndsWith(" pinned", StringComparison.Ordinal) ? button[..^7].Trim() : button.Trim();
    }

    // "Telegram" for "Telegram Desktop": one name starts the other.
    public static bool Same(string app, string program) =>
        app.Length > 0 && program.Length > 0 &&
        (program.StartsWith(app, StringComparison.OrdinalIgnoreCase) || app.StartsWith(program, StringComparison.OrdinalIgnoreCase));
}

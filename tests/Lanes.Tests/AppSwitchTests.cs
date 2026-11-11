using Lanes.Core;
using Xunit;

public class AppSwitchTests
{
    static readonly IntPtr ChromeA = 1, ChromeB = 2, Telegram = 3, Code = 4, Notepad = 5;

    // In z-order, front first.
    static readonly (IntPtr, string)[] Windows =
    {
        (Code, "code"), (ChromeA, "chrome"), (Notepad, "notepad"), (Telegram, "telegram"), (ChromeB, "chrome"),
    };

    [Fact]
    public void TheWindowLastInFrontComesFirst()
    {
        var r = new RecentWindows();
        r.Front(ChromeA);
        r.Front(Telegram);
        r.Front(ChromeA);
        Assert.Equal(new[] { ChromeA, Telegram }, r.Order);
        r.Keep(new[] { Telegram, Code });
        Assert.Equal(new[] { Telegram }, r.Order);
    }

    [Fact]
    public void AppsGoInTheOrderTheyWereLastInFrontThenAsTheyLie()
    {
        var apps = AppSwitch.Apps(Windows, new[] { Telegram, ChromeB, Code });
        Assert.Equal(new[] { "telegram", "chrome", "code", "notepad" }, apps.Select(a => a.Id));
    }

    [Fact]
    public void AnAppGoesToItsWindowLastInFront()
    {
        var apps = AppSwitch.Apps(Windows, new[] { Telegram, ChromeB, Code });
        Assert.Equal(new[] { ChromeB, ChromeA }, apps[1].Windows);
        Assert.Equal(ChromeB, apps[1].Window);
        // Never in front: the one in front of the other.
        Assert.Equal(ChromeA, AppSwitch.Apps(Windows, Array.Empty<IntPtr>()).First(a => a.Id == "chrome").Window);
    }

    [Fact]
    public void AppIdsDoNotCareAboutCase()
    {
        var apps = AppSwitch.Apps(new[] { (ChromeA, "Chrome"), (ChromeB, "chrome") }, Array.Empty<IntPtr>());
        Assert.Single(apps);
    }

    [Fact]
    public void TheFirstTabPicksTheAppBeforeAndShiftTheLast()
    {
        Assert.Equal(1, AppSwitch.First(4, back: false));
        Assert.Equal(3, AppSwitch.First(4, back: true));
        Assert.Equal(0, AppSwitch.First(1, back: false));
        Assert.Equal(-1, AppSwitch.First(0, back: false));
    }

    [Fact]
    public void StepsGoRoundAtTheEnds()
    {
        Assert.Equal(2, AppSwitch.Step(1, 1, 4));
        Assert.Equal(0, AppSwitch.Step(3, 1, 4));
        Assert.Equal(3, AppSwitch.Step(0, -1, 4));
    }

    [Fact]
    public void IconsShrinkOnlyWhenTheRowDoesNotFit()
    {
        Assert.Equal(96, AppSwitch.IconSize(5, 1800, 96, 12));
        Assert.Equal(1800f / 20 - 24, AppSwitch.IconSize(20, 1800, 96, 12));
    }
}

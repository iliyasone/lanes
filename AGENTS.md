# Lanes: rules for agents

Lanes is a Windows 11 tray app; the user's goal is that swipes feel exactly as smooth as
Windows' own. Read the README first: it documents behaviour and is updated in the same PR
as any change to it.

## Where things go

- `src/Lanes.Core/` has no Windows dependencies and holds every decision that can be
  tested: gesture recognition, motion, the lane map, layout. Put new logic there with a
  test in `tests/Lanes.Tests/`, and keep `src/Lanes/` to talking to Windows and drawing.
- Everything that runs during a swipe stays on the engine thread (`Engine*.cs`): touchpad
  input, recognition, drawing. Nothing on it may block for long (no network, no disk
  beyond small JSON writes after a swipe); slow work (wallpapers, screen captures at touch
  time) runs on the thread pool.
- Virtual-desktop calls go through `Desktops.cs` only.

## Checking a change

- `dotnet test tests/Lanes.Tests` and a Release build of `src/Lanes` (both run on Linux).
- CI's demo run (`Lanes.exe --demo`) uploads screenshots of each swipe and the log with
  frame timings: look at them for any change to drawing. The runner draws in software, so
  its frame times are only a regression signal; real smoothness is judged on the user's PC
  with a Magic Trackpad, and that check is the user's call — never install or run Lanes on
  the user's PC unasked.
- Visual changes follow `docs/design/mock.html`; change the mockup in the same PR when the
  design changes.

## Releases

Bump `<Version>` in `src/Lanes/Lanes.csproj`, merge, tag `vX.Y.Z` on main and push the
tag; `release.yml` publishes `Lanes.exe` and installed copies offer the update.

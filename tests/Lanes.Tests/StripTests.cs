using Lanes.Core;
using Xunit;

public class StripTests
{
    static readonly Guid A = G(1), B = G(2), C = G(3), Msg = G(4), Agent = G(5), Video = G(6);
    static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    // Messages above; A B C in the middle, on B; Agent and Video below, last on Video.
    static LaneMap Sample()
    {
        var m = LaneMap.Fresh(new[] { A, B, C }, B);
        m.Add(Msg, 0);
        m.Add(Agent, 2); m.Add(Video, 2);
        m.Reconcile(new[] { Msg, A, B, C, Agent, Video }, B);
        m.Visited(Video);
        m.Visited(B);
        return m;
    }

    static Box Of(Strip s, Guid d) => s.Tiles.Single(t => t.Desktop == d && t.Kind == TileKind.Desktop).Rect;
    static (double X, double Y) Centre(Box b) => (b.X + b.W / 2, b.Y + b.H / 2);

    [Fact]
    public void CurrentDesktopsOfAllLanesShareOneColumn()
    {
        var s = Strip.Layout(Sample(), B, 1920, 1080, 1);
        Assert.Equal(Of(s, B).X, Of(s, Msg).X);
        Assert.Equal(Of(s, B).X, Of(s, Video).X);
        Assert.Equal((1920 - s.TileW) / 2, Of(s, B).X, 3);
    }

    [Fact]
    public void AnAppsMapHasBiggerTilesInTheSameColumnAndNoButtons()
    {
        var rows = Strip.Layout(Sample(), B, 1920, 1080, 1, offset: 48, room: 1032, bin: true);
        var map = Strip.Layout(Sample(), B, 1920, 1080, 1, offset: 48, room: 1032, bin: true, app: true);
        Assert.True(map.TileW > rows.TileW * 1.5);
        Assert.True(map.Bottom <= 48 + 1032 * 0.6 + 18);
        Assert.Equal((1920 - map.TileW) / 2, Of(map, B).X, 3);
        Assert.DoesNotContain(map.Tiles, t => t.Kind is TileKind.Plus or TileKind.Trash);
    }

    [Fact]
    public void ADragOpensNewLaneSlotsAboveAndBelowWithoutMovingAnything()
    {
        var idle = Strip.Layout(Sample(), B, 1920, 1080, 1, offset: 48);
        var drag = Strip.Layout(Sample(), B, 1920, 1080, 1, dragging: true, offset: 48);
        Assert.DoesNotContain(idle.Tiles, t => t.Kind == TileKind.NewLane);
        var slots = drag.Tiles.Where(t => t.Kind == TileKind.NewLane).ToList();
        Assert.Equal(2, slots.Count);
        Assert.Equal(Of(idle, Msg), Of(drag, Msg));
        Assert.Equal(Of(idle, Video), Of(drag, Video));
        Assert.Equal(idle.SpreadTop, drag.SpreadTop, 3);
        Assert.Equal(idle.Bottom, drag.Bottom, 3);
        // Room above the top row and below the bottom one, over no desktop; a thin bar in each.
        Assert.True(slots[0].Rect.Bottom <= Of(drag, Msg).Y && slots[1].Rect.Y >= Of(drag, Video).Bottom);
        Assert.True(slots[1].Rect.Bottom <= drag.Bottom);
        foreach (var slot in slots)
        {
            var bar = drag.Bar(slot, 4);
            Assert.Equal(4, bar.H, 3);
            Assert.True(bar.Y >= slot.Rect.Y && bar.Bottom <= slot.Rect.Bottom, $"{bar} in {slot.Rect}");
        }
    }

    [Fact]
    public void ADragGetsANewDesktopSlotAtEachEndOfEachRow()
    {
        var m = Sample();
        Assert.DoesNotContain(Strip.Layout(m, B, 1920, 1080, 1).Tiles, t => t.Kind == TileKind.NewDesk);
        var s = Strip.Layout(m, B, 1920, 1080, 1, dragging: true);
        var middle = s.Tiles.Where(t => t.Kind == TileKind.NewDesk && t.Lane == 1).OrderBy(t => t.Rect.X).ToList();
        Assert.Equal(2 * m.Lanes.Count, s.Tiles.Count(t => t.Kind == TileKind.NewDesk));
        Assert.Equal(2, middle.Count);
        Assert.Equal(0, middle[0].Index);
        Assert.Equal(m.Lanes[1].Desktops.Count, middle[1].Index);
        Assert.True(middle[0].Rect.Right < Of(s, A).X && middle[1].Rect.X > Of(s, C).Right);
        Assert.Equal(middle[0], s.Hit(middle[0].Rect.X + 2, middle[0].Rect.Y + 2));
        // Shown as a bar in the gap beside the row's end; nothing is open yet.
        var bar = s.Bar(middle[1], 4);
        Assert.InRange(bar.X + bar.W / 2, Of(s, C).Right, middle[1].Rect.X);
        Assert.Null(s.Opened);
    }

    [Fact]
    public void AnOpenedNewLaneMovesTheRowsBelowItDownAndGrowsTheBand()
    {
        var m = Sample();
        var idle = Strip.Layout(m, B, 1920, 1080, 1, dragging: true, offset: 48);
        var below = Strip.Layout(m, B, 1920, 1080, 1, dragging: true, offset: 48, open: new DeskDrop(3, 0, true));
        double row = idle.RowY(1) - idle.RowY(0);
        // At the bottom: the rows stay, the band grows by a row, the block in the middle column under them.
        Assert.Equal(Of(idle, Video), Of(below, Video));
        Assert.Equal(idle.Bottom + row, below.Bottom, 3);
        Assert.Equal(idle.SpreadTop + row, below.SpreadTop, 3);
        Assert.Equal(new Box(Of(idle, B).X, idle.RowY(2) + row, idle.TileW, idle.TileH), below.Opened);
        // Its slot takes the room it opened from, so the block stays while held there.
        var bottom = idle.Tiles.Last(t => t.Kind == TileKind.NewLane).Rect;
        Assert.Equal(below.OpenedSlot, below.Hit(bottom.X + 10, bottom.Y + 2));
        Assert.Equal(below.OpenedSlot, below.Hit(10, below.Opened!.Value.Y + 10));
        // At the top: every row moves down a row and the block is where the first was.
        var above = Strip.Layout(m, B, 1920, 1080, 1, dragging: true, offset: 48, open: new DeskDrop(0, 0, true));
        Assert.Equal(Of(idle, Msg).Y + row, Of(above, Msg).Y, 3);
        Assert.Equal(Of(idle, Video).Y + row, Of(above, Video).Y, 3);
        Assert.Equal(Of(idle, Msg).Y, above.Opened!.Value.Y, 3);
        Assert.Equal(idle.TileW, above.TileW, 3);
        Assert.Equal(new DeskDrop(0, 0, true), above.DropFor(10, 5, A, m));
    }

    [Fact]
    public void AnOpenedPlaceInARowMovesTheDesktopsPastItOutward()
    {
        var m = Sample();
        var idle = Strip.Layout(m, B, 1920, 1080, 1, dragging: true);
        double step = idle.TileW + idle.Gap;
        // Between B (in the middle column) and C: C moves out to the right, the block takes its place.
        var right = Strip.Layout(m, B, 1920, 1080, 1, dragging: true, open: new DeskDrop(1, 2, false));
        Assert.Equal(Of(idle, A), Of(right, A));
        Assert.Equal(Of(idle, B), Of(right, B));
        Assert.Equal(Of(idle, C).X + step, Of(right, C).X, 3);
        Assert.Equal(Of(idle, C), right.Opened);
        Assert.Equal(new StripTile(TileKind.NewDesk, 1, 2, Guid.Empty, right.OpenedSlot!.Rect), right.OpenedSlot);
        Assert.Equal(right.OpenedSlot, right.Hit(Of(idle, C).X + 5, Of(idle, C).Y + 5));
        // and stays open by the gap where it opened, over the edge of B
        Assert.Equal(right.OpenedSlot, right.Hit(Of(idle, B).Right - 5, Of(idle, B).Y + 5));
        Assert.Equal(B, right.Hit(Of(idle, B).X + Of(idle, B).W / 2, Of(idle, B).Y + 5)!.Desktop);
        // Between A and B: A moves out to the left; B stays in the column.
        var left = Strip.Layout(m, B, 1920, 1080, 1, dragging: true, open: new DeskDrop(1, 1, false));
        Assert.Equal(Of(idle, A).X - step, Of(left, A).X, 3);
        Assert.Equal(Of(idle, B), Of(left, B));
        Assert.Equal(Of(idle, A), left.Opened);
        Assert.Equal(left.OpenedSlot, left.Hit(Of(idle, B).X + 5, Of(idle, B).Y + 5));
        // A row's end: nothing moves, the block is just past it.
        var end = Strip.Layout(m, B, 1920, 1080, 1, dragging: true, open: new DeskDrop(1, 3, false));
        Assert.Equal(Of(idle, C), Of(end, C));
        Assert.Equal(Of(idle, C).X + step, end.Opened!.Value.X, 3);
        // It grows out of the bar in the gap.
        var seed = idle.Seed(new DeskDrop(1, 2, false), Guid.Empty, 4);
        Assert.InRange(seed.X + seed.W / 2, Of(idle, B).Right, Of(idle, C).X);
    }

    [Fact]
    public void ADraggedDesktopOpensItsPlaceCountedWithoutItself()
    {
        var m = Sample();
        var idle = Strip.Layout(m, B, 1920, 1080, 1, dragging: true);
        // A dragged past C: without A the lane is B C, and index 2 is past C.
        var drop = idle.DropFor(Of(idle, C).Right + 4, Of(idle, C).Y + 5, A, m);
        Assert.Equal(new DeskDrop(1, 2, false), drop);
        var open = Strip.Layout(m, B, 1920, 1080, 1, dragging: true, open: drop, dragged: A);
        Assert.Equal(Of(idle, C).X + idle.TileW + idle.Gap, open.Opened!.Value.X, 3);
        Assert.Equal(drop, open.DropFor(open.Opened.Value.X + 10, open.Opened.Value.Y + 5, A, m));
    }

    [Fact]
    public void AWindowHeldByTheGapBetweenTwoDesktopsAsksForOneThere()
    {
        var s = Strip.Layout(Sample(), B, 1920, 1080, 1, dragging: true);
        var a = Of(s, A);
        var b = Of(s, B);
        Assert.Equal(new DeskDrop(1, 1, false), s.Between((a.Right + b.X) / 2, a.Y + 10));
        Assert.Equal(new DeskDrop(1, 1, false), s.Between(b.X + 10, a.Y + 10));
        Assert.Null(s.Between(b.X + b.W / 2, b.Y + 10));
        // not past a row's end
        Assert.Null(s.Between(Of(s, C).Right + 6, b.Y + 10));
    }

    [Fact]
    public void OnePlusInTheTopRightCornerAddsToYourLane()
    {
        var s = Strip.Layout(Sample(), B, 1920, 1080, 1);
        var plus = Assert.Single(s.Tiles, t => t.Kind == TileKind.Plus);
        Assert.Equal(1, plus.Lane);
        Assert.True(plus.Rect.Right <= 1920 && plus.Rect.X > 1920 - 100, $"{plus.Rect}");
        Assert.Equal(s.RowY(0), plus.Rect.Y, 3);
    }

    [Fact]
    public void TheBinSitsJustBelowThePlus()
    {
        var drag = Strip.Layout(Sample(), B, 1920, 1080, 1, dragging: true, bin: true);
        var plus = drag.Tiles.Single(t => t.Kind == TileKind.Plus).Rect;
        var bin = drag.Tiles.Single(t => t.Kind == TileKind.Trash).Rect;
        Assert.Equal(plus.X, bin.X, 3);
        Assert.InRange(bin.Y - plus.Bottom, 1, 20);
        Assert.DoesNotContain(Strip.Layout(Sample(), B, 1920, 1080, 1).Tiles, t => t.Kind == TileKind.Trash);
        Assert.Equal(2, drag.LaneAt(drag.RowY(2) + 5));
        Assert.Null(drag.LaneAt(drag.SpreadTop));
    }

    [Fact]
    public void DroppingBetweenTwoDesktopsPutsItThere()
    {
        var m = Sample();
        var s = Strip.Layout(m, B, 1920, 1080, 1, dragging: true);
        // Video dragged between A and B in the middle lane.
        var (ax, ay) = Centre(Of(s, A));
        var (bx, _) = Centre(Of(s, B));
        var drop = s.DropFor((ax + bx) / 2, ay, Video, m);
        Assert.Equal(new DeskDrop(1, 1, false), drop);
        m.Add(Video, drop!.Value.Lane, drop.Value.Index);
        Assert.Equal(new[] { A, Video, B, C }, m.Lanes[1].Desktops);
    }

    [Fact]
    public void DroppingWhereItAlreadyIsChangesNothing()
    {
        var m = Sample();
        var s = Strip.Layout(m, B, 1920, 1080, 1, dragging: true);
        var (x, y) = Centre(Of(s, B));
        Assert.Null(s.DropFor(x, y, B, m));
        // and just past A, which is where B already is
        Assert.Null(s.DropFor(Of(s, A).Right + 2, y, B, m));
    }

    [Fact]
    public void NewLaneTilesMakeALaneAboveOrBelow()
    {
        var m = Sample();
        var s = Strip.Layout(m, B, 1920, 1080, 1, dragging: true);
        var top = s.Tiles.First(t => t.Kind == TileKind.NewLane);
        var bottom = s.Tiles.Last(t => t.Kind == TileKind.NewLane);
        Assert.Equal(new DeskDrop(0, 0, true), s.DropFor(top.Rect.X + 10, top.Rect.Y + 5, A, m));
        Assert.Equal(new DeskDrop(3, 0, true), s.DropFor(bottom.Rect.X + 10, bottom.Rect.Y + 5, A, m));
        // Messages is alone in the top lane: a new lane right above it would be the same thing.
        Assert.Null(s.DropFor(top.Rect.X + 10, top.Rect.Y + 5, Msg, m));
    }
}

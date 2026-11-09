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
    public void ADraggedWindowGetsANewDesktopSlotAtEachEndOfEachRow()
    {
        var m = Sample();
        Assert.DoesNotContain(Strip.Layout(m, B, 1920, 1080, 1, dragging: true).Tiles, t => t.Kind == TileKind.NewDesk);
        var s = Strip.Layout(m, B, 1920, 1080, 1, dragging: true, window: true);
        var middle = s.Tiles.Where(t => t.Kind == TileKind.NewDesk && t.Lane == 1).OrderBy(t => t.Rect.X).ToList();
        Assert.Equal(2 * m.Lanes.Count, s.Tiles.Count(t => t.Kind == TileKind.NewDesk));
        Assert.Equal(2, middle.Count);
        Assert.Equal(0, middle[0].Index);
        Assert.Equal(m.Lanes[1].Desktops.Count, middle[1].Index);
        Assert.True(middle[0].Rect.Right < Of(s, A).X && middle[1].Rect.X > Of(s, C).Right);
        Assert.Equal(middle[0], s.Hit(middle[0].Rect.X + 2, middle[0].Rect.Y + 2));
        // Shown as a bar in the gap beside the row's end.
        var bar = s.Bar(middle[1], 4);
        Assert.InRange(bar.X + bar.W / 2, Of(s, C).Right, middle[1].Rect.X);
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

    [Fact]
    public void TheMarkerSitsInTheGapWhereTheDesktopWouldGo()
    {
        var m = Sample();
        var s = Strip.Layout(m, B, 1920, 1080, 1, dragging: true);
        var marker = s.Marker(new DeskDrop(1, 1, false), Video, 1);
        Assert.InRange(marker.X + marker.W / 2, Of(s, A).Right, Of(s, B).X);
    }
}

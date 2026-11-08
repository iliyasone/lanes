using Lanes.Core;
using Xunit;

public class LaneMapTests
{
    static readonly Guid A = G(1), B = G(2), C = G(3), Msg = G(4), Agent = G(5), Video = G(6);
    static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    static LaneMap Sample()
    {
        var m = new LaneMap();
        m.Add(Msg, LaneMap.Top);
        m.Add(A, LaneMap.Main); m.Add(B, LaneMap.Main); m.Add(C, LaneMap.Main);
        m.Add(Agent, LaneMap.Bottom); m.Add(Video, LaneMap.Bottom);
        m.Reconcile(new[] { Msg, A, B, C, Agent, Video }, B);
        return m;
    }

    [Fact]
    public void UpAlwaysLandsOnTheTopLanesDesktopWhereverYouAre()
    {
        var m = Sample();
        Assert.Equal(Msg, m.Neighbor(B, Direction.Up));
        Assert.Equal(Msg, m.Neighbor(C, Direction.Up));
        Assert.Null(m.Neighbor(Msg, Direction.Up));
    }

    [Fact]
    public void EachLaneRemembersWhereYouWere()
    {
        // The example from the brief: down to Agent, right to Video, up, down again.
        var m = Sample();
        Assert.Equal(Agent, m.Neighbor(B, Direction.Down));
        m.Visited(Agent);
        Assert.Equal(Video, m.Neighbor(Agent, Direction.Right));
        m.Visited(Video);
        Assert.Equal(B, m.Neighbor(Video, Direction.Up));
        m.Visited(B);
        Assert.Equal(Msg, m.Neighbor(B, Direction.Up));
        Assert.Equal(Video, m.Neighbor(B, Direction.Down));
    }

    [Fact]
    public void SidewaysStaysInTheLane()
    {
        var m = Sample();
        Assert.Null(m.Neighbor(C, Direction.Right));
        Assert.Null(m.Neighbor(Agent, Direction.Left));
        Assert.Equal(C, m.Neighbor(B, Direction.Right));
    }

    [Fact]
    public void FollowsDesktopsMadeAndRemovedOutsideLanes()
    {
        var m = Sample();
        var made = G(7);
        m.Reconcile(new[] { Msg, A, C, Agent, Video, made }, made);
        Assert.Equal(new[] { A, C, made }, m.Lanes[LaneMap.Main].Desktops);
        Assert.Equal(made, m.Lanes[LaneMap.Main].Current);
        m.Reconcile(new[] { A, C, Agent, Video, made }, A);
        Assert.Empty(m.Lanes[LaneMap.Top].Desktops);
        Assert.Null(m.Neighbor(A, Direction.Up));
        Assert.Equal(new[] { LaneMap.Top }, m.EmptyLanes());
    }

    [Fact]
    public void SavesAndLoads()
    {
        var m = Sample();
        var back = LaneMap.Load(m.Save());
        Assert.Equal(m.Save(), back.Save());
    }
}

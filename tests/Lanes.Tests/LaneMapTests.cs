using Lanes.Core;
using Xunit;

public class LaneMapTests
{
    static readonly Guid A = G(1), B = G(2), C = G(3), Msg = G(4), Agent = G(5), Video = G(6);
    static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    const int Top = 0, Main = 1, Bottom = 2;

    static LaneMap Sample()
    {
        var m = LaneMap.Fresh(new[] { A, B, C }, B);
        m.Add(Msg, Top);
        m.Add(Agent, Bottom); m.Add(Video, Bottom);
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
        // Made elsewhere while you are on C: it joins C's lane.
        m.Reconcile(new[] { Msg, A, C, Agent, Video, made }, C);
        Assert.Equal(new[] { A, C, made }, m.Lanes[Main].Desktops);
        Assert.Equal(C, m.Lanes[Main].Current);
        // The top lane's only desktop is closed: the lane goes, Main is now the top one.
        m.Reconcile(new[] { A, C, Agent, Video, made }, A);
        Assert.Equal(2, m.Lanes.Count);
        Assert.Null(m.Neighbor(A, Direction.Up));
        Assert.Equal(Agent, m.Neighbor(A, Direction.Down));
    }

    [Fact]
    public void LanesCanBeAdded()
    {
        var m = Sample();
        var made = G(8);
        m.InsertLane(3);
        m.Add(made, 3);
        m.Reconcile(new[] { Msg, A, B, C, Agent, Video, made }, B);
        Assert.Equal(4, m.Lanes.Count);
        Assert.Equal(made, m.Neighbor(Agent, Direction.Down));
    }

    [Fact]
    public void SavesAndLoads()
    {
        var m = Sample();
        var back = LaneMap.Load(m.Save());
        Assert.Equal(m.Save(), back.Save());
    }
}

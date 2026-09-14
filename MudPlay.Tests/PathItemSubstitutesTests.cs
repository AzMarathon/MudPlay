using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

public sealed class PathItemSubstitutesTests
{
    private const int Raft = 690, Skiff = 691, Canoe = 1181, Punt = 3609;
    private static readonly int[] River = { Raft, Skiff, Canoe, Punt };
    private static readonly int[] Lake = { Raft, Skiff };

    [Fact]
    public void Unrecorded_ItemStandsAlone()
    {
        var s = new PathItemSubstitutes();
        Assert.Equal(new[] { Raft }, s.For(Raft));
    }

    [Fact]
    public void RiverRoute_AnyBoatCovers()
    {
        var s = new PathItemSubstitutes();
        s.Record(Raft, River);
        s.Commit(keep: _ => false);

        Assert.Equal(new[] { Raft, Skiff, Canoe, Punt }, s.For(Raft));
        Assert.Equal(1, s.Coverage(Raft, id => id == Canoe ? 1 : 0));
    }

    [Fact]
    public void RiverAndLakeRoute_IntersectsToRaftOrSkiff()
    {
        var s = new PathItemSubstitutes();
        s.Record(Raft, River);
        s.Record(Raft, Lake);
        s.Commit(keep: _ => false);

        Assert.Equal(new[] { Raft, Skiff }, s.For(Raft));
        Assert.Equal(0, s.Coverage(Raft, id => id == Canoe ? 1 : 0));
    }

    [Fact]
    public void GroupWithoutTheItem_IsIgnored()
    {
        var s = new PathItemSubstitutes();
        s.Record(Canoe, Lake);
        s.Commit(keep: _ => false);

        Assert.Equal(new[] { Canoe }, s.For(Canoe));
    }

    [Fact]
    public void StagedEntries_OnlyApplyOnceCommitted()
    {
        var s = new PathItemSubstitutes();
        s.Record(Raft, River);
        Assert.Equal(new[] { Raft }, s.For(Raft));
        s.Commit(keep: _ => false);
        Assert.Equal(4, s.For(Raft).Count);
    }

    [Fact]
    public void LaterRouteWithoutTheHazard_DropsSubstitutesUnlessStillWanted()
    {
        var s = new PathItemSubstitutes();
        s.Record(Skiff, River);
        s.Commit(keep: _ => false);

        // A detour's announce (walking to the shop) doesn't cross the river, but the
        // skiff is still being obtained: its substitutes survive.
        s.Commit(keep: id => id == Skiff);
        Assert.Equal(4, s.For(Skiff).Count);

        // An unrelated later walk: a plain "(Item: 691)" exit must not take a canoe.
        s.Commit(keep: _ => false);
        Assert.Equal(new[] { Skiff }, s.For(Skiff));
    }

    [Fact]
    public void Clear_DropsStagedAndActive()
    {
        var s = new PathItemSubstitutes();
        s.Record(Raft, River);
        s.Commit(keep: _ => false);
        s.Record(Skiff, River);
        s.Clear();
        s.Commit(keep: _ => true);

        Assert.Equal(new[] { Raft }, s.For(Raft));
        Assert.Equal(new[] { Skiff }, s.For(Skiff));
    }
}

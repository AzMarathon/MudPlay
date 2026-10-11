using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using Xunit;

namespace MudPlay.Tests;

// The Simulator's finished rankings, kept by the game data and character each was
// made on: a pairing gets its own back and no other's, names match without case, a
// set read in again loses all of its rankings, and only the last few made or shown
// are held.
public sealed class AreaRankingStoreTests
{
    private static AreaRanking Ranking(string area) => new(
        new[]
        {
            new AreaRank(area, 40, new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, 2,
                new LoopSimSummary(Array.Empty<LoopSimRun>())),
        },
        40, 1.0, 1.0, 3, new RoomKey(1, 1), "Stock", area);

    [Fact]
    public void APairingGetsItsOwnRankingBackAndNoOthers()
    {
        var store = new AreaRankingStore();
        AreaRanking hills = Ranking("Hills"), marsh = Ranking("Marsh"), fen = Ranking("Fen");
        store.Keep(new SimulatorOwner("alpha", "board/one"), hills);
        store.Keep(new SimulatorOwner("beta", "board/one"), marsh);
        store.Keep(new SimulatorOwner("alpha", "board/two"), fen);

        Assert.Same(hills, store.Get(new SimulatorOwner("ALPHA", "Board/One")));
        Assert.Same(marsh, store.Get(new SimulatorOwner("beta", "board/one")));
        Assert.Same(fen, store.Get(new SimulatorOwner("alpha", "board/two")));
        Assert.Null(store.Get(new SimulatorOwner("beta", "board/two")));
        Assert.Null(store.Get(new SimulatorOwner("alpha", null)));
        Assert.Null(store.Get(new SimulatorOwner(null, "board/one")));
    }

    [Fact]
    public void RankingAgainReplacesThePairingsRanking()
    {
        var store = new AreaRankingStore();
        var owner = new SimulatorOwner("alpha", "board/one");
        AreaRanking second = Ranking("Marsh");
        store.Keep(owner, Ranking("Hills"));
        store.Keep(owner, second);

        Assert.Equal(1, store.Count);
        Assert.Same(second, store.Get(owner));

        store.Forget(owner);
        Assert.Null(store.Get(owner));
    }

    [Fact]
    public void ASwitchOfGameDataDropsNothingButTheSameSetReadInAgainDropsAllOfItsRankings()
    {
        var store = new AreaRankingStore();
        store.NoteActiveSet("alpha");
        store.Keep(new SimulatorOwner("alpha", "board/one"), Ranking("Hills"));
        store.Keep(new SimulatorOwner("alpha", "board/two"), Ranking("Marsh"));
        store.Keep(new SimulatorOwner("beta", "board/one"), Ranking("Fen"));

        Assert.Equal(0, store.NoteActiveSet("beta"));
        Assert.Equal(0, store.NoteActiveSet("alpha"));
        Assert.Equal(3, store.Count);

        Assert.Equal(2, store.NoteActiveSet("Alpha"));

        Assert.Equal(1, store.Count);
        Assert.NotNull(store.Get(new SimulatorOwner("beta", "board/one")));
    }

    [Fact]
    public void ASetWhoseDataIsReplacedWhileAnotherIsActiveLosesItsRankings()
    {
        var store = new AreaRankingStore();
        store.NoteActiveSet("alpha");
        store.Keep(new SimulatorOwner("alpha", "board/one"), Ranking("Hills"));
        store.Keep(new SimulatorOwner("alpha", "board/two"), Ranking("Marsh"));
        store.Keep(new SimulatorOwner("beta", "board/one"), Ranking("Fen"));
        store.NoteActiveSet("beta");

        // alpha imported again, or deleted, from beta.
        Assert.Equal(2, store.ForgetSet("ALPHA"));

        // Coming back to it is a plain switch, with nothing left to list.
        Assert.Equal(0, store.NoteActiveSet("alpha"));
        Assert.Null(store.Get(new SimulatorOwner("alpha", "board/one")));
        Assert.Null(store.Get(new SimulatorOwner("alpha", "board/two")));
        Assert.NotNull(store.Get(new SimulatorOwner("beta", "board/one")));
    }

    [Fact]
    public void OnlyTheLastFewMadeOrShownAreKept()
    {
        var store = new AreaRankingStore();
        List<SimulatorOwner> owners = Enumerable.Range(0, AreaRankingStore.MaxKept)
            .Select(i => new SimulatorOwner($"set-{i}", "board/one")).ToList();
        foreach (SimulatorOwner owner in owners) Assert.Null(store.Keep(owner, Ranking("Hills")));
        // The oldest is shown again, so the next oldest is the one let go.
        Assert.NotNull(store.Get(owners[0]));

        SimulatorOwner? dropped = store.Keep(new SimulatorOwner("one-more", "board/one"), Ranking("Marsh"));

        Assert.Equal(owners[1], dropped);
        Assert.Equal(AreaRankingStore.MaxKept, store.Count);
        Assert.NotNull(store.Get(owners[0]));
        Assert.Null(store.Get(owners[1]));
        Assert.NotNull(store.Get(new SimulatorOwner("one-more", "board/one")));
    }
}

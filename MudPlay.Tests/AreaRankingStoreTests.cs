using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using Xunit;

namespace MudPlay.Tests;

// The Simulator's finished rankings, kept by the game data and character each was
// made on: a pairing gets its own back and no other's, names match without case, a
// set read in again keeps the rankings whose tables it still has, and only the last
// few made or shown are held.
public sealed class AreaRankingStoreTests
{
    private static AreaRanking Ranking(string area) => new(
        new[]
        {
            new AreaRank(area, 40, new[] { new RoomKey(1, 1), new RoomKey(1, 2) }, 2,
                new LoopSimSummary(Array.Empty<LoopSimRun>())),
        },
        40, 1.0, 1.0, 3, new RoomKey(1, 1), "Stock", area, DateTimeOffset.Now, 40);

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

    private static readonly RankingWorld Tables = new(
        1, RankingWorld.TableNames.ToDictionary(t => t, _ => "10:1"), "seed");

    private static readonly RankingWorld OtherTables = Tables with
    {
        Tables = RankingWorld.TableNames.ToDictionary(t => t, t => t == "Rooms" ? "11:2" : "10:1"),
    };

    // A switch drops nothing, and neither does the same set read in again: a
    // ranking that says which tables it was made on is kept, and handed back while
    // the load in hand has those tables.
    [Fact]
    public void NeitherASwitchOfGameDataNorTheSameSetReadInAgainDropsARankingOfTheSameTables()
    {
        var store = new AreaRankingStore();
        store.NoteActiveSet("alpha");
        AreaRanking hills = Ranking("Hills") with { World = Tables };
        var one = new SimulatorOwner("alpha", "board/one");
        store.Keep(one, hills);
        store.Keep(new SimulatorOwner("alpha", "board/two"), Ranking("Marsh") with { World = Tables });
        store.Keep(new SimulatorOwner("beta", "board/one"), Ranking("Fen") with { World = Tables });

        Assert.Equal(0, store.NoteActiveSet("beta"));
        Assert.Equal(0, store.NoteActiveSet("alpha"));
        Assert.Equal(0, store.NoteActiveSet("Alpha"));

        Assert.Equal(3, store.Count);
        Assert.Same(hills, store.Get(one, Tables));
        // Compared once with the load in hand, it is handed back for the rest of it.
        Assert.Same(hills, store.Get(one));
    }

    // Read in again, the set's tables may be other files. A ranking made on the old
    // ones is dropped when it is next asked for, with the world in hand: memory
    // hands back nothing a read from disk would refuse. Until it can be compared it
    // is neither handed back nor dropped.
    [Fact]
    public void ARankingHeldThroughAReloadIsComparedWithTheNewLoadBeforeItIsHandedBack()
    {
        var store = new AreaRankingStore();
        store.NoteActiveSet("alpha");
        var one = new SimulatorOwner("alpha", "board/one");
        var two = new SimulatorOwner("alpha", "board/two");
        store.Keep(one, Ranking("Hills") with { World = Tables });
        store.Keep(two, Ranking("Marsh") with { World = Tables });

        store.NoteActiveSet("alpha");

        // No world to compare with (the load can't be stamped): not shown, still held.
        Assert.Null(store.Get(one));
        Assert.Equal(2, store.Count);

        Assert.Null(store.Get(one, OtherTables));
        Assert.Equal(1, store.Count);
        Assert.Contains(store.DiskNotes, n => n.Contains("was dropped: the game data's Rooms table has changed since"));
        Assert.NotNull(store.Get(two, Tables));
    }

    // A world handed in is the active set's: it says nothing of a ranking held for
    // another set, which is handed back as it is and never dropped by it.
    [Fact]
    public void TheWorldInHandIsNotComparedWithARankingOfAnotherSet()
    {
        var store = new AreaRankingStore();
        store.NoteActiveSet("alpha");
        var onBeta = new SimulatorOwner("beta", "board/one");
        store.Keep(onBeta, Ranking("Fen") with { World = Tables });

        Assert.NotNull(store.Get(onBeta, OtherTables));
        Assert.Equal(1, store.Count);
    }

    // A ranking with no record of its tables can be compared with nothing. A switch
    // leaves it; its set read in again drops it, as it dropped every ranking before.
    [Fact]
    public void ARankingWithNoRecordOfItsTablesGoesWhenItsSetIsReadInAgain()
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
    public void ARenamedCharactersRankingsGoWithItOnEverySetAndTheOldNameKeepsNone()
    {
        var store = new AreaRankingStore();
        AreaRanking hills = Ranking("Hills"), marsh = Ranking("Marsh");
        store.Keep(new SimulatorOwner("alpha", "board/one"), hills);
        store.Keep(new SimulatorOwner("beta", "board/one"), marsh);
        store.Keep(new SimulatorOwner("alpha", "board/two"), Ranking("Fen"));
        // Kept for a character that had the new name before: no longer that name's.
        store.Keep(new SimulatorOwner("alpha", "board/new"), Ranking("Dunes"));

        Assert.Equal(2, store.RenameCharacter("Board/One", "board/new"));

        Assert.Same(hills, store.Get(new SimulatorOwner("alpha", "board/new")));
        Assert.Same(marsh, store.Get(new SimulatorOwner("beta", "board/new")));
        Assert.Null(store.Get(new SimulatorOwner("alpha", "board/one")));
        Assert.Null(store.Get(new SimulatorOwner("beta", "board/one")));
        Assert.NotNull(store.Get(new SimulatorOwner("alpha", "board/two")));
        Assert.Equal(3, store.Count);

        // The default profile given a name, and a name that is the same but for case.
        store.Keep(new SimulatorOwner("alpha", null), Ranking("Moor"));
        Assert.Equal(1, store.RenameCharacter(null, "board/named"));
        Assert.Null(store.Get(new SimulatorOwner("alpha", null)));
        Assert.Equal(0, store.RenameCharacter("board/named", "BOARD/NAMED"));
        Assert.NotNull(store.Get(new SimulatorOwner("alpha", "board/named")));
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

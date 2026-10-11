using System.IO;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The pairs the client has learned of where the dead of a room arrive ("room died
// in → room arrived in"), kept in one file per game-data set and shared by every
// client on that set.
public sealed class LearnedArrivalRoomStoreTests : IDisposable
{
    private static readonly RoomKey TerraIsta = new(14, 1813);
    private static readonly RoomKey IstaGraveyard = new(14, 1900);
    private static readonly RoomKey Elsewhere = new(14, 1950);

    private readonly string _root;
    private readonly List<LogEntry> _log = new();
    private readonly LogService _logService = new();

    public LearnedArrivalRoomStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-learnedarrivals-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _logService.EntryAdded += _log.Add;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    private string FileFor(string set) => Path.Combine(_root, set, "learned-arrival-rooms.json");

    // A client on a set: its own store over the shared folder.
    private LearnedArrivalRoomStore Client(string set = "paradigm")
    {
        LearnedArrivalRoomStore store = new(_logService, FileFor);
        store.OnActiveSetChanged(set);
        return store;
    }

    [Fact]
    public void APair_IsLearned_SaidInTheLog_AndKeptWithTheSet()
    {
        LearnedArrivalRoomStore store = Client();
        Assert.Null(store.ArrivalsFor(TerraIsta));

        Assert.Equal(LearnedArrivalRoomStore.Outcome.Learned, store.Learn(TerraIsta, IstaGraveyard));

        Assert.Equal(new[] { IstaGraveyard }, store.ArrivalsFor(TerraIsta));
        Assert.Equal((1, 0), (store.Count, store.DisputedCount));
        Assert.Contains(_log, e => e.Severity == LogSeverity.Info
            && e.Message == "learned: the dead of 14/1813 arrive in 14/1900");
        Assert.True(File.Exists(FileFor("paradigm")));
        // Seen again: nothing new, and nothing written over.
        Assert.Equal(LearnedArrivalRoomStore.Outcome.Known, store.Learn(TerraIsta, IstaGraveyard));
        Assert.Equal(1, store.Count);
    }

    // The file is the set's: a client started later reads what an earlier one
    // learned, and another set knows nothing of it.
    [Fact]
    public void WhatWasLearned_IsReloadedForItsSet_AndASetSwitchUsesTheOtherSetsPairs()
    {
        Client().Learn(TerraIsta, IstaGraveyard);

        LearnedArrivalRoomStore later = Client();
        Assert.Equal(new[] { IstaGraveyard }, later.ArrivalsFor(TerraIsta));

        later.OnActiveSetChanged("an edited board");
        Assert.Null(later.ArrivalsFor(TerraIsta));
        Assert.Equal(0, later.Count);
        later.Learn(TerraIsta, Elsewhere);
        Assert.Equal(new[] { Elsewhere }, later.ArrivalsFor(TerraIsta));

        later.OnActiveSetChanged("paradigm");
        Assert.Equal(new[] { IstaGraveyard }, later.ArrivalsFor(TerraIsta));

        // No set: nothing is held, and nothing can be kept.
        later.OnActiveSetChanged(null);
        Assert.Null(later.ArrivalsFor(TerraIsta));
        Assert.Equal(LearnedArrivalRoomStore.Outcome.NotKept, later.Learn(TerraIsta, IstaGraveyard));
    }

    // Two clients on one set: what one learns the other takes in on its next
    // heartbeat, and a write of its own merges with it.
    [Fact]
    public void ASecondClientsWrite_IsTakenIn_AndAWriteMergesWithIt()
    {
        LearnedArrivalRoomStore one = Client(), two = Client();
        Assert.False(one.TakeInOutsideChanges());

        two.Learn(TerraIsta, IstaGraveyard);

        Assert.Null(one.ArrivalsFor(TerraIsta));
        Assert.True(one.TakeInOutsideChanges());
        Assert.Equal(new[] { IstaGraveyard }, one.ArrivalsFor(TerraIsta));
        Assert.False(one.TakeInOutsideChanges());

        // Each learns another room without having looked: neither loses the other's.
        RoomKey fuego = new(14, 1812), talkiran = new(14, 15000);
        one.Learn(fuego, new RoomKey(14, 1850));
        two.Learn(talkiran, new RoomKey(14, 15010));
        one.TakeInOutsideChanges();
        Assert.Equal(3, one.Count);
        Assert.Equal(3, two.Count);
        Assert.Equal(3, Client().Count);
    }

    // A room seen to send its dead to two different rooms: both readings are kept
    // and neither is used. Said once; nothing is overwritten.
    [Fact]
    public void AContradictingPair_MakesTheRoomDisputed_AndNeitherReadingIsUsed()
    {
        LearnedArrivalRoomStore store = Client();
        store.Learn(TerraIsta, IstaGraveyard);

        Assert.Equal(LearnedArrivalRoomStore.Outcome.Disputed, store.Learn(TerraIsta, Elsewhere));

        Assert.Null(store.ArrivalsFor(TerraIsta));
        Assert.Equal((0, 1), (store.Count, store.DisputedCount));
        (RoomKey diedIn, IReadOnlyList<RoomKey> arrivals) = Assert.Single(store.Disputed);
        Assert.Equal(TerraIsta, diedIn);
        Assert.Equal(new[] { IstaGraveyard, Elsewhere }, arrivals);
        LogEntry warning = Assert.Single(_log, e => e.Severity == LogSeverity.Warn);
        Assert.Contains("14/1900", warning.Message);
        Assert.Contains("14/1950", warning.Message);

        // Either reading again, or a third: still disputed, and not said again.
        Assert.Equal(LearnedArrivalRoomStore.Outcome.Disputed, store.Learn(TerraIsta, IstaGraveyard));
        Assert.Equal(LearnedArrivalRoomStore.Outcome.Disputed, store.Learn(TerraIsta, new RoomKey(14, 1960)));
        Assert.Single(_log, e => e.Severity == LogSeverity.Warn);
        // It stays disputed for a client that starts later.
        Assert.Equal((0, 1), (Client().Count, Client().DisputedCount));
    }

    // Two clients that each saw it differently, neither having read the other's
    // yet: the second write finds the first on file and the room is disputed.
    [Fact]
    public void TwoClientsThatSawItDifferently_EndDisputed_NotWithTheLastWriteWinning()
    {
        LearnedArrivalRoomStore one = Client(), two = Client();
        one.Learn(TerraIsta, IstaGraveyard);

        Assert.Equal(LearnedArrivalRoomStore.Outcome.Disputed, two.Learn(TerraIsta, Elsewhere));

        one.TakeInOutsideChanges();
        Assert.Null(one.ArrivalsFor(TerraIsta));
        Assert.Equal(1, one.DisputedCount);
    }

    // A room that names no graveyard sends its dead to one of the engine's two
    // default rooms, by the character's evil points. Seen in either, it stands for
    // both; seen in each, it is no dispute.
    [Fact]
    public void TheTwoDefaultRooms_StandForEachOther()
    {
        LearnedArrivalRoomStore store = Client();
        RoomKey room = new(13, 4000);

        store.Learn(room, HangupDeathArrivalRooms.Temple);
        Assert.Equal(new[] { HangupDeathArrivalRooms.Temple, HangupDeathArrivalRooms.EvilTomb }, store.ArrivalsFor(room));

        Assert.Equal(LearnedArrivalRoomStore.Outcome.Learned, store.Learn(room, HangupDeathArrivalRooms.EvilTomb));
        Assert.Equal((1, 0), (store.Count, store.DisputedCount));
        Assert.DoesNotContain(_log, e => e.Severity == LogSeverity.Warn);
        // A third room is a dispute like any other.
        Assert.Equal(LearnedArrivalRoomStore.Outcome.Disputed, store.Learn(room, Elsewhere));
        Assert.Null(store.ArrivalsFor(room));
    }

    // A file that can't be read is left alone and what is held is kept.
    [Fact]
    public void AFileCaughtHalfWritten_IsLeftForTheNextLook()
    {
        LearnedArrivalRoomStore store = Client();
        store.Learn(TerraIsta, IstaGraveyard);

        File.WriteAllText(FileFor("paradigm"), "[ { \"DiedIn\": \"14/18");

        Assert.False(store.TakeInOutsideChanges());
        Assert.Equal(new[] { IstaGraveyard }, store.ArrivalsFor(TerraIsta));
    }
}

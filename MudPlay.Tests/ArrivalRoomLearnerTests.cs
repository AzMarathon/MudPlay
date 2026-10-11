using System.IO;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Learning where the dead of a room arrive from a death the client sees: the room
// the map was sure of when the death came, and the room the first display after
// it showed, with no move sent in between. Run on the real RoomTracker over a
// small graph: an island across the water with its own shrine, two grottoes the
// map can't tell apart, Darkwood Forest and the Temple, an arena, Kingsport and
// the Burial Vault.
public sealed class ArrivalRoomLearnerTests : IDisposable
{
    private const string Rooms = """
        [
          { "Map Number": 14, "Room Number": 1813, "Name": "Terra Ista, Landing",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "14/1814", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 1814, "Name": "Terra Ista, Path",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "14/1813", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 1900, "Name": "Isle Shrine",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "14/1814", "D": "0" },
          { "Map Number": 14, "Room Number": 1901, "Name": "Dark Grotto",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "14/1814", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 1902, "Name": "Dark Grotto",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "14/1813", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 109, "Name": "Darkwood Forest",
            "Light": -150, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/116", "E": "0", "W": "0",
            "NE": "1/110", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 2189, "Name": "Temple, Halls of the Dead",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "1/527", "D": "0" },
          { "Map Number": 16, "Room Number": 500, "Name": "Training Grounds",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "16/501", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 5000, "Name": "Kingsport, Market",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "14/5001", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 14, "Room Number": 1077, "Name": "Burial Vault",
            "Light": -50, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "14/1072", "D": "0" }
        ]
        """;

    private static readonly RoomKey Landing = new(14, 1813);
    private static readonly RoomKey Shrine = new(14, 1900);
    private static readonly RoomKey Darkwood = new(1, 109);
    private static readonly RoomKey Temple = new(1, 2189);
    private static readonly RoomKey Kingsport = new(14, 5000);
    private static readonly RoomKey Vault = new(14, 1077);

    private static readonly RoomObservation ShrineShown = new("Isle Shrine", new HashSet<Direction> { Direction.U });
    private static readonly RoomObservation TempleShown = new("Temple, Halls of the Dead", new HashSet<Direction> { Direction.U });
    private static readonly RoomObservation VaultShown = new("Burial Vault", new HashSet<Direction> { Direction.U });
    private static readonly RoomObservation GrottoShown = new("Dark Grotto", new HashSet<Direction> { Direction.E });

    private readonly string _root;
    private readonly RoomTracker _tracker;
    private readonly LearnedArrivalRoomStore _store;
    private readonly ArrivalRoomLearner _learner;
    private readonly List<LogEntry> _log = new();
    private HangupArrivalListTrust _trust = HangupArrivalListTrust.Paradigm;

    public ArrivalRoomLearnerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-arrivallearner-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), Rooms);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        _tracker = new RoomTracker(graph);

        LogService log = new();
        log.EntryAdded += _log.Add;
        _store = new LearnedArrivalRoomStore(log, set => Path.Combine(_root, set, "learned-arrival-rooms.json"));
        _store.OnActiveSetChanged("alpha");
        _learner = new ArrivalRoomLearner(_tracker, _store, () => _trust, graph.CouldBeTakenFor,
            inAlbion: room => room == Kingsport || room == Vault, log);
        // As AppServices wires it.
        _tracker.PlayerDeathObserved += _learner.OnPlayerDied;
        _tracker.StateChanged += _learner.OnRoomStateChanged;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    // The map is sure of the room, and the death lines are read there.
    private void DieIn(RoomKey room, bool arena = false)
    {
        _tracker.SetLocated(room);
        _tracker.NoteDeath(livesRemaining: 6, tookNothing: arena);
    }

    [Fact]
    public void AWitnessedDeath_TeachesWhereThatRoomsDeadArrive()
    {
        DieIn(Landing);
        Assert.Null(_store.ArrivalsFor(Landing));

        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(new[] { Shrine }, _store.ArrivalsFor(Landing));
        Assert.Contains(_log, e => e.Severity == LogSeverity.Info
            && e.Message == "learned: the dead of 14/1813 arrive in 14/1900");
        // That room only: the path beside it has taught nothing.
        Assert.Null(_store.ArrivalsFor(new RoomKey(14, 1814)));
        Assert.Equal(1, _store.Count);
    }

    // Only the first display after the death counts, and only with no move sent
    // before it: after either, the character may stand somewhere else.
    [Fact]
    public void AMoveSentBeforeTheRoomWasDrawn_TeachesNothing()
    {
        DieIn(Landing);
        _learner.NoteMoveSent();

        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(0, _store.Count);
    }

    [Fact]
    public void ARoomTheMapCantPlace_TeachesNothing_AndALaterDisplayIsNotTriedInstead()
    {
        DieIn(Landing);

        // Two grottoes look the same: the map isn't sure which this is.
        _tracker.NoteRoomObserved(GrottoShown);
        Assert.NotEqual(RoomConfidence.Confirmed, _tracker.State.Confidence);
        Assert.Equal(0, _store.Count);

        // The next display is of a room it can place, and is not the first.
        _tracker.NoteRoomObserved(ShrineShown);
        Assert.Equal(RoomConfidence.Confirmed, _tracker.State.Confidence);
        Assert.Equal(0, _store.Count);
    }

    // An arena death is the arena's own rule: where it puts the character says
    // nothing of a death that costs a life.
    [Fact]
    public void AnArenaDeath_TeachesNothing()
    {
        _trust = HangupArrivalListTrust.Unverified;
        DieIn(new RoomKey(16, 500), arena: true);

        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(0, _store.Count);
    }

    // A move was in flight when the death came: the room the map held is only
    // where the character was last known to be.
    [Fact]
    public void ADeathInARoomTheMapWasNotSureOf_TeachesNothing()
    {
        _tracker.SetLocated(Landing);
        _tracker.NoteMoveSent(Direction.N);
        Assert.NotEqual(RoomConfidence.Confirmed, _tracker.State.Confidence);
        _tracker.NoteDeath(livesRemaining: 6);
        Assert.False(_tracker.LastDeathRoomConfirmed);

        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(0, _store.Count);
    }

    // Out of the game before the room was drawn: the room the next entry shows is
    // not known to be the one the death left the character in.
    [Fact]
    public void LeavingTheGameBeforeTheRoomWasDrawn_TeachesNothing()
    {
        DieIn(Landing);
        _learner.Forget();

        _tracker.NoteRoomObserved(ShrineShown);

        Assert.Equal(0, _store.Count);
    }

    // Where the Stock list is the world's own, nothing is learned. A death that
    // ends where the list says is no news; one that doesn't is said, and the list
    // stands.
    [Theory]
    [InlineData(HangupArrivalListTrust.VanillaStock)]
    [InlineData(HangupArrivalListTrust.Paradigm)]
    public void WhereTheStockListIsTheWorldsOwn_NothingIsLearned(HangupArrivalListTrust trust)
    {
        _trust = trust;

        DieIn(Darkwood);
        _tracker.NoteRoomObserved(TempleShown);
        Assert.Equal(0, _store.Count);
        Assert.DoesNotContain(_log, e => e.Severity == LogSeverity.Warn);

        DieIn(Darkwood);
        _tracker.NoteRoomObserved(ShrineShown);
        Assert.Equal(0, _store.Count);
        LogEntry warning = Assert.Single(_log, e => e.Severity == LogSeverity.Warn);
        Assert.Contains("A death seen in 1/109 ended in 14/1900", warning.Message);
        Assert.Contains("1/2189 or 1/142", warning.Message);
        Assert.Contains("Not learned", warning.Message);
    }

    // On a board whose rooms may have been edited, the list is only Stock's word:
    // a death that ends somewhere else is learned, as one more room to say yes in.
    [Fact]
    public void OnAnEditedBoard_ADeathThatEndsSomewhereElseThanTheList_IsLearned()
    {
        _trust = HangupArrivalListTrust.Unverified;

        DieIn(Darkwood);
        _tracker.NoteRoomObserved(TempleShown);
        Assert.Equal(0, _store.Count);

        DieIn(Darkwood);
        _tracker.NoteRoomObserved(ShrineShown);
        Assert.Equal(new[] { Shrine }, _store.ArrivalsFor(Darkwood));
        Assert.DoesNotContain(_log, e => e.Severity == LogSeverity.Warn);
    }

    // Albion's dead are known to arrive in the Burial Vault: seeing it adds
    // nothing. Seen to end somewhere else, that room is learned for that room.
    [Fact]
    public void AnAlbionDeath_EndingInTheBurialVault_IsNoNews()
    {
        DieIn(Kingsport);
        _tracker.NoteRoomObserved(VaultShown);
        Assert.Equal(0, _store.Count);

        DieIn(Kingsport);
        _tracker.NoteRoomObserved(ShrineShown);
        Assert.Equal(new[] { Shrine }, _store.ArrivalsFor(Kingsport));
    }

    // A second death in the same room that ends somewhere else: the room becomes
    // disputed, and neither reading is used.
    [Fact]
    public void ASecondDeathThatEndsSomewhereElse_DisputesTheRoom()
    {
        DieIn(Landing);
        _tracker.NoteRoomObserved(ShrineShown);
        DieIn(Landing);
        _tracker.NoteRoomObserved(VaultShown);

        Assert.Null(_store.ArrivalsFor(Landing));
        Assert.Equal((0, 1), (_store.Count, _store.DisputedCount));
        Assert.Single(_log, e => e.Severity == LogSeverity.Warn);
    }
}

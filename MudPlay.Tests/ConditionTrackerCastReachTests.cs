using System.IO;
using System.Linq;
using System.Reflection;
using MudPlay.Game.Combat;
using MudPlay.Game.Conditions;
using MudPlay.Game.GameData;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// One applied line, several spells: which records latch goes by who could have cast
// each where the character stands. The message records are the Paradigm seed's for
// the freezing family, the game rows SpellCasterIndexTests' copies of the Paradigm
// set's.
public sealed class ConditionTrackerCastReachTests : IDisposable
{
    private readonly string _root;
    private readonly LogService _log = new() { Diagnostics = new LogDiagnosticState { DebugDiagnostics = true } };
    private readonly MessageStore _messages = new();
    private readonly ConditionTracker _tracker;
    private readonly List<MessageRecord> _applied = new();
    // What the tracker is told of the room; null is a position that isn't confirmed.
    private CastReach.Here? _here = new CastReach.Here(0, Array.Empty<string>());

    public ConditionTrackerCastReachTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-cast-reach-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        (_, SpellCasterIndex casters) = SpellCasterIndexTests.NewIndex(_root);

        _tracker = new ConditionTracker(_messages, _log);
        _tracker.ConditionApplied += _applied.Add;
        _tracker.SetCastReach(new CastReach(casters, () => _here));
    }

    public void Dispose()
    {
        _tracker.Dispose();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private static MessageRecord Record(
        string name, MessageFlags flags, string target, string applied, string endsWith, params int[] spells) =>
        new(
            Id: MessageRecord.ComputeId(name, "", target, "", applied, endsWith),
            Name: name,
            Flags: flags,
            RawFlagsHex: (ushort)flags,
            CasterMessage: string.Empty,
            TargetMessage: target,
            WitnessMessage: string.Empty,
            AppliedMessage: applied,
            AppliedEndsWith: endsWith,
            Links: spells.Select(n => new GameDataLink("Spells", n)).ToArray());

    // glacial mist (#1042) can be learned by players; galacial mist (#1336), the two
    // monster ice storms (#1363, #5027, one record text under two links, as the seed
    // has it) are cast by monsters alone; freezing (#1015) comes at the end of a
    // blizzard, one of which can be learned.
    private void AddTheFreezingFamily(bool withTheLearnableMist = true)
    {
        _messages.Messages.Add(Record("freezing water", MessageFlags.None,
            "You are freezing cold!", "{null}", "{null}", 511, 5242));
        if (withTheLearnableMist)
            _messages.Messages.Add(Record("glacial mist", MessageFlags.Confused,
                "An opalescent white mist surrounds you!", "You are freezing", "You are no longer freezing", 1042));
        _messages.Messages.Add(Record("galacial mist", MessageFlags.Confused,
            "An opalescent white mist surrounds you!", "You are freezing", "You are no longer freezing", 1336));
        _messages.Messages.Add(Record("ice storm", MessageFlags.None,
            "A freezing storm of ice surrounds you!", "You are freezing", "You are no longer freezing", 1363));
        _messages.Messages.Add(Record("ice storm", MessageFlags.None,
            "A freezing storm of ice surrounds you!", "You are freezing", "You are no longer freezing", 5027));
        _messages.Messages.Add(Record("freezing", MessageFlags.None,
            "Icy hail pelts you, freezing your bones!", "You are freezing", "You are no longer freezing", 1015));
    }

    private void Feed(string text) =>
        typeof(ConditionTracker)
            .GetMethod("OnLine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_tracker, new object[]
            {
                new LineExtractor.EmittedLine(text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false),
            });

    private IEnumerable<string> AppliedNames => _applied.Select(r => r.Name);

    private void Monsters(params string[] names) => _here = new CastReach.Here(0, names);

    [Fact]
    public void AnIceStormCasterHere_AndNoMistCaster_LatchesTheStormAndNoConfusion()
    {
        AddTheFreezingFamily(withTheLearnableMist: false);
        Monsters("ice sorceress");

        Feed("You are freezing");

        Assert.Contains("ice storm", AppliedNames);
        Assert.DoesNotContain("galacial mist", AppliedNames);
        Assert.False(_tracker.IsConfused);
    }

    // The same room with the whole family: the mist a player can learn is never left
    // out, since a player's cast is nothing the roster shows.
    [Fact]
    public void AnIceStormCasterHere_TheMistAPlayerCanLearn_IsNotLeftOut()
    {
        AddTheFreezingFamily();
        Monsters("ice sorceress");

        Feed("You are freezing");

        Assert.Contains("ice storm", AppliedNames);
        Assert.Contains("glacial mist", AppliedNames);
        Assert.Contains("freezing", AppliedNames);
        Assert.DoesNotContain("galacial mist", AppliedNames);
    }

    // The white queen casts galacial mist and the other ice storm.
    [Fact]
    public void AMistCasterHere_LatchesConfusedAsBefore()
    {
        AddTheFreezingFamily(withTheLearnableMist: false);
        Monsters("white queen");

        Feed("You are freezing");

        Assert.Contains("galacial mist", AppliedNames);
        Assert.Contains("ice storm", AppliedNames);
        Assert.True(_tracker.IsConfused);
    }

    [Fact]
    public void TheReportsLine_InTheUnderwaterPassage_LatchesNothing()
    {
        AddTheFreezingFamily();
        _here = new CastReach.Here(511, Array.Empty<string>());

        Feed("You are freezing cold!");

        Assert.Empty(_applied);
        Assert.Equal(MessageFlags.None, _tracker.ActiveFlags);
    }

    [Fact]
    public void WithNoCasterOfAnyOfThemInSight_EveryRecordLatchesAsBefore()
    {
        AddTheFreezingFamily();
        Monsters();

        Feed("You are freezing");

        Assert.Equal(4, _applied.Count);   // the two ice storm records are one Id
        Assert.True(_tracker.IsConfused);
    }

    [Fact]
    public void InARoomThatCantBeSeen_AMonstersSpellIsNotRuledOut()
    {
        AddTheFreezingFamily(withTheLearnableMist: false);
        _here = new CastReach.Here(0, Monsters: null);

        Feed("You are freezing");

        Assert.Contains("galacial mist", AppliedNames);
        Assert.True(_tracker.IsConfused);
    }

    [Fact]
    public void WithThePositionNotConfirmed_NothingIsLeftOut()
    {
        AddTheFreezingFamily(withTheLearnableMist: false);
        _here = null;

        Feed("You are freezing");

        Assert.Contains("galacial mist", AppliedNames);
        Assert.True(_tracker.IsConfused);
    }

    // Two records that are not the seeds', sharing an applied text: one for drowning
    // (#513), which the Underwater Passage's freezing water (#511) reaches through
    // holding breath (#512); one a hold for galacial mist (#1336), which only the
    // white queen casts.
    private void AddAChainedPair()
    {
        _messages.Messages.Add(Record("drowning (test)", MessageFlags.None,
            "{null}", "You are choking for air", "You can breathe again", 513));
        _messages.Messages.Add(Record("strangle (test)", MessageFlags.MovementPrevented,
            "{null}", "You are choking for air", "You can breathe again", 1336));
    }

    [Fact]
    public void ASpellTheRoomsSpellEndsIn_IsCastHere_AndTheMonstersIsLeftOut()
    {
        AddAChainedPair();
        _here = new CastReach.Here(511, Array.Empty<string>());

        Feed("You are choking for air!");

        Assert.Equal(new[] { "drowning (test)" }, AppliedNames);
        Assert.False(_tracker.IsMovementPrevented);
        Assert.Contains(_log.Snapshot(), e =>
            e.Message.Contains("cast here: 'drowning (test)' (room spell #511)", StringComparison.Ordinal)
            && e.Message.Contains("'strangle (test)' (MovementPrevented)", StringComparison.Ordinal));
    }

    // The room's half still stands in the dark; the monsters' half does not.
    [Fact]
    public void ASpellTheRoomsSpellEndsIn_InTheDark_LeavesTheMonstersSpellIn()
    {
        AddAChainedPair();
        _here = new CastReach.Here(511, Monsters: null);

        Feed("You are choking for air!");

        Assert.Equal(2, _applied.Count);
        Assert.True(_tracker.IsMovementPrevented);
    }

    [Fact]
    public void InAnotherRoom_WithTheMonsterHere_TheMonstersSpellIsTheOneLatched()
    {
        AddAChainedPair();
        _here = new CastReach.Here(829, new[] { "white queen" });

        Feed("You are choking for air!");

        // Drowning has a textblock among its casters, so it is never ruled out.
        Assert.Equal(2, _applied.Count);
        Assert.True(_tracker.IsMovementPrevented);
    }

    // ----- what of the roster can be vouched for ----------------------

    private static RoomEntitiesObservation Roster(params RoomEntity[] entities) =>
        new("Also here: …", entities, DateTimeOffset.UtcNow);

    private static RoomEntity Monster(string name) => new("fierce " + name, name, EntityKind.Monster, 1);

    private static bool Known(string name) => name is "ice sorceress" or "white queen";

    [Fact]
    public void MonstersSeen_AreTheRostersMonstersByTheirOwnNames()
    {
        IReadOnlyCollection<string>? seen = CastReach.MonstersSeen(
            Roster(Monster("ice sorceress"), new RoomEntity("Someone", "Someone", EntityKind.Player, null)),
            cannotSee: false, Known);

        Assert.Equal(new[] { "ice sorceress" }, seen);
    }

    [Fact]
    public void MonstersSeen_AnEmptyRoom_IsKnownToBeEmpty()
        => Assert.Empty(CastReach.MonstersSeen(Roster(), cannotSee: false, Known)!);

    [Fact]
    public void MonstersSeen_IsUnknown_WhenTheRoomCantBeSeen()
        => Assert.Null(CastReach.MonstersSeen(Roster(Monster("ice sorceress")), cannotSee: true, Known));

    [Fact]
    public void MonstersSeen_IsUnknown_BeforeAnyRosterWasRead()
        => Assert.Null(CastReach.MonstersSeen(null, cannotSee: false, Known));

    [Fact]
    public void MonstersSeen_IsUnknown_WithANameNobodyCanPlace()
    {
        Assert.Null(CastReach.MonstersSeen(
            Roster(Monster("ice sorceress"), new RoomEntity("a shape", "a shape", EntityKind.Unknown, null)),
            cannotSee: false, Known));
        Assert.Null(CastReach.MonstersSeen(Roster(Monster("snow leopard")), cannotSee: false, Known));
    }
}

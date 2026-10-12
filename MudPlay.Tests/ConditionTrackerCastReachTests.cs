using System.IO;
using System.Linq;
using System.Reflection;
using MudPlay.Game.Combat;
using MudPlay.Game.Conditions;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// One applied line, several spells: which records latch goes by who could have cast
// each where the character stands. The message records are the Paradigm seed's for
// the freezing family, the game rows SpellCasterIndexTests' copies of the Paradigm
// set's. Where the character stands is given the way the client gives it, through
// CastReach.Standing, so each thing it goes by is one a test can take away.
public sealed class ConditionTrackerCastReachTests : IDisposable
{
    private readonly string _root;
    private readonly LogService _log = new() { Diagnostics = new LogDiagnosticState { DebugDiagnostics = true } };
    private readonly MessageStore _messages = new();
    private readonly SpellCasterIndex _casters;
    private readonly ConditionTracker _tracker;
    private readonly List<MessageRecord> _applied = new();

    // What the client holds of the room: a lit room with no spell, its position
    // confirmed, and a roster read whole that lists nobody.
    private RoomConfidence _confidence = RoomConfidence.Confirmed;
    private int _roomSpell;
    private bool _dark;
    private bool _enteredBlind;
    private bool _blinded;
    private RoomEntitiesObservation? _roster = Roster();
    private bool _rosterIsWhole = true;

    public ConditionTrackerCastReachTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-cast-reach-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        (_, _casters) = SpellCasterIndexTests.NewIndex(_root);

        _tracker = new ConditionTracker(_messages, _log);
        _tracker.ConditionApplied += _applied.Add;
        _tracker.SetCastReach(new CastReach(_casters, Standing));
    }

    private CastReach.Here? Standing() => CastReach.Standing(
        _confidence, RoomWith(_roomSpell), _dark, _enteredBlind, _blinded,
        _roster, _rosterIsWhole, _casters.KnowsMonster);

    private static Room RoomWith(int spell) => new()
    {
        Key = new RoomKey(1, 1), Name = "Somewhere", Spell = spell,
        Exits = new Dictionary<Direction, RoomExit>(),
    };

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

    private void Feed(string text) => FeedAt(text, DateTimeOffset.UtcNow);

    private void FeedAt(string text, DateTimeOffset when) =>
        typeof(ConditionTracker)
            .GetMethod("OnLine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_tracker, new object[]
            {
                new LineExtractor.EmittedLine(text, Array.Empty<CellAttributes>(), when, IsPromptLine: false),
            });

    private IEnumerable<string> AppliedNames => _applied.Select(r => r.Name);

    private static RoomEntitiesObservation Roster(params string[] monsters) =>
        new("Also here: …", monsters.Select(Monster).ToArray(), DateTimeOffset.UtcNow);

    private static RoomEntity Monster(string name) => new("fierce " + name, name, EntityKind.Monster, 1);

    private void Monsters(params string[] names) => _roster = Roster(names);

    // ----- the freezing family ---------------------------------------

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
        _roomSpell = 511;

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

    // ----- what the roster and the position have to be ---------------
    // Each of these is the first test's room, where galacial mist is left out, with
    // one thing taken away, and then it is not.

    [Theory]
    [InlineData(RoomConfidence.Unknown)]
    [InlineData(RoomConfidence.Pending)]
    [InlineData(RoomConfidence.Suspect)]
    [InlineData(RoomConfidence.Lost)]
    [InlineData(RoomConfidence.PendingRespawn)]
    public void WithThePositionNotConfirmed_NothingIsLeftOut(RoomConfidence confidence)
    {
        AddTheFreezingFamily(withTheLearnableMist: false);
        Monsters("ice sorceress");
        _confidence = confidence;

        Feed("You are freezing");

        Assert.Contains("galacial mist", AppliedNames);
        Assert.True(_tracker.IsConfused);
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("entered blind")]
    [InlineData("blinded")]
    public void InARoomThatCantBeSeen_AMonstersSpellIsNotLeftOut(string why)
    {
        AddTheFreezingFamily(withTheLearnableMist: false);
        Monsters("ice sorceress");
        _dark = why == "dark";
        _enteredBlind = why == "entered blind";
        _blinded = why == "blinded";

        Feed("You are freezing");

        Assert.Contains("galacial mist", AppliedNames);
        Assert.True(_tracker.IsConfused);
    }

    [Fact]
    public void WithARosterNotKnownToBeWhole_AMonstersSpellIsNotLeftOut()
    {
        AddTheFreezingFamily(withTheLearnableMist: false);
        Monsters("ice sorceress");
        _rosterIsWhole = false;

        Feed("You are freezing");

        Assert.Contains("galacial mist", AppliedNames);
        Assert.True(_tracker.IsConfused);
    }


    // ----- a chain from the room's spell ------------------------------

    // Two records that are not the seeds', sharing an applied text: one for drowning
    // (#513), which the Underwater Passage's freezing water (#511) reaches through
    // holding breath (#512); one for galacial mist (#1336), which only the white
    // queen casts.
    private void AddAChainedPair(MessageFlags monstersFlags)
    {
        _messages.Messages.Add(Record("drowning (test)", MessageFlags.None,
            "{null}", "You are choking for air", "You can breathe again", 513));
        _messages.Messages.Add(Record("the queen's (test)", monstersFlags,
            "{null}", "You are choking for air", "You can breathe again", 1336));
    }

    [Fact]
    public void ASpellTheRoomsSpellEndsIn_IsCastHere_AndTheMonstersIsLeftOut()
    {
        AddAChainedPair(MessageFlags.Confused);
        _roomSpell = 511;

        Feed("You are choking for air!");

        Assert.Equal(new[] { "drowning (test)" }, AppliedNames);
        Assert.False(_tracker.IsConfused);
        Assert.Contains(_log.Snapshot(), e =>
            e.Message.Contains("cast here: 'drowning (test)' (room spell #511)", StringComparison.Ordinal)
            && e.Message.Contains("'the queen's (test)' (Confused)", StringComparison.Ordinal));
    }

    // The room's half still stands in the dark, since the room's spell is the map's;
    // the monsters' half does not, and a monster's spell is then not ruled out.
    [Fact]
    public void ASpellTheRoomsSpellEndsIn_InTheDark_LeavesTheMonstersSpellIn()
    {
        AddAChainedPair(MessageFlags.Confused);
        _roomSpell = 511;
        _dark = true;

        Feed("You are choking for air!");

        Assert.Equal(2, _applied.Count);
        Assert.True(_tracker.IsConfused);
    }

    [Fact]
    public void ASpellTheRoomsSpellEndsIn_WithNoRosterRead_LeavesTheMonstersSpellIn()
    {
        AddAChainedPair(MessageFlags.Confused);
        _roomSpell = 511;
        _roster = null;

        Feed("You are choking for air!");

        Assert.Equal(2, _applied.Count);
        Assert.True(_tracker.IsConfused);
    }

    // The queen is here and the room's spell is another: hers is cast here, and
    // drowning, which a textblock also casts, is never ruled out. Both latch.
    [Fact]
    public void WithTheMonsterHere_InAnotherRoom_BothLatch_DrowningHavingACasterNotInView()
    {
        AddAChainedPair(MessageFlags.Confused);
        _roomSpell = 829;
        Monsters("white queen");

        Feed("You are choking for air!");

        Assert.Equal(
            new[] { "drowning (test)", "the queen's (test)" }, AppliedNames.Order(StringComparer.Ordinal));
        Assert.True(_tracker.IsConfused);
    }

    // ----- what is never left out -------------------------------------

    // What stops the character moving, fighting or seeing is latched whoever the
    // roster lists: a monster can have followed in with no line to say so.
    [Theory]
    [InlineData(MessageFlags.MovementPrevented)]
    [InlineData(MessageFlags.AttackPrevented)]
    [InlineData(MessageFlags.Blinded)]
    [InlineData(MessageFlags.Confused | MessageFlags.MovementPrevented)]
    public void AHold_AnAttackStop_ABlindness_IsNeverLeftOut(MessageFlags flags)
    {
        AddAChainedPair(flags);
        _roomSpell = 511;

        Feed("You are choking for air!");

        Assert.Equal(2, _applied.Count);
        Assert.Equal(flags, _tracker.ActiveFlags);
    }

    // The lines of the next three tests are made up, so that two spells whose cast
    // lines differ share an applied text; the spells and their casters are the set's.
    private void AddTwoSpraysWithTheirOwnCastLines()
    {
        _messages.Messages.Add(Record("red spray (test)", MessageFlags.Confused,
            "The {source} sprays a red cloud at you!", "You feel dizzy", "Your head clears", 1336));
        _messages.Messages.Add(Record("blue spray (test)", MessageFlags.None,
            "The {source} sprays a blue cloud at you!", "You feel dizzy", "Your head clears", 1363));
    }

    // The red spray is the white queen's alone and she is not listed; the blue is the
    // ice sorceress's, who is. The red one's own cast line was just seen: it is the
    // one applied, whoever is listed.
    [Fact]
    public void ACastLineJustSeen_Decides_AheadOfWhoIsHere()
    {
        AddTwoSpraysWithTheirOwnCastLines();
        Monsters("ice sorceress");

        Feed("The white queen sprays a red cloud at you!");
        Feed("You feel dizzy!");

        Assert.Equal(new[] { "red spray (test)" }, AppliedNames);
        Assert.True(_tracker.IsConfused);
    }

    [Fact]
    public void WithNoCastLineSeen_WhoIsHereDecides()
    {
        AddTwoSpraysWithTheirOwnCastLines();
        Monsters("ice sorceress");

        Feed("You feel dizzy!");

        Assert.Equal(new[] { "blue spray (test)" }, AppliedNames);
        Assert.False(_tracker.IsConfused);
    }

    // A line holding two applied texts. The red spray's cast line was seen, so the
    // blue one is out and the red one in; the burn is the ice sorceress's, who is
    // here, and without the cast line the red spray would be left out beside it.
    [Fact]
    public void ARecordItsCastLineSinglesOut_IsNotLeftOut_BesideAnotherTextsRecord()
    {
        AddTwoSpraysWithTheirOwnCastLines();
        _messages.Messages.Add(Record("burn (test)", MessageFlags.None,
            "{null}", "your skin burns", "The burning stops", 1363));
        Monsters("ice sorceress");

        Feed("The white queen sprays a red cloud at you!");
        Feed("You feel dizzy and your skin burns!");

        Assert.Equal(new[] { "burn (test)", "red spray (test)" }, AppliedNames.Order(StringComparer.Ordinal));
        Assert.True(_tracker.IsConfused);
    }

    // The cast line speaks before who is here is asked: the blue spray it rules out
    // is the only record with a caster in the room, and with it gone nobody listed
    // casts any of the rest, so the ache, a spell of the absent queen's, stays.
    [Fact]
    public void WhoIsHere_IsAskedOnlyOfWhatTheCastLineLeavesStanding()
    {
        AddTwoSpraysWithTheirOwnCastLines();
        _messages.Messages.Add(Record("ache (test)", MessageFlags.Poisoned,
            "{null}", "your bones ache", "The ache passes", 1336));
        Monsters("ice sorceress");

        Feed("The white queen sprays a red cloud at you!");
        Feed("You feel dizzy and your bones ache!");

        Assert.Equal(new[] { "ache (test)", "red spray (test)" }, AppliedNames.Order(StringComparer.Ordinal));
        Assert.True(_tracker.IsPoisoned);
    }

    // One cast line shared by every record says nothing of which was cast, and who is
    // here still decides.
    [Fact]
    public void ACastLineAllOfThemShare_ProtectsNoneOfThem()
    {
        _messages.Messages.Add(Record("red spray (test)", MessageFlags.Confused,
            "The {source} sprays a cloud at you!", "You feel dizzy", "Your head clears", 1336));
        _messages.Messages.Add(Record("blue spray (test)", MessageFlags.None,
            "The {source} sprays a cloud at you!", "You feel dizzy", "Your head clears", 1363));
        Monsters("ice sorceress");

        Feed("The ice sorceress sprays a cloud at you!");
        Feed("You feel dizzy!");

        Assert.Equal(new[] { "blue spray (test)" }, AppliedNames);
        Assert.False(_tracker.IsConfused);
    }

    // ----- CastReach, asked directly ----------------------------------

    private CastReach.Verdict VerdictOn(int spell, CastReach.Here here) =>
        new CastReach(_casters, () => here).Of(Record("r", MessageFlags.None, "", "x", "y", spell), here, out _);

    [Fact]
    public void AMonstersSpell_IsRuledOutOnlyByARosterThatIsKnown()
    {
        Assert.Equal(CastReach.Verdict.OutOfReach, VerdictOn(1336, new CastReach.Here(0, Array.Empty<string>())));
        Assert.Equal(CastReach.Verdict.OutOfReach, VerdictOn(1336, new CastReach.Here(0, new[] { "ice sorceress" })));
        Assert.Equal(CastReach.Verdict.Unknown, VerdictOn(1336, new CastReach.Here(0, Monsters: null)));
        Assert.Equal(CastReach.Verdict.InReach, VerdictOn(1336, new CastReach.Here(0, new[] { "white queen" })));
    }

    // Confusion #829 is the spell of two rooms and of no monster: the room alone
    // rules it in or out, seen or not.
    [Fact]
    public void ARoomsSpell_IsRuledOutByTheRoom_WhateverTheRoster()
    {
        Assert.Equal(CastReach.Verdict.OutOfReach, VerdictOn(829, new CastReach.Here(511, Monsters: null)));
        Assert.Equal(CastReach.Verdict.InReach, VerdictOn(829, new CastReach.Here(829, Monsters: null)));
    }

    [Fact]
    public void ASpellWithACasterNotInView_OrNotInTheSet_IsNeverRuledOut()
    {
        Assert.Equal(CastReach.Verdict.Unknown, VerdictOn(1042, new CastReach.Here(0, Array.Empty<string>())));
        Assert.Equal(CastReach.Verdict.Unknown, VerdictOn(31999, new CastReach.Here(0, Array.Empty<string>())));
    }

    // ----- Standing: what the client's own state lets it go by -------

    private static bool Known(string name) => name is "ice sorceress" or "white queen";

    private static CastReach.Here? StandingIn(
        RoomConfidence confidence = RoomConfidence.Confirmed, bool noRoom = false,
        bool dark = false, bool enteredBlind = false, bool blinded = false,
        bool noRoster = false, bool rosterIsWhole = true) =>
        CastReach.Standing(
            confidence, noRoom ? null : RoomWith(511), dark, enteredBlind, blinded,
            noRoster ? null : Roster("ice sorceress"), rosterIsWhole, Known);

    [Fact]
    public void Standing_ConfirmedAndSeen_GivesTheRoomsSpellAndTheMonsters()
    {
        CastReach.Here here = StandingIn()!.Value;

        Assert.Equal(511, here.RoomSpell);
        Assert.Equal(new[] { "ice sorceress" }, here.Monsters);
    }

    [Theory]
    [InlineData(RoomConfidence.Unknown)]
    [InlineData(RoomConfidence.Pending)]
    [InlineData(RoomConfidence.Suspect)]
    [InlineData(RoomConfidence.Lost)]
    [InlineData(RoomConfidence.PendingRespawn)]
    public void Standing_IsNowhere_UnlessThePositionIsConfirmed(RoomConfidence confidence)
        => Assert.Null(StandingIn(confidence));

    [Fact]
    public void Standing_IsNowhere_WithNoRoom()
        => Assert.Null(StandingIn(noRoom: true));

    [Fact]
    public void Standing_KeepsTheRoomsSpell_AndGivesNoMonsters_WhenTheRoomCantBeSeen()
    {
        foreach (CastReach.Here? unseen in new[]
                 {
                     StandingIn(dark: true), StandingIn(enteredBlind: true), StandingIn(blinded: true),
                     StandingIn(noRoster: true), StandingIn(rosterIsWhole: false),
                 })
        {
            Assert.Equal(511, unseen!.Value.RoomSpell);
            Assert.Null(unseen.Value.Monsters);
        }
    }

    // ----- what of the roster can be vouched for ----------------------

    [Fact]
    public void MonstersSeen_AreTheRostersMonstersByTheirOwnNames()
    {
        IReadOnlyCollection<string>? seen = CastReach.MonstersSeen(
            new RoomEntitiesObservation("Also here: …",
                new[] { Monster("ice sorceress"), new RoomEntity("Someone", "Someone", EntityKind.Player, null) },
                DateTimeOffset.UtcNow),
            cannotSee: false, rosterIsWhole: true, Known);

        Assert.Equal(new[] { "ice sorceress" }, seen);
    }

    [Fact]
    public void MonstersSeen_AnEmptyRoom_IsKnownToBeEmpty()
        => Assert.Empty(CastReach.MonstersSeen(Roster(), cannotSee: false, rosterIsWhole: true, Known)!);

    [Fact]
    public void MonstersSeen_IsUnknown_WhenTheRoomCantBeSeen()
        => Assert.Null(CastReach.MonstersSeen(Roster("ice sorceress"), cannotSee: true, rosterIsWhole: true, Known));

    [Fact]
    public void MonstersSeen_IsUnknown_WhenSomeoneMayHaveComeInUnlisted()
        => Assert.Null(CastReach.MonstersSeen(Roster("ice sorceress"), cannotSee: false, rosterIsWhole: false, Known));

    [Fact]
    public void MonstersSeen_IsUnknown_BeforeAnyRosterWasRead()
        => Assert.Null(CastReach.MonstersSeen(null, cannotSee: false, rosterIsWhole: true, Known));

    [Fact]
    public void MonstersSeen_IsUnknown_WithANameNobodyCanPlace()
    {
        Assert.Null(CastReach.MonstersSeen(
            new RoomEntitiesObservation("Also here: …",
                new[] { Monster("ice sorceress"), new RoomEntity("a shape", "a shape", EntityKind.Unknown, null) },
                DateTimeOffset.UtcNow),
            cannotSee: false, rosterIsWhole: true, Known));
        Assert.Null(CastReach.MonstersSeen(Roster("snow leopard"), cannotSee: false, rosterIsWhole: true, Known));
    }
}

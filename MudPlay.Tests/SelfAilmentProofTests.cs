using MudPlay.Game.Conditions;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Our own latched blindness or hold dropped on outside proof that it is over: any
// room shown (a blind move and a blind look both show only "You are blind."), a move
// that lands (a held character can't walk), and a cure blindness landing on us (it
// prints no wear-off). Driven on TrackerWireRig: the game's text goes through the
// terminal into the tracker, the router and the condition tracker as the client wires
// them.
public sealed class SelfAilmentProofTests
{
    private static readonly RoomKey Gate = new(1, 1);

    private static readonly string Rooms = TrackerWireRig.Rooms(new[]
    {
        TrackerWireRig.Room(1, 1, "Gate", ("N", "1/2")),
        TrackerWireRig.Room(1, 2, "Yard", ("S", "1/1")),
    });

    private sealed class World : IDisposable
    {
        public TrackerWireRig Rig { get; }
        public MessageStore Messages { get; } = new();
        public ConditionTracker Conditions { get; }

        public World()
        {
            Messages.Messages.Add(Record("blind", MessageFlags.Blinded, "You are blind", "You can see again"));
            Messages.Messages.Add(Record("breathes", MessageFlags.Blinded | MessageFlags.Confused,
                "You struggle for breath!", "The effects of the mummy's breath wears off!"));
            Messages.Messages.Add(Record("hold", MessageFlags.MovementPrevented,
                "You are paralyzed", "You can move again"));
            Rig = new TrackerWireRig(Rooms, prompt: "[HP=100/MA=50]:") { Position = Gate };
            Conditions = new ConditionTracker(Messages, Rig.Log);
            Conditions.AttachLineExtractor(Rig.Lines);
            SelfAilmentProof.Wire(Rig.Tracker, Conditions, Rig.Router, () => Cures, () => "Francisco Sanders");
            Rig.ShowRoom();
            Assert.Equal(Gate, Rig.Believed);
        }

        public void Dispose()
        {
            Conditions.Dispose();
            Rig.Dispose();
        }
    }

    // Cure blindness (#87) and cure poison (#19) as the shipped message records word
    // them: caster, target and witness lines, no wear-off.
    private static readonly IReadOnlyList<CureCastMatcher> Cures = new[]
    {
        Cure(MessageFlags.Blinded, "cure blindness"),
        Cure(MessageFlags.Poisoned, "cure poison"),
    };

    private static CureCastMatcher Cure(MessageFlags ailment, string spell) => new(
        ailment, spell,
        CasterMessageMatcher.TryCreate("You cast {spellname} on {target}!")!,
        CasterMessageMatcher.TryCreate("{source} casts {spellname} on {target}!"),
        CasterMessageMatcher.TryCreate("{source} casts {spellname} upon you!"));

    private static MessageRecord Record(string name, MessageFlags flags, string applied, string ends) =>
        new(
            Id: MessageRecord.ComputeId(name, "", "", "", applied, ends),
            Name: name,
            Flags: flags,
            RawFlagsHex: (ushort)flags,
            CasterMessage: string.Empty,
            TargetMessage: string.Empty,
            WitnessMessage: string.Empty,
            AppliedMessage: applied,
            AppliedEndsWith: ends);

    // The cleric cast cure blindness on itself every 15 s for as long as a blindness
    // whose wear-off it never saw stayed latched, walking a loop whose rooms it plainly
    // saw (report CureBlindlessBugReport, 2026-10-22).
    [Theory]
    [InlineData("n")]
    [InlineData("l")]
    public void LatchedBlindness_ARoomShown_Cleared(string command)
    {
        using World w = new();
        w.Rig.Show("You are blind!");
        Assert.True(w.Conditions.IsBlinded);

        w.Rig.Do(command);

        Assert.False(w.Conditions.IsBlinded);
    }

    // Blind, a move goes through and a look answers, each showing only "You are blind.".
    [Fact]
    public void Blindness_MoveAndLookShowOnlyYouAreBlind_StaysLatched()
    {
        using World w = new();
        w.Rig.Show("You are blind!");
        w.Rig.NextMoveShows.Enqueue("You are blind.");
        w.Rig.Do("n");
        w.Rig.NextAnswers.Enqueue("You are blind.");
        w.Rig.Do("l");

        Assert.True(w.Conditions.IsBlinded);
    }

    // A latch that blinds and confuses is left: the room display proves sight, not that
    // the confusion is over, and dropping the record would end both.
    [Fact]
    public void BlindAndConfusedLatch_ARoomShown_Kept()
    {
        using World w = new();
        w.Rig.Show("You struggle for breath!");

        w.Rig.Do("n");

        Assert.True(w.Conditions.IsConfused);
        Assert.True(w.Conditions.IsBlinded);
    }

    // A held character can't walk: a move that lands drops a hold latched past its
    // wear-off (report paradigm-20261003-161904). A look proves nothing about moving.
    [Fact]
    public void LatchedHold_ALookKeepsIt_AMoveClearsIt()
    {
        using World w = new();
        w.Rig.Show("You are paralyzed!");

        w.Rig.Do("l");
        Assert.True(w.Conditions.IsMovementPrevented);

        w.Rig.Do("n");
        Assert.False(w.Conditions.IsMovementPrevented);
    }

    // Cure blindness prints its spell lines and no wear-off (user, 2026-10-23): the
    // cast landing on us is the end of the blindness, ours or another's.
    [Theory]
    [InlineData("You cast cure blindness on Francisco!", false)]
    [InlineData("Kenwood casts cure blindness upon you!", false)]
    [InlineData("You cast cure blindness on Kenwood!", true)]
    [InlineData("Kenwood casts cure blindness on Farmer!", true)]
    [InlineData("You cast cure poison on Francisco!", true)]
    public void CureBlindnessLandingOnUs_ClearsOurBlindness(string line, bool stillBlind)
    {
        using World w = new();
        w.Rig.Show("You are blind!");

        w.Rig.Show(line);

        Assert.Equal(stillBlind, w.Conditions.IsBlinded);
    }

    // The cure ends a blindness that rides with a confusion as well, but the record is
    // left, as for a room shown.
    [Fact]
    public void CureBlindness_BlindAndConfusedLatch_Kept()
    {
        using World w = new();
        w.Rig.Show("You struggle for breath!");

        w.Rig.Show("You cast cure blindness on Francisco!");

        Assert.True(w.Conditions.IsConfused);
    }
}

using MudPlay.Game.Conditions;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A move that lands shown by its room's own display drops a latched hold, and a
// latched blindness, whose wear-off line never came. Driven on TrackerWireRig: the
// game's text goes through the terminal into the tracker and the condition tracker
// as the client wires them.
public sealed class MoveDisprovesAilmentsTests
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
            MoveDisprovesAilments.Wire(Rig.Tracker, Conditions);
            Rig.ShowRoom();
            Assert.Equal(Gate, Rig.Believed);
        }

        public void Dispose()
        {
            Conditions.Dispose();
            Rig.Dispose();
        }
    }

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
    [Fact]
    public void LatchedBlindness_MoveShowsTheRoom_Cleared()
    {
        using World w = new();
        w.Rig.Show("You are blind!");
        Assert.True(w.Conditions.IsBlinded);

        w.Rig.Do("n");

        Assert.False(w.Conditions.IsBlinded);
    }

    // Blind, a move goes through but shows only "You are blind.": no proof of sight.
    [Fact]
    public void Blindness_MoveShowsOnlyYouAreBlind_StaysLatched()
    {
        using World w = new();
        w.Rig.Show("You are blind!");
        w.Rig.NextMoveShows.Enqueue("You are blind.");

        w.Rig.Do("n");

        Assert.True(w.Conditions.IsBlinded);
    }

    // A latch that blinds and confuses is left: the room display proves sight, not that
    // the confusion is over, and dropping the record would end both.
    [Fact]
    public void BlindAndConfusedLatch_MoveShowsTheRoom_Kept()
    {
        using World w = new();
        w.Rig.Show("You struggle for breath!");

        w.Rig.Do("n");

        Assert.True(w.Conditions.IsConfused);
        Assert.True(w.Conditions.IsBlinded);
    }

    // A held character can't walk: a move that lands drops a hold latched past its
    // wear-off (report paradigm-20261003-161904).
    [Fact]
    public void LatchedHold_MoveLands_Cleared()
    {
        using World w = new();
        w.Rig.Show("You are paralyzed!");
        Assert.True(w.Conditions.IsMovementPrevented);

        w.Rig.Do("n");

        Assert.False(w.Conditions.IsMovementPrevented);
    }
}

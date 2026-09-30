using MudPlay.Game.Cash;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Report paradigm-20260929-224330: a deposit trip that started mid-loop walked past
// three monsters. The hold now waits until the detour leaves the loop's rooms, and it
// flips the real Auto-Combat toggle (the main window acts on HoldChanged).
public sealed class DetourCombatHoldTests
{
    private static readonly Loop GrindLoop = new("grind", new[] { new RoomKey(9, 1), new RoomKey(9, 3) });
    private static readonly RoomKey[] LoopArea = { new(9, 1), new(9, 2), new(9, 3) };

    private sealed class World
    {
        public bool Depositing;
        public bool Selling;
        public DetourResume Resume = new(DetourResumeKind.Loop, GrindLoop);
        public CashSettings Cash = new() { NoCombatOnDepositTrip = true, NoCombatOnSellDetour = true };
        public RoomKey? Here = new(9, 2);
        public List<bool> Changes { get; } = new();

        public DetourCombatHold Build()
        {
            DetourCombatHold hold = new(
                () => Selling, () => Resume, () => Depositing, () => Resume, () => Cash,
                _ => LoopArea, () => Here);
            hold.HoldChanged += Changes.Add;
            return hold;
        }
    }

    [Fact]
    public void DepositTrip_HoldsCombatOnlyOutsideTheLoopsRooms()
    {
        World w = new();
        DetourCombatHold hold = w.Build();

        w.Depositing = true;
        hold.Evaluate();
        Assert.Null(hold.HeldFor);                         // still in the loop's rooms

        w.Here = new RoomKey(1, 297);                     // out on the way to the bank
        hold.Evaluate();
        Assert.Equal("auto-deposit trip", hold.HeldFor);

        w.Here = new RoomKey(9, 3);                       // back in the loop's rooms
        hold.Evaluate();
        Assert.Null(hold.HeldFor);
        Assert.Equal(new[] { true, false }, w.Changes);
    }

    [Fact]
    public void Hold_ReleasesWhenTheDetourEnds()
    {
        World w = new() { Here = new RoomKey(1, 297) };
        DetourCombatHold hold = w.Build();
        w.Depositing = true;
        hold.Evaluate();
        Assert.NotNull(hold.HeldFor);

        w.Depositing = false;                               // stopped by hand, no move
        hold.Evaluate();
        Assert.Null(hold.HeldFor);
        Assert.Equal(new[] { true, false }, w.Changes);
    }

    [Fact]
    public void BoxOff_NeverHolds()
    {
        World w = new() { Here = new RoomKey(1, 297) };
        w.Cash.NoCombatOnDepositTrip = false;
        DetourCombatHold hold = w.Build();
        w.Depositing = true;
        hold.Evaluate();
        Assert.Null(hold.HeldFor);
        Assert.Empty(w.Changes);
    }

    [Fact]
    public void DetourFromAutoLair_HoldsForTheWholeTrip()
    {
        World w = new() { Resume = new DetourResume(DetourResumeKind.Lair) };
        DetourCombatHold hold = w.Build();
        w.Selling = true;
        hold.Evaluate();
        Assert.Equal("auto-sell detour", hold.HeldFor);
    }
}

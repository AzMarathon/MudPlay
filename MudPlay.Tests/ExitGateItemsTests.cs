using System.Collections.Generic;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// The collector every gate-item consumer shares. These pin the two things that
// matter: which hints contribute a possession requirement (a multi-action held
// item and an item-use teleport used to be invisible to the planner while the
// blocked-route message saw them), and that a locked door does NOT — a key is one
// of several openers, so it stays a DoorKey gate rather than a carry gate.
public sealed class ExitGateItemsTests
{
    private static RoomExit Exit(RoomExitHint hint, int keyItemId = 0,
        MultiActionExitData? multiAction = null, int statRequirement = 0)
        => new(new RoomKey(1, 2), hint, RawHint: null,
            StatRequirement: statRequirement, KeyItemId: keyItemId, MultiAction: multiAction);

    private static MultiActionExitData Actions(params int[] requiredItemIds)
    {
        var steps = new List<ExitAction>();
        for (int i = 0; i < requiredItemIds.Length; i++)
            steps.Add(new ExitAction(i + 1, new[] { "pull lever" }, RemoteSourceRoom: null,
                RequiredItemId: requiredItemIds[i]));
        return new MultiActionExitData(steps.Count, RequiresSpecificOrder: false, steps);
    }

    [Theory]
    [InlineData(RoomExitHint.Item)]
    [InlineData(RoomExitHint.Ticket)]
    [InlineData(RoomExitHint.Teleport)]
    public void CollectsKeyItemId_ForPossessionHints(RoomExitHint hint)
        => Assert.Equal(new[] { 42 }, ExitGateItems.Of(Exit(hint, keyItemId: 42)));

    [Fact]
    public void CollectsHeldItem_FromMultiActionStep()
        => Assert.Equal(
            new[] { 807 },
            ExitGateItems.Of(Exit(RoomExitHint.MultiActionHidden, multiAction: Actions(807))));

    [Fact]
    public void CollectsEveryDistinctHeldItem_AcrossSteps()
        => Assert.Equal(
            new[] { 807, 815 },
            ExitGateItems.Of(Exit(RoomExitHint.MultiActionHidden, multiAction: Actions(807, 0, 815, 807))));

    // A lever-only hidden exit needs no possession — nothing to fetch.
    [Fact]
    public void CollectsNothing_FromItemlessMultiAction()
        => Assert.Empty(ExitGateItems.Of(Exit(RoomExitHint.MultiActionHidden, multiAction: Actions(0))));

    // A key is one of several openers, so a locked door is never a carry gate.
    [Fact]
    public void SkipsKeyLockedDoor()
        => Assert.Empty(ExitGateItems.Of(
            Exit(RoomExitHint.KeyLocked, keyItemId: 806, statRequirement: 31)));

    [Fact]
    public void SkipsPlainDoorAndUnhintedExit()
    {
        Assert.Empty(ExitGateItems.Of(Exit(RoomExitHint.Door, statRequirement: 80)));
        Assert.Empty(ExitGateItems.Of(Exit(RoomExitHint.None)));
    }

    // Collect appends without duplicating, so a route can accumulate across hops
    // that re-cross the same gate.
    [Fact]
    public void Collect_AppendsWithoutDuplicating()
    {
        var into = new List<int> { 807 };
        ExitGateItems.Collect(Exit(RoomExitHint.Item, keyItemId: 807), into);
        ExitGateItems.Collect(Exit(RoomExitHint.Item, keyItemId: 5), into);
        Assert.Equal(new[] { 807, 5 }, into);
    }
}

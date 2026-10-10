using MudPlay.Game.Events;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// The "which waiting events should run?" prompt: what it lists, and what its
// ticks and buttons hand back to the events manager.
public sealed class HeldEventsPromptViewModelTests
{
    private static EventManager.HeldQueueEntry Entry(string name, int waitedMinutes, int heldMinutes) =>
        new(new ScheduledEvent
            {
                Name = name,
                TriggerType = EventTriggerType.Every,
                ActionType = EventActionType.WalkTo,
                WalkToTarget = new RoomRef(1, 2),
                Then = EventThenType.Nothing,
            },
            TimeSpan.FromMinutes(waitedMinutes), TimeSpan.FromMinutes(heldMinutes));

    private static HeldEventsPromptViewModel Prompt(out List<IReadOnlyList<ScheduledEvent>?> answers, params string[] names)
    {
        HeldEventsPromptViewModel vm = new(names.Select(n => Entry(n, 75, 12)).ToList());
        List<IReadOnlyList<ScheduledEvent>?> got = new();
        vm.CloseRequested += got.Add;
        answers = got;
        return vm;
    }

    [Fact]
    public void ListsEveryWaitingEvent_Ticked_WithWhatItDoesAndHowLongItWaited()
    {
        HeldEventsPromptViewModel vm = Prompt(out _, "bank run", "boss");

        Assert.Equal(new[] { "bank run", "boss" }, vm.Rows.Select(r => r.Name));
        Assert.All(vm.Rows, r => Assert.True(r.Run));
        Assert.Equal("Walk to 1/2 → stop", vm.Rows[0].What);
        Assert.Equal("waiting 1 h 15 min, 12 min of it with Auto-All off", vm.Rows[0].WaitedText);
        Assert.Equal("Run all 2", vm.ConfirmLabel);
    }

    [Fact]
    public void Confirm_HandsBackTheTickedEvents_InTheirOrder()
    {
        HeldEventsPromptViewModel vm = Prompt(out List<IReadOnlyList<ScheduledEvent>?> answers, "a", "b", "c");
        vm.Rows[1].Run = false;
        Assert.Equal("Run 2, drop 1", vm.ConfirmLabel);

        vm.ConfirmCommand.Execute(null);

        Assert.Equal(new[] { "a", "c" }, Assert.Single(answers)!.Select(e => e.Name));
        Assert.False(vm.Withdrawn);
    }

    [Fact]
    public void SelectNone_ThenConfirm_HandsBackAnEmptyAnswer_NotNoAnswer()
    {
        HeldEventsPromptViewModel vm = Prompt(out List<IReadOnlyList<ScheduledEvent>?> answers, "a", "b");

        vm.SelectNoneCommand.Execute(null);
        Assert.All(vm.Rows, r => Assert.False(r.Run));
        Assert.Equal("Drop all 2", vm.ConfirmLabel);
        vm.ConfirmCommand.Execute(null);

        Assert.Empty(Assert.Single(answers)!);
    }

    [Fact]
    public void SelectAll_TicksEveryRowAgain()
    {
        HeldEventsPromptViewModel vm = Prompt(out _, "a", "b");
        vm.SelectNoneCommand.Execute(null);

        vm.SelectAllCommand.Execute(null);

        Assert.All(vm.Rows, r => Assert.True(r.Run));
        Assert.Equal("Run all 2", vm.ConfirmLabel);
    }

    // The confirm label follows the ticks, so the button always says what it does.
    [Fact]
    public void ConfirmLabel_IsRaisedWhenATickChanges()
    {
        HeldEventsPromptViewModel vm = Prompt(out _, "a");
        List<string?> changed = new();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Rows[0].Run = false;

        Assert.Contains(nameof(HeldEventsPromptViewModel.ConfirmLabel), changed);
        Assert.Equal("Drop it", vm.ConfirmLabel);
    }

    // Taken back by the manager: closes with no answer, and says so, so the
    // opener does not treat it as the user putting the choice off.
    [Fact]
    public void Withdraw_ClosesWithNoAnswer_AndIsMarked()
    {
        HeldEventsPromptViewModel vm = Prompt(out List<IReadOnlyList<ScheduledEvent>?> answers, "a");

        vm.Withdraw();

        Assert.Null(Assert.Single(answers));
        Assert.True(vm.Withdrawn);
    }
}

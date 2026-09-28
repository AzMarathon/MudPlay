using System.Reflection;
using MudPlay.Game.Conditions;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// While we're afraid our own navigation waits: fear runs us through obvious exits
// at random, so moves of ours only fight it. The hold lifts on the wear-off line.
public sealed class SelfFearMovementGateTests
{
    private static MessageRecord Fear() => new(
        Id: MessageRecord.ComputeId("terror", "", "", "", "You are afraid!", "The effects of fear wear off!"),
        Name: "terror",
        Flags: MessageFlags.Fear,
        RawFlagsHex: (ushort)MessageFlags.Fear,
        CasterMessage: string.Empty,
        TargetMessage: string.Empty,
        WitnessMessage: string.Empty,
        AppliedMessage: "You are afraid!",
        AppliedEndsWith: "The effects of fear wear off!");

    [Fact]
    public void Afraid_HoldsNavigation_UntilTheFearWearsOff()
    {
        LogService log = new();
        MessageStore messages = new();
        messages.Messages.Add(Fear());
        using ConditionTracker conditions = new(messages, log);
        MovementCoordinator coordinator = new(log);
        using SelfFearMovementGate gate = new(conditions, coordinator, log);

        Feed(conditions, "You are afraid!");
        Assert.Contains(MovementCoordinator.FearGate, coordinator.AssertedGates);

        Feed(conditions, "The effects of fear wear off!");
        Assert.DoesNotContain(MovementCoordinator.FearGate, coordinator.AssertedGates);
    }

    private static void Feed(ConditionTracker conditions, string text) =>
        typeof(ConditionTracker)
            .GetMethod("OnLine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(conditions, new object[]
            {
                new LineExtractor.EmittedLine(text, System.Array.Empty<CellAttributes>(), DateTimeOffset.UnixEpoch,
                    IsPromptLine: false),
            });
}

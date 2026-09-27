using MudPlay.Game.Train;
using Xunit;

namespace MudPlay.Tests;

// The trainer's "not enough money" refusal: one funding recovery per run, then the
// run gives up and backs off. Recovering on every refusal is the walk-to-the-
// trainer-and-back loop the gate exists to stop.
public sealed class TrainRefusalGateTests
{
    [Fact]
    public void FirstRefusal_RecoversBySeekingFunds()
    {
        var gate = new TrainRefusalGate();
        Assert.True(gate.TryClaimRecovery(canRecover: true));
    }

    [Fact]
    public void SecondRefusalInTheSameRun_GivesUp()
    {
        var gate = new TrainRefusalGate();
        gate.TryClaimRecovery(canRecover: true);

        Assert.False(gate.TryClaimRecovery(canRecover: true));
        Assert.False(gate.TryClaimRecovery(canRecover: true));
    }

    [Fact]
    public void ANewRun_GetsItsRecoveryBack()
    {
        // Resetting only on Train Now left every later armed run without one.
        var gate = new TrainRefusalGate();
        gate.TryClaimRecovery(canRecover: true);

        gate.Reset();

        Assert.True(gate.TryClaimRecovery(canRecover: true));
    }

    [Fact]
    public void NothingToRecoverWith_GivesUpWithoutSpendingTheRecovery()
    {
        // No funding router, trainer or known position: back off now. That refusal
        // doesn't use up the run's one recovery.
        var gate = new TrainRefusalGate();

        Assert.False(gate.TryClaimRecovery(canRecover: false));
        Assert.True(gate.TryClaimRecovery(canRecover: true));
    }
}

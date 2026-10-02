using MudPlay.Game.Train;
using Xunit;

namespace MudPlay.Tests;

// What a train run does with the funding router's answer. Only a collecting errand
// may leave the run waiting: a short answer comes back with the router idle, and a
// run parked on it never hears Finished, so it stays busy until Reset States.
public sealed class TrainFundingHandoffTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collecting_IsTheOnlyAnswerTheRunWaitsOn(bool midRun)
    {
        Assert.Equal(TrainFundingHandoff.AwaitErrand,
            TrainFundingHandoffRule.For(TrainFundingStart.Collecting, midRun));
        Assert.NotEqual(TrainFundingHandoff.AwaitErrand,
            TrainFundingHandoffRule.For(TrainFundingStart.Short, midRun));
        Assert.NotEqual(TrainFundingHandoff.AwaitErrand,
            TrainFundingHandoffRule.For(TrainFundingStart.Funded, midRun));
    }

    [Fact]
    public void ShortBeforeSettingOut_AbandonsTheRun()
    {
        Assert.Equal(TrainFundingHandoff.Abandon,
            TrainFundingHandoffRule.For(TrainFundingStart.Short, midRun: false));
    }

    [Fact]
    public void ShortAfterTheTrainerRefused_StopsTheLoopInsteadOfAbandoning()
    {
        // Abandoning here drops the stat refresh, CP apply and report for the levels
        // trained before the refusal.
        Assert.Equal(TrainFundingHandoff.StopShort,
            TrainFundingHandoffRule.For(TrainFundingStart.Short, midRun: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Funded_CarriesOn(bool midRun)
    {
        Assert.Equal(TrainFundingHandoff.Proceed,
            TrainFundingHandoffRule.For(TrainFundingStart.Funded, midRun));
    }
}

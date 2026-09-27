using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Train;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Settings → Auto-Trainer → "When short on cash" decides which stashes and banks a
// short run may draw on. The planner only ever sees what this filter keeps.
public sealed class TrainFundingSourceFilterTests
{
    private static readonly TrainFundingSource StashA =
        new(TrainFundingSourceKind.Stash, new RoomKey(1, 100), "stash 1/100", 5_000);
    private static readonly TrainFundingSource StashB =
        new(TrainFundingSourceKind.Stash, new RoomKey(1, 200), "stash 1/200", 5_000);
    private static readonly TrainFundingSource Godfrey =
        new(TrainFundingSourceKind.Bank, new RoomKey(1, 297), "Bank of Godfrey", 50_000);
    private static readonly TrainFundingSource Rhudaur =
        new(TrainFundingSourceKind.Bank, new RoomKey(2, 50), "Bank of Rhudaur", 50_000);

    private static readonly TrainFundingSource[] All = { StashA, StashB, Godfrey, Rhudaur };

    private static List<TrainFundingSource> Kept(AutoTrainerSettings s) =>
        TrainFundingSourceFilter.Apply(All, s);

    [Fact]
    public void Default_KeepsEveryStashAndBank()
    {
        Assert.Equal(All, Kept(new AutoTrainerSettings()));
    }

    [Fact]
    public void BankOnly_DropsStashes()
    {
        Assert.Equal(new[] { Godfrey, Rhudaur },
            Kept(new AutoTrainerSettings { FundingMode = TrainFundingMode.BankOnly }));
    }

    [Fact]
    public void StashOnly_DropsBanks()
    {
        Assert.Equal(new[] { StashA, StashB },
            Kept(new AutoTrainerSettings { FundingMode = TrainFundingMode.StashOnly }));
    }

    [Fact]
    public void None_KeepsNothing_SoTheRunStaysArmedAndLoops()
    {
        Assert.Empty(Kept(new AutoTrainerSettings { FundingMode = TrainFundingMode.None }));
        Assert.False(TrainFundingSourceFilter.UsesBanks(TrainFundingMode.None));
    }

    [Fact]
    public void ASpecificBank_IsTheOnlyBank_MatchedIgnoringCase()
    {
        List<TrainFundingSource> kept = Kept(new AutoTrainerSettings { FundingBank = "bank of rhudaur" });

        Assert.Contains(Rhudaur, kept);
        Assert.DoesNotContain(Godfrey, kept);
        Assert.Contains(StashA, kept);                  // stashes still allowed in this mode
    }

    [Fact]
    public void ASpecificStash_IsTheOnlyStash()
    {
        List<TrainFundingSource> kept = Kept(new AutoTrainerSettings
        {
            FundingMode = TrainFundingMode.StashOnly,
            FundingStash = new RoomRef(1, 200),
        });

        Assert.Equal(new[] { StashB }, kept);
    }

    [Fact]
    public void StashOnly_NeedsNoBankListing()
    {
        Assert.False(TrainFundingSourceFilter.UsesBanks(TrainFundingMode.StashOnly));
        Assert.True(TrainFundingSourceFilter.UsesBanks(TrainFundingMode.StashAndBank));
    }
}

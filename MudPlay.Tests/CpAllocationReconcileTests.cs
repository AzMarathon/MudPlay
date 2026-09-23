using MudPlay.Models.Profile;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The CP Allocation plan drops a row once the character has trained past it — reached the
// level AND allocated its CP (the raw-base baseline meets the row's targets). This pins that
// prune decision, which deletes persisted plan rows, so it can't drift into removing a level
// that's reached-but-unallocated or a future level. Numbers mirror the reported case: a
// level-16 Dwarf Priest with STR 54 / INT 30 / WIL 118 / AGL 30 / HEA 60 / CHM 30.
public sealed class CpAllocationReconcileTests
{
    private static readonly CpPlanEntry Baseline16 = new(16, 54, 30, 118, 30, 60, 30);

    private static CpPlanEntry Row(int level, int str = 54, int @int = 30, int wil = 118,
                                   int agl = 30, int hea = 60, int chm = 30)
        => new(level, str, @int, wil, agl, hea, chm);

    [Fact]
    public void ReachedLevel_TargetsMet_IsTrained()
    {
        // The stuck row: level 16 reached, its target stats equal the trained baseline.
        Assert.True(CpAllocationSectionViewModel.IsRowTrained(Row(16), Baseline16, 16));
        // A level already passed is likewise done.
        Assert.True(CpAllocationSectionViewModel.IsRowTrained(Row(15), Baseline16, 16));
    }

    [Fact]
    public void ReachedLevel_TargetNotYetAllocated_IsNotTrained()
    {
        // Level 16 reached but a planned raise (STR 55, WIL 120) not yet allocated → keep it,
        // even one unmet stat is enough to keep the row.
        Assert.False(CpAllocationSectionViewModel.IsRowTrained(Row(16, str: 55), Baseline16, 16));
        Assert.False(CpAllocationSectionViewModel.IsRowTrained(Row(16, wil: 120), Baseline16, 16));
    }

    [Fact]
    public void FutureLevel_EvenIfStatsMet_IsNotTrained()
    {
        // Level 17's target stats already happen to be met, but the character is only 16 —
        // the level guard keeps the future row.
        Assert.False(CpAllocationSectionViewModel.IsRowTrained(Row(17), Baseline16, 16));
    }
}

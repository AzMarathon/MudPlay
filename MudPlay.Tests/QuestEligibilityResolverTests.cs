using System;
using MudPlay.Game.Calculators;
using MudPlay.Game.Quests;
using Xunit;

namespace MudPlay.Tests;

// The shared "can this character complete this quest?" gate used by both the Quest
// Status journal and the availability announcer. Covers the crawl's class/race
// guards, the explicit per-quest class-restriction override (Magebane/Tarl), and
// the alignment-quest checkboxes.
public sealed class QuestEligibilityResolverTests
{
    private static CrawledQuest Quest(
        int[]? classIds = null, int[]? raceIds = null, AlignmentBucket? align = null) =>
        new(Flag: 1, Step: 0, RequiredLevel: 1,
            Bonuses: Array.Empty<QuestBonus>(), AwardItems: Array.Empty<int>(),
            ClassIds: classIds, RaceIds: raceIds, RequiredAlignment: align);

    // ----- crawl-derived class / race guards -----

    [Fact]
    public void CrawlClassGuard_ExcludesWrongClass_KeepsMatchingClass()
    {
        CrawledQuest q = Quest(classIds: new[] { 2 });   // Witchunter-only
        Assert.True(QuestEligibilityResolver.IsIneligible(q, classId: 3, null, null, false, false, false));
        Assert.False(QuestEligibilityResolver.IsIneligible(q, classId: 2, null, null, false, false, false));
        // Classless viewer is never gated by class.
        Assert.False(QuestEligibilityResolver.IsIneligible(q, classId: null, null, null, false, false, false));
    }

    // ----- explicit per-quest class restriction override -----

    [Fact]
    public void ClassRestrictOverride_GatesLikeACrawlGuard()
    {
        CrawledQuest q = Quest();   // crawl found no restriction (the Magebane leak)
        var restrict = new System.Collections.Generic.List<int> { 2 };
        Assert.True(QuestEligibilityResolver.IsIneligible(q, classId: 3, null, restrict, false, false, false));
        Assert.False(QuestEligibilityResolver.IsIneligible(q, classId: 2, null, restrict, false, false, false));
        Assert.False(QuestEligibilityResolver.IsIneligible(q, classId: null, null, restrict, false, false, false));
    }

    // ----- alignment-quest checkboxes -----

    [Fact]
    public void AlignmentQuest_HiddenUntilItsBucketIsChecked()
    {
        CrawledQuest evil = Quest(align: AlignmentBucket.Evil);
        // All boxes off (the default) → the evil quest is ineligible.
        Assert.True(QuestEligibilityResolver.IsIneligible(evil, classId: 3, null, null, false, false, false));
        // Evil box on → eligible; the other boxes don't matter.
        Assert.False(QuestEligibilityResolver.IsIneligible(evil, classId: 3, null, null, allowGood: false, allowNeutral: false, allowEvil: true));
        // A different box on → still hidden.
        Assert.True(QuestEligibilityResolver.IsIneligible(evil, classId: 3, null, null, allowGood: true, allowNeutral: true, allowEvil: false));
    }

    [Fact]
    public void NonAlignmentQuest_UnaffectedByCheckboxes()
    {
        CrawledQuest plain = Quest();   // RequiredAlignment null
        Assert.False(QuestEligibilityResolver.IsIneligible(plain, classId: 3, null, null, false, false, false));
    }
}

using System.Collections.Generic;
using MudPlay.Models.Profile;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The Quest editor row's two eligibility-facing controls: the "Show in quest journal"
// checkbox (ShowInJournal) whose backing field flips with eligibility, and the
// "Restrict to classes" checklist (ClassOptions) that persists as ClassRestrict.
public sealed class QuestEditRowViewModelTests
{
    // Builds a crawled-quest row (flag < ManualFlagBase) so ToDefinition runs the
    // delta-diff path; name/steps/rewards match their baselines so only the eligibility
    // fields under test carry through.
    private static QuestEditRowViewModel Row(bool ineligible, bool showIfIneligible = false,
                                             bool visible = true, IReadOnlyList<ClassRestrictOption>? options = null) =>
        new(50, 1, "Fallback", autoSteps: "", autoRewards: "", bonusText: "",
            levelText: "", autoRequiredLevel: 0, requirementsText: "",
            name: "Fallback", visible: visible, steps: "", rewards: "",
            requiredLevel: null, ineligible: ineligible, showIfIneligible: showIfIneligible,
            classOptions: options);

    [Fact]
    public void ShowInJournal_EligibleQuest_MapsToVisibleHide()
    {
        QuestEditRowViewModel row = Row(ineligible: false, visible: true);
        Assert.True(row.ShowInJournal);          // eligible + visible → shown

        row.ShowInJournal = false;               // per-taste hide
        Assert.False(row.Visible);
        QuestDefinition def = row.ToDefinition();
        Assert.False(def.Visible);
        Assert.False(def.ShowIfIneligible);      // untouched for an eligible quest
    }

    [Fact]
    public void ShowInJournal_IneligibleQuest_StartsUnchecked_ThenMapsToShowAnyway()
    {
        // A "Cannot complete" quest opens unchecked even though its stored Visible is true.
        QuestEditRowViewModel row = Row(ineligible: true, showIfIneligible: false, visible: true);
        Assert.False(row.ShowInJournal);

        row.ShowInJournal = true;                // opt back in
        Assert.True(row.ShowInJournal);
        QuestDefinition def = row.ToDefinition();
        Assert.True(def.ShowIfIneligible);
        Assert.True(def.Visible);                // Visible left alone — the two never clobber
    }

    [Fact]
    public void ClassOptions_TickedClasses_PersistAsClassRestrict()
    {
        var options = new List<ClassRestrictOption>
        {
            new(3, "Witchhunter", false),
            new(8, "Priest", false),
        };
        QuestEditRowViewModel row = Row(ineligible: false, options: options);

        Assert.Equal("Any class", row.ClassRestrictSummary);
        Assert.Null(row.SelectedClassNumbers());
        Assert.Null(row.ToDefinition().ClassRestrict);

        options[0].IsSelected = true;

        Assert.Equal("Witchhunter", row.ClassRestrictSummary);
        Assert.Equal(new List<int> { 3 }, row.SelectedClassNumbers());
        Assert.Equal(new List<int> { 3 }, row.ToDefinition().ClassRestrict);
    }

    [Fact]
    public void ClassOptions_PreSelected_SummarizeAndPersist()
    {
        var options = new List<ClassRestrictOption>
        {
            new(3, "Witchhunter", true),
            new(8, "Priest", true),
        };
        QuestEditRowViewModel row = Row(ineligible: false, options: options);

        Assert.Equal("Witchhunter, Priest", row.ClassRestrictSummary);
        Assert.Equal(new List<int> { 3, 8 }, row.SelectedClassNumbers());
    }
}

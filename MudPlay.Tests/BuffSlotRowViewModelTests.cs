using System.Collections.Generic;
using MudPlay.Models.Profile;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// Buff Watchdog config row targeting: the All/None master must be INDEPENDENT of
// the Self box (the reported bug — unchecking All cleared Self), gated to a party
// (solo shows only Self), and it drives the auto-adapt AllMembers flag that decides
// whether a joining member is blessed.
public sealed class BuffSlotRowViewModelTests
{
    private static BuffSlotRowViewModel Row(BuffSlot dto, BuffSlotScope scope = BuffSlotScope.SingleTarget)
        => new(dto, _ => scope, s => s ?? string.Empty, _ => (null, null), () => { });

    private static IReadOnlyList<(string Display, string Given)> Party(params string[] given)
    {
        var list = new List<(string, string)>();
        foreach (string g in given) list.Add((g, g.ToLowerInvariant()));
        return list;
    }

    private static BuffMemberToggle Toggle(BuffSlotRowViewModel row, string given)
    {
        foreach (BuffMemberToggle t in row.MemberTargets)
            if (t.Given == given) return t;
        throw new Xunit.Sdk.XunitException($"no member toggle for '{given}'");
    }

    [Fact]
    public void UncheckingAll_LeavesSelfChecked()
    {
        var dto = new BuffSlot { Spell = "bless", CastOnSelf = true, AllMembers = true };
        var row = Row(dto);
        row.RebuildMemberTargets(Party("aragorn", "gimli"));
        Assert.True(row.AllTargets);
        Assert.True(row.CastOnSelf);

        row.AllTargets = false;   // the bug: this used to clear Self too

        Assert.False(row.AllTargets);
        Assert.True(row.CastOnSelf);     // Self untouched
        Assert.True(dto.CastOnSelf);
        Assert.False(dto.AllMembers);
        Assert.Empty(dto.Targets);
    }

    [Fact]
    public void CheckingAll_SetsAutoAdapt_SoAJoinerIsAssigned()
    {
        var dto = new BuffSlot { Spell = "bless" };
        var row = Row(dto);
        row.RebuildMemberTargets(Party("aragorn"));
        row.AllTargets = true;
        Assert.True(dto.AllMembers);     // casting layer blesses every current member
        Assert.Empty(dto.Targets);

        row.RebuildMemberTargets(Party("aragorn", "legolas"));   // legolas joins
        Assert.True(Toggle(row, "aragorn").IsChecked);
        Assert.True(Toggle(row, "legolas").IsChecked);           // auto-assigned
    }

    [Fact]
    public void AllOff_JoinerIsNotAssigned()
    {
        var dto = new BuffSlot { Spell = "bless" };
        dto.Targets.Add("aragorn");
        var row = Row(dto);
        row.RebuildMemberTargets(Party("aragorn", "legolas"));   // legolas joins, All off
        Assert.True(Toggle(row, "aragorn").IsChecked);           // explicit target
        Assert.False(Toggle(row, "legolas").IsChecked);          // NOT auto-assigned
        Assert.False(row.AllTargets);
    }

    [Fact]
    public void ShowMemberTargets_HiddenSolo_ShownInParty()
    {
        var row = Row(new BuffSlot { Spell = "bless" });
        row.RebuildMemberTargets(Party());               // solo
        Assert.False(row.HasPartyMembers);
        Assert.False(row.ShowMemberTargets);
        Assert.True(row.ShowSelf);                       // Self still shows solo

        row.RebuildMemberTargets(Party("aragorn"));      // party
        Assert.True(row.HasPartyMembers);
        Assert.True(row.ShowMemberTargets);
    }

    [Fact]
    public void ShowMemberTargets_NeverForSelfOnlyBuff()
    {
        var row = Row(new BuffSlot { Spell = "shield" }, BuffSlotScope.SelfOnly);
        row.RebuildMemberTargets(Party("aragorn"));
        Assert.False(row.ShowMemberTargets);
        Assert.True(row.ShowSelf);
    }

    [Fact]
    public void OverwriteWarning_NoConflict_HiddenAndNullTooltip()
    {
        var row = Row(new BuffSlot { Spell = "bless" });
        Assert.False(row.HasOverwriteWarning);
        Assert.Null(row.OverwriteWarningTooltip);
    }

    [Fact]
    public void OverwriteWarning_BothDirections_CombinedIntoOneTooltip()
    {
        var dto = new BuffSlot { Spell = "bless" };
        var row = new BuffSlotRowViewModel(
            dto, _ => BuffSlotScope.SingleTarget, s => s ?? string.Empty,
            _ => ("chant", "poison"), () => { });

        Assert.True(row.HasOverwriteWarning);
        Assert.Equal("Removed by: chant\nRemoves: poison", row.OverwriteWarningTooltip);
    }

    // The live cross-row mutual-exclusion hook (BuffPanelViewModel.OnSelfCastActivated)
    // only reacts to a fresh Self CHECK, never an uncheck — otherwise unchecking a row
    // to let its conflict win would immediately re-fire and fight the user's own click.
    [Fact]
    public void CastOnSelf_CheckedOn_FiresOnSelfActivated()
    {
        int fired = 0;
        var dto = new BuffSlot { Spell = "grze" };
        var row = new BuffSlotRowViewModel(
            dto, _ => BuffSlotScope.SelfOnly, s => s ?? string.Empty,
            _ => (null, null), () => { }, _ => fired++);

        row.CastOnSelf = true;
        Assert.Equal(1, fired);
    }

    [Fact]
    public void CastOnSelf_CheckedOff_DoesNotFireOnSelfActivated()
    {
        int fired = 0;
        var dto = new BuffSlot { Spell = "grze", CastOnSelf = true };
        var row = new BuffSlotRowViewModel(
            dto, _ => BuffSlotScope.SelfOnly, s => s ?? string.Empty,
            _ => (null, null), () => { }, _ => fired++);

        row.CastOnSelf = false;
        Assert.Equal(0, fired);
    }

    [Fact]
    public void CastOnSelf_NullActivationCallback_DoesNotThrow()
    {
        // Every existing Row(...) helper call in this file omits the callback —
        // it must default to a no-op, not a required parameter.
        var row = Row(new BuffSlot { Spell = "bles" }, BuffSlotScope.SelfOnly);
        row.CastOnSelf = true;   // would NRE if the default wasn't null-safe
        Assert.True(row.CastOnSelf);
    }

    [Fact]
    public void IsLearned_NoResolver_DefaultsTrue()
    {
        // Every Row(...) helper call in this file omits the resolver — a row must
        // not spuriously show "unlearned" when nothing wired the concept up.
        var row = Row(new BuffSlot { Spell = "bles" });
        Assert.True(row.IsLearned);
    }

    [Fact]
    public void IsLearned_ReflectsResolver()
    {
        var dto = new BuffSlot { Spell = "grze" };
        var row = new BuffSlotRowViewModel(
            dto, _ => BuffSlotScope.SelfOnly, s => s ?? string.Empty,
            _ => (null, null), () => { }, onSelfActivated: null,
            resolveLearned: code => code != "grze");

        Assert.False(row.IsLearned);
    }

    [Fact]
    public void HeaderText_NoReqLevelResolver_OmitsLevelTag()
    {
        // Every Row(...) helper call in this file omits the resolver — must not
        // render a bogus "(Lvl )" or throw.
        var row = Row(new BuffSlot { Spell = "bless", RecastMarginSec = 15 });
        Assert.Equal("bless - 15s", row.HeaderText);
    }

    [Fact]
    public void HeaderText_ReqLevelResolved_InsertsLevelTagBeforeRecast()
    {
        var dto = new BuffSlot { Spell = "dfla", RecastMarginSec = 15 };
        var row = new BuffSlotRowViewModel(
            dto, _ => BuffSlotScope.SelfOnly, _ => "dark flagellation",
            _ => (null, null), () => { }, onSelfActivated: null,
            resolveLearned: null, resolveReqLevel: _ => 50);

        Assert.Equal("dark flagellation (Lvl 50) - 15s", row.HeaderText);
    }
}

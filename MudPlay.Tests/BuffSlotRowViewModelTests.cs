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
        => new(dto, _ => scope, s => s ?? string.Empty, () => { });

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
}

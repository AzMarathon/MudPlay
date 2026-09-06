using System.Collections.Generic;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// BuffAffectSet.CanCoLand answers "could these two buff slots ever land on the same
// character" — the precondition for a RemovesSpell relation between their spells to
// matter. Pure given the raw targeting flags, no live party/spellbook wiring needed.
public sealed class BuffConflictAnalyzerTests
{
    private static BuffAffectSet SelfOnly() =>
        BuffAffectSet.From(isWholePartySpell: false, wholePartyOn: false, castOnSelf: true, allMembers: false, targets: System.Array.Empty<string>());

    private static BuffAffectSet WholeParty(bool on = true) =>
        BuffAffectSet.From(isWholePartySpell: true, wholePartyOn: on, castOnSelf: false, allMembers: false, targets: System.Array.Empty<string>());

    private static BuffAffectSet Members(params string[] names) =>
        BuffAffectSet.From(isWholePartySpell: false, wholePartyOn: false, castOnSelf: false, allMembers: false, targets: names);

    private static BuffAffectSet AllMembers() =>
        BuffAffectSet.From(isWholePartySpell: false, wholePartyOn: false, castOnSelf: false, allMembers: true, targets: System.Array.Empty<string>());

    private static BuffAffectSet Inert() =>
        BuffAffectSet.From(isWholePartySpell: false, wholePartyOn: false, castOnSelf: false, allMembers: false, targets: System.Array.Empty<string>());

    [Fact]
    public void TwoSelfCastSlots_CoLand()
    {
        Assert.True(BuffConflictAnalyzer.CanCoLand(SelfOnly(), SelfOnly()));
    }

    [Fact]
    public void SelfCastAndWholeParty_CoLand()
    {
        // Whole-party always lands on self too — this is the existing SelfBuffCoverage case.
        Assert.True(BuffConflictAnalyzer.CanCoLand(SelfOnly(), WholeParty()));
    }

    [Fact]
    public void ToggledOffWholeParty_NeverCoLands()
    {
        Assert.False(BuffConflictAnalyzer.CanCoLand(SelfOnly(), WholeParty(on: false)));
    }

    [Fact]
    public void TwoWholePartySlots_CoLand()
    {
        Assert.True(BuffConflictAnalyzer.CanCoLand(WholeParty(), WholeParty()));
    }

    [Fact]
    public void OverlappingNamedMembers_CoLand()
    {
        Assert.True(BuffConflictAnalyzer.CanCoLand(Members("aragorn", "gimli"), Members("gimli", "legolas")));
    }

    [Fact]
    public void DisjointNamedMembers_DoNotCoLand()
    {
        Assert.False(BuffConflictAnalyzer.CanCoLand(Members("aragorn"), Members("legolas")));
    }

    [Fact]
    public void AllMembersVersusNamedMember_CoLand()
    {
        Assert.True(BuffConflictAnalyzer.CanCoLand(AllMembers(), Members("legolas")));
    }

    [Fact]
    public void AllMembersVersusSelfOnly_DoNotCoLand()
    {
        // AllMembers never implies self unless CastOnSelf is also set.
        Assert.False(BuffConflictAnalyzer.CanCoLand(AllMembers(), SelfOnly()));
    }

    [Fact]
    public void InertSlot_NeverCoLandsWithAnything()
    {
        Assert.False(BuffConflictAnalyzer.CanCoLand(Inert(), WholeParty()));
        Assert.False(BuffConflictAnalyzer.CanCoLand(Inert(), SelfOnly()));
    }

    [Fact]
    public void Resolve_FindsBothDirections()
    {
        List<BuffOverwritePair> pairs = new()
        {
            new BuffOverwritePair("chan", "Chant", "bles", "Bless"),
            new BuffOverwritePair("bles", "Bless", "pois", "Poison Ward"),
        };

        (string? removedBy, string? removes) = BuffConflictAnalyzer.Resolve(pairs, "bles");
        Assert.Equal("Chant", removedBy);
        Assert.Equal("Poison Ward", removes);
    }

    [Fact]
    public void Resolve_NoMatch_ReturnsNulls()
    {
        (string? removedBy, string? removes) = BuffConflictAnalyzer.Resolve(new List<BuffOverwritePair>(), "bles");
        Assert.Null(removedBy);
        Assert.Null(removes);
    }

    [Fact]
    public void FormatTooltip_CombinesBothDirections()
    {
        Assert.Equal("Removed by: Chant\nRemoves: Poison Ward", BuffConflictAnalyzer.FormatTooltip("Chant", "Poison Ward"));
        Assert.Equal("Removed by: Chant", BuffConflictAnalyzer.FormatTooltip("Chant", null));
        Assert.Null(BuffConflictAnalyzer.FormatTooltip(null, null));
    }
}

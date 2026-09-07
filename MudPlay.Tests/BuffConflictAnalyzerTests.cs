using System;
using System.Collections.Generic;
using System.Linq;
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

    [Fact]
    public void RemovedSpellNumbers_ExtractsOnlyAbil122Values()
    {
        SpellFormulaInput formula = new()
        {
            Abilities = new[]
            {
                new SpellAbility(Code: 4, Value: 0),
                new SpellAbility(Code: 122, Value: 5581),
                new SpellAbility(Code: 122, Value: 347),
                new SpellAbility(Code: 58, Value: 0),
            },
        };
        Assert.Equal(new HashSet<int> { 5581, 347 }, BuffConflictAnalyzer.RemovedSpellNumbers(formula));
    }

    // Confirmed Paradigm data (Spells #304 "zeal" / #5581 "greater zeal"): each
    // carries an Abil-122 RemovesSpell pointing at the other, so casting either one
    // strips the other's timer — "Add all blesses" must only ever offer one.
    private static SelfBlessCandidate Zeal() => new("zeal", "zeal", 304, ReqLevel: 25, ManaCost: 20, Removes: new[] { 5581 });
    private static SelfBlessCandidate GreaterZeal() => new("grze", "greater zeal", 5581, ReqLevel: 44, ManaCost: 28, Removes: new[] { 304 });

    [Fact]
    public void SelectSelfBlessCandidates_MutualFamily_KeepsOnlyHighestReqLevel()
    {
        List<SelfBlessCandidate> result = BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { Zeal(), GreaterZeal() }, Array.Empty<ExistingBuffSlot>())
            .ToList();

        SelfBlessCandidate kept = Assert.Single(result);
        Assert.Equal("greater zeal", kept.Name);
    }

    [Fact]
    public void SelectSelfBlessCandidates_UnrelatedCandidates_KeepsBoth()
    {
        SelfBlessCandidate other = new("bles", "bless", 100, ReqLevel: 5, ManaCost: 10, Removes: Array.Empty<int>());
        List<SelfBlessCandidate> result = BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { Zeal(), other }, Array.Empty<ExistingBuffSlot>())
            .ToList();

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SelectSelfBlessCandidates_ExistingSlotAlreadyRemovesCandidate_CandidateDropped()
    {
        // An existing self-cast slot for greater zeal already covers this — adding
        // zeal on top would just get overwritten every recast.
        ExistingBuffSlot existingGreaterZeal = new(5581, SelfAffect(), Removes: new[] { 304 });
        List<SelfBlessCandidate> result = BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { Zeal() }, new[] { existingGreaterZeal })
            .ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void SelectSelfBlessCandidates_CandidateWouldRemoveExistingSlot_CandidateDropped()
    {
        // The reverse direction: an existing manually-configured zeal slot would be
        // clobbered by adding greater zeal — don't touch the user's existing config.
        ExistingBuffSlot existingZeal = new(304, SelfAffect(), Removes: new[] { 5581 });
        List<SelfBlessCandidate> result = BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { GreaterZeal() }, new[] { existingZeal })
            .ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void SelectSelfBlessCandidates_ExistingSlotCannotCoLandWithSelf_DoesNotFilter()
    {
        // An existing slot aimed only at other party members (never self) can't
        // conflict with a self-cast candidate, even if the spell numbers overlap.
        BuffAffectSet membersOnly = BuffAffectSet.From(
            isWholePartySpell: false, wholePartyOn: false, castOnSelf: false, allMembers: false,
            targets: new[] { "legolas" });
        ExistingBuffSlot existingMemberSlot = new(5581, membersOnly, Removes: new[] { 304 });
        List<SelfBlessCandidate> result = BuffConflictAnalyzer
            .SelectSelfBlessCandidates(new[] { Zeal() }, new[] { existingMemberSlot })
            .ToList();

        SelfBlessCandidate kept = Assert.Single(result);
        Assert.Equal("zeal", kept.Name);
    }

    [Fact]
    public void SelectSelfBlessCandidates_NoCandidates_ReturnsEmpty()
    {
        Assert.Empty(BuffConflictAnalyzer.SelectSelfBlessCandidates(
            Array.Empty<SelfBlessCandidate>(), Array.Empty<ExistingBuffSlot>()));
    }

    private static BuffAffectSet SelfAffect() => BuffAffectSet.From(
        isWholePartySpell: false, wholePartyOn: false, castOnSelf: true, allMembers: false,
        targets: Array.Empty<string>());
}

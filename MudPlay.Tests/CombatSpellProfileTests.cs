using System.Collections.Generic;
using MudPlay.Game.Combat;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Casting spell profiles: the pure @profile best-match resolver, the swap report,
// and the capture/overlay round-trip. UI wiring (chips, Action menu, remote
// handler) is smoke-tested via dotnet run per the no-VM-tests rule.
public sealed class CombatSpellProfileTests
{
    private static List<CombatSpellProfile> Named(params string[] names)
    {
        var list = new List<CombatSpellProfile>();
        foreach (string n in names) list.Add(new CombatSpellProfile { Name = n });
        return list;
    }

    [Fact]
    public void Matcher_BareNumber_IsPositional()
    {
        var p = Named("a", "b", "c");
        Assert.Equal(0, CombatSpellProfileMatcher.Resolve(p, "1"));
        Assert.Equal(2, CombatSpellProfileMatcher.Resolve(p, "3"));
        Assert.Null(CombatSpellProfileMatcher.Resolve(p, "0"));   // out of range, no name "0"
        Assert.Null(CombatSpellProfileMatcher.Resolve(p, "9"));
    }

    [Fact]
    public void Matcher_PicksClosestName()
    {
        var p = Named("fire spells", "lightning spells");
        Assert.Equal(0, CombatSpellProfileMatcher.Resolve(p, "fire"));    // the user's example
        Assert.Equal(1, CombatSpellProfileMatcher.Resolve(p, "light"));
    }

    [Fact]
    public void Matcher_ExactAndTierOrder()
    {
        var p = Named("Fire", "Firestorm");
        Assert.Equal(0, CombatSpellProfileMatcher.Resolve(p, "fire"));    // exact (ci) beats prefix
        Assert.Equal(1, CombatSpellProfileMatcher.Resolve(p, "storm"));   // substring of the 2nd only
    }

    [Fact]
    public void Matcher_NoMatchIsNull()
    {
        var p = Named("fire", "ice");
        Assert.Null(CombatSpellProfileMatcher.Resolve(p, "poison"));
        Assert.Null(CombatSpellProfileMatcher.Resolve(p, ""));
        Assert.Null(CombatSpellProfileMatcher.Resolve(p, "   "));
        Assert.Null(CombatSpellProfileMatcher.Resolve(new List<CombatSpellProfile>(), "fire"));
    }

    [Fact]
    public void Report_ListsCastCodes_OmitsEmptySlots()
    {
        var p = new CombatSpellProfile { Name = "Fire" };
        p.NormalAttackSpell.SpellName = "fbl";
        p.AlternateAttackSpell.SpellName = "fs";
        string r = CombatSpellProfileReport.Describe(p, 2);
        Assert.Contains("Combat profile 2 (Fire)", r);
        Assert.Contains("normal: fbl", r);
        Assert.Contains("alt: fs", r);
        Assert.DoesNotContain("multi:", r);
        Assert.DoesNotContain("drain:", r);
    }

    [Fact]
    public void Report_NoSpells()
    {
        string r = CombatSpellProfileReport.Describe(new CombatSpellProfile(), 1);
        Assert.Contains("Combat profile 1", r);
        Assert.Contains("no spells set", r);
    }

    [Fact]
    public void CaptureThenApply_RoundTrips_AndDeepCopies()
    {
        var src = new CombatSettings();
        src.NormalAttackSpell.SpellName = "mm";
        src.NormalAttackSpell.MinManaPerCast = 40;
        src.DrainSpell.SpellName = "vamp";
        src.DrainHpTrigger = 33;
        src.DrainsOverrideAoe = true;
        src.SpellManaThresholdMode = ThresholdMode.Absolute;

        CombatSpellProfile prof = CombatSpellProfile.Capture("X", src);
        Assert.Equal("mm", prof.NormalAttackSpell.SpellName);
        Assert.Equal(40, prof.NormalAttackSpell.MinManaPerCast);
        Assert.Equal("vamp", prof.DrainSpell.SpellName);
        Assert.Equal(33, prof.DrainHpTrigger);
        Assert.True(prof.DrainsOverrideAoe);
        Assert.Equal(ThresholdMode.Absolute, prof.SpellManaThresholdMode);

        // Deep copy: mutating the source after capture doesn't leak into the profile.
        src.NormalAttackSpell.SpellName = "changed";
        Assert.Equal("mm", prof.NormalAttackSpell.SpellName);

        // Overlay onto a fresh CombatSettings — spell fields land, non-spell fields
        // (e.g. the attack verb) are untouched.
        var dst = new CombatSettings { NormalAttackCommand = "z" };
        prof.ApplyTo(dst);
        Assert.Equal("mm", dst.NormalAttackSpell.SpellName);
        Assert.Equal("vamp", dst.DrainSpell.SpellName);
        Assert.Equal(33, dst.DrainHpTrigger);
        Assert.Equal(ThresholdMode.Absolute, dst.SpellManaThresholdMode);
        Assert.Equal("z", dst.NormalAttackCommand);   // non-spell field untouched

        // Overlay deep-copies too: editing the destination slot doesn't touch the profile.
        dst.NormalAttackSpell.SpellName = "q";
        Assert.Equal("mm", prof.NormalAttackSpell.SpellName);
    }
}

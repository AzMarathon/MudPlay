using System.Collections.Generic;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// Combat profiles: the pure @profile best-match resolver, the swap report, the
// capture/overlay round-trip (now a full posture — spells + verbs + room
// thresholds + weapons + the whole Health tab), and the Default-set weapon
// write-back. UI wiring (chips, Action menu, remote handler, cross-tab staging) is
// smoke-tested via dotnet run per the no-VM-tests rule.
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
    public void Roster_ListsCurrentThenStandby()
    {
        var p = Named("Fire", "Cold", "Lightning");
        Assert.Equal("{Current: 1)Fire, On Standby: 2)Cold, 3)Lightning}",
            CombatSpellProfileReport.DescribeRoster(p, 0));
        Assert.Equal("{Current: 2)Cold, On Standby: 1)Fire, 3)Lightning}",
            CombatSpellProfileReport.DescribeRoster(p, 1));
    }

    [Fact]
    public void Roster_LoneProfile_OmitsStandby()
    {
        Assert.Equal("{Current: 1)Fire}",
            CombatSpellProfileReport.DescribeRoster(Named("Fire"), 0));
    }

    [Fact]
    public void Roster_UnnamedShowsPlaceholder()
    {
        Assert.Equal("{Current: 1)unnamed, On Standby: 2)Cold}",
            CombatSpellProfileReport.DescribeRoster(Named("", "Cold"), 0));
    }

    [Fact]
    public void DescribeConfig_ShowsAllSlots_Gates_AndKnobs()
    {
        var p = new CombatSpellProfile { Name = "Fire", DrainHpTrigger = 40, DrainsOverrideAoe = true };
        p.MultiAttackSpell.SpellName = "fbl";
        p.MultiAttackSpell.MinEnemies = 2;
        p.MultiAttackSpell.MinManaPerCast = 10;
        p.NormalAttackSpell.SpellName = "fbl";
        p.AlternateAttackSpell.SpellName = "fs";
        p.AlternateAttackSpell.MaxCastsPerRoom = 2;
        // AoE-debuff / single-target debuff / drain left empty

        string r = CombatSpellProfileReport.DescribeConfig(p, 2);

        Assert.Contains("Combat profile 2 (Fire)", r);
        Assert.Contains("multi fbl(≥2, m10)", r);        // room-wide min-enemies + mana gate
        Assert.Contains("AoE-debuff —", r);              // empty slots shown, never omitted
        Assert.Contains("drain —", r);
        Assert.Contains("normal fbl", r);
        Assert.Contains("alt fs(×2)", r);                // max-casts gate
        Assert.Contains("mana-mode=Percentage", r);
        Assert.Contains("drain-HP-trigger=40", r);
        Assert.Contains("drains-override-AoE=on", r);
    }

    [Fact]
    public void DescribeConfig_MinEnemies_OnlyReportedOnRoomWideRows()
    {
        var p = new CombatSpellProfile();
        p.SingleTargetDebuffSpell.SpellName = "curse";
        p.SingleTargetDebuffSpell.MinEnemies = 3;   // engine ignores it on single-target rows
        string r = CombatSpellProfileReport.DescribeConfig(p, 1);
        Assert.Contains("debuff curse", r);
        Assert.DoesNotContain("≥3", r);
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

        CombatSpellProfile prof = CombatSpellProfile.Capture("X", src, new HealthSettings());
        Assert.Equal("mm", prof.NormalAttackSpell.SpellName);
        Assert.Equal(40, prof.NormalAttackSpell.MinManaPerCast);
        Assert.Equal("vamp", prof.DrainSpell.SpellName);
        Assert.Equal(33, prof.DrainHpTrigger);
        Assert.True(prof.DrainsOverrideAoe);
        Assert.Equal(ThresholdMode.Absolute, prof.SpellManaThresholdMode);

        // Deep copy: mutating the source after capture doesn't leak into the profile.
        src.NormalAttackSpell.SpellName = "changed";
        Assert.Equal("mm", prof.NormalAttackSpell.SpellName);

        // Overlay onto a fresh CombatSettings — the per-profile fields land; the
        // SHARED fields ApplyTo doesn't own (e.g. backstab) are untouched.
        var dst = new CombatSettings { DoBackstab = true };
        prof.ApplyTo(dst);
        Assert.Equal("mm", dst.NormalAttackSpell.SpellName);
        Assert.Equal("vamp", dst.DrainSpell.SpellName);
        Assert.Equal(33, dst.DrainHpTrigger);
        Assert.Equal(ThresholdMode.Absolute, dst.SpellManaThresholdMode);
        Assert.True(dst.DoBackstab);   // shared field ApplyTo never writes

        // Overlay deep-copies too: editing the destination slot doesn't touch the profile.
        dst.NormalAttackSpell.SpellName = "q";
        Assert.Equal("mm", prof.NormalAttackSpell.SpellName);
    }

    [Fact]
    public void CaptureThenApply_RoundTripsExpandedFields()
    {
        var src = new CombatSettings
        {
            NormalAttackCommand = "kick",
            AlternateAttackCommand = "swing",
            MinMonstersInRoom = 2,
            MaxMonstersInRoom = 9,
            RunDistance = 5,
        };
        var health = new HealthSettings { RestMaxHp = 88, RunIfBelowHp = 15, BlessIfAboveMa = 65 };

        CombatSpellProfile prof = CombatSpellProfile.Capture("Boss", src, health);
        Assert.Equal("kick", prof.NormalAttackCommand);
        Assert.Equal("swing", prof.AlternateAttackCommand);
        Assert.Equal(2, prof.MinMonstersInRoom);
        Assert.Equal(9, prof.MaxMonstersInRoom);
        Assert.Equal(5, prof.RunDistance);
        // Health captured as an independent clone.
        Assert.Equal(88, prof.Health.RestMaxHp);
        health.RestMaxHp = 1;
        Assert.Equal(88, prof.Health.RestMaxHp);

        // ApplyTo writes the per-profile combat fields (commands + room), not health.
        var dst = new CombatSettings();
        prof.ApplyTo(dst);
        Assert.Equal("kick", dst.NormalAttackCommand);
        Assert.Equal("swing", dst.AlternateAttackCommand);
        Assert.Equal(2, dst.MinMonstersInRoom);
        Assert.Equal(9, dst.MaxMonstersInRoom);
        Assert.Equal(5, dst.RunDistance);
    }

    [Fact]
    public void Clone_DeepCopies_Weapons_And_Health()
    {
        var prof = new CombatSpellProfile
        {
            Name = "Melee",
            NormalWeapon = "long sword", NormalOffHand = "buckler",
            AlternateWeapon = "great axe", AlternateOffHand = null,
        };
        prof.Health.RestMaxHp = 77;

        CombatSpellProfile copy = prof.Clone(newIdentity: true);
        Assert.NotEqual(prof.Id, copy.Id);              // new identity
        Assert.Equal("long sword", copy.NormalWeapon);
        Assert.Equal("buckler", copy.NormalOffHand);
        Assert.Equal("great axe", copy.AlternateWeapon);
        Assert.Equal(77, copy.Health.RestMaxHp);

        // Independent Health + weapon fields.
        copy.Health.RestMaxHp = 1;
        copy.NormalWeapon = "dagger";
        Assert.Equal(77, prof.Health.RestMaxHp);
        Assert.Equal("long sword", prof.NormalWeapon);
    }

    [Fact]
    public void CaptureCombatFrom_LeavesHealthAndWeaponsIntact()
    {
        var prof = new CombatSpellProfile { NormalWeapon = "mace" };
        prof.Health.RestMaxHp = 90;

        var combat = new CombatSettings { NormalAttackCommand = "bash", MaxMonstersInRoom = 7 };
        combat.NormalAttackSpell.SpellName = "mm";
        prof.CaptureCombatFrom(combat);

        Assert.Equal("bash", prof.NormalAttackCommand);
        Assert.Equal(7, prof.MaxMonstersInRoom);
        Assert.Equal("mm", prof.NormalAttackSpell.SpellName);
        Assert.Equal("mace", prof.NormalWeapon);        // weapons untouched
        Assert.Equal(90, prof.Health.RestMaxHp);        // health untouched
    }

    [Fact]
    public void HealthSettings_Clone_IsIndependent()
    {
        var h = new HealthSettings { RestMaxHp = 95, PreRestCommand = "peer" };
        HealthSettings c = h.Clone();
        Assert.Equal(95, c.RestMaxHp);
        Assert.Equal("peer", c.PreRestCommand);
        c.RestMaxHp = 10;
        Assert.Equal(95, h.RestMaxHp);
    }

    [Fact]
    public void WriteProfileWeapons_WritesIntoDefaultSet_CreatingItWhenMissing()
    {
        var equip = new EquipmentSettings();   // no sets at all
        var prof = new CombatSpellProfile
        {
            NormalWeapon = "long sword", NormalOffHand = "buckler",
            AlternateWeapon = "great axe", AlternateOffHand = null,
        };

        EquipmentWeaponSync.WriteProfileWeapons(equip, prof);

        // ApplyWeapons reads them straight back off the Default set — the live surface.
        var combat = new CombatSettings();
        EquipmentWeaponSync.ApplyWeapons(combat, equip);
        Assert.Equal("long sword", combat.NormalWeapon);
        Assert.Equal("buckler", combat.NormalOffHand);
        Assert.Equal("great axe", combat.AlternateWeapon);
        Assert.Null(combat.AlternateOffHand);           // null profile weapon = bare slot
    }

    [Fact]
    public void WriteProfileWeapons_ClearingAWeapon_DropsTheSlot()
    {
        var equip = new EquipmentSettings();
        EquipmentWeaponSync.WriteProfileWeapons(equip, new CombatSpellProfile { NormalWeapon = "sword" });
        // Now switch to a no-weapon profile — the slot must clear, not linger.
        EquipmentWeaponSync.WriteProfileWeapons(equip, new CombatSpellProfile());
        var combat = new CombatSettings();
        EquipmentWeaponSync.ApplyWeapons(combat, equip);
        Assert.Null(combat.NormalWeapon);
    }

    [Fact]
    public void CaptureDefaultWeapons_ReadsBackWhatWasWritten()
    {
        var equip = new EquipmentSettings();
        var written = new CombatSpellProfile
        {
            NormalWeapon = "spear", NormalOffHand = "shield",
            AlternateWeapon = "bow", AlternateOffHand = "quiver",
        };
        EquipmentWeaponSync.WriteProfileWeapons(equip, written);

        var readBack = new CombatSpellProfile();
        EquipmentWeaponSync.CaptureDefaultWeapons(equip, readBack);
        Assert.Equal("spear", readBack.NormalWeapon);
        Assert.Equal("shield", readBack.NormalOffHand);
        Assert.Equal("bow", readBack.AlternateWeapon);
        Assert.Equal("quiver", readBack.AlternateOffHand);
    }

    [Fact]
    public void DescribeConfig_IncludesVerbs_Room_Weapons_AndHealth()
    {
        var prof = new CombatSpellProfile
        {
            Name = "Melee",
            NormalAttackCommand = "kick", AlternateAttackCommand = "swing",
            MinMonstersInRoom = 1, MaxMonstersInRoom = 8, RunDistance = 4,
            NormalWeapon = "long sword", NormalOffHand = "buckler",
        };
        prof.Health.RestMaxHp = 90;

        string r = CombatSpellProfileReport.DescribeConfig(prof, 1);
        Assert.Contains("atk=kick/swing", r);
        Assert.Contains("monsters=1-8", r);
        Assert.Contains("run=4", r);
        Assert.Contains("wpn=long sword+buckler", r);
        Assert.Contains("health[", r);
    }
}

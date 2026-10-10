using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Input;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class ProfileMigrationsTests
{
    [Fact]
    public void V1Profile_ResetsKeybindsAndToolbarLayout_BumpsVersion()
    {
        CharacterProfile profile = new()
        {
            SchemaVersion = 1,
            BuiltInKeybindings = new Dictionary<BuiltInAction, KeyChord>
            {
                [BuiltInAction.OpenNavigation] = new(Key.F8),
            },
            Settings = new Dictionary<string, JsonElement>
            {
                ["Toolbar"] = JsonSerializer.SerializeToElement(new ToolbarSettings
                {
                    Layout = new()
                    {
                        new() { Kind = ToolbarItemKind.Button, ActionId = "OpenParty" },
                    },
                    Visible = false,
                    Position = ToolbarPosition.Right,
                }),
            },
        };

        bool changed = ProfileMigrations.Apply(profile);

        Assert.True(changed);
        Assert.Equal(CharacterProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Null(profile.BuiltInKeybindings);

        // Toolbar layout is cleared so it falls back to defaults, but the user's
        // visibility + position choices survive.
        ToolbarSettings toolbar = JsonSerializer.Deserialize<ToolbarSettings>(
            profile.Settings!["Toolbar"].GetRawText())!;
        Assert.Null(toolbar.Layout);
        Assert.False(toolbar.Visible);
        Assert.Equal(ToolbarPosition.Right, toolbar.Position);
    }

    [Fact]
    public void V1Profile_LeavesAutoModeUntouched()
    {
        var general = new GeneralSettings { AutoMode = new AutoActionDefaults { AutoCombat = false, AutoLight = true } };
        CharacterProfile profile = new()
        {
            SchemaVersion = 1,
            Settings = new Dictionary<string, JsonElement>
            {
                ["General"] = JsonSerializer.SerializeToElement(general),
            },
        };

        ProfileMigrations.Apply(profile);

        GeneralSettings after = JsonSerializer.Deserialize<GeneralSettings>(
            profile.Settings!["General"].GetRawText())!;
        Assert.False(after.AutoMode.AutoCombat);
        Assert.True(after.AutoMode.AutoLight);
    }

    [Fact]
    public void V1Profile_WithNoDeltas_StillBumpsVersion()
    {
        CharacterProfile profile = new() { SchemaVersion = 1 };

        bool changed = ProfileMigrations.Apply(profile);

        Assert.True(changed);
        Assert.Equal(CharacterProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Null(profile.BuiltInKeybindings);
    }

    [Fact]
    public void V4Profile_CopiesTheSharedBlessConditionsOntoEveryBuff()
    {
        // "Bless if above" (Health) and the self rest / combat switches (Spells) were
        // one setting for every buff; each existing buff takes what the character had.
        CharacterProfile profile = new()
        {
            SchemaVersion = 4,
            PartyBuffs = new BuffSettings
            {
                Slots =
                {
                    new BuffSlot { Spell = "bles", CastOnSelf = true },
                    new BuffSlot { Spell = "chan", WholePartyOn = true },
                },
            },
            Settings = new Dictionary<string, JsonElement>
            {
                ["Health"] = JsonSerializer.SerializeToElement(new HealthSettings { BlessIfAboveMa = 45 }),
                ["Spells"] = JsonSerializer.SerializeToElement(new SpellsSettings { SelfBlessDuringCombat = true }),
            },
        };

        Assert.True(ProfileMigrations.Apply(profile));

        Assert.Equal(CharacterProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.All(profile.PartyBuffs!.Slots, slot =>
        {
            Assert.Equal(45, slot.BlessIfAboveMa);
            Assert.True(slot.BlessDuringCombat);
            Assert.False(slot.BlessWhileResting);
        });
    }

    [Fact]
    public void V4Profile_WithNoStoredSections_GivesBuffsTheOldDefaults()
    {
        CharacterProfile profile = new()
        {
            SchemaVersion = 4,
            PartyBuffs = new BuffSettings { Slots = { new BuffSlot { Spell = "bles", CastOnSelf = true } } },
        };

        ProfileMigrations.Apply(profile);

        BuffSlot slot = Assert.Single(profile.PartyBuffs!.Slots);
        Assert.Equal(70, slot.BlessIfAboveMa);   // what the shared setting defaulted to, not a new buff's 50
        Assert.False(slot.BlessWhileResting);
        Assert.False(slot.BlessDuringCombat);
    }

    [Fact]
    public void V5Profile_FoldsThePartyBlessSwitchesIntoTheBuffsCastOnTheParty()
    {
        // The party pair was one setting for every party cast. A buff cast only on
        // members takes it; a buff cast both ways keeps a switch on when either side
        // had it on; a buff cast only on the character is left alone.
        CharacterProfile profile = new()
        {
            SchemaVersion = 5,
            PartyBuffs = new BuffSettings
            {
                Slots =
                {
                    new BuffSlot { Spell = "self", CastOnSelf = true, BlessWhileResting = true },
                    new BuffSlot { Spell = "memb", Targets = { "raijin" }, BlessWhileResting = true },
                    new BuffSlot { Spell = "both", CastOnSelf = true, AllMembers = true, BlessWhileResting = true },
                    new BuffSlot { Spell = "chan", CastSolo = true, BlessWhileResting = true },
                    new BuffSlot { Spell = "pray", CastSolo = false, BlessWhileResting = true },
                },
            },
            Settings = new Dictionary<string, JsonElement>
            {
                ["Party"] = JsonSerializer.SerializeToElement(new PartySettings { BlessDuringCombat = true }),
            },
        };

        Assert.True(ProfileMigrations.Apply(profile));

        Dictionary<string, BuffSlot> by = profile.PartyBuffs!.Slots.ToDictionary(s => s.Spell!);
        // (while resting, during combat): self was (on, off), party was (off, on).
        Assert.Equal((true, false), (by["self"].BlessWhileResting, by["self"].BlessDuringCombat));
        Assert.Equal((false, true), (by["memb"].BlessWhileResting, by["memb"].BlessDuringCombat));
        Assert.Equal((true, true), (by["both"].BlessWhileResting, by["both"].BlessDuringCombat));
        Assert.Equal((true, true), (by["chan"].BlessWhileResting, by["chan"].BlessDuringCombat));
        Assert.Equal((false, true), (by["pray"].BlessWhileResting, by["pray"].BlessDuringCombat));
    }

    [Fact]
    public void V4Profile_RunsBothBlessSteps_SelfThenParty()
    {
        CharacterProfile profile = new()
        {
            SchemaVersion = 4,
            PartyBuffs = new BuffSettings { Slots = { new BuffSlot { Spell = "memb", AllMembers = true } } },
            Settings = new Dictionary<string, JsonElement>
            {
                ["Health"] = JsonSerializer.SerializeToElement(new HealthSettings { BlessIfAboveMa = 55 }),
                ["Spells"] = JsonSerializer.SerializeToElement(new SpellsSettings { SelfBlessDuringCombat = true }),
                ["Party"] = JsonSerializer.SerializeToElement(new PartySettings { BlessWhileResting = true }),
            },
        };

        ProfileMigrations.Apply(profile);

        BuffSlot slot = Assert.Single(profile.PartyBuffs!.Slots);
        Assert.Equal(55, slot.BlessIfAboveMa);
        Assert.True(slot.BlessWhileResting);      // the party value
        Assert.False(slot.BlessDuringCombat);     // the self value doesn't reach a members-only buff
    }

    [Fact]
    public void CurrentProfile_KeepsEachBuffsOwnConditions()
    {
        // Once migrated, a buff's own values are never written over by the old
        // shared ones still sitting in the stored sections.
        CharacterProfile profile = new()
        {
            PartyBuffs = new BuffSettings
            {
                Slots = { new BuffSlot { Spell = "bles", BlessIfAboveMa = 20, BlessWhileResting = true } },
            },
            Settings = new Dictionary<string, JsonElement>
            {
                ["Health"] = JsonSerializer.SerializeToElement(new HealthSettings { BlessIfAboveMa = 90 }),
            },
        };

        Assert.False(ProfileMigrations.Apply(profile));

        BuffSlot slot = Assert.Single(profile.PartyBuffs!.Slots);
        Assert.Equal(20, slot.BlessIfAboveMa);
        Assert.True(slot.BlessWhileResting);
    }

    [Fact]
    public void CurrentProfile_IsNoOp()
    {
        CharacterProfile profile = new(); // authored at CurrentSchemaVersion.
        Assert.Equal(CharacterProfile.CurrentSchemaVersion, profile.SchemaVersion);

        bool changed = ProfileMigrations.Apply(profile);

        Assert.False(changed);
    }

    // ----- v7: Priority buffs joins the spell-type priority list -----

    private static SpellsSettings MigratedSpells(string storedSpellsJson)
    {
        CharacterProfile profile = new()
        {
            SchemaVersion = 6,
            Settings = new Dictionary<string, JsonElement>
            {
                ["Spells"] = JsonDocument.Parse(storedSpellsJson).RootElement.Clone(),
            },
        };
        Assert.True(ProfileMigrations.Apply(profile));
        Assert.Equal(CharacterProfile.CurrentSchemaVersion, profile.SchemaVersion);
        return JsonSerializer.Deserialize<SpellsSettings>(profile.Settings!["Spells"].GetRawText())!;
    }

    private static int[] Ranks(SpellsSettings s) => new[]
    {
        s.PriorityEmergencyHeal, s.PriorityMajorPartyHeal, s.PriorityMinorPartyHeal, s.PriorityMajorSelfHeal,
        s.PriorityPriorityBuffs, s.PriorityMinorSelfHeal, s.PriorityDownedAllyHeal, s.PriorityCuring,
        s.PriorityBuffing, s.PriorityDebuffing,
    };

    private static readonly int[] NewDefault = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };

    [Theory]
    // The nine-rank default nobody reordered.
    [InlineData("""{"PriorityEmergencyHeal":1,"PriorityMinorPartyHeal":2,"PriorityMajorPartyHeal":3,"PriorityDownedAllyHeal":4,"PriorityMinorSelfHeal":5,"PriorityMajorSelfHeal":6,"PriorityCuring":7,"PriorityBuffing":8,"PriorityDebuffing":9}""")]
    // The older seven-rank default: Emergency and Downed-ally heal weren't stored.
    [InlineData("""{"PriorityMinorPartyHeal":1,"PriorityMajorPartyHeal":2,"PriorityMinorSelfHeal":3,"PriorityMajorSelfHeal":4,"PriorityCuring":5,"PriorityBuffing":6,"PriorityDebuffing":7}""")]
    // A list someone had already arranged in the new default's order.
    [InlineData("""{"PriorityEmergencyHeal":1,"PriorityMajorPartyHeal":2,"PriorityMinorPartyHeal":3,"PriorityMajorSelfHeal":4,"PriorityMinorSelfHeal":5,"PriorityDownedAllyHeal":6,"PriorityCuring":7,"PriorityBuffing":8,"PriorityDebuffing":9}""")]
    public void V6Profile_UneditedSpellPriority_TakesTheNewDefault(string stored)
        => Assert.Equal(NewDefault, Ranks(MigratedSpells(stored)));

    [Fact]
    public void V6Profile_EditedSpellPriority_KeepsItsOrder_PriorityBuffsLast()
    {
        // Curing moved to the top, the rest pushed down one.
        SpellsSettings s = MigratedSpells(
            """{"PriorityCuring":1,"PriorityEmergencyHeal":2,"PriorityMinorPartyHeal":3,"PriorityMajorPartyHeal":4,"PriorityDownedAllyHeal":5,"PriorityMinorSelfHeal":6,"PriorityMajorSelfHeal":7,"PriorityBuffing":8,"PriorityDebuffing":9,"MinorHealSpell":"mihe"}""");

        Assert.Equal(1, s.PriorityCuring);
        Assert.Equal(2, s.PriorityEmergencyHeal);
        Assert.Equal(5, s.PriorityDownedAllyHeal);
        Assert.Equal(9, s.PriorityDebuffing);
        Assert.Equal(10, s.PriorityPriorityBuffs);
        Assert.Equal("mihe", s.MinorHealSpell);          // the rest of the section is untouched
    }

    [Fact]
    public void V6Profile_EditedSevenRankList_KeepsEmergencyFirstAndDownedAllyFourth()
    {
        // Stored before those two could be moved: they must stay where the engine had
        // them, not jump to a fresh profile's defaults.
        SpellsSettings s = MigratedSpells(
            """{"PriorityCuring":1,"PriorityMinorPartyHeal":2,"PriorityMajorPartyHeal":3,"PriorityMinorSelfHeal":4,"PriorityMajorSelfHeal":5,"PriorityBuffing":6,"PriorityDebuffing":7}""");

        Assert.Equal(1, s.PriorityEmergencyHeal);
        Assert.Equal(4, s.PriorityDownedAllyHeal);
        Assert.Equal(1, s.PriorityCuring);
        Assert.Equal(8, s.PriorityPriorityBuffs);
    }

    [Fact]
    public void V6Profile_CombatProfilesGetPriorityBuffsToo()
    {
        CombatSpellProfile unedited = new();
        unedited.Spells.PriorityEmergencyHeal = 1; unedited.Spells.PriorityMinorPartyHeal = 2;
        unedited.Spells.PriorityMajorPartyHeal = 3; unedited.Spells.PriorityDownedAllyHeal = 4;
        unedited.Spells.PriorityMinorSelfHeal = 5; unedited.Spells.PriorityMajorSelfHeal = 6;
        unedited.Spells.PriorityCuring = 7; unedited.Spells.PriorityBuffing = 8; unedited.Spells.PriorityDebuffing = 9;
        CombatSpellProfile edited = unedited.Clone(newIdentity: true);
        edited.Spells.PriorityCuring = 1; edited.Spells.PriorityEmergencyHeal = 7;
        CharacterProfile profile = new()
        {
            SchemaVersion = 6,
            CombatProfiles = new CombatProfileSettings { Profiles = { unedited, edited } },
        };

        ProfileMigrations.Apply(profile);

        Assert.Equal(5, unedited.Spells.PriorityPriorityBuffs);
        Assert.Equal(6, unedited.Spells.PriorityMinorSelfHeal);
        Assert.Equal(10, edited.Spells.PriorityPriorityBuffs);
        Assert.Equal(1, edited.Spells.PriorityCuring);
    }

    // ----- v9: quest step ticks saved before the drafts gained kill lines -----

    [Fact]
    public void ProfileWithStepTicks_IsMarkedForTheTickMove()
    {
        CharacterProfile profile = new()
        {
            SchemaVersion = 8,
            QuestLog = new List<QuestProgress> { new(128, 30) { CheckedSteps = new List<int> { 0, 1 } } },
        };

        Assert.True(ProfileMigrations.Apply(profile));

        Assert.True(profile.QuestTicksPredateKillSteps);
        Assert.Equal(new[] { 0, 1 }, profile.QuestLog[0].CheckedSteps);   // moved later, with the game data
    }

    [Fact]
    public void ProfileWithoutStepTicks_IsNotMarked()
    {
        CharacterProfile profile = new()
        {
            SchemaVersion = 8,
            QuestLog = new List<QuestProgress> { new(133, 0) { Complete = true } },
        };

        Assert.True(ProfileMigrations.Apply(profile));

        Assert.False(profile.QuestTicksPredateKillSteps);
        // A profile made at this version starts with ticks that already count the new way.
        Assert.False(new CharacterProfile().QuestTicksPredateKillSteps);
    }

    // ----- v10: monster observations saved with spell casts counted as missed swings -----

    private static MonsterObservation SavedObservation(int number, int hits, int misses) => new()
    {
        MonsterNumber = number,
        HitCount = hits,
        HitDamageMin = hits > 0 ? 8 : 0,
        HitDamageMax = hits > 0 ? 128 : 0,
        HitDamageSum = hits * 50L,
        MissCount = misses,
        PhysicalNoEffectCount = 2,
        SpellNoEffectCount = 3,
        FirstObservedAt = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero),
        LastObservedAt = new DateTimeOffset(2026, 10, 9, 18, 30, 0, TimeSpan.Zero),
    };

    private static CharacterProfile Reloaded(CharacterProfile profile) =>
        JsonSerializer.Deserialize<CharacterProfile>(
            JsonSerializer.Serialize(profile, JsonStore.Options), JsonStore.Options)!;

    [Fact]
    public void SavedWeaponSwingTallies_AreResetOnce_AndNothingElseOnTheRecord()
    {
        CharacterProfile profile = new()
        {
            SchemaVersion = 9,
            MonsterObservations = new List<MonsterObservation>
            {
                SavedObservation(2624, hits: 0, misses: 10275),
                SavedObservation(879, hits: 4, misses: 10),
                SavedObservation(2799, hits: 0, misses: 0),   // nothing to reset
            },
        };
        LogService log = new();

        Assert.True(ProfileMigrations.Apply(profile, log));

        Assert.Equal(10, profile.SchemaVersion);
        Assert.All(profile.MonsterObservations, o =>
        {
            Assert.Equal(0, o.HitCount);
            Assert.Equal(0, o.HitDamageMin);
            Assert.Equal(0, o.HitDamageMax);
            Assert.Equal(0, o.HitDamageSum);
            Assert.Equal(0, o.MissCount);
            Assert.Equal(0, o.SwingCount);
            Assert.Equal(2, o.PhysicalNoEffectCount);
            Assert.Equal(3, o.SpellNoEffectCount);
            Assert.Equal(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero), o.FirstObservedAt);
            Assert.Equal(new DateTimeOffset(2026, 10, 9, 18, 30, 0, TimeSpan.Zero), o.LastObservedAt);
        });
        Assert.Equal(new[] { 2624, 879, 2799 }, profile.MonsterObservations.Select(o => o.MonsterNumber));
        LogEntry line = Assert.Single(log.Snapshot(), e => e.Source == "ProfileMigrations");
        Assert.Equal(LogSeverity.Info, line.Severity);
        Assert.Contains("reset on 2 record(s)", line.Message);
    }

    [Fact]
    public void TalliesRecordedAfterTheReset_SurviveAReload()
    {
        CharacterProfile profile = new()
        {
            SchemaVersion = 9,
            MonsterObservations = new List<MonsterObservation> { SavedObservation(2624, hits: 0, misses: 10275) },
        };
        Assert.True(ProfileMigrations.Apply(profile));

        // Swings made under the new version, on the old record and on a new one.
        profile.MonsterObservations[0].HitCount = 3;
        profile.MonsterObservations[0].HitDamageSum = 90;
        profile.MonsterObservations[0].MissCount = 1;
        profile.MonsterObservations.Add(SavedObservation(879, hits: 4, misses: 10));

        CharacterProfile reloaded = Reloaded(profile);
        LogService log = new();

        Assert.False(ProfileMigrations.Apply(reloaded, log));   // already at v10: never again

        Assert.Equal(3, reloaded.MonsterObservations![0].HitCount);
        Assert.Equal(90, reloaded.MonsterObservations[0].HitDamageSum);
        Assert.Equal(1, reloaded.MonsterObservations[0].MissCount);
        Assert.Equal(4, reloaded.MonsterObservations[1].HitCount);
        Assert.Equal(10, reloaded.MonsterObservations[1].MissCount);
        Assert.DoesNotContain(log.Snapshot(), e => e.Source == "ProfileMigrations");
    }

    [Fact]
    public void ProfileWithNoObservations_MovesToTheNewVersionQuietly()
    {
        CharacterProfile profile = new() { SchemaVersion = 9 };
        LogService log = new();

        Assert.True(ProfileMigrations.Apply(profile, log));

        Assert.Equal(CharacterProfile.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Null(profile.MonsterObservations);
        Assert.DoesNotContain(log.Snapshot(), e => e.Source == "ProfileMigrations");
    }
}

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
        Assert.Equal(HealthSettings.DefaultBlessIfAboveMa, slot.BlessIfAboveMa);
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
}

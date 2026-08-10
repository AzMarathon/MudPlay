using System.Collections.Generic;
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
    public void CurrentProfile_IsNoOp()
    {
        CharacterProfile profile = new(); // authored at CurrentSchemaVersion.
        Assert.Equal(CharacterProfile.CurrentSchemaVersion, profile.SchemaVersion);

        bool changed = ProfileMigrations.Apply(profile);

        Assert.False(changed);
    }
}

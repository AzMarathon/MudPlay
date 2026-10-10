using System.Text.Json;
using MudPlay.Game;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// AutoModeController — the master switch. Pins KillSwitchEngaged as the one
// "all autos off" signal, distinct from AllWiredOff (also true for a character
// played by hand who never ticked a toggle): unticking the toggles one by one is
// not the switch, and the switch engages with every toggle already off.
public sealed class AutoModeControllerTests
{
    private static ProfileService BlankProfile()
    {
        ProfileService profile = new();
        profile.LoadBlank();
        return profile;
    }

    private static void WriteAutoMode(ProfileService profile, AutoActionDefaults mode,
                                      AutoActionDefaults? baseModes = null)
    {
        CharacterProfile current = profile.Current!;
        current.Settings ??= new();
        current.Settings["General"] =
            JsonSerializer.SerializeToElement(new GeneralSettings { AutoMode = mode, AutoModeBase = baseModes });
    }

    private static AutoActionDefaults ReadAutoMode(ProfileService profile) =>
        JsonSerializer.Deserialize<GeneralSettings>(profile.Current!.Settings!["General"].GetRawText())!.AutoMode;

    private static AutoActionDefaults AllOff() => new()
    {
        AutoCombat = false, AutoNuke = false, AutoHeal = false, AutoRest = false,
        AutoBless = false, AutoLight = false, AutoGetItems = false,
        AutoGetCash = false, AutoSneak = false, AutoHide = false, AutoSearch = false,
    };

    private static AutoActionDefaults Only(Action<AutoActionDefaults> set)
    {
        AutoActionDefaults mode = AllOff();
        set(mode);
        return mode;
    }

    [Fact]
    public void ManualPlay_AllTogglesOff_SwitchStillOn()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff());
        AutoModeController controller = new(profile);

        Assert.True(controller.AllWiredOff);
        Assert.False(controller.KillSwitchEngaged);
        Assert.False(controller.Blocks("Test"));
    }

    [Fact]
    public void Press_WithTogglesOn_SwitchesOffAndClearsThem()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);

        controller.ToggleAll();

        Assert.True(controller.AllWiredOff);
        Assert.True(controller.KillSwitchEngaged);
    }

    [Fact]
    public void SecondPress_SwitchesOnAndRestoresTheToggles()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => { m.AutoCombat = true; m.AutoSneak = true; }));
        AutoModeController controller = new(profile);
        controller.ToggleAll();

        controller.ToggleAll();

        Assert.False(controller.KillSwitchEngaged);
        AutoActionDefaults mode = ReadAutoMode(profile);
        Assert.True(mode.AutoCombat);
        Assert.True(mode.AutoSneak);
        Assert.False(mode.AutoHeal);
    }

    // The ruling's second half: toggles unticked by hand are not the switch, so
    // an off must still engage it.
    [Fact]
    public void TurnOff_WithEveryToggleAlreadyOff_StillEngages()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff());
        AutoModeController controller = new(profile);
        List<bool> events = new();
        controller.KillSwitchToggled += events.Add;

        controller.TurnOff("test");

        Assert.True(controller.KillSwitchEngaged);
        Assert.Equal(new[] { true }, events);
    }

    [Fact]
    public void TurnOff_Twice_IsOneSwitch()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        List<bool> events = new();
        controller.KillSwitchToggled += events.Add;

        controller.TurnOff("test");
        controller.TurnOff("test");
        controller.TurnOn("test");

        // The second off must not overwrite what the first remembered.
        Assert.True(ReadAutoMode(profile).AutoCombat);
        Assert.Equal(new[] { true, false }, events);
    }

    [Fact]
    public void TurnOn_NothingRemembered_SwitchesOnTheBaseModes()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff(), baseModes: Only(m => { m.AutoCombat = true; m.AutoRest = true; }));
        AutoModeController controller = new(profile);
        List<bool> events = new();
        controller.KillSwitchToggled += events.Add;

        controller.TurnOn("test");

        AutoActionDefaults mode = ReadAutoMode(profile);
        Assert.True(mode.AutoCombat);
        Assert.True(mode.AutoRest);
        Assert.False(mode.AutoNuke);
        Assert.False(controller.KillSwitchEngaged);
        // The switch never went off, so nothing is told it came back on.
        Assert.Empty(events);
    }

    [Fact]
    public void TurnOn_AfterAnOffWithEveryToggleOff_SwitchesOnTheBaseModes()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff(), baseModes: Only(m => m.AutoHeal = true));
        AutoModeController controller = new(profile);
        controller.TurnOff("test");

        controller.TurnOn("test");

        Assert.False(controller.KillSwitchEngaged);
        Assert.True(ReadAutoMode(profile).AutoHeal);
    }

    [Fact]
    public void TurnOn_SwitchAlreadyOnWithAToggleOn_ChangesNothing()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoLight = true), baseModes: Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);

        controller.TurnOn("test");

        AutoActionDefaults mode = ReadAutoMode(profile);
        Assert.True(mode.AutoLight);
        Assert.False(mode.AutoCombat);
    }

    [Fact]
    public void TurnOn_KeepsAToggleTickedByHandWhileOff()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        controller.TurnOff("test");
        WriteAutoMode(profile, Only(m => m.AutoSearch = true));

        controller.TurnOn("test");

        AutoActionDefaults mode = ReadAutoMode(profile);
        Assert.True(mode.AutoCombat);
        Assert.True(mode.AutoSearch);
    }

    [Fact]
    public void Blocks_OnlyWhileOff_AndCountsPerSystem()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        Assert.False(controller.Blocks("Triggers"));
        Assert.Empty(controller.SkippedSinceOff);

        controller.TurnOff("test");
        Assert.True(controller.Blocks("Triggers", "one"));
        Assert.True(controller.Blocks("Triggers", "two"));
        Assert.True(controller.Blocks("Polls"));

        Assert.Equal(2, controller.SkippedSinceOff["Triggers"]);
        Assert.Equal(1, controller.SkippedSinceOff["Polls"]);
        Assert.Equal("Polls 1, Triggers 2", controller.DescribeSkipped());

        // A new off starts a new count.
        controller.TurnOn("test");
        controller.TurnOff("test");
        Assert.Empty(controller.SkippedSinceOff);
    }

    [Fact]
    public void TurnOff_AsksWhatWasInFlightBeforeItChangesAnything()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        bool? engagedWhenAsked = null;
        controller.DescribeInFlight = () => { engagedWhenAsked = controller.KillSwitchEngaged; return "a loop"; };

        controller.TurnOff("test");

        Assert.False(engagedWhenAsked);
    }

    // "the reconnect should not turn autos back on in the situation that the user
    // was connected, then hung up and turned them off, then reconnect" (user,
    // 2026-10-09): with the switch off the re-enable boxes change nothing and the
    // switch stays off.
    [Fact]
    public void ReEnableOnReconnect_SwitchOff_ChangesNothing_AndLeavesTheSwitchOff()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        controller.TurnOff("test");
        List<bool> events = new();
        controller.KillSwitchToggled += events.Add;

        bool done = controller.ReEnableOnReconnect(new GeneralSettings
        {
            ReEnableAutoCombatOnReconnect = true,
            ReEnableAutoHealRestOnReconnect = true,
        });

        Assert.False(done);
        Assert.True(controller.KillSwitchEngaged);
        Assert.True(ReadAutoMode(profile).SameAs(AllOff()));
        Assert.Empty(events);

        // What the user had is still what comes back when they switch it on.
        controller.TurnOn("test");
        AutoActionDefaults mode = ReadAutoMode(profile);
        Assert.True(mode.AutoCombat);
        Assert.False(mode.AutoHeal);
    }

    [Fact]
    public void ReEnableOnReconnect_SwitchOn_TicksTheOptedInToggles()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff());
        AutoModeController controller = new(profile);

        bool done = controller.ReEnableOnReconnect(new GeneralSettings
        {
            ReEnableAutoCombatOnReconnect = true,
            ReEnableAutoHealRestOnReconnect = true,
        });

        Assert.True(done);
        AutoActionDefaults mode = ReadAutoMode(profile);
        Assert.True(mode.AutoCombat);
        Assert.True(mode.AutoHeal);
        Assert.True(mode.AutoRest);
        Assert.False(mode.AutoNuke);
    }

    [Fact]
    public void ResetSnapshot_ClearsTheSwitch_AndSaysSo()
    {
        // Profile-load boundary: a freshly loaded character starts with the
        // switch on, and whatever it held is released.
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, Only(m => m.AutoCombat = true));
        AutoModeController controller = new(profile);
        controller.ToggleAll();
        List<bool> events = new();
        controller.KillSwitchToggled += events.Add;

        controller.ResetSnapshot();

        Assert.False(controller.KillSwitchEngaged);
        Assert.Equal(new[] { false }, events);
    }
}

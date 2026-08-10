using System.Text.Json;
using MudPlay.Game;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// <see cref="AutoModeController"/> — the Auto-All master kill switch.
/// Pins <see cref="AutoModeController.KillSwitchEngaged"/> as the "user
/// actively silenced automation" signal, deliberately distinct from
/// <see cref="AutoModeController.AllWiredOff"/> (which is ALSO true for a
/// manual-play character that simply never enabled an engine). The
/// main-menu auto-entry gate reads KillSwitchEngaged, so this distinction
/// is exactly what lets a manual-play character still auto-enter the realm
/// on the first connect instead of being stranded at the game menu.
/// </summary>
public sealed class AutoModeControllerTests
{
    private static ProfileService BlankProfile()
    {
        ProfileService profile = new();
        profile.LoadBlank();
        return profile;
    }

    private static void WriteAutoMode(ProfileService profile, AutoActionDefaults mode)
    {
        CharacterProfile current = profile.Current!;
        current.Settings ??= new();
        current.Settings["General"] =
            JsonSerializer.SerializeToElement(new GeneralSettings { AutoMode = mode });
    }

    private static AutoActionDefaults AllOff() => new()
    {
        AutoCombat = false, AutoNuke = false, AutoHealRest = false,
        AutoBless = false, AutoLight = false, AutoGetItems = false,
        AutoGetCash = false, AutoSneak = false, AutoHide = false,
    };

    private static AutoActionDefaults CombatOn() => new() { AutoCombat = true };

    [Fact]
    public void ManualPlay_AllEnginesOff_ButNeverKilled_KillSwitchNotEngaged()
    {
        // The regression guard: a character running every auto-engine off
        // (manual play) reports AllWiredOff but NOT KillSwitchEngaged, so
        // the auto-entry gate (!KillSwitchEngaged) still fires.
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff());
        AutoModeController controller = new(profile);

        Assert.True(controller.AllWiredOff);
        Assert.False(controller.KillSwitchEngaged);
    }

    [Fact]
    public void KillPress_EngagesKillSwitch()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, CombatOn());
        AutoModeController controller = new(profile);
        Assert.False(controller.KillSwitchEngaged);

        controller.ToggleAll();   // engines were on → kill

        Assert.True(controller.AllWiredOff);
        Assert.True(controller.KillSwitchEngaged);
    }

    [Fact]
    public void RestorePress_ClearsKillSwitch()
    {
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, CombatOn());
        AutoModeController controller = new(profile);
        controller.ToggleAll();   // kill
        Assert.True(controller.KillSwitchEngaged);

        controller.ToggleAll();   // restore

        Assert.False(controller.KillSwitchEngaged);
        Assert.False(controller.AllWiredOff);
    }

    [Fact]
    public void KillSwitchToggled_FiresTrueOnKill_FalseOnRestore()
    {
        // The event AppServices bridges to MovementController so navigation
        // suspends on engage and resumes on restore.
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, CombatOn());
        AutoModeController controller = new(profile);
        List<bool> events = new();
        controller.KillSwitchToggled += engaged => events.Add(engaged);

        controller.ToggleAll();   // kill
        controller.ToggleAll();   // restore

        Assert.Equal(new[] { true, false }, events);
    }

    [Fact]
    public void KillSwitchToggled_DoesNotFire_WhenRestoreHasNoSnapshot()
    {
        // A restore press with nothing snapshotted (everything already off, no
        // prior kill this session) is a no-op — it must not emit a spurious
        // resume, which would fight a nav the user is intentionally running.
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, AllOff());
        AutoModeController controller = new(profile);
        List<bool> events = new();
        controller.KillSwitchToggled += engaged => events.Add(engaged);

        controller.ToggleAll();   // all already off, no snapshot → no-op

        Assert.Empty(events);
    }

    [Fact]
    public void ResetSnapshot_ClearsKillSwitch()
    {
        // Profile-load boundary: a freshly loaded character must not
        // inherit the previous one's silenced-automation flag.
        ProfileService profile = BlankProfile();
        WriteAutoMode(profile, CombatOn());
        AutoModeController controller = new(profile);
        controller.ToggleAll();   // kill
        Assert.True(controller.KillSwitchEngaged);

        controller.ResetSnapshot();

        Assert.False(controller.KillSwitchEngaged);
    }
}

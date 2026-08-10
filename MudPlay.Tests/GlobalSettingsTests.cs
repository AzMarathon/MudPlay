using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using Xunit;

namespace MudPlay.Tests;

// GlobalSettings.StartupProfile — the app-level "auto-load last profile" decision:
// return the last-used profile only when the toggle is on and one exists, else
// null (→ the app opens a blank draft).
public sealed class GlobalSettingsTests
{
    [Fact]
    public void StartupProfile_ToggleOff_ReturnsNull()
    {
        var g = new GlobalSettings
        {
            AutoLoadLastProfile = false,
            LastUsedProfile = new ProfileRef("Paradigm", "MudPlay"),
        };

        Assert.Null(g.StartupProfile());
    }

    [Fact]
    public void StartupProfile_ToggleOn_WithLastUsed_ReturnsIt()
    {
        var last = new ProfileRef("Paradigm", "MudPlay");
        var g = new GlobalSettings { AutoLoadLastProfile = true, LastUsedProfile = last };

        Assert.Equal(last, g.StartupProfile());
    }

    [Fact]
    public void StartupProfile_ToggleOn_FirstRun_ReturnsNull()
    {
        // Toggle on but nothing loaded yet (fresh install) → still opens blank.
        var g = new GlobalSettings { AutoLoadLastProfile = true, LastUsedProfile = null };

        Assert.Null(g.StartupProfile());
    }

    [Fact]
    public void AutoLoadLastProfile_DefaultsOff()
    {
        Assert.False(new GlobalSettings().AutoLoadLastProfile);
    }
}

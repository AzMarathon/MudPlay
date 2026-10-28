using System.Text.Json;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins the Buff Watchdog's one mana floor for every buff (BuffSettings.BlessIfAboveMa):
// a profile saved before it existed reads the per-buff default, and a set value is
// kept across a save.
public sealed class BuffSettingsFloorTests
{
    [Fact]
    public void AProfileFromBeforeTheSharedFloor_ReadsTheNewBuffDefault()
    {
        BuffSettings settings = JsonSerializer.Deserialize<BuffSettings>(
            """{"Slots":[{"Spell":"chao","CastOnSelf":true,"BlessIfAboveMa":50}]}""", JsonStore.Options)!;

        Assert.Equal(BuffSlot.DefaultBlessIfAboveMa, settings.BlessIfAboveMa);
    }

    [Fact]
    public void TheSharedFloor_SurvivesASave()
    {
        BuffSettings saved = new() { BlessIfAboveMa = 0 };

        BuffSettings read = JsonSerializer.Deserialize<BuffSettings>(
            JsonSerializer.Serialize(saved, JsonStore.Options), JsonStore.Options)!;

        Assert.Equal(0, read.BlessIfAboveMa);
    }
}

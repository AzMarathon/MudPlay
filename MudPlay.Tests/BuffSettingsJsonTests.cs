using System.Text.Json;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// BuffSettings' new ordering flags round-trip through the app's JSON options, and a
// legacy file that predates them loads with both false (additive bools, no migration).
public sealed class BuffSettingsJsonTests
{
    [Fact]
    public void ManualOrderAndPriority_RoundTrip_AndPreserveSlotOrder()
    {
        var settings = new BuffSettings { ManualOrder = true, PriorityTopDown = true };
        settings.Slots.Add(new BuffSlot { Spell = "bbbb" });
        settings.Slots.Add(new BuffSlot { Spell = "aaaa" });

        string json = JsonSerializer.Serialize(settings, JsonStore.Options);
        BuffSettings? back = JsonSerializer.Deserialize<BuffSettings>(json, JsonStore.Options);

        Assert.NotNull(back);
        Assert.True(back!.ManualOrder);
        Assert.True(back.PriorityTopDown);
        Assert.Equal(new[] { "bbbb", "aaaa" }, back.Slots.ConvertAll(s => s.Spell));
    }

    [Fact]
    public void LegacyFileWithoutFlags_DefaultsBothFalse()
    {
        const string legacy = """
            { "Slots": [ { "Spell": "bles" } ] }
            """;
        BuffSettings? loaded = JsonSerializer.Deserialize<BuffSettings>(legacy, JsonStore.Options);
        Assert.NotNull(loaded);
        Assert.False(loaded!.ManualOrder);
        Assert.False(loaded.PriorityTopDown);
        Assert.Single(loaded.Slots);
    }
}

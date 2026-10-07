using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

public sealed class TeleportSettingsTests
{
    // A character that has never opened the tab allows automatic walks no teleports.
    [Fact]
    public void NoEntry_AllowsNothing()
    {
        Assert.Empty(TeleportsSectionViewModel.ReadOrDefault(null).AutomaticWalkTeleports);
        Assert.Empty(TeleportsSectionViewModel.ReadOrDefault(new CharacterProfile()).AutomaticWalkTeleports);
    }

    [Fact]
    public void StoredTeleports_ReadBackAsTheSameSpots()
    {
        (RoomKey From, RoomKey To) hatch = (new RoomKey(16, 356), new RoomKey(16, 543));
        CharacterProfile profile = new() { Settings = new() };
        profile.Settings[TeleportsSectionViewModel.TabKey] = JsonSerializer.SerializeToElement(
            new TeleportSettings { AutomaticWalkTeleports = { TeleportCatalog.KeyOf(hatch.From, hatch.To) } });

        TeleportSettings read = TeleportsSectionViewModel.ReadOrDefault(profile);

        Assert.Equal(new[] { hatch }, TeleportCatalog.ParseKeys(read.AutomaticWalkTeleports));
    }
}

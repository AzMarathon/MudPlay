using System.Text.Json;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

public sealed class OtherSettingsTests
{
    // The terminal-line switch took over the saved name of the wider "show monster
    // HP lookup" switch, so a character that had that off keeps the line off.
    [Theory]
    [InlineData("""{ "ShowMonsterHpLookup": false }""", false)]
    [InlineData("""{ "ShowMonsterHpLookup": true }""", true)]
    [InlineData("{}", true)]
    public void PrintMonsterHpOnLook_ReadsTheOlderSwitchsSavedValue(string saved, bool expected)
    {
        OtherSettings settings = JsonSerializer.Deserialize<OtherSettings>(saved)!;
        Assert.Equal(expected, settings.PrintMonsterHpOnLook);
        Assert.Contains("\"ShowMonsterHpLookup\"", JsonSerializer.Serialize(settings));
    }
}

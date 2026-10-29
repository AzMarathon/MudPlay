using System.IO;
using MudPlay.Models.Profile;
using MudPlay.Models.Settings;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins the Buff Watchdog's one mana floor for every buff (BuffSettings.BlessIfAboveMa)
// through the profile store the client saves and loads characters with: a profile
// saved before it existed reads the per-buff default, and a set value survives a
// save and a reload.
public sealed class BuffSettingsFloorTests : IDisposable
{
    private readonly string _bbs = "test-buff-floor-" + Guid.NewGuid().ToString("N")[..12];

    public BuffSettingsFloorTests()
    {
        ScratchRoot.Require();
        new BbsProfileStore().Save(new BbsProfile { Name = _bbs });
    }

    public void Dispose()
    {
        if (Directory.Exists(AppPaths.BbsFolder(_bbs))) Directory.Delete(AppPaths.BbsFolder(_bbs), true);
    }

    [Fact]
    public void AProfileFromBeforeTheSharedFloor_ReadsTheNewBuffDefault()
    {
        string path = AppPaths.CharacterProfileFile(_bbs, "Hero");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            { "Name": "Hero", "PartyBuffs": { "Slots": [ { "Spell": "chao", "CastOnSelf": true, "BlessIfAboveMa": 30 } ] } }
            """);

        CharacterProfile hero = TestProfiles.Service().Load(_bbs, "Hero");

        Assert.Equal(BuffSlot.DefaultBlessIfAboveMa, hero.PartyBuffs!.BlessIfAboveMa);
    }

    [Fact]
    public void TheSharedFloor_SurvivesASaveAndAReload()
    {
        TestProfiles.Service().CreateProfile(_bbs, "Hero", p => (p.PartyBuffs ??= new BuffSettings()).BlessIfAboveMa = 0);

        CharacterProfile hero = TestProfiles.Service().Load(_bbs, "Hero");

        Assert.Equal(0, hero.PartyBuffs!.BlessIfAboveMa);
    }
}

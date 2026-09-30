using System;
using System.IO;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Simulation;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins the level move the live check simulates with: HP and mana shift by the class
// formulas' difference between the two levels, and a Mystic's kai never lands in
// max mana.
public sealed class SimLevelProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-simlevel-" + Path.GetRandomFileName());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
    }

    private GameDataCache Data()
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Classes.json"), JsonSerializer.Serialize(new object[]
        {
            new { Number = 1, Name = "Mage", MinHits = 2, MaxHits = 4, MageryType = 1, MageryLVL = 3 },
            new { Number = 2, Name = "Mystic", MinHits = 4, MaxHits = 6, MageryType = 5, MageryLVL = 3 },
        }));
        File.WriteAllText(Path.Combine(dir, "Races.json"), JsonSerializer.Serialize(new object[]
        {
            new { Number = 1, Name = "Human", HPPerLVL = 0 },
        }));
        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        return cache;
    }

    private static PlayerStats Char(string cls, int maxMana) => new()
    {
        Name = "Test", Race = "Human", Class = cls, Level = 20, MaxHits = 200, MaxMana = maxMana,
        Health = 50, Intellect = 60, Willpower = 60, Charm = 50,
    };

    [Fact]
    public void AMagesManaShiftsByTheManaFormula()
    {
        PlayerStats at30 = SimLevelProjection.StatsAt(Char("Mage", 150), 30, Data());
        Assert.Equal(150 + 3 * 10 * 2, at30.MaxMana);
        Assert.True(at30.MaxHits > 200);
    }

    [Fact]
    public void AMysticsKaiIsNotAddedToMaxMana()
    {
        PlayerStats at30 = SimLevelProjection.StatsAt(Char("Mystic", 0), 30, Data());
        Assert.Equal(0, at30.MaxMana);
        Assert.True(at30.MaxHits > 200);
    }

    [Fact]
    public void AnUnknownClassKeepsTodaysPools()
    {
        PlayerStats at30 = SimLevelProjection.StatsAt(Char("Nobody", 150), 30, Data());
        Assert.Equal((200, 150, 30), (at30.MaxHits, at30.MaxMana, at30.Level));
    }
}

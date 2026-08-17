using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// Pins the derived cross-reference rows SpellInfoRowsBuilder adds to a spell's
// Game Data tab: the "Negated by" reverse lookup (items whose NegateSpell-0..9
// list the spell) and the clickable record links (name text + a link per
// resolved record). Uses the same synthetic-table shape as the other
// GameDataCache tests.
public sealed class SpellInfoRowsBuilderTests : IDisposable
{
    private readonly string _root;

    public SpellInfoRowsBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-spellinfo-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private GameDataCache NewCache(object[] spells, object[]? items = null, object[]? monsters = null)
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), JsonSerializer.Serialize(spells));
        if (items is not null) File.WriteAllText(Path.Combine(dir, "Items.json"), JsonSerializer.Serialize(items));
        if (monsters is not null) File.WriteAllText(Path.Combine(dir, "Monsters.json"), JsonSerializer.Serialize(monsters));
        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        return cache;
    }

    private static Dictionary<string, object> SpellRow(int number, string name)
        => new() { ["Number"] = number, ["Name"] = name };

    private static Dictionary<string, object> NamedRow(int number, string name)
        => new() { ["Number"] = number, ["Name"] = name };

    private static Dictionary<string, object> ItemRow(int number, string name, params int[] negates)
    {
        var row = new Dictionary<string, object> { ["Number"] = number, ["Name"] = name };
        for (int i = 0; i < 10; i++) row[$"NegateSpell-{i}"] = i < negates.Length ? negates[i] : 0;
        return row;
    }

    [Fact]
    public void Build_ListsItemsThatNegateTheSpell_AsLinks()
    {
        GameDataCache cache = NewCache(
            spells: [SpellRow(50, "hold person"), SpellRow(51, "blindness")],
            items:
            [
                ItemRow(100, "Ring of Free Action", 50),   // negates hold person
                ItemRow(101, "Amulet of Clarity", 51, 50), // negates blindness AND hold person
                ItemRow(102, "Plain Dagger"),              // negates nothing
            ]);

        GameDataInfoRow negated = Assert.Single(
            new SpellInfoRowsBuilder(cache).Build(50).Where(r => r.Label == "Negated by"));

        // Value keeps the plain names (text fallback + what a reader scans).
        Assert.Contains("Ring of Free Action", negated.Value);
        Assert.Contains("Amulet of Clarity", negated.Value);
        Assert.DoesNotContain("Plain Dagger", negated.Value);

        // …and each item is a clickable link.
        Assert.True(negated.HasLinks);
        Assert.Equal(
            new[] { "Ring of Free Action", "Amulet of Clarity" },
            negated.Links!.Select(l => l.Name));
        Assert.All(negated.Links!, l => Assert.True(l.IsLinked));
    }

    [Fact]
    public void Build_NoNegatingItems_OmitsRow()
    {
        GameDataCache cache = NewCache(
            spells: [SpellRow(50, "hold person")],
            items: [ItemRow(102, "Plain Dagger")]);

        Assert.DoesNotContain(new SpellInfoRowsBuilder(cache).Build(50), r => r.Label == "Negated by");
    }

    [Fact]
    public void Build_CastedBySourceList_ResolvesMonsterLinks()
    {
        var spell = SpellRow(50, "vampire kill");
        spell["Casted By"] = "Monster #200, Monster #201";
        GameDataCache cache = NewCache(
            spells: [spell],
            monsters: [NamedRow(200, "vampire magus"), NamedRow(201, "vampire acolyte")]);

        GameDataInfoRow row = Assert.Single(
            new SpellInfoRowsBuilder(cache).Build(50).Where(r => r.Label == "Casted By"));

        Assert.True(row.HasLinks);
        Assert.Equal(new[] { "vampire magus", "vampire acolyte" }, row.Links!.Select(l => l.Name));
        Assert.All(row.Links!, l => Assert.True(l.IsLinked));
    }
}

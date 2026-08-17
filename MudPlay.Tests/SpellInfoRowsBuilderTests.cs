using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// Pins the "Negated by" reverse-lookup row SpellInfoRowsBuilder adds to a spell's
// Game Data tab: the items whose NegateSpell-0..9 columns list this spell's
// Number. Uses the same synthetic-table shape as the other GameDataCache tests.
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

    private GameDataCache NewCache(object[] spells, object[] items)
    {
        string dir = Path.Combine(_root, "set");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), JsonSerializer.Serialize(spells));
        File.WriteAllText(Path.Combine(dir, "Items.json"), JsonSerializer.Serialize(items));
        GameDataCache cache = new(_root);
        cache.SwitchSet("set");
        return cache;
    }

    private static Dictionary<string, object> SpellRow(int number, string name)
        => new() { ["Number"] = number, ["Name"] = name };

    private static Dictionary<string, object> ItemRow(int number, string name, params int[] negates)
    {
        var row = new Dictionary<string, object> { ["Number"] = number, ["Name"] = name };
        for (int i = 0; i < 10; i++) row[$"NegateSpell-{i}"] = i < negates.Length ? negates[i] : 0;
        return row;
    }

    [Fact]
    public void Build_ListsItemsThatNegateTheSpell()
    {
        GameDataCache cache = NewCache(
            spells: [SpellRow(50, "hold person"), SpellRow(51, "blindness")],
            items:
            [
                ItemRow(100, "Ring of Free Action", 50),   // negates hold person
                ItemRow(101, "Amulet of Clarity", 51, 50), // negates blindness AND hold person
                ItemRow(102, "Plain Dagger"),              // negates nothing
            ]);

        IReadOnlyList<GameDataInfoRow> rows = new SpellInfoRowsBuilder(cache).Build(50);

        GameDataInfoRow negated = Assert.Single(rows.Where(r => r.Label == "Negated by"));
        Assert.Contains("Ring of Free Action", negated.Value);
        Assert.Contains("Amulet of Clarity", negated.Value);
        Assert.DoesNotContain("Plain Dagger", negated.Value);
    }

    [Fact]
    public void Build_NoNegatingItems_OmitsRow()
    {
        GameDataCache cache = NewCache(
            spells: [SpellRow(50, "hold person")],
            items: [ItemRow(102, "Plain Dagger")]);

        IReadOnlyList<GameDataInfoRow> rows = new SpellInfoRowsBuilder(cache).Build(50);

        Assert.DoesNotContain(rows, r => r.Label == "Negated by");
    }
}

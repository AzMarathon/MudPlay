using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;
using Xunit;

namespace MudPlay.Tests;

// ItemMdbViewBuilder renders an item's MDB record into the "Other Info" key/value
// rows the item dialog shows. Covers the two fixes: a flag-style ability code
// whose paired value is 0 must read "Yes" (presence), not the misleading "0"; and
// the standalone never-drop / delete-on-death MDB columns are surfaced as rows.
public sealed class ItemMdbViewBuilderTests : IDisposable
{
    private readonly string _root;

    public ItemMdbViewBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-itemmdb-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    // phoenix feather : LoyalItem (Abil 100, val 0) + Del@Maint (Abil 119, val 0),
    //                   the standalone Not Droppable / Destroy On Death columns,
    //                   and a negated spell (526 magma heat).
    private const string Items =
        "[{\"Number\":1,\"Name\":\"phoenix feather\",\"ItemType\":0,\"Worn\":8," +
        "\"Abil-0\":100,\"AbilVal-0\":0," +
        "\"Abil-1\":119,\"AbilVal-1\":0," +
        "\"NegateSpell-0\":526," +
        "\"Not Droppable\":1,\"Destroy On Death\":1}]";

    private const string Spells = "[{\"Number\":526,\"Name\":\"magma heat\"}]";

    private IReadOnlyList<KeyValuePair<string, string>> BuildInfo()
    {
        string dir = Path.Combine(_root, "realm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), Items);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), Spells);
        GameDataCache cache = new(_root);
        cache.SwitchSet("realm");
        return new ItemMdbViewBuilder(cache, playerCharm: 50).Build("1").OtherInfo;
    }

    [Fact]
    public void FlagAbilityCodes_RenderPresence_NotRawZero()
    {
        IReadOnlyList<KeyValuePair<string, string>> info = BuildInfo();

        Assert.Contains(new KeyValuePair<string, string>("LoyalItem", "Yes"), info);
        Assert.Contains(new KeyValuePair<string, string>("Del@Maint", "Yes"), info);
        Assert.DoesNotContain(info, kv => kv.Key == "LoyalItem" && kv.Value == "0");
    }

    [Fact]
    public void MdbFlagColumns_And_Negates_Surface()
    {
        IReadOnlyList<KeyValuePair<string, string>> info = BuildInfo();

        Assert.Contains(new KeyValuePair<string, string>("Not Droppable", "Yes"), info);
        Assert.Contains(new KeyValuePair<string, string>("Delete on Death", "Yes"), info);
        Assert.Contains(new KeyValuePair<string, string>("Negates", "magma heat"), info);
    }
}

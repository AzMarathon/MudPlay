using System.IO;
using System.Linq;
using System.Text.Json;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The item-use-teleport chain: item Abil 43 (CastsSp) → spell → spell Abil 148
// (TextBlock) → TBInfo whose Action carries a literal `teleport <room> <map>` and
// a `roomitem` gate. The canonical case is the potion of levitation (item 992 →
// spell 607 → TBInfo 1421 → 9/1009, gated on fixture 993).
public sealed class ItemUseTeleportResolverTests : IDisposable
{
    private readonly string _root;

    public ItemUseTeleportResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-itemteleport-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private (JsonDocument Items, KnownSpellCatalog Catalog, TBInfoStore Store) NewSet(
        string itemsJson, string spellsJson, string tbinfoJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), itemsJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), spellsJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), tbinfoJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        TBInfoStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        return (cache.GetRawTable("Items")!, new KnownSpellCatalog(cache), store);
    }

    private const string PotionItems = """
        [ { "Number": 992, "Name": "potion of levitation", "Abil-0": 43, "AbilVal-0": 607 } ]
        """;
    private const string PotionSpells = """
        [ { "Number": 607, "Name": "potion of levitation", "Abil-0": 148, "AbilVal-0": 1421 } ]
        """;
    private const string PotionTbInfo = """
        [ { "Number": 1421, "LinkTo": 0,
            "Action": "roomitem 993 1834:message 1835:teleport 1009 9:message 1836\n",
            "Called From": "Spell #607" } ]
        """;

    [Fact]
    public void ResolvesItemCastSpellTextblockTeleportChain()
    {
        (JsonDocument items, KnownSpellCatalog catalog, TBInfoStore store) = NewSet(PotionItems, PotionSpells, PotionTbInfo);

        ItemUseTeleportResolver.ItemUseTeleport t =
            Assert.Single(ItemUseTeleportResolver.Enumerate(items, catalog, store));

        Assert.Equal(992, t.HolderItemId);
        Assert.Equal("potion of levitation", t.HolderItemName);
        Assert.Equal(993, t.GateItemId);                       // the roomitem gate → source anchor
        Assert.Equal(new RoomKey(9, 1009), t.Destination);     // teleport 1009 9 → map 9 room 1009
        Assert.Equal(0, t.MinLevel);
    }

    [Fact]
    public void NoCastsSpAbility_YieldsNothing()
    {
        // An item with no Abil 43 (CastsSp) isn't an item-use teleport.
        const string items = """
            [ { "Number": 992, "Name": "plain rock", "Abil-0": 44, "AbilVal-0": 607 } ]
            """;
        (JsonDocument doc, KnownSpellCatalog catalog, TBInfoStore store) = NewSet(items, PotionSpells, PotionTbInfo);
        Assert.Empty(ItemUseTeleportResolver.Enumerate(doc, catalog, store));
    }

    [Fact]
    public void SpellWithoutTeleportTextblock_YieldsNothing()
    {
        // The cast spell's TextBlock TBInfo carries no literal teleport → no edge.
        const string tb = """
            [ { "Number": 1421, "LinkTo": 0, "Action": "message 1835\n", "Called From": "Spell #607" } ]
            """;
        (JsonDocument doc, KnownSpellCatalog catalog, TBInfoStore store) = NewSet(PotionItems, PotionSpells, tb);
        Assert.Empty(ItemUseTeleportResolver.Enumerate(doc, catalog, store));
    }

    // Rows are sifted by their raw text before any slot is read, so a casting item
    // has to be found however its row is spaced, and a 43 that isn't an ability
    // slot's code (another column's value, part of a longer number, inside a text)
    // must not make an item cast.
    [Theory]
    [InlineData("""{"Number":992,"Name":"potion","Abil-0":43,"AbilVal-0":607}""", true)]          // no spaces
    [InlineData("{ \"Number\": 992, \"Name\": \"potion\", \"Abil-3\" :\t43 , \"AbilVal-3\": 607 }", true)]   // tab after the colon
    [InlineData("{ \"Number\": 992, \"Name\": \"potion\", \"AbilVal-0\": 607, \"Abil-0\":\n  43\n}", true)]   // 43 on its own line, last
    [InlineData("""{ "Number": 43, "Name": "rock", "Weight": 43, "Abil-0": 44, "AbilVal-0": 607 }""", false)]   // 43 elsewhere
    [InlineData("""{ "Number": 992, "Name": "rock", "Abil-0": 430, "AbilVal-0": 607 }""", false)]
    [InlineData("""{ "Number": 992, "Name": "Abil-0: 43", "Abil-0": 143, "AbilVal-0": 607 }""", false)]
    [InlineData("""{ "Number": 992, "Name": "rock", "Abil-0": "43", "AbilVal-0": 607 }""", false)]   // a text, not a number
    public void CastingItemIsFound_HoweverTheRowIsWritten(string row, bool casts)
    {
        (JsonDocument doc, KnownSpellCatalog catalog, TBInfoStore store) =
            NewSet("[ " + row + " ]", PotionSpells, PotionTbInfo);

        Assert.Equal(casts ? 1 : 0, ItemUseTeleportResolver.Enumerate(doc, catalog, store).Count());
    }
}

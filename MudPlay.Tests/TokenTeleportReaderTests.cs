using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Tokens;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins TokenTeleportReader's read of the token→teleport chain: item (ItemType 10,
// "token of <place>") → Abil 43 (CastsSp) → spell → Abil 148 (TextBlock) → TBInfo
// whose one Action line carries `minlevel N`, `price <copper>`, and
// `teleport <room> <map>`. A "the Lost City" token keys under "lost city" (the
// leading article dropped), and non-token items are ignored.
public sealed class TokenTeleportReaderTests : IDisposable
{
    private readonly string _root;

    public TokenTeleportReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-token-reader-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string ItemsJson = """
        [
          { "Number": 3381, "Name": "token of Silvermere", "ItemType": 10, "Abil-0": 43, "AbilVal-0": 5054 },
          { "Number": 3387, "Name": "token of the Lost City", "ItemType": 10, "Abil-0": 43, "AbilVal-0": 5057 },
          { "Number": 999, "Name": "a healing potion", "ItemType": 2 }
        ]
        """;

    private const string SpellsJson = """
        [
          { "Number": 5054, "Name": "Silvermere token", "Abil-0": 148, "AbilVal-0": 6054 },
          { "Number": 5057, "Name": "Lost City token", "Abil-0": 148, "AbilVal-0": 6057 }
        ]
        """;

    private const string TBInfoJson = """
        [
          { "Number": 6054, "Action": "nomonsters a:minlevel 25 b:price 200000 c:cast 310:teleport 1813 1:message d\n" },
          { "Number": 6057, "Action": "nomonsters a:minlevel 40 b:price 400000 c:cast 310:teleport 426 16:message d\n" }
        ]
        """;

    private GameDataCache NewCache()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), ItemsJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "Spells.json"), SpellsJson);
        File.WriteAllText(Path.Combine(_root, "alpha", "TBInfo.json"), TBInfoJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        return cache;
    }

    [Fact]
    public void ReadAll_ResolvesTokenTeleports()
    {
        var map = TokenTeleportReader.ReadAll(NewCache());
        Assert.Equal(2, map.Count);

        TokenTeleportInfo silver = map["silvermere"];
        Assert.Equal(3381, silver.ItemNumber);
        Assert.Equal(new RoomKey(1, 1813), silver.Destination);
        Assert.Equal(200000, silver.CostCopper);
        Assert.Equal(25, silver.MinLevel);

        // "token of the Lost City" — the arrival place carries "the", so it keys under
        // "lost city" (NormalizePlace drops the leading article).
        TokenTeleportInfo lost = map["lost city"];
        Assert.Equal(new RoomKey(16, 426), lost.Destination);
        Assert.Equal(400000, lost.CostCopper);
        Assert.Equal(40, lost.MinLevel);
    }

    [Fact]
    public void ReadAll_IgnoresNonTokenItems()
    {
        var map = TokenTeleportReader.ReadAll(NewCache());
        Assert.DoesNotContain(map.Values, t => t.ItemNumber == 999);
    }
}

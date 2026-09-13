using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The "Likely source" hint on the Unrecognized Lines tab used to list every spell every
// monster in the room could cast, so a real export answered "bites (#80)" — a forest
// spider's on-hit proc — identically for all 39 captured lines, including one about a
// twig snapping. These pin that the hint is now derived from the line itself.
public sealed class RoomSpellAttributorTests : IDisposable
{
    private readonly string _root;
    private const string SetName = "set";

    public RoomSpellAttributorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-attrib-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Room 1 mirrors Darkwood: a room spell of its own plus a lair holding the spider
    // and the archer. Room 2 has the same lair but no room spell.
    private const string Rooms = """
        [
          { "Map Number": 1, "Room Number": 1, "Name": "Darkwood Forest",
            "Spell": 915, "Lair": "(Max 3): 48,51,52" },
          { "Map Number": 1, "Room Number": 2, "Name": "Darkwood Forest",
            "Spell": 0, "Lair": "(Max 3): 48,51,52" }
        ]
        """;

    private const string Spells = """
        [
          { "Number": 80,  "Name": "bites" },
          { "Number": 915, "Name": "darkwood forest spell" },
          { "Number": 200, "Name": "web" }
        ]
        """;

    // Forest spider's slot 1 is a physical attack carrying an on-hit proc (#80) — the
    // real shape behind the reported hint — plus a between-rounds spell. The archer
    // casts nothing. "goblin" exists only to prove the longest name wins.
    private const string Monsters = """
        [
          { "Number": 48, "Name": "dark goblin archer",
            "AttType-0": 1, "Att%-0": 100, "AttAcc-0": 40, "AttHitSpell-0": 0 },
          { "Number": 51, "Name": "forest spider",
            "AttType-0": 1, "Att%-0": 95,  "AttAcc-0": 40, "AttHitSpell-0": 0,
            "AttType-1": 1, "Att%-1": 100, "AttAcc-1": 50, "AttHitSpell-1": 80,
            "MidSpell-0": 200, "MidSpell%-0": 25 },
          { "Number": 52, "Name": "goblin",
            "AttType-0": 1, "Att%-0": 100, "AttAcc-0": 30, "AttHitSpell-0": 0 }
        ]
        """;

    private (RoomGraphManager Graph, GameDataCache Cache, MonsterCatalog Monsters) NewSet()
    {
        string setRoot = Path.Combine(_root, SetName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), Rooms);
        File.WriteAllText(Path.Combine(setRoot, "Spells.json"), Spells);
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), Monsters);
        GameDataCache cache = new(_root);
        cache.SwitchSet(SetName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(SetName);
        return (graph, cache, new MonsterCatalog(cache));
    }

    private string Source(int room, string? line)
    {
        var (graph, cache, monsters) = NewSet();
        return RoomSpellAttributor.LikelySource(
            new RoomKey(1, room), line, graph, cache,
            spawns: null, monsters: monsters,
            spells: new Game.Spells.KnownSpellCatalog(cache));
    }

    [Fact]
    public void AmbienceLine_AttributesTheRoomsOwnSpell()
    {
        // The atmosphere lines that read like scenery are the room's on-entry spell —
        // the whole reason this column is worth having.
        string text = Source(1, "An ominous wind blows through the trees");

        Assert.Contains("darkwood forest spell (#915)", text);
        Assert.Contains("room spell", text);
        // The spider's proc has nothing to do with this line.
        Assert.DoesNotContain("bites", text);
    }

    [Fact]
    public void LineNamingAMonster_AttributesThatMonsterOnly()
    {
        string text = Source(1, "The forest spider's fangs sink in!");

        Assert.Contains("bites (#80)", text);
        Assert.Contains("forest spider, on hit", text);
        Assert.Contains("web (#200)", text);
        Assert.Contains("between rounds", text);
        // The line said who acted, so the room's ambient spell isn't the source.
        Assert.DoesNotContain("room spell", text);
    }

    [Fact]
    public void LineNamingANonCastingMonster_FallsBackToTheRoomSpell()
    {
        // The archer casts nothing, so naming it yields no monster attribution and the
        // room's own spell is the only remaining candidate.
        string text = Source(1, "The dark goblin archer looses a shaft!");

        Assert.Contains("darkwood forest spell (#915)", text);
        Assert.DoesNotContain("bites", text);
    }

    [Fact]
    public void LongestMonsterNameWins()
    {
        // Both "goblin" and "dark goblin archer" are in the room and both appear in the
        // line; the specific one must be chosen. Neither casts, so the observable proof
        // is that the spider's spells are not attributed.
        string text = Source(1, "The dark goblin archer shouts to the goblin!");

        Assert.DoesNotContain("bites", text);
        Assert.Contains("room spell", text);
    }

    [Fact]
    public void NoRoomSpellAndNoMonsterNamed_GivesNoHint()
    {
        // Nothing ties the line to a spell. Blank is the honest answer — a hint every
        // row shares is what made this column useless.
        Assert.Equal(string.Empty, Source(2, "A dry twig snaps loudly behind you."));
    }

    [Fact]
    public void NoRoomSpell_ButMonsterNamed_StillAttributes()
    {
        string text = Source(2, "The forest spider's fangs sink in!");

        Assert.Contains("bites (#80)", text);
        Assert.Contains("forest spider, on hit", text);
    }

    [Fact]
    public void UnknownRoom_GivesNoHint()
        => Assert.Equal(string.Empty, Source(999, "The forest spider's fangs sink in!"));

    [Fact]
    public void BlankLine_FallsBackToTheRoomSpell()
    {
        // No text means no monster can be named, but the room's spell still stands.
        string text = Source(1, null);

        Assert.Contains("darkwood forest spell (#915)", text);
    }

    [Fact]
    public void NeverFiringAttackSlot_IsNotAttributed()
    {
        // A slot with no chance to land can't have produced anything.
        string setRoot = Path.Combine(_root, SetName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), Rooms);
        File.WriteAllText(Path.Combine(setRoot, "Spells.json"), Spells);
        File.WriteAllText(Path.Combine(setRoot, "Monsters.json"), """
            [
              { "Number": 51, "Name": "forest spider",
                "AttType-0": 1, "Att%-0": 0, "AttAcc-0": 50, "AttHitSpell-0": 80 }
            ]
            """);
        GameDataCache cache = new(_root);
        cache.SwitchSet(SetName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(SetName);

        string text = RoomSpellAttributor.LikelySource(
            new RoomKey(1, 2), "The forest spider's fangs sink in!", graph, cache,
            spawns: null, monsters: new MonsterCatalog(cache),
            spells: new Game.Spells.KnownSpellCatalog(cache));

        Assert.Equal(string.Empty, text);
    }
}

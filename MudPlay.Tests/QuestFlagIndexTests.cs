using System.IO;
using System.Linq;
using MudPlay.Game.Quests;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins QuestFlagIndex — the TBInfo quest-flag scanner behind the Game Data Browser's Quest
// Flags table. Verifies each relationship verb (give/add/check/test/fail/removeability) maps to
// its relation, source attribution via Called-From (monster / room / spell roots, a
// textblock→monster chain resolved two hops up, and an orphan block falling back to itself),
// id→name resolution, flag labelling, and self-invalidation on a set swap.
public sealed class QuestFlagIndexTests : IDisposable
{
    private readonly string _root;

    public QuestFlagIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-questflag-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string MonstersJson = """
        [
          { "Number": 61, "Name": "Gnome Commander" },
          { "Number": 62, "Name": "Oracle" }
        ]
        """;

    private const string RoomsJson = """
        [
          { "Map Number": 7, "Room Number": 1008, "Name": "Shrine" }
        ]
        """;

    private const string SpellsJson = """
        [
          { "Number": 559, "Name": "Reveal" }
        ]
        """;

    // 100 — Gnome Commander grants + advances flag 200 (Mandos Quest).
    // 210 — Shrine room CMD requires flag 200.
    // 220 — the Reveal spell tests flag 200.
    // 230 — the Oracle gates on (failability) and clears (removeability) flag 200.
    // 240/241 — textblock chain: giveability 201, called by TB 241, rooted at Monster 61.
    // 250 — orphan block (no Called-From): grants 202, attributed to itself as a textblock.
    private const string TBInfoJson = """
        [
          { "Number": 100, "Action": "ask good:giveability 200 1\naddability 200 2\n", "Called From": "Monster #61" },
          { "Number": 210, "Action": "touch:checkability 200 1\n", "Called From": "Room 7/1008" },
          { "Number": 220, "Action": "cast:testability 200 3\n", "Called From": "Spell #559" },
          { "Number": 230, "Action": "failability 200 1:removeability 200\n", "Called From": "Monster #62" },
          { "Number": 240, "Action": "giveability 201 1\n", "Called From": "Textblock #241" },
          { "Number": 241, "Action": "text 240\n", "Called From": "Monster #61" },
          { "Number": 250, "Action": "giveability 202 1\n", "Called From": "" }
        ]
        """;

    private GameDataCache NewCache(string setName = "alpha", bool withTbInfo = true)
    {
        string dir = Path.Combine(_root, setName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), MonstersJson);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), RoomsJson);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), SpellsJson);
        if (withTbInfo) File.WriteAllText(Path.Combine(dir, "TBInfo.json"), TBInfoJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet(setName);
        return cache;
    }

    private static QuestFlagRef Find(QuestFlagIndex index, int flag, QuestFlagRelation rel) =>
        index.Entries.Single(e => e.Flag == flag && e.Relation == rel);

    [Fact]
    public void Grants_FromMonster_NamesTheNpcAndFlag()
    {
        QuestFlagIndex index = new(NewCache());

        QuestFlagRef r = Find(index, 200, QuestFlagRelation.Grants);
        Assert.Equal(QuestFlagSourceKind.Monster, r.SourceKind);
        Assert.Equal(61, r.SourceNumber);
        Assert.Equal("Gnome Commander", r.SourceName);
        Assert.Equal("Mandos Quest", r.FlagName);
        Assert.Equal(1, r.Value);
    }

    [Fact]
    public void Advances_CarriesTheDelta()
    {
        QuestFlagRef r = Find(new QuestFlagIndex(NewCache()), 200, QuestFlagRelation.Advances);
        Assert.Equal(2, r.Value);
        Assert.Equal(61, r.SourceNumber);
    }

    [Fact]
    public void Requires_FromRoom_ResolvesRoomName()
    {
        QuestFlagRef r = Find(new QuestFlagIndex(NewCache()), 200, QuestFlagRelation.Requires);
        Assert.Equal(QuestFlagSourceKind.Room, r.SourceKind);
        Assert.Equal(7, r.Map);
        Assert.Equal(1008, r.Room);
        Assert.Equal("Shrine", r.SourceName);
    }

    [Fact]
    public void Tests_FromSpell_ResolvesSpellName()
    {
        QuestFlagRef r = Find(new QuestFlagIndex(NewCache()), 200, QuestFlagRelation.Tests);
        Assert.Equal(QuestFlagSourceKind.Spell, r.SourceKind);
        Assert.Equal(559, r.SourceNumber);
        Assert.Equal("Reveal", r.SourceName);
    }

    [Fact]
    public void GateAndClears_BothSurface_FromTheSameBlock()
    {
        QuestFlagIndex index = new(NewCache());
        Assert.Equal("Oracle", Find(index, 200, QuestFlagRelation.Gate).SourceName);
        Assert.Equal("Oracle", Find(index, 200, QuestFlagRelation.Clears).SourceName);
    }

    [Fact]
    public void TextblockChain_RootsAtMonsterTwoHopsUp()
    {
        QuestFlagRef r = Find(new QuestFlagIndex(NewCache()), 201, QuestFlagRelation.Grants);
        Assert.Equal(QuestFlagSourceKind.Monster, r.SourceKind);
        Assert.Equal(61, r.SourceNumber);
        Assert.Equal("Volums Quest", r.FlagName);
    }

    [Fact]
    public void OrphanBlock_FallsBackToItselfAsTextblock()
    {
        QuestFlagRef r = Find(new QuestFlagIndex(NewCache()), 202, QuestFlagRelation.Grants);
        Assert.Equal(QuestFlagSourceKind.Textblock, r.SourceKind);
        Assert.Equal(250, r.SourceNumber);
    }

    [Fact]
    public void Entries_AreSortedByFlag()
    {
        var flags = new QuestFlagIndex(NewCache()).Entries.Select(e => e.Flag).ToList();
        Assert.Equal(flags.OrderBy(f => f), flags);
    }

    [Fact]
    public void SetSwap_ToSetWithoutTbInfo_ClearsEntries()
    {
        GameDataCache cache = NewCache();
        QuestFlagIndex index = new(cache);
        Assert.NotEmpty(index.Entries);

        // Swap to a set with no TBInfo — the lazy index self-invalidates on ActiveSet.
        string bare = Path.Combine(_root, "bravo");
        Directory.CreateDirectory(bare);
        File.WriteAllText(Path.Combine(bare, "Monsters.json"), MonstersJson);
        cache.SwitchSet("bravo");

        Assert.Empty(index.Entries);
    }
}

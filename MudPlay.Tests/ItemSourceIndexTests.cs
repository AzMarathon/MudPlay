using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins ItemSourceIndex's two reverse-acquisition paths:
//   * Containers — invert ChestContentsReader so an item lists the chests it
//     drops from (item 10 ← Wooden Chest), and a chest-loot giveitem does NOT
//     also masquerade as a "given by" (its Called-From roots at a Spell).
//   * Givers — a TBInfo `giveitem` attributed via Called-From to its monster /
//     room root, with the requirement gate read off the award line:
//       - takeitem  → "turn in <item>"      (Monster #300 turn-in)
//       - price     → "purchase"            (Monster #301 merchant give)
//       - giveability → "quest reward"      (Room 3/606 dragon-statue reward)
//     and a textblock→textblock chain that roots at a monster two hops up.
//   * Self-invalidation on a set swap (the index is lazy, no eviction).
public sealed class ItemSourceIndexTests : IDisposable
{
    private readonly string _root;

    public ItemSourceIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-itemsrc-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string ItemsJson = """
        [
          { "Number": 100, "Name": "Wooden Chest", "ItemType": 8, "Abil-0": 43, "AbilVal-0": 200 },
          { "Number": 10, "Name": "Gold Ring", "ItemType": 2 },
          { "Number": 20, "Name": "Spider Silk", "ItemType": 2 },
          { "Number": 21, "Name": "Dragon Key", "ItemType": 2 },
          { "Number": 22, "Name": "Fang Blade", "ItemType": 1 },
          { "Number": 23, "Name": "Health Potion", "ItemType": 2 },
          { "Number": 24, "Name": "Dragon Hide Vest", "ItemType": 0 },
          { "Number": 25, "Name": "Bloodstone Orb", "ItemType": 2 },
          { "Number": 26, "Name": "Gate Key", "ItemType": 7 },
          { "Number": 27, "Name": "Black Star Key", "ItemType": 7 },
          { "Number": 28, "Name": "Stone Signet", "ItemType": 2 }
        ]
        """;

    private const string SpellsJson = """
        [
          { "Number": 200, "Name": "Open Wooden Chest", "Abil-0": 148, "AbilVal-0": 500 }
        ]
        """;

    private const string MonstersJson = """
        [
          { "Number": 300, "Name": "Martok" },
          { "Number": 301, "Name": "Gnome Merchant" },
          { "Number": 302, "Name": "Dragon Lord" },
          { "Number": 303, "Name": "Gnome Commander", "Summoned By": "Room 5/512, Room 5/513" },
          { "Number": 347, "Name": "obsidian statue", "Summoned By": "Textblock #863",
            "DropItem-0": 26, "DropItem%-0": 100 },
          { "Number": 348, "Name": "sandstone sphinx", "Summoned By": "Textblock #864",
            "DropItem-0": 28, "DropItem%-0": 50 },
          { "Number": 29, "Name": "dark cultist",
            "Summoned By": "Group: 1/1127,[8-2-2][2]Group(lair): 1/1128",
            "DropItem-0": 27, "DropItem%-0": 10 }
        ]
        """;

    private const string RoomsJson = """
        [
          { "Map Number": 3, "Room Number": 606, "Name": "Dragon Statue", "CMD": 0 },
          { "Map Number": 8, "Room Number": 461, "Name": "Black Steel Gate", "CMD": 863 },
          { "Map Number": 12, "Room Number": 2442, "Name": "Sphinx Chamber", "CMD": 864 }
        ]
        """;

    // 500 — chest loot (Spell-rooted → never a giver).
    // 610 — Martok turn-in: takeitem 20 → giveitem 22 (single-block keyword "give blade").
    // 620 — dragon statue quest reward: giveitem 21 + giveability (room CMD "insert fang").
    // 630 — gnome merchant purchase: price → giveitem 23.
    // 640/641 — textblock chain: giveitem 24, called by TB 641, which is called
    //           by Monster 302 (deterministic, bare-greeting → empty keyword).
    // 700/701 — multi-block menu: Gnome Commander's greeting routes "orb" to a
    //           sub-block that unconditionally gives item 25 (deterministic,
    //           keyword read off the parent menu).
    private const string TBInfoJson = """
        [
          { "Number": 500, "LinkTo": 0, "Action": "giveitem 10\n", "Called From": "Spell #200" },
          { "Number": 610, "LinkTo": 0, "Action": "give blade:takeitem 20 999:giveitem 22\n", "Called From": "Monster #300" },
          { "Number": 620, "LinkTo": 0, "Action": "insert fang:checkability 126 4:giveitem 21:giveability 126 5\n", "Called From": "Room 3/606" },
          { "Number": 630, "LinkTo": 0, "Action": "buy potion:price 5000 999:giveitem 23\n", "Called From": "Monster #301" },
          { "Number": 640, "LinkTo": 0, "Action": "giveitem 24\n", "Called From": "Textblock #641" },
          { "Number": 641, "LinkTo": 0, "Action": "text 640\n", "Called From": "Monster #302" },
          { "Number": 700, "LinkTo": 0, "Action": "orb:701\n", "Called From": "Monster #303" },
          { "Number": 701, "LinkTo": 0, "Action": "giveitem 25\n", "Called From": "Textblock #700" },
          { "Number": 863, "LinkTo": 0, "Action": "touch statue:summon 347\nmove statue:summon 347\n", "Called From": "Room 8/461" },
          { "Number": 864, "LinkTo": 0, "Action": "touch sphinx:summon 348\n", "Called From": "Room 12/2442" }
        ]
        """;

    private GameDataCache NewCache(string setName = "alpha")
    {
        string dir = Path.Combine(_root, setName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), ItemsJson);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), SpellsJson);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), MonstersJson);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), RoomsJson);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), TBInfoJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet(setName);
        return cache;
    }

    private static ItemSourceIndex NewIndex(GameDataCache cache)
    {
        // Wire TBInfoStore to the cache exactly as AppServices does so a set swap
        // reloads its entries — the index reads the store's typed entries and
        // must see them clear when the new set has no TBInfo.
        TBInfoStore tb = new(cache);
        cache.ActiveSetChanged += tb.OnActiveSetChanged;
        tb.OnActiveSetChanged(cache.ActiveSet);
        return new ItemSourceIndex(cache, tb);
    }

    [Fact]
    public void ContainersOf_LootedItem_ListsHostChest()
    {
        ItemSourceIndex index = NewIndex(NewCache());

        var containers = index.ContainersOf(10);
        ItemSource src = Assert.Single(containers);
        Assert.Equal(100, src.ContainerItemId);
        Assert.Equal("Wooden Chest", src.ContainerName);
        Assert.Equal(1.0, src.Probability, 3);
    }

    [Fact]
    public void GiversOf_ChestLootItem_IsEmpty()
    {
        // Item 10 is chest loot only — its giveitem block roots at a Spell, which
        // is not a walkable giver (the Found-in path covers it instead).
        ItemSourceIndex index = NewIndex(NewCache());
        Assert.Empty(index.GiversOf(10));
    }

    [Fact]
    public void GiversOf_MonsterTurnIn_NamesRequiredItem()
    {
        ItemSourceIndex index = NewIndex(NewCache());

        ItemGiver giver = Assert.Single(index.GiversOf(22));
        Assert.Equal(ItemGiverKind.Monster, giver.Kind);
        Assert.Equal(300, giver.Number);
        Assert.Equal("Martok", giver.Name);
        Assert.Equal("turn in Spider Silk", giver.Requirement);
    }

    [Fact]
    public void GiversOf_RoomQuestReward_AttributesToRoom()
    {
        ItemSourceIndex index = NewIndex(NewCache());

        ItemGiver giver = Assert.Single(index.GiversOf(21));
        Assert.Equal(ItemGiverKind.Room, giver.Kind);
        Assert.Equal(3, giver.Map);
        Assert.Equal(606, giver.Room);
        Assert.Equal("Dragon Statue", giver.Name);
        Assert.Equal("quest reward", giver.Requirement);
    }

    [Fact]
    public void GiversOf_MerchantPrice_ReadsPurchase()
    {
        ItemSourceIndex index = NewIndex(NewCache());

        ItemGiver giver = Assert.Single(index.GiversOf(23));
        Assert.Equal(ItemGiverKind.Monster, giver.Kind);
        Assert.Equal("Gnome Merchant", giver.Name);
        Assert.Equal("purchase", giver.Requirement);
    }

    [Fact]
    public void GiversOf_TextblockChain_RootsAtMonster()
    {
        ItemSourceIndex index = NewIndex(NewCache());

        ItemGiver giver = Assert.Single(index.GiversOf(24));
        Assert.Equal(ItemGiverKind.Monster, giver.Kind);
        Assert.Equal(302, giver.Number);
        Assert.Equal("Dragon Lord", giver.Name);
        Assert.Equal(string.Empty, giver.Requirement);
    }

    [Fact]
    public void GiversOf_MultiBlockMenu_CarriesMenuKeywordAndIsDeterministic()
    {
        // Gnome Commander's greeting menu keys "orb" to a sub-block that gives
        // item 25 with no turn-in / price / random — the keyword is read off the
        // parent menu, and the unconditional hand-over is deterministic.
        ItemSourceIndex index = NewIndex(NewCache());

        ItemGiver giver = Assert.Single(index.GiversOf(25));
        Assert.Equal(ItemGiverKind.Monster, giver.Kind);
        Assert.Equal(303, giver.Number);
        Assert.Equal("Gnome Commander", giver.Name);
        Assert.Equal("orb", giver.Keyword);
        Assert.True(giver.Deterministic);
        Assert.Equal(string.Empty, giver.Requirement);
    }

    [Fact]
    public void GiversOf_SingleBlockGive_CarriesLeadingTokenKeyword()
    {
        // Martok's "give blade:takeitem 20:giveitem 22" supplies its own trigger
        // as the award line's leading token; the turn-in gate makes it non-det.
        ItemSourceIndex index = NewIndex(NewCache());

        ItemGiver giver = Assert.Single(index.GiversOf(22));
        Assert.Equal("give blade", giver.Keyword);
        Assert.False(giver.Deterministic);
    }

    [Fact]
    public void GiversOf_RoomCmd_CarriesVerbatimKeyword()
    {
        // A room CMD's keyword is the verbatim command typed in the room —
        // "insert fang" here; the giveability gate makes it non-deterministic.
        ItemSourceIndex index = NewIndex(NewCache());

        ItemGiver giver = Assert.Single(index.GiversOf(21));
        Assert.Equal(ItemGiverKind.Room, giver.Kind);
        Assert.Equal("insert fang", giver.Keyword);
        Assert.False(giver.Deterministic);
    }

    [Fact]
    public void GiversOf_BareGreetingChain_DeterministicWithEmptyKeyword()
    {
        // The 640/641 chain gives item 24 through LinkTo continuations with no
        // menu key — deterministic hand-over, but nothing to ask for.
        ItemSourceIndex index = NewIndex(NewCache());

        ItemGiver giver = Assert.Single(index.GiversOf(24));
        Assert.True(giver.Deterministic);
        Assert.Equal(string.Empty, giver.Keyword);
    }

    [Fact]
    public void GiversOf_MerchantPurchase_IsNotDeterministic()
    {
        // A priced give still costs cash — never an unconditional hand-over.
        ItemSourceIndex index = NewIndex(NewCache());
        Assert.False(Assert.Single(index.GiversOf(23)).Deterministic);
    }

    [Fact]
    public void GiverMonsterRoomsOf_ResolvesSpawnRoomsFromSummonedBy()
    {
        // The give router needs a concrete room for a Monster giver — resolved
        // off Monsters.json "Summoned By" (item 25's giver, the Gnome Commander).
        ItemSourceIndex index = NewIndex(NewCache());

        var rooms = index.GiverMonsterRoomsOf(303);
        Assert.Equal(2, rooms.Count);
        Assert.Contains(new RoomKey(5, 512), rooms);
        Assert.Contains(new RoomKey(5, 513), rooms);
    }

    [Fact]
    public void GiverMonsterRoomsOf_NonGiverMonster_IsEmpty()
    {
        // Only monsters that actually give an item get their spawn rooms indexed;
        // Martok gives (turn-in) but has no Summoned By, so it resolves to none.
        ItemSourceIndex index = NewIndex(NewCache());
        Assert.Empty(index.GiverMonsterRoomsOf(300));
    }

    [Fact]
    public void SwitchingSets_RebuildsFromNewSet()
    {
        GameDataCache cache = NewCache();
        ItemSourceIndex index = NewIndex(cache);
        Assert.NotEmpty(index.GiversOf(22));   // populate against 'alpha'

        // A set with no game-data tables clears both maps on next query.
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        cache.SwitchSet("empty");
        Assert.Empty(index.GiversOf(22));
        Assert.Empty(index.ContainersOf(10));
    }

    // ----- Summon drops -------------------------------------------------

    // The reported case: `touch statue` in 8/461 summons the obsidian statue, which
    // drops the gate key at 100% — the whole chain is deterministic, so it's
    // routable (report paradigm-20260911-010954).
    [Fact]
    public void SummonDrops_IndexesGuaranteedDropFromRoomCommand()
    {
        ItemSourceIndex index = NewIndex(NewCache());

        SummonDropSource src = Assert.Single(index.SummonDropsOf(26));
        Assert.Equal(347, src.MonsterId);
        Assert.Equal("obsidian statue", src.MonsterName);
        Assert.Equal(8, src.Map);
        Assert.Equal(461, src.Room);
        Assert.Equal("touch statue", src.Command);
        Assert.Equal(100, src.DropPercent);
    }

    // The control the whole gate exists for: a low-drop lair dropper is NOT
    // routable, so the black-star-key shape must never reach the summon index.
    [Fact]
    public void SummonDrops_ExcludesLowDropLairMonster()
        => Assert.Empty(NewIndex(NewCache()).SummonDropsOf(27));

    // A room command DOES summon this one, but its drop is a 50% roll — the spawn
    // being deterministic isn't enough on its own.
    [Fact]
    public void SummonDrops_ExcludesNonGuaranteedDropFromRoomCommand()
        => Assert.Empty(NewIndex(NewCache()).SummonDropsOf(28));

    // Synonyms conjure the same monster in the same room; one routable source is
    // enough, so "move statue" doesn't produce a duplicate row.
    [Fact]
    public void SummonDrops_CollapsesSynonymCommands()
        => Assert.Single(NewIndex(NewCache()).SummonDropsOf(26));

    [Fact]
    public void SummonDrops_ClearedOnSetChange()
    {
        GameDataCache cache = NewCache();
        ItemSourceIndex index = NewIndex(cache);
        Assert.NotEmpty(index.SummonDropsOf(26));

        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        cache.SwitchSet("empty");
        Assert.Empty(index.SummonDropsOf(26));
    }
    // ----- concurrent warm-up ----------------------------------------
    //
    // The index is warmed on a worker thread so its ~600 ms first build lands while
    // the set loads rather than inside the first walk that crosses a gate. That is
    // only safe because a build touches nothing shared until it publishes a finished
    // snapshot by one reference assignment.

    [Fact]
    public void Warm_ThenQuery_ReturnsTheSameBuild()
    {
        ItemSourceIndex index = NewIndex(NewCache());
        index.Warm();

        Assert.NotEmpty(index.GiversOf(22));
        Assert.NotEmpty(index.SummonDropsOf(26));
    }

    [Fact]
    public void ConcurrentReadersAndWarms_NeverSeeAPartialBuild()
    {
        ItemSourceIndex index = NewIndex(NewCache());
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        System.Threading.Tasks.Parallel.For(0, 64, i =>
        {
            try
            {
                if (i % 4 == 0) index.Warm();
                // Every reader must see a COMPLETE build — a half-filled map would
                // show up as a giver or summon source going missing.
                if (index.GiversOf(22).Count == 0) failures.Add("givers empty");
                if (index.SummonDropsOf(26).Count == 0) failures.Add("summon drops empty");
                if (index.ContainersOf(10).Count == 0) failures.Add("containers empty");
            }
            catch (Exception ex)
            {
                failures.Add(ex.GetType().Name);
            }
        });

        Assert.Empty(failures);
    }

    // A warm-up is an optimisation, never a crash vector: with no readable set it
    // publishes an empty build instead of throwing on the worker thread.
    [Fact]
    public void Warm_WithNoActiveSet_IsHarmless()
    {
        GameDataCache cache = NewCache();
        ItemSourceIndex index = NewIndex(cache);
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        cache.SwitchSet("empty");

        index.Warm();

        Assert.Empty(index.GiversOf(22));
        Assert.Empty(index.SummonDropsOf(26));
    }

}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.CharacterWorkshop;
using Xunit;

namespace MudPlay.Tests;

// The drafted quest checklists name where to fetch what a step turns in (report
// paradigm-20261011-230844: "lay out how to complete identified quests ... similar to the
// quest guides"). Flag 208 has the shape of Paradigm's Conquest1: the four hands come off
// monsters whose death hands one over while the flag is at 1, and the turn-in takes them.
// Flag 126 carries a lost-item reissue and a turn-in item only a monster's drop slots hold;
// flag 300 an item an NPC gives on a keyword, and a roll-table line.
public sealed class QuestItemGatheringTests : IDisposable
{
    private readonly string _root;

    public QuestItemGatheringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-questgather-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private const string ItemsJson = """
        [
          { "Number": 3643, "Name": "hill chieftan's hand" },
          { "Number": 3644, "Name": "stone chieftan's hand" },
          { "Number": 622, "Name": "locked wooden box" },
          { "Number": 684, "Name": "gleaming shard" },
          { "Number": 700, "Name": "orc-head" },
          { "Number": 701, "Name": "quest token" },
          { "Number": 702, "Name": "Darkbane" },
          { "Number": 703, "Name": "adamant ore" },
          { "Number": 931, "Name": "iron crown" }
        ]
        """;

    private const string MonstersJson = """
        [
          { "Number": 50, "Name": "hill giant chieftan", "DeathSpell": 5519 },
          { "Number": 51, "Name": "stone giant chieftain", "DeathSpell": 5521 },
          { "Number": 60, "Name": "renfry", "DeathSpell": 0 },
          { "Number": 61, "Name": "metallic monstrosity", "DeathSpell": 0, "DropItem-0": 684, "DropItem%-0": 25 },
          { "Number": 62, "Name": "aldreth", "DeathSpell": 0 },
          { "Number": 63, "Name": "Commander Markus", "DeathSpell": 0, "DropItem-0": 622, "DropItem%-0": 100 },
          { "Number": 64, "Name": "kobold king", "DeathSpell": 574, "DropItem-0": 931, "DropItem%-0": 100 }
        ]
        """;

    private const string SpellsJson = """
        [
          { "Number": 5519, "Name": "hill chieftan text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 5211 },
          { "Number": 5521, "Name": "stone chieftan text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 5212 },
          { "Number": 574, "Name": "kobold king text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 1400 }
        ]
        """;

    private const string TBInfoJson = """
        [
          { "Number": 5208, "Action": "task:5209\nhands:5215\n", "Called From": "Monster #60" },
          { "Number": 5209, "Action": "", "Called From": "Textblock #5208" },
          { "Number": 5215, "Action": "", "Called From": "Textblock #5208" },
          { "Number": 5222, "Action": "class 1:minlevel 45:failability 208:giveability 208 1:text 5210\n", "Called From": "Textblock #5209" },
          { "Number": 5211, "Action": "failitem 3643:testability 208 1:checkability 208 1:message 9267:giveitem 3643\n", "Called From": "Spell #5519" },
          { "Number": 5212, "Action": "failitem 3644:testability 208 1:checkability 208 1:message 9268:giveitem 3644\n", "Called From": "Spell #5521" },
          { "Number": 5216, "Action": "checkability 208 1:testability 208 1:checkitem 3643 9271:checkitem 3644 9272:takeitem 3643:takeitem 3644:giveability 208 2:addability 96 10:text 5217\n", "Called From": "Textblock #5215" },

          { "Number": 350, "Action": "bow:testability 126 4:checkability 126 4:giveitem 622:giveability 126 5\nbow:testability 126 5:checkability 126 5:failitem 622:giveitem 622\n", "Called From": "Room 2/2518" },
          { "Number": 353, "Action": "kneel:testability 126 5:checkability 126 5:checkitem 622 3208:takeitem 622:giveability 126 6\n", "Called From": "Room 1/1333" },
          { "Number": 354, "Action": "forge:checkability 126 6:takeitem 684:giveability 126 7\n", "Called From": "Room 6/1357" },

          { "Number": 900, "Action": "heads:910\n", "Called From": "Monster #62" },
          { "Number": 910, "Action": "takeitem 700:giveitem 701\n", "Called From": "Textblock #900" },
          { "Number": 920, "Action": "return:takeitem 701:giveability 300 1\n", "Called From": "Room 1/2305" },
          { "Number": 930, "Action": "100:giveitem 702:giveability 300 2\n", "Called From": "Room 15/183" },

          { "Number": 1300, "Action": "salute:takeitem 622:giveability 128 1\n", "Called From": "Room 2/2577" },

          { "Number": 1390, "Action": "kneel:failability 129:giveability 129 1\n", "Called From": "Room 6/833" },
          { "Number": 1400, "Action": "checkability 129 1:testability 129 1:giveability 129 2\n", "Called From": "Spell #574" },
          { "Number": 1410, "Action": "crown:checkability 129 2:takeitem 931:giveability 129 3\n", "Called From": "Room 1/723" },

          { "Number": 4999, "Action": "mine vein:nomonsters:random 5000\n", "Called From": "Room 14/7127" },
          { "Number": 5000, "Action": "100:giveitem 703\n", "Called From": "Textblock(rndm) #4999" },
          { "Number": 5010, "Action": "forge ore:takeitem 703:giveability 301 1\n", "Called From": "Room 14/7225" }
        ]
        """;

    private GameDataCache NewCache()
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Items.json"), ItemsJson);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), MonstersJson);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), SpellsJson);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), TBInfoJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        return cache;
    }

    private static (ItemSourceIndex Givers, MonsterDropIndex Drops) Indexes(GameDataCache cache)
    {
        TBInfoStore tb = new(cache);
        tb.OnActiveSetChanged(cache.ActiveSet);
        MonsterDropIndex drops = new(cache);
        drops.OnActiveSetChanged(cache.ActiveSet);
        return (new ItemSourceIndex(cache, tb), drops);
    }

    private static readonly Dictionary<int, IReadOnlyList<RoomKey>> Placement = new()
    {
        [50] = new[] { new RoomKey(16, 801) },
        [51] = new[] { new RoomKey(16, 1095) },
        [60] = new[] { new RoomKey(14, 247) },
        [61] = new[] { new RoomKey(6, 76) },
        [62] = new[] { new RoomKey(1, 2305) },
        [63] = new[] { new RoomKey(2, 2518) },
        [64] = new[] { new RoomKey(6, 834) },
    };

    private static CrawledQuest Quest(GameDataCache cache, int flag) =>
        QuestCrawler.Crawl(cache, null).First(q => q.Flag == flag);

    [Fact]
    public void FlagGatedDeathGives_AreKillLines_AheadOfTheTurnInThatTakesThem()
    {
        GameDataCache cache = NewCache();

        Assert.Equal(new[]
        {
            "[] (14/247) `ask renfry task`",
            "[] (16/801) kill hill giant chieftan (hill chieftan's hand)",
            "[] (16/1095) kill stone giant chieftain (stone chieftan's hand)",
            "[] (14/247) `ask renfry hands` (turn in hill chieftan's hand, stone chieftan's hand)",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 208), Placement));
    }

    [Fact]
    public void LostItemReissue_IsLeftOut_AndADroppedItemNamesItsDropper()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);

        // The second `bow` line hands the box back to one who lost it; the first `bow`
        // already gave it, so it isn't a step. Nothing in the quest yields the shard: the
        // monster whose drop slot holds it is the fetch.
        Assert.Equal(new[]
        {
            "[] (2/2518) `bow` (get locked wooden box)",
            "[] (1/1333) `kneel` (turn in locked wooden box)",
            "[] (6/76) kill metallic monstrosity (gleaming shard)",
            "[] (6/1357) `forge` (turn in gleaming shard)",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 126), Placement, givers, drops));
    }

    [Fact]
    public void ItemAnNpcGives_IsAnAskLine_WithWhatItTakes_AndARollLineIsNotTyped()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);

        Assert.Equal(new[]
        {
            "[] (1/2305) `ask aldreth heads` (get quest token) (turn in orc-head)",
            "[] (1/2305) `return` (turn in quest token)",
            "[] (15/183) obtain Darkbane",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 300), Placement, givers, drops));
    }

    [Fact]
    public void TicksFromBeforeTheGatherLines_StayOnTheirSteps()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);
        // Ticked against the old draft of flag 208: "ask renfry task" (0) and "ask renfry
        // hands" (1). Today the two kill lines sit between them.
        CharacterProfile profile = new()
        {
            QuestLog = new List<QuestProgress> { new(208, 0) { CheckedSteps = new List<int> { 0, 1 } } },
        };

        Assert.True(QuestTickMigration.Apply(profile, cache, (f, s) => new QuestDefinition(f, s), out int moved, givers, drops));

        Assert.Equal(1, moved);
        Assert.Equal(new[] { 0, 3 }, profile.QuestLog[0].CheckedSteps);
        Assert.Equal(QuestTickMigration.CurrentDraftRevision, profile.QuestDraftRevision);
        Assert.False(QuestTickMigration.Apply(profile, cache, (f, s) => new QuestDefinition(f, s), out _, givers, drops));
    }

    [Fact]
    public void AnotherQuestsGatedGive_LosesToASureDrop()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);

        // Stock's Evil 2 shape: the box is also handed out by the Good quest's own `bow`
        // line, which an evil character is refused; Commander Markus always drops it.
        Assert.Equal(new[]
        {
            "[] (2/2518) kill Commander Markus (locked wooden box)",
            "[] (2/2577) `salute` (turn in locked wooden box)",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 128), Placement, givers, drops));
    }

    [Fact]
    public void DropOffAMonsterTheQuestKillsAnyway_IsNotedOnThatKill()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);

        Assert.Equal(new[]
        {
            "[] (6/833) `kneel`",
            "[] (6/834) kill kobold king (iron crown)",
            "[] (1/723) `crown` (turn in iron crown)",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 129), Placement, givers, drops));
    }

    [Fact]
    public void RoomRollTableGive_IsWhereToGo_NotANumberToType()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, MonsterDropIndex drops) = Indexes(cache);

        // `mine vein` draws from table 5000, whose line leads with its roll band.
        Assert.DoesNotContain(givers.GiversOf(703), g => g.Keyword == "100");
        Assert.Equal(new[]
        {
            "[] (14/7127) obtain adamant ore",
            "[] (14/7225) `forge ore` (turn in adamant ore)",
        }, QuestTextFormatter.StepLines(cache, Quest(cache, 301), Placement, givers, drops));
    }

    [Fact]
    public void DropIndexStillOnTheLastSet_LeavesTheTicksForLater()
    {
        GameDataCache cache = NewCache();
        (ItemSourceIndex givers, _) = Indexes(cache);
        MonsterDropIndex stale = new(cache);
        stale.OnActiveSetChanged("beta");
        CharacterProfile profile = new()
        {
            QuestLog = new List<QuestProgress> { new(208, 0) { CheckedSteps = new List<int> { 0, 1 } } },
        };

        Assert.False(QuestTickMigration.Apply(profile, cache, (f, s) => new QuestDefinition(f, s), out _, givers, stale));

        Assert.Equal(0, profile.QuestDraftRevision);
        Assert.Equal(new[] { 0, 1 }, profile.QuestLog[0].CheckedSteps);
    }
}

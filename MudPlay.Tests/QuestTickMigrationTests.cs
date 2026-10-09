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

// Pins the one-time move of saved quest-step ticks after the drafted checklists gained their
// kill lines: a tick stays on the step it was made on, quests whose draft did not change and
// checklists the user wrote are left alone, and it runs once.
public sealed class QuestTickMigrationTests : IDisposable
{
    private readonly string _root;

    public QuestTickMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-questtick-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // Flag 400 has the shape of Paradigm's third EvilQuest tier: a typed step, then a step
    // that happens on a monster's death and hands nothing over (no line in the old draft, a
    // kill line now), then two more typed steps. Two records of the monster run the kill
    // step; only the second is placed. Flag 401 is all typed steps: its draft is unchanged.
    private const string MonstersJson = """
        [
          { "Number": 11, "Name": "kobold king", "DeathSpell": 40 },
          { "Number": 14, "Name": "kobold king", "DeathSpell": 46 }
        ]
        """;

    private const string SpellsJson = """
        [
          { "Number": 40, "Name": "kobold king temp", "Targets": 12, "Abil-0": 151, "AbilVal-0": 41 },
          { "Number": 41, "Name": "kobold king text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 610 },
          { "Number": 46, "Name": "kobold king temp", "Targets": 12, "Abil-0": 151, "AbilVal-0": 47 },
          { "Number": 47, "Name": "kobold king text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 611 }
        ]
        """;

    private const string TBInfoJson = """
        [
          { "Number": 600, "Action": "bow:failability 400:giveability 400 1\n", "Called From": "Room 1/100" },
          { "Number": 610, "Action": "checkability 400 1:testability 400 1:giveability 400 2\n", "Called From": "Spell #41" },
          { "Number": 611, "Action": "checkability 400 1:testability 400 1:giveability 400 2\n", "Called From": "Spell #47" },
          { "Number": 620, "Action": "take crown:checkability 400 2:giveability 400 3\n", "Called From": "Room 1/101" },
          { "Number": 630, "Action": "kneel:checkability 400 3:giveability 400 4\n", "Called From": "Room 1/102" },
          { "Number": 700, "Action": "wave:failability 401:giveability 401 1\n", "Called From": "Room 1/100" },
          { "Number": 710, "Action": "sing:checkability 401 1:giveability 401 2\n", "Called From": "Room 1/101" }
        ]
        """;

    private GameDataCache NewCache(bool withData = true)
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        if (withData)
        {
            File.WriteAllText(Path.Combine(dir, "Monsters.json"), MonstersJson);
            File.WriteAllText(Path.Combine(dir, "Spells.json"), SpellsJson);
            File.WriteAllText(Path.Combine(dir, "TBInfo.json"), TBInfoJson);
        }
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        return cache;
    }

    private static CharacterProfile ProfileWith(params QuestProgress[] log) => new()
    {
        QuestTicksPredateKillSteps = true,
        QuestLog = log.ToList(),
    };

    private static QuestDefinition Drafted(int flag, int step) => new(flag, step);

    [Fact]
    public void Draft_NowHasTheKillLine_WithThePlacedRecordsRoom()
    {
        GameDataCache cache = NewCache();
        CrawledQuest quest = QuestCrawler.Crawl(cache, null).Single(q => q.Flag == 400);
        var placement = new Dictionary<int, IReadOnlyList<RoomKey>> { [14] = new[] { new RoomKey(6, 834) } };

        Assert.Equal(new[]
        {
            "[] (1/100) `bow`",
            "[] (6/834) kill kobold king",
            "[] (1/101) `take crown`",
            "[] (1/102) `kneel`",
        }, QuestTextFormatter.StepLines(cache, quest, placement));
        // The old draft was the three typed lines: its checkboxes 0, 1, 2 are 0, 2, 3 now.
        Assert.Equal(new[] { 0, 2, 3 }, QuestTextFormatter.CheckboxesSinceKillSteps(cache, quest));
    }

    [Fact]
    public void TicksAfterTheInsertedLine_StayOnTheirSteps_AndTheLastIsNotLost()
    {
        GameDataCache cache = NewCache();
        // Ticked before the change: "bow", "take crown" and "kneel" — every line there was.
        CharacterProfile profile = ProfileWith(new QuestProgress(400, 0) { CheckedSteps = new List<int> { 0, 1, 2 } });

        Assert.True(QuestTickMigration.Apply(profile, cache, Drafted));

        // The same three steps; the new kill line (checkbox 1) is left unticked.
        Assert.Equal(new[] { 0, 2, 3 }, profile.QuestLog![0].CheckedSteps);
        Assert.False(profile.QuestTicksPredateKillSteps);
    }

    [Fact]
    public void QuestWhoseDraftDidNotChange_IsUntouched()
    {
        GameDataCache cache = NewCache();
        List<int> ticks = new() { 1 };
        CharacterProfile profile = ProfileWith(new QuestProgress(401, 0) { CheckedSteps = ticks });

        Assert.True(QuestTickMigration.Apply(profile, cache, Drafted));

        Assert.Same(ticks, profile.QuestLog![0].CheckedSteps);
        Assert.Equal(new[] { 1 }, ticks);
    }

    [Fact]
    public void ChecklistTheUserWrote_IsUntouched()
    {
        GameDataCache cache = NewCache();
        List<int> ticks = new() { 0, 1, 2 };
        CharacterProfile profile = ProfileWith(new QuestProgress(400, 0) { CheckedSteps = ticks });

        Assert.True(QuestTickMigration.Apply(profile, cache,
            (flag, step) => new QuestDefinition(flag, step, steps: "[] my own step\n[] another\n[] a third")));

        Assert.Same(ticks, profile.QuestLog![0].CheckedSteps);
        Assert.Equal(new[] { 0, 1, 2 }, ticks);
    }

    [Fact]
    public void RunningItTwice_MovesTheTicksOnce()
    {
        GameDataCache cache = NewCache();
        CharacterProfile profile = ProfileWith(new QuestProgress(400, 0) { CheckedSteps = new List<int> { 1 } });

        Assert.True(QuestTickMigration.Apply(profile, cache, Drafted));
        Assert.Equal(new[] { 2 }, profile.QuestLog![0].CheckedSteps);

        // The mark is gone, so a second pass leaves the already-moved tick where it is.
        Assert.False(QuestTickMigration.Apply(profile, cache, Drafted));
        Assert.Equal(new[] { 2 }, profile.QuestLog[0].CheckedSteps);
    }

    [Fact]
    public void ProfileThatIsNotMarked_IsLeftAlone()
    {
        GameDataCache cache = NewCache();
        CharacterProfile profile = new()
        {
            QuestLog = new List<QuestProgress> { new(400, 0) { CheckedSteps = new List<int> { 1 } } },
        };

        Assert.False(QuestTickMigration.Apply(profile, cache, Drafted));
        Assert.Equal(new[] { 1 }, profile.QuestLog[0].CheckedSteps);
    }

    [Fact]
    public void WithNoGameData_ItWaits()
    {
        GameDataCache cache = NewCache(withData: false);
        CharacterProfile profile = ProfileWith(new QuestProgress(400, 0) { CheckedSteps = new List<int> { 1 } });

        Assert.False(QuestTickMigration.Apply(profile, cache, Drafted));

        Assert.True(profile.QuestTicksPredateKillSteps);
        Assert.Equal(new[] { 1 }, profile.QuestLog![0].CheckedSteps);
    }
}

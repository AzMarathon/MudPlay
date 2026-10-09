using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins how a TBInfo script line is read for the Quest Flags table's requirement columns and
// for the Quest Flag Steps window: which directives are put into words, that every other step
// is carried over untouched, how a line's way in is found (room command, NPC keyword, a
// monster's death spell), which lines fold into one entry, and the order the entries come in.
public sealed class QuestFlagWalkthroughTests : IDisposable
{
    private const int Flag = 300;

    private readonly string _root;

    public QuestFlagWalkthroughTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-questwalk-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // 10 is an NPC with keywords; 11 dies into spell 40, which ends by casting 41; 12's death
    // spell targets a user, so it never fires.
    private const string MonstersJson = """
        [
          { "Number": 10, "Name": "Old Sage", "DeathSpell": 0 },
          { "Number": 11, "Name": "bog troll", "DeathSpell": 40 },
          { "Number": 12, "Name": "town crier", "DeathSpell": 45 }
        ]
        """;

    private const string SpellsJson = """
        [
          { "Number": 40, "Name": "troll temp", "Targets": 12, "Abil-0": 151, "AbilVal-0": 41 },
          { "Number": 41, "Name": "troll text", "Targets": 1, "Abil-0": 148, "AbilVal-0": 600 },
          { "Number": 45, "Name": "crier text", "Targets": 8, "Abil-0": 148, "AbilVal-0": 610, "Casted By": "Monster #12" },
          { "Number": 60, "Name": "reveal", "Targets": 1 },
          { "Number": 70, "Name": "fireball", "Targets": 1 }
        ]
        """;

    private const string RoomsJson = """
        [
          { "Map Number": 1, "Room Number": 100, "Name": "Mossy Shrine" },
          { "Map Number": 1, "Room Number": 101, "Name": "Sage's Hut" }
        ]
        """;

    private const string ItemsJson = """
        [
          { "Number": 900, "Name": "troll tooth" },
          { "Number": 901, "Name": "silver key" },
          { "Number": 902, "Name": "old map" }
        ]
        """;

    private const string ClassesJson = """
        [
          { "Number": 1, "Name": "Warrior" },
          { "Number": 2, "Name": "Mage" },
          { "Number": 3, "Name": "Thief" }
        ]
        """;

    private const string RacesJson = """
        [
          { "Number": 5, "Name": "Elf" }
        ]
        """;

    // 500 — the Old Sage's keywords: tale / story → 501, reward → 510.
    // 502 — start: without the flag, level 15, sets 1.
    // 511 — turn-in at exactly 2: the Warrior and Mage lines differ only in the class step; the
    //       Thief line has its own level and no reward.
    // 600 — the bog troll's death script: at exactly 1, sets 2.
    // 610 — hangs off a death spell that never fires: at 1 or more, adds 1.
    // 700 — a room command with two wordings: at 3 or more, clears the flag.
    // 710 — a room command only for a character without the flag.
    // 720 — one band of a random table, with a failability written a way that isn't settled.
    // 730 — a room command that reads and changes other flags.
    private const string TBInfoJson = """
        [
          { "Number": 500, "Action": "tale:501\nstory:501\nreward:510\n", "Called From": "Monster #10" },
          { "Number": 501, "Action": "\u0000", "Called From": "Textblock #500" },
          { "Number": 502, "Action": "minlevel 10 3000:minlevel 15:failability 300 3001:giveability 300 1:text 503\n", "Called From": "Textblock #501" },
          { "Number": 510, "Action": "\u0000", "Called From": "Textblock #500" },
          { "Number": 511, "Action": "check class:class 1:checkability 300 2:testability 300 2:checkitem 900 3002:takeitem 900:price 500 3003:giveability 300 3:giveitem 901:addexp 2500:addability 4 1:learnspell 70:teleport 101 1:remoteaction 5 6 0 3:cast 60\ncheck class:class 2:checkability 300 2:testability 300 2:checkitem 900 3002:takeitem 900:price 500 3003:giveability 300 3:giveitem 901:addexp 2500:addability 4 1:learnspell 70:teleport 101 1:remoteaction 5 6 0 3:cast 60\ncheck class:class 3:minlevel 20:checkability 300 2:testability 300 2:giveability 300 3\n", "Called From": "Textblock #510" },
          { "Number": 600, "Action": "checkability 300 1:testability 300 1:failitem 900:giveitem 900:giveability 300 2\n", "Called From": "Spell #41" },
          { "Number": 610, "Action": "checkability 300 1:addability 300 1\n", "Called From": "Spell #45" },
          { "Number": 700, "Action": "touch altar:checkability 300 3:nomonsters 3004:goodaligned -51 3005:needmonster 11 3006:roomitem 902 3007:removeability 300\npress altar:checkability 300 3:nomonsters 3004:goodaligned -51 3005:needmonster 11 3006:roomitem 902 3007:removeability 300\n", "Called From": "Room 1/100" },
          { "Number": 710, "Action": "kneel:failability 300:testskill agility 25 955\n", "Called From": "Room 1/100" },
          { "Number": 720, "Action": "60:addexp 0\n100:giveability 300 9:failability 301 1 4105\n", "Called From": "Textblock(rndm) #719" },
          { "Number": 730, "Action": "pray:checkability 300 1:failability 301:giveability 302 1:addability 301 2\n", "Called From": "Room 1/101" },
          { "Number": 740, "Action": "giveability 301 1\n", "Called From": "" }
        ]
        """;

    private static readonly IReadOnlyDictionary<int, IReadOnlyList<RoomKey>> MonsterRooms =
        new Dictionary<int, IReadOnlyList<RoomKey>>
        {
            [10] = new[] { new RoomKey(1, 101) },
            [11] = new[] { new RoomKey(1, 100) },
        };

    private GameDataCache NewCache()
    {
        string dir = Path.Combine(_root, "alpha");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Monsters.json"), MonstersJson);
        File.WriteAllText(Path.Combine(dir, "Spells.json"), SpellsJson);
        File.WriteAllText(Path.Combine(dir, "Rooms.json"), RoomsJson);
        File.WriteAllText(Path.Combine(dir, "Items.json"), ItemsJson);
        File.WriteAllText(Path.Combine(dir, "Classes.json"), ClassesJson);
        File.WriteAllText(Path.Combine(dir, "Races.json"), RacesJson);
        File.WriteAllText(Path.Combine(dir, "TBInfo.json"), TBInfoJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        return cache;
    }

    private QuestFlagWalkthrough Walk()
    {
        GameDataCache cache = NewCache();
        return QuestFlagWalkthroughBuilder.Build(new QuestFlagIndex(cache), cache, Flag, MonsterRooms);
    }

    private static QuestFlagStepEntry Step(QuestFlagWalkthrough walk, string headingStart) =>
        walk.Steps.Concat(walk.WithoutFlagSteps).Single(s => s.Heading.StartsWith(headingStart, StringComparison.Ordinal));

    // ----- The script line itself -----

    [Theory]
    [InlineData("throw egg:checkitem 998", "throw egg")]
    [InlineData("summon avatar:minlevel 30 1674", "summon avatar")]
    [InlineData("check class:class 2:giveability 50 1", null)]
    [InlineData("minlevel 15:giveability 133 1", null)]
    [InlineData("summon 595:message 12", null)]
    [InlineData("100:giveitem 1449", null)]
    public void Line_LeadingStep_IsACommandOnlyWhenAPlayerTypesIt(string raw, string? command)
    {
        Assert.Equal(command, QuestScriptLine.Parse(raw).Command);
    }

    [Fact]
    public void Line_LeadingNumber_IsARandomTableBand()
    {
        QuestScriptLine line = QuestScriptLine.Parse("100:giveitem 1449:giveability 205 1");
        Assert.Equal(100, line.RollBand);
        Assert.Equal(new[] { "giveitem 1449", "giveability 205 1" }, line.Steps.Select(s => s.Raw));
    }

    [Theory]
    [InlineData("minlevel 15:giveability 133 1", "15+")]
    [InlineData("minlevel 10 3246:takeitem 5:minlevel 15", "15+")]   // every gate is enforced
    [InlineData("minlevel 27 2614:addexp 5000:minlevel 23", "27+")]  // a later, lower one relaxes nothing
    [InlineData("maxlevel 19", "up to 19")]
    [InlineData("minlevel 20:maxlevel 29", "20 to 29")]
    [InlineData("giveability 133 1", "")]
    public void Line_Level_TakesTheHighestGate(string raw, string level)
    {
        Assert.Equal(level, QuestScriptLine.Parse(raw).LevelText);
    }

    // ----- Table columns -----

    private static QuestFlagRef Row(QuestFlagIndex index, int textblock, QuestFlagRelation relation, int order = 0) =>
        index.Entries.Single(e => e.Flag == Flag && e.Relation == relation
            && e.Line.Textblock == textblock && e.Line.Order == order);

    [Fact]
    public void Table_NpcKeywordLine_ShowsTheAskAndItsRequirements()
    {
        QuestFlagIndex index = new(NewCache());

        QuestFlagRef start = Row(index, 502, QuestFlagRelation.Grants);
        Assert.Equal("ask old sage tale (or story)", start.Line.Command);
        Assert.Equal("15+", start.Line.Level);
        Assert.Equal(1, start.Value);

        QuestFlagRef turnIn = Row(index, 511, QuestFlagRelation.Grants);
        Assert.Equal("ask old sage reward", turnIn.Line.Command);
        Assert.Equal("Warrior", turnIn.Line.Classes);
        Assert.Equal("troll tooth (taken)", turnIn.Line.Items);
        Assert.Equal(QuestFlagSourceKind.Monster, turnIn.SourceKind);
        Assert.Equal("Old Sage", turnIn.SourceName);
    }

    [Fact]
    public void Table_KeepsOneRowPerScriptLine()
    {
        QuestFlagIndex index = new(NewCache());

        // Three lines of one textblock each grant the flag the same value from the same NPC.
        List<QuestFlagRef> grants = index.Entries
            .Where(e => e.Flag == Flag && e.Relation == QuestFlagRelation.Grants && e.Line.Textblock == 511)
            .ToList();
        Assert.Equal(new[] { "Warrior", "Mage", "Thief" }, grants.Select(g => g.Line.Classes));
        Assert.Equal(new[] { "", "", "20+" }, grants.Select(g => g.Line.Level));
    }

    [Fact]
    public void Table_RoomCommand_IsTypedAsWritten()
    {
        QuestFlagIndex index = new(NewCache());
        Assert.Equal("touch altar", Row(index, 700, QuestFlagRelation.Clears, order: 0).Line.Command);
        Assert.Equal("press altar", Row(index, 700, QuestFlagRelation.Clears, order: 1).Line.Command);
    }

    [Fact]
    public void Table_DeathSpellLine_NamesTheMonsterToKill()
    {
        QuestFlagIndex index = new(NewCache());
        QuestFlagRef row = Row(index, 600, QuestFlagRelation.Grants);
        // Spell 41 is reached from the bog troll's death spell 40 through its EndCast.
        Assert.Equal("kill bog troll", row.Line.Command);
        Assert.Equal(QuestFlagSourceKind.Spell, row.SourceKind);
    }

    [Fact]
    public void Table_DeathSpellThatNeverFires_GivesNoCommand()
    {
        QuestFlagIndex index = new(NewCache());
        Assert.Equal(string.Empty, Row(index, 610, QuestFlagRelation.Advances).Line.Command);
    }

    [Fact]
    public void Table_FailabilityMessageNumber_IsNotAStep()
    {
        QuestFlagIndex index = new(NewCache());
        Assert.Equal(0, Row(index, 502, QuestFlagRelation.Gate).Value);
    }

    // ----- Steps: the settled directives -----

    [Fact]
    public void Step_Start_ReadsWithoutTheFlagAndTheHighestLevel()
    {
        QuestFlagStepEntry step = Step(Walk(), "Start");

        Assert.Equal("Start — without the flag → sets 1", step.Heading);
        Assert.Equal(new[] { "Type \"ask old sage tale\" (the same reply comes from: story) — Old Sage is in Sage's Hut (1/101)" }, step.Do);
        Assert.Equal(new[] { "Level 15 or higher" }, step.Needs);
        Assert.Empty(step.Gives);
        Assert.Equal(new[] { "text 503" }, step.Also);
        Assert.Equal(new[] { "Textblock #502: minlevel 10 3000:minlevel 15:failability 300 3001:giveability 300 1:text 503" }, step.Script);
    }

    [Fact]
    public void Step_TurnIn_PutsTheSettledStepsIntoWordsAndQuotesTheRest()
    {
        QuestFlagStepEntry step = Walk().Steps.First(s => s.Heading == "At exactly 2 → sets 3");

        Assert.Equal(new[]
        {
            "Class: Warrior, Mage",
            "Taken from you: troll tooth (item 900)",
            "Costs 500 copper",
        }, step.Needs);
        Assert.Equal(new[]
        {
            "silver key (item 901)",
            "2,500 experience",
            "MaxDamage +1",
            "Teaches the spell fireball (spell 70)",
            "Teleports you to Sage's Hut (1/101)",
        }, step.Gives);
        // What isn't settled stays word for word; a cast only gains the spell's name.
        Assert.Equal(new[] { "check class", "remoteaction 5 6 0 3", "cast 60  [spell: reveal]" }, step.Also);
    }

    [Fact]
    public void Step_ClassVariants_FoldIntoOneEntry_OtherDifferencesStayApart()
    {
        List<QuestFlagStepEntry> turnIns = Walk().Steps.Where(s => s.Heading == "At exactly 2 → sets 3").ToList();

        Assert.Equal(2, turnIns.Count);
        Assert.Equal(2, turnIns[0].Script.Count);                       // the Warrior and Mage lines
        Assert.Contains("Class: Warrior, Mage", turnIns[0].Needs);
        Assert.Equal(new[] { "Level 20 or higher", "Class: Thief" }, turnIns[1].Needs);
        Assert.Single(turnIns[1].Script);
    }

    [Fact]
    public void Step_KillLine_NamesTheMonsterAndItsRoom()
    {
        QuestFlagStepEntry step = Step(Walk(), "At exactly 1");

        Assert.Equal("At exactly 1 → sets 2", step.Heading);
        Assert.Equal(new[] { "Kill bog troll (monster 11), found in Mossy Shrine (1/100) — its death runs this script" }, step.Do);
        Assert.Equal(new[] { "Must not have troll tooth (item 900)" }, step.Needs);
        Assert.Equal(new[] { "troll tooth (item 900)" }, step.Gives);
    }

    [Fact]
    public void Step_RoomCommand_ListsEveryWordingOnce()
    {
        QuestFlagStepEntry step = Step(Walk(), "At 3 or more");

        Assert.Equal("At 3 or more → clears the flag", step.Heading);
        Assert.Equal(new[] { "Type any of: \"touch altar\", \"press altar\" in Mossy Shrine (1/100)" }, step.Do);
        Assert.Equal(new[]
        {
            "old map (item 902) must be in the room",
            "No monster in the room (an NPC counts as one)",
            "bog troll (monster 11) in the room",
            // The threshold is quoted, without the message number that follows it.
            "Alignment, as the script writes it: goodaligned -51",
        }, step.Needs);
        Assert.Equal(new[]
        {
            "Textblock #700: touch altar:checkability 300 3:nomonsters 3004:goodaligned -51 3005:needmonster 11 3006:roomitem 902 3007:removeability 300",
            "    (and 1 more line with the other wording of the command, otherwise the same)",
        }, step.Script);
    }

    [Fact]
    public void Step_OtherFlags_AreNamedAndListed()
    {
        QuestFlagWalkthrough walk = Walk();
        QuestFlagStepEntry step = Step(walk, "At 1 or more → no change");

        Assert.Equal(new[] { "Type \"pray\" in Sage's Hut (1/101)" }, step.Do);
        Assert.Equal(new[] { "Without QuestFlag301 (301)" }, step.Needs);
        Assert.Equal(new[] { "Sets QuestFlag302 (302) to 1", "Adds 2 to QuestFlag301 (301)" }, step.Gives);
        Assert.Equal(new[] { 301, 302 }, walk.OtherFlags);
    }

    // ----- Steps: what isn't settled stays raw -----

    [Fact]
    public void Step_UnsettledWayIn_SaysWhereTheLineIsReachedFrom()
    {
        QuestFlagStepEntry step = Step(Walk(), "At 1 or more → adds 1");
        Assert.Equal(new[] { "Reached from spell crier text (#45; the data lists it as cast by Monster #12)" }, step.Do);
    }

    [Fact]
    public void Step_RandomBand_GivesItsShareAndKeepsAnUnsettledFailabilityRaw()
    {
        QuestFlagStepEntry step = Step(Walk(), "No check on the flag");

        Assert.Equal("No check on the flag → sets 9", step.Heading);
        Assert.Equal(new[]
        {
            "Reached from Textblock(rndm) #719",
            "One outcome of a random draw from textblock #720: the band up to 100 (40% of draws)",
        }, step.Do);
        Assert.Equal(new[] { "failability 301 1 4105" }, step.Also);
        Assert.Empty(step.Needs);
    }

    [Fact]
    public void Step_UnknownDirective_IsShownVerbatim()
    {
        QuestFlagStepEntry step = Step(Walk(), "Without the flag");
        Assert.Equal(new[] { "testskill agility 25 955" }, step.Also);
        Assert.Empty(step.Needs);
        Assert.Empty(step.Gives);
    }

    // ----- Order -----

    [Fact]
    public void Steps_AreOrderedByTheValueGoingIn_ThenTheValueLeft()
    {
        QuestFlagWalkthrough walk = Walk();

        Assert.Equal(new[]
        {
            "Start — without the flag → sets 1",
            "No check on the flag → sets 9",
            "At exactly 1 → sets 2",
            "At 1 or more → no change",
            "At 1 or more → adds 1",
            "At exactly 2 → sets 3",
            "At exactly 2 → sets 3",
            "At 3 or more → clears the flag",
        }, walk.Steps.Select(s => s.Heading));
        // A line that only bars a character who has the flag is kept apart.
        Assert.Equal(new[] { "Without the flag → no change" }, walk.WithoutFlagSteps.Select(s => s.Heading));
    }

    [Fact]
    public void FlagNoScriptTouches_HasNoSteps()
    {
        GameDataCache cache = NewCache();
        QuestFlagWalkthrough walk = QuestFlagWalkthroughBuilder.Build(new QuestFlagIndex(cache), cache, 399);
        Assert.Empty(walk.Steps);
        Assert.Empty(walk.WithoutFlagSteps);
    }
}

using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Pins RoomSpellTeleportClassifier — the read behind the Navigation map's
// by-teleport room-spell overlay. Red (Sudden) is a teleport behind nothing or
// behind a roll; yellow (Conditional) one behind conditions alone. The spells and
// textblocks are copied from the imported data (Stock 1.11p unless a test says
// Paradigm), cut down to the lines that decide the answer; the made-up ones say so.
public sealed class RoomSpellTeleportClassifierTests
{
    private const int TeleportRoom = 140, TeleportMap = 141, TextBlock = 148, EndCast = 151, EndCastPercent = 164;
    private const int Damage = 1, DescMsg = 115;

    private readonly Dictionary<int, SpellFormulaInput> _spells = new();
    private readonly Dictionary<int, TBInfoEntry> _blocks = new();

    private void Spell(int number, int minBase, int maxBase, params (int Code, int Value)[] abilities)
    {
        var list = new List<SpellAbility>();
        foreach ((int code, int value) in abilities) list.Add(new SpellAbility(code, value));
        _spells[number] = new SpellFormulaInput { Number = number, MinBase = minBase, MaxBase = maxBase, Abilities = list };
    }

    private void Block(int number, string? action, int linkTo = 0) =>
        _blocks[number] = new TBInfoEntry { Number = number, Action = action, LinkTo = linkTo };

    private RoomSpellTeleport Classify(int spell) => Classify(spell, out _);

    private RoomSpellTeleport Classify(int spell, out string? gap) => RoomSpellTeleportClassifier.Classify(
        spell,
        n => _spells.TryGetValue(n, out SpellFormulaInput s) ? s : null,
        n => _blocks.GetValueOrDefault(n),
        out gap);

    // ----- Sudden: nothing in the way ---------------------------------

    [Fact]
    public void OwnTeleportAbility_IsSudden()
    {
        // "gloomy teleport": a random room of map 8. Where it lands is random;
        // that it moves you is not.
        Spell(1257, 633, 656, (TeleportRoom, 0), (TeleportMap, 8), (DescMsg, 66));

        Assert.Equal(RoomSpellTeleport.Sudden, Classify(1257, out string? gap));
        Assert.Null(gap);
    }

    [Fact]
    public void ConditionAfterTheTeleport_DoesNotGateIt()
    {
        // Made up: the move has happened by the time the later step is tried.
        Spell(9020, 0, 0, (TextBlock, 9700));
        Block(9700, "message 4:teleport 12 3:nomonsters:message 5");

        Assert.Equal(RoomSpellTeleport.Sudden, Classify(9020));
    }

    [Fact]
    public void WordTheInterpreterDoesNotKnow_GatesNothing()
    {
        // `levelcheck` is in the data and is no step of the interpreter, which
        // passes over it.
        Spell(9021, 0, 0, (TextBlock, 9701));
        Block(9701, "levelcheck 5:teleport 12 3");

        Assert.Equal(RoomSpellTeleport.Sudden, Classify(9021));
    }

    // ----- Conditional: conditions alone ------------------------------

    [Fact]
    public void TextblockTeleportBehindItemChecks_IsConditional()
    {
        // "ice cavern level 2": the block number is in MinBase, the slot holds 0.
        Spell(1145, 9410, 9410, (TextBlock, 0), (DescMsg, 66));
        Spell(1142, 50, 60, (Damage, 0));
        Block(9410, "failitem 930:failitem 191:message 2979:teleport 297 10:message 2980:cast 1142\n\n");

        Assert.Equal(RoomSpellTeleport.Conditional, Classify(1145));
    }

    [Fact]
    public void ClassAlignmentAndEmptyRoomGates_AreConditional()
    {
        // "thief filter", "white forest noise" and "dao scatter".
        Spell(1325, 0, 0, (TextBlock, 4241), (DescMsg, 66));
        Block(4241, "class 1:teleport 2982 17\nclass 2:teleport 2982 17\nclass 3:teleport 2982 17");
        Spell(1079, 0, 0, (TextBlock, 9443), (DescMsg, 66));
        Spell(1135, 50, 100, (Damage, 0));
        Block(9443, "evilaligned -50:goodaligned 39:teleport 1641 17\nevilaligned 40:teleport 1641 17:cast 1135");
        Spell(742, 335, 335, (157, 0), (TextBlock, 2692), (DescMsg, 66));
        Block(2692, "nomonsters:message 2081:teleport 335 12:message 2082");

        Assert.Equal(RoomSpellTeleport.Conditional, Classify(1325));
        Assert.Equal(RoomSpellTeleport.Conditional, Classify(1079));
        Assert.Equal(RoomSpellTeleport.Conditional, Classify(742));
    }

    [Fact]
    public void ConditionBeforeACast_CarriesIntoTheSpellCast()
    {
        // "desert sandstorm temp" ends into "sandstorm", whose textblock casts the
        // teleport spell 713 unless the sunstone is held.
        Spell(743, 0, 0, (EndCast, 1387), (DescMsg, 66));
        Spell(1387, 0, 0, (TextBlock, 4357), (DescMsg, 66));
        Spell(713, 1055, 1084, (TeleportRoom, 0), (TeleportMap, 12));
        Block(4357, "failitem 1180:cast 713");

        Assert.Equal(RoomSpellTeleport.Conditional, Classify(743));
    }

    [Fact]
    public void TableWhoseEveryBandTeleports_IsNoRoll_AndKeepsTheLinesCondition()
    {
        // "ice cavern level 1": the roll only picks which of two rooms, so what
        // decides the slide is the rope.
        Spell(1144, 9407, 9407, (TextBlock, 0), (DescMsg, 66));
        Spell(1142, 50, 60, (Damage, 0));
        Block(9407, "failitem 191:failitem 930:random 9408");
        Block(9408, "96:message 2979:teleport 287 10:message 2980:cast 1142\n"
                  + "100:message 2979:teleport 297 10:message 2980:cast 1142");

        Assert.Equal(RoomSpellTeleport.Conditional, Classify(1144));
    }

    [Fact]
    public void TableWhoseEveryBandTeleports_WithNothingBeforeIt_IsSudden()
    {
        // Made up: the same table reached with no condition on the line.
        Spell(9022, 0, 0, (TextBlock, 9702));
        Block(9702, "random 9408");
        Block(9408, "96:teleport 287 10\n100:teleport 297 10");

        Assert.Equal(RoomSpellTeleport.Sudden, Classify(9022));
    }

    [Fact]
    public void ConditionInsideEveryBand_IsConditional()
    {
        // Made up: whatever is rolled, the empty room decides it.
        Spell(9023, 0, 0, (TextBlock, 9703));
        Block(9703, "random 9704");
        Block(9704, "50:nomonsters:teleport 12 3\n100:nomonsters:teleport 13 3");

        Assert.Equal(RoomSpellTeleport.Conditional, Classify(9023));
    }

    [Fact]
    public void BuffCheck_IsACondition_OnBothItsBranches()
    {
        // Made up: the block named runs with the buff missing, the rest of the line
        // with it up.
        Spell(9001, 0, 0, (TextBlock, 9100));
        Block(9100, "checkspell 711 9101:message 5");
        Block(9101, "teleport 12 3");
        Spell(9024, 0, 0, (TextBlock, 9102));
        Block(9102, "failspell 711 9103:teleport 12 3");
        Block(9103, "message 5");

        Assert.Equal(RoomSpellTeleport.Conditional, Classify(9001));
        Assert.Equal(RoomSpellTeleport.Conditional, Classify(9024));
    }

    [Fact]
    public void NamedAreaTeleport_IsATeleport()
    {
        // Made up: the engine's named forms pick a random room of an area.
        Spell(9006, 0, 0, (TextBlock, 9400));
        Block(9400, "nomonsters:teleport_sewers");
        Spell(9025, 0, 0, (TextBlock, 9401));
        Block(9401, "teleport_sewers");

        Assert.Equal(RoomSpellTeleport.Conditional, Classify(9006));
        Assert.Equal(RoomSpellTeleport.Sudden, Classify(9025));
    }

    // ----- Sudden: a roll, with or without conditions -----------------

    [Fact]
    public void TableWithABandThatDoesNothing_IsSudden_DespiteTheBandsCondition()
    {
        // Paradigm "cavern 1": 85 in 100 nothing, the rest a fall for low levels.
        Spell(5788, 10167, 10167, (TextBlock, 0), (DescMsg, 66));
        Spell(5786, 2290, 2298, (TeleportRoom, 0), (TeleportMap, 12), (Damage, 35));
        Block(10167, "random 10168");
        Block(10168, "85:addevil 0\n100:maxlevel 19:cast 5786");

        Assert.Equal(RoomSpellTeleport.Sudden, Classify(5788));
    }

    [Fact]
    public void DesertSpell_SandstormBehindNestedTables_IsSudden()
    {
        // "desert spell": the buff check's missing-buff block and the line after it
        // both roll table 2655, whose last band rolls 2700, whose second band casts
        // the sandstorm. Conditions all the way down, and two rolls.
        Spell(683, 0, 0, (TextBlock, 2653), (DescMsg, 66));
        Spell(712, 5, 20, (Damage, 0));
        Spell(743, 0, 0, (EndCast, 1387), (DescMsg, 66));
        Spell(1387, 0, 0, (TextBlock, 4357), (DescMsg, 66));
        Spell(713, 1055, 1084, (TeleportRoom, 0), (TeleportMap, 12));
        Block(2653, "checkspell 711 2654:random 2655");
        Block(2654, "failitem 1180:cast 712:random 2655\ncheckitem 1180:random 2655");
        Block(2655, "87:addexp 0\n91:message 2018\n99:message 2023\n100:random 2700");
        Block(2700, "15:nomonsters:summon 570:summon 601\n30:nomonsters:failitem 1180:cast 743\n"
                  + "100:nomonsters:checkitem 1607:summon 938:summon 938");
        Block(4357, "failitem 1180:cast 713");

        Assert.Equal(RoomSpellTeleport.Sudden, Classify(683, out string? gap));
        Assert.Null(gap);
    }

    [Fact]
    public void TeleportPastASkillRoll_IsSudden_OnEitherSideOfIt()
    {
        // Made up: no room spell in either set teleports off a testskill. Both the
        // steps after the roll and the block run on a miss hang on it, and the class
        // condition beside it doesn't make it foreseeable.
        Spell(9002, 0, 0, (TextBlock, 9200));
        Block(9200, "class 5:testskill agility 20 9201:teleport 12 3");
        Block(9201, "message 7");
        Spell(9003, 0, 0, (TextBlock, 9210));
        Block(9210, "class 5:testskill agility 20 9211:addevil 0");
        Block(9211, "teleport 12 3");

        Assert.Equal(RoomSpellTeleport.Sudden, Classify(9002));
        Assert.Equal(RoomSpellTeleport.Sudden, Classify(9003));
    }

    [Fact]
    public void EndCastWithAPercent_IsARoll()
    {
        // Made up: no room spell in either set carries EndCast%. Shaped like "spear"
        // (EndCast% 20), pointed at the conditional sandstorm chain: the roll
        // outranks the condition.
        Spell(9004, 0, 0, (Damage, 0), (EndCastPercent, 20), (EndCast, 1387));
        Spell(9026, 0, 0, (Damage, 0), (EndCast, 1387));
        Spell(1387, 0, 0, (TextBlock, 4357), (DescMsg, 66));
        Spell(713, 1055, 1084, (TeleportRoom, 0), (TeleportMap, 12));
        Block(4357, "failitem 1180:cast 713");

        Assert.Equal(RoomSpellTeleport.Sudden, Classify(9004));
        Assert.Equal(RoomSpellTeleport.Conditional, Classify(9026));
    }

    [Fact]
    public void TableThatStopsShortOfTheRoll_IsARoll()
    {
        // Made up: both bands teleport, but a roll of 90 or more runs neither.
        Spell(9005, 0, 0, (TextBlock, 9300));
        Block(9300, "nomonsters:random 9301");
        Block(9301, "50:teleport 12 3\n90:teleport 13 3");

        Assert.Equal(RoomSpellTeleport.Sudden, Classify(9005));
    }

    [Fact]
    public void TablesThatRollEachOther_AreReadOnce()
    {
        // "sea 1" with a boat: tables 9390 and 9394 roll each other, and the only
        // teleport sits five rolls down.
        Spell(1076, 0, 0, (DescMsg, 66), (TextBlock, 9358));
        Block(9358, "minlevel 50:checkitem 690:random 9363");
        Block(9363, "10:addexp 0\n100:nomonsters:random 9390");
        Block(9390, "50:addexp 0\n80:message 3071:summon 904\n100:random 9394");
        Block(9394, "5:message 2906:random 9396\n10:random 9390\n100:random 9389");
        Block(9389, "40:addexp 0\n100:message 3071:summon 904");
        Block(9396, "20:teleport 981 17\n40:teleport 982 17\n60:teleport 983 17\n80:teleport 984 17\n100:teleport 985 17");

        Assert.Equal(RoomSpellTeleport.Sudden, Classify(1076, out string? gap));
        Assert.Null(gap);
    }

    // ----- Tables: which lines can run --------------------------------

    [Fact]
    public void BandUnderAnEarlierNumber_NeverRuns()
    {
        // Paradigm tables 5396 and 5605 open on a high number and follow it with
        // lower ones, which no roll reaches. Made up: one of those dead bands
        // teleports.
        Spell(9027, 0, 0, (TextBlock, 9705));
        Block(9705, "random 9706");
        Block(9706, "80:addevil 0\n70:teleport 12 3\n74:message 9493\n100:message 9355");

        Assert.Equal(RoomSpellTeleport.None, Classify(9027, out string? gap));
        Assert.Null(gap);
    }

    [Fact]
    public void LineThatDoesNotLeadWithANumber_IsNoBand()
    {
        // Paradigm table 5435 opens `nomonsters 289:25:summon 2655`: its number
        // isn't first, so the roll never picks it. Made up: such a line teleporting,
        // above a real band that doesn't, and the reverse.
        Spell(9028, 0, 0, (TextBlock, 9707));
        Block(9707, "random 9708");
        Block(9708, "nomonsters 289:25:teleport 12 3\n100:addevil 0");
        Spell(9029, 0, 0, (TextBlock, 9709));
        Block(9709, "random 9710");
        Block(9710, "nomonsters 289:25:addevil 0\n100:teleport 12 3");

        Assert.Equal(RoomSpellTeleport.None, Classify(9028));
        Assert.Equal(RoomSpellTeleport.Sudden, Classify(9029));
    }

    // ----- None, and loops ---------------------------------------------

    [Fact]
    public void DamageAndSummonSpells_HaveNoTeleport()
    {
        // "silver river" (a damage cast unless a boat is held), "magma heat", and
        // Paradigm "crypt summon 2".
        Spell(753, 0, 0, (TextBlock, 2750), (DescMsg, 66));
        Spell(754, 10, 20, (Damage, 0));
        Block(2750, "failitem 690:failitem 691:failitem 1181:message 2096:cast 754");
        Spell(526, 30, 60, (Damage, 0), (DescMsg, 66));
        Spell(5248, 0, 0, (TextBlock, 3411));
        Block(3411, "nomonsters:random 3412");
        Block(3412, "60:addevil 0\n85:message 4064\n90:message 4063:summon 2111\n100:message 4063:summon 2122");

        Assert.Equal(RoomSpellTeleport.None, Classify(753, out string? gap));
        Assert.Null(gap);
        Assert.Equal(RoomSpellTeleport.None, Classify(526));
        Assert.Equal(RoomSpellTeleport.None, Classify(5248));
    }

    [Fact]
    public void BlockThatIsThereAndEmpty_IsNoGap()
    {
        // "stop drowning": block 4099 is in the data with nothing in it, which is
        // the data saying nothing happens.
        Spell(515, 0, 0, (153, 512), (153, 513), (TextBlock, 4098));
        Block(4098, "checkspell 512 4099:cast 515\ncheckspell 513 4099:cast 515\n\n");
        Block(4099, null);

        Assert.Equal(RoomSpellTeleport.None, Classify(515, out string? gap));
        Assert.Null(gap);
    }

    [Fact]
    public void SpellsThatEndIntoEachOther_AreReadOnce()
    {
        // Made up: an EndCast loop with nothing in it, and one that reaches a
        // teleport on the way round.
        Spell(9007, 0, 0, (EndCast, 9008));
        Spell(9008, 0, 0, (EndCast, 9007));
        Spell(9009, 0, 0, (EndCast, 9010));
        Spell(9010, 0, 0, (TextBlock, 9500), (EndCast, 9009));
        Block(9500, "cast 9009\nteleport 12 3");

        Assert.Equal(RoomSpellTeleport.None, Classify(9007, out string? gap));
        Assert.Null(gap);
        Assert.Equal(RoomSpellTeleport.Sudden, Classify(9009));
    }

    // ----- Unknown: never "no teleport" on a chain not read in full ---

    [Fact]
    public void NoRoomSpell_IsNone()
    {
        Assert.Equal(RoomSpellTeleport.None, Classify(0, out string? gap));
        Assert.Null(gap);
    }

    [Fact]
    public void SpellTheSetLacks_IsUnknown()
    {
        Assert.Equal(RoomSpellTeleport.Unknown, Classify(4242, out string? gap));
        Assert.Equal("spell 4242 missing", gap);
    }

    [Fact]
    public void MissingTextblock_IsUnknown()
    {
        // "sys j": its buff check names block 4043, which neither realm's data has.
        Spell(943, 100, 100, (TextBlock, 4042), (18, 50), (DescMsg, 66), (150, 50), (157, 1076));
        Block(4042, "checkspell 935 4043:addevil 0\n\n");
        // Made up: a textblock that casts a spell the set lacks.
        Spell(9011, 0, 0, (TextBlock, 9600));
        Block(9600, "nomonsters:cast 4242");

        Assert.Equal(RoomSpellTeleport.Unknown, Classify(943, out string? gap));
        Assert.Equal("textblock 4043 missing", gap);
        Assert.Equal(RoomSpellTeleport.Unknown, Classify(9011, out gap));
        Assert.Equal("spell 4242 missing", gap);
    }

    [Fact]
    public void ContinuationBlock_IsUnknown()
    {
        // Made up: no block a room spell reaches in either set has a LinkTo.
        Spell(9012, 0, 0, (TextBlock, 9601));
        Block(9601, "message 5", linkTo: 9602);
        Block(9602, "teleport 12 3");

        Assert.Equal(RoomSpellTeleport.Unknown, Classify(9012, out string? gap));
        Assert.Equal("textblock 9601 continues in 9602, not read", gap);
    }

    [Fact]
    public void ChainLongerThanTheCap_IsUnknown_NotNone()
    {
        // Made up: EndCasts in a row, the last one a teleport. Inside the cap it is
        // read; one hop past it the walk stops and says so.
        int Chain(int first, int hops)
        {
            for (int i = 0; i < hops; i++) Spell(first + i, 0, 0, (EndCast, first + i + 1));
            Spell(first + hops, 633, 656, (TeleportRoom, 0));
            return first;
        }

        Assert.Equal(RoomSpellTeleport.Sudden, Classify(Chain(20000, RoomSpellTeleportClassifier.MaxChainDepth), out string? gap));
        Assert.Null(gap);
        Assert.Equal(RoomSpellTeleport.Unknown, Classify(Chain(21000, RoomSpellTeleportClassifier.MaxChainDepth + 1), out gap));
        Assert.NotNull(gap);
        Assert.StartsWith("chain cut at spell", gap);
    }

    [Fact]
    public void KnownTeleport_StandsOverAGapElsewhere()
    {
        // Paradigm "desert spell": the buff check names block 2654, which Paradigm's
        // data lacks, but the rest of the line still rolls its way to the sandstorm.
        Spell(683, 0, 0, (TextBlock, 2653), (DescMsg, 66));
        Spell(713, 1061, 1084, (TeleportRoom, 0), (TeleportMap, 12));
        Block(2653, "failspell 711 2654:random 2655");
        Block(2655, "85:addexp 0\n86:maxlevel 19:cast 713\n100:message 2023");

        Assert.Equal(RoomSpellTeleport.Sudden, Classify(683, out string? gap));
        Assert.Equal("textblock 2654 missing", gap);
    }
}

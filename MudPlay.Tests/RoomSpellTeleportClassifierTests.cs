using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Pins RoomSpellTeleportClassifier — the read behind the Navigation map's
// by-teleport room-spell overlay. The spells and textblocks are copied from the
// imported data (Stock 1.11p unless a test says Paradigm), cut down to the lines
// that decide the answer; the few made-up ones say so.
public sealed class RoomSpellTeleportClassifierTests
{
    private const int TeleportRoom = 140, TeleportMap = 141, TextBlock = 148, EndCast = 151, EndCastPercent = 164;
    private const int Damage = 1, DescMsg = 115;

    private readonly Dictionary<int, SpellFormulaInput> _spells = new();
    private readonly Dictionary<int, string> _blocks = new();

    private void Spell(int number, int minBase, int maxBase, params (int Code, int Value)[] abilities)
    {
        var list = new List<SpellAbility>();
        foreach ((int code, int value) in abilities) list.Add(new SpellAbility(code, value));
        _spells[number] = new SpellFormulaInput { Number = number, MinBase = minBase, MaxBase = maxBase, Abilities = list };
    }

    private RoomSpellTeleport Classify(int spell) => RoomSpellTeleportClassifier.Classify(
        spell,
        n => _spells.TryGetValue(n, out SpellFormulaInput s) ? s : null,
        n => _blocks.TryGetValue(n, out string? a) ? a : null);

    [Fact]
    public void OwnTeleportAbility_Teleports()
    {
        // "gloomy teleport": a random room of map 8. Where it lands is random;
        // that it moves you is not.
        Spell(1257, 633, 656, (TeleportRoom, 0), (TeleportMap, 8), (DescMsg, 66));

        Assert.Equal(RoomSpellTeleport.Always, Classify(1257));
    }

    [Fact]
    public void TextblockTeleportBehindItemChecks_Teleports()
    {
        // "ice cavern level 2": the block number is in MinBase, the slot holds 0.
        Spell(1145, 9410, 9410, (TextBlock, 0), (DescMsg, 66));
        Spell(1142, 50, 60, (Damage, 0));
        _blocks[9410] = "failitem 930:failitem 191:message 2979:teleport 297 10:message 2980:cast 1142\n\n";

        Assert.Equal(RoomSpellTeleport.Always, Classify(1145));
    }

    [Fact]
    public void ClassAndAlignmentGates_AreNotChance()
    {
        // "thief filter" and "white forest noise": who is teleported depends on the
        // character, not on a roll.
        Spell(1325, 0, 0, (TextBlock, 4241), (DescMsg, 66));
        _blocks[4241] = "class 1:teleport 2982 17\nclass 2:teleport 2982 17\nclass 3:teleport 2982 17";
        Spell(1079, 0, 0, (TextBlock, 9443), (DescMsg, 66));
        Spell(1135, 50, 100, (Damage, 0));
        _blocks[9443] = "evilaligned -50:goodaligned 39:teleport 1641 17\nevilaligned 40:teleport 1641 17:cast 1135";

        Assert.Equal(RoomSpellTeleport.Always, Classify(1325));
        Assert.Equal(RoomSpellTeleport.Always, Classify(1079));
    }

    [Fact]
    public void EndCastThenTextblockCastOfATeleportSpell_Teleports()
    {
        // "desert sandstorm temp" ends into "sandstorm", whose textblock casts the
        // teleport spell 713.
        Spell(743, 0, 0, (EndCast, 1387), (DescMsg, 66));
        Spell(1387, 0, 0, (TextBlock, 4357), (DescMsg, 66));
        Spell(713, 1055, 1084, (TeleportRoom, 0), (TeleportMap, 12));
        _blocks[4357] = "failitem 1180:cast 713";

        Assert.Equal(RoomSpellTeleport.Always, Classify(743));
    }

    [Fact]
    public void TableWhoseEveryBandTeleports_Teleports()
    {
        // "ice cavern level 1": the roll only picks which of two rooms.
        Spell(1144, 9407, 9407, (TextBlock, 0), (DescMsg, 66));
        Spell(1142, 50, 60, (Damage, 0));
        _blocks[9407] = "failitem 191:failitem 930:random 9408";
        _blocks[9408] = "96:message 2979:teleport 287 10:message 2980:cast 1142\n"
                      + "100:message 2979:teleport 297 10:message 2980:cast 1142";

        Assert.Equal(RoomSpellTeleport.Always, Classify(1144));
    }

    [Fact]
    public void TableWithABandThatDoesNothing_IsAChance()
    {
        // Paradigm "cavern 1": 85 in 100 nothing, the rest a fall for low levels.
        Spell(5788, 10167, 10167, (TextBlock, 0), (DescMsg, 66));
        Spell(5786, 2290, 2298, (TeleportRoom, 0), (TeleportMap, 12), (Damage, 35));
        _blocks[10167] = "random 10168";
        _blocks[10168] = "85:addevil 0\n100:maxlevel 19:cast 5786";

        Assert.Equal(RoomSpellTeleport.Chance, Classify(5788));
    }

    [Fact]
    public void DesertSpell_SandstormBehindNestedTables_IsAChance()
    {
        // "desert spell": the buff check's missing-buff block and the line after it
        // both roll table 2655, whose last band rolls 2700, whose second band casts
        // the sandstorm.
        Spell(683, 0, 0, (TextBlock, 2653), (DescMsg, 66));
        Spell(712, 5, 20, (Damage, 0));
        Spell(743, 0, 0, (EndCast, 1387), (DescMsg, 66));
        Spell(1387, 0, 0, (TextBlock, 4357), (DescMsg, 66));
        Spell(713, 1055, 1084, (TeleportRoom, 0), (TeleportMap, 12));
        _blocks[2653] = "checkspell 711 2654:random 2655";
        _blocks[2654] = "failitem 1180:cast 712:random 2655\ncheckitem 1180:random 2655";
        _blocks[2655] = "87:addexp 0\n91:message 2018\n99:message 2023\n100:random 2700";
        _blocks[2700] = "15:nomonsters:summon 570:summon 601\n30:nomonsters:failitem 1180:cast 743\n"
                      + "100:nomonsters:checkitem 1607:summon 938:summon 938";
        _blocks[4357] = "failitem 1180:cast 713";

        Assert.Equal(RoomSpellTeleport.Chance, Classify(683));
    }

    [Fact]
    public void TeleportOnlyInAMissingBuffBlock_StillTeleports()
    {
        // Made up: a buff check is a condition, so the block it names counts in full.
        Spell(9001, 0, 0, (TextBlock, 9100));
        _blocks[9100] = "checkspell 711 9101:message 5";
        _blocks[9101] = "teleport 12 3";

        Assert.Equal(RoomSpellTeleport.Always, Classify(9001));
    }

    [Fact]
    public void TeleportPastASkillRoll_IsAChance_OnEitherSideOfIt()
    {
        // Made up: no room spell in either set teleports off a testskill. Both the
        // steps after the roll and the block run on a miss hang on it.
        Spell(9002, 0, 0, (TextBlock, 9200));
        _blocks[9200] = "class 5:testskill agility 20 9201:teleport 12 3";
        _blocks[9201] = "message 7";
        Spell(9003, 0, 0, (TextBlock, 9210));
        _blocks[9210] = "testskill agility 20 9211:addevil 0";
        _blocks[9211] = "teleport 12 3";

        Assert.Equal(RoomSpellTeleport.Chance, Classify(9002));
        Assert.Equal(RoomSpellTeleport.Chance, Classify(9003));
    }

    [Fact]
    public void EndCastWithAPercent_IsAChance()
    {
        // Made up: no room spell in either set carries EndCast%. Shaped like "spear"
        // (EndCast% 20), pointed at a teleport spell.
        Spell(9004, 0, 0, (Damage, 0), (EndCastPercent, 20), (EndCast, 713));
        Spell(713, 1055, 1084, (TeleportRoom, 0), (TeleportMap, 12));

        Assert.Equal(RoomSpellTeleport.Chance, Classify(9004));
    }

    [Fact]
    public void TableThatStopsShortOfTheRoll_IsAChance()
    {
        // Made up: both bands teleport, but a roll of 90 or more runs neither.
        Spell(9005, 0, 0, (TextBlock, 9300));
        _blocks[9300] = "random 9301";
        _blocks[9301] = "50:teleport 12 3\n90:teleport 13 3";

        Assert.Equal(RoomSpellTeleport.Chance, Classify(9005));
    }

    [Fact]
    public void NamedAreaTeleport_Teleports()
    {
        // Made up: the engine's named forms pick a random room of an area.
        Spell(9006, 0, 0, (TextBlock, 9400));
        _blocks[9400] = "nomonsters:teleport_sewers";

        Assert.Equal(RoomSpellTeleport.Always, Classify(9006));
    }

    [Fact]
    public void DamageAndSummonSpells_HaveNoTeleport()
    {
        // "silver river" (a damage cast unless a boat is held), "magma heat", and
        // Paradigm "crypt summon 2".
        Spell(753, 0, 0, (TextBlock, 2750), (DescMsg, 66));
        Spell(754, 10, 20, (Damage, 0));
        _blocks[2750] = "failitem 690:failitem 691:failitem 1181:message 2096:cast 754";
        Spell(526, 30, 60, (Damage, 0), (DescMsg, 66));
        Spell(5248, 0, 0, (TextBlock, 3411));
        _blocks[3411] = "nomonsters:random 3412";
        _blocks[3412] = "60:addevil 0\n85:message 4064\n90:message 4063:summon 2111\n100:message 4063:summon 2122";

        Assert.Equal(RoomSpellTeleport.None, Classify(753));
        Assert.Equal(RoomSpellTeleport.None, Classify(526));
        Assert.Equal(RoomSpellTeleport.None, Classify(5248));
    }

    [Fact]
    public void TablesThatRollEachOther_AreReadOnce()
    {
        // "sea 1" with a boat: tables 9390 and 9394 roll each other, and the only
        // teleport sits five rolls down.
        Spell(1076, 0, 0, (DescMsg, 66), (TextBlock, 9358));
        _blocks[9358] = "minlevel 50:checkitem 690:random 9363";
        _blocks[9363] = "10:addexp 0\n100:nomonsters:random 9390";
        _blocks[9390] = "50:addexp 0\n80:message 3071:summon 904\n100:random 9394";
        _blocks[9394] = "5:message 2906:random 9396\n10:random 9390\n100:random 9389";
        _blocks[9389] = "40:addexp 0\n100:message 3071:summon 904";
        _blocks[9396] = "20:teleport 981 17\n40:teleport 982 17\n60:teleport 983 17\n80:teleport 984 17\n100:teleport 985 17";

        Assert.Equal(RoomSpellTeleport.Chance, Classify(1076));
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
        _blocks[9500] = "cast 9009\nteleport 12 3";

        Assert.Equal(RoomSpellTeleport.None, Classify(9007));
        Assert.Equal(RoomSpellTeleport.Always, Classify(9009));
    }

    [Fact]
    public void UnknownSpellOrTextblock_HasNoTeleport()
    {
        // A room naming a spell the set lacks, a spell naming a block it lacks, and
        // a buff check naming one (Paradigm's desert names the absent 2654).
        Spell(9011, 0, 0, (TextBlock, 9600));
        Spell(9012, 0, 0, (TextBlock, 9601));
        _blocks[9601] = "failspell 711 2654:message 5";

        Assert.Equal(RoomSpellTeleport.None, Classify(4242));
        Assert.Equal(RoomSpellTeleport.None, Classify(0));
        Assert.Equal(RoomSpellTeleport.None, Classify(9011));
        Assert.Equal(RoomSpellTeleport.None, Classify(9012));
    }
}

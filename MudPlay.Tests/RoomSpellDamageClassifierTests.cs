using System.Collections.Generic;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// Pins RoomSpellDamageClassifier, the read behind "no rest is started in a room
// whose own spell does damage". The spells and textblocks are copied from the
// imported data (Paradigm 1.9.1 unless a test says Stock), cut down to the lines
// that decide the answer.
public sealed class RoomSpellDamageClassifierTests
{
    private const int Damage = 1, DescMsg = 115, TextBlock = 148, EndCast = 151, Summon = 12;

    private readonly Dictionary<int, SpellFormulaInput> _spells = new();
    private readonly Dictionary<int, TBInfoEntry> _blocks = new();

    private void Spell(int number, int minBase, int maxBase, params (int Code, int Value)[] abilities)
    {
        var list = new List<SpellAbility>();
        foreach ((int code, int value) in abilities) list.Add(new SpellAbility(code, value));
        _spells[number] = new SpellFormulaInput { Number = number, MinBase = minBase, MaxBase = maxBase, Abilities = list };
    }

    private void Block(int number, string action) =>
        _blocks[number] = new TBInfoEntry { Number = number, Action = action };

    private RoomSpellDamage Classify(int spell) => Classify(spell, out _);

    private RoomSpellDamage Classify(int spell, out string? gap) => RoomSpellDamageClassifier.Classify(
        spell,
        n => _spells.TryGetValue(n, out SpellFormulaInput s) ? s : null,
        n => _blocks.GetValueOrDefault(n),
        out gap);

    // ----- damages every tick ------------------------------------------

    [Fact]
    public void OwnDamageAbility_Damages()
    {
        // Magma heat, the spell of the volcano's rooms.
        Spell(526, 30, 60, (Damage, 0), (DescMsg, 66));

        Assert.Equal(RoomSpellDamage.EveryTick, Classify(526, out string? gap));
        Assert.Null(gap);
    }

    [Fact]
    public void EndCastToADamagingSpell_Damages()
    {
        // Holding breath runs out into drowning.
        Spell(512, 0, 0, (EndCast, 513));
        Spell(513, 5, 15, (Damage, 0));

        Assert.Equal(RoomSpellDamage.EveryTick, Classify(512));
    }

    [Fact]
    public void CastBehindACounterItem_Damages()
    {
        // River damage: battered unless a raft is held. The raft is the way out of
        // it, not a condition on the room.
        Spell(753, 0, 0, (TextBlock, 2750));
        Block(2750, "failitem 690:failitem 691:failitem 1181:failitem 3609:message 2096:cast 754\n");
        Spell(754, 10, 20, (Damage, 0));

        Assert.Equal(RoomSpellDamage.EveryTick, Classify(753));
    }

    [Fact]
    public void CastBehindABuffCheck_Damages()
    {
        // Stock's desert: the block run when the water buff is missing does the damage.
        Spell(683, 0, 0, (TextBlock, 2653));
        Block(2653, "checkspell 711 2654:random 2655\n");
        Block(2654, "failitem 1180:cast 712:random 2655\ncheckitem 1180:random 2655\n");
        Block(2655, "100:message 1\n");
        Spell(712, 5, 20, (Damage, 0));

        Assert.Equal(RoomSpellDamage.EveryTick, Classify(683));
    }

    [Fact]
    public void TableThatDamagesWhateverIsRolled_Damages()
    {
        // The ice cavern's slide: the roll only picks where you land.
        Spell(1144, 9407, 9407, (TextBlock, 0));
        Block(9407, "failitem 191:failitem 930:random 9408\n");
        Block(9408, "96:message 2979:teleport 287 10:message 2980:cast 1142\n100:message 2979:teleport 297 10:message 2980:cast 1142\n");
        Spell(1142, 50, 60, (Damage, 0));

        Assert.Equal(RoomSpellDamage.EveryTick, Classify(1144));
    }

    // ----- on a roll: a rest there still recovers ---------------------------

    [Fact]
    public void CastInOneBandOfATable_IsOnARoll()
    {
        // The fungus spell: one tick in ten sprays spores.
        Spell(1205, 4072, 4072, (TextBlock, 0));
        Block(4072, "random 4073\n");
        Block(4073, "80:addevil 0\n90:message 3160:cast 1204\n100:message 3160:cast 1206\n");
        Spell(1204, 5, 25, (Damage, 0));
        Spell(1206, 0, 0, (DescMsg, 1));

        Assert.Equal(RoomSpellDamage.OnARoll, Classify(1205));
    }

    [Fact]
    public void CastBehindASkillTest_IsOnARoll()
    {
        // The Great Pyramid's traps: a band of the table tests Traps, and a miss
        // runs the block that hurts.
        Spell(692, 1239, 1278, (TextBlock, 2519));
        Block(2519, "cast 687:random 2520\n");
        Spell(687, 0, 0, (153, 685));
        Block(2520, "74:addexp 0\n92:testskill traps 20 2701:message 2090\n100:message 1969\n");
        Block(2701, "cast 754");
        Spell(754, 10, 20, (Damage, 0));

        Assert.Equal(RoomSpellDamage.OnARoll, Classify(692));
    }

    [Fact]
    public void EndCastOnAPercent_IsOnARoll()
    {
        // Made up: the follow-on spell fires half the time.
        Spell(9024, 0, 0, (EndCast, 754), (164, 50));
        Spell(754, 10, 20, (Damage, 0));

        Assert.Equal(RoomSpellDamage.OnARoll, Classify(9024));
    }

    // ----- only on a condition: not counted -------------------------------

    [Fact]
    public void CastBehindAlignment_IsConditional()
    {
        // White forest noise burns the evil-aligned only.
        Spell(1079, 0, 0, (TextBlock, 9443));
        Block(9443, "evilaligned -50:goodaligned 39:teleport 1641 17\nevilaligned 40:teleport 1641 17:cast 1135\n");
        Spell(1135, 50, 100, (Damage, 0));

        Assert.Equal(RoomSpellDamage.Conditional, Classify(1079));
    }

    [Fact]
    public void CastBehindALevelCapOnARoll_IsConditional()
    {
        // The cavern floor gives way under a character of level 19 or less.
        Spell(5788, 10167, 10167, (TextBlock, 0));
        Block(10167, "random 10168\n");
        Block(10168, "85:addevil 0\n100:maxlevel 19:cast 5786\n");
        Spell(5786, 2290, 2298, (Damage, 35));

        Assert.Equal(RoomSpellDamage.Conditional, Classify(5788));
    }

    [Fact]
    public void ConditionAfterTheCast_DoesNotGateIt()
    {
        // Made up: the damage has landed by the time the later step is tried.
        Spell(9020, 0, 0, (TextBlock, 9700));
        Block(9700, "cast 754:nomonsters:message 5");
        Spell(754, 10, 20, (Damage, 0));

        Assert.Equal(RoomSpellDamage.EveryTick, Classify(9020));
    }

    [Fact]
    public void AnUnconditionalPathBesideAConditionalOne_Damages()
    {
        // Made up: the strongest path decides.
        Spell(9021, 0, 0, (TextBlock, 9701));
        Block(9701, "class 4:cast 754\ncast 754");
        Spell(754, 10, 20, (Damage, 0));

        Assert.Equal(RoomSpellDamage.EveryTick, Classify(9021));
    }

    // ----- no damage ---------------------------------------------------------

    [Fact]
    public void SummonOrMessageOnly_IsNone()
    {
        // Crypt summon 2: a roll table of messages and summons.
        Spell(5248, 0, 0, (TextBlock, 3411));
        Block(3411, "nomonsters:random 3412");
        Block(3412, "60:addevil 0\n85:message 4064\n90:summon 2111\n100:summon 2122\n");
        Spell(5001, 0, 0, (Summon, 480));

        Assert.Equal(RoomSpellDamage.None, Classify(5248, out string? gap));
        Assert.Null(gap);
        Assert.Equal(RoomSpellDamage.None, Classify(5001));
    }

    [Fact]
    public void MissingPiece_IsNone_AndSaidSo()
    {
        Spell(9022, 0, 0, (TextBlock, 9702));

        Assert.Equal(RoomSpellDamage.None, Classify(9022, out string? gap));
        Assert.Equal("textblock 9702 missing", gap);
    }

    [Fact]
    public void ChainThatLoops_Ends()
    {
        // Made up: two tables that roll each other.
        Spell(9023, 0, 0, (TextBlock, 9703));
        Block(9703, "50:random 9704\n100:message 1");
        Block(9704, "50:random 9703\n100:cast 754");
        Spell(754, 10, 20, (Damage, 0));

        Assert.Equal(RoomSpellDamage.OnARoll, Classify(9023));
    }
}

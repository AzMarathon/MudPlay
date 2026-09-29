using System.Text.Json;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using Xunit;

namespace MudPlay.Tests;

// HealLineReader: which lines are heals, who they healed, and by how much. The records
// mirror the shipped message seeds' wordings for these spells, and the formulas their
// Paradigm 1.9.1 Spells rows.
public sealed class HealLineReaderTests
{
    // minor healing #13: 2 + lvl/3 .. 8 + 2*lvl/3, obtained at 1, capped at 10.
    private static readonly HealSpell MinorHealing = Heal(13, "minor healing", 2,
        minBase: 2, minInc: 1, minIncLvls: 3, maxBase: 8, maxInc: 2, maxIncLvls: 3, reqLevel: 1, cap: 10);

    // greater healing rain #123: 15 + lvl/2 .. 35 + lvl, obtained at 24, capped at 46.
    private static readonly HealSpell GreaterHealingRain = Heal(123, "greater healing rain", 13,
        minBase: 15, minInc: 1, minIncLvls: 2, maxBase: 35, maxInc: 1, maxIncLvls: 1, reqLevel: 24, cap: 46);

    private static readonly HealSpell WhiteSatinGloves = Heal(161, "white satin gloves", 2,
        minBase: 8, minInc: 0, minIncLvls: 0, maxBase: 20, maxInc: 0, maxIncLvls: 0, reqLevel: 0, cap: 0);

    private static readonly HealSpell DivineHealing = Heal(1059, "divine healing", 2,
        minBase: 0, minInc: 3, minIncLvls: 1, maxBase: 0, maxInc: 5, maxIncLvls: 1, reqLevel: 50, cap: 70);

    private static readonly HealSpell AnnointedHands = Heal(744, "annointed hands", 0,
        minBase: 2, minInc: 0, minIncLvls: 0, maxBase: 0, maxInc: 1, maxIncLvls: 1, reqLevel: 10, cap: 15);

    private static readonly MessageRecord MinorHealingRecord = Record("minor healing", 13,
        caster: "You cast {spellname} on {target}, healing {damage} damage!",
        target: "{source} casts {spellname} on you!",
        witness: "{source} casts {spellname} on {target}!");

    private static readonly MessageRecord GreaterHealingRainRecord = Record("greater healing rain", 123,
        caster: "You cast {spellname} on your party, healing {damage} damage!",
        target: "{source} casts {spellname} on you, healing {damage} damage!",
        witness: "{source} casts {spellname} on the room!");

    private static readonly MessageRecord WhiteSatinGlovesRecord = Record("white satin gloves", 161,
        caster: "{target} is healed of {damage} damage!",
        target: "You are healed of {damage} damage!",
        witness: "{target} is healed of {damage} damage!");

    private static readonly MessageRecord DivineHealingRecord = Record("divine healing", 1059,
        caster: "{spellname} is healed of {damage} damage!",
        target: "You are healed of {damage} damage!",
        witness: "{source} is healed of {damage} damage!");

    private static readonly MessageRecord AnnointedHandsRecord = Record("annointed hands", 744,
        caster: "You lay hands on {target}.",
        target: "You are healed of {damage} damage!",
        witness: "{target} is healed of {damage} damage!");

    // A buff sharing the generic room line.
    private static readonly MessageRecord BlessRecord = Record("bless", 5,
        caster: "You cast {spellname} on {target}!",
        target: "{source} casts {spellname} on you!",
        witness: "{source} casts {spellname} on {target}!");

    private static HealLineReader Reader(params (HealSpell Spell, MessageRecord Record)[] pairs)
        => new(pairs.Select(p => p.Spell).ToList(), pairs.Select(p => p.Record).Append(BlessRecord));

    private static bool Read(HealLineReader reader, string line, out HealSeen heal, int casterLevel = 0)
        => reader.TryRead(line, (_, _) => casterLevel, out heal);

    [Fact]
    public void OurSingleHeal_UsesTheLinesAmount()
    {
        HealLineReader reader = Reader((MinorHealing, MinorHealingRecord));

        Assert.True(Read(reader, "You cast minor healing on Raijin, healing 12 damage!", out HealSeen heal));
        Assert.Equal("Raijin", heal.Target);
        Assert.Equal(12, heal.Amount);
        Assert.True(heal.AmountShown);
        Assert.True(heal.Ours);
        Assert.Equal(13, heal.SpellNumber);
    }

    [Fact]
    public void RoomLine_AveragesAtTheCastersLevel()
    {
        HealLineReader reader = Reader((MinorHealing, MinorHealingRecord));

        // Level 5: 2 + 5/3 = 3 .. 8 + 10/3 = 11 → 7.
        Assert.True(Read(reader, "Raijin casts minor healing on Bob!", out HealSeen heal, casterLevel: 5));
        Assert.Equal("Bob", heal.Target);
        Assert.Equal("Raijin", heal.Caster);
        Assert.False(heal.Ours);
        Assert.False(heal.AmountShown);
        Assert.Equal(7, heal.Amount);
    }

    [Fact]
    public void RoomLine_UnknownCasterLevel_AveragesAtTheSpellsLowestLevel()
    {
        HealLineReader reader = Reader((MinorHealing, MinorHealingRecord));

        // Level 0 clamps to the obtain level 1: 2 .. 8 → 5.
        Assert.True(Read(reader, "Stranger casts minor healing on Bob!", out HealSeen heal, casterLevel: 0));
        Assert.Equal(5, heal.Amount);
    }

    [Fact]
    public void OtherSpell_OnTheSharedRoomLine_IsNotAHeal()
    {
        HealLineReader reader = Reader((MinorHealing, MinorHealingRecord));

        Assert.False(Read(reader, "Raijin casts bless on Bob!", out _));
        Assert.False(Read(reader, "You cast bless on Bob!", out _));
    }

    [Fact]
    public void OurPartyHeal_HealsTheWholePartyByTheLinesAmount()
    {
        HealLineReader reader = Reader((GreaterHealingRain, GreaterHealingRainRecord));

        Assert.True(Read(reader, "You cast greater healing rain on your party, healing 40 damage!", out HealSeen heal));
        Assert.Null(heal.Target);
        Assert.Equal(40, heal.Amount);
        Assert.True(heal.Ours);
    }

    [Fact]
    public void PartyHeal_RoomLine_AveragesAndNamesTheCaster()
    {
        HealLineReader reader = Reader((GreaterHealingRain, GreaterHealingRainRecord));

        // Level 30: 15 + 15 = 30 .. 35 + 30 = 65 → 47.
        Assert.True(Read(reader, "Raijin casts greater healing rain on the room!", out HealSeen heal, casterLevel: 30));
        Assert.Null(heal.Target);
        Assert.Equal("Raijin", heal.Caster);
        Assert.Equal(47, heal.Amount);
        Assert.False(heal.ViewedAsMember);
    }

    [Fact]
    public void PartyHeal_TheMembersOwnLine_CarriesTheAmountForEveryone()
    {
        HealLineReader reader = Reader((GreaterHealingRain, GreaterHealingRainRecord));

        Assert.True(Read(reader, "Raijin casts greater healing rain on you, healing 38 damage!", out HealSeen heal));
        Assert.Null(heal.Target);
        Assert.Equal(38, heal.Amount);
        Assert.True(heal.ViewedAsMember);
    }

    [Fact]
    public void IsHealedOf_NamesTheHealedPlayer()
    {
        HealLineReader reader = Reader((WhiteSatinGloves, WhiteSatinGlovesRecord));

        Assert.True(Read(reader, "Bob is healed of 14 damage!", out HealSeen heal));
        Assert.Equal("Bob", heal.Target);
        Assert.Equal(14, heal.Amount);
        Assert.False(heal.Ours);
    }

    [Fact]
    public void LoneNameCapture_IsTheHealedPlayer_WhateverTheTemplateCallsIt()
    {
        HealLineReader reader = Reader((DivineHealing, DivineHealingRecord));

        Assert.True(Read(reader, "Bob is healed of 120 damage!", out HealSeen heal));
        Assert.Equal("Bob", heal.Target);
        Assert.Equal(120, heal.Amount);
    }

    [Fact]
    public void UnnumberedLine_SharedWithAnotherRecord_IsNotRead()
    {
        MessageRecord sameWording = Record("laying on of hands", 9000,
            caster: "You lay hands on {target}.", target: "", witness: "");
        HealLineReader shared = new(new[] { AnnointedHands }, new[] { AnnointedHandsRecord, sameWording });
        HealLineReader alone = new(new[] { AnnointedHands }, new[] { AnnointedHandsRecord });

        Assert.False(shared.TryRead("You lay hands on Bob.", (_, _) => 12, out _));
        Assert.True(alone.TryRead("You lay hands on Bob.", (_, _) => 12, out HealSeen heal));
        Assert.True(heal.Ours);
        Assert.Equal(12, heal.CasterLevel);
    }

    [Fact]
    public void UnrelatedLine_ReadsNothing()
    {
        HealLineReader reader = Reader((MinorHealing, MinorHealingRecord), (GreaterHealingRain, GreaterHealingRainRecord));

        Assert.False(Read(reader, "The goblin bites Raijin for 7 damage!", out _));
        Assert.False(Read(reader, "Raijin just arrived from the north.", out _));
    }

    [Fact]
    public void InstantHeals_KeepsInstantPartyAndTargetedHeals_Only()
    {
        using JsonDocument doc = JsonDocument.Parse("""
            [
              { "Number": 13,   "Name": "minor healing",  "Targets": 2,  "Dur": 0,  "Abil-0": 18, "Abil-1": 108 },
              { "Number": 123,  "Name": "greater healing rain", "Targets": 13, "Dur": 0, "Abil-0": 18 },
              { "Number": 50,   "Name": "red potion",     "Targets": 1,  "Dur": 0,  "Abil-0": 18 },
              { "Number": 349,  "Name": "regeneration",   "Targets": 2,  "Dur": 12, "Abil-0": 18 },
              { "Number": 1061, "Name": "dark transfusion", "Targets": 0, "Dur": 0, "Abil-0": 8, "Abil-3": 18, "AbilVal-3": 200 },
              { "Number": 5,    "Name": "bless",          "Targets": 2,  "Dur": 20, "Abil-0": 22 }
            ]
            """);

        IReadOnlyList<HealSpell> heals = HealLineReader.InstantHeals(doc.RootElement);

        Assert.Equal(new[] { 13, 123 }, heals.Select(h => h.Number));
    }

    private static HealSpell Heal(int number, string name, int targets,
        int minBase, int minInc, int minIncLvls, int maxBase, int maxInc, int maxIncLvls, int reqLevel, int cap)
        => new(number, name, targets, new SpellFormulaInput
        {
            Number = number,
            MinBase = minBase, MinInc = minInc, MinIncLVLs = minIncLvls,
            MaxBase = maxBase, MaxInc = maxInc, MaxIncLVLs = maxIncLvls,
            ReqLevel = reqLevel, Cap = cap, EnergyCost = 500,
            Abilities = new[] { new SpellAbility(18, 0) },
        });

    private static MessageRecord Record(string name, int spell, string caster, string target, string witness)
        => new(
            Id: MessageRecord.ComputeId(name, caster, target, witness, "", ""),
            Name: name,
            Flags: MessageFlags.None,
            RawFlagsHex: 0,
            CasterMessage: caster,
            TargetMessage: target,
            WitnessMessage: witness,
            AppliedMessage: "",
            AppliedEndsWith: "",
            Links: new[] { new GameDataLink("Spells", spell) });
}

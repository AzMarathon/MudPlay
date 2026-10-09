using System;
using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using Xunit;

namespace MudPlay.Tests;

// Working the trained stats back from a `stat` screen: equipment, the effects the
// screen listed, and the rule that a marked stat nothing explains is never used.
// Arrays are STR/INT/WIL/AGL/HEA/CHM.
public sealed class UnmodifiedStatResolverTests
{
    private static readonly int[] NoGear = new int[6];

    private static int[] Stats(int str, int agl) => new[] { str, 40, 30, agl, 60, 30 };

    private static EffectStatReading Spell(string name, int str = 0, int agl = 0, StatSet unknown = StatSet.None) =>
        new(name, new[] { str, 0, 0, agl, 0, 0 }, unknown);

    private static ListedEffect Effect(string text, params EffectStatReading[] readings) => new(text, readings);

    private static readonly ListedEffect Unrelated = Effect("You feel powerful!", Spell("bless"));

    [Fact]
    public void EquipmentOnly_ComesOffAnUnmarkedStat()
    {
        UnmodifiedStats r = UnmodifiedStatResolver.Resolve(
            Stats(143, 100), new[] { 10, 0, 0, 0, 0, 0 }, marksRead: true, StatSet.None, Array.Empty<ListedEffect>());

        Assert.Equal(StatReadingState.Accounted, r.State);
        Assert.Equal(133, r.Base[0]);
        Assert.Equal(100, r.Base[3]);
    }

    [Fact]
    public void EquipmentOnly_ExplainsAMarkedStat()
    {
        UnmodifiedStats r = UnmodifiedStatResolver.Resolve(
            Stats(143, 100), new[] { 10, 0, 0, 0, 0, 0 }, marksRead: true, StatSet.Strength, new[] { Unrelated });

        Assert.Equal(StatReadingState.Accounted, r.State);
        Assert.Equal(133, r.Base[0]);
        Assert.Equal(0, r.Effects[0]);
    }

    [Fact]
    public void BuffOnly_TheReportedScreen_GivesTheTrainersValues()
    {
        // Strength 153 and Agility 90 in red under "You feel strong, but clumsy!";
        // the trainer showed 133 and 100.
        UnmodifiedStats r = UnmodifiedStatResolver.Resolve(
            Stats(153, 90), NoGear, marksRead: true, StatSet.Strength | StatSet.Agility,
            new[] { Unrelated, Effect("You feel strong, but clumsy!", Spell("way of the bear", str: 20, agl: -10)) });

        Assert.Equal(StatReadingState.Accounted, r.State);
        Assert.Equal(new[] { 133, 40, 30, 100, 60, 30 }, r.Base);
        Assert.Equal(20, r.Effects[0]);
        Assert.Equal(-10, r.Effects[3]);
        Assert.Contains("way of the bear", r.Describe());
    }

    [Fact]
    public void BuffAndEquipment_BothComeOff()
    {
        UnmodifiedStats r = UnmodifiedStatResolver.Resolve(
            Stats(163, 100), new[] { 10, 0, 0, 0, 0, 0 }, marksRead: true, StatSet.Strength,
            new[] { Effect("You feel strong, but clumsy!", Spell("way of the bear", str: 20)) });

        Assert.Equal(StatReadingState.Accounted, r.State);
        Assert.Equal(133, r.Base[0]);
        Assert.Equal(10, r.Equipment[0]);
        Assert.Equal(20, r.Effects[0]);
    }

    [Fact]
    public void MarkedStat_NothingExplains_IsUnexplained()
    {
        UnmodifiedStats r = UnmodifiedStatResolver.Resolve(
            Stats(153, 100), NoGear, marksRead: true, StatSet.Strength, new[] { Unrelated });

        Assert.Equal(StatReadingState.Unexplained, r.State);
        Assert.Equal(StatSet.Strength, r.Unexplained);
        Assert.Contains("STR can't be accounted for", r.Describe());
    }

    [Fact]
    public void AnEffectLineNoSpellMatches_LeavesEveryMarkedStatUnexplained()
    {
        // The bear explains Strength, but an unknown effect is on the list too:
        // nothing says it isn't adding to Strength as well.
        UnmodifiedStats r = UnmodifiedStatResolver.Resolve(
            Stats(153, 100), NoGear, marksRead: true, StatSet.Strength,
            new[] { Effect("You feel strong, but clumsy!", Spell("way of the bear", str: 20)), Effect("You feel odd!") });

        Assert.Equal(StatReadingState.Unexplained, r.State);
        Assert.Contains("matches no spell on record", r.Describe());
    }

    [Fact]
    public void AModifierTheDataCannotPinDown_IsUnexplained()
    {
        UnmodifiedStats r = UnmodifiedStatResolver.Resolve(
            Stats(153, 100), NoGear, marksRead: true, StatSet.Strength,
            new[] { Effect("Your strength is enhanced!", Spell("giant strength", unknown: StatSet.Strength)) });

        Assert.Equal(StatReadingState.Unexplained, r.State);
    }

    [Fact]
    public void ASharedLine_IsSettledByTheMark()
    {
        // "You feel strong" is song of might (+5) or song of force (nothing). A
        // marked Strength with nothing else on it can only be the first.
        ListedEffect shared = Effect("You feel strong!", Spell("song of might", str: 5), Spell("song of force"));

        UnmodifiedStats marked = UnmodifiedStatResolver.Resolve(
            Stats(138, 100), NoGear, marksRead: true, StatSet.Strength, new[] { shared });
        Assert.Equal(StatReadingState.Accounted, marked.State);
        Assert.Equal(133, marked.Base[0]);

        // Unmarked, it is the other one, and nothing comes off.
        UnmodifiedStats unmarked = UnmodifiedStatResolver.Resolve(
            Stats(133, 100), NoGear, marksRead: true, StatSet.None, new[] { shared });
        Assert.Equal(133, unmarked.Base[0]);
    }

    [Fact]
    public void TwoTotalsThatBothFit_AreUnexplained()
    {
        // Gear already explains the mark, so the shared line could be either spell.
        UnmodifiedStats r = UnmodifiedStatResolver.Resolve(
            Stats(148, 100), new[] { 10, 0, 0, 0, 0, 0 }, marksRead: true, StatSet.Strength,
            new[] { Effect("You feel strong!", Spell("song of might", str: 5), Spell("song of force")) });

        Assert.Equal(StatReadingState.Unexplained, r.State);
    }

    [Fact]
    public void WithoutMarks_OnlyGearComesOff_AndTheReadingIsUnverified()
    {
        UnmodifiedStats r = UnmodifiedStatResolver.Resolve(
            Stats(153, 90), new[] { 10, 0, 0, 0, 0, 0 }, marksRead: false, StatSet.None, Array.Empty<ListedEffect>());

        Assert.Equal(StatReadingState.Unverified, r.State);
        Assert.Equal(143, r.Base[0]);
        Assert.Null(r.Describe());
    }

    // ----- reading the effect list against the catalogue ---------------------

    private static MessageRecord Record(string name, string applied, params int[] spells)
    {
        var links = new List<GameDataLink>();
        foreach (int n in spells) links.Add(new GameDataLink("Spells", n));
        return new MessageRecord(
            Id: name, Name: name, Flags: MessageFlags.None, RawFlagsHex: 0, CasterMessage: string.Empty,
            TargetMessage: string.Empty, WitnessMessage: string.Empty, AppliedMessage: applied,
            AppliedEndsWith: string.Empty, Links: links);
    }

    private static readonly Dictionary<int, string> SpellRows = new()
    {
        // way of the bear: fixed Strength +20, Agility -10.
        [298] = """{"Number":298,"Name":"way of the bear","MinBase":20,"MaxBase":20,"Abil-0":46,"AbilVal-0":20,"Abil-1":48,"AbilVal-1":-10}""",
        // song of might: a zero ability value takes the spell's one fixed magnitude.
        [47] = """{"Number":47,"Name":"song of might","MinBase":5,"MaxBase":5,"Abil-0":46,"AbilVal-0":0}""",
        [1106] = """{"Number":1106,"Name":"song of force","MinBase":5,"MaxBase":5,"Abil-0":4,"AbilVal-0":0}""",
        // vile oil: a rolled range.
        [533] = """{"Number":533,"Name":"vile oil","MinBase":-10,"MaxBase":-5,"Abil-0":46,"AbilVal-0":0}""",
        // giant strength: grows with the caster's level.
        [5574] = """{"Number":5574,"Name":"giant strength","MinBase":0,"MaxBase":0,"MinInc":10,"MinIncLVLs":45,"MaxInc":10,"MaxIncLVLs":45,"Abil-0":46,"AbilVal-0":0}""",
    };

    private static JsonElement? SpellRow(int number) =>
        SpellRows.TryGetValue(number, out string? json) ? JsonDocument.Parse(json).RootElement.Clone() : null;

    private static readonly MessageRecord[] Catalogue =
    {
        Record("way of the bear", "You feel strong, but clumsy", 298),
        Record("song of might", "You feel strong", 47),
        Record("song of force", "You feel strong", 1106),
        Record("vile oil", "You are nauseated", 533),
        Record("giant strength", "Your strength is enhanced", 5574),
    };

    private static IReadOnlyList<ListedEffect> Read(RealmType realm, params StatusEffectLine[] lines) =>
        ListedEffectReader.Read(lines, realm, Catalogue, SpellRow);

    [Fact]
    public void Reader_MatchesTheWholeAppliedText_WhateverItsPunctuation()
    {
        IReadOnlyList<ListedEffect> effects = Read(RealmType.ParaMud,
            new StatusEffectLine("You feel strong, but clumsy!", true));

        // Not "You feel strong", which the line merely starts with.
        EffectStatReading bear = Assert.Single(Assert.Single(effects).Readings);
        Assert.Equal("way of the bear", bear.Spell);
        Assert.Equal(new[] { 20, 0, 0, -10, 0, 0 }, bear.Modifiers);
        Assert.Equal(StatSet.None, bear.Unknown);
    }

    [Fact]
    public void Reader_KeepsEverySpellThatSharesALine()
    {
        ListedEffect effect = Assert.Single(Read(RealmType.ParaMud, new StatusEffectLine("You feel strong!", true)));

        Assert.Equal(2, effect.Readings.Count);
        Assert.Equal(5, effect.Readings[0].Modifiers[0]);   // song of might
        Assert.Equal(0, effect.Readings[1].Modifiers[0]);   // song of force
    }

    [Fact]
    public void Reader_ARangeOrALevelScaledModifier_IsUnknown()
    {
        IReadOnlyList<ListedEffect> effects = Read(RealmType.ParaMud,
            new StatusEffectLine("You are nauseated!", true), new StatusEffectLine("Your strength is enhanced!", true));

        Assert.Equal(StatSet.Strength, effects[0].Readings[0].Unknown);
        Assert.Equal(StatSet.Strength, effects[1].Readings[0].Unknown);
    }

    [Fact]
    public void Reader_AnUnknownLine_HasNoReadings()
    {
        Assert.Empty(Assert.Single(Read(RealmType.ParaMud, new StatusEffectLine("You feel odd!", true))).Readings);
    }

    [Fact]
    public void Reader_OnParadigm_SkipsLinesWithoutACountdown_OnStockTakesThem()
    {
        var stray = new StatusEffectLine("Someone just left the Realm.", false);

        Assert.Empty(Read(RealmType.ParaMud, stray));
        Assert.Single(Read(RealmType.Stock, stray));
    }

    [Fact]
    public void TheReportedScreen_EndToEnd_ResolvesToTheTrainersValues()
    {
        IReadOnlyList<ListedEffect> effects = Read(RealmType.ParaMud,
            new StatusEffectLine("You feel strong, but clumsy!", true));

        UnmodifiedStats r = UnmodifiedStatResolver.Resolve(
            Stats(153, 90), NoGear, marksRead: true, StatSet.Strength | StatSet.Agility, effects);

        Assert.Equal(StatReadingState.Accounted, r.State);
        Assert.Equal(133, r.Base[0]);
        Assert.Equal(100, r.Base[3]);
    }
}

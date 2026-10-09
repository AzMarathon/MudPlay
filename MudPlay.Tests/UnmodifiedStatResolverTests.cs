using System;
using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using Xunit;

namespace MudPlay.Tests;

// Working the trained stats back from a `stat` screen: what the screen's own
// modified mark says, worn gear, the effects the screen listed, and the rule that a
// marked stat nothing explains is never used. Arrays are STR/INT/WIL/AGL/HEA/CHM.
public sealed class UnmodifiedStatResolverTests
{
    private static readonly int[] NoGear = new int[6];
    private static readonly int[] StrGear = { 10, 0, 0, 0, 0, 0 };

    private static int[] Stats(int str, int agl) => new[] { str, 40, 30, agl, 60, 30 };

    private static EffectStatReading Spell(string name, int str = 0, int agl = 0, StatSet unknown = StatSet.None) =>
        new(name, new[] { str, 0, 0, agl, 0, 0 }, unknown);

    private static ListedEffect Effect(string text, params EffectStatReading[] readings) => new(text, readings);

    private static readonly ListedEffect Unrelated = Effect("You feel powerful!", Spell("bless"));
    private static readonly ListedEffect Bear =
        Effect("You feel strong, but clumsy!", Spell("way of the bear", str: 20, agl: -10));

    private static UnmodifiedStats Paradigm(int[] shown, int[] gear, StatSet marked, params ListedEffect[] effects) =>
        UnmodifiedStatResolver.Resolve(shown, gear, RealmType.ParaMud, marksRead: true, marked, effects);

    private static UnmodifiedStats Stock(int[] shown, int[] gear, StatSet marked, params ListedEffect[] effects) =>
        UnmodifiedStatResolver.Resolve(shown, gear, RealmType.Stock, marksRead: true, marked, effects);

    [Fact]
    public void AnUnmarkedStat_IsTheTrainedValue_GearOrNoGear()
    {
        // Paradigm turns a gear-boosted stat red and Stock gear never moves the
        // number, so an unmarked one has nothing in it on either realm.
        Assert.Equal(133, Paradigm(Stats(133, 100), StrGear, StatSet.None).Base[0]);
        Assert.Equal(133, Stock(Stats(133, 100), StrGear, StatSet.None).Base[0]);
        Assert.Equal(StatReadingState.Accounted, Paradigm(Stats(133, 100), StrGear, StatSet.None).State);
    }

    [Fact]
    public void Paradigm_EquipmentOnly_ExplainsAMarkedStat_AndSaysNothingOfIt()
    {
        UnmodifiedStats r = Paradigm(Stats(143, 100), StrGear, StatSet.Strength, Unrelated);

        Assert.Equal(StatReadingState.Accounted, r.State);
        Assert.Equal(133, r.Base[0]);
        Assert.Equal(10, r.Equipment[0]);
        Assert.Equal(0, r.Effects[0]);
        // A character with stat gear reads like this on every screen.
        Assert.True(r.GearAlone);
        Assert.Null(r.Describe());
    }

    [Fact]
    public void Stock_GearNeverExplainsAMark()
    {
        // Stock gear doesn't move the shown value, so a mark is a spell's doing.
        UnmodifiedStats r = Stock(Stats(143, 100), StrGear, StatSet.Strength, Unrelated);

        Assert.Equal(StatReadingState.Unexplained, r.State);
        Assert.Equal(StatSet.Strength, r.Unexplained);
    }

    [Fact]
    public void Stock_ABuffComesOff_WithoutTheGear()
    {
        UnmodifiedStats r = Stock(Stats(153, 90), StrGear, StatSet.Strength | StatSet.Agility, Bear);

        Assert.Equal(StatReadingState.Accounted, r.State);
        Assert.Equal(133, r.Base[0]);
        Assert.Equal(0, r.Equipment[0]);
    }

    [Fact]
    public void BuffOnly_TheReportedScreen_GivesTheTrainersValues()
    {
        // Strength 153 and Agility 90 in red under "You feel strong, but clumsy!";
        // the trainer showed 133 and 100.
        UnmodifiedStats r = Paradigm(Stats(153, 90), NoGear, StatSet.Strength | StatSet.Agility, Unrelated, Bear);

        Assert.Equal(StatReadingState.Accounted, r.State);
        Assert.Equal(new[] { 133, 40, 30, 100, 60, 30 }, r.Base);
        Assert.Equal(20, r.Effects[0]);
        Assert.Equal(-10, r.Effects[3]);
        Assert.Contains("way of the bear", r.Describe());
    }

    [Fact]
    public void Paradigm_BuffAndEquipment_BothComeOff_AndBothAreNamed()
    {
        UnmodifiedStats r = Paradigm(Stats(163, 100), StrGear, StatSet.Strength,
            Effect("You feel strong, but clumsy!", Spell("way of the bear", str: 20)));

        Assert.Equal(StatReadingState.Accounted, r.State);
        Assert.Equal(133, r.Base[0]);
        Assert.Equal(10, r.Equipment[0]);
        Assert.Equal(20, r.Effects[0]);
        Assert.Contains("worn gear: STR +10", r.Describe());
        Assert.Contains("effects: STR +20", r.Describe());
    }

    [Fact]
    public void MarkedStat_NothingExplains_IsUnexplained_AndIsLeftAsShown()
    {
        UnmodifiedStats r = Paradigm(Stats(153, 100), NoGear, StatSet.Strength, Unrelated);

        Assert.Equal(StatReadingState.Unexplained, r.State);
        Assert.Equal(StatSet.Strength, r.Unexplained);
        Assert.Equal(153, r.Base[0]);
        Assert.Contains("STR can't be accounted for", r.Describe());
    }

    [Fact]
    public void AnEffectLineNoSpellMatches_LeavesEveryMarkedStatUnexplained()
    {
        // The bear explains Strength, but an unknown effect is on the list too:
        // nothing says it isn't adding to Strength as well.
        UnmodifiedStats r = Paradigm(Stats(153, 100), NoGear, StatSet.Strength,
            Effect("You feel strong, but clumsy!", Spell("way of the bear", str: 20)), Effect("You feel odd!"));

        Assert.Equal(StatReadingState.Unexplained, r.State);
        Assert.Contains("matches no spell on record", r.Describe());
    }

    [Fact]
    public void AModifierTheDataCannotPinDown_IsUnexplained()
    {
        UnmodifiedStats r = Paradigm(Stats(153, 100), NoGear, StatSet.Strength,
            Effect("Your strength is enhanced!", Spell("giant strength", unknown: StatSet.Strength)));

        Assert.Equal(StatReadingState.Unexplained, r.State);
    }

    [Fact]
    public void ASharedLine_IsSettledByTheMark()
    {
        // "You feel strong" is song of might (+5) or song of force (nothing). A
        // reading that puts nothing on Strength can't be the marked one.
        ListedEffect shared = Effect("You feel strong!", Spell("song of might", str: 5), Spell("song of force"));

        UnmodifiedStats marked = Paradigm(Stats(138, 100), NoGear, StatSet.Strength, shared);
        Assert.Equal(StatReadingState.Accounted, marked.State);
        Assert.Equal(133, marked.Base[0]);

        // Unmarked, the shown value stands.
        Assert.Equal(133, Paradigm(Stats(133, 100), NoGear, StatSet.None, shared).Base[0]);
    }

    [Fact]
    public void TwoTotalsThatBothFit_AreUnexplained()
    {
        // Gear already explains the mark, so the shared line could be either spell.
        UnmodifiedStats r = Paradigm(Stats(148, 100), StrGear, StatSet.Strength,
            Effect("You feel strong!", Spell("song of might", str: 5), Spell("song of force")));

        Assert.Equal(StatReadingState.Unexplained, r.State);
    }

    [Fact]
    public void ModifiersThatCancel_AreInDoubtOnParadigm_AndRuledOutOnStock()
    {
        // The line is one of two spells: +5, or -5 (which would cancel the other +5).
        ListedEffect up = Effect("You feel strong!", Spell("song of might", str: 5));
        ListedEffect either = Effect("You feel odd!", Spell("a second song", str: 5), Spell("a curse", str: -5));

        // Stock marks a stat only when it differs from the trained value, so the
        // cancelling reading can't be the marked one: +10 it is.
        UnmodifiedStats stock = Stock(Stats(143, 100), NoGear, StatSet.Strength, up, either);
        Assert.Equal(StatReadingState.Accounted, stock.State);
        Assert.Equal(133, stock.Base[0]);

        // Whether Paradigm leaves a cancelled stat unmarked isn't known.
        Assert.Equal(StatReadingState.Unexplained, Paradigm(Stats(143, 100), NoGear, StatSet.Strength, up, either).State);
    }

    [Fact]
    public void WithoutMarks_GearComesOffAsItAlwaysDid_AndTheReadingIsUnverified()
    {
        UnmodifiedStats r = UnmodifiedStatResolver.Resolve(
            Stats(153, 90), StrGear, RealmType.ParaMud, marksRead: false, StatSet.None, Array.Empty<ListedEffect>());

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
    public void Reader_ASourceThatLeadsToNoSpellRow_IsKeptAsAnUnknownReading()
    {
        // The line is song of might, or a spell missing from the game data, or
        // whatever an item record stands for: dropping the last two would leave
        // song of might looking like the only answer.
        MessageRecord[] catalogue =
        {
            Record("song of might", "You feel strong", 47),
            Record("a spell not imported", "You feel strong", 9999),
            new MessageRecord(
                Id: "belt", Name: "belt of might", Flags: MessageFlags.None, RawFlagsHex: 0,
                CasterMessage: string.Empty, TargetMessage: string.Empty, WitnessMessage: string.Empty,
                AppliedMessage: "You feel strong", AppliedEndsWith: string.Empty,
                Links: new[] { new GameDataLink("Items", 438) }),
        };

        ListedEffect effect = Assert.Single(ListedEffectReader.Read(
            new[] { new StatusEffectLine("You feel strong!", true) }, RealmType.ParaMud, catalogue, SpellRow));

        Assert.Equal(3, effect.Readings.Count);
        Assert.Equal(StatSet.None, effect.Readings[0].Unknown);
        Assert.Equal(StatSet.All, effect.Readings[1].Unknown);
        Assert.Equal(StatSet.All, effect.Readings[2].Unknown);
        Assert.Equal(StatReadingState.Unexplained,
            Paradigm(Stats(138, 100), NoGear, StatSet.Strength, effect).State);
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
            Stats(153, 90), NoGear, RealmType.ParaMud, marksRead: true, StatSet.Strength | StatSet.Agility, effects);

        Assert.Equal(StatReadingState.Accounted, r.State);
        Assert.Equal(133, r.Base[0]);
        Assert.Equal(100, r.Base[3]);
    }
}

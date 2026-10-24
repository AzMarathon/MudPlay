using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game.Conditions;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The longest an ailment of ours can last, read off the shipped Paradigm records and
// their spells' rows as the game data has them (data-Paradigm-1.9.1): the time
// ConditionTracker gives one whose wear-off never comes. A spell's duration grows
// with the caster's level, and ending one early would drop a real ailment, so it is
// the longest any caster could make it (review of report paradigm-20261022-164244).
public sealed class AilmentDurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mudplay-ailment-duration-" + Path.GetRandomFileName());
    private readonly List<MessageRecord> _seed;

    public AilmentDurationTests()
    {
        AppPaths.ExtractEmbeddedSeeds(_dir);
        _seed = JsonStore.Load<List<MessageRecord>>(Path.Combine(_dir, "Messages.paradigm.seed.json")) ?? new();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    // Banish (#435) and hypnotic hands (#300) as data-Paradigm-1.9.1 has them, and
    // the same hypnotic hands with no cap, for the uncapped case.
    private Func<int, SpellFormulaInput?> Spells(bool hypnoticHandsCapped = true)
    {
        string set = Path.Combine(_dir, "data");
        Directory.CreateDirectory(Path.Combine(set, "para"));
        string cap = hypnoticHandsCapped ? "30" : "0";
        File.WriteAllText(Path.Combine(set, "para", "Spells.json"), $$"""
            [
              { "Number": 435, "Name": "banish", "Dur": 20, "DurInc": 0, "DurIncLVLs": 0, "Cap": 0, "ReqLevel": 40 },
              { "Number": 300, "Name": "hypnotic hands", "Dur": 7, "DurInc": 1, "DurIncLVLs": 10, "Cap": {{cap}}, "ReqLevel": 21 }
            ]
            """);
        GameDataCache cache = new(set);
        cache.SwitchSet("para");
        Dictionary<int, SpellFormulaInput> formulas = new();
        foreach (GameRow row in cache.GetTable("Spells")!.Rows)
            if (row.TryGetProperty("Number", out GameValue n) && n.TryGetInt32(out int number))
                formulas[number] = SpellFormulaReader.Read(row);
        return number => formulas.TryGetValue(number, out SpellFormulaInput f) ? f : null;
    }

    private MessageRecord Record(string name) => _seed.Find(r => r.Name == name)
        ?? throw new InvalidOperationException(name);

    [Fact]
    public void ASpellThatDoesntGrow_LastsItsBaseDuration()
    {
        double? secs = AilmentDuration.LongestSeconds(Record("banish"), Spells(), _ => 0);

        Assert.Equal(20 * SpellCalculator.SpellRoundSecondsWallClock, secs);
    }

    // Hypnotic hands grows a round every ten levels up to level 30: 7 + 3 rounds.
    [Fact]
    public void ACappedSpell_LastsWhatItReachesAtItsCap()
    {
        double? secs = AilmentDuration.LongestSeconds(Record("hypnotic hands"), Spells(), _ => 0);

        Assert.Equal(10 * SpellCalculator.SpellRoundSecondsWallClock, secs);
    }

    // With no cap, the highest level a monster of the set casts it at: 40, so 7 + 4.
    [Fact]
    public void AnUncappedSpell_LastsWhatItsHighestCasterMakesIt()
    {
        double? secs = AilmentDuration.LongestSeconds(
            Record("hypnotic hands"), Spells(hypnoticHandsCapped: false), number => number == 300 ? 40 : 0);

        Assert.Equal(11 * SpellCalculator.SpellRoundSecondsWallClock, secs);
    }

    // A record no spell of the data gives a duration to: nothing to go by, and the
    // tracker's cap applies.
    [Fact]
    public void ARecordWithNoSpellInTheData_HasNoDuration()
    {
        Assert.Null(AilmentDuration.LongestSeconds(Record("stinking cloud"), Spells(), _ => 0));
    }
}

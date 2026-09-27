using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MudPlay.Game.Inventory;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Events;

// Decides whether a State event's conditions hold, and renders them for the event
// list. Pure: the caller supplies the character's current readings.
public static class EventConditionEvaluator
{
    // The character's current readings. A null reading is unknown (the inventory
    // hasn't been read yet, the stat screen hasn't landed), and a condition on an
    // unknown reading never holds — an event must not fire on data we don't have.
    public readonly record struct Readings(long? Copper, int? EncumbrancePercent, long? Experience, int? Level);

    // True when every condition holds. An event with no conditions never fires.
    public static bool AllHold(IReadOnlyList<EventCondition>? conditions, Readings now)
    {
        if (conditions is not { Count: > 0 }) return false;
        foreach (EventCondition c in conditions)
            if (!Holds(c, now)) return false;
        return true;
    }

    public static bool Holds(EventCondition c, Readings now)
    {
        ArgumentNullException.ThrowIfNull(c);
        long? actual = c.Stat switch
        {
            EventConditionStat.Money => now.Copper,
            EventConditionStat.Encumbrance => now.EncumbrancePercent,
            EventConditionStat.Experience => now.Experience,
            EventConditionStat.Level => now.Level,
            _ => null,
        };
        if (actual is not { } a) return false;
        long target = c.Stat == EventConditionStat.Money
            ? c.Value * CurrencyHoldings.CopperUnit(c.Denomination)
            : c.Value;
        return c.Comparison switch
        {
            EventComparison.AtLeast => a >= target,
            EventComparison.AtMost => a <= target,
            EventComparison.Above => a > target,
            EventComparison.Below => a < target,
            EventComparison.Equal => a == target,
            EventComparison.NotEqual => a != target,
            _ => false,
        };
    }

    // "money ≥ 5 platinum and encumbrance ≥ 60%".
    public static string Describe(IReadOnlyList<EventCondition>? conditions) =>
        conditions is { Count: > 0 }
            ? string.Join(" and ", conditions.Select(Describe))
            : "(no conditions)";

    public static string Describe(EventCondition c)
    {
        string value = c.Value.ToString("N0", CultureInfo.InvariantCulture);
        return c.Stat switch
        {
            EventConditionStat.Money => $"money {Symbol(c.Comparison)} {value} {c.Denomination.ToString().ToLowerInvariant()}",
            EventConditionStat.Encumbrance => $"encumbrance {Symbol(c.Comparison)} {value}%",
            EventConditionStat.Experience => $"exp {Symbol(c.Comparison)} {value}",
            EventConditionStat.Level => $"level {Symbol(c.Comparison)} {value}",
            _ => "?",
        };
    }

    public static string Symbol(EventComparison comparison) => comparison switch
    {
        EventComparison.AtLeast => "≥",
        EventComparison.AtMost => "≤",
        EventComparison.Above => ">",
        EventComparison.Below => "<",
        EventComparison.Equal => "=",
        EventComparison.NotEqual => "≠",
        _ => "?",
    };
}

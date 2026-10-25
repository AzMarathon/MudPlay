using System.Text.Json;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// One per-spell value (target class, damage element, learn level) for every Spells row,
// resolved for a cast-code the way the game resolves it for the character typing it.
//
// A Short cast-code can name SEVERAL rows, and not only item / monster variants: two
// classes' learnable spells can share one. On Anomaly "blaz" is the Druid's blaze (#290,
// fire, hits anything) AND the Priest's blazing light (#5585, no element, undead only). A
// Druid's `blaz` casts blaze, so judging it by every row with that Short made it undead-only
// and the chooser fell through to the alternate spell against a living manscorpion shaman.
//
// So the lookup asks, in order:
//   1. The caster's own spell — the row the character's spell book holds for the code
//      (SpellbookState.FindByCastCode: the class's learnable list, alignment-filtered,
//      a learned match first), by Spells.Number.
//   2. The learnable rows, when they all agree. When they disagree and we can't tell which
//      the character casts, the code is unknown (each index fails open).
//   3. Only when no row is learnable, the item / monster variants — each index's own rule
//      (last writer, or the last restricted row for the target class).
public sealed class CastCodeRows<T> where T : struct
{
    private readonly Dictionary<int, T> _byNumber = new();
    private readonly Dictionary<string, T> _learnable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _learnableSplit = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, T> _unlearnable = new(StringComparer.OrdinalIgnoreCase);

    // Record a row's value. keepUnlearnable = false leaves a non-learnable row out of the
    // step-3 fallback (it still answers step 1 by its Number).
    public void Add(GameRow row, string code, T value, bool keepUnlearnable = true)
    {
        string key = code.Trim();
        if (row.TryGetProperty("Number", out GameValue numEl)
            && numEl.ValueKind == JsonValueKind.Number && numEl.TryGetInt32(out int number))
            _byNumber[number] = value;

        if (IsLearnable(row))
        {
            if (_learnable.TryGetValue(key, out T seen) && !EqualityComparer<T>.Default.Equals(seen, value))
                _learnableSplit.Add(key);
            _learnable[key] = value;
        }
        else if (keepUnlearnable)
        {
            _unlearnable[key] = value;
        }
    }

    // The value for castCode, or false when nothing decides it (unknown code, or learnable
    // rows that disagree with no caster spell to pick between them).
    public bool TryGet(string castCode, Func<string, int?>? casterSpell, out T value)
    {
        string key = castCode.Trim();
        if (casterSpell?.Invoke(key) is { } number && _byNumber.TryGetValue(number, out value))
            return true;
        if (_learnable.ContainsKey(key))
        {
            if (_learnableSplit.Contains(key)) { value = default; return false; }
            value = _learnable[key];
            return true;
        }
        return _unlearnable.TryGetValue(key, out value);
    }

    private static bool IsLearnable(GameRow row)
        => row.TryGetProperty("Learnable", out GameValue learnEl)
           && learnEl.ValueKind == JsonValueKind.Number
           && learnEl.TryGetInt32(out int learnVal) && learnVal != 0;
}

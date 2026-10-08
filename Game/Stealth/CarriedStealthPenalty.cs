using MudPlay.Game.Inventory;

namespace MudPlay.Game.Stealth;

// Whether a sneak can still take while the pack holds something that drags Stealth
// down. A few items do that just by being carried (a log raft is Stealth -125, the
// large black gem -200), and with one of them a sneak is refused every time: the
// client resent `sn` for fifteen seconds in each room it entered (report
// paradigm-20261007-213809). Auto-Sneak asks here first and stands down instead.
//
// The Stealth figure is the last one read off a `stat` screen, which already
// includes whatever was in the pack at that moment (GAME_MECHANICS "Abilities of
// carried items"). So the estimate is that reading, less what the pack was doing
// then, plus what it is doing now.
public sealed class CarriedStealthPenalty
{
    // A sneak under this chance isn't worth sending: every sneaked move re-rolls
    // under the same chance, so one that does take is lost a room later.
    public const int HopelessChance = 10;

    private readonly Func<IReadOnlyList<string>> _carried;
    private readonly Func<string, int> _packStealthOf;
    private readonly Func<int> _stealthReading;
    private readonly Func<int> _encumbrancePercent;
    private readonly Func<bool> _perfectStealth;
    private int _carriedAtRead;

    // Items names the carried things that lower Stealth; Modifier is their sum.
    public readonly record struct Verdict(bool Hopeless, string Items, int Modifier, int Chance);

    public CarriedStealthPenalty(
        Func<IReadOnlyList<string>> carried, Func<string, int> packStealthOf,
        Func<int> stealthReading, Func<int> encumbrancePercent, Func<bool> perfectStealth)
    {
        ArgumentNullException.ThrowIfNull(carried);
        ArgumentNullException.ThrowIfNull(packStealthOf);
        ArgumentNullException.ThrowIfNull(stealthReading);
        ArgumentNullException.ThrowIfNull(encumbrancePercent);
        ArgumentNullException.ThrowIfNull(perfectStealth);
        _carried = carried;
        _packStealthOf = packStealthOf;
        _stealthReading = stealthReading;
        _encumbrancePercent = encumbrancePercent;
        _perfectStealth = perfectStealth;
    }

    // A `stat` screen just gave a fresh Stealth figure: remember what the pack was
    // doing to it. Until the first one, the reading is taken as free of any penalty,
    // which can only make a pack that holds one look worse, never better.
    public void NoteStealthRead() => _carriedAtRead = Tally().Modifier;

    public Verdict Current()
    {
        (int modifier, string items) = Tally();
        if (modifier >= 0) return new Verdict(false, string.Empty, modifier, 0);

        int stealth = _stealthReading() - _carriedAtRead + modifier;
        int chance = SneakChance.Percent(stealth - SneakChance.EncumbrancePenalty(_encumbrancePercent()));
        // Perfect Stealth takes and holds whatever the penalties.
        bool hopeless = chance < HopelessChance && !_perfectStealth();
        return new Verdict(hopeless, $"{items} (Stealth {modifier})", modifier, chance);
    }

    private (int Modifier, string Items) Tally()
    {
        int total = 0;
        List<string>? lowering = null;
        foreach (string entry in _carried())
        {
            (int count, string name) = CountedCommand.SplitLeadingCount(entry);
            int each = _packStealthOf(name);
            if (each == 0) continue;
            total += each * count;
            if (each < 0) (lowering ??= new List<string>()).Add(name);
        }
        return (total, lowering is null ? string.Empty : string.Join(" and ", lowering));
    }
}

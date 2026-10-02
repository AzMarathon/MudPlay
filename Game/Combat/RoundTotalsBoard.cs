using MudPlay.Models.Profile;

namespace MudPlay.Game.Combat;

// The latest round's damage table for the Round Totals window. Every round lands
// here whether or not the terminal prints its own table; the window's own options
// pick the rows.
//
// It also says how much room the window should give the table, steadied so the
// window doesn't jump as the fight changes: the largest table (and the longest
// name) of the last Memory rounds. A bigger round grows it at once; it only shrinks
// after Memory rounds have all been smaller.
public sealed class RoundTotalsBoard
{
    // How many rounds a larger table is remembered for — about a minute of combat.
    public const int Memory = 10;

    private readonly Func<RoundTotalsWindowSettings> _options;
    private readonly Queue<(int Rows, int NameWidth)> _recent = new();
    private RoundSummary? _last;

    public bool HasRound => _last is not null;
    public int Round { get; private set; }
    public IReadOnlyList<RoundTotalsRow> Rows { get; private set; } = Array.Empty<RoundTotalsRow>();

    // Rows of room to give the table (at least Rows.Count) and the name column's
    // width in characters.
    public int RowSlots { get; private set; }
    public int NameWidth { get; private set; }

    public event Action? Changed;

    public RoundTotalsBoard(Func<RoundTotalsWindowSettings> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public void Publish(RoundSummary round)
    {
        _last = round;
        Recompute();
    }

    // The window's options changed: redraw the same round with them. The sizes
    // start again, since the old ones were for a different choice of rows.
    public void OptionsChanged()
    {
        if (_last is null) return;
        _recent.Clear();
        Recompute();
    }

    // Another character loaded: nothing to show until its first round.
    public void Clear()
    {
        _last = null;
        _recent.Clear();
        Round = 0;
        Rows = Array.Empty<RoundTotalsRow>();
        RowSlots = 0;
        NameWidth = 0;
        Changed?.Invoke();
    }

    private void Recompute()
    {
        if (_last is not { } round) return;
        RoundTotalsWindowSettings options = _options();
        List<CombatantKind> shown = new(4);
        if (options.ShowSelf) shown.Add(CombatantKind.Self);
        if (options.ShowParty) shown.Add(CombatantKind.Party);
        if (options.ShowPlayers) shown.Add(CombatantKind.Player);
        if (options.ShowMonsters) shown.Add(CombatantKind.Monster);

        Round = round.FightRound;
        Rows = RoundTotalsFormatter.Rows(round, shown, options.EachMonster, options.CapAtMonsterHp);

        _recent.Enqueue((Rows.Count, RoundTotalsFormatter.NameWidth(Rows)));
        while (_recent.Count > Memory) _recent.Dequeue();
        RowSlots = _recent.Max(static r => r.Rows);
        NameWidth = _recent.Max(static r => r.NameWidth);
        Changed?.Invoke();
    }
}

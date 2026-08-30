using MudPlay.Services;

namespace MudPlay.Game.Spells;

// Threshold + cap governing a mana-regen roll-spell reroll cycle, read live
// from SpellsSettings on every decision so a mid-cycle settings edit takes
// effect on the next roll.
//
// Threshold: reroll while the rolled spells: contribution lands below this.
// null disables rerolling — the spell then just recasts on expiry through the
// normal buff path, no abil read. Cap: hard ceiling on consecutive rerolls per
// cast cycle before accepting whatever landed.
public readonly record struct ManaRegenRerollConfig(int? Threshold, int Cap);

// Paradigm-only reroll state machine for a code-145 mana-regen roll spell
// (nature tap / mana flux) — a persistent +/- modifier to the mana-regen rate
// whose landed value is a fresh roll each cast. After the spell lands we read
// its rolled contribution off abil 145's spells: slice (surfaced by
// AbilBreakdownParser); a roll below the configured threshold drags the regen
// rate down, so we recast to try for a better one — up to Cap times,
// hard-stopping early if the next cast can't be paid without dropping under the
// buff floor.
//
// Purely a state machine driven by two external events: a confirmed roll-spell
// landing (OnRollSpellLanded, wired from the CastingDirector's buff-confirmation
// path) and a parsed abil breakdown (AbilBreakdownParser.BreakdownParsed). It
// owns no timers, no wire access, and no spell classification: the caller only
// invokes OnRollSpellLanded for a spell it has already resolved to a code-145
// roll spell, and supplies the query / recast / afford actions. This keeps the
// decision logic deterministic and unit-testable.
//
// The Stock realm has no abil breakdown to read, so the reroll path is
// Paradigm-only; the caller gates construction / invocation on the active
// realm. Stock infers roll quality from observed regen ticks on a separate
// path.
//
// Cycle life: a landing while idle opens a cycle and zeroes the reroll counter;
// a landing mid-cycle is the recast we asked for and preserves the counter.
// Either way it fires one abil 145 query. The returned breakdown decides accept
// (roll >= threshold, cap reached, or floor hit) or reroll (recast, counter++).
// Serial by construction — each abil read is bracketed by a landing, so there
// is never more than one query in flight.
public sealed class ManaRegenReroller : IDisposable
{
    // LogService category — appears as [ManaRegen] rows per reroll decision.
    public const string LogCategory = "ManaRegen";

    // The ability code the reroll logic reads off an abil breakdown —
    // mana-regen is ability 145.
    public const int ManaRegenAbilityCode = RegenSpellClassifier.ManaRegenCode;

    // True when formula is a mana-regen roll spell — it carries a
    // code-ManaRegenAbilityCode ability whose stored AbilVal is 0, the
    // signature of nature tap / mana flux (the magnitude is rolled from the
    // level-scaled Min/Max range on each cast). A fixed +N regen buff (AbilVal =
    // N) or a mana HoT (chaos surge, codes 150 / 123) is excluded — rerolling
    // those is pointless. Thin alias over RegenSpellClassifier.Classify's
    // ManaRegenRoll trait so the "what counts as a roll spell" rule lives in
    // exactly one place, shared by the caller's landing classifier and the
    // Spells tab's range readout.
    public static bool IsRollSpell(in SpellFormulaInput formula)
        => RegenSpellClassifier.Has(formula, RegenSpellTraits.ManaRegenRoll);

    private readonly AbilBreakdownParser _parser;
    private readonly Func<ManaRegenRerollConfig> _readConfig;
    private readonly Action _sendAbilQuery;
    private readonly Action<string> _recast;
    private readonly Func<bool> _canAffordReroll;
    // True on the Stock realm — there's no `abil 145`, so roll quality is judged from
    // the observed passive mana TICK (an MP jump on the statline) instead. The
    // threshold then means the desired tick, not the rolled percent.
    private readonly Func<bool> _useTickMonitor;
    private readonly LogService? _log;

    private string? _activeShort;
    private int _rerollsUsed;
    private bool _awaitingAbil;
    private bool _awaitingTick;
    private bool _disposed;

    public ManaRegenReroller(
        AbilBreakdownParser parser,
        Func<ManaRegenRerollConfig> readConfig,
        Action sendAbilQuery,
        Action<string> recast,
        Func<bool> canAffordReroll,
        Func<bool> useTickMonitor,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _parser = parser;
        _readConfig = readConfig;
        _sendAbilQuery = sendAbilQuery;
        _recast = recast;
        _canAffordReroll = canAffordReroll;
        _useTickMonitor = useTickMonitor;
        _log = log;
        _parser.BreakdownParsed += OnBreakdown;
    }

    // True while a reroll cycle is mid-flight (a roll landed and we're awaiting
    // its abil read, or about to recast). For diagnostics / tests.
    public bool CycleActive => _activeShort is not null;

    // Rerolls spent in the current cycle; resets when a fresh roll lands while
    // idle. For diagnostics / tests.
    public int RerollsUsed => _rerollsUsed;

    // The roll quality last judged — the abil-145 spells value (Paradigm) or the
    // observed tick (Stock). Null until the first roll is evaluated. For the bug
    // report, so a "reroll isn't working" capture shows what value the engine saw.
    public int? LastObservedValue { get; private set; }

    // The roll spell spellShort just landed (confirmed via its caster / applied
    // message). A landing while idle opens a new cycle with the reroll counter
    // reset; a landing mid-cycle is the recast we triggered and keeps the
    // counter. Either way, fire an abil 145 query to read what it rolled. No-op
    // (and abandons any cycle) when rerolling is disabled.
    public void OnRollSpellLanded(string spellShort)
    {
        if (string.IsNullOrWhiteSpace(spellShort)) return;

        ManaRegenRerollConfig cfg = _readConfig();
        if (cfg.Threshold is null)
        {
            // Rerolling disabled — nothing to verify; the spell just rides the
            // normal recast-on-expiry buff path. Ensure we hold no stale cycle.
            Reset();
            return;
        }

        if (_activeShort is null
            || !string.Equals(_activeShort, spellShort, StringComparison.OrdinalIgnoreCase))
        {
            // Fresh cycle: idle, or a different roll spell replaced the tracked one.
            _activeShort = spellShort;
            _rerollsUsed = 0;
            _log?.Info(LogCategory,
                $"reroll cycle start spell={spellShort} threshold={cfg.Threshold} cap={cfg.Cap}");
        }
        else
        {
            _log?.Debug(LogCategory,
                $"recast landed spell={spellShort} reroll={_rerollsUsed}/{cfg.Cap}");
        }

        // Paradigm reads the rolled contribution off `abil 145`; Stock instead waits
        // for the next observed passive mana tick (an MP jump on the statline).
        if (_useTickMonitor())
        {
            _awaitingTick = true;
            _log?.Debug(LogCategory, "awaiting the next passive mana tick to judge the roll");
        }
        else
        {
            _awaitingAbil = true;
            _log?.Debug(LogCategory, "querying abil 145 for rolled mana-regen contribution");
            _sendAbilQuery();
        }
    }

    private void OnBreakdown(AbilBreakdown b)
    {
        if (!_awaitingAbil) return;
        if (b.Code != ManaRegenAbilityCode) return;   // an unrelated abil query — keep waiting

        _awaitingAbil = false;
        _log?.Debug(LogCategory,
            $"observed rolled mana-regen contribution spells={b.Spells} (abil total {b.Total})");
        Decide(b.Spells, "roll");
    }

    // A passive mana tick was observed (Stock): the MP jump IS the roll's quality, so
    // it stands in for the abil read. Only consumed while a reroll cycle is awaiting a
    // tick; the caller supplies only CLEAN passive ticks (not while resting / meditating).
    public void OnManaTickObserved(int tickAmount)
    {
        if (!_awaitingTick) return;
        _awaitingTick = false;
        _log?.Debug(LogCategory, $"observed passive mana tick={tickAmount}");
        Decide(tickAmount, "tick");
    }

    // The shared accept-or-reroll decision. value is the roll's quality — the rolled
    // percent on Paradigm, the observed tick on Stock — compared against the configured
    // threshold; reroll while below it, up to the cap, hard-stopping at the mana floor.
    private void Decide(int value, string valueLabel)
    {
        if (_activeShort is not { } shortCode) return;   // defensive: no active cycle
        LastObservedValue = value;
        ManaRegenRerollConfig cfg = _readConfig();

        // Threshold went null mid-cycle (settings edit) — accept and drop out.
        if (cfg.Threshold is not { } threshold)
        {
            _log?.Info(LogCategory, $"reroll disabled mid-cycle — accepting spell={shortCode} {valueLabel}={value}");
            Reset();
            return;
        }

        if (value >= threshold)
        {
            _log?.Info(LogCategory,
                $"accepted spell={shortCode} {valueLabel}={value} >= threshold={threshold} " +
                $"after {_rerollsUsed} reroll(s)");
            Reset();
            return;
        }

        if (_rerollsUsed >= cfg.Cap)
        {
            _log?.Info(LogCategory,
                $"reroll cap reached spell={shortCode} {valueLabel}={value} < threshold={threshold} " +
                $"cap={cfg.Cap} — accepting");
            Reset();
            return;
        }

        if (!_canAffordReroll())
        {
            _log?.Info(LogCategory,
                $"reroll stopped at mana floor spell={shortCode} {valueLabel}={value} < threshold={threshold} " +
                $"after {_rerollsUsed} reroll(s)");
            Reset();
            return;
        }

        _rerollsUsed++;
        _log?.Info(LogCategory,
            $"rerolling spell={shortCode} {valueLabel}={value} < threshold={threshold} " +
            $"attempt {_rerollsUsed}/{cfg.Cap}");
        _recast(shortCode);
    }

    // Abandon any in-progress cycle — call on disconnect / death / spell-pick
    // change so a stale wait can't strand the reroller.
    public void Reset()
    {
        _activeShort = null;
        _rerollsUsed = 0;
        _awaitingAbil = false;
        _awaitingTick = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _parser.BreakdownParsed -= OnBreakdown;
    }
}

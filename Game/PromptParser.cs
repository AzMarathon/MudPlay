using MudPlay.Services;

namespace MudPlay.Game;

// Writes observed status-line values from WirePromptScanner into
// PlayerState. Sole writer of HP / MA / mana-type / position fields (the
// IL-scan test enforces this).
//
// Subscribes to the wire stream rather than MessageRouter's StatusLine id
// because the server overwrites the statline in place (CR + erase-line + new
// content on the same row). By the time LineExtractor emits a row, only the
// last statline survives and intermediate HP / MA / position changes are
// lost. Scanning the wire catches every update.
//
// MaxHp / MaxMa ratchet upward on every observation. A custom statline may
// include the %H / %M max wildcards, but StatlinePromptRegexBuilder emits
// those as non-capturing digit runs — they render on the wire without
// feeding a second write path into the max fields, so this parser stays the
// sole writer. The high-water mark reads low until the character is first
// seen at full; the authoritative ceilings come from the stat screen via
// ApplyStatScreenMax (routed here to preserve sole ownership).
public sealed class PromptParser : IDisposable
{
    private readonly WirePromptScanner _scanner;
    private bool _disposed;

    public PlayerState State { get; }

    public PromptParser(WirePromptScanner scanner, PlayerState state)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(state);
        State = state;
        _scanner = scanner;
        _scanner.PromptObserved += OnPromptObserved;
    }

    private void OnPromptObserved(PromptObservation obs)
    {
        State.Hp = obs.Hp;
        if (obs.Hp > State.MaxHp) State.MaxHp = obs.Hp;

        State.ManaType = obs.ManaType;
        if (obs.ManaType == ManaType.None)
        {
            State.Ma = 0;
            State.MaxMa = 0;
        }
        else
        {
            State.Ma = obs.Mana;
            if (obs.Mana > State.MaxMa) State.MaxMa = obs.Mana;
        }

        State.Position = obs.Position;
        State.HasPromptData = true;
    }

    // Snap MaxHp / MaxMa to the authoritative ceilings read off the stat
    // screen. The standard status line carries only current HP / MA, so the
    // prompt path learns the maxima as a high-water mark that reads low until
    // the character is observed at full — the stat screen is the true source,
    // so this overrides even downward. Routed through the parser to keep it
    // the sole writer of the max fields. A non-positive value is ignored so a
    // failed / absent parse can't wipe an already-learned max.
    public void ApplyStatScreenMax(int maxHp, int maxMa)
    {
        if (maxHp > 0) State.MaxHp = maxHp;
        // A no-mana class (ManaType.None — warriors, ninjas) has no live mana
        // pool, yet its stat / exp screen can list a latent MaxMana (a Ninja reads
        // 8). Applying it would flip MaxMa 0 → N until the next None prompt resets
        // it, defeating the `MaxMa > 0` guards that keep every mana / kai health
        // setting inert for a no-mana character — which is what let a manual `exp`
        // poll trip a spurious MA-flee ("break combat and run") mid-combat
        // (reports stock-20260730-150957 / -151145). Skip the mana ceiling once a
        // prompt has CONFIRMED the class carries no mana; before any prompt
        // (HasPromptData false) the stat screen stays authoritative, so a caster
        // read before its first prompt still learns its max.
        bool knownNoMana = State.HasPromptData && State.ManaType == ManaType.None;
        if (maxMa > 0 && !knownNoMana) State.MaxMa = maxMa;
    }

    // Wipe live status state to "no data" when the character changes out from
    // under us (a profile swap). The HP / MA / position fields describe the
    // OUTGOING character's last observed prompt; carrying them into the new
    // character leaves HasPromptData reading true against a mismatched body —
    // the incoming character's MaxHp gets re-seeded from its profile
    // (ApplyStatScreenMax) while the previous character's current HP lingers,
    // which fired a spurious low-HP emergency hangup the instant a swap ran
    // (paradigm-20260909-172633: Fujin's 66 HP against FujinPVP's 324 max).
    // Reset here so nothing HP-driven acts until the new character's first real
    // prompt re-establishes live data. Sole-writer safe — routed through the
    // parser like every other max/HP mutation.
    public void ResetForProfileSwap()
    {
        State.Hp = 0;
        State.MaxHp = 0;
        State.Ma = 0;
        State.MaxMa = 0;
        State.ManaType = ManaType.None;
        State.Position = default;
        State.HasPromptData = false;
    }

    // Adjust MaxHp / MaxMa by a signed delta when the worn set's flat pool
    // bonus changes mid-session (see Game.Health.EquipmentMaxPoolSync) —
    // composes with whatever base the ratchet/stat-screen already
    // established rather than guessing an absolute ceiling. Routed through
    // the parser to keep it the sole writer of the max fields. Floors each
    // pool at 0 so an unexpected combination of deltas can't go negative.
    public void ApplyEquipmentMaxDelta(int hpDelta, int maDelta)
    {
        if (hpDelta != 0) State.MaxHp = Math.Max(0, State.MaxHp + hpDelta);
        if (maDelta != 0) State.MaxMa = Math.Max(0, State.MaxMa + maDelta);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanner.PromptObserved -= OnPromptObserved;
    }
}

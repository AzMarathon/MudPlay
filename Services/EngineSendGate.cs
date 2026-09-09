namespace MudPlay.Services;

// Pause switch for every engine-driven wire send. Held while the app is in a flow
// that mustn't be polluted by automatic commands — and more than one such flow can
// overlap, so it tracks a SET of named holds rather than a single flag and stays
// locked until every hold is released. Current holders:
//   - Game.SuicidePasswordTracker, around each password-entry prompt (a stray par
//     poll mid-flow would become the password).
//   - Game.PlayerDroppedGate, while the local character is mortally wounded (HP <=
//     0): the game rejects every action command with "You may not do that while you
//     are mortally wounded!", so engine sends are pure noise until we recover.
//   - Game.TrainerScreenGate, while the `train stats` / character-creation form
//     owns the keyboard: the form's first field is the Family Name, so any stray
//     engine send types into it and Enter corrupts the character's last name. The
//     auto-trainer's CP allocation is the one exception — it rides the raw sender
//     (below), not the wrapped path, so it can still fill in the stat plan.
// MainWindowViewModel wraps every engine's SetWireSender callback through
// WrapEngineSender, so a raised hold silently no-ops every engine until cleared.
//
// User-typed input does NOT go through the wrapped path — it flows from
// TerminalControl -> LocalInputBuffer -> directly into
// MainWindowViewModel.SendUserInput. So even while held, the user can still type
// (their password, a manual command) normally; only background engines are gated.
//
// A couple of automatic sends must survive a hold, so they're bound to a separate,
// un-wrapped sender rather than the wrapped path: the emergency low-HP hangup
// (HealthManager.SetHangupWireSender — hanging up is still allowed at 0 HP or
// below, exactly when the mortally-wounded hold is up) and the auto-trainer's CP
// allocation (AutoTrainManager on the raw SendUserInput — it's the one automation
// permitted while the TrainerScreenGate hold silences everything else).
//
// Single-threaded: every holder flips these on the UI thread (router handlers and
// PlayerState.PropertyChanged both marshal upstream), and the wrapper reads on the
// same thread, so the plain HashSet needs no lock.
public sealed class EngineSendGate
{
    private readonly HashSet<string> _holds = new(StringComparer.Ordinal);

    // The last command the client actually put on the wire (a non-locked engine
    // send) and the sender that sent it, so a confusion fumble — which eats the
    // just-sent command without executing it — can re-send it (ReplayLastClientCommand).
    // Only CLIENT sends are tracked: user-typed input never flows through the wrapped
    // path (see the class comment), so re-firing only ever repeats the client's own
    // automatic commands, never something the user typed.
    private byte[]? _lastClientCommand;
    private Action<byte[]>? _replaySender;

    // True while any hold is active — engine wire-sends drop on the floor.
    public bool IsLocked => _holds.Count > 0;

    // Fired when the LAST hold clears (locked → unlocked). Lets an engine whose
    // send was silently dropped while the gate was up re-drive it — e.g. the walker
    // re-sends a move step swallowed during the trainer-menu hold rather than
    // stalling forever with a route drawn and nothing on the wire.
    public event Action? Released;

    // Raise a named hold. Idempotent per reason.
    public void Hold(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        _holds.Add(reason);
    }

    // Release a named hold. Fires Released once the last hold clears.
    public void Release(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        if (_holds.Remove(reason) && _holds.Count == 0)
            Released?.Invoke();
    }

    // Wrap an engine's raw Action<byte[]> wire-sender so it short-circuits while any
    // hold is active.
    public Action<byte[]> WrapEngineSender(Action<byte[]> rawSender)
    {
        ArgumentNullException.ThrowIfNull(rawSender);
        return bytes =>
        {
            if (IsLocked) return;
            rawSender(bytes);
            // Remember the just-sent client command so a confusion fumble can re-fire
            // it. The replay goes back through THIS sender (they all funnel to the same
            // SendUserInput), so re-firing lands on the wire exactly as the original did.
            _lastClientCommand = bytes;
            _replaySender = rawSender;
        };
    }

    // Re-send the last client command — a confusion fumble consumed it without it
    // executing (GAME_MECHANICS "Confusion fumbles"), so re-sending is what performs
    // the intended action. Driven by ConditionTracker.ActionFailed. No-op while a hold
    // is up, before any client send, or when the last command was a bare MOVEMENT step:
    // a fumbled move is already recovered by MovementRefusalDetector's revert + the
    // walker's own re-send, so re-firing it here would double-step and desync position.
    // (Combat weapon swings are re-sent by CombatManager with its engage bookkeeping; the
    // AppServices coordinator only falls through to this for the non-weapon cases —
    // attack spells, item uses, and other client commands.)
    public void ReplayLastClientCommand()
    {
        if (IsLocked) return;
        if (_lastClientCommand is not { Length: > 0 } cmd) return;
        if (_replaySender is not { } send) return;
        if (IsBareMovementCommand(cmd)) return;
        send(cmd);
    }

    // True when the bytes are just a bare movement direction (with its trailing CR) —
    // "n", "ne", "up", "south", etc. Door/item/attack commands ("bash n", "use x",
    // "a mob") are multi-token and never match, so they still re-fire.
    private static bool IsBareMovementCommand(byte[] bytes)
    {
        string cmd = System.Text.Encoding.Latin1.GetString(bytes).Trim().ToLowerInvariant();
        return cmd is "n" or "s" or "e" or "w" or "ne" or "nw" or "se" or "sw" or "u" or "d"
            or "north" or "south" or "east" or "west"
            or "northeast" or "northwest" or "southeast" or "southwest" or "up" or "down";
    }
}

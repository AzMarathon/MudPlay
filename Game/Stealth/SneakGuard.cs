using System;
using System.Collections.Generic;
using MudPlay.Services;

namespace MudPlay.Game.Stealth;

// Why automation that would end a sneak is being held (GAME_MECHANICS "Sneaking —
// commands, equip order, and the sneak state machine" → "What ends a sneak").
public enum SneakHold
{
    None,
    // A backstab opener is owed or its round hasn't resolved: anything sent first
    // spends the surprise, so hold until the backstab fires.
    UntilBackstab,
    // Our sneaked move is in flight: a command sent now lands in the room we're
    // entering, before its "Sneaking..." confirms us.
    MoveInFlight,
    // Sneaking past hostiles we won't fight: hold until a room with none, where the
    // action can go out and we re-sneak straight after.
    UntilClearRoom,
}

// The one rule every sneak-breaking automation consults before it sends: gear
// swaps, room search, rest / meditate, auto-light, in-between spells, party
// invites and say-channel chatter. Walk steps that can't be skipped (doors, traps,
// room commands) don't ask — they go anyway and the sneak is marked broken.
// Plain commands with nothing to re-derive later are taken at the engine send gate
// (TakeIfHeld) and go out once the hold lifts.
public sealed class SneakGuard
{
    public const string LogCategory = "SneakGuard";

    private readonly Func<bool> _autoSneak;
    private readonly Func<bool> _backstabOwed;
    private readonly Func<bool> _moveInFlight;
    private readonly Func<bool> _hostileHere;
    private readonly Func<bool> _fightingHere;
    private readonly Func<bool> _inCombat;
    private readonly Func<bool> _stealthed;
    private readonly LogService? _log;
    private readonly List<(string Command, string Why)> _queued = new();
    private Action<string>? _send;
    private SneakHold _last;

    public SneakGuard(
        Func<bool> autoSneak, Func<bool> backstabOwed, Func<bool> moveInFlight,
        Func<bool> hostileHere, Func<bool> fightingHere, Func<bool> inCombat, Func<bool> stealthed,
        LogService? log = null)
    {
        _autoSneak = autoSneak;
        _backstabOwed = backstabOwed;
        _moveInFlight = moveInFlight;
        _hostileHere = hostileHere;
        _fightingHere = fightingHere;
        _inCombat = inCombat;
        _stealthed = stealthed;
        _log = log;
    }

    // Raised when a hold lifts, so a held action can be re-driven.
    public event Action? Released;

    public void SetWireSender(Action<string> send) => _send = send;

    public SneakHold Current
    {
        get
        {
            if (_backstabOwed()) return SneakHold.UntilBackstab;
            // In a fight the sneak is already gone (being attacked ends it), and a
            // heal held there would be held while we're hit.
            if (!_autoSneak() || _inCombat()) return SneakHold.None;
            if (_moveInFlight()) return SneakHold.MoveInFlight;
            // Only while there's a sneak to keep: once a rest or a loud entry has ended
            // it, holding would protect nothing.
            if (_hostileHere() && !_fightingHere() && _stealthed()) return SneakHold.UntilClearRoom;
            return SneakHold.None;
        }
    }

    public bool Holds => Current != SneakHold.None;

    public IReadOnlyList<(string Command, string Why)> Queued => _queued;

    // Take a command that can wait (SneakBreakingCommands.CanWait) for later while a
    // hold is on; it goes out unchanged once the hold lifts. False — send it now —
    // when nothing is held.
    public bool TakeIfHeld(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        SneakHold hold = Current;
        if (hold == SneakHold.None) return false;
        _queued.Add((command, Describe(hold)));
        _log?.Info(LogCategory, $"held '{command}' — {Describe(hold)}");
        return true;
    }

    // Re-check on every heartbeat and room change: announce a hold starting or
    // lifting, and on lifting flush the queue and raise Released.
    public void Poll()
    {
        SneakHold now = Current;
        if (now == _last) return;
        SneakHold was = _last;
        _last = now;
        if (now != SneakHold.None)
        {
            if (now != SneakHold.MoveInFlight)   // every sneaked step would log otherwise
                _log?.Info(LogCategory, $"holding sneak-breaking automation — {Describe(now)}");
            return;
        }
        if (was != SneakHold.MoveInFlight || _queued.Count > 0)
            _log?.Info(LogCategory, $"hold lifted ({Describe(was)} over)"
                + (_queued.Count > 0 ? $" — sending {_queued.Count} held command(s)" : ""));
        List<(string Command, string Why)> flush = new(_queued);
        _queued.Clear();
        foreach ((string command, _) in flush) _send?.Invoke(command);
        Released?.Invoke();
    }

    // Drop anything queued — a new profile or a disconnect makes it stale.
    public void Reset()
    {
        _queued.Clear();
        _last = SneakHold.None;
    }

    public static string Describe(SneakHold hold) => hold switch
    {
        SneakHold.UntilBackstab => "until the backstab fires",
        SneakHold.MoveInFlight => "while our sneaked move lands",
        SneakHold.UntilClearRoom => "sneaking past hostiles, until a room without them",
        _ => "nothing held",
    };
}

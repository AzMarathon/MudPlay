using MudPlay.Game.Combat;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Looks at the floor again after a kill whose monster could have dropped an item.
// A drop lands on the ground with no line of its own (GAME_MECHANICS "Monster drops
// land on the ground"), so only a re-display of the room shows it: to the player,
// and to the auto-get engine's "You notice … here." reader. A bare Enter re-displays
// the room without its description.
//
// Either of two reasons is enough:
//   - the dead monster can drop an item flagged Auto-collect, with Auto-Get Items on;
//   - it has a chance above 0 of dropping anything, with Settings → Cash + Items →
//     "Look after killing a monster that can drop an item" on.
//
// The death is read on its exp line or its *Combat Off*, and the Enter goes out at
// the end of that server read: never among the kill's own lines, and one Enter for
// all the kills the read carried. For the setting's reason alone it also waits for the room's
// fight to end, so a fight that kills several such monsters looks once. An
// Auto-collect drop is looked for straight away, since auto-get collects mid-fight.
//
// No Enter of its own when the room has been displayed since the kill or an Enter
// has gone out since it (the combat engine's roster re-read, the cash engine's
// combat-clear look, a death-spell nudge), when we have left the room or a move is
// on its way, in a room too dark to see or while blind (the display shows no floor
// then), while the send gate is held, or with the master switch off.
//
// The walker is held through AcquisitionGate from the kill until the display has
// been read (or ReleaseAfter, for a display that never comes). The kill clears the
// Combat gate in the same pass, so the hold goes up on the death itself, ahead of
// the roster resync: a walk step would otherwise leave first, and the Enter would
// show the next room.
public sealed class PostKillFloorCheck : IDisposable
{
    public const string LogCategory = "FloorCheck";

    // The name the master switch counts this system's skips under.
    public const string SystemName = "Floor check after a kill";

    // How long the walker waits for the display the Enter asked for.
    public static readonly TimeSpan ReleaseAfter = TimeSpan.FromMilliseconds(1500);

    // The look a kill has asked for and not yet had. AtOnce: an Auto-collect drop,
    // looked for while the fight goes on. EntersAtLastKill: the bare-Enter count when
    // the latest kill that asked for it was read.
    private sealed class OwedLook(string why, bool atOnce, int enters)
    {
        public string Why { get; } = why;
        public bool AtOnce { get; set; } = atOnce;
        public int EntersAtLastKill { get; set; } = enters;
        public int Kills { get; set; } = 1;
        public bool WaitLogged { get; set; }
    }

    private readonly MessageRouter _router;
    private readonly Func<MonsterDeathEvent, IReadOnlyCollection<int>> _dyingMonsters;
    private readonly Func<int, MonsterCatalogEntry?> _monster;
    private readonly Func<int, bool> _autoCollectWanted;
    private readonly Func<bool> _lookForAnyDrop;
    private readonly Func<bool> _hasEngageableHostiles;
    private readonly Func<bool> _canSeeRoom;
    private readonly Func<bool> _moveOnItsWay;
    private readonly Func<bool> _sendGateHeld;
    private readonly Func<int> _bareEntersSent;
    private readonly Func<string, bool> _blocked;
    private readonly Func<int, string?> _itemName;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private AcquisitionGate? _gate;
    private RoomRedisplayCoordinator? _redisplay;

    private OwedLook? _owed;
    private bool _holding;
    private IDisposable? _holdRelease;
    private bool _disposed;

    // What the last kill that asked for a look came to, for the bug report.
    public string? LastCheck { get; private set; }

    // autoCollectWanted: whether the item is one auto-get would take now (flagged
    // Auto-collect, takeable, Auto-Get Items on). lookForAnyDrop: the setting.
    // bareEntersSent: EngineSendGate.BareEntersSent. blocked: the master switch,
    // asked with a detail for its skip count.
    public PostKillFloorCheck(
        MessageRouter router,
        Func<MonsterDeathEvent, IReadOnlyCollection<int>> dyingMonsters,
        Func<int, MonsterCatalogEntry?> monster,
        Func<int, bool> autoCollectWanted,
        Func<bool> lookForAnyDrop,
        Func<bool> hasEngageableHostiles,
        Func<bool> canSeeRoom,
        Func<bool> moveOnItsWay,
        Func<bool> sendGateHeld,
        Func<int> bareEntersSent,
        Func<string, bool> blocked,
        Func<int, string?> itemName,
        LogService? log = null,
        Func<TimeSpan, Action, IDisposable>? schedule = null,
        Func<DateTimeOffset>? now = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
        _dyingMonsters = dyingMonsters;
        _monster = monster;
        _autoCollectWanted = autoCollectWanted;
        _lookForAnyDrop = lookForAnyDrop;
        _hasEngageableHostiles = hasEngageableHostiles;
        _canSeeRoom = canSeeRoom;
        _moveOnItsWay = moveOnItsWay;
        _sendGateHeld = sendGateHeld;
        _bareEntersSent = bareEntersSent;
        _blocked = blocked;
        _itemName = itemName;
        _log = log;
        _schedule = schedule ?? DispatcherTicks.Once;
        _now = now ?? (static () => DateTimeOffset.Now);
        _router.ReadEnded += OnReadEnded;
    }

    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public void SetAcquisitionGate(AcquisitionGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gate = gate;
    }

    // Shared with the cash engine's combat-clear look, so the last kill of a fight
    // re-displays the room once for both.
    public void SetRoomRedisplay(RoomRedisplayCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _redisplay = coordinator;
    }

    // For the bug report: what is owed now, and the last check.
    public string Describe()
    {
        string state = _owed is { } owed
            ? $"owed after {owed.Kills} kill(s) ({owed.Why})"
              + (owed.AtOnce ? "" : ", waiting for the room's fight to end")
            : _holding ? "waiting on the room display" : "idle";
        return LastCheck is null ? state : $"{state}; last: {LastCheck}";
    }

    // A monster we're credited with died. Subscribed ahead of the roster resync,
    // which can clear the Combat gate in this same pass.
    public void OnMonsterDied(MonsterDeathEvent evt)
    {
        if (_disposed) return;
        if (WhyLook(evt) is not { } look)
        {
            // A later kill of the fight a look is owed for: the hold keeps the walker
            // for that look once this kill has ended the fight.
            if (_owed is not null) Hold();
            return;
        }
        if (_owed is null) _owed = new OwedLook(look.Why, look.AtOnce, _bareEntersSent());
        else
        {
            _owed.Kills++;
            _owed.AtOnce |= look.AtOnce;
            _owed.EntersAtLastKill = _bareEntersSent();
        }
        Hold();
    }

    // A room display was read (its exits line): it showed the floor.
    public void NoteRoomDisplayed()
    {
        if (_owed is { } owed)
        {
            _owed = null;
            Skipped(owed, "the room was displayed after the kill");
        }
        ReleaseHold();
    }

    // We are in another room: the floor of the one the kill was in is behind us.
    public void OnRoomChanged()
    {
        if (_owed is { } owed)
        {
            _owed = null;
            Skipped(owed, "left the room first");
        }
        ReleaseHold();
    }

    // Out of the game (the board's menu, a dropped link): the room isn't known to
    // be the same on the way back in.
    public void NoteGameLeft()
    {
        if (_owed is { } owed)
        {
            _owed = null;
            Skipped(owed, "left the game first");
        }
        ReleaseHold();
    }

    // Why the death asks for a look, or null. An Auto-collect drop wins: it is the
    // one that is looked for while the fight goes on.
    private (string Why, bool AtOnce)? WhyLook(MonsterDeathEvent evt)
    {
        bool anyDrop = _lookForAnyDrop();
        string? chance = null;
        string? names = null;
        foreach (int number in _dyingMonsters(evt))
        {
            if (_monster(number) is not { } monster) continue;
            names = names is null ? $"'{monster.Name}'" : $"{names}, '{monster.Name}'";
            foreach (MonsterDropSlot drop in monster.Drops)
            {
                if (_autoCollectWanted(drop.ItemId))
                    return ($"killed '{monster.Name}' (#{number}), which can drop "
                            + $"'{ItemLabel(drop.ItemId)}', flagged Auto-collect", true);
                if (anyDrop && chance is null && drop.Percent > 0)
                    chance = $"killed '{monster.Name}' (#{number}), which can drop "
                             + $"'{ItemLabel(drop.ItemId)}' ({drop.Percent}%), and "
                             + "'Look after killing a monster that can drop an item' is on";
            }
        }
        if (chance is not null) return (chance, false);
        _log?.Debug(LogCategory, names is null
            ? "a kill, but which monster died isn't known — no floor check"
            : !anyDrop
                ? $"killed {names} — 'Look after killing a monster that can drop an item' is off, and nothing it drops is flagged Auto-collect"
                : $"killed {names} — its data gives no item a chance to drop, no floor check");
        return null;
    }

    private string ItemLabel(int itemId) => _itemName(itemId) ?? $"item #{itemId}";

    // The read with the kill in it has ended: look now, unless the fight is still on
    // and nothing flagged Auto-collect is waiting.
    private void OnReadEnded()
    {
        if (_owed is not { } owed) return;
        if (!owed.AtOnce && _hasEngageableHostiles())
        {
            if (!owed.WaitLogged)
            {
                owed.WaitLogged = true;
                _log?.Debug(LogCategory, $"look owed ({owed.Why}) — waiting for the room's fight to end");
            }
            // The Combat gate holds the walker meanwhile; the last kill holds it again.
            ReleaseHold();
            return;
        }
        _owed = null;

        if (_bareEntersSent() != owed.EntersAtLastKill)
        {
            // Its display is on the way; the hold stays up for it.
            Skipped(owed, "an Enter has gone out since the kill, and its room display will show the floor");
            Hold();
            return;
        }
        string? skip = _moveOnItsWay() ? "a move is on its way, so the room shown would be the next one"
            : !_canSeeRoom() ? "the room can't be seen (too dark, or blind), so a display shows no floor"
            : _sendGateHeld() ? "the send gate is held"
            : _wireSender is null ? "no connection to send on"
            : _blocked(owed.Why) ? "the master switch is off"
            : null;
        if (skip is not null)
        {
            Skipped(owed, skip);
            ReleaseHold();
            return;
        }
        if (_redisplay is not null && !_redisplay.ShouldSend())
        {
            Skipped(owed, "the cash engine has just re-displayed the room");
            Hold();
            return;
        }

        Hold();
        _wireSender!(new[] { (byte)'\r' });
        LastCheck = $"{_now():HH:mm:ss} sent an Enter after {owed.Kills} kill(s): {owed.Why}";
        _log?.Info(LogCategory, $"re-displaying the room after {owed.Kills} kill(s): {owed.Why}");
    }

    private void Skipped(OwedLook owed, string reason)
    {
        LastCheck = $"{_now():HH:mm:ss} no Enter after {owed.Kills} kill(s), {reason} ({owed.Why})";
        _log?.Debug(LogCategory, $"no Enter of our own: {reason} ({owed.Why})");
    }

    // Hold the walker, or restart the wait of a hold already up.
    private void Hold()
    {
        if (_gate is null) return;
        if (!_holding)
        {
            _holding = true;
            _gate.NoteReLookPending();
        }
        _holdRelease?.Dispose();
        _holdRelease = _schedule(ReleaseAfter, OnHoldElapsed);
    }

    private void OnHoldElapsed()
    {
        _holdRelease = null;
        if (_holding)
            _log?.Debug(LogCategory, $"no room display within {ReleaseAfter.TotalSeconds:0.#}s — letting the walker go");
        ReleaseHold();
    }

    private void ReleaseHold()
    {
        _holdRelease?.Dispose();
        _holdRelease = null;
        if (!_holding) return;
        _holding = false;
        _gate?.NoteReLookDone();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.ReadEnded -= OnReadEnded;
        ReleaseHold();
    }
}

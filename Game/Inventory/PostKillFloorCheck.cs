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
// Which monster died comes from the attribute delegate (AppServices): the engine's
// target, the one an exp-inferred kill dropped, or, for this reader alone, the
// monster the user is fighting by hand (CombatManager.HandFoughtTarget).
//
// The death is read on its exp line or its *Combat Off*, and the Enter goes out at
// the end of that server read: never among the kill's own lines, and one Enter for
// all the kills the read carried. For the setting's reason alone it also waits for
// the room's fight to end, so a fight that kills several such monsters looks once.
// An Auto-collect drop is looked for straight away, since auto-get collects
// mid-fight. A monster whose death spell stalls the room (a silent "… temp" spell)
// is looked at once the stall has run out: the game throws away what is sent before
// then (GAME_MECHANICS "Silent death spells that stall the room").
//
// No Enter of its own when the room has been displayed since the kill, or an Enter
// has gone out since the kill's read began and its display is still to come (the
// combat engine's roster re-read, the cash engine's combat-clear look). Any Enter
// sent after the kill reached us is answered after it, so its display shows the
// floor, whichever death handler sent it first; during a stall only one sent as it
// ends counts. None either when we have left the room or a move is on its way, in a
// room too dark to see or while blind (the display shows no floor then), while the
// send gate is held, or with the master switch off. A `look <dir>` peek's display
// is another room's and settles nothing.
//
// The walker is held through AcquisitionGate from the kill until the display has
// been read (or ReleaseAfter, for a display that never comes). The kill clears the
// Combat gate in the same pass, so the hold goes up on the death itself, ahead of
// the roster resync: a walk step would otherwise leave first, and the Enter would
// show the next room. A fight that ends with no kill of ours (a party member's
// last blow, a flee) holds it the same way, through NoteFightEnded.
public sealed class PostKillFloorCheck : IDisposable
{
    public const string LogCategory = "FloorCheck";

    // The name the master switch counts this system's skips under.
    public const string SystemName = "Floor check after a kill";

    // How long the walker waits for the display the Enter asked for.
    public static readonly TimeSpan ReleaseAfter = TimeSpan.FromMilliseconds(1500);

    // Who a death is taken to be: the Monsters numbers it may be, the name they were
    // found by, and where that name came from (for the log and the bug report).
    public readonly record struct DeathAttribution(
        IReadOnlyCollection<int> Monsters, string? Name, string Source);

    // The look a kill has asked for and not yet had. AtOnce: an Auto-collect drop,
    // looked for while the fight goes on. EntersBefore: the bare Enters that can't
    // show the latest such kill, sent before its read began or already answered.
    // Stall: how long a death spell keeps the room from acting; StallTimer is armed
    // at the read's end and the look waits for it.
    private sealed class OwedLook(string why, bool atOnce, int entersBefore)
    {
        public string Why { get; } = why;
        public bool AtOnce { get; set; } = atOnce;
        public int EntersBefore { get; set; } = entersBefore;
        public int Kills { get; set; } = 1;
        public bool WaitLogged { get; set; }
        public TimeSpan Stall { get; set; }
        public IDisposable? StallTimer { get; set; }
    }

    private readonly MessageRouter _router;
    private readonly Func<MonsterDeathEvent, DeathAttribution> _attribute;
    private readonly Func<int, MonsterCatalogEntry?> _monster;
    private readonly Func<int, TimeSpan> _deathStall;
    private readonly Func<int, bool> _autoCollectWanted;
    private readonly Func<bool> _lookForAnyDrop;
    private readonly Func<bool> _hasEngageableHostiles;
    private readonly Func<bool> _canSeeRoom;
    private readonly Func<bool> _moveOnItsWay;
    private readonly Func<bool> _displayIsPeek;
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

    // The bare-Enter count when the server read now being dispatched began, and
    // when the last room display was read.
    private int _entersAtReadStart;
    private int _entersAtLastDisplay;
    private bool _readUnderway;

    // What the last kill that asked for a look came to, for the bug report.
    private string? _lastCheck;

    // Who the last death was taken to be, and how, for the bug report.
    public string? LastAttribution { get; private set; }

    // attribute: who the death was (see DeathAttribution). deathStall: how long a
    // monster's death spell stalls the room, zero for none. autoCollectWanted:
    // whether the item is one auto-get would take now (flagged Auto-collect,
    // takeable, Auto-Get Items on). lookForAnyDrop: the setting. displayIsPeek: the
    // display being read is a `look <dir>` peek's. bareEntersSent:
    // EngineSendGate.BareEntersSent. blocked: the master switch, asked with a
    // detail for its skip count.
    public PostKillFloorCheck(
        MessageRouter router,
        Func<MonsterDeathEvent, DeathAttribution> attribute,
        Func<int, MonsterCatalogEntry?> monster,
        Func<int, TimeSpan> deathStall,
        Func<int, bool> autoCollectWanted,
        Func<bool> lookForAnyDrop,
        Func<bool> hasEngageableHostiles,
        Func<bool> canSeeRoom,
        Func<bool> moveOnItsWay,
        Func<bool> displayIsPeek,
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
        _attribute = attribute;
        _monster = monster;
        _deathStall = deathStall;
        _autoCollectWanted = autoCollectWanted;
        _lookForAnyDrop = lookForAnyDrop;
        _hasEngageableHostiles = hasEngageableHostiles;
        _canSeeRoom = canSeeRoom;
        _moveOnItsWay = moveOnItsWay;
        _displayIsPeek = displayIsPeek;
        _sendGateHeld = sendGateHeld;
        _bareEntersSent = bareEntersSent;
        _blocked = blocked;
        _itemName = itemName;
        _log = log;
        _schedule = schedule ?? DispatcherTicks.Once;
        _now = now ?? (static () => DateTimeOffset.Now);
        _router.LineDispatched += OnLineDispatched;
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
              + (owed.StallTimer is not null ? ", waiting out a death spell's stall"
                 : owed.AtOnce ? "" : ", waiting for the room's fight to end")
            : _holding ? "waiting on the room display" : "idle";
        return _lastCheck is null ? state : $"{state}; last: {_lastCheck}";
    }

    // A monster we're credited with died. Subscribed ahead of the roster resync,
    // which can clear the Combat gate in this same pass.
    public void OnMonsterDied(MonsterDeathEvent evt)
    {
        if (_disposed) return;
        DeathAttribution who = _attribute(evt);
        LastAttribution = $"{who.Name ?? "(no name)"} ({who.Source})";
        _log?.Debug(LogCategory, $"death attributed to {LastAttribution}");

        TimeSpan stall = TimeSpan.Zero;
        foreach (int number in who.Monsters)
        {
            TimeSpan own = _deathStall(number);
            if (own > stall) stall = own;
        }

        if (WhyLook(who.Monsters) is not { } look)
        {
            // A later kill of the fight a look is owed for: the hold keeps the walker
            // for that look once this kill has ended the fight.
            if (_owed is not null)
            {
                NoteStall(_owed, stall);
                Hold();
            }
            return;
        }
        int entersBefore = Math.Max(_entersAtReadStart, _entersAtLastDisplay);
        if (_owed is null) _owed = new OwedLook(look.Why, look.AtOnce, entersBefore);
        else
        {
            _owed.Kills++;
            _owed.AtOnce |= look.AtOnce;
            _owed.EntersBefore = entersBefore;
        }
        NoteStall(_owed, stall);
        Hold();
    }

    // A death spell stalls the room: the look waits it out, and a stall that comes
    // later restarts the wait.
    private void NoteStall(OwedLook owed, TimeSpan stall)
    {
        if (stall <= TimeSpan.Zero) return;
        owed.StallTimer?.Dispose();
        owed.StallTimer = null;
        owed.Stall = stall;
    }

    // The room's fight is over (CombatStateTracker.FightEnded), just before the
    // walker may step: a look still owed holds it, and goes out at the read's end.
    public void NoteFightEnded()
    {
        if (_disposed || _owed is not { } owed) return;
        _log?.Debug(LogCategory, $"the room's fight is over — holding the walker for the look owed ({owed.Why})");
        // A stall's wait already holds it, for longer.
        if (owed.StallTimer is null) Hold();
    }

    // A room display was read (its exits line): it showed the floor.
    public void NoteRoomDisplayed()
    {
        if (_displayIsPeek()) return;
        _entersAtLastDisplay = _bareEntersSent();
        Settle("the room was displayed after the kill");
        ReleaseHold();
    }

    // We are in another room: the floor of the one the kill was in is behind us.
    public void OnRoomChanged()
    {
        Settle("left the room first");
        ReleaseHold();
    }

    // Out of the game (the board's menu, a dropped link): the room isn't known to
    // be the same on the way back in.
    public void NoteGameLeft()
    {
        Settle("left the game first");
        ReleaseHold();
    }

    private void Settle(string reason)
    {
        if (_owed is not { } owed) return;
        _owed = null;
        owed.StallTimer?.Dispose();
        Skipped(owed, reason);
    }

    // Why the death asks for a look, or null. An Auto-collect drop wins: it is the
    // one that is looked for while the fight goes on.
    private (string Why, bool AtOnce)? WhyLook(IReadOnlyCollection<int> dying)
    {
        bool anyDrop = _lookForAnyDrop();
        string? chance = null;
        string? names = null;
        foreach (int number in dying)
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

    private void OnLineDispatched(Terminal.LineExtractor.EmittedLine _)
    {
        if (_readUnderway) return;
        _readUnderway = true;
        _entersAtReadStart = _bareEntersSent();
    }

    // The read with the kill in it has ended: look now, unless a death spell's stall
    // is still to run out, or the fight is still on and nothing flagged Auto-collect
    // is waiting.
    private void OnReadEnded()
    {
        // First, so a handler below that throws can't leave the next read's
        // baseline unread.
        _readUnderway = false;
        if (_owed is not { } owed) return;
        if (owed.StallTimer is not null) return;
        if (owed.Stall > TimeSpan.Zero)
        {
            TimeSpan stall = owed.Stall;
            _log?.Debug(LogCategory,
                $"look owed ({owed.Why}) — waiting {stall.TotalMilliseconds:F0} ms for the death spell's stall to run out");
            owed.StallTimer = _schedule(stall, () => OnStallOver(owed, _bareEntersSent()));
            Hold(stall + ReleaseAfter);
            return;
        }
        Decide(owed);
    }

    // The stall has run out. An Enter sent while it ran (a death-spell nudge) was
    // thrown away; one sent as it ended (the cash engine's coin look) is answered.
    private void OnStallOver(OwedLook owed, int entersAtArm)
    {
        if (_disposed || !ReferenceEquals(_owed, owed)) return;
        owed.StallTimer = null;
        owed.Stall = TimeSpan.Zero;
        owed.EntersBefore = Math.Max(owed.EntersBefore, entersAtArm);
        Decide(owed);
    }

    private void Decide(OwedLook owed)
    {
        if (!owed.AtOnce && _hasEngageableHostiles())
        {
            if (!owed.WaitLogged)
            {
                owed.WaitLogged = true;
                _log?.Debug(LogCategory, $"look owed ({owed.Why}) — waiting for the room's fight to end");
            }
            // The Combat gate holds the walker meanwhile; the fight's end holds it again.
            ReleaseHold();
            return;
        }
        _owed = null;

        if (_bareEntersSent() > owed.EntersBefore)
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
        // Stamps the window the cash engine's combat-clear look checks, so that look
        // folds onto this Enter. A refusal here is for an Enter sent before the
        // kill's read or already answered, which shows nothing of the kill: ours
        // goes out all the same.
        _ = _redisplay?.ShouldSend();

        Hold();
        _wireSender!(new[] { (byte)'\r' });
        _lastCheck = $"{_now():HH:mm:ss} sent an Enter after {owed.Kills} kill(s): {owed.Why}";
        _log?.Info(LogCategory, $"re-displaying the room after {owed.Kills} kill(s): {owed.Why}");
    }

    private void Skipped(OwedLook owed, string reason)
    {
        _lastCheck = $"{_now():HH:mm:ss} no Enter after {owed.Kills} kill(s), {reason} ({owed.Why})";
        _log?.Debug(LogCategory, $"no Enter of our own: {reason} ({owed.Why})");
    }

    // Hold the walker, or restart the wait of a hold already up.
    private void Hold(TimeSpan? wait = null)
    {
        if (_gate is null) return;
        if (!_holding)
        {
            _holding = true;
            _gate.NoteReLookPending();
        }
        _holdRelease?.Dispose();
        _holdRelease = _schedule(wait ?? ReleaseAfter, OnHoldElapsed);
    }

    private void OnHoldElapsed()
    {
        _holdRelease = null;
        if (_holding)
            _log?.Debug(LogCategory, "no room display came in time — letting the walker go");
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
        _router.LineDispatched -= OnLineDispatched;
        _router.ReadEnded -= OnReadEnded;
        _owed?.StallTimer?.Dispose();
        ReleaseHold();
    }
}

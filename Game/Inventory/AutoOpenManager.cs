using System.Collections.Generic;
using System.Linq;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Auto-open containers engine. When a container item (ItemType == Container)
// flagged ItemOverlay.AutoOpen newly enters the pack, it is opened without the
// player doing it by hand. The engine only decides which arrivals to open and
// when. The open itself, and the inventory read that finds what it gave, are
// ChestOpenTracker's, so a container opened here lands on the Chest Offload list
// exactly as one opened from the window or the terminal does, and is counted once.
//
// Trigger: InventoryManager.Changed. Each change counts the carried copies of
// every container by resolved item Number, flagged or not, and diffs them
// against the previous snapshot; each new copy of a flagged one is owed one open.
// Counting them all is what keeps a flag change from looking like an arrival:
// ticking Auto-open on a container already in the pack opens nothing. The
// baseline is seeded silently on the first change seen once inventory is loaded
// (a full 'i'), so containers already carried at connect aren't opened either —
// only genuine new acquisitions are.
//
// One open at a time, since the tracker reads the pack after each. An owed open
// waits, and is not forgotten, while
//   - a fight is on, the character is resting or meditating, or a sneak is being
//     kept: on Stock `open <container>` ends all three (GAME_MECHANICS "Chests
//     and chest loot tables"; Paradigm is not recorded and is held the same);
//   - the client can't send (the engine send gate is up, or it isn't in the game):
//     the command would be dropped unsent;
//   - the tracker is still reading another open.
// It is forgotten, not held, when item automation is switched off (Auto Get Items,
// or the Auto-All switch): nothing opens later that arrived while it was off. The
// same goes for a Roomba sweep, whose carries are items being moved and not loot,
// and for a container whose flag is unticked before its turn. A copy that leaves
// the pack before its turn takes its open with it.
//
// A copy that comes back is not an arrival. What the hang-up check is picking
// back up off the floor is asked of that check while it is at it. What the
// character held when it died is counted here: the first full read after the
// death says how many copies really went, and that many arrivals are returns for
// as long as that death's pile is still open in the death history. A returned
// copy that was in the pack unopened because the player kept it so stays shut.
// One that was still owed its open when it went (picked up mid-fight, then the
// death or the hang-up) is owed it again when it comes back (user, 2026-10-10),
// through the same holds and only while still flagged. That memory is held here
// and lasts a reconnect; it does not outlive the client, whose next start seeds
// a new baseline from whatever is then in the pack.
//
// An open is tried once. A container still there afterwards (out of uses, or not
// this character's to use) stays in the pack, and nothing owes it another.
//
// UI-thread only (Inventory.Changed and the tracker's events marshal upstream),
// so the collections need no lock.
public sealed class AutoOpenManager
{
    // LogService category — [AutoOpen] rows per arrival, open and hold.
    public const string LogCategory = "AutoOpen";

    // One carried container: the item Number, the name to send to the game, and
    // whether it is flagged for the engine to open.
    public sealed record ResolvedOpen(int Number, string Name, bool AutoOpen);

    private readonly Func<IReadOnlyList<string>> _carried;
    private readonly Func<string, ResolvedOpen?> _resolve;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isLoaded;
    private readonly Func<string, bool> _open;
    private readonly LogService? _log;

    // item Number → count of that container seen in the previous carried
    // snapshot. Rebuilt on every change once seeded.
    private readonly Dictionary<int, int> _prevCounts = new();
    private bool _seeded;

    // One entry per open still owed, oldest first.
    private readonly List<ResolvedOpen> _owed = new();
    // The container handed to the tracker and not yet read.
    private ResolvedOpen? _opening;
    // Its copy has been seen to leave the pack: the next one to go is another.
    private bool _openingSeenGone;
    // item Number → copies carried when the character died, until the first full
    // read after the death says how many of them went. Null when none is awaited.
    private Dictionary<int, int>? _heldAtDeath;
    // item Number → copies that went at a death and haven't come back.
    private readonly Dictionary<int, int> _lostAtDeath = new();
    // The earliest death those belong to.
    private DateTimeOffset _diedAt;
    // item Number → opens that were owed for copies which then went out of the
    // pack at a death or to a hang-up penalty: owed again as copies come back.
    private readonly Dictionary<int, (ResolvedOpen Item, int Count)> _owedOut = new();
    // The game was entered and the pack not read since: what that read shows gone
    // went while the character was out of the game.
    private bool _awaitingEntryRead;
    // item Number → copies that read found gone.
    private readonly Dictionary<int, int> _goneWhileOut = new();
    // The hold last logged, so one hold is one log line.
    private string? _heldFor;

    // resolve: a carried entry's container record, or null when it isn't a
    //          container.
    // open:    hands one container to ChestOpenTracker (TryOpenNow). False when
    //          the tracker is busy; the open stays owed.
    public AutoOpenManager(
        Func<IReadOnlyList<string>> carriedItems,
        Func<string, ResolvedOpen?> resolve,
        Func<bool> isEnabled,
        Func<bool> isLoaded,
        Func<string, bool> open,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(carriedItems);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(isLoaded);
        ArgumentNullException.ThrowIfNull(open);
        _carried = carriedItems;
        _resolve = resolve;
        _isEnabled = isEnabled;
        _isLoaded = isLoaded;
        _open = open;
        _log = log;
    }

    // True while a Roomba sweep is sorting the house: a container it carries is on
    // its way to another room, and opening it would destroy what was being sorted.
    public Func<bool> SuppressDuringSweep { get; set; } = static () => false;

    // False while a command sent now would be dropped unsent.
    public Func<bool> SendGateOpen { get; set; } = static () => true;

    public Func<bool> InCombat { get; set; } = static () => false;

    // A rest or a meditate is being waited out (the hold the door, winch, search
    // and trap helpers share).
    public Func<bool> Resting { get; set; } = static () => false;

    // SneakGuard.Holds.
    public Func<bool> SneakKept { get; set; } = static () => false;

    // Copies of the named item the hang-up check has asked the game for and not
    // yet seen arrive (HangupItemRecheck.BeingPickedUp).
    public Func<string, int> ComingBack { get; set; } = static _ => 0;

    // Whether a death no earlier than the given time still has its pile open in
    // the death history (not recovered, not found missing, not cleared).
    public Func<DateTimeOffset, bool> DeathpileOpenSince { get; set; } = static _ => true;

    // ----- state for the bug report ---------------------------------------

    public IReadOnlyList<string> Owed => _owed.Select(o => o.Name).ToList();

    public string? Opening => _opening?.Name;

    // Why an owed open isn't going out, or null when nothing is owed or held.
    public string? HeldFor => _owed.Count == 0 ? null : WhyOff() ?? WhyHeld() ?? _heldFor;

    // The last container opened here, when, and what it gave.
    public string? LastOpen { get; private set; }

    // Copies lost at a death that would not count as arrivals if they came back.
    public int AwaitedFromDeath => _lostAtDeath.Values.Sum();

    // Opens owed for copies that went at a death or to a hang-up, should they return.
    public int OwedIfReturned => _owedOut.Values.Sum(o => o.Count);

    // Re-evaluate the pack on any inventory change: each flagged container that
    // newly entered it is owed an open.
    public void OnInventoryChanged()
    {
        // Wait for the first full 'i' so the baseline reflects the real pack —
        // a coin pickup can fire Changed before any inventory dump.
        if (!_isLoaded()) return;

        // Group current carried copies by resolved item Number. A pile is one
        // entry standing for all its copies, so a container that joins one already
        // carried still reads as an arrival.
        Dictionary<int, (ResolvedOpen Item, int Count)> current = new();
        foreach ((string name, int copies) in InventorySnapshot.Stacks(_carried()))
        {
            if (_resolve(name) is not { } item) continue;
            if (current.TryGetValue(item.Number, out (ResolvedOpen Item, int Count) g))
                current[item.Number] = (item, g.Count + copies);
            else
                current[item.Number] = (item, copies);
        }

        // Seed the baseline silently the first time (once loaded) so containers
        // already carried at connect aren't opened — only later acquisitions do.
        if (!_seeded)
        {
            _seeded = true;
            RebaseTo(current);
            return;
        }

        // Off: the baseline still follows the pack, so switching back on doesn't
        // open a container acquired meanwhile.
        string? off = WhyOff();
        if (off is not null) Forget(off);

        foreach (int number in _prevCounts.Keys.Union(current.Keys).ToList())
        {
            current.TryGetValue(number, out (ResolvedOpen Item, int Count) now);
            int moved = now.Count - _prevCounts.GetValueOrDefault(number);
            if (moved < 0) Left(number, -moved);
            else if (moved > 0) Arrived(now.Item, moved, off is not null);
        }

        RebaseTo(current);
        Recheck();
    }

    // InventoryManager.FullInventoryParsed, after the change it raised. The first
    // one after a death is what says which containers really went into the pile:
    // one that stayed in the pack, or was back before this read, is not awaited.
    public void OnFullInventoryRead()
    {
        _awaitingEntryRead = false;
        if (_heldAtDeath is { } held)
        {
            _heldAtDeath = null;
            foreach ((int number, int count) in held)
            {
                int now = _prevCounts.GetValueOrDefault(number);
                if (count > now) _lostAtDeath[number] = _lostAtDeath.GetValueOrDefault(number) + count - now;
                // Still in the pack and still owed its open.
                Reowe(number, Math.Min(count, now));
            }
            if (_lostAtDeath.Count > 0)
                _log?.Info(LogCategory,
                    $"{AwaitedFromDeath} container(s) went with the death: a copy that comes back while that pile "
                    + $"is being recovered is not a new one ({OwedIfReturned} of them owed an open, and opened then)");
        }
        Recheck();
    }

    // InGameCapture.InGameChanged(true): the next full read is the first of this
    // stay in the game.
    public void OnEnteredGame()
    {
        _awaitingEntryRead = true;
        _goneWhileOut.Clear();
    }

    // RoomTracker.UnwitnessedDeathRecorded: a death the client did not see (a
    // hang-up on a board that kills for it) was worked out on the way back in.
    // What the entry read found gone went into that death's pile, and coming
    // back from it is no more an arrival than after a death that was seen.
    public void OnUnwitnessedDeath(DateTimeOffset diedAt)
    {
        if (_goneWhileOut.Count == 0) return;
        if (_lostAtDeath.Count == 0 || diedAt < _diedAt) _diedAt = diedAt;
        foreach ((int number, int count) in _goneWhileOut)
            _lostAtDeath[number] = _lostAtDeath.GetValueOrDefault(number) + count;
        _goneWhileOut.Clear();
        _log?.Info(LogCategory,
            $"{AwaitedFromDeath} container(s) went with a death while out of the game: a copy that comes back "
            + "while that pile is being recovered is not a new one");
    }

    // Something that kept an owed open back may have cleared, or the engine may
    // have been switched off: send the next one if nothing holds it now.
    public void Recheck()
    {
        if (_owed.Count == 0)
        {
            _heldFor = null;
            return;
        }
        if (WhyOff() is { } off)
        {
            Forget(off);
            return;
        }

        string? why = WhyHeld();
        while (why is null && _owed.Count > 0)
        {
            ResolvedOpen next = _owed[0];
            // Unticked while it waited.
            if (_resolve(next.Name) is not { AutoOpen: true })
            {
                _owed.RemoveAt(0);
                _log?.Info(LogCategory, $"{next.Name} is no longer flagged Auto-open — not opened");
                continue;
            }
            if (_open(next.Name))
            {
                _owed.RemoveAt(0);
                _opening = next;
                _openingSeenGone = false;
                _heldFor = null;
                _log?.Info(LogCategory,
                    $"open container item={next.Name}" + (_owed.Count > 0 ? $" ({_owed.Count} more owed)" : ""));
                return;
            }
            why = "another open is being read";
        }
        if (_owed.Count == 0)
        {
            _heldFor = null;
            return;
        }

        // Waiting on our own open is the ordinary one-at-a-time step, not a hold.
        if (_opening is null && why != _heldFor)
            _log?.Info(LogCategory, $"holding {_owed.Count} open(s) — {why}");
        _heldFor = why;
    }

    // ChestOpenTracker.OpenSettled: an open is over, ours or not.
    public void OnOpenSettled(ChestOpenTracker.OpenResult result)
    {
        if (_opening is { } done)
        {
            _opening = null;
            string outcome;
            if (!result.Read)
                outcome = "its contents were not read (no inventory read came back); it is not tried again";
            else if (result.Items.Count == 0 && result.Coin.TotalCopperValue == 0)
                outcome = "it gave nothing; it is not tried again";
            else
                outcome = "gave " + string.Join(", ", result.Items.Select(i => $"{i.Count} {i.Name}")
                    .Append($"{result.Coin.TotalCopperValue}c"));
            LastOpen = $"{done.Name} at {DateTimeOffset.Now:HH:mm:ss} — {outcome}";
            _log?.Info(LogCategory, $"opened item={done.Name} — {outcome}");
        }
        Recheck();
    }

    // The character died. What it carried may be on the floor or in its corpse,
    // and getting that back is not acquiring it. The pack as the client knows it
    // still lists everything until it is read again, so how much really went is
    // settled at that read (OnFullInventoryRead).
    public void OnPlayerDied()
    {
        if (_owed.Count > 0)
            _log?.Info(LogCategory,
                $"{_owed.Count} container(s) were owed an open when the character died: opened if they are recovered");
        foreach (ResolvedOpen owed in _owed) RememberOwedOut(owed, 1);
        _owed.Clear();
        _heldFor = null;

        if (_heldAtDeath is null && _lostAtDeath.Count == 0) _diedAt = DateTimeOffset.UtcNow;
        _heldAtDeath ??= new Dictionary<int, int>();
        foreach ((int number, int count) in _prevCounts)
            if (count > 0) _heldAtDeath[number] = Math.Max(_heldAtDeath.GetValueOrDefault(number), count);
    }

    // A different character: nothing known about the last one's pack holds.
    public void Reset()
    {
        _prevCounts.Clear();
        _seeded = false;
        _owed.Clear();
        _opening = null;
        _heldAtDeath = null;
        _lostAtDeath.Clear();
        _owedOut.Clear();
        _awaitingEntryRead = false;
        _goneWhileOut.Clear();
        _heldFor = null;
        LastOpen = null;
    }

    private void Arrived(ResolvedOpen item, int copies, bool off)
    {
        // Between a death and the read after it the pack is not known: what shows
        // up then is the pile coming back, and that read does the counting.
        if (_heldAtDeath is not null) return;

        int back = ReturningFromDeath(item.Number, copies);
        back += Math.Min(copies - back, Math.Max(0, ComingBack(item.Name)));
        // Of the copies coming back, those that still had an open owed.
        int reowed = Reowe(item.Number, back);
        if (back > reowed && item.AutoOpen)
            _log?.Info(LogCategory,
                $"{back - reowed}x {item.Name} back in the pack (lost at a death or to a hang-up) — not opened");

        int fresh = copies - back;
        if (fresh <= 0 || off || !item.AutoOpen) return;
        for (int i = 0; i < fresh; i++) _owed.Add(item);
        _log?.Info(LogCategory, $"{fresh}x {item.Name} arrived — {_owed.Count} open(s) owed");
    }

    private void RememberOwedOut(ResolvedOpen item, int copies)
    {
        _owedOut.TryGetValue(item.Number, out (ResolvedOpen Item, int Count) had);
        _owedOut[item.Number] = (item, had.Count + copies);
    }

    // Up to `copies` returned copies of this container take back the opens they
    // were owed when they went. Whether they are still wanted (the flag, the
    // switches) is settled when each comes to its turn, as for any owed open.
    private int Reowe(int number, int copies)
    {
        if (copies <= 0 || !_owedOut.TryGetValue(number, out (ResolvedOpen Item, int Count) away)) return 0;
        int reowed = Math.Min(copies, away.Count);
        if (reowed == away.Count) _owedOut.Remove(number);
        else _owedOut[number] = (away.Item, away.Count - reowed);
        for (int i = 0; i < reowed; i++) _owed.Add(away.Item);
        _log?.Info(LogCategory,
            $"{reowed}x {away.Item.Name} back in the pack and still owed an open — {_owed.Count} open(s) owed");
        return reowed;
    }

    // How many of these arriving copies are ones lost at a death. The allowance
    // lasts as long as that death's pile is open: once it is recovered, found
    // missing or cleared, a copy that turns up is a new one.
    private int ReturningFromDeath(int number, int copies)
    {
        if (_lostAtDeath.Count == 0) return 0;
        if (!DeathpileOpenSince(_diedAt))
        {
            _log?.Info(LogCategory,
                $"the deathpile is no longer being recovered — {AwaitedFromDeath} container(s) are no longer awaited from it");
            _lostAtDeath.Clear();
            _owedOut.Clear();
            return 0;
        }
        int back = Math.Min(copies, _lostAtDeath.GetValueOrDefault(number));
        if (back <= 0) return 0;
        if (back == _lostAtDeath[number]) _lostAtDeath.Remove(number);
        else _lostAtDeath[number] -= back;
        return back;
    }

    private void Left(int number, int copies)
    {
        if (_awaitingEntryRead) _goneWhileOut[number] = _goneWhileOut.GetValueOrDefault(number) + copies;

        // The first copy to go after our own open is the one it opened.
        if (_opening is { } opening && opening.Number == number && !_openingSeenGone)
        {
            _openingSeenGone = true;
            copies--;
        }

        int dropped = 0;
        ResolvedOpen? gone = null;
        for (int i = _owed.Count - 1; i >= 0 && dropped < copies; i--)
        {
            if (_owed[i].Number != number) continue;
            gone = _owed[i];
            _owed.RemoveAt(i);
            dropped++;
        }
        if (gone is null) return;

        // Found gone at the first read of a stay in the game: it went while the
        // character was out of it, which is what a hang-up penalty does. If the
        // hang-up check picks it back up, it is still owed.
        if (_awaitingEntryRead)
        {
            RememberOwedOut(gone, dropped);
            _log?.Info(LogCategory,
                $"{dropped}x {gone.Name} left the pack while out of the game, unopened — owed again if picked back up");
        }
        else
            _log?.Info(LogCategory, $"{dropped}x {gone.Name} left the pack before being opened — no longer owed");
    }

    // Why nothing is opened at all just now, or null.
    private string? WhyOff()
    {
        if (!_isEnabled()) return "item automation is switched off";
        if (SuppressDuringSweep()) return "a Roomba sweep is running";
        return null;
    }

    // Why the next owed open has to wait, or null.
    private string? WhyHeld()
    {
        if (_opening is not null) return $"{_opening.Name} is being opened";
        if (!SendGateOpen()) return "the client can't send just now";
        // A hang-up penalty may have dropped it: the read says whether it is there.
        if (_awaitingEntryRead) return "the pack hasn't been read since the game was entered";
        if (InCombat()) return "in a fight";
        if (Resting()) return "resting";
        if (SneakKept()) return "a sneak is being kept";
        return null;
    }

    private void Forget(string why)
    {
        if (_owed.Count == 0) return;
        _log?.Info(LogCategory, $"{_owed.Count} owed open(s) forgotten — {why}");
        _owed.Clear();
        _heldFor = null;
    }

    // Replace the baseline with the current container counts.
    private void RebaseTo(Dictionary<int, (ResolvedOpen Item, int Count)> current)
    {
        _prevCounts.Clear();
        foreach ((int number, (ResolvedOpen _, int count)) in current)
            _prevCounts[number] = count;
    }
}

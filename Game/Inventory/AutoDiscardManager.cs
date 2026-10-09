using System.Collections.Generic;
using System.Linq;
using System.Text;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Inventory;

// Auto-discard items engine. When the pack holds an item flagged
// ItemOverlay.AutoDiscard above its keep floor, offloads the excess with
// drop <item name> — or hide <item name> when HideMode is set (OtherSettings
// "hide when discarding") — one command per copy (MajorMUD has no bulk-drop
// verb). The keep floor is MinToKeep when MustHaveMinimum is set; otherwise
// zero, so an unbanded flagged item is discarded entirely (the confirmed
// "no band → discard all" rule). A LoyalItem is never discarded even if also
// flagged AutoDiscard — loyalty (never-drop) is the safety flag and wins the
// contradiction.
//
// Exists to clean up chest dumps — open chest pours a set of random items
// straight into inventory that the player can't refuse — and unwanted
// auto-collected loot.
//
// Trigger: InventoryManager.Changed (wired in AppServices to OnInventoryChanged).
// Every inventory change re-evaluates the carried list. Offloads the engine has
// sent but not yet seen confirmed are held in _inFlight (drops) or _hidesInFlight
// (hides) and subtracted from the live count, so the Changed events its own
// confirmations raise don't re-send. Own "You dropped X." lines clear the drop
// count via the PlayerDrops subscription; "You hid X." clears the hide count via
// UserHides. Other players' drops (same PlayerDrops pattern, alternate branch)
// are ignored.
//
// Each hide is also registered in _suppressLog so the transaction-history
// forwarder can tell a discard's hide from a genuine stash — it is a discard, not
// a "stashed" item, so TryConsumeSuppressedHide lets the forwarder drop that
// ledger row while manual / stash-room hides (never registered here) still record.
//
// The hide-or-drop choice is one decision for every discard, not only the
// engine's own: a surface that discards by hand (Chest Offload's Drop buttons)
// sends through EmitDiscard, so the setting covers it and its hides stay out of
// the stash ledger the same way.
//
// A hide the room has no room for (Stock only: its rooms cap their hidden items)
// keeps the item and is sent again in each new room the character enters until
// it lands (user, 2026-10-09). It is never turned into a drop.
//
// A hide for a copy that isn't in the pack is worse than wasted: Stock's engine
// doesn't answer it, and the line then falls through to speech. So no hide goes
// out for more copies than are carried and not already spoken for, and whatever
// is still unanswered is forgotten on a death, a disconnect, a profile swap, and
// once two inventory reads pass with no discard traffic between them.
//
// Master switch: AutoActionDefaults.AutoDiscard (shared with the Settings and
// Action-menu toggle). Runs UI-thread only (MessageRouter + Inventory.Changed
// both marshal upstream), so the dictionaries need no lock.
public sealed class AutoDiscardManager : IDisposable
{
    // LogService category — [AutoDiscard] rows per dropped item.
    public const string LogCategory = "AutoDiscard";

    // One resolved carried entry: the canonical item Number, the name to send to
    // the game, whether the user flagged it for discard, and how many copies to
    // keep (0 = discard all).
    public sealed record ResolvedDiscard(int Number, string Name, bool Discard, int KeepCount);

    private readonly Func<IReadOnlyList<string>> _carried;
    private readonly Func<string, ResolvedDiscard?> _resolve;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isParadigm;
    private readonly LogService? _log;
    private readonly IDisposable _dropSub;
    private readonly IDisposable _hideSub;
    private readonly IDisposable _hideRefusedSub;

    // item Number → the engine's drops sent but not yet confirmed by a self
    // "You dropped X.".
    private readonly Dictionary<int, int> _inFlight = new();

    // item Number → hides sent (the engine's, a by-hand surface's, a retry's) but
    // not yet confirmed by a self "You hid X.".
    private readonly Dictionary<int, int> _hidesInFlight = new();

    // item Number → discard hides not yet claimed by the transaction-log
    // forwarder, so a hide that is a discard is kept out of the stash ledger.
    private readonly Dictionary<int, int> _suppressLog = new();

    // item Number → copies whose hide the room refused, kept until the character
    // stands in another room. The name is the one to send.
    private readonly Dictionary<int, (string Name, int Count)> _held = new();

    // Items with a held hide sent again and not yet settled, so its landing can be
    // told from an ordinary one in the log.
    private readonly HashSet<int> _retrying = new();

    // The room the character stands in, as last confirmed.
    private RoomKey? _room;

    // Whether a discard was sent or answered since the last full inventory read.
    private bool _trafficSinceRead;

    private bool _hideMode;

    // When true, offload with hide <item> (conceal on the ground) instead of
    // drop <item>. Live-mirrored from OtherSettings via AppServices ApplyToServices.
    // Turning it off gives up the hides waiting for a room: the player no longer
    // wants items hidden, and the engine's own are picked up again as drops.
    public bool HideMode
    {
        get => _hideMode;
        set
        {
            if (_hideMode == value) return;
            _hideMode = value;
            if (value || _held.Count == 0) return;
            _log?.Info(LogCategory, $"hide when discarding turned off: {DescribeHeld()} no longer waiting for a room");
            _held.Clear();
        }
    }

    // The verb a discard goes out with right now.
    public string DiscardVerb => HideMode ? "hide" : "drop";

    // A discard's hide was refused for want of room ("There is no room to hide X
    // here.") and is held for the next room, with the item the game named. A hide
    // this engine never sent (typed, a stash room's, Hide All's) doesn't raise it.
    public event Action<string>? HideRefused;

    // True while a Roomba sweep is underway. Auto-discard is held off for the whole
    // sweep so it can't bin an item Roomba is in the middle of relocating — Roomba
    // sorts auto-discard-flagged items into their labeled rooms instead. Normal
    // auto-discard resumes the moment the sweep ends, clearing anything that
    // accumulated. Wired by AppServices to GhSweepManager.IsActive; defaults to
    // never-suppress.
    public Func<bool> SuppressDuringSweep { get; set; } = static () => false;

    // Sends a batch a few at a time. A room's worth of held hides goes out right
    // after a move, when Stock queues commands behind the move's delay and drops
    // them past a dozen. Unbound (tests): each goes straight out.
    public Action<IReadOnlyList<string>>? PacedSender { get; set; }

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    public AutoDiscardManager(
        MessageRouter router,
        Func<IReadOnlyList<string>> carriedItems,
        Func<string, ResolvedDiscard?> resolve,
        Func<bool> isEnabled,
        LogService? log = null,
        Func<bool>? isParadigm = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(carriedItems);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(isEnabled);
        _carried = carriedItems;
        _resolve = resolve;
        _isEnabled = isEnabled;
        // Unbound (tests) → Stock behaviour: one drop/hide per copy, never batched.
        _isParadigm = isParadigm ?? (static () => false);
        _log = log;

        _dropSub = router.Subscribe(KnownPatterns.PlayerDrops, OnDropLine);
        _hideSub = router.Subscribe(KnownPatterns.UserHides, OnHideLine);
        _hideRefusedSub = router.Subscribe(KnownPatterns.RoomHideRefused, OnHideRefusedLine);
    }

    // Bind the wire sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Re-evaluate the carried list on any inventory change and drop each flagged
    // item down to its keep floor.
    public void OnInventoryChanged()
    {
        if (!_isEnabled() || _wireSender is null) return;
        // A Roomba sweep is sorting the house — don't bin an item it may be
        // relocating; Roomba sorts auto-discard-flagged items itself.
        if (SuppressDuringSweep()) return;

        // Group carried copies by resolved item Number so duplicate name strings
        // ("a torch", "a torch") count as two of one item.
        Dictionary<int, (ResolvedDiscard Item, int Count)> groups = new();
        foreach (string entry in _carried())
        {
            if (_resolve(entry) is not { Discard: true } item) continue;
            if (groups.TryGetValue(item.Number, out (ResolvedDiscard Item, int Count) g))
                groups[item.Number] = (g.Item, g.Count + 1);
            else
                groups[item.Number] = (item, 1);
        }

        foreach ((int number, (ResolvedDiscard item, int count)) in groups)
        {
            // Count that will remain once the outstanding offloads land. A copy
            // held for the next room is spoken for too: this room refused it.
            int projected = count - SpokenFor(number);
            int toDrop = projected - item.KeepCount;
            if (toDrop <= 0) continue;

            _log?.Info(LogCategory, $"discard {toDrop}x item={item.Name} via {DiscardVerb} (keep {item.KeepCount})");
            // The count-prefixed confirmation clears in-flight by N below.
            Offload(Send, number, item.Name, toDrop, byHand: false);
        }
    }

    // Send a discard of up to `count` copies of `name` for a surface that discards
    // by hand, in the engine's own wording: hide or drop by HideMode, counted the
    // way the realm takes it. Only copies that are carried and not already spoken
    // for go out, so a second press before the first is answered sends nothing.
    // Returns the verb and how many copies it sent.
    public (string Verb, int Sent) EmitDiscard(Action<string> send, string name, int count)
    {
        ArgumentNullException.ThrowIfNull(send);
        string verb = DiscardVerb;
        // A blank name would leave a bare `hide`, which hides the character.
        if (string.IsNullOrWhiteSpace(name) || _resolve(name) is not { } item) return (verb, 0);
        int free = Math.Min(count, CarriedCopies(item.Number)) - SpokenFor(item.Number);
        if (free <= 0) return (verb, 0);
        Offload(send, item.Number, name, free, byHand: true);
        return (verb, free);
    }

    // Copies of `name` whose hide a room refused, waiting for the next one.
    public int HeldFor(string name)
        => _resolve(name) is { } item && _held.TryGetValue(item.Number, out (string Name, int Count) h) ? h.Count : 0;

    // Every held hide, and how many hides are out with no answer yet, for the bug
    // report.
    public IReadOnlyList<(string Name, int Count)> HeldHides => _held.Values.ToList();
    public int UnansweredHides => _hidesInFlight.Values.Sum();

    // Copies of an item a discard is already out for, or waiting on a room for.
    private int SpokenFor(int number)
        => _inFlight.GetValueOrDefault(number) + _hidesInFlight.GetValueOrDefault(number)
           + (_held.TryGetValue(number, out (string Name, int Count) h) ? h.Count : 0);

    // Copies of an item in the pack. The carried list holds single entries and,
    // after an `i`, a stack as one entry with its count in front ("3 moonstone").
    private int CarriedCopies(int number)
    {
        int copies = 0;
        foreach (string entry in _carried())
            if (_resolve(entry) is { } item && item.Number == number)
                copies += CountedCommand.SplitLeadingCount(entry.Trim()).Count;
        return copies;
    }

    // Paradigm offloads the pile in one `{verb} N <item>`; Stock sends one per
    // copy. A hide is counted until its "You hid X." and registered so the
    // transaction log can tell it from a stash. A by-hand drop is not counted:
    // a repeat of it only earns the game's private "You don't have X to drop!".
    private void Offload(Action<string> send, int number, string name, int count, bool byHand)
    {
        if (count <= 0) return;
        _trafficSinceRead = true;
        if (HideMode)
        {
            _hidesInFlight[number] = _hidesInFlight.GetValueOrDefault(number) + count;
            _suppressLog[number] = _suppressLog.GetValueOrDefault(number) + count;
        }
        else if (!byHand)
        {
            _inFlight[number] = _inFlight.GetValueOrDefault(number) + count;
        }
        CountedCommand.Emit(send, DiscardVerb, count, name, _isParadigm());
    }

    // Clear a pending drop when our own "You dropped X." confirmation arrives.
    // PlayerDrops is a combined pattern (Groups[0] = the other-player name for
    // "<name> drops X.", empty for the self "You dropped X." branch); only the
    // self branch confirms a command we sent.
    private void OnDropLine(MatchResult m)
    {
        if (m.Groups.Count < 2) return;
        if (!string.IsNullOrEmpty(m.Groups[0])) return;   // another player's drop
        Settle(_inFlight, m.Groups[1]);
    }

    // Clear a pending hide when our own "You hid X." confirmation arrives.
    // UserHides is a self-only single-group pattern (Groups[0] = the hidden item),
    // so unlike PlayerDrops there's no other-player branch to filter out. Coin
    // hides ("You hid 10 gold.") don't resolve to an item, so they fall through
    // Settle's resolve guard harmlessly.
    private void OnHideLine(MatchResult m)
    {
        if (m.Groups.Count < 1) return;
        if (Settle(_hidesInFlight, m.Groups[0]) is not { } item) return;
        if (!_retrying.Contains(item.Number) || SpokenFor(item.Number) > 0) return;
        _retrying.Remove(item.Number);
        _log?.Info(LogCategory, $"held hide of {item.Name} landed in {_room}");
    }

    // The room has no room left for hidden items. The copy stays in the pack and
    // waits for the next room; this one would only refuse it again. Only a hide
    // registered as a discard is carried on with: a typed hide, a stash room's or
    // Hide All's belongs to whoever sent it. Its ledger registration goes with the
    // refusal (no "You hid X." will claim it) and the retry takes out a fresh one.
    private void OnHideRefusedLine(MatchResult m)
    {
        if (m.Groups.Count < 1) return;
        string token = m.Groups[0];
        if (!TryConsumeSuppressedHide(token)) return;
        (int count, string name) = CountedCommand.SplitLeadingCount(token);
        if (_resolve(name) is not { } item) return;
        Reduce(_hidesInFlight, item.Number, count);
        if (!HideMode)
        {
            _log?.Info(LogCategory, $"hide of {item.Name} refused, no room here: not held, hide when discarding is off");
            return;
        }
        int waiting = (_held.TryGetValue(item.Number, out (string Name, int Count) h) ? h.Count : 0) + count;
        _held[item.Number] = (item.Name, waiting);
        _log?.Info(LogCategory, $"hide of {item.Name} refused, no room here: {waiting} held for the next room");
        HideRefused?.Invoke(item.Name);
    }

    // The character stands in a room, by the room tracker's confirmed reading. A
    // redisplay of the same room is not an arrival: that room has refused already.
    public void OnRoomEntered(RoomKey room)
    {
        if (_room is { } here && here.Equals(room)) return;
        _room = room;
        RetryHeldHides();
    }

    // Send each held hide once in the room just entered. A refusal here holds it
    // again for the room after.
    private void RetryHeldHides()
    {
        if (_held.Count == 0) return;
        // The gates the engine's own offloads wait on; the copies stay held.
        if (_wireSender is null || SuppressDuringSweep()) return;

        List<string> commands = new();
        foreach ((int number, (string name, int count)) in _held.ToList())
        {
            _held.Remove(number);
            // An item flagged for auto-discard is the engine's to offload, and its
            // switch is off; a by-hand discard never answered to that switch.
            if (_resolve(name) is { Discard: true } && !_isEnabled())
            {
                _log?.Info(LogCategory, $"held hide of {name} given up: auto-discard is off");
                _retrying.Remove(number);
                continue;
            }
            int copies = Math.Min(count, CarriedCopies(number) - SpokenFor(number));
            if (copies <= 0)
            {
                _log?.Info(LogCategory, $"held hide of {name} forgotten: no longer carried");
                _retrying.Remove(number);
                continue;
            }
            _log?.Info(LogCategory, $"retrying held hide of {copies}x {name} in {_room}");
            _retrying.Add(number);
            Offload(commands.Add, number, name, copies, byHand: false);
        }
        if (commands.Count == 0) return;
        if (PacedSender is { } paced) paced(commands);
        else foreach (string command in commands) Send(command);
    }

    // A full `i` listing landed. A hide for a copy the pack no longer holds can't
    // be answered, so nothing stays counted past what is carried. And once a whole
    // read-to-read stretch passes with no discard sent or answered, whatever is
    // still counted never will be (a refusal with no line of its own, a command
    // the game dropped for coming too fast): forget it, or it would block every
    // later discard of that item and swallow a later stash's ledger row.
    public void OnFullInventoryRead()
    {
        foreach (int number in _hidesInFlight.Keys.Concat(_suppressLog.Keys).Concat(_held.Keys).Distinct().ToList())
        {
            int carried = CarriedCopies(number);
            Cap(_hidesInFlight, number, carried);
            Cap(_suppressLog, number, carried);
            if (!_held.TryGetValue(number, out (string Name, int Count) h) || h.Count <= carried) continue;
            if (carried <= 0) _held.Remove(number);
            else _held[number] = (h.Name, carried);
        }

        if (!_trafficSinceRead && (_hidesInFlight.Count > 0 || _suppressLog.Count > 0))
        {
            _log?.Info(LogCategory,
                $"{_hidesInFlight.Values.Sum()} hide(s) never answered: no longer counted as on their way");
            _hidesInFlight.Clear();
            _suppressLog.Clear();
            _retrying.Clear();
        }
        _trafficSinceRead = false;
    }

    // Nothing sent before this point will be answered (a death empties the pack,
    // a dropped connection loses the replies, another character has its own pack).
    public void Reset(string reason)
    {
        if (_inFlight.Count + _hidesInFlight.Count + _held.Count > 0)
            _log?.Info(LogCategory,
                $"{reason}: {_inFlight.Values.Sum() + _hidesInFlight.Values.Sum()} unanswered discard(s)"
                + (_held.Count > 0 ? $" and held hides ({DescribeHeld()})" : "") + " forgotten");
        _inFlight.Clear();
        _hidesInFlight.Clear();
        _suppressLog.Clear();
        _held.Clear();
        _retrying.Clear();
        _room = null;
        _trafficSinceRead = false;
    }

    private string DescribeHeld()
        => string.Join(", ", _held.Values.Select(h => $"{h.Count} {h.Name}"));

    // Take the item named in a self drop/hide confirmation off its count, so the
    // Changed event that confirmation raises doesn't re-send. Paradigm confirms a
    // batched offload in one counted line ("You dropped 5 orc-head."), so strip
    // the count and clear that many. Returns the item when the line names one.
    private ResolvedDiscard? Settle(Dictionary<int, int> counts, string itemToken)
    {
        (int count, string name) = CountedCommand.SplitLeadingCount(itemToken);
        if (_resolve(name) is not { } item) return null;
        if (Reduce(counts, item.Number, count)) _trafficSinceRead = true;
        return item;
    }

    // Lower a count by up to `by`, dropping the entry at zero. False when there
    // was nothing to lower.
    private static bool Reduce(Dictionary<int, int> counts, int number, int by)
    {
        if (!counts.TryGetValue(number, out int n) || n <= 0) return false;
        if (n <= by) counts.Remove(number);
        else counts[number] = n - by;
        return true;
    }

    private static void Cap(Dictionary<int, int> counts, int number, int max)
    {
        if (!counts.TryGetValue(number, out int n) || n <= max) return;
        if (max <= 0) counts.Remove(number);
        else counts[number] = max;
    }

    // The transaction-history forwarder calls this on every "You hid X." to ask
    // whether the hide was a discard (skip the stash ledger) or a genuine manual /
    // stash-room hide (record it). A discard's hide was registered in _suppressLog
    // at send time; claim it here (returns true). A hide we never initiated isn't
    // registered (returns false), so it still records.
    public bool TryConsumeSuppressedHide(string itemName)
    {
        (int count, string name) = CountedCommand.SplitLeadingCount(itemName);
        if (_resolve(name) is not { } item) return false;
        if (!Reduce(_suppressLog, item.Number, count)) return false;
        _trafficSinceRead = true;
        return true;
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dropSub.Dispose();
        _hideSub.Dispose();
        _hideRefusedSub.Dispose();
    }
}

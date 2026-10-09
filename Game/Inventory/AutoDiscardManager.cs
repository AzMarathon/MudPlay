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
// it lands (user, 2026-10-09). It is never turned into a drop. The hold is for a
// hide still wanted: selling the item, dropping it, hiding it by hand, or taking
// it off the Chest Offload list calls the hold off (ReleaseHeld), since by then
// any copy left in the pack may be one the player means to keep.
//
// A hide for a copy that isn't in the pack is worse than wasted: Stock's engine
// doesn't answer it, and the line then falls through to speech. So no hide goes
// out for more copies than are carried and not already spoken for, and whatever
// is still unanswered is forgotten on a death, a disconnect, a profile swap, and
// once two inventory reads pass with no discard traffic between them.
//
// Master switch: Auto Get Items (AutoActionDefaults.AutoGetItems, the Settings
// and Action-menu toggle the item engines share). Runs UI-thread only
// (MessageRouter + Inventory.Changed both marshal upstream), so the dictionaries
// need no lock.
public sealed class AutoDiscardManager : IDisposable
{
    // LogService category — [AutoDiscard] rows per dropped item.
    public const string LogCategory = "AutoDiscard";

    // One resolved carried entry: the canonical item Number, the name to send to
    // the game, whether the user flagged it for discard, and how many copies to
    // keep (0 = discard all).
    public sealed record ResolvedDiscard(int Number, string Name, bool Discard, int KeepCount);

    // Copies of one item by who sent their hide. The engine's own answer to its
    // master switch; a by-hand surface's never did.
    private readonly record struct Copies(int ByHand, int Engine)
    {
        public int Total => ByHand + Engine;

        public static Copies Of(int count, bool byHand) => byHand ? new(count, 0) : new(0, count);

        public Copies Plus(Copies other) => new(ByHand + other.ByHand, Engine + other.Engine);

        // Split off up to `count` copies, the by-hand ones first: which copy a
        // line is about can't be told, and by-hand is the one the player is
        // watching.
        public (Copies Taken, Copies Left) Take(int count)
        {
            int hand = Math.Clamp(count, 0, ByHand);
            int engine = Math.Clamp(count - hand, 0, Engine);
            return (new(hand, engine), new(ByHand - hand, Engine - engine));
        }
    }

    // One item's hides that are waiting for somewhere they can land.
    private sealed class HeldHide
    {
        // The name to send.
        public required string Name { get; init; }
        // Refused, or never sent for want of an answer: waiting for a room.
        public Copies Waiting;
        // Sent again and not yet answered. These are in _hidesInFlight as well.
        public int Out;
        // The room that last refused it, or the one its last retry went out in.
        // Null when no room has refused it.
        public RoomKey? TriedIn;
        // Refused while the room wasn't known: the first room confirmed is that one.
        public bool RefusedBeforeRoomKnown;
        // Copies in the pack that no discard was out for when the hide was last
        // refused: the player's own. A retry never reaches into these, however
        // the copies that were to be hidden have left since.
        public int Kept;
        public int Count => Waiting.Total + Out;
    }

    // A held hide as the bug report shows it.
    public sealed record HeldHideInfo(string Name, int ByHand, int Engine, int Out, string Why)
    {
        public int Count => ByHand + Engine + Out;
    }

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
    private readonly Dictionary<int, Copies> _hidesInFlight = new();

    // item Number → discard hides not yet claimed by the transaction-log
    // forwarder, so a hide that is a discard is kept out of the stash ledger.
    private readonly Dictionary<int, int> _suppressLog = new();

    // item Number → the copies whose hide a room refused.
    private readonly Dictionary<int, HeldHide> _held = new();

    // The room the character stands in, as last confirmed. Null while unknown.
    private RoomKey? _room;

    // Whether a discard was sent or answered since the last full inventory read.
    private bool _trafficSinceRead;

    // Held hides came through a disconnect and the pack hasn't been read since: the
    // carried list is the one from before, and a hide for a copy lost while away
    // would not be refused but said aloud.
    private bool _awaitingRead;

    // The last reason every held hide was kept back, and the room it was in, so
    // the log says it once and not on every inventory change.
    private (RoomKey? Room, string Reason)? _keptBack;

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
            if (!value) EndAllHeld("hide when discarding turned off");
        }
    }

    // The verb a discard goes out with right now.
    public string DiscardVerb => HideMode ? "hide" : "drop";

    // A discard's hide was refused for want of room ("There is no room to hide X
    // here.") and is held for the next room, with the item the game named. A hide
    // this engine never sent (typed, a stash room's, Hide All's) doesn't raise it.
    public event Action<string>? HideRefused;

    // Nothing of this item is held any more: its hide landed, or the hold was
    // called off, given up or forgotten.
    public event Action<string>? HeldHideEnded;

    // True while a Roomba sweep is underway. Auto-discard is held off for the whole
    // sweep so it can't bin an item Roomba is in the middle of relocating — Roomba
    // sorts auto-discard-flagged items into their labeled rooms instead. Normal
    // auto-discard resumes the moment the sweep ends, clearing anything that
    // accumulated. Wired by AppServices to GhSweepManager.IsActive; defaults to
    // never-suppress.
    public Func<bool> SuppressDuringSweep { get; set; } = static () => false;

    // False while a hide sent now would get no answer of its own: the engine send
    // gate is up (the send is dropped unsent), or, on Stock, the character can't
    // see (its engine answers a hide in the dark or blind with the can't-see line
    // and hides nothing; Paradigm isn't known to). Held hides wait, the engine's
    // own hides are not sent, and a by-hand one is held instead of sent.
    public Func<bool> CanHideHere { get; set; } = static () => true;

    // Drops whatever the pacer still has queued. After a death or a disconnect a
    // queued discard would go out against a pack that is no longer the one it was
    // counted from.
    public Action? CancelQueuedSends { get; set; }

    // True while the pacer still has discards queued that were counted when they
    // were handed to it. They are not unanswered yet, only unsent.
    public Func<bool> SendsQueued { get; set; } = static () => false;

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
        // Whatever kept a held hide back on arriving here (a sweep, the engine's
        // switch, the send gate, the dark) may have cleared since.
        RecheckHeldHides();

        if (!_isEnabled() || _wireSender is null) return;
        // A Roomba sweep is sorting the house — don't bin an item it may be
        // relocating; Roomba sorts auto-discard-flagged items itself.
        if (SuppressDuringSweep()) return;
        // Sent now it would be lost and still counted as on its way.
        if (HideMode && !CanHideHere()) return;

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
            Offload(Send, number, item.Name, Copies.Of(toDrop, byHand: false));
        }
    }

    // Send a discard of up to `count` copies of `name` for a surface that discards
    // by hand, in the engine's own wording: hide or drop by HideMode, counted the
    // way the realm takes it. Only copies that are carried and not already spoken
    // for go out, so a second press before the first is answered sends nothing.
    // A hide that could get no answer here is held for when it can instead of
    // sent. Returns the verb, how many copies it sent, and how many it holds.
    public (string Verb, int Sent, int Held) EmitDiscard(Action<string> send, string name, int count)
    {
        ArgumentNullException.ThrowIfNull(send);
        string verb = DiscardVerb;
        // A blank name would leave a bare `hide`, which hides the character.
        if (string.IsNullOrWhiteSpace(name) || _resolve(name) is not { } item) return (verb, 0, 0);

        int held = TakeOverEngineHold(item, count);
        int free = Math.Min(count, CarriedCopies(item.Number)) - SpokenFor(item.Number);
        if (free > 0 && HideMode && !CanHideHere())
        {
            HeldHide hold = HoldFor(item);
            hold.Waiting = hold.Waiting.Plus(Copies.Of(free, byHand: true));
            hold.Kept = UnspokenCopies(item.Number);
            _log?.Info(LogCategory, $"hide of {free}x {item.Name} not sent, it could not be answered here: held");
            return (verb, 0, held + free);
        }
        if (free > 0) Offload(send, item.Number, name, Copies.Of(free, byHand: true));
        // Copies taken over may be free to go already: this room needn't be the
        // one that refused them.
        if (held > 0) RecheckHeldHides();
        return (verb, Math.Max(free, 0), held);
    }

    // The engine's held copies wait for its switch. With the switch off they
    // would wait for good and still count as spoken for, so a by-hand discard of
    // the same item takes them over: the player has now asked for them himself.
    private int TakeOverEngineHold(ResolvedDiscard item, int count)
    {
        if (_isEnabled() || !_held.TryGetValue(item.Number, out HeldHide? held)) return 0;
        int taken = Math.Min(count, held.Waiting.Engine);
        if (taken <= 0) return 0;
        held.Waiting = new Copies(held.Waiting.ByHand + taken, held.Waiting.Engine - taken);
        _log?.Info(LogCategory, $"held hide of {taken}x {item.Name} taken over by hand: auto-discard is off");
        return taken;
    }

    // Copies of `name` whose hide is waiting for a room or has not landed since.
    public int HeldFor(string name)
        => _resolve(name) is { } item && _held.TryGetValue(item.Number, out HeldHide? held) ? held.Count : 0;

    // Every held hide with who sent it and what it is waiting on, and how many
    // hides are out with no answer yet, for the bug report.
    public IReadOnlyList<HeldHideInfo> HeldHides
        => _held.Values.Select(h => new HeldHideInfo(h.Name, h.Waiting.ByHand, h.Waiting.Engine, h.Out, WhyWaiting(h))).ToList();
    public int UnansweredHides => _hidesInFlight.Values.Sum(c => c.Total);

    private string WhyWaiting(HeldHide held)
    {
        if (held.Waiting.Total == 0) return $"sent again in {RoomText(held.TriedIn)}, no answer yet";
        string refused = held.RefusedBeforeRoomKnown ? "refused before the room was known"
            : held.TriedIn is null ? "not sent yet"
            : $"last tried in {RoomText(held.TriedIn)}";
        if (KeptBackBy() is { } reason) return $"{refused}; waiting: {reason}";
        if (_room is { } here && here.Equals(held.TriedIn)) return $"{refused}; waiting for another room";
        if (held.Waiting.ByHand == 0 && !_isEnabled()) return $"{refused}; waiting: auto-discard is off";
        return $"{refused}; goes out on the next look";
    }

    private static string RoomText(RoomKey? room) => room is { } key ? key.ToString() : "an unknown room";

    private HeldHide HoldFor(ResolvedDiscard item)
    {
        if (!_held.TryGetValue(item.Number, out HeldHide? held))
            _held[item.Number] = held = new HeldHide { Name = item.Name };
        return held;
    }

    // Copies of an item in the pack that no discard is out for or waiting on.
    private int UnspokenCopies(int number) => Math.Max(0, CarriedCopies(number) - SpokenFor(number));

    // Call off held hides of an item: it was sold, dropped, hidden by hand, or
    // taken off the list it was being discarded from. The copy the hold was for
    // is gone or wanted again, and a retry would hide some other copy. A by-hand
    // surface calling off its own asks for byHandOnly, leaving the engine's.
    public void ReleaseHeld(string name, int count, bool byHandOnly = false)
    {
        if (count <= 0 || _resolve(name) is not { } item) return;
        if (!_held.TryGetValue(item.Number, out HeldHide? held)) return;
        Copies from = byHandOnly ? held.Waiting with { Engine = 0 } : held.Waiting;
        Copies released = from.Take(count).Taken;
        held.Waiting = new Copies(held.Waiting.ByHand - released.ByHand, held.Waiting.Engine - released.Engine);

        // A retry already out is called off too, or its refusal would put the copy
        // back to waiting. It comes off the count here; its ledger registration
        // stays, so a landing is still a discard and a refusal finds nothing to hold.
        Copies flight = _hidesInFlight.GetValueOrDefault(item.Number);
        int recalled = Math.Min(Math.Min(count - released.Total, held.Out), byHandOnly ? flight.ByHand : flight.Total);
        if (recalled > 0)
        {
            held.Out -= recalled;
            TakeFlight(item.Number, recalled);
        }

        int calledOff = released.Total + recalled;
        if (calledOff == 0) return;
        if (held.Count == 0)
            EndHeld(item.Number, $"held hide of {held.Name} called off ({calledOff})");
        else
            _log?.Info(LogCategory, $"held hide of {held.Name} called off ({calledOff}), {held.Count} still held");
    }

    // Copies of an item a discard is already out for, or waiting on a room for.
    private int SpokenFor(int number)
        => _inFlight.GetValueOrDefault(number) + _hidesInFlight.GetValueOrDefault(number).Total
           + (_held.TryGetValue(number, out HeldHide? held) ? held.Waiting.Total : 0);

    // Copies of an item in the pack: an entry is a stack with its count in front
    // ("3 moonstone"). AppServices.CountInPack does the same sum off the snapshot;
    // this one goes through the resolver the class is handed, as its tests do.
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
    private void Offload(Action<string> send, int number, string name, Copies copies)
    {
        if (copies.Total <= 0) return;
        _trafficSinceRead = true;
        if (HideMode)
        {
            _hidesInFlight[number] = _hidesInFlight.GetValueOrDefault(number).Plus(copies);
            _suppressLog[number] = _suppressLog.GetValueOrDefault(number) + copies.Total;
        }
        else if (copies.Engine > 0)
        {
            _inFlight[number] = _inFlight.GetValueOrDefault(number) + copies.Engine;
        }
        CountedCommand.Emit(send, DiscardVerb, copies.Total, name, _isParadigm());
    }

    // Clear a pending drop when our own "You dropped X." confirmation arrives.
    // PlayerDrops is a combined pattern (Groups[0] = the other-player name for
    // "<name> drops X.", empty for the self "You dropped X." branch); only the
    // self branch confirms a command we sent. A drop of an item whose hide is held
    // calls the hold off.
    private void OnDropLine(MatchResult m)
    {
        if (m.Groups.Count < 2) return;
        if (!string.IsNullOrEmpty(m.Groups[0])) return;   // another player's drop
        (int count, string name) = CountedCommand.SplitLeadingCount(m.Groups[1]);
        if (_resolve(name) is not { } item) return;
        if (Reduce(_inFlight, item.Number, count)) _trafficSinceRead = true;
        ReleaseHeld(item.Name, count);
    }

    // Clear a pending hide when our own "You hid X." confirmation arrives.
    // UserHides is a self-only single-group pattern (Groups[0] = the hidden item),
    // so unlike PlayerDrops there's no other-player branch to filter out. Coin
    // hides ("You hid 10 gold.") don't resolve to an item and fall through.
    private void OnHideLine(MatchResult m)
    {
        if (m.Groups.Count < 1) return;
        (int count, string name) = CountedCommand.SplitLeadingCount(m.Groups[0]);
        if (_resolve(name) is not { } item) return;
        int ours = TakeFlight(item.Number, count).Total;
        if (ours > 0) _trafficSinceRead = true;
        if (!_held.TryGetValue(item.Number, out HeldHide? held)) return;

        held.Out -= Math.Min(held.Out, ours);
        // A hide nobody here sent (typed, Hide All) put a held copy away itself.
        if (count > ours) ReleaseHeld(item.Name, count - ours);
        if (_held.ContainsKey(item.Number) && held.Count == 0)
            EndHeld(item.Number, $"held hide of {held.Name} landed in {_room}");
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
        Copies refused = TakeFlight(item.Number, count);
        if (refused.Total == 0) return;
        if (!HideMode)
        {
            _log?.Info(LogCategory, $"hide of {item.Name} refused, no room here: not held, hide when discarding is off");
            return;
        }
        HeldHide held = HoldFor(item);
        held.Out -= Math.Min(held.Out, refused.Total);
        held.Waiting = held.Waiting.Plus(refused);
        held.TriedIn = _room;
        held.RefusedBeforeRoomKnown = _room is null;
        held.Kept = UnspokenCopies(item.Number);
        _log?.Info(LogCategory, $"hide of {item.Name} refused, no room here: {held.Waiting.Total} held for the next room");
        HideRefused?.Invoke(item.Name);
    }

    // The character stands in a room, by the room tracker's confirmed reading. A
    // redisplay of the same room is not an arrival.
    public void OnRoomEntered(RoomKey room)
    {
        if (_room is { } here && here.Equals(room)) return;
        _room = room;
        // A hide refused before any room was confirmed was refused by this one.
        foreach (HeldHide held in _held.Values)
        {
            if (!held.RefusedBeforeRoomKnown) continue;
            held.RefusedBeforeRoomKnown = false;
            held.TriedIn = room;
        }
        RecheckHeldHides();
    }

    // What keeps every held hide back right now, or null when they may go.
    private string? KeptBackBy()
    {
        if (_awaitingRead) return "the pack hasn't been read since the reconnect";
        if (_room is null) return "the room isn't known";
        if (_wireSender is null) return "not connected";
        if (SuppressDuringSweep()) return "a Roomba sweep is running";
        if (!CanHideHere()) return "a hide can't be answered here (the send gate is up, or the character can't see)";
        return null;
    }

    // Send each held hide once in the room the character stands in, unless that
    // room is the one that refused it. A refusal here holds it again for the room
    // after. Safe to call whenever something that kept the hides back may have
    // cleared.
    public void RecheckHeldHides()
    {
        if (_held.Count == 0) return;
        if (KeptBackBy() is { } reason)
        {
            if (_keptBack == (_room, reason)) return;
            _keptBack = (_room, reason);
            _log?.Info(LogCategory, $"held hides kept back in {RoomText(_room)}: {reason}");
            return;
        }
        if (_room is not { } here) return;
        _keptBack = null;

        List<string> commands = new();
        foreach ((int number, HeldHide held) in _held.ToList())
        {
            // The room is kept per item, not per sender: engine copies left waiting
            // for the switch when the by-hand ones went out here skip this room too.
            if (held.Waiting.Total == 0 || here.Equals(held.TriedIn)) continue;

            ResolvedDiscard? item = _resolve(held.Name);
            // The engine's copies are its own only while the item is still flagged.
            if (held.Waiting.Engine > 0 && item is not { Discard: true })
            {
                _log?.Info(LogCategory, $"held hide of {held.Name}: no longer flagged for auto-discard, {held.Waiting.Engine} let go");
                held.Waiting = held.Waiting with { Engine = 0 };
            }
            // Not for a copy that has left the pack some other way, and never into
            // the copies that weren't to be discarded when the hide was refused.
            int carried = CarriedCopies(number) - _inFlight.GetValueOrDefault(number)
                          - _hidesInFlight.GetValueOrDefault(number).Total;
            int free = carried - held.Kept;
            if (held.Waiting.Total > free)
            {
                _log?.Info(LogCategory, $"held hide of {held.Name}: {held.Waiting.Total - Math.Max(free, 0)} no longer in the pack to hide, forgotten");
                held.Waiting = held.Waiting.Take(free).Taken;
            }
            // The engine also leaves the item's keep amount, as it is set now.
            if (held.Waiting.Engine > 0 && item is { } flagged)
            {
                int engineFree = Math.Max(0, carried - flagged.KeepCount - held.Waiting.ByHand);
                if (held.Waiting.Engine > engineFree)
                {
                    _log?.Info(LogCategory, $"held hide of {held.Name}: {held.Waiting.Engine - engineFree} let go, within the keep amount of {flagged.KeepCount}");
                    held.Waiting = held.Waiting with { Engine = engineFree };
                }
            }
            if (held.Count == 0) { EndHeld(number, $"held hide of {held.Name} given up"); continue; }

            // The engine's copies wait for its switch; a by-hand discard never did.
            Copies due = _isEnabled() ? held.Waiting : held.Waiting with { Engine = 0 };
            if (due.Total == 0) continue;

            held.Waiting = new Copies(held.Waiting.ByHand - due.ByHand, held.Waiting.Engine - due.Engine);
            held.Out += due.Total;
            held.TriedIn = here;
            _log?.Info(LogCategory, $"retrying held hide of {due.Total}x {held.Name} in {here}");
            Offload(commands.Add, number, held.Name, due);
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
    // later discard of that item and swallow a later stash's ledger row. A retry
    // lost that way goes back to waiting for a room.
    public void OnFullInventoryRead()
    {
        bool wasAwaitingRead = _awaitingRead;
        _awaitingRead = false;
        foreach (int number in _hidesInFlight.Keys.Concat(_suppressLog.Keys).Concat(_held.Keys).Distinct().ToList())
        {
            int carried = CarriedCopies(number);
            CapFlight(number, carried);
            Cap(_suppressLog, number, carried);
            if (!_held.TryGetValue(number, out HeldHide? held)) continue;
            held.Out = Math.Min(held.Out, _hidesInFlight.GetValueOrDefault(number).Total);
            held.Waiting = held.Waiting.Take(carried - held.Kept - held.Out).Taken;
            if (held.Count == 0) EndHeld(number, $"held hide of {held.Name} forgotten: no longer carried");
        }

        // Discards still queued in the pacer were counted when handed over. They
        // are unsent, not unanswered, and forgetting them would let a second press
        // send for copies the queue is about to take.
        bool queued = SendsQueued();
        if (!_trafficSinceRead && !queued && (_hidesInFlight.Count > 0 || _suppressLog.Count > 0))
        {
            foreach ((int number, HeldHide held) in _held.ToList())
            {
                if (held.Out == 0) continue;
                Copies back = TakeFlight(number, held.Out).Plus(held.Waiting)
                    .Take(CarriedCopies(number) - held.Kept).Taken;
                held.Out = 0;
                held.Waiting = back;
                if (held.Count == 0) EndHeld(number, $"held hide of {held.Name} forgotten: no longer carried");
                else _log?.Info(LogCategory, $"retry of {held.Name} never answered: {back.Total} held again for the next room");
            }
            if (_hidesInFlight.Count > 0)
                _log?.Info(LogCategory, $"{UnansweredHides} hide(s) never answered: no longer counted as on their way");
            _hidesInFlight.Clear();
            _suppressLog.Clear();
        }
        _trafficSinceRead = queued;
        // This read is what the held hides were waiting on.
        if (wasAwaitingRead) RecheckHeldHides();
    }

    // Nothing sent before this point will be answered (a death empties the pack,
    // a dropped connection loses the replies, another character has its own pack).
    // A dropped connection leaves the pack as it was, so with keepHeld the refused
    // copies go on waiting, each still tied to the room that refused it: the
    // character comes back into that same room. They wait for the pack to be read
    // again first, since what happened to it while away isn't known.
    public void Reset(string reason, bool keepHeld = false)
    {
        CancelQueuedSends?.Invoke();
        _keptBack = null;
        _awaitingRead = keepHeld && _held.Count > 0;
        int unanswered = _inFlight.Values.Sum() + UnansweredHides;
        if (unanswered > 0) _log?.Info(LogCategory, $"{reason}: {unanswered} unanswered discard(s) forgotten");
        if (keepHeld)
        {
            foreach ((int number, HeldHide held) in _held)
            {
                held.Waiting = held.Waiting.Plus(TakeFlight(number, held.Out));
                held.Out = 0;
            }
        }
        else
        {
            EndAllHeld(reason);
        }
        _inFlight.Clear();
        _hidesInFlight.Clear();
        _suppressLog.Clear();
        _room = null;
        _trafficSinceRead = false;
    }

    // One item's hold is over: say why, and tell whoever is showing it.
    private void EndHeld(int number, string message)
    {
        if (!_held.Remove(number, out HeldHide? held)) return;
        _log?.Info(LogCategory, message);
        HeldHideEnded?.Invoke(held.Name);
    }

    private void EndAllHeld(string reason)
    {
        foreach ((int number, HeldHide held) in _held.ToList())
            EndHeld(number, $"{reason}: held hide of {held.Count} {held.Name} given up");
    }

    // Take up to `count` hides of an item off the in-flight count; what came off.
    private Copies TakeFlight(int number, int count)
    {
        if (!_hidesInFlight.TryGetValue(number, out Copies flight)) return default;
        (Copies taken, Copies left) = flight.Take(count);
        if (left.Total == 0) _hidesInFlight.Remove(number);
        else _hidesInFlight[number] = left;
        return taken;
    }

    private void CapFlight(int number, int max)
    {
        if (_hidesInFlight.TryGetValue(number, out Copies flight) && flight.Total > max)
            TakeFlight(number, flight.Total - max);
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

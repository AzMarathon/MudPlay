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
// each flagged container by resolved item Number and diffs them against the
// previous snapshot; each new copy is owed one open. The baseline is seeded
// silently on the first change seen once inventory is loaded (a full 'i'), so
// containers already carried at connect aren't opened — only genuine new
// acquisitions are.
//
// One open at a time, since the tracker reads the pack after each. An owed open
// waits, and is not forgotten, while
//   - a fight is on: `open` ends it (GAME_MECHANICS "Chests and chest loot tables");
//   - a sneak is being kept: `open` ends that too;
//   - the client can't send (the engine send gate is up, or it isn't in the game):
//     the command would be dropped unsent;
//   - the tracker is still reading another open.
// It is forgotten, not held, when item automation is switched off (Auto Get Items,
// or the Auto-All switch): nothing opens later that arrived while it was off. The
// same goes for a Roomba sweep, whose carries are items being moved and not loot.
// A copy that leaves the pack before its turn takes its open with it.
//
// A copy that comes back is not an arrival: what the character held when it died
// and then recovers, and what a hang-up penalty dropped and the client picks up
// again. Those were in the pack unopened because the player kept them that way.
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

    // One resolved carried entry: the item Number, the name to send to the
    // game, and whether it's a flagged container the engine should auto-open.
    public sealed record ResolvedOpen(int Number, string Name, bool AutoOpen);

    private readonly Func<IReadOnlyList<string>> _carried;
    private readonly Func<string, ResolvedOpen?> _resolve;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _isLoaded;
    private readonly Func<string, bool> _open;
    private readonly LogService? _log;

    // item Number → count of that flagged container seen in the previous
    // carried snapshot. Rebuilt on every change once seeded.
    private readonly Dictionary<int, int> _prevCounts = new();
    private bool _seeded;

    // One entry per open still owed, oldest first.
    private readonly List<ResolvedOpen> _owed = new();
    // The container handed to the tracker and not yet read.
    private ResolvedOpen? _opening;
    // Its copy has been seen to leave the pack: the next one to go is another.
    private bool _openingSeenGone;
    // item Number → copies the character held when it died and hasn't got back.
    private readonly Dictionary<int, int> _lostAtDeath = new();
    // The hold last logged, so one hold is one log line.
    private string? _heldFor;

    // open: hands one container to ChestOpenTracker (TryOpenNow). False when the
    // tracker is busy; the open stays owed.
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

    // SneakGuard.Holds.
    public Func<bool> SneakKept { get; set; } = static () => false;

    // Copies of the named item the client is picking back up after a hang-up
    // penalty dropped them (HangupItemRecheck).
    public Func<string, int> ComingBack { get; set; } = static _ => 0;

    // ----- state for the bug report ---------------------------------------

    public IReadOnlyList<string> Owed => _owed.Select(o => o.Name).ToList();

    public string? Opening => _opening?.Name;

    // Why an owed open isn't going out, or null when nothing is owed or held.
    public string? HeldFor => _owed.Count == 0 ? null : WhyOff() ?? WhyHeld() ?? _heldFor;

    // The last container opened here, when, and what it gave.
    public string? LastOpen { get; private set; }

    // Re-evaluate the pack on any inventory change: each flagged container that
    // newly entered it is owed an open.
    public void OnInventoryChanged()
    {
        // Wait for the first full 'i' so the baseline reflects the real pack —
        // a coin pickup can fire Changed before any inventory dump.
        if (!_isLoaded()) return;

        // Group current carried copies by resolved item Number, keeping only
        // flagged containers. A pile is one entry standing for all its copies, so
        // a container that joins one already carried still reads as an arrival.
        Dictionary<int, (ResolvedOpen Item, int Count)> current = new();
        foreach ((string name, int copies) in InventorySnapshot.Stacks(_carried()))
        {
            if (_resolve(name) is not { AutoOpen: true } item) continue;
            if (current.TryGetValue(item.Number, out (ResolvedOpen Item, int Count) g))
                current[item.Number] = (g.Item, g.Count + copies);
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
            else if (moved > 0 && off is null) Arrived(now.Item, moved);
        }

        RebaseTo(current);
        Recheck();
    }

    // Something that kept an owed open back may have cleared: send the next one
    // if nothing holds it now.
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
        if (why is null)
        {
            ResolvedOpen next = _owed[0];
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

        // Waiting on our own open is the ordinary one-at-a-time step, not a hold.
        if (_opening is null && why != _heldFor)
            _log?.Info(LogCategory, $"holding {_owed.Count} open(s) — {why}");
        _heldFor = why;
    }

    // ChestOpenTracker.OpenSettled: an open has been read, ours or not.
    public void OnOpenSettled(ChestOpenTracker.OpenResult result)
    {
        if (_opening is { } done)
        {
            _opening = null;
            bool nothing = result.Items.Count == 0 && result.Coin.TotalCopperValue == 0;
            string gave = nothing
                ? "nothing"
                : string.Join(", ", result.Items.Select(i => $"{i.Count} {i.Name}")
                    .Append($"{result.Coin.TotalCopperValue}c"));
            LastOpen = $"{done.Name} at {DateTimeOffset.Now:HH:mm:ss} — gave {gave}";
            _log?.Info(LogCategory, nothing
                ? $"opened item={done.Name} — it gave nothing; it is not tried again"
                : $"opened item={done.Name} — gave {gave}");
        }
        Recheck();
    }

    // The character died: what it held is on the floor or in its corpse, and
    // getting it back is not acquiring it.
    public void OnPlayerDied()
    {
        foreach ((int number, int count) in _prevCounts)
            if (count > 0) _lostAtDeath[number] = _lostAtDeath.GetValueOrDefault(number) + count;
        Forget("the character died");
    }

    // A different character: nothing known about the last one's pack holds.
    public void Reset()
    {
        _prevCounts.Clear();
        _seeded = false;
        _owed.Clear();
        _opening = null;
        _lostAtDeath.Clear();
        _heldFor = null;
        LastOpen = null;
    }

    private void Arrived(ResolvedOpen item, int copies)
    {
        int fromDeath = Math.Min(copies, _lostAtDeath.GetValueOrDefault(item.Number));
        if (fromDeath > 0) _lostAtDeath[item.Number] -= fromDeath;
        int pickedBack = Math.Min(copies - fromDeath, Math.Max(0, ComingBack(item.Name)));
        int back = fromDeath + pickedBack;
        if (back > 0)
            _log?.Info(LogCategory,
                $"{back}x {item.Name} back in the pack (lost at a death or to a hang-up) — not opened");

        int fresh = copies - back;
        if (fresh <= 0) return;
        for (int i = 0; i < fresh; i++) _owed.Add(item);
        _log?.Info(LogCategory, $"{fresh}x {item.Name} arrived — {_owed.Count} open(s) owed");
    }

    private void Left(int number, int copies)
    {
        // The first copy to go after our own open is the one it opened.
        if (_opening is { } opening && opening.Number == number && !_openingSeenGone)
        {
            _openingSeenGone = true;
            copies--;
        }

        int dropped = 0;
        string name = "";
        for (int i = _owed.Count - 1; i >= 0 && dropped < copies; i--)
        {
            if (_owed[i].Number != number) continue;
            name = _owed[i].Name;
            _owed.RemoveAt(i);
            dropped++;
        }
        if (dropped > 0)
            _log?.Info(LogCategory, $"{dropped}x {name} left the pack before being opened — no longer owed");
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
        if (InCombat()) return "in a fight";
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

    // Replace the baseline with the current flagged-container counts.
    private void RebaseTo(Dictionary<int, (ResolvedOpen Item, int Count)> current)
    {
        _prevCounts.Clear();
        foreach ((int number, (ResolvedOpen _, int count)) in current)
            _prevCounts[number] = count;
    }
}

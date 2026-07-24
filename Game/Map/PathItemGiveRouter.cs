using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FujinTerm.Services;

namespace FujinTerm.Game.Map;

// One resolved way to be handed an item on demand: a room to stand in, the
// verbatim command to type there, and the giver's display name for the log.
// AppServices builds these off ItemSourceIndex — a Monster giver becomes
// `ask <noun> <keyword>` at each of its spawn rooms (the noun is the last word of
// the name, since the game's `ask` parser takes a single-word target), a Room
// giver becomes the bare room-CMD keyword typed in that room (e.g. "insert fang").
public readonly record struct GiveSource(RoomKey Room, string Command, string GiverName);

// Active fulfiller for NeedKind.PathItem needs an NPC / room hands over for
// free: when a one-shot walk crosses an (Item: N) / (Ticket: N) gate whose item
// we're not carrying and a deterministic textblock `giveitem` supplies it (an
// `ask <npc> <keyword>` dialogue give, or a room-CMD keyword give), detour to
// the giver that adds the fewest steps, issue the command, and resume once the
// item lands. Backs the item record's "Auto-obtain for path" flag
// (ItemOverlay.AutoObtainForPath).
//
// Preempts the shop and drop routers. A deterministic give is free and needs
// no RNG — strictly better than buying or hunting — so both of those stand down
// whenever one exists (deterministicGiveExists). Only the give's keyword-carrying,
// ungated awards reach here: AppServices filters ItemSourceIndex to
// Deterministic givers with a non-empty keyword and resolves each to a concrete
// room, so a candidate list is always something we can actually walk to and ask
// for. A gated give (turn-in / purchase / quest-reward) or a `random` roll is
// excluded upstream — those aren't a reliable one-command hand-over.
//
// Auto-detour, don't prompt. Like the shop buy and unlike the drop hunt, a
// deterministic give is cheap, certain, and a single command, so it runs
// silently rather than asking.
//
// Giver selection. Among the candidate rooms, pick the one minimising
// dist(cur, giver) + dist(giver, dest) — the fewest steps added to the trip,
// the same metric the shop router uses. Distances use the walker's own
// IRoomFilter so the estimate matches the walk that runs. Ties break on the
// nearer giver, then room-key order, for determinism.
//
// The command issues once. A give either fires or it doesn't; re-asking a
// deterministic giver isn't a known way to draw a second copy, so we don't
// invent one — a shortfall beyond the first hand-over falls through the buy
// window to search rather than spamming the NPC.
//
// Scope. Detours apply only to a plain walk-to. When a loop or auto-lair run is
// driving movement (engineWalkActive) the need is left to demand-driven search.
//
// Resolution paths. The item entering inventory (OnInventoryChanged) resumes the
// original walk from any active phase — the give landing, or a search / party
// hand-off revealing it en route (found-first abort). A walk that fails, a give
// that doesn't land within the window, and a user redirect all resume / abandon
// gracefully and leave the need outstanding for the other fulfillers. The need's
// own lifecycle (post / resolve) stays owned by PathItemDemandTracker; this
// router only reacts.
//
// Inventory / graph / walker are reached through delegates so the FSM stays
// unit-testable without a live line stream, room graph, and dispatcher. Each
// delegate has exactly one production binding in AppServices.
public sealed class PathItemGiveRouter : IDisposable
{
    private const string LogCategory = "AutoSearch";

    private enum Phase
    {
        Idle,
        WalkingToGiver,
        Acquiring,
    }

    private readonly Func<int, IReadOnlyList<GiveSource>> _giveSourcesForItem;
    private readonly Func<RoomKey?> _currentRoom;
    private readonly Func<RoomKey?> _walkDestination;
    private readonly Func<RoomKey, RoomKey, int?> _distanceBetween;
    private readonly Func<int, int> _carriedCount;
    private readonly Func<int, string?> _itemName;
    private readonly Func<int, bool> _isEnabled;
    private readonly Func<bool> _engineWalkActive;
    private readonly Action<RoomKey> _walkTo;
    private readonly Action<Action> _post;
    private readonly LogService? _log;
    private readonly TimeSpan _giveTimeout;
    private readonly WireSender _wire = new();
    private readonly Timer _giveTimer;

    private Phase _phase = Phase.Idle;
    private int _itemId;
    private int _targetCount = 1;
    private RoomKey _origDest;
    private GiveSource _giver;

    public PathItemGiveRouter(
        Func<int, IReadOnlyList<GiveSource>> giveSourcesForItem,
        Func<RoomKey?> currentRoom,
        Func<RoomKey?> walkDestination,
        Func<RoomKey, RoomKey, int?> distanceBetween,
        Func<int, int> carriedCount,
        Func<int, string?> itemName,
        Func<int, bool> isEnabled,
        Func<bool> engineWalkActive,
        Action<RoomKey> walkTo,
        Action<Action> post,
        LogService? log = null,
        TimeSpan? giveTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(giveSourcesForItem);
        ArgumentNullException.ThrowIfNull(currentRoom);
        ArgumentNullException.ThrowIfNull(walkDestination);
        ArgumentNullException.ThrowIfNull(distanceBetween);
        ArgumentNullException.ThrowIfNull(carriedCount);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(engineWalkActive);
        ArgumentNullException.ThrowIfNull(walkTo);
        ArgumentNullException.ThrowIfNull(post);
        _giveSourcesForItem = giveSourcesForItem;
        _currentRoom = currentRoom;
        _walkDestination = walkDestination;
        _distanceBetween = distanceBetween;
        _carriedCount = carriedCount;
        _itemName = itemName;
        _isEnabled = isEnabled;
        _engineWalkActive = engineWalkActive;
        _walkTo = walkTo;
        _post = post;
        _log = log;
        _giveTimeout = giveTimeout ?? TimeSpan.FromSeconds(8);
        _giveTimer = new Timer(_ => _post(OnGiveTimeout), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // Bind the wire sink used to issue the give command.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Every buffer this router pushed to the wire, in order (test seam).
    internal IReadOnlyList<byte[]> LastSentForTests => _wire.LastSentForTests;

    // True while a give detour is walking to the giver or waiting for the item.
    public bool DetourActive => _phase != Phase.Idle;

    // New-need callback (wired to NeedsRegistry.NeedPosted). Arms a detour toward
    // the fewest-added-steps giver when the item is flagged, no engine walk is
    // driving, a give exists, and we can route both to the giver and on to the
    // destination. A no-op otherwise.
    public void OnNeedPosted(Need need)
    {
        if (need.Kind != NeedKind.PathItem) return;
        if (_phase != Phase.Idle) return;
        if (_engineWalkActive()) return;

        if (!int.TryParse(need.Descriptor, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int itemId)
            || itemId <= 0)
            return;
        if (!_isEnabled(itemId)) return;   // per-item auto-obtain gate
        int target = Math.Max(1, need.Quantity);
        if (_carriedCount(itemId) >= target) return;   // already hold the shortfall

        string? name = _itemName(itemId);
        if (string.IsNullOrWhiteSpace(name)) return;

        if (_currentRoom() is not { } cur) return;
        if (_walkDestination() is not { } dest) return;

        if (!TrySelectGiver(cur, dest, itemId, out GiveSource giver)) return;

        _itemId = itemId;
        _targetCount = target;
        _origDest = dest;
        _giver = giver;
        _phase = Phase.WalkingToGiver;
        _log?.Info(LogCategory,
            $"path item {itemId} ('{name}') given by {giver.GiverName} — detouring to {giver.Room} to '{giver.Command}'");
        _post(() => _walkTo(giver.Room));
    }

    // Walker-event callback (wired to AutoWalkManager.Event). Arrival at the giver
    // issues the command; a failed walk resumes to the destination; a user-driven
    // walk abandons the detour.
    public void OnWalkEvent(WalkEvent e)
    {
        switch (_phase)
        {
            case Phase.WalkingToGiver:
                if (e.Kind == WalkEventKind.Finished && KeyMatches(e.Destination, _giver.Room))
                    BeginAcquiring();
                else if (e.Kind == WalkEventKind.Failed)
                {
                    _log?.Info(LogCategory,
                        $"giver at {_giver.Room} unreachable ({e.Detail}) — resuming to {_origDest}");
                    ResumeToPath();
                }
                else if (e.Kind == WalkEventKind.Stopped)
                    Reset(); // user / another engine took over — abandon quietly
                break;

            case Phase.Acquiring:
                // Idle at the giver while the give settles. A fresh walk means the
                // user redirected — drop the detour and let them drive.
                if (e.Kind is WalkEventKind.Started or WalkEventKind.Stopped)
                    Reset();
                break;
        }
    }

    // Inventory-change callback (wired to InventoryManager.Changed). When the item
    // we detoured for is now carried — the give landed, or search / a party
    // hand-off revealed it en route — resume the original walk. The found case
    // doubles as the found-first abort for an in-flight walk to the giver.
    public void OnInventoryChanged()
    {
        if (_phase is not (Phase.WalkingToGiver or Phase.Acquiring)) return;
        if (_carriedCount(_itemId) < _targetCount) return;   // still short
        _log?.Info(LogCategory, $"path item {_itemId} acquired — resuming to {_origDest}");
        ResumeToPath();
    }

    // Give-window elapsed. If the item still isn't carried the give didn't land
    // (dialogue mismatch, a hidden prerequisite, a moved NPC) — resume to the
    // destination and leave the need outstanding for the other fulfillers.
    // Invoked on the UI thread via the injected post delegate; tests call it directly.
    public void OnGiveTimeout()
    {
        if (_phase != Phase.Acquiring) return;
        if (_carriedCount(_itemId) >= _targetCount) return; // race: OnInventoryChanged handled it
        _log?.Info(LogCategory,
            $"give of path item {_itemId} by {_giver.GiverName} did not land in time — resuming to {_origDest}");
        ResumeToPath();
    }

    public void Dispose() => _giveTimer.Dispose();

    private void BeginAcquiring()
    {
        _phase = Phase.Acquiring;
        _log?.Info(LogCategory, $"at giver {_giver.Room} — '{_giver.Command}' for path item {_itemId}");
        _wire.Send(_giver.Command);
        ArmGiveTimer();
    }

    private void ResumeToPath()
    {
        DisarmGiveTimer();
        RoomKey dest = _origDest;
        _phase = Phase.Idle;
        _post(() => _walkTo(dest));
    }

    private void Reset()
    {
        DisarmGiveTimer();
        _phase = Phase.Idle;
    }

    private void ArmGiveTimer()
        => _giveTimer.Change(_giveTimeout, Timeout.InfiniteTimeSpan);

    private void DisarmGiveTimer()
        => _giveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private bool TrySelectGiver(RoomKey cur, RoomKey dest, int itemId, out GiveSource best)
        => TrySelectGiver(_giveSourcesForItem(itemId), cur, dest, _distanceBetween, out best);

    // Pick the give source minimising dist(cur,giver)+dist(giver,dest) among the
    // candidates reachable both ways. Ties break on the nearer giver, then
    // room-key order — deterministic for testability. Static + internal so the
    // route picker can name the very giver this router would detour to without
    // duplicating the selection rule.
    internal static bool TrySelectGiver(
        IReadOnlyList<GiveSource> givers, RoomKey cur, RoomKey dest,
        Func<RoomKey, RoomKey, int?> distanceBetween, out GiveSource best)
    {
        ArgumentNullException.ThrowIfNull(givers);
        ArgumentNullException.ThrowIfNull(distanceBetween);
        best = default;
        int bestTotal = int.MaxValue;
        int bestToGiver = int.MaxValue;
        foreach (GiveSource g in givers)
        {
            if (distanceBetween(cur, g.Room) is not { } toGiver) continue;
            if (distanceBetween(g.Room, dest) is not { } toDest) continue;
            int total = toGiver + toDest;
            bool better = total < bestTotal
                || (total == bestTotal && toGiver < bestToGiver)
                || (total == bestTotal && toGiver == bestToGiver && CompareKeys(g.Room, best.Room) < 0);
            if (better)
            {
                bestTotal = total;
                bestToGiver = toGiver;
                best = g;
            }
        }
        return bestTotal != int.MaxValue;
    }

    private static bool KeyMatches(RoomKey? actual, RoomKey expected)
        => actual.HasValue && actual.Value.Equals(expected);

    private static int CompareKeys(RoomKey a, RoomKey b)
    {
        int byMap = a.Map.CompareTo(b.Map);
        return byMap != 0 ? byMap : a.Room.CompareTo(b.Room);
    }
}

using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using MudPlay.Game.Combat;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// One resolved way to conjure an item's dropper on demand: the room to stand in,
// the room-CMD keyword that summons the monster, and the monster's name for the
// log. AppServices builds these off ItemSourceIndex.SummonDropsOf, which only
// admits a guaranteed (100%) drop from a monster a room command spawns.
public readonly record struct SummonSource(
    RoomKey Room, string Command, string MonsterName);

// Active fulfiller for NeedKind.PathItem needs satisfied by summoning a monster
// that drops the item outright: detour to the summoning room, type the room
// command, let the fight resolve, then re-survey the floor so the drop is
// collected, and resume the original walk once it lands.
//
// Why this is its own router rather than a MonsterDropRouter case. A lair drop is
// a gamble twice over — the monster has to spawn and then has to roll the item —
// so that router prompts before committing to a hunt. A room-command summon is
// deterministic on both counts: the command conjures the monster whenever asked,
// and the drop is a 100% slot. That's the difference between the town gate key
// (`touch statue` in 8/461 always summons the obsidian statue, which always drops
// it) and the black star key (a 1-10% roll off lair-spawned cultists, which can
// never be relied on mid-route). Only the first kind belongs in an automatic
// detour, so only the first kind reaches here (report paradigm-20260911-010954).
//
// Precedence. Between the free routers and the gamble: a deterministic give
// (PathItemGiveRouter) and a shop buy (PathItemShopRouter) both cost no combat and
// win outright, so this stands down when either exists; MonsterDropRouter in turn
// stands down when a summon source exists, since a guaranteed kill beats a
// percentage one. All four react to the same NeedsRegistry.NeedPosted event.
//
// Auto-detour, don't prompt. The per-item AutoObtainForPath opt-in (or the route
// picker's per-walk override, granted when the user accepts a gated route) is the
// consent — once given, the detour runs without a second question, like the give
// and shop routers.
//
// Collecting the drop. A kill's drop lands loose on the room floor and is NOT
// announced on the death line, so the floor has to be re-surveyed before anything
// can see it (confirmed mechanic, GAME_MECHANICS.md "Items & acquisition"). This
// router therefore issues a bare `look` on each death while it's waiting and
// leaves the `get` to PathItemFloorCollector, which already grabs any outstanding
// path item a survey reveals — so the pickup path is the shared one rather than a
// second implementation of it.
//
// Deaths are not attributed, so a death never ends the wait. MonsterDeathWatcher
// recognises a kill from the generic "exp gain + *Combat Off*" pair and reports no
// identity at all (per-monster death lines were retired as unmatchable), so this
// router cannot tell our summon's death from a wanderer's. Rather than guess, a
// death only ever triggers a re-survey: if the key is on the floor the collector
// takes it and the walk resumes, and if the death was someone else's nothing lands
// and we keep waiting. That makes an unrelated kill cost one wasted `look` instead
// of abandoning the detour with the statue still alive. The re-surveys are capped
// so a room being ground by a party can't turn into `look` spam.
//
// The command issues once per detour. A summon either lands or it doesn't;
// re-typing the keyword isn't a known way to force a second monster (the gate
// statue is GameLimit 1), so a miss falls through to the other fulfillers instead
// of spamming the room.
//
// One deadline covers the whole detour. The timer is armed once when the detour
// arms and disarmed when it ends, so every phase is bounded by the same budget —
// including the walk, which can otherwise strand: each router redirects with
// supersedeSilently, so a sibling detour stealing the walk fires no Stopped here
// and only the deadline (or a foreign Finished) releases us. A single armed window
// also means no stale callback from a previous phase can fire against a later one.
//
// Inventory / graph / walker / wire are reached through delegates so the FSM stays
// unit-testable without a live line stream, room graph, and dispatcher. Each
// delegate has exactly one production binding in AppServices.
public sealed class PathItemSummonRouter : IDisposable
{
    private const string LogCategory = "AutoSearch";

    // Re-surveys allowed per detour. Generous enough for our own kill to follow a
    // few unrelated ones, small enough that a busy room can't flood the wire.
    private const int MaxReSurveys = 6;

    private enum Phase
    {
        Idle,
        WalkingToSummon,
        AwaitingKill,   // command typed; the monster is up and the fight is running.
    }

    private readonly Func<int, IReadOnlyList<SummonSource>> _summonSourcesForItem;
    private readonly Func<int, bool> _cheaperSourceExists;
    private readonly Func<bool> _siblingDetourActive;
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
    private readonly TimeSpan _detourTimeout;
    private readonly WireSender _wire = new();
    private readonly Timer _timer;

    private Phase _phase = Phase.Idle;
    private int _itemId;
    private int _targetCount = 1;
    private int _reSurveys;
    private RoomKey _origDest;
    private SummonSource _source;

    public PathItemSummonRouter(
        Func<int, IReadOnlyList<SummonSource>> summonSourcesForItem,
        Func<int, bool> cheaperSourceExists,
        Func<bool> siblingDetourActive,
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
        TimeSpan? detourTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(summonSourcesForItem);
        ArgumentNullException.ThrowIfNull(cheaperSourceExists);
        ArgumentNullException.ThrowIfNull(siblingDetourActive);
        ArgumentNullException.ThrowIfNull(currentRoom);
        ArgumentNullException.ThrowIfNull(walkDestination);
        ArgumentNullException.ThrowIfNull(distanceBetween);
        ArgumentNullException.ThrowIfNull(carriedCount);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(engineWalkActive);
        ArgumentNullException.ThrowIfNull(walkTo);
        ArgumentNullException.ThrowIfNull(post);
        _summonSourcesForItem = summonSourcesForItem;
        _cheaperSourceExists = cheaperSourceExists;
        _siblingDetourActive = siblingDetourActive;
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
        // One budget for the walk, the fight, and the pickup. A summoned monster is
        // a real fight, not a dialogue beat — the gate statue is 300 HP — so this is
        // minutes where the give router's give window is seconds. Expiring it only
        // abandons the detour; it never interferes with a fight in progress.
        _detourTimeout = detourTimeout ?? TimeSpan.FromMinutes(4);
        _timer = new Timer(_ => _post(OnTimeout), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // Bind the wire sink used to issue the summon command and the re-survey.
    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    // Every buffer this router pushed to the wire, in order (test seam).
    internal IReadOnlyList<byte[]> LastSentForTests => _wire.LastSentForTests;

    // True while a summon detour is walking, fighting, or collecting.
    public bool DetourActive => _phase != Phase.Idle;

    // The monster this detour is waiting on, or null when idle. Lets the bug report
    // state what the run is fighting for.
    public string? PendingMonsterName => _phase == Phase.Idle ? null : _source.MonsterName;

    // New-need callback (wired to NeedsRegistry.NeedPosted). Arms a detour toward
    // the fewest-added-steps summon room when the item is flagged, no engine walk
    // is driving, no cheaper source exists, and we can route both to the room and
    // on to the destination. A no-op otherwise.
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

        // A free give or a shop buy costs no combat — both preempt this.
        if (_cheaperSourceExists(itemId)) return;

        // A route can need several items, and the demand tracker posts one need per
        // item synchronously in a loop — so a sibling router may already have
        // claimed an earlier item and redirected the walk. Claiming a second item
        // now would have both routers drive walkTo and leave the loser stranded, so
        // stand down and let this need wait for the next walk.
        if (_siblingDetourActive()) return;

        if (_currentRoom() is not { } cur) return;
        if (_walkDestination() is not { } dest) return;

        if (!TrySelectSource(cur, dest, itemId, out SummonSource src)) return;

        _itemId = itemId;
        _targetCount = target;
        _reSurveys = 0;
        _origDest = dest;
        _source = src;
        _phase = Phase.WalkingToSummon;
        ArmTimer();
        _log?.Info(LogCategory,
            $"path item {itemId} ('{name}') dropped by '{src.MonsterName}' on a guaranteed summon — " +
            $"detouring to {src.Room} to '{src.Command}'");
        _post(() => _walkTo(src.Room));
    }

    // Walker-event callback (wired to AutoWalkManager.Event). Arrival at the summon
    // room types the command; a failed walk resumes to the destination; a
    // user-driven walk abandons the detour.
    public void OnWalkEvent(WalkEvent e)
    {
        switch (_phase)
        {
            case Phase.WalkingToSummon:
                if (e.Kind == WalkEventKind.Finished)
                {
                    if (KeyMatches(e.Destination, _source.Room)) BeginSummoning();
                    // A Finished for somewhere else means a sibling detour
                    // superseded our walk. Those redirects are silent by design, so
                    // no Stopped is coming and this is the only signal we get —
                    // without it the detour would sit in WalkingToSummon until the
                    // deadline, blocking the one door-key source for that whole time.
                    else Reset();
                }
                else if (e.Kind == WalkEventKind.Failed)
                {
                    _log?.Info(LogCategory,
                        $"summon room {_source.Room} unreachable ({e.Detail}) — resuming to {_origDest}");
                    ResumeToPath();
                }
                else if (e.Kind == WalkEventKind.Stopped)
                    Reset();   // user / another engine took over — abandon quietly
                break;

            case Phase.AwaitingKill:
                // Standing in the summon room through the fight and the pickup. A
                // fresh walk means the user redirected — drop the detour. Note this
                // abandons the DETOUR, not the fight: combat is not ours to stop.
                if (e.Kind is WalkEventKind.Started or WalkEventKind.Stopped)
                    Reset();
                break;
        }
    }

    // Monster-death callback (wired to MonsterDeathWatcher.MonsterDied). The drop
    // isn't announced on the death line, so re-survey the floor and let
    // PathItemFloorCollector grab it.
    //
    // The death is NOT taken as our summon's — the watcher reports no identity for
    // any kill, so we can't know. It stays in AwaitingKill and simply re-surveys:
    // our kill lands the key (and OnInventoryChanged resumes), someone else's lands
    // nothing and we keep waiting. Capped so a busy room can't flood the wire.
    public void OnMonsterDied(MonsterDeathEvent evt)
    {
        if (_phase != Phase.AwaitingKill) return;
        if (_reSurveys >= MaxReSurveys) return;

        _reSurveys++;
        _log?.Info(LogCategory,
            $"a kill landed while waiting on '{_source.MonsterName}' — re-surveying " +
            $"{_source.Room} for path item {_itemId} ({_reSurveys}/{MaxReSurveys})");
        _wire.Send("look");
    }

    // Inventory-change callback (wired to InventoryManager.Changed). When the item
    // we detoured for is now carried — the drop collected, or search / a party
    // hand-off revealing it en route — resume the original walk. Doubles as the
    // found-first abort for an in-flight walk to the summon room.
    public void OnInventoryChanged()
    {
        if (_phase == Phase.Idle) return;
        if (_carriedCount(_itemId) < _targetCount) return;   // still short
        _log?.Info(LogCategory, $"path item {_itemId} acquired — resuming to {_origDest}");
        ResumeToPath();
    }

    // Detour budget elapsed in whatever phase we were in — the walk was superseded
    // and never reported, the summon never died (combat off, a fight we lost, an
    // already-claimed GameLimit monster), or the drop never reached inventory.
    // Resume to the destination and leave the need outstanding for the other
    // fulfillers. Invoked on the UI thread via the injected post delegate; tests
    // call it directly.
    public void OnTimeout()
    {
        if (_phase == Phase.Idle) return;
        if (_carriedCount(_itemId) >= _targetCount) return;   // race: OnInventoryChanged has it
        _log?.Info(LogCategory,
            _phase == Phase.WalkingToSummon
                ? $"never reached summon room {_source.Room} — resuming to {_origDest}"
                : $"path item {_itemId} did not land within the summon window — resuming to {_origDest}");
        ResumeToPath();
    }

    public void Dispose() => _timer.Dispose();

    private void BeginSummoning()
    {
        _phase = Phase.AwaitingKill;
        _log?.Info(LogCategory,
            $"at {_source.Room} — '{_source.Command}' to summon '{_source.MonsterName}' " +
            $"for path item {_itemId}");
        _wire.Send(_source.Command);
    }

    private void ResumeToPath()
    {
        DisarmTimer();
        RoomKey dest = _origDest;
        _phase = Phase.Idle;
        _post(() => _walkTo(dest));
    }

    private void Reset()
    {
        DisarmTimer();
        _phase = Phase.Idle;
    }

    private void ArmTimer() => _timer.Change(_detourTimeout, Timeout.InfiniteTimeSpan);

    private void DisarmTimer()
        => _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private bool TrySelectSource(RoomKey cur, RoomKey dest, int itemId, out SummonSource best)
        => TrySelectSource(_summonSourcesForItem(itemId), cur, dest, _distanceBetween, out best);

    // Pick the summon room minimising dist(cur,room)+dist(room,dest) among the
    // candidates reachable both ways — the fewest steps added to the trip, the same
    // metric the give and shop routers use. Ties break on the nearer room, then
    // room-key order, for determinism. Static + internal so the route picker can
    // name the very source this router would detour to without duplicating the rule.
    internal static bool TrySelectSource(
        IReadOnlyList<SummonSource> sources, RoomKey cur, RoomKey dest,
        Func<RoomKey, RoomKey, int?> distanceBetween, out SummonSource best)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(distanceBetween);
        best = default;
        int bestTotal = int.MaxValue;
        int bestToRoom = int.MaxValue;
        foreach (SummonSource s in sources)
        {
            if (distanceBetween(cur, s.Room) is not { } toRoom) continue;
            if (distanceBetween(s.Room, dest) is not { } toDest) continue;
            int total = toRoom + toDest;
            bool better = total < bestTotal
                || (total == bestTotal && toRoom < bestToRoom)
                || (total == bestTotal && toRoom == bestToRoom && CompareKeys(s.Room, best.Room) < 0);
            if (better)
            {
                bestTotal = total;
                bestToRoom = toRoom;
                best = s;
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

using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Sell detours. An item flagged "Make detours to sell this item" (with Auto-sell)
// that's carried above its "Detour to sell if above" count turns a running walk-to,
// loop or Auto-Lair aside to a shop that trades it: stop the engine, walk to the
// shop, let AutoSellManager sell there on arrival, then carry on — the walk-to
// resumes to its destination, a loop / lair walks back to where it left off and
// restarts (DetourResume, shared with the auto-deposit reroute).
//
// Shop choice: the shops the user ticked "Sell here" on the item (any that trade it
// when none are), and among those the one adding the fewest steps to the trip
// (PathItemShopRouter.TrySelectShop). No detour when the running engine reaches such
// a shop on its own — the walk ends there, the loop passes through it, or it's a
// marked lair room — since Auto-sell sells in passing. After one shop, a still-due
// item whose shops weren't tried yet sends the detour on to the next before heading
// back. A shop that sold nothing for an item isn't tried for it again this session.
//
// Single controller: it only starts when nothing else owns movement (the caller's
// blocked probe covers combat, party following and the other errand engines) and
// stands down if movement is stopped externally mid-detour. A walk sends its next
// step the moment a room confirms, so a detour that comes due mid-step holds the
// engine at the room it's entering (SellDetourGate) and takes over from there. UI
// thread only.
public sealed class SellDetourManager : IDisposable
{
    public const string LogCategory = "SellDetour";

    // One carried item that can take a sell detour: how many are carried, the keep
    // floor Auto-sell stops at, the count above which a detour goes, and the shop
    // rooms it may use (already narrowed to the user's picks).
    public sealed record Candidate(int Number, string Name, int Carried, int KeepFloor, int DetourAbove,
        IReadOnlyList<RoomKey> Shops)
    {
        public bool Due => Carried > Math.Max(KeepFloor, DetourAbove);
    }

    private enum Phase { Idle, WalkingToShop, Selling, WalkingBack }

    private readonly Func<IReadOnlyList<Candidate>> _candidates;
    private readonly Func<RoomKey, RoomKey, int?> _distance;
    private readonly RoomTracker _tracker;
    private readonly AutoWalkManager _walker;
    private readonly LoopRunner _loops;
    private readonly AutoLairManager _lair;
    private readonly AutoSellManager _sell;
    private readonly MovementCoordinator _coordinator;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _blocked;
    private readonly LogService? _log;

    private Phase _phase = Phase.Idle;
    private DetourResume _resume;
    private RoomKey _origin;
    private RoomKey _shop;
    private bool _drivingWalker;
    private readonly HashSet<RoomKey> _visited = new();
    // (item, shop) pairs that sold nothing — not tried again this session.
    private readonly HashSet<(int Item, RoomKey Shop)> _refused = new();
    private Dictionary<int, int> _carriedAtShop = new();
    private bool _gateHeld;
    private bool _disposed;

    public SellDetourManager(
        Func<IReadOnlyList<Candidate>> candidates,
        Func<RoomKey, RoomKey, int?> distance,
        RoomTracker tracker,
        AutoWalkManager walker,
        LoopRunner loops,
        AutoLairManager lair,
        AutoSellManager sell,
        MovementCoordinator coordinator,
        Func<bool> isEnabled,
        Func<bool> blocked,
        LogService? log = null)
    {
        _coordinator = coordinator;
        _candidates = candidates;
        _distance = distance;
        _tracker = tracker;
        _walker = walker;
        _loops = loops;
        _lair = lair;
        _sell = sell;
        _isEnabled = isEnabled;
        _blocked = blocked;
        _log = log;
        _walker.Event += OnWalkEvent;
        _sell.Finished += OnSellFinished;
    }

    public bool IsDetouring => _phase != Phase.Idle;

    // One-line status for the bug report.
    public string Status => _phase switch
    {
        Phase.Idle => "idle",
        Phase.WalkingBack => $"walking back to {_origin} (resume {_resume.Kind})",
        _ => $"{_phase} at/to {_shop} (resume {_resume.Kind})",
    };

    // Re-check whether a detour is due. Driven by the heartbeat and inventory changes;
    // a no-op unless a walk / loop / lair runs, the room is settled and nothing else
    // owns movement.
    public void Evaluate()
    {
        if (_phase != Phase.Idle) return;
        if (!_isEnabled() || _blocked() || _tracker.State.CurrentRoom is not { } here) { ReleaseStop("not now"); return; }
        DetourResume resume = DetourResume.Snapshot(_walker, _loops, _lair, includeWalk: true);
        if (resume.Kind == DetourResumeKind.None) { ReleaseStop("nothing running"); return; }

        RoomKey cur = here.Key;
        RoomKey back = resume.WalkDestination ?? cur;
        List<RoomKey> shops = new();
        foreach (Candidate c in _candidates())
        {
            if (!c.Due) continue;
            List<RoomKey> usable = c.Shops.Where(s => !_refused.Contains((c.Number, s))).ToList();
            if (usable.Count == 0) continue;
            // The engine reaches one of its shops anyway: Auto-sell sells in passing.
            if (usable.Any(s => s.Equals(cur) || ReachedAnyway(s, resume, cur))) continue;
            shops.AddRange(usable);
        }
        if (shops.Count == 0) { ReleaseStop("nothing to sell"); return; }
        if (!PathItemShopRouter.TrySelectShop(shops.Distinct().ToList(), cur, back, _distance, out RoomKey shop))
        {
            ReleaseStop("no reachable shop");
            return;
        }

        // Mid-step: hold the engine at the room it's entering and take over there.
        if (_tracker.State.Confidence != RoomConfidence.Confirmed)
        {
            if (!_gateHeld)
            {
                _gateHeld = true;
                _coordinator.AssertGate(MovementCoordinator.SellDetourGate, nameof(SellDetourManager),
                    "stopping at the next room to detour and sell");
            }
            return;
        }

        _resume = resume;
        _origin = cur;
        _visited.Clear();
        _log?.Info(LogCategory, $"detouring from {cur} to sell at {shop} (resume {resume.Kind}{(resume.WalkDestination is { } d ? $" to {d}" : "")})");
        _drivingWalker = true;
        try { resume.Stop(_walker, _loops, _lair, "sell detour"); }
        finally { _drivingWalker = false; }
        ReleaseStop("detour started");
        GoToShop(shop);
    }

    // Reset States: stand down without resuming.
    public void Cancel()
    {
        ReleaseStop("cancelled");
        if (_phase != Phase.Idle) GoIdle("cancelled");
    }

    private void ReleaseStop(string why)
    {
        if (!_gateHeld) return;
        _gateHeld = false;
        _coordinator.ClearGate(MovementCoordinator.SellDetourGate, nameof(SellDetourManager), why);
    }

    // Whether the running engine will stand in this shop room without a detour.
    private bool ReachedAnyway(RoomKey shop, DetourResume resume, RoomKey cur) => resume.Kind switch
    {
        DetourResumeKind.Walk => resume.WalkDestination is { } d && d.Equals(shop),
        DetourResumeKind.Lair => _lair.IsMarked(shop),
        DetourResumeKind.Loop => _loops.ResolveLoopRoomKeys(cur).Contains(shop),
        _ => false,
    };

    private void GoToShop(RoomKey shop)
    {
        _shop = shop;
        _visited.Add(shop);
        _phase = Phase.WalkingToShop;
        if (!DriveWalk(shop))
        {
            _log?.Warn(LogCategory, $"can't reach {shop} — carrying on");
            foreach (Candidate c in _candidates())
                if (c.Shops.Contains(shop)) _refused.Add((c.Number, shop));
            Next();
        }
    }

    private bool DriveWalk(RoomKey to)
    {
        _drivingWalker = true;
        try { return _walker.WalkTo(to, planThroughAcquirableGates: true); }
        finally { _drivingWalker = false; }
    }

    private void OnWalkEvent(WalkEvent e)
    {
        if (_phase is Phase.Idle or Phase.Selling) return;
        if (e.Kind == WalkEventKind.Stopped)
        {
            if (_drivingWalker) return;
            GoIdle($"movement stopped externally ({e.Detail})");
            return;
        }
        if (_phase == Phase.WalkingToShop)
        {
            if (e.Kind == WalkEventKind.Finished) ArrivedAtShop();
            else if (e.Kind == WalkEventKind.Failed)
            {
                _log?.Warn(LogCategory, $"path to {_shop} failed — carrying on");
                Next();
            }
        }
        else if (_phase == Phase.WalkingBack && e.Kind is WalkEventKind.Finished or WalkEventKind.Failed)
        {
            Resume();
        }
    }

    // Auto-sell already queued on arrival (RoomTracker's room change runs ahead of the
    // walker's Finished); wait for it, or move on when there was nothing it would sell.
    private void ArrivedAtShop()
    {
        _carriedAtShop = _candidates().ToDictionary(c => c.Number, c => c.Carried);
        if (_sell.IsSelling)
        {
            _phase = Phase.Selling;
            _log?.Info(LogCategory, $"selling at {_shop}");
            return;
        }
        NoteUnsold();
        Next();
    }

    private void OnSellFinished()
    {
        if (_phase != Phase.Selling) return;
        NoteUnsold();
        Next();
    }

    // An item still carried at its arrival count sold nothing here — don't come back
    // to this shop for it.
    private void NoteUnsold()
    {
        foreach (Candidate c in _candidates())
            if (c.Shops.Contains(_shop) && _carriedAtShop.TryGetValue(c.Number, out int before) && c.Carried >= before)
                _refused.Add((c.Number, _shop));
    }

    // Another shop for a still-due item, or head back.
    private void Next()
    {
        if (_tracker.State.CurrentRoom is { } here)
        {
            RoomKey back = _resume.WalkDestination ?? _origin;
            List<RoomKey> shops = _candidates()
                .Where(c => c.Due)
                .SelectMany(c => c.Shops.Where(s => !_visited.Contains(s) && !_refused.Contains((c.Number, s))))
                .Distinct().ToList();
            if (shops.Count > 0
                && PathItemShopRouter.TrySelectShop(shops, here.Key, back, _distance, out RoomKey next))
            {
                _log?.Info(LogCategory, $"on to {next} for what's left");
                GoToShop(next);
                return;
            }
        }

        // A walk-to just resumes toward its destination; a loop / lair walks back to
        // where it was first.
        if (_resume.Kind == DetourResumeKind.Walk || _tracker.State.CurrentRoom?.Key.Equals(_origin) == true)
        {
            Resume();
            return;
        }
        _phase = Phase.WalkingBack;
        _log?.Info(LogCategory, $"walking back to {_origin}");
        if (!DriveWalk(_origin)) Resume();
    }

    private void Resume()
    {
        DetourResume r = _resume;
        _phase = Phase.Idle;
        _log?.Info(LogCategory, $"detour done — resuming {r.Kind}");
        _drivingWalker = true;
        try { r.Resume(_walker, _loops, _lair); }
        finally { _drivingWalker = false; }
    }

    private void GoIdle(string why)
    {
        _log?.Info(LogCategory, $"detour abandoned — {why}");
        _phase = Phase.Idle;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _walker.Event -= OnWalkEvent;
        _sell.Finished -= OnSellFinished;
        ReleaseStop("disposed");
    }
}

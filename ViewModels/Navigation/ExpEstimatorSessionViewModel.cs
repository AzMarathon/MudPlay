using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// Ephemeral session for the Navigation window's Exp/Hr Estimator mode. Mirrors
// LoopBuilderSessionViewModel — tracks the user's clicked rooms as an ordered
// loop and BFS-fills the preview polyline — but instead of a step count it
// resolves the route's lair/NPC targets (RouteExpResolver) and replays them
// (LoopExpSimulator) to estimate exp/hr, exposing the per-lair fires/misses/
// shortfall breakdown for tuning. Save persists the click list as a normal Loop,
// exactly like the builder, so an estimated loop can be run. The Simulator window
// can play the live character around the sketch (SimWaypoints); RouteChanged tells
// it the route it may have played is gone.
public sealed partial class ExpEstimatorSessionViewModel : ObservableObject
{
    private readonly RouteExpResolver _resolver;
    private readonly LoopManager _loops;
    private readonly RoomGraphManager _graph;
    private readonly GameDataCache _gameData;
    private readonly IRoomFilter? _filter;
    private readonly List<RoomKey> _clicks = new();
    // A loaded loop's own waypoints, so the simulation counts its in-room commands'
    // delays; dropped by any edit to the route, which then runs as bare clicks.
    private IReadOnlyList<LoopWaypoint>? _loadedWaypoints;
    public ExpEstimatorSessionViewModel(
        RouteExpResolver resolver, LoopManager loops, RoomGraphManager graph,
        GameDataCache gameData, IRoomFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(gameData);
        _resolver = resolver;
        _loops = loops;
        _graph = graph;
        _gameData = gameData;
        _filter = filter;
        ProposedName = $"Loop {DateTime.Now:HH-mm}";
    }

    // Active realm drives only how often a room's summon spell re-rolls (Paradigm on
    // the combat round, Stock on the medium tick); the estimate otherwise uses the
    // user's own kill-rate knobs. Shown read-only so the user knows which cadence the
    // summon figures assume.
    public RealmType Realm => _gameData.ActiveRealm;
    public string RealmLabel => Realm == RealmType.ParaMud ? "Paradigm" : "Stock";

    // Ordered clicked rooms (reuses the loop-builder row shape).
    public ObservableCollection<LoopBuilderRow> Clicks { get; } = new();

    // Per-lair readout: how often each fires / misses, and the closest miss.
    public ObservableCollection<ExpEstimatorLairRow> Lairs { get; } = new();

    // Boss readout: monsters (GameLimit 1 / long regen) pulled out of the lair
    // average and amortised over their regen — a flat exp/hr contribution counted
    // once for the whole loop, not per room.
    public ObservableCollection<ExpEstimatorBossRow> Bosses { get; } = new();
    public bool HasBosses => Bosses.Count > 0;

    // Room-spell summon readout: rooms whose entry spell summons monsters, and the
    // expected exp/hr each contributes (already folded into the headline estimate).
    public ObservableCollection<ExpEstimatorSummonRow> Summons { get; } = new();
    public bool HasSummons => Summons.Count > 0;

    [ObservableProperty] private string _proposedName = "";
    [ObservableProperty] private IReadOnlyList<RoomKey>? _previewedRoomKeys;
    [ObservableProperty] private IReadOnlyList<RoomKey>? _waypointKeys;

    // Tunables — each change re-runs the estimate.
    [ObservableProperty] private double _secondsPerStep = 1.4;
    [ObservableProperty] private bool _areaCombat;                 // false = single-target, true = AoE ("rooming")
    [ObservableProperty] private double _roundsPerMob = 1.0;
    [ObservableProperty] private double _realConditionsMultiplier = 0.9;

    // Results.
    [ObservableProperty] private double _expPerHour;
    [ObservableProperty] private double _avgLapSeconds;
    [ObservableProperty] private int _lapsPerHour;
    [ObservableProperty] private string _summary = "Click rooms on the map to build a loop.";

    // The route changed: a click added, removed or moved, the sketch cleared or
    // replaced. Not raised for the estimate's own knobs.
    public event Action? RouteChanged;

    partial void OnSecondsPerStepChanged(double value) => Recompute();
    partial void OnAreaCombatChanged(bool value) => Recompute();
    partial void OnRoundsPerMobChanged(double value) => Recompute();
    partial void OnRealConditionsMultiplierChanged(double value) => Recompute();

    public bool HasClicks => Clicks.Count > 0;
    public bool CanSave => Clicks.Count >= 2;

    public void AddClick(RoomKey key)
    {
        if (_graph.GetRoom(key) is not { } room) return;
        if (_clicks.Count > 0 && _clicks[^1].Equals(key)) return;   // adjacent dupe gap-fills to nothing
        _clicks.Add(key);
        _loadedWaypoints = null;
        Clicks.Add(new LoopBuilderRow(Clicks.Count + 1, key, room.DisplayName));
        OnPropertyChanged(nameof(HasClicks));
        RouteChanged?.Invoke();
        Recompute();
    }

    // Seed the session from a saved loop. Its waypoints are kept for the simulation
    // only when every one made it in as a click — a room missing from the map would
    // leave them describing a different route.
    public void LoadWaypoints(IReadOnlyList<LoopWaypoint> waypoints)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        foreach (LoopWaypoint w in waypoints) AddClick(w.Key);
        var keys = new List<RoomKey>(waypoints.Count);
        foreach (LoopWaypoint w in waypoints)
            if (keys.Count == 0 || !keys[^1].Equals(w.Key)) keys.Add(w.Key);
        _loadedWaypoints = keys.SequenceEqual(_clicks) ? waypoints : null;
    }

    // The route the simulation walks: the loaded loop's waypoints while unedited,
    // else the bare clicks.
    internal IReadOnlyList<LoopWaypoint> SimWaypoints() =>
        _loadedWaypoints ?? _clicks.Select(k => new LoopWaypoint(k)).ToList();

    public void RemoveClickAt(int index)
    {
        if (index < 0 || index >= _clicks.Count) return;
        _clicks.RemoveAt(index);
        _loadedWaypoints = null;
        Clicks.RemoveAt(index);
        Renumber();
        OnPropertyChanged(nameof(HasClicks));
        RouteChanged?.Invoke();
        Recompute();
    }

    // Alt+click on the map: take the room back out — its most recent click, as the
    // loop builder does. False when the room isn't in the sketch.
    public bool RemoveLastClickOf(RoomKey key)
    {
        int index = _clicks.LastIndexOf(key);
        if (index < 0) return false;
        RemoveClickAt(index);
        return true;
    }

    // Map drag: move the room's most recent click to another room, as the loop
    // builder does; a move that leaves the sketch with no route is undone.
    public bool MoveLastClickOf(RoomKey from, RoomKey to)
    {
        int index = _clicks.LastIndexOf(from);
        if (index < 0 || from.Equals(to) || _graph.GetRoom(to) is not { } room) return false;
        bool wasRoutable = _clicks.Count < 2 || PreviewedRoomKeys is not null;
        LoopBuilderRow old = Clicks[index];
        _clicks[index] = to;
        Clicks[index] = old with { Key = to, Name = room.DisplayName };
        Recompute();
        if (wasRoutable && _clicks.Count >= 2 && PreviewedRoomKeys is null)
        {
            _clicks[index] = from;
            Clicks[index] = old;
            Recompute();
            return false;
        }
        _loadedWaypoints = null;
        RouteChanged?.Invoke();
        return true;
    }

    public void MoveClick(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= _clicks.Count) return;
        if (toIndex < 0 || toIndex >= _clicks.Count) return;
        RoomKey key = _clicks[fromIndex];
        _clicks.RemoveAt(fromIndex);
        _clicks.Insert(toIndex, key);
        _loadedWaypoints = null;
        Clicks.Move(fromIndex, toIndex);
        Renumber();
        RouteChanged?.Invoke();
        Recompute();
    }

    public void Clear()
    {
        _clicks.Clear();
        _loadedWaypoints = null;
        Clicks.Clear();
        Lairs.Clear();
        ExpPerHour = 0;
        AvgLapSeconds = 0;
        LapsPerHour = 0;
        PreviewedRoomKeys = null;
        WaypointKeys = null;
        Summary = "Click rooms on the map to build a loop.";
        OnPropertyChanged(nameof(HasClicks));
        OnPropertyChanged(nameof(CanSave));
        RouteChanged?.Invoke();
    }

    private void Renumber()
    {
        for (int i = 0; i < Clicks.Count; i++) Clicks[i] = Clicks[i] with { Index = i + 1 };
    }

    public Loop? BuildTransient()
    {
        if (_clicks.Count < 2) return null;
        var waypoints = new List<LoopWaypoint>(_clicks.Count);
        foreach (RoomKey k in _clicks) waypoints.Add(new LoopWaypoint(k));
        return new Loop(ProposedName, waypoints);
    }

    // Freeze the current session for a bug-report capture — route, tunables, and
    // the live estimate + per-lair breakdown, pre-formatted so the report is a
    // straight print.
    public ExpEstimatorSnapshot ToSnapshot()
    {
        var rooms = new List<string>(Clicks.Count);
        foreach (LoopBuilderRow r in Clicks)
            rooms.Add($"{r.Index}. {r.Key.Map}/{r.Key.Room}  {r.Name}");

        var lairs = new List<string>(Lairs.Count);
        foreach (ExpEstimatorLairRow l in Lairs)
        {
            string miss = l.MissesPerHour == 0 ? "no misses" : $"{l.MissesPerHour}/hr early by {l.ClosestMissShortfallSeconds:N0}s";
            string summons = l.Summons ? " (summons)" : "";
            lairs.Add($"{l.Room.Map}/{l.Room.Room}  {l.Name} — {l.FiresLabel}, {miss}{summons}");
        }

        var bosses = new List<string>(Bosses.Count);
        foreach (ExpEstimatorBossRow b in Bosses)
            bosses.Add($"{b.Label} — {b.ContribLabel}");

        var summonLines = new List<string>(Summons.Count);
        foreach (ExpEstimatorSummonRow su in Summons)
            summonLines.Add($"{su.RoomLabel}  {su.SpellName} — {su.ContribLabel}");

        return new ExpEstimatorSnapshot(
            ProposedName, rooms, SecondsPerStep, AreaCombat, RoundsPerMob, RealConditionsMultiplier,
            ExpPerHour, AvgLapSeconds, LapsPerHour, Summary, lairs, bosses, summonLines, RealmLabel);
    }

    // Replace the sketch with a whole route at once — one estimate, not one per room
    // (a Simulator ranking picked to show on the map).
    public void LoadRoute(IReadOnlyList<RoomKey> rooms, string name)
    {
        _clicks.Clear();
        Clicks.Clear();
        foreach (RoomKey key in rooms)
        {
            if (_graph.GetRoom(key) is not { } room) continue;
            if (_clicks.Count > 0 && _clicks[^1].Equals(key)) continue;   // adjacent dupe, as AddClick
            _clicks.Add(key);
            Clicks.Add(new LoopBuilderRow(Clicks.Count + 1, key, room.DisplayName));
        }
        _loadedWaypoints = null;
        ProposedName = name;
        OnPropertyChanged(nameof(HasClicks));
        RouteChanged?.Invoke();
        Recompute();
    }

    private void Recompute()
    {
        WaypointKeys = _clicks.Count == 0 ? null : new List<RoomKey>(_clicks);
        Lairs.Clear();
        Bosses.Clear();
        Summons.Clear();
        OnPropertyChanged(nameof(HasBosses));
        OnPropertyChanged(nameof(HasSummons));

        if (_clicks.Count < 2)
        {
            ExpPerHour = 0;
            AvgLapSeconds = 0;
            LapsPerHour = 0;
            PreviewedRoomKeys = null;
            Summary = _clicks.Count == 0 ? "Click rooms on the map to build a loop." : "Add at least 2 rooms.";
            OnPropertyChanged(nameof(CanSave));
            return;
        }

        var waypoints = new List<LoopWaypoint>(_clicks.Count);
        foreach (RoomKey k in _clicks) waypoints.Add(new LoopWaypoint(k));

        // Preview polyline (same BFS the loop builder uses).
        (IReadOnlyList<LoopStep> steps, var unreachable) = _loops.ExpandWaypoints(waypoints, _filter);
        PreviewedRoomKeys = unreachable.Count == 0 ? BuildSequence(_clicks[0], steps) : null;

        // Estimate: resolve the route's targets, then replay it on the clock.
        ExpRoute route = _resolver.Resolve(waypoints, _filter);
        var settings = new ExpSimSettings(
            Math.Max(0, SecondsPerStep),
            AreaCombat ? ExpCombatMode.AreaAllTargets : ExpCombatMode.SingleTarget,
            Math.Max(0.1, RoundsPerMob),
            Math.Clamp(RealConditionsMultiplier, 0.1, 1.0),
            Realm: Realm);
        ExpSimResult r = LoopExpSimulator.Simulate(route, settings);

        ExpPerHour = r.ExpPerHour;
        AvgLapSeconds = r.AvgLapSeconds;
        LapsPerHour = r.LapsPerHour;
        foreach (ExpLairStat stat in r.Lairs)
        {
            string name = _graph.GetRoom(stat.Room)?.DisplayName ?? stat.Room.ToString();
            Lairs.Add(new ExpEstimatorLairRow(
                stat.Room, name, stat.FiresPerHour, stat.MissesPerHour, stat.ClosestMissShortfallSeconds,
                stat.Summons));
        }
        foreach (ExpBossStat b in r.Bosses)
            Bosses.Add(new ExpEstimatorBossRow(b.Name, b.ExpPerHour, b.RegenHours));
        foreach (ExpSummonStat su in r.Summons)
        {
            string name = _graph.GetRoom(su.Room)?.DisplayName ?? su.Room.ToString();
            Summons.Add(new ExpEstimatorSummonRow(su.Room, name, su.SpellName, su.ExpPerHour, su.SummonChance));
        }
        OnPropertyChanged(nameof(HasBosses));
        OnPropertyChanged(nameof(HasSummons));

        Summary = unreachable.Count > 0
            ? $"{unreachable.Count} unreachable segment(s) — fix the loop"
            : $"≈ {ExpPerHour:N0} exp/hr  ·  {LapsPerHour} laps  ·  {AvgLapSeconds:N1}s/lap";
        OnPropertyChanged(nameof(CanSave));
    }

    private IReadOnlyList<RoomKey>? BuildSequence(RoomKey start, IReadOnlyList<LoopStep> steps)
    {
        var sequence = new List<RoomKey>(steps.Count + 1) { start };
        RoomKey cursor = start;
        foreach (LoopStep step in steps)
        {
            if (step is not MoveLoopStep move) continue;
            if (_graph.GetRoom(cursor) is not { } room) return null;
            if (!room.Exits.TryGetValue(move.Direction, out RoomExit exit)) return null;
            cursor = exit.Target;
            sequence.Add(cursor);
        }
        return sequence;
    }
}

// One lair on the estimated route: how often it fires per hour, and — when the loop
// laps back before it repops — how far EARLY you arrive (the shortfall to ready). A
// near-miss flags "nudge the loop to catch it," a big shortfall flags "too fast for
// this lair, the lap outruns its respawn."
public sealed record ExpEstimatorLairRow(
    RoomKey Room, string Name, int FiresPerHour, int MissesPerHour, double ClosestMissShortfallSeconds,
    bool Summons = false)
{
    public bool NearMiss => MissesPerHour > 0 && ClosestMissShortfallSeconds > 0 && ClosestMissShortfallSeconds <= 15;

    // Map/room identifier — the list shows this rather than the (often repeated)
    // room name so each lair is individually identifiable.
    public string RoomLabel => $"{Room.Map}/{Room.Room}";

    public string FiresLabel => $"{FiresPerHour}/hr";

    public string MissLabel => MissesPerHour == 0
        ? "full"
        : $"early by {ClosestMissShortfallSeconds:N0}s";
}

// One boss on the route: killable only once per RegenHours, so it adds a flat
// ExpPerHour (boss exp ÷ regen), independent of the lap and counted once for the
// whole loop. Shown apart from the lairs so its share of the estimate is visible.
public sealed record ExpEstimatorBossRow(string Name, double ExpPerHour, int RegenHours)
{
    public string Label => string.IsNullOrWhiteSpace(Name) ? "boss" : Name;
    public string ContribLabel => $"+{ExpPerHour:N0}/hr · once per {RegenHours}h";
}

// One summoning room on the route: its entry spell rolls monsters (SummonChance to
// hit each check), contributing ExpPerHour to the estimate. Shown apart from the
// lairs so the extra yield these rooms provide is visible.
public sealed record ExpEstimatorSummonRow(
    RoomKey Room, string Name, string SpellName, double ExpPerHour, double SummonChance)
{
    public string RoomLabel => $"{Room.Map}/{Room.Room}";
    public string ContribLabel => $"+{ExpPerHour:N0}/hr · {SummonChance:P0} summon";
}

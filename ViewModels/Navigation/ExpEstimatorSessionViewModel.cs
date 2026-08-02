using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FujinTerm.Game.Map;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.Navigation;

// Ephemeral session for the Navigation window's Exp/Hr Estimator mode. Mirrors
// LoopBuilderSessionViewModel — tracks the user's clicked rooms as an ordered
// loop and BFS-fills the preview polyline — but instead of a step count it
// resolves the route's lair/NPC targets (RouteExpResolver) and replays them
// (LoopExpSimulator) to estimate exp/hr, exposing the per-lair fires/misses/
// shortfall breakdown for tuning. Save persists the click list as a normal Loop,
// exactly like the builder, so an estimated loop can be run.
public sealed partial class ExpEstimatorSessionViewModel : ObservableObject
{
    private readonly RouteExpResolver _resolver;
    private readonly LoopManager _loops;
    private readonly RoomGraphManager _graph;
    private readonly IRoomFilter? _filter;
    private readonly List<RoomKey> _clicks = new();

    public ExpEstimatorSessionViewModel(
        RouteExpResolver resolver, LoopManager loops, RoomGraphManager graph, IRoomFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(graph);
        _resolver = resolver;
        _loops = loops;
        _graph = graph;
        _filter = filter;
        ProposedName = $"Loop {DateTime.Now:HH-mm}";
    }

    // Ordered clicked rooms (reuses the loop-builder row shape).
    public ObservableCollection<LoopBuilderRow> Clicks { get; } = new();

    // Per-lair readout: how often each fires / misses, and the closest miss.
    public ObservableCollection<ExpEstimatorLairRow> Lairs { get; } = new();

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
        Clicks.Add(new LoopBuilderRow(Clicks.Count + 1, key, room.DisplayName));
        OnPropertyChanged(nameof(HasClicks));
        Recompute();
    }

    public void RemoveClickAt(int index)
    {
        if (index < 0 || index >= _clicks.Count) return;
        _clicks.RemoveAt(index);
        Clicks.RemoveAt(index);
        Renumber();
        OnPropertyChanged(nameof(HasClicks));
        Recompute();
    }

    public void MoveClick(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex) return;
        if (fromIndex < 0 || fromIndex >= _clicks.Count) return;
        if (toIndex < 0 || toIndex >= _clicks.Count) return;
        RoomKey key = _clicks[fromIndex];
        _clicks.RemoveAt(fromIndex);
        _clicks.Insert(toIndex, key);
        Clicks.Move(fromIndex, toIndex);
        Renumber();
        Recompute();
    }

    public void Clear()
    {
        _clicks.Clear();
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
    }

    private void Renumber()
    {
        for (int i = 0; i < Clicks.Count; i++) Clicks[i] = Clicks[i] with { Index = i + 1 };
    }

    // Persist the current loop under ProposedName; returns the saved Loop or null.
    public Loop? Save()
    {
        Loop? loop = BuildTransient();
        if (loop is null) return null;
        _loops.Save(loop);
        return loop;
    }

    public Loop? BuildTransient()
    {
        if (_clicks.Count < 2) return null;
        var waypoints = new List<LoopWaypoint>(_clicks.Count);
        foreach (RoomKey k in _clicks) waypoints.Add(new LoopWaypoint(k));
        return new Loop(ProposedName, waypoints);
    }

    private void Recompute()
    {
        WaypointKeys = _clicks.Count == 0 ? null : new List<RoomKey>(_clicks);
        Lairs.Clear();

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
            Math.Clamp(RealConditionsMultiplier, 0.1, 1.0));
        ExpSimResult r = LoopExpSimulator.Simulate(route, settings);

        ExpPerHour = r.ExpPerHour;
        AvgLapSeconds = r.AvgLapSeconds;
        LapsPerHour = r.LapsPerHour;
        foreach (ExpLairStat stat in r.Lairs)
        {
            string name = _graph.GetRoom(stat.Room)?.DisplayName ?? stat.Room.ToString();
            Lairs.Add(new ExpEstimatorLairRow(
                stat.Room, name, stat.FiresPerHour, stat.MissesPerHour, stat.ClosestMissShortfallSeconds));
        }

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

// One lair on the estimated route: how often it fires vs is missed per hour, and
// the closest a miss came to being ready — a near-miss flags "nudge the loop to
// catch it," a big shortfall flags "too fast for this lair."
public sealed record ExpEstimatorLairRow(
    RoomKey Room, string Name, int FiresPerHour, int MissesPerHour, double ClosestMissShortfallSeconds)
{
    public bool NearMiss => MissesPerHour > 0 && ClosestMissShortfallSeconds > 0 && ClosestMissShortfallSeconds <= 15;

    public string FiresLabel => $"{FiresPerHour}/hr";

    public string MissLabel => MissesPerHour == 0
        ? "full"
        : $"missed by {ClosestMissShortfallSeconds:N0}s";
}

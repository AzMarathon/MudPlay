using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using MudPlay.Services;

namespace MudPlay.ViewModels.Navigation;

// Ephemeral session for the Navigation window's Exp/Hr Estimator mode. Mirrors
// LoopBuilderSessionViewModel — tracks the user's clicked rooms as an ordered
// loop and BFS-fills the preview polyline — but instead of a step count it
// resolves the route's lair/NPC targets (RouteExpResolver) and replays them
// (LoopExpSimulator) to estimate exp/hr, exposing the per-lair fires/misses/
// shortfall breakdown for tuning. Save persists the click list as a normal Loop,
// exactly like the builder, so an estimated loop can be run.
// Simulate plays the live character around the same route (LoopSimulator) for
// the fight-by-fight answer the estimate's rounds-to-kill knob stands in for.
public sealed partial class ExpEstimatorSessionViewModel : ObservableObject
{
    private readonly RouteExpResolver _resolver;
    private readonly LoopManager _loops;
    private readonly RoomGraphManager _graph;
    private readonly GameDataCache _gameData;
    private readonly IRoomFilter? _filter;
    private readonly SimulationSource? _simulation;
    private readonly LogService? _log;
    private readonly List<RoomKey> _clicks = new();
    // The walk pace the shown SimResult was run at — the bug report quotes this, not
    // a pace recomputed from gear that may have changed since.
    private double _simWalkUsed;
    private CancellationTokenSource? _simCancel;

    public ExpEstimatorSessionViewModel(
        RouteExpResolver resolver, LoopManager loops, RoomGraphManager graph,
        GameDataCache gameData, IRoomFilter? filter = null,
        SimulationSource? simulation = null, LogService? log = null)
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
        _simulation = simulation;
        _log = log;
        ProposedName = $"Loop {DateTime.Now:HH-mm}";
    }

    // The walk between rooms the simulation uses: the user's figure when set, else
    // the character's own pace — on Paradigm the server's move timer from gear
    // quickness and encumbrance plus SimLagMs of lag (GAME_MECHANICS "Per-hop
    // movement speed"); on Stock Auto-Lair's wall-clock pace by encumbrance, lag
    // already in it. Combat is simulated separately, so this is the bare walk,
    // unlike the estimate's Seconds per room.
    public double SimWalkSeconds => SimSecondsPerStep > 0
        ? SimSecondsPerStep
        : _simulation?.WalkSeconds(Math.Max(0, SimLagMs) / 1000.0) ?? (Realm == RealmType.ParaMud ? 1.1 : 0.7);

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

    // Character simulation — the live character played around this route by
    // LoopSimulator. A route or simulation-setting change drops the result as stale
    // and cancels a run still in flight; the estimate-only knobs leave it be.
    [ObservableProperty] private double _simSecondsPerStep;            // 0 = the character's own pace (SimWalkSeconds)
    [ObservableProperty] private int _simLagMs = 100;
    [ObservableProperty] private double _simHours = 1.0;
    [ObservableProperty] private int _simRuns = 3;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SimulateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckAgainstPlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(RankAreasCommand))]
    private bool _isSimulating;
    [ObservableProperty] private string _simStatus = "";
    [ObservableProperty] private LoopSimSummary? _simResult;
    public ObservableCollection<string> SimLines { get; } = new();

    // Simulator-vs-your-play check (CheckAgainstPlay): one line per loop and level.
    [ObservableProperty] private string _checkStatus = "";
    public ObservableCollection<string> CheckLines { get; } = new();

    // Area rankings (RankAreas): every area's lair tour at RankLevel (0 = current).
    [ObservableProperty] private int _rankLevel;
    [ObservableProperty] private string _rankStatus = "";
    [ObservableProperty] private AreaRank? _selectedRanking;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelRankingCommand))]
    private bool _isRanking;
    private CancellationTokenSource? _rankCts;
    public ObservableCollection<AreaRank> Rankings { get; } = new();
    public bool HasRankings => Rankings.Count > 0;
    public bool HasSimResult => SimResult is not null;
    public bool CanSimulate => _simulation is not null;

    partial void OnSimResultChanged(LoopSimSummary? value) => OnPropertyChanged(nameof(HasSimResult));
    // A simulation setting also clears the live check, which ran under it; a route
    // change doesn't — the check reads saved loops, not the sketch.
    partial void OnSimSecondsPerStepChanged(double value) => ClearSimulation(alsoCheck: true);
    partial void OnSimLagMsChanged(int value) => ClearSimulation(alsoCheck: true);
    partial void OnSimHoursChanged(double value) => ClearSimulation(alsoCheck: true);
    partial void OnSimRunsChanged(int value) => ClearSimulation(alsoCheck: true);

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
        ClearSimulation();
        Recompute();
    }

    public void RemoveClickAt(int index)
    {
        if (index < 0 || index >= _clicks.Count) return;
        _clicks.RemoveAt(index);
        Clicks.RemoveAt(index);
        Renumber();
        OnPropertyChanged(nameof(HasClicks));
        ClearSimulation();
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
        ClearSimulation();
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
        Clicks.Move(fromIndex, toIndex);
        Renumber();
        ClearSimulation();
        Recompute();
    }

    public void Clear()
    {
        ClearSimulation();
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
            ExpPerHour, AvgLapSeconds, LapsPerHour, Summary, lairs, bosses, summonLines, RealmLabel,
            SimResult is null ? null : SimLines.ToList(), SimResult is null ? SimWalkSeconds : _simWalkUsed, SimHours,
            CheckLines.Count == 0 ? null
                : (string.IsNullOrEmpty(CheckStatus) ? CheckLines : CheckLines.Prepend(CheckStatus)).ToList(),
            // The status alone still reports a ranking that was cancelled, failed or found nothing.
            Rankings.Count == 0 && string.IsNullOrEmpty(RankStatus) ? null
                : (string.IsNullOrEmpty(RankStatus) ? Rankings.Take(15).Select(r => r.Label)
                    : Rankings.Take(15).Select(r => r.Label).Prepend(RankStatus)).ToList());
    }

    // Play the live character around the route for SimRuns seeded runs of SimHours
    // each, off the UI thread (a run is a few hundred milliseconds of CPU per hour).
    // A run cancelled by a route / setting change or by closing the estimator is
    // dropped, so a late result never lands on a route it wasn't played on.
    [RelayCommand(CanExecute = nameof(CanRunSimulation))]
    private async Task SimulateAsync()
    {
        using var cancel = new CancellationTokenSource();
        _simCancel = cancel;
        try
        {
            if (_simulation?.Build(null) is not { } setup)
            {
                SimStatus = "No character yet — log in and type stat so the client knows your level and pools.";
                return;
            }
            var waypoints = _clicks.Select(k => new LoopWaypoint(k)).ToList();
            IReadOnlyList<SimRoom> lap = _resolver.ResolveSimLap(waypoints, _filter);
            if (lap.Count == 0)
            {
                SimStatus = "The route has no walkable lap — fix the loop first.";
                return;
            }

            (SimCharacter character, SimWorld world) = SimFreeze.For(setup.Character, setup.World, lap);
            double step = Math.Max(0.1, SimWalkSeconds), hours = Math.Clamp(SimHours, 0.1, 24);
            int runs = Math.Clamp(SimRuns, 1, 20);
            IsSimulating = true;
            SimStatus = $"Simulating {runs} × {hours:0.#} h…";
            CancellationToken token = cancel.Token;
            LoopSimSummary result = await Task.Run(() =>
                LoopSimulator.RunMany(character, lap, world, step, hours, runs, token), token);
            if (token.IsCancellationRequested) return;
            _simWalkUsed = step;
            SimResult = result;
            FillSimLines(result, step);
            SimStatus = "";
            _log?.Info("ExpEstimator",
                $"simulated '{ProposedName}' ({lap.Count} rooms, {runs}×{hours:0.#}h, {step:0.##}s/step, L{character.Level}): " +
                $"{result.ExpPerHour:N0} exp/hr ({result.MinExpPerHour:N0}–{result.MaxExpPerHour:N0}, " +
                $"bosses +{result.BossExpPerHour:N0}), {result.KillsPerHour:0} kills/hr, " +
                $"{result.DamageTakenPerHour:N0} dmg taken/hr, {result.FleesPerHour:0.#} flees/hr, " +
                $"{result.Deaths} death(s), {result.HangUps} hang-up(s), low HP {result.LowestHpPercent}%");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Cancelled on purpose (route or setting changed, estimator closed) — not an error.
        }
        catch (Exception ex)
        {
            // A simulation fault must never take the client down with it.
            _log?.Warn("ExpEstimator", $"simulation of '{ProposedName}' failed: {ex.GetType().Name}: {ex.Message}");
            SimStatus = "Simulation failed — see the program log.";
        }
        finally
        {
            if (ReferenceEquals(_simCancel, cancel)) _simCancel = null;
            IsSimulating = false;
        }
    }

    // Stop a simulation still running; its result is dropped.
    public void CancelSimulation() => _simCancel?.Cancel();

    private bool CanRunSimulation() => _simulation is not null && !IsSimulating && _clicks.Count >= 2;

    // Loops need this many live hours at one level before they're checked — a
    // shorter sample swings too far on luck to judge the simulator by.
    private const double CheckMinHours = 1.0;

    // Set every loop this character has played (per level, from the program logs)
    // against the simulator at that level, so the user can see how far to trust it.
    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task CheckAgainstPlayAsync()
    {
        if (_simulation is null) return;
        if (string.IsNullOrWhiteSpace(_simulation.Character()))
        {
            CheckStatus = "No character yet — log in and type `stat` first.";
            return;
        }
        string character = _simulation.Character()!;
        IsSimulating = true;
        CheckLines.Clear();
        CheckStatus = "Reading your program logs…";
        try
        {
            string dir = _simulation.LogsDir;
            IReadOnlyList<LiveLoopRecord> records = await Task.Run(() => LiveLoopSessions.Pool(
                LiveLoopSessions.ReadFolder(dir, msg => _log?.Warn("ExpEstimator", $"live check: {msg}")),
                character, CheckMinHours));
            if (records.Count == 0)
            {
                // Program logs exist only while Auto-collect logs is on (off by default)
                // and are pruned after DebugLogWriter.DefaultRetentionDays.
                CheckStatus = $"No loop played for {CheckMinHours:0} h or more at one level in your program logs " +
                              $"(the last {DebugLogWriter.DefaultRetentionDays} days). They're only written while " +
                              "Program Log (F4) → Auto-collect logs is on — turn it on and play your loops.";
                _log?.Info("ExpEstimator", $"live check for {character}: no loop with {CheckMinHours:0} h at one level in {dir}");
                return;
            }

            // Characters and laps are built (and frozen) here on the UI thread; the
            // simulations themselves run on a worker.
            var jobs = new List<(LiveLoopRecord Live, SimCharacter? Character, SimWorld? World, IReadOnlyList<SimRoom>? Lap, string? Problem)>();
            var laps = new Dictionary<string, IReadOnlyList<SimRoom>>(StringComparer.Ordinal);
            foreach (LiveLoopRecord r in records)
            {
                if (_loops.Get(r.Loop) is not { } loop) { jobs.Add((r, null, null, null, "loop no longer saved")); continue; }
                // One loop is often played at several levels; its lap is the same for each.
                if (!laps.TryGetValue(r.Loop, out IReadOnlyList<SimRoom>? lap))
                    laps[r.Loop] = lap = _resolver.ResolveSimLap(loop.Waypoints, _filter);
                if (lap.Count == 0) { jobs.Add((r, null, null, null, "route no longer resolves")); continue; }
                if (_simulation.Build(r.Level) is not { } setup) { jobs.Add((r, null, null, null, "no character")); continue; }
                (SimCharacter ch, SimWorld world) = SimFreeze.For(setup.Character, setup.World, lap);
                jobs.Add((r, ch, world, lap, null));
            }

            double step = Math.Max(0.1, SimWalkSeconds), hours = Math.Clamp(SimHours, 0.1, 24);
            int runs = Math.Clamp(SimRuns, 1, 20);
            CheckStatus = $"Simulating {jobs.Count(j => j.Problem is null)} loop(s), {runs} × {hours:0.#} h each…";
            IReadOnlyList<SimLiveCheckRow> rows = await Task.Run(() => jobs
                .Select(j => j.Problem is not null
                    ? new SimLiveCheckRow(j.Live, null, j.Problem)
                    : new SimLiveCheckRow(j.Live, LoopSimulator.RunMany(j.Character!, j.Lap!, j.World!, step, hours, runs)))
                .ToList());

            foreach (SimLiveCheckRow row in rows) CheckLines.Add(row.Label);
            // Another level is simulated with today's gear, stats and spells, so a
            // session from before an upgrade reads high for reasons the simulator
            // can't see.
            int now = _simulation.Level();
            if (rows.Any(r => r.Live.Level != now))
                CheckLines.Add($"Other levels are simulated with today's gear, stats and spells (you're L{now} now) — older sessions read high.");
            var diffs = rows.Where(r => r.DiffPercent is not null).Select(r => r.DiffPercent!.Value).ToList();
            CheckStatus = diffs.Count == 0 ? "" :
                $"{diffs.Count(d => Math.Abs(d) <= 10)} of {diffs.Count} within 10% · average {diffs.Average():+0.0;-0.0}%";
            _log?.Info("ExpEstimator", $"live check for {character} ({runs}×{hours:0.#}h, {step:0.##}s/step): " +
                string.Join(" | ", rows.Select(r => r.Label)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CheckLines.Clear();
            CheckStatus = $"The check failed: {ex.Message}";
            _log?.Warn("ExpEstimator", $"live check for {character} failed: {ex}");
        }
        finally
        {
            IsSimulating = false;
        }
    }

    private bool CanRunCheck() => _simulation is not null && !IsSimulating;

    // Every hunting area's lair tour (AreaTours, grouped by the Monsters' Region /
    // Area labels) played by the character at RankLevel, safe areas first by
    // exp/hr. Tours are mapped here on the UI thread — the room graph is rebuilt on
    // it (a set switch) and game data is read there — so a yield that lets input and
    // rendering through comes before each piece of that work (the reach search, each
    // area grouping, each tour search, each lap resolve, area and saved loop alike);
    // then the areas simulate in parallel on workers. CancelRanking (the Cancel
    // button, the session's teardown, or a room-graph reload) stops it at the next
    // yield or between simulations.
    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task RankAreasAsync()
    {
        if (_simulation is null || _filter is null) return;
        int level = RankLevel > 0 ? RankLevel : _simulation.Level();
        if (_simulation.Build(level) is not { } setup)
        {
            RankStatus = "No character yet — log in and type `stat` first.";
            return;
        }
        if (_simulation.Room() is not { } here)
        {
            RankStatus = "MudPlay doesn't know which room you're in yet — step into a room so it can tell which areas you can reach.";
            return;
        }
        using var cts = new CancellationTokenSource();
        _rankCts = cts;
        CancellationToken cancel = cts.Token;
        IsSimulating = true;
        IsRanking = true;
        Rankings.Clear();
        OnPropertyChanged(nameof(HasRankings));
        try
        {
            // Only what the character could reach from here at that level: level gates
            // (a (Level 50+) exit, a minimum-level sailing) judged at `level`, every
            // other gate as the live movement filter has it, boats and portals included.
            IRoomFilter gates = new LevelIgnoringFilter(_filter, level);
            RankStatus = "Finding the areas you can reach…";
            await YieldToUi(cancel);
            IReadOnlyDictionary<RoomKey, int> reach = _resolver.DistancesFrom(here, gates, viaBoats: true);
            await YieldToUi(cancel);
            var lairs = _resolver.LairRooms().ToList();
            var reachable = lairs.Where(l => reach.ContainsKey(l.Room)).ToList();
            await YieldToUi(cancel);
            IReadOnlyList<(string Area, IReadOnlyList<RoomKey> Rooms)> groups = AreaTours.Group(
                reachable, n => AreaLabel(setup.Character.Overlay(n)));
            await YieldToUi(cancel);
            int gatedAreas = AreaTours.Group(lairs, n => AreaLabel(setup.Character.Overlay(n))).Count - groups.Count;
            var jobs = new List<(AreaTour Tour, SimCharacter Character, SimWorld World, IReadOnlyList<SimRoom> Lap, bool IsLoop)>();
            int skippedAreas = 0, skippedLoops = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                RankStatus = $"Mapping area {i + 1} of {groups.Count}…";
                (string area, IReadOnlyList<RoomKey> rooms) = groups[i];
                var distances = new Dictionary<RoomKey, IReadOnlyDictionary<RoomKey, int>>();
                foreach (RoomKey from in AreaTours.SearchSources(rooms))
                {
                    await YieldToUi(cancel);
                    distances[from] = _resolver.DistancesTo(from, rooms, gates);
                }
                AreaTour tour = AreaTours.Order(area, rooms, k => distances[k]);
                if (tour.Rooms.Count < 2) { skippedAreas++; continue; }
                await YieldToUi(cancel);
                IReadOnlyList<SimRoom> lap = _resolver.ResolveSimLap(tour.Rooms.Select(k => new LoopWaypoint(k)).ToList(), gates);
                if (lap.Count == 0) { skippedAreas++; continue; }
                (SimCharacter ch, SimWorld world) = SimFreeze.For(setup.Character, setup.World, lap);
                jobs.Add((tour, ch, world, lap, false));
            }

            // The user's own saved loops rank beside the area tours — a loop tuned
            // inside a good area beats that area's whole tour — with what the logs
            // say the user actually made on each.
            foreach (Loop loop in _loops.Loops)
            {
                if (loop.Waypoints.Count < 2 || !reach.ContainsKey(loop.Waypoints[0].Key)) continue;
                RankStatus = $"Mapping your loop {loop.Name}…";
                await YieldToUi(cancel);
                IReadOnlyList<SimRoom> lap = _resolver.ResolveSimLap(loop.Waypoints, gates);
                if (lap.Count == 0) { skippedLoops++; continue; }
                (SimCharacter ch, SimWorld world) = SimFreeze.For(setup.Character, setup.World, lap);
                jobs.Add((new AreaTour(loop.Name, loop.Waypoints.Select(w => w.Key).ToList()), ch, world, lap, true));
            }
            string? character = _simulation.Character();
            string logs = _simulation.LogsDir;
            IReadOnlyList<LiveLoopRecord> live = string.IsNullOrWhiteSpace(character)
                ? Array.Empty<LiveLoopRecord>()
                : await Task.Run(() => LiveLoopSessions.Pool(LiveLoopSessions.ReadFolder(logs), character!, CheckMinHours), cancel);

            double step = Math.Max(0.1, SimWalkSeconds), hours = Math.Clamp(SimHours, 0.1, 24);
            int runs = Math.Clamp(SimRuns, 1, 20);
            string simulating = $"Simulating {jobs.Count(j => !j.IsLoop)} areas and {jobs.Count(j => j.IsLoop)} of your loops " +
                $"at L{level}, {runs} × {hours:0.#} h each";
            RankStatus = simulating + "…";
            int done = 0;
            var progress = new Progress<int>(_ =>
            {
                // A report queued behind the finish or a cancel mustn't overwrite its status.
                if (!IsRanking || cancel.IsCancellationRequested) return;
                RankStatus = $"{simulating} — {++done} of {jobs.Count} done…";
            });
            // One core is left free so the client (and the game it's playing) stays responsive.
            IReadOnlyList<AreaRank> ranked = AreaRank.Rank(await Task.Run(() => jobs.AsParallel()
                .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount - 1))
                .WithCancellation(cancel)
                .Select(j =>
                {
                    var rank = new AreaRank(j.Tour.Name, level, j.Tour.Rooms, j.Lap.Count,
                        LoopSimulator.RunMany(j.Character, j.Lap, j.World, step, hours, runs, cancel),
                        j.IsLoop, j.IsLoop ? LiveAt(live, j.Tour.Name, level) : null);
                    ((IProgress<int>)progress).Report(1);
                    return rank;
                })
                .ToList(), cancel));

            foreach (AreaRank r in ranked) Rankings.Add(r);
            OnPropertyChanged(nameof(HasRankings));
            int safe = ranked.Count(r => r.Safe);
            string skipped = (skippedAreas > 0 ? $"; {skippedAreas} area(s) skipped (one lair or no walkable lap)" : "")
                + (skippedLoops > 0 ? $"; {skippedLoops} of your loops skipped (no walkable lap)" : "");
            RankStatus = $"L{level} from {here.Map}/{here.Room}: {safe} safe option(s) (areas and your loops), best first; {ranked.Count - safe} where you died listed last"
                + (gatedAreas > 0 ? $"; {gatedAreas} area(s) you can't reach at L{level} left out" : "") + skipped
                + ". Pick one to load its tour.";
            _log?.Info("ExpEstimator", $"ranked {ranked.Count} options at L{level} ({gatedAreas} areas unreachable, " +
                $"{skippedAreas} areas and {skippedLoops} loops skipped): " +
                string.Join(" | ", ranked.Take(10).Select(r => r.Label)));
        }
        catch (Exception ex) when (cancel.IsCancellationRequested && IsCancellation(ex))
        {
            Rankings.Clear();
            OnPropertyChanged(nameof(HasRankings));
            RankStatus = "Ranking cancelled.";
            _log?.Info("ExpEstimator", $"area ranking at L{level} cancelled");
        }
        catch (Exception ex)
        {
            // A ranking fault must never take the client down with it.
            _log?.Warn("ExpEstimator", $"area ranking at L{level} failed: {ex.GetType().Name}: {ex.Message}");
            Rankings.Clear();
            OnPropertyChanged(nameof(HasRankings));
            RankStatus = "Ranking failed — see the program log.";
        }
        finally
        {
            _rankCts = null;
            IsRanking = false;
            IsSimulating = false;
        }
    }

    // Stops a running RankAreas; the Cancel button, and the Navigation window when
    // it tears this session down or the room graph reloads.
    [RelayCommand(CanExecute = nameof(IsRanking))]
    public void CancelRanking() => _rankCts?.Cancel();

    // PLINQ can hand a worker's cancel back wrapped in an AggregateException.
    private static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException
        || ex is AggregateException agg && agg.Flatten().InnerExceptions.All(e => e is OperationCanceledException);

    // Let queued input and rendering run before the next search on the UI thread.
    private static async Task YieldToUi(CancellationToken cancel)
    {
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
        cancel.ThrowIfCancellationRequested();
    }

    // What the user made on a loop: their record at the ranked level, and their
    // biggest sample within a few levels of it (the long runs are the trustworthy
    // ones) when that's a different level.
    private static IReadOnlyList<LiveLoopRecord> LiveAt(IReadOnlyList<LiveLoopRecord> live, string loop, int level)
    {
        var mine = live.Where(r => string.Equals(r.Loop, loop, StringComparison.OrdinalIgnoreCase)).ToList();
        var shown = new List<LiveLoopRecord>();
        if (mine.FirstOrDefault(r => r.Level == level) is { } same) shown.Add(same);
        if (mine.Where(r => Math.Abs(r.Level - level) <= 3).OrderByDescending(r => r.Hours).FirstOrDefault() is { } big
            && !shown.Contains(big))
            shown.Add(big);
        return shown;
    }

    // "Region / Area", or just the area when the two match; null when unfiled.
    private static string? AreaLabel(Models.GameData.MonsterOverlay o) =>
        string.IsNullOrWhiteSpace(o.Area) ? null
        : !string.IsNullOrWhiteSpace(o.Region) && !string.Equals(o.Region, o.Area, StringComparison.OrdinalIgnoreCase)
            ? $"{o.Region} / {o.Area}" : o.Area;

    partial void OnSelectedRankingChanged(AreaRank? value)
    {
        if (value is null) return;
        LoadRooms(value.Tour, value.Area);   // clears SimStatus, so the reminder goes after it
        // Simulate plays the sketch as the character stands now, not as ranked.
        if (_simulation?.Level() is int now && now != value.Level)
            SimStatus = $"Loaded from the L{value.Level} ranking — Simulate plays it at your current level (L{now}) and today's gates.";
    }

    // Replace the sketch with a whole route at once — one estimate, not one per room.
    private void LoadRooms(IReadOnlyList<RoomKey> rooms, string name)
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
        ProposedName = name;
        OnPropertyChanged(nameof(HasClicks));
        ClearSimulation();
        Recompute();
    }

    private void FillSimLines(LoopSimSummary r, double walkSeconds)
    {
        SimLines.Clear();
        SimLines.Add($"≈ {r.ExpPerHour:N0} exp/hr  ({r.MinExpPerHour:N0} – {r.MaxExpPerHour:N0} over {r.Runs.Count} runs)");
        SimLines.Add($"{r.KillsPerHour:0} kills/hr  ·  {r.Runs.Average(x => x.AvgLapSeconds):0}s/lap  ·  walking {walkSeconds:0.00}s/room");
        SimLines.Add($"Attacking {r.Share(x => x.AttackingSeconds):P0} · moving {r.Share(x => x.MovingSeconds):P0} · " +
                     $"resting {r.Share(x => x.RestingSeconds):P0} · meditating {r.Share(x => x.MeditatingSeconds):P0} · " +
                     $"waiting {r.Share(x => x.WaitingSeconds):P0}");
        SimLines.Add($"Lowest HP {r.LowestHpPercent}% · lowest mana {r.LowestManaPercent}% · {r.DamageTakenPerHour:N0} damage taken/hr");
        SimLines.Add(r.Deaths == 0 ? "No deaths" : $"Died in {r.Deaths} of {r.Runs.Count} runs");
        if (r.HangUps > 0) SimLines.Add($"Hung up in {r.HangUps} of {r.Runs.Count} runs");
        if (r.FleesPerHour > 0) SimLines.Add($"Fled {r.FleesPerHour:0.#} times an hour");
        foreach (ExpBossStat b in r.Bosses ?? Array.Empty<ExpBossStat>())
            SimLines.Add($"Boss {b.Name}: +{b.ExpPerHour:N0}/hr (once per {b.RegenHours:0.#}h, not fought in the runs)");
        var casts = r.CastsPerHour();
        if (casts.Count > 0)
            SimLines.Add("Per hour: " + string.Join(", ", casts.Take(6).Select(c => $"{c.Spell} {c.PerHour:0}")));
    }

    private void ClearSimulation(bool alsoCheck = false)
    {
        CancelSimulation();
        SimResult = null;
        SimLines.Clear();
        SimStatus = "";
        if (alsoCheck)
        {
            CheckLines.Clear();
            CheckStatus = "";
        }
        SimulateCommand.NotifyCanExecuteChanged();
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

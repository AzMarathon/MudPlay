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

// The Simulator window: plays the live character (LoopSimulator) around a saved loop
// or the estimator's sketch, sets the loops they've played against the simulator
// (Check against my play), and ranks every reachable hunting area at a level. It
// lives with the Navigation window rather than the estimator session, since only
// the sketch route needs estimator mode. A route, simulation setting, character or
// game-data change drops a result as stale and cancels a run still in flight. A
// finished ranking is kept for the game data and character it was made on
// (AreaRankingStore) and listed again when both are back; nothing made on one game
// data is listed, picked or saved under another.
public sealed partial class SimulatorViewModel : ObservableObject
{
    private readonly RouteExpResolver _resolver;
    private readonly LoopManager _loops;
    private readonly GameDataCache _gameData;
    private readonly IRoomFilter? _filter;
    private readonly SimulationSource? _simulation;
    private readonly LogService? _log;
    private readonly Func<ExpEstimatorSessionViewModel?> _sketch;
    private readonly Func<IReadOnlyList<RoomKey>, string, string?> _showOnMap;
    // The picker's sketch entry, kept while the estimator has a route to offer.
    private readonly SimRouteOption _sketchOption = new("", null);
    // The walk pace the shown SimResult was run at — the bug report quotes this, not
    // a pace recomputed from gear that may have changed since.
    private double _simWalkUsed;
    private string _simRouteName = "";
    private CancellationTokenSource? _simCancel;
    // The live check's own token: a route change cancels a simulation but must leave
    // the check (which reads saved loops, not the chosen route) running.
    private CancellationTokenSource? _checkCancel;
    private CancellationTokenSource? _rankCts;
    private readonly AreaRankingStore _rankings;
    // The loaded character as results are kept under (SimulatorOwner.CharacterKey),
    // read when it is needed: a profile comes to be known by another name without
    // being loaded again, and the name told at the last load would then be stale.
    private readonly Func<string?> _character;
    // The game data and character the results on show were made on.
    private SimulatorOwner _owner;

    // showOnMap loads a ranked tour into the estimator's sketch and returns null, or
    // returns why it couldn't.
    public SimulatorViewModel(
        RouteExpResolver resolver, LoopManager loops, GameDataCache gameData, IRoomFilter? filter,
        SimulationSource? simulation, LogService? log,
        Func<ExpEstimatorSessionViewModel?> sketch, Func<IReadOnlyList<RoomKey>, string, string?> showOnMap,
        AreaRankingStore rankings, Func<string?> character)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(loops);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(showOnMap);
        ArgumentNullException.ThrowIfNull(rankings);
        ArgumentNullException.ThrowIfNull(character);
        _resolver = resolver;
        _loops = loops;
        _gameData = gameData;
        _filter = filter;
        _simulation = simulation;
        _log = log;
        _sketch = sketch;
        _showOnMap = showOnMap;
        _rankings = rankings;
        _character = character;
        _owner = new SimulatorOwner(gameData.ActiveSet, character());
        RefreshRoutes();
        // The store outlives the Navigation window this lives with, so a ranking made
        // before the window was last closed is listed again, and one made before the
        // client was last closed is looked for on disk.
        ListKept(_owner);
    }

    // ----- Whose results these are ---------

    // The active game-data set changed. Under another set every room number here
    // means another room, so nothing of the set left behind stays: the result and
    // live check, a run in flight, the ranking and its picked row, and the
    // estimator's sketch, which is that set's rooms on the map and a route this
    // window offers. The new set's ranking for this character is listed if one was
    // made.
    //
    // The same name again is the set read in afresh (a re-import, the set picked
    // again, record overrides synced in): its tables may differ, so the ranking
    // listed was made on tables that are gone and is taken off, while the sketch,
    // still that set's rooms, stays. The store drops what it kept for the set by
    // itself (AreaRankingStore.NoteActiveSet), told by the app and not from here,
    // since no Simulator need be open when a set is read in again.
    //
    // The character is read with the set. A profile load changes both, the set
    // first: taken one at a time, the pairing in between (the new set with the
    // character just left) would be listed and logged though nobody is ever it.
    public void GameDataChanged(string? set)
    {
        bool readAgain = string.Equals(set, _owner.Set, StringComparison.OrdinalIgnoreCase);
        if (!readAgain && _sketch() is { HasClicks: true } sketch) sketch.Clear();
        ChangeOwner(new SimulatorOwner(set, _character()));
        if (readAgain) ShowRanking(null);
    }

    // Another character was loaded, or the loaded one closed: a result is one
    // character's stats and gear, so the ranking listed is that character's own for
    // the active game data, or none.
    public void CharacterChanged() => ChangeOwner(_owner with { Character = _character() });

    // The loaded character goes by another name (renamed, moved to another BBS, the
    // default profile given a name). It is the same character, so what is listed
    // stays; the name its results are kept under moves, as the store has moved what
    // it kept (AreaRankingStore.RenameCharacter). A ranking still running was
    // started under the old name and is cancelled, not kept under a name that is no
    // longer this character's.
    public void CharacterRenamed()
    {
        SimulatorOwner renamed = _owner with { Character = _character() };
        if (renamed == _owner) return;
        if (IsRanking)
        {
            _rankCts?.Cancel();
            RankStatus = "Ranking cancelled: the character was renamed. Rank again.";
        }
        _owner = renamed;
    }

    private void ChangeOwner(SimulatorOwner owner)
    {
        // A profile load that leaves the pairing as it was (the same character read
        // again) still stops a run and drops the result, as it always has.
        _rankCts?.Cancel();
        ClearSimulation(alsoCheck: true);
        if (owner == _owner) return;
        // Each line says only what happened, since a profile swap passes through "no
        // character" on its way to the next one.
        if (Rankings.Count > 0)
            _log?.Info("Simulator", $"the area ranking for {_owner.Label} is off the list: it is {owner.Label} now");
        _owner = owner;
        ListKept(owner);
    }

    // List what is kept for owner, the pairing now on show: the ranking held in
    // memory, or, the first time the pairing is shown with none there, the one on
    // disk from an earlier session. The file is read off the UI thread and listed
    // when it arrives, unless the pairing has changed, a ranking has started or one
    // has been listed meanwhile.
    private void ListKept(SimulatorOwner owner)
    {
        AreaRanking? own = _rankings.Get(owner);
        ShowRanking(own, kept: true);
        if (own is not null) { LogListed(owner, own); return; }
        if (_simulation?.World() is not { } world) return;
        _rankings.Load(owner, world, saved =>
        {
            if (saved is null || owner != _owner || IsRanking || Rankings.Count > 0) return;
            ShowRanking(saved, kept: true);
            LogListed(owner, saved);
        });
    }

    // After ShowRanking, which worked out how the ranking stands (_keptCheck).
    private void LogListed(SimulatorOwner owner, AreaRanking ranking) => _log?.Info("Simulator",
        $"listing the area ranking kept for {owner.Label}{(ranking.FromDisk ? ", read back from disk" : "")} ({ranking.Rows.Count} options): " +
        ranking.KeptNote(_simulation?.Level() ?? 0, DateTimeOffset.Now, _keptCheck));

    // List a ranking (null: none). No row is picked: picking one puts its tour on
    // the map as the estimator's sketch, which a switch must not do by itself.
    //
    // kept: it comes from those kept, not from a ranking that has just finished. It
    // then says when it was made and at what level, and is marked when it is not
    // known to be about the character as it is now (user, 2026-10-11): the level
    // has changed, something the simulator reads has, or the character can't be
    // read yet. It is listed as what it is, a ranking of the character as it was.
    private void ShowRanking(AreaRanking? ranking, bool kept = false)
    {
        SelectedRanking = null;
        _shown = ranking;
        _shownKept = kept && ranking is not null;
        Rankings.Clear();
        if (ranking is not null)
            foreach (AreaRank r in ranking.Rows) Rankings.Add(r);
        OnPropertyChanged(nameof(HasRankings));
        _keptCheck = default;
        RankingNotCurrent = false;
        RankStatus = ranking?.Summary ?? "";
        if (_shownKept) NoteKept(ranking!);
    }

    // Lead the status line of the kept ranking on show with how it stands now.
    private void NoteKept(AreaRanking ranking)
    {
        int levelNow = _simulation?.Level() ?? 0;
        _keptCheck = CheckKept(ranking);
        RankingNotCurrent = !ranking.IsCurrentAt(levelNow, _keptCheck);
        RankStatus = $"{ranking.KeptNote(levelNow, DateTimeOffset.Now, _keptCheck)} {ranking.Summary}";
    }

    // The character as it would be simulated now at the ranking's level, against
    // the one it was made for.
    private RankingCheck CheckKept(AreaRanking ranking) =>
        RankingCheck.Of(ranking.Character, _simulation?.CharacterAt(ranking.Level));

    // A `stat` screen or a full inventory was read. A kept ranking listed before the
    // character could be read says "not checked against the character yet"; it is
    // checked now. One already checked is left: it says how it stood when listed.
    public void CharacterRead()
    {
        if (_shownKept && _shown is { } ranking && _keptCheck.Unchecked && !IsRanking) NoteKept(ranking);
    }

    private bool _shownKept;
    private RankingCheck _keptCheck;

    // The ranking listed is not known to be about the character as it is now: the
    // table is shown muted.
    [ObservableProperty] private bool _rankingNotCurrent;

    public string RealmLabel => _gameData.ActiveRealm == RealmType.ParaMud ? "Paradigm" : "Stock";

    // ----- Route ---------

    // Saved loops by name, with the estimator's sketch first while it has a route.
    public ObservableCollection<SimRouteOption> Routes { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SimulateCommand))]
    private SimRouteOption? _selectedRoute;

    // Set while the picker is re-listed, so re-selecting the same route keeps its result.
    private bool _relisting;

    partial void OnSelectedRouteChanged(SimRouteOption? value)
    {
        if (!_relisting) ClearSimulation();
    }

    // Rebuild the picker: the loops were saved / deleted / renamed, or the estimator
    // opened or closed. The selection survives when its route is still offered.
    public void RefreshRoutes()
    {
        SimRouteOption? keep = SelectedRoute;
        var wanted = new List<SimRouteOption>();
        if (SketchOffered()) wanted.Add(_sketchOption);
        foreach (Loop loop in _loops.Loops.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            if (loop.Waypoints.Count >= 2) wanted.Add(new SimRouteOption(loop.Name, loop));
        SimRouteOption? again = keep is null ? null
            : keep.IsSketch ? (wanted.Contains(_sketchOption) ? _sketchOption : null)
            : wanted.FirstOrDefault(o => o.Loop is { } l && string.Equals(l.Name, keep.Loop!.Name, StringComparison.OrdinalIgnoreCase));
        Relist(() =>
        {
            Routes.Clear();
            foreach (SimRouteOption o in wanted) Routes.Add(o);
            SelectedRoute = again ?? wanted.FirstOrDefault();
        });
        if (again is null) ClearSimulation();
    }

    // The estimator's route was edited: a result played on the sketch is stale, and
    // the sketch comes into (or drops out of) the picker as it gains or loses a route.
    public void SketchChanged()
    {
        if (SelectedRoute?.IsSketch == true) ClearSimulation();
        bool offered = SketchOffered(), listed = Routes.Contains(_sketchOption);
        if (offered && !listed)
        {
            // With nothing picked, the new sketch is the route; any result left is stale.
            bool pick = SelectedRoute is null;
            Relist(() =>
            {
                Routes.Insert(0, _sketchOption);
                if (pick) SelectedRoute = _sketchOption;
            });
            if (pick) ClearSimulation();
        }
        else if (!offered && listed)
        {
            bool wasSelected = SelectedRoute?.IsSketch == true;
            Relist(() =>
            {
                Routes.Remove(_sketchOption);
                if (wasSelected) SelectedRoute = Routes.FirstOrDefault();
            });
        }
    }

    // The sketch was renamed: its picker entry follows.
    public void SketchRenamed() => SketchOffered();

    // The sketch is offered while the estimator holds a route, under its name.
    private bool SketchOffered()
    {
        if (_sketch() is not { CanSave: true } sketch) return false;
        _sketchOption.Label = $"Estimator sketch — {sketch.ProposedName}";
        return true;
    }

    private void Relist(Action change)
    {
        _relisting = true;
        try { change(); }
        finally { _relisting = false; }
        SimulateCommand.NotifyCanExecuteChanged();
    }

    private (IReadOnlyList<LoopWaypoint> Waypoints, string Name)? ChosenRoute()
    {
        if (SelectedRoute is not { } route) return null;
        if (route.Loop is { } loop) return (loop.Waypoints, loop.Name);
        return _sketch() is { CanSave: true } s ? (s.SimWaypoints(), s.ProposedName) : null;
    }

    // ----- Settings ---------

    // 0 = the character's own pace (WalkSeconds).
    [ObservableProperty] private double _simSecondsPerStep;
    [ObservableProperty] private int _simLagMs = 100;
    [ObservableProperty] private double _simHours = 1.0;
    [ObservableProperty] private int _simRuns = 3;

    // A simulation setting also clears the live check, which ran under it; a route
    // change doesn't — the check reads saved loops, not the chosen route.
    partial void OnSimSecondsPerStepChanged(double value) => ClearSimulation(alsoCheck: true);
    partial void OnSimLagMsChanged(int value) => ClearSimulation(alsoCheck: true);
    partial void OnSimHoursChanged(double value) => ClearSimulation(alsoCheck: true);
    partial void OnSimRunsChanged(int value) => ClearSimulation(alsoCheck: true);

    // The walk between rooms the simulation uses: the user's figure when set, else
    // the character's own pace — on Paradigm the server's move timer from gear
    // quickness and encumbrance plus SimLagMs of lag (GAME_MECHANICS "Per-hop
    // movement speed"); on Stock Auto-Lair's wall-clock pace by encumbrance, lag
    // already in it. Combat is simulated separately, so this is the bare walk,
    // unlike the estimate's Seconds per room.
    public double SimWalkSeconds => SimSecondsPerStep > 0
        ? SimSecondsPerStep
        : _simulation?.WalkSeconds(Math.Max(0, SimLagMs) / 1000.0)
          ?? (_gameData.ActiveRealm == RealmType.ParaMud ? 1.1 : 0.7);

    // ----- Simulation ---------

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SimulateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckAgainstPlayCommand))]
    [NotifyCanExecuteChangedFor(nameof(RankAreasCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelSimulationCommand))]
    private bool _isSimulating;
    [ObservableProperty] private string _simStatus = "";
    [ObservableProperty] private LoopSimSummary? _simResult;
    [ObservableProperty] private string _simHeadline = "";
    public ObservableCollection<SimStat> SimStats { get; } = new();
    public bool HasSimResult => SimResult is not null;

    partial void OnSimResultChanged(LoopSimSummary? value) => OnPropertyChanged(nameof(HasSimResult));

    // Play the live character around the chosen route for SimRuns seeded runs of
    // SimHours each, off the UI thread (a run is a few hundred milliseconds of CPU per
    // hour). A run cancelled by a route / setting change or by closing the Navigation
    // window is dropped, so a late result never lands on a route it wasn't played on.
    [RelayCommand(CanExecute = nameof(CanRunSimulation))]
    private async Task SimulateAsync()
    {
        if (ChosenRoute() is not { } route) return;
        using var cancel = new CancellationTokenSource();
        _simCancel = cancel;
        try
        {
            if (_simulation?.Build(null) is not { } setup)
            {
                SimStatus = "No character yet — log in and type stat so the client knows your level and pools.";
                return;
            }
            IReadOnlyList<SimRoom> lap = _resolver.ResolveSimLap(route.Waypoints, _filter);
            if (lap.Count == 0)
            {
                SimStatus = "The route has no walkable lap — fix the loop first.";
                return;
            }

            (SimCharacter character, SimWorld world) = SimFreeze.For(setup.Character, setup.World, lap);
            double step = Math.Max(0.1, SimWalkSeconds), hours = Math.Clamp(SimHours, 0.1, 24);
            int runs = Math.Clamp(SimRuns, 1, 20);
            IsSimulating = true;
            SimStatus = $"Simulating {route.Name}, {runs} × {hours:0.#} h…";
            CancellationToken token = cancel.Token;
            LoopSimSummary result = await Task.Run(() =>
                LoopSimulator.RunMany(character, lap, world, step, hours, runs, token), token);
            if (token.IsCancellationRequested) return;
            _simWalkUsed = step;
            _simRouteName = route.Name;
            SimResult = result;
            SimHeadline = Headline(result);
            Fill(SimStats, result, step);
            SimStatus = $"{route.Name} · {lap.Count} rooms · L{character.Level}" +
                        (character.Backstab is not null ? " · opens with a sneak backstab" : "");
            _log?.Info("Simulator",
                $"simulated '{route.Name}' ({lap.Count} rooms, {runs}×{hours:0.#}h, {step:0.##}s/step, L{character.Level}" +
                $"{(character.Backstab is not null ? ", backstab opener" : "")}): " +
                $"{result.ExpPerHour:N0} exp/hr ({result.MinExpPerHour:N0}–{result.MaxExpPerHour:N0}, " +
                $"bosses +{result.BossExpPerHour:N0}), {result.KillsPerHour:0} kills/hr, " +
                $"{result.DamageTakenPerHour:N0} dmg taken/hr, {result.FleesPerHour:0.#} flees/hr, " +
                $"{result.Deaths} death(s), {result.HangUps} hang-up(s), low HP {result.LowestHpPercent}%");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Cancelled on purpose (route or setting changed, window closed) — not an error.
        }
        catch (Exception ex)
        {
            // A simulation fault must never take the client down with it.
            _log?.Warn("Simulator", $"simulation of '{route.Name}' failed: {ex.GetType().Name}: {ex.Message}");
            SimStatus = "Simulation failed — see the program log.";
        }
        finally
        {
            if (ReferenceEquals(_simCancel, cancel)) _simCancel = null;
            IsSimulating = false;
        }
    }

    private bool CanRunSimulation() => _simulation is not null && !IsSimulating && SelectedRoute is not null;

    // Stop a simulation or live check still running; its result is dropped. The
    // Cancel button, and the Navigation window when it closes.
    [RelayCommand(CanExecute = nameof(IsSimulating))]
    public void CancelSimulation()
    {
        _simCancel?.Cancel();
        _checkCancel?.Cancel();
    }

    // Drop the result (and stop a run still going) — the route, a simulation
    // setting, the character or the game-data set changed under it. alsoCheck also
    // drops the live check, which reads saved loops rather than the chosen route.
    public void ClearSimulation(bool alsoCheck = false)
    {
        _simCancel?.Cancel();
        SimResult = null;
        SimHeadline = "";
        SimStats.Clear();
        SimStatus = "";
        if (alsoCheck)
        {
            _checkCancel?.Cancel();
            CheckRows.Clear();
            CheckStatus = "";
            CheckNote = "";
            OnPropertyChanged(nameof(HasCheckRows));
        }
        SimulateCommand.NotifyCanExecuteChanged();
    }

    // ----- Check against my play ---------

    // Loops need this many live hours at one level before they're checked — a
    // shorter sample swings too far on luck to judge the simulator by.
    private const double CheckMinHours = 1.0;

    [ObservableProperty] private string _checkStatus = "";
    [ObservableProperty] private string _checkNote = "";
    public ObservableCollection<SimLiveCheckRow> CheckRows { get; } = new();
    public bool HasCheckRows => CheckRows.Count > 0;

    // Set every loop this character has played (per level, from the program logs)
    // against the simulator at that level, so the user can see how far to trust it.
    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task CheckAgainstPlayAsync()
    {
        if (_simulation is null) return;
        if (string.IsNullOrWhiteSpace(_simulation.Character()))
        {
            CheckStatus = "No character yet — log in and type stat first.";
            return;
        }
        string character = _simulation.Character()!;
        using var cancel = new CancellationTokenSource();
        _checkCancel = cancel;
        CancellationToken token = cancel.Token;
        IsSimulating = true;
        CheckRows.Clear();
        OnPropertyChanged(nameof(HasCheckRows));
        CheckNote = "";
        CheckStatus = "Reading your program logs…";
        try
        {
            string dir = _simulation.LogsDir;
            IReadOnlyList<LiveLoopRecord> records = await Task.Run(() => LiveLoopSessions.Pool(
                LiveLoopSessions.ReadFolder(dir, msg => _log?.Warn("Simulator", $"live check: {msg}")),
                character, CheckMinHours), token);
            if (token.IsCancellationRequested) return;
            if (records.Count == 0)
            {
                // Program logs exist only while Auto-collect logs is on (off by default)
                // and are pruned after DebugLogWriter.DefaultRetentionDays.
                CheckStatus = $"No loop played for {CheckMinHours:0} h or more at one level in your program logs " +
                              $"(the last {DebugLogWriter.DefaultRetentionDays} days). They're only written while " +
                              "Program Log (F4) → Auto-collect logs is on — turn it on and play your loops.";
                _log?.Info("Simulator", $"live check for {character}: no loop with {CheckMinHours:0} h at one level in {dir}");
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
                    : new SimLiveCheckRow(j.Live, LoopSimulator.RunMany(j.Character!, j.Lap!, j.World!, step, hours, runs, token)))
                .ToList(), token);
            if (token.IsCancellationRequested) return;

            foreach (SimLiveCheckRow row in rows) CheckRows.Add(row);
            OnPropertyChanged(nameof(HasCheckRows));
            // Another level is simulated with today's gear, stats and spells, so a
            // session from before an upgrade reads high for reasons the simulator
            // can't see.
            int now = _simulation.Level();
            if (rows.Any(r => r.Live.Level != now))
                CheckNote = $"Other levels are simulated with today's gear, stats and spells (you're L{now} now) — older sessions read high.";
            var diffs = rows.Where(r => r.DiffPercent is not null).Select(r => r.DiffPercent!.Value).ToList();
            CheckStatus = diffs.Count == 0 ? "" :
                $"{diffs.Count(d => Math.Abs(d) <= 10)} of {diffs.Count} within 10% · average {diffs.Average():+0.0;-0.0}%";
            _log?.Info("Simulator", $"live check for {character} ({runs}×{hours:0.#}h, {step:0.##}s/step): " +
                string.Join(" | ", rows.Select(r => r.Label)));
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Cancelled on purpose (setting changed, window closed) — not an error.
        }
        catch (Exception ex)
        {
            CheckRows.Clear();
            OnPropertyChanged(nameof(HasCheckRows));
            CheckStatus = $"The check failed: {ex.Message}";
            _log?.Warn("Simulator", $"live check for {character} failed: {ex}");
        }
        finally
        {
            if (ReferenceEquals(_checkCancel, cancel)) _checkCancel = null;
            IsSimulating = false;
        }
    }

    private bool CanRunCheck() => _simulation is not null && !IsSimulating;

    // ----- Area rankings ---------

    // 0 = the character's current level.
    [ObservableProperty] private int _rankLevel;
    [ObservableProperty] private string _rankStatus = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveRankingAsLoopCommand))]
    private AreaRank? _selectedRanking;
    [ObservableProperty] private string _rankHeadline = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelRankingCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveRankingAsLoopCommand))]
    private bool _isRanking;
    public ObservableCollection<AreaRank> Rankings { get; } = new();
    public ObservableCollection<SimStat> RankStats { get; } = new();
    public bool HasRankings => Rankings.Count > 0;
    public bool HasRankDetail => RankStats.Count > 0;
    // The ranking listed, with the settings it ran with — a picked row's detail and a
    // loop saved from it quote these, not settings the user has changed since.
    private AreaRanking? _shown;

    // Every hunting area's lair tour (AreaTours, grouped by the Monsters' Region /
    // Area labels) played by the character at RankLevel, safe areas first by
    // exp/hr. Tours are mapped here on the UI thread — the room graph is rebuilt on
    // it (a set switch) and game data is read there — so a yield that lets input and
    // rendering through comes before each piece of that work (the reach search, each
    // area grouping, each tour search, each lap resolve, area and saved loop alike);
    // then the areas simulate in parallel on workers. CancelRanking (the Cancel
    // button, the window's teardown, a room-graph reload) or a change of game data
    // or character stops it at the next yield or between simulations.
    [RelayCommand(CanExecute = nameof(CanRunCheck))]
    private async Task RankAreasAsync()
    {
        if (_simulation is null || _filter is null) return;
        int ownLevel = _simulation.Level();
        int level = RankLevel > 0 ? RankLevel : ownLevel;
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
        // Whose ranking this is, for as long as it runs: a switch of game data or
        // character cancels it, and by the time it finds that out the list on show is
        // another pairing's.
        SimulatorOwner owner = _owner;
        // What it is made in and for, taken as it starts: a ranking kept between
        // sessions is listed again by these.
        RankingWorld? madeIn = _simulation.World();
        RankingCharacter? madeFor = _simulation.CharacterAt(level);
        using var cts = new CancellationTokenSource();
        _rankCts = cts;
        CancellationToken cancel = cts.Token;
        IsSimulating = true;
        IsRanking = true;
        // Ranking again replaces the pairing's ranking from here on, as the list does.
        _rankings.Forget(owner);
        ShowRanking(null);
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
            // The read may have finished just before a cancel that came with a switch.
            cancel.ThrowIfCancellationRequested();

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
            // The last simulation may have finished just before a cancel: after a
            // switch these rows are not the pairing's now on show, and after the set
            // was read in again they were made on tables that are gone.
            cancel.ThrowIfCancellationRequested();

            int safe = ranked.Count(r => r.Safe);
            string skipped = (skippedAreas > 0 ? $"; {skippedAreas} area(s) skipped (one lair or no walkable lap)" : "")
                + (skippedLoops > 0 ? $"; {skippedLoops} of your loops skipped (no walkable lap)" : "");
            string summary = $"L{level} from {here.Map}/{here.Room}: {safe} safe option(s) (areas and your loops), best first; {ranked.Count - safe} where you died listed last"
                + (gatedAreas > 0 ? $"; {gatedAreas} area(s) you can't reach at L{level} left out" : "") + skipped
                + ". Pick one to see its route and details, show it on the map, and save it as a loop to run.";
            ListFinishedRanking(owner, new AreaRanking(
                ranked, level, step, hours, runs, here, RealmLabel, summary, DateTimeOffset.Now, ownLevel, madeIn, madeFor));
            _log?.Info("Simulator", $"ranked {ranked.Count} options at L{level} for {owner.Label} ({gatedAreas} areas unreachable, " +
                $"{skippedAreas} areas and {skippedLoops} loops skipped): " +
                string.Join(" | ", ranked.Take(10).Select(r => r.Label)));
        }
        catch (Exception ex) when (cancel.IsCancellationRequested && IsCancellation(ex))
        {
            RankingStopped(owner, "Ranking cancelled.");
            _log?.Info("Simulator", $"area ranking at L{level} for {owner.Label} cancelled");
        }
        catch (Exception ex)
        {
            // A ranking fault must never take the client down with it.
            _log?.Warn("Simulator", $"area ranking at L{level} failed: {ex.GetType().Name}: {ex.Message}");
            RankingStopped(owner, "Ranking failed — see the program log.");
        }
        finally
        {
            _rankCts = null;
            IsRanking = false;
            IsSimulating = false;
        }
    }

    // A ranking finished for owner, the pairing it was started for: it is kept as
    // that pairing's, and listed while that pairing is still the one on show. A
    // switch cancels a ranking that is running, so one that finishes does so for
    // the pairing on show; keying on its own owner is what makes that true of a
    // ranking kept even should a path ever finish one late.
    internal void ListFinishedRanking(SimulatorOwner owner, AreaRanking ranking)
    {
        if (_rankings.Keep(owner, ranking) is { } dropped)
            _log?.Info("Simulator", $"the area ranking for {dropped.Label} was let go: only the last {AreaRankingStore.MaxKept} made or shown are kept");
        if (owner == _owner) ShowRanking(ranking);
    }

    // A ranking for owner was cancelled or failed. Its own list is left empty with
    // the reason; after a switch the list on show is another pairing's and is left
    // as it is.
    internal void RankingStopped(SimulatorOwner owner, string status)
    {
        if (owner != _owner) return;
        ShowRanking(null);
        RankStatus = status;
    }

    // Stops a running RankAreas: the Cancel button, the Navigation window's teardown,
    // a change of game data or character, or a room-graph reload.
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

    // A picked row shows its full result here and its route on the map, where the
    // estimator's sketch takes it — and the picker follows, so Simulate replays it
    // (at the character's current level and today's gates, not the ranked ones).
    partial void OnSelectedRankingChanged(AreaRank? value)
    {
        OnPropertyChanged(nameof(RankIsArea));
        if (value is null) { ClearRankDetail(); return; }
        RankHeadline = $"{value.Area} at L{value.Level} — {Headline(value.Result)}";
        double step = _shown?.Step ?? SimWalkSeconds;
        Fill(RankStats, value.Result, step);
        if (_shown is { } run)
            RankStats.Insert(0, new SimStat("Tested", $"L{value.Level} · {run.Runs} × {run.Hours:0.#} h · " +
                                                     $"{run.Step:0.00} s a room · {run.Realm} · from {run.From.Map}/{run.From.Room}"));
        RankStats.Insert(0, new SimStat("Route", RouteLine(value)));
        OnPropertyChanged(nameof(HasRankDetail));
        if (_showOnMap(value.Tour, value.Area) is { } why)
        {
            RankStatus = why;
            return;
        }
        RefreshRoutes();
        if (Routes.Contains(_sketchOption)) SelectedRoute = _sketchOption;
    }

    // How many of a route's rooms its line spells out. An area's tour can be hundreds
    // of lairs: all of them would crowd the ranking table out of the window, and in a
    // saved loop's notes run past what @loop send will carry. The loop itself holds
    // every room.
    private const int RouteRoomsShown = 20;

    // The rooms the run walked between, in order — the waypoints a saved loop gets.
    private static string RouteLine(AreaRank r) =>
        $"{(r.IsLoop ? $"{r.Tour.Count} waypoints" : $"{r.Tour.Count} lairs")}, {r.LapRooms} rooms a lap: " +
        string.Join(" → ", r.Tour.Take(RouteRoomsShown).Select(k => $"{k.Map}/{k.Room}")) +
        (r.Tour.Count > RouteRoomsShown ? $" → … ({r.Tour.Count - RouteRoomsShown} more)" : "");

    // Save the picked area's tour as a loop the user can run: the same rooms, in the
    // same order, the simulation walked between (the runner re-routes the legs with
    // the gates as they are when it starts), with how it was tested in its notes.
    [RelayCommand(CanExecute = nameof(CanSaveRankingAsLoop))]
    private void SaveRankingAsLoop()
    {
        if (SelectedRanking is not { IsLoop: false } rank) return;
        if (_loops.SetName is null)
        {
            RankStatus = "No game-data set is active, so there's nowhere to save a loop.";
            return;
        }
        // A loop another client saved a moment ago counts for both checks below, so
        // the folder is read first.
        _loops.TakeInOutsideChanges();
        string wanted = $"{rank.Area.Replace('/', '-')} (L{rank.Level} sim)";
        var loop = new Loop(wanted, rank.Tour) { Notes = LoopNotes(rank, _shown) };
        // The same tour saved again would be one loop under two names. Loops are told
        // apart as @loop send tells them (LoopSignature): this one is its rooms in
        // order with nothing set on a room and both loop settings off, which is how
        // the simulation played it.
        string signature = LoopSignature.Of(loop);
        if (_loops.Loops.FirstOrDefault(l => LoopSignature.Of(l) == signature) is { } same)
        {
            _log?.Info("Simulator", $"ranked tour '{rank.Area}' (L{rank.Level}) is already saved as loop '{same.Name}' " +
                                    $"[{LoopSignature.Short(signature)}]; nothing saved");
            PickRoute(same.Name);
            RankStatus = $"Already saved as loop \"{same.Name}\" — nothing saved. It's picked as the Route above.";
            return;
        }
        // Saving under a taken name would overwrite that loop, so a different tour
        // of the same area and level gets a number.
        if (_loops.Get(wanted) is not null) loop.Name = _loops.NumberedName(wanted);
        string name = loop.Name;
        try
        {
            _loops.Save(loop);
        }
        catch (Exception ex)
        {
            _log?.Warn("Simulator", $"saving ranked tour '{rank.Area}' as loop '{name}' failed: {ex.GetType().Name}: {ex.Message}");
            RankStatus = $"Couldn't save the loop: {ex.Message}";
            return;
        }
        // The ranking walked the tour with level gates judged at the ranked level and
        // for the character alone; a run walks it with every gate as it is then. Legs
        // shut today are named now, not left for the run to find.
        int shut = _loops.ExpandWaypoints(loop.Waypoints, _filter).UnreachableSegments.Count;
        _log?.Info("Simulator", $"saved ranked tour '{rank.Area}' (L{rank.Level}, {rank.Tour.Count} waypoints, " +
                                $"{rank.LapRooms} rooms a lap, {rank.Result.ExpPerHour:N0} exp/hr) as loop '{name}'" +
                                (shut > 0 ? $"; {shut} of its legs can't be walked as the character is now" : ""));
        PickRoute(name);
        RankStatus = shut > 0
            ? $"Saved as loop \"{name}\", but {shut} of its {loop.Waypoints.Count} legs can't be walked as you are now " +
              $"(it was ranked at L{rank.Level}) — it won't run right until they open. Its notes say how it was tested."
            : $"Saved as loop \"{name}\" — it's in the Navigation window's Loops list to run, and picked as the Route above. " +
              "Its notes say how it was tested.";
    }

    // Make the saved loop of that name the Simulator's route.
    private void PickRoute(string loopName)
    {
        RefreshRoutes();
        if (Routes.FirstOrDefault(o => o.Loop is { } l && string.Equals(l.Name, loopName, StringComparison.OrdinalIgnoreCase)) is { } saved)
            SelectedRoute = saved;
    }

    // A ★ row is already saved, so only an area row offers Save as loop.
    public bool RankIsArea => SelectedRanking is { IsLoop: false };

    private bool CanSaveRankingAsLoop() => SelectedRanking is { IsLoop: false } && !IsRanking;

    private static string LoopNotes(AreaRank r, AreaRanking? run)
    {
        LoopSimSummary s = r.Result;
        string deaths = s.Deaths + s.HangUps == 0 ? "no deaths"
            : $"died or hung up in {s.Deaths + s.HangUps} of {s.Runs.Count} runs";
        var lines = new List<string>
        {
            $"Saved from the Simulator's area ranking on {DateTime.Now:yyyy-MM-dd}.",
            $"Simulated at L{r.Level}{(run is null ? "" : $" on {run.Realm}")}: {s.ExpPerHour:N0} exp/hr " +
            $"({s.MinExpPerHour:N0} – {s.MaxExpPerHour:N0} over {s.Runs.Count} runs{(run is null ? "" : $" × {run.Hours:0.#} h")}), " +
            $"{s.KillsPerHour:0} kills/hr, lowest HP {s.LowestHpPercent}%, {deaths}.",
        };
        if (run is not null)
            lines.Add($"Walk pace {run.Step:0.00} s a room; ranked from {run.From.Map}/{run.From.Room}.");
        lines.Add($"Route: {RouteLine(r)}.");
        // The simulator fights whatever is in each room it walks and never waits at a
        // lair's door, which is this loop as saved; the figures stop holding once
        // either setting is turned on for it.
        lines.Add("Simulated fighting in every room on the way, with no wait to enter lairs: both Entire Loop Settings off, as saved.");
        lines.Add($"The simulation judged level gates at L{r.Level}; run it there, since the loop walks the gates as they are when it starts.");
        return string.Join("\n", lines);
    }

    private void ClearRankDetail()
    {
        RankHeadline = "";
        RankStats.Clear();
        OnPropertyChanged(nameof(HasRankDetail));
    }

    // ----- Readout ---------

    private static string Headline(LoopSimSummary r) =>
        $"≈ {r.ExpPerHour:N0} exp/hr  ({r.MinExpPerHour:N0} – {r.MaxExpPerHour:N0} over {r.Runs.Count} runs)";

    private static void Fill(ObservableCollection<SimStat> stats, LoopSimSummary r, double walkSeconds)
    {
        stats.Clear();
        foreach (SimStat s in Stats(r, walkSeconds)) stats.Add(s);
    }

    private static IEnumerable<SimStat> Stats(LoopSimSummary r, double walkSeconds)
    {
        yield return new SimStat("Kills", $"{r.KillsPerHour:0} an hour");
        yield return new SimStat("Lap", r.AvgLapSeconds > 0 ? $"{r.AvgLapSeconds:0} s" : "no lap finished");
        yield return new SimStat("Walking", $"{walkSeconds:0.00} s a room");
        yield return new SimStat("Time", $"attacking {r.Share(x => x.AttackingSeconds):P0} · moving {r.Share(x => x.MovingSeconds):P0} · " +
                                         $"resting {r.Share(x => x.RestingSeconds):P0} · meditating {r.Share(x => x.MeditatingSeconds):P0} · " +
                                         $"waiting {r.Share(x => x.WaitingSeconds):P0}");
        yield return new SimStat("Lowest", $"HP {r.LowestHpPercent}% · mana {r.LowestManaPercent}%");
        yield return new SimStat("Damage taken", $"{r.DamageTakenPerHour:N0} an hour");
        yield return new SimStat("Deaths", r.Deaths == 0 ? "none" : $"died in {r.Deaths} of {r.Runs.Count} runs");
        if (r.HangUps > 0) yield return new SimStat("Hang-ups", $"{r.HangUps} of {r.Runs.Count} runs");
        if (r.FleesPerHour > 0) yield return new SimStat("Flees", $"{r.FleesPerHour:0.#} an hour");
        foreach (ExpBossStat b in r.Bosses ?? Array.Empty<ExpBossStat>())
            yield return new SimStat($"Boss {b.Name}", $"+{b.ExpPerHour:N0}/hr (once per {b.RegenHours:0.#}h, not fought in the runs)");
        var casts = r.CastsPerHour();
        if (casts.Count > 0)
            yield return new SimStat("Per hour", string.Join(", ", casts.Take(8).Select(c => $"{c.Spell} {c.PerHour:0}")));
    }

    // Frozen for a bug-report capture, pre-formatted so the report is a straight print.
    public SimulatorSnapshot ToSnapshot()
    {
        static string Line(SimStat s) => $"{s.Label}: {s.Value}";
        return new SimulatorSnapshot(
            SelectedRoute?.Label, RealmLabel,
            SimResult is null ? null : SimStats.Select(Line).Prepend(SimHeadline).Prepend(SimStatus).ToList(),
            SimResult is null ? 0 : _simWalkUsed, SimHours, SimRuns,
            CheckRows.Count == 0 ? null
                : CheckRows.Select(r => r.Label).Prepend(CheckStatus).Append(CheckNote)
                    .Where(l => !string.IsNullOrEmpty(l)).ToList(),
            // The status alone still reports a ranking that was cancelled, failed or found nothing.
            Rankings.Count == 0 && string.IsNullOrEmpty(RankStatus) ? null
                : Rankings.Take(15).Select(r => r.Label).Prepend(RankStatus)
                    .Concat(RankStats.Count == 0 ? Array.Empty<string>()
                        : RankStats.Select(s => "  " + Line(s)).Prepend($"Picked: {RankHeadline}"))
                    .Where(l => !string.IsNullOrEmpty(l)).ToList(),
            SimResult is null ? null : _simRouteName,
            _owner.Set, _owner.Character, _rankings.Count,
            DescribeListedRanking(), _rankings.DiskNotes);
    }

    // Where the ranking on show came from and how it stands, for the bug report: a
    // report of figures that look wrong turns on whether they are today's.
    private string? DescribeListedRanking()
    {
        if (_shown is not { } ranking) return null;
        TimeSpan age = DateTimeOffset.Now - ranking.MadeAt;
        string made = $"made {ranking.MadeAt.ToLocalTime():yyyy-MM-dd HH:mm} " +
                      $"({(age.TotalDays >= 1 ? $"{age.TotalDays:0} day(s)" : $"{age.TotalHours:0.#} h")} ago) at L{ranking.Level}, character L{ranking.CharacterLevel} then";
        if (!_shownKept) return $"just made this session; {made}";
        string from = ranking.FromDisk ? "read back from disk" : "kept in memory this session";
        string stands = _keptCheck.Unchecked ? "not checked against the character yet"
            : _keptCheck.Changed is { Count: > 0 } changed ? $"marked for {string.Join(", ", changed)}"
            : "the character's stats, gear, spells, settings, game-data edits and reach are as they were";
        return $"{from}; {made}; {stands}{(RankingNotCurrent ? "; shown as not current" : "")}";
    }
}

// One route the Simulator window can play: a saved loop, or (Loop null) the
// estimator's sketch.
public sealed partial class SimRouteOption(string label, Loop? loop) : ObservableObject
{
    [ObservableProperty] private string _label = label;
    public Loop? Loop { get; } = loop;
    public bool IsSketch => Loop is null;
    public override string ToString() => Label;
}

// One labelled figure of a simulation result.
public sealed record SimStat(string Label, string Value);

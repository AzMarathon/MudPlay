using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FujinTerm.Game;
using FujinTerm.Game.Calculators;
using FujinTerm.Game.Cash;
using FujinTerm.Game.Combat;
using FujinTerm.Services;

namespace FujinTerm.ViewModels;

// Modeless Session Stats window VM. A pure projection over the three session
// trackers — CombatSessionTracker (Player Statistics), TimeAnalysisTracker
// (Time Analysis), and SessionActivityTracker (Session Statistics +
// kills/hour sparkline). It snapshots each on their Changed signal and
// exposes the figures for binding; the trackers own all the state and the
// session-reset boundary, so this VM never mutates game data.
//
// Combat Changed can fire many times a round, so refreshes are coalesced
// onto a single dispatcher tick rather than re-snapshotting per event. The
// snapshots are held as record-struct properties and the window binds their
// fields directly (with StringFormat for plain numbers / percentages);
// durations and damage ranges get formatted getters here since StringFormat
// can't express "hours past 24" or a min–max pair. A 1-second
// DispatcherTimer also re-snapshots on the wall clock so the durations and
// per-hour rates tick up live while the window is open, instead of only
// advancing when a tracker input changes.
public sealed partial class SessionStatsViewModel : ObservableObject, IDisposable
{
    // Bucket count for the kills/hour sparkline across the rolling window.
    private const int SparklineBuckets = 30;

    // How many loop steps the HP/MA History graph shows at once; the slider pans
    // this window across a longer loop.
    private const int StepViewWindow = 15;

    // Percentage points of headroom below the lowest recorded value for the HP/MA
    // graph's axis floor — so the plot spreads over the range that matters instead
    // of wasting the bottom half on values you never reach.
    private const double AxisFloorHeadroom = 30;

    // Upper bound on the banked-level scan — same cap the auto-trainer and
    // level-up announcer use, so the time-to-level count stays in lock-step.
    private const int MaxLevelScan = 60;

    private readonly CombatSessionTracker _combatTracker;
    private readonly TimeAnalysisTracker _timeTracker;
    private readonly SessionActivityTracker _activityTracker;
    private readonly HpMaHistoryTracker _hpMaTracker;
    private readonly SessionStatsLayoutStore _layoutStore;

    // Resolves the per-BBS runic word for the currency denomination labels.
    private readonly CurrencyNaming _naming;

    // Live progression + game data for the time-to-level readout: PlayerStats
    // supplies level / exp / class / race, GameDataCache supplies the exp chart
    // and active realm. Read-only here — the trackers own all session state.
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;

    // Opens the Transaction history window. Routed back to
    // MainWindowViewModel so the window follows the modeless toggle-window
    // contract (re-press closes) rather than being spawned here.
    private readonly Action _openTransactionHistory;

    // Opens the Players Seen window — routed back to MainWindowViewModel for the
    // same modeless toggle-window reason as the transaction opener above.
    private readonly Action _openPlayersSeen;

    // Drives the live wall-clock ticking of durations / rates: the
    // time-derived figures advance with real time even when no tracker input
    // fires, so the user sees the session clock move.
    private readonly DispatcherTimer _liveTick;

    private bool _refreshScheduled;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HitRangeText), nameof(CritRangeText), nameof(BackstabRangeText),
        nameof(RoundRangeText), nameof(ProcRangeText), nameof(SpellRangeText),
        nameof(HasProcs), nameof(HasSpells))]
    private CombatSessionStats _combat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(MovingText), nameof(AttackingText),
        nameof(RestingText), nameof(WaitingText), nameof(RestingHpText), nameof(RestingMaText),
        nameof(BlindedText), nameof(PoisonedText), nameof(DiseasedText), nameof(ConfusedText),
        nameof(HeldText))]
    private TimeAnalysisStats _time;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrencyCollectedText), nameof(CurrencyCollectedTip),
        nameof(CurrencyPerHourText), nameof(CurrencyStashedText), nameof(CurrencyStashedTip),
        nameof(KillsRateText), nameof(ExpRateText), nameof(TimeToLevelText))]
    private SessionActivityStats _activity;

    // Kills/hour series feeding the kills sparkline; reassigned each refresh.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KillsPeakText), nameof(KillsFloorText))]
    private IReadOnlyList<double> _killsPerHour = Array.Empty<double>();

    // Experience/hour series feeding the exp sparkline; reassigned each refresh.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpPeakText), nameof(ExpFloorText))]
    private IReadOnlyList<double> _experiencePerHour = Array.Empty<double>();

    // Per-loop-step HP/MA min/max (percent of max), indexed by step position;
    // reassigned each refresh. HP and MA each draw a per-step high-low bar on a
    // shared 0–100% axis. HasManaHistory is false for a no-mana class, hiding the
    // mana bars + legend. The step count (HpLow.Count) drives the slider bounds.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepViewMax), nameof(IsStepSliderVisible), nameof(StepRangeText),
        nameof(AxisMin), nameof(AxisMinText), nameof(CenterStepText), nameof(WindowOffset), nameof(CursorIndex))]
    private IReadOnlyList<double> _hpLow = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _hpHigh = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _hpAvg = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _maLow = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _maHigh = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _maAvg = Array.Empty<double>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AxisMin), nameof(AxisMinText))]
    private bool _hasManaHistory;

    // Lowest HP / MA percent seen anywhere on the loop, shown in each series'
    // legend label and feeding the adaptive axis floor. 100 until the first on-loop
    // sample lands.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HpLegendText), nameof(AxisMin), nameof(AxisMinText))]
    private double _lowestHpPercent = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaLegendText), nameof(AxisMin), nameof(AxisMinText))]
    private double _lowestMaPercent = 100;

    // The loop step the slider is anchored on (0-based). The slider spans every
    // step (0 … N-1), so the cursor reaches any step; the visible window follows it
    // (WindowOffset), centring where it can and clamping at the head / tail.
    // Clamped to StepViewMax each refresh.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepRangeText), nameof(CenterStepText),
        nameof(WindowOffset), nameof(CursorIndex))]
    private double _focusStep;

    // True while the user is holding / dragging the pan slider — drives the graph's
    // vertical scrub cursor so they can see exactly which step they're centred on.
    [ObservableProperty] private bool _isScrubbing;

    // Per-panel visibility toggles — each of the five panels (the two rate
    // graphs and the three stat sections) can be shown or hidden via the
    // window's context menu. Each change is written through to the
    // per-character layout via PersistLayout; _loadingLayout gates the write
    // so applying a saved layout doesn't echo straight back to disk.
    [ObservableProperty]
    private bool _isKillsGraphVisible = true;

    [ObservableProperty]
    private bool _isExpGraphVisible = true;

    [ObservableProperty]
    private bool _isHpMaGraphVisible = true;

    [ObservableProperty]
    private bool _isPlayerStatsVisible = true;

    [ObservableProperty]
    private bool _isTimeAnalysisVisible = true;

    [ObservableProperty]
    private bool _isSessionStatsVisible = true;

    // Panel ids in their resolved top-to-bottom order — the window reads this
    // on open to reorder its panel host, and pushes drag-reorders back via
    // SaveOrder.
    private List<string> _panelOrder = new();

    // Suppresses PersistLayout while LoadLayout seeds the visibility toggles
    // from a saved layout, so hydration doesn't immediately write the same
    // values back.
    private bool _loadingLayout;

    public SessionStatsViewModel(
        CombatSessionTracker combat,
        TimeAnalysisTracker time,
        SessionActivityTracker activity,
        HpMaHistoryTracker hpMaHistory,
        SessionStatsLayoutStore layout,
        PlayerStats stats,
        GameDataCache gameData,
        CurrencyNaming naming,
        Action openTransactionHistory,
        Action openPlayersSeen)
    {
        ArgumentNullException.ThrowIfNull(combat);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(hpMaHistory);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(naming);
        ArgumentNullException.ThrowIfNull(openTransactionHistory);
        ArgumentNullException.ThrowIfNull(openPlayersSeen);
        _combatTracker = combat;
        _timeTracker = time;
        _activityTracker = activity;
        _hpMaTracker = hpMaHistory;
        _layoutStore = layout;
        _stats = stats;
        _gameData = gameData;
        _naming = naming;
        _openTransactionHistory = openTransactionHistory;
        _openPlayersSeen = openPlayersSeen;

        LoadLayout();

        _combatTracker.Changed += OnChanged;
        _timeTracker.Changed += OnChanged;
        _activityTracker.Changed += OnChanged;
        _hpMaTracker.Changed += OnChanged;

        _liveTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _liveTick.Tick += (_, _) => Refresh();
        _liveTick.Start();

        Refresh();
    }

    // ----- Panel layout (order + visibility) ---------------------------

    // The resolved panel order the window applies on open.
    public IReadOnlyList<string> PanelOrder => _panelOrder;

    // Hydrate the order + visibility toggles from the per-character layout
    // store. Guarded so the toggle assignments don't write straight back.
    private void LoadLayout()
    {
        _loadingLayout = true;
        IReadOnlyList<(string Id, bool Visible)> resolved = _layoutStore.Resolve();
        _panelOrder = resolved.Select(p => p.Id).ToList();
        foreach ((string id, bool visible) in resolved)
            SetVisible(id, visible);
        _loadingLayout = false;
    }

    // Push a new panel order (from a drag-reorder) through to the store,
    // keeping the current hidden set.
    public void SaveOrder(IEnumerable<string> ids)
    {
        _panelOrder = ids.ToList();
        PersistLayout();
    }

    // Snapshot the live order + hidden set into the per-character store.
    private void PersistLayout()
    {
        if (_loadingLayout) return;
        List<string> hidden = new();
        if (!IsKillsGraphVisible)   hidden.Add("KillsGraph");
        if (!IsExpGraphVisible)     hidden.Add("ExpGraph");
        if (!IsHpMaGraphVisible)    hidden.Add("HpMaGraph");
        if (!IsPlayerStatsVisible)  hidden.Add("PlayerStatistics");
        if (!IsTimeAnalysisVisible) hidden.Add("TimeAnalysis");
        if (!IsSessionStatsVisible) hidden.Add("SessionStatistics");
        _layoutStore.Update(_panelOrder, hidden);
    }

    private void SetVisible(string id, bool visible)
    {
        switch (id)
        {
            case "KillsGraph":        IsKillsGraphVisible = visible; break;
            case "ExpGraph":          IsExpGraphVisible = visible; break;
            case "HpMaGraph":         IsHpMaGraphVisible = visible; break;
            case "PlayerStatistics":  IsPlayerStatsVisible = visible; break;
            case "TimeAnalysis":      IsTimeAnalysisVisible = visible; break;
            case "SessionStatistics": IsSessionStatsVisible = visible; break;
        }
    }

    partial void OnIsKillsGraphVisibleChanged(bool value) => PersistLayout();
    partial void OnIsExpGraphVisibleChanged(bool value) => PersistLayout();
    partial void OnIsHpMaGraphVisibleChanged(bool value) => PersistLayout();
    partial void OnIsPlayerStatsVisibleChanged(bool value) => PersistLayout();
    partial void OnIsTimeAnalysisVisibleChanged(bool value) => PersistLayout();
    partial void OnIsSessionStatsVisibleChanged(bool value) => PersistLayout();

    // ----- Time Analysis (durations) -----------------------------------

    public string DurationText  => Fmt(Time.TimeOn);
    public string MovingText    => Fmt(Time.Moving);
    public string AttackingText => Fmt(Time.Attacking);
    public string RestingText   => Fmt(Time.Resting);
    public string WaitingText   => Fmt(Time.Waiting);
    public string RestingHpText => Fmt(Time.RestingHp);
    public string RestingMaText => Fmt(Time.RestingMa);
    public string BlindedText   => Fmt(Time.Blinded);
    public string PoisonedText  => Fmt(Time.Poisoned);
    public string DiseasedText  => Fmt(Time.Diseased);
    public string ConfusedText  => Fmt(Time.Confused);
    public string HeldText      => Fmt(Time.Held);

    // ----- Player Statistics (damage ranges) ---------------------------

    public string HitRangeText      => Range(Combat.HitMinDamage, Combat.HitMaxDamage);
    public string CritRangeText     => Range(Combat.CritMinDamage, Combat.CritMaxDamage);
    public string BackstabRangeText => Range(Combat.BackstabMinDamage, Combat.BackstabMaxDamage);
    public string RoundRangeText    => Range(Combat.RoundMinDamage, Combat.RoundMaxDamage);
    public string ProcRangeText     => Range(Combat.ProcMinDamage, Combat.ProcMaxDamage);
    public string SpellRangeText    => Range(Combat.SpellMinDamage, Combat.SpellMaxDamage);

    // Drives the proc row's visibility — hidden until a weapon procs.
    public bool HasProcs => Combat.ProcHits > 0;

    // Drives the spell row's visibility — hidden until a configured attack
    // spell lands.
    public bool HasSpells => Combat.SpellHits > 0;

    // ----- Session Statistics (currency) -------------------------------
    // The copper totals/rate read as coin denominations rather than raw
    // comma-grouped copper, flipping up to the largest denomination that fits
    // (1000 copper/hr -> "10 gold/hr"). The exact itemised wealth line rides
    // along as a tooltip for the total/stashed figures.

    public string CurrencyCollectedText => CurrencyFormat.Denominate(Activity.CurrencyCollected, _naming.RunicName);
    public string CurrencyCollectedTip  => CurrencyFormat.Full(Activity.CurrencyCollected, _naming.RunicName);
    public string CurrencyPerHourText   => CurrencyFormat.Denominate(Activity.CurrencyPerHour, _naming.RunicName);
    public string CurrencyStashedText   => CurrencyFormat.Denominate(Activity.CurrencyStashed, _naming.RunicName);
    public string CurrencyStashedTip    => CurrencyFormat.Full(Activity.CurrencyStashed, _naming.RunicName);

    // ----- Rate-graph scales -------------------------------------------
    // The sparklines normalise each series to its own min–max, so the plot is
    // shapely but unitless on its own. These label the y-axis — peak at the top
    // edge, floor at the bottom — so a glance reads off the value the line maps
    // to. Exp rates run large, so their labels abbreviate (k / M).

    public string KillsPeakText  => RateLabel(Peak(KillsPerHour), compact: false);
    public string KillsFloorText => RateLabel(Floor(KillsPerHour), compact: false);
    public string ExpPeakText    => RateLabel(Peak(ExperiencePerHour), compact: true);
    public string ExpFloorText   => RateLabel(Floor(ExperiencePerHour), compact: true);

    // HP/MA-history legend labels — each names the series and its worst dip across
    // the loop's steps ("HP (low 28%)"), so the scariest moment reads without
    // eyeballing the bars. 100% until the first on-loop sample lands.
    public string HpLegendText => $"HP (low {LowestHpPercent:F0}%)";
    public string MaLegendText => $"MA (low {LowestMaPercent:F0}%)";

    // HP/MA graph slider bounds: the window pans from step 1 to the tail, so the
    // slider's max is the count past a full window; it's only shown (and only
    // pannable) once the loop is longer than one window.
    // Slider spans every step (its max is the last step index), so the cursor can
    // anchor anywhere; only shown once the loop is longer than one window (a loop
    // that fits needs no panning).
    public double StepViewMax => Math.Max(0, HpLow.Count - 1);
    public bool IsStepSliderVisible => HpLow.Count > StepViewWindow;

    // The anchored step (0-based, clamped) the cursor + readout mark.
    public int CursorIndex
    {
        get
        {
            int n = HpLow.Count;
            return n == 0 ? 0 : Math.Clamp((int)Math.Round(FocusStep), 0, n - 1);
        }
    }

    // First visible step: the window follows the cursor — centred on it where it
    // can be, clamped so the head and tail steps stay reachable.
    public int WindowOffset
    {
        get
        {
            int n = HpLow.Count;
            if (n == 0) return 0;
            int window = Math.Min(StepViewWindow, n);
            return Math.Clamp(CursorIndex - window / 2, 0, n - window);
        }
    }

    // Adaptive vertical floor for the graph (top is fixed at 100%): 30 points below
    // the lowest value seen across the plotted series, clamped at 0. 0 before any
    // data lands. AxisMinText labels the bottom scale tick.
    public double AxisMin
    {
        get
        {
            if (HpLow.Count == 0) return 0;
            double lowest = HasManaHistory ? Math.Min(LowestHpPercent, LowestMaPercent) : LowestHpPercent;
            return Math.Max(0, lowest - AxisFloorHeadroom);
        }
    }

    public string AxisMinText => $"{AxisMin:F0}%";

    // X-axis caption: which loop steps the window currently shows, of the total —
    // labels the step axis and tracks the slider. "steps 1–N" when the whole loop
    // fits.
    public string StepRangeText
    {
        get
        {
            int n = HpLow.Count;
            if (n == 0) return "no loop steps recorded yet";
            int first = WindowOffset + 1;
            int last = Math.Min(n, WindowOffset + Math.Min(StepViewWindow, n));
            return first <= 1 && last >= n ? $"steps 1–{n}" : $"steps {first}–{last} of {n}";
        }
    }

    // The anchored step (1-based) for the in-graph readout + the scrub cursor.
    public string CenterStepText => HpLow.Count > 0 ? $"step {CursorIndex + 1}" : string.Empty;

    // Current headline rate, printed in each graph header so the number is
    // legible without eyeballing the curve — it equals the curve's right-most
    // point by construction (both are total ÷ time online).
    public string KillsRateText => $"{Activity.KillsPerHour:F1}/hr";
    public string ExpRateText   => $"{RateLabel(Activity.ExperiencePerHour, compact: true)}/hr";

    // ----- Time to level -----------------------------------------------
    // Countdown to the next level the running exp hasn't reached yet, at the
    // session exp/hour rate — honouring MajorMUD level banking, where exp keeps
    // accruing past the trained level. "N level(s) gained" is the count of
    // banked-but-untrained levels; the ETA then targets the first level above
    // that ceiling. So at trained level 5 with enough banked to train to 6 this
    // reads "1 level gained · HH:MM:SS until level 7". Rate comes from the same
    // whole-session average ExpRateText prints, so the two stay consistent.
    public string TimeToLevelText
    {
        get
        {
            int level = _stats.Level;
            if (level <= 0) return "level unknown — type stat";

            // Chart resolves from the class + race exp tables. Mirrors the
            // caller-resolves-chart convention the trainer / level-projection
            // paths use — the pure exp calculator never reads game data.
            int chart = ExperienceTableCalculator.CalcExpChart(
                GetInt(_gameData.FindRowByName("Classes", _stats.Class), "ExpTable"),
                GetInt(_gameData.FindRowByName("Races", _stats.Race), "ExpTable"));
            if (chart <= 0) return "exp chart unavailable — import game data";

            RealmType realm = _gameData.ActiveRealm;
            long exp = _stats.Exp;

            int banked = TrainBudgetCalculator.BankableLevels(exp, level, chart, realm, MaxLevelScan);
            int target = level + banked + 1;
            string bankedPart = $"{banked} level{(banked == 1 ? "" : "s")} gained";

            double rate = Activity.ExperiencePerHour;
            TimeSpan? eta = ExperienceTableCalculator.CalcTimeToLevel(
                ExperienceTableCalculator.CalcExpNeeded(target, chart, realm), exp, (long)rate);
            string etaPart = eta is null
                ? "rate unknown"
                : eta.Value <= TimeSpan.Zero ? "ready to level" : $"{Fmt(eta.Value)} until level {target}";

            return $"{bankedPart} · {etaPart}";
        }
    }

    private static double Peak(IReadOnlyList<double> s)  => s.Count > 0 ? s.Max() : 0;
    private static double Floor(IReadOnlyList<double> s) => s.Count > 0 ? s.Min() : 0;

    private static string RateLabel(double v, bool compact)
    {
        if (v <= 0) return "0";
        return compact ? RateText.Compact(v) : v.ToString("F0");
    }

    // ----- Commands ----------------------------------------------------

    // Top-bar "Reset session" — wipes every session tracker's counters and
    // restarts their clocks. The transaction ledger is deliberately left intact:
    // it is user-owned and cleared only from its own window's Clear button. The
    // resets raise Changed, which refreshes the bound figures.
    [RelayCommand]
    private void Reset()
    {
        _combatTracker.Reset();
        _timeTracker.Reset();
        _activityTracker.Reset();
        _hpMaTracker.Reset();
    }

    // Per-section resets, one per collapsible. Each wipes only its own section's
    // figures so a user can re-anchor one dimension without losing the rest.

    // "Player Statistics" reset — the combat tally (hit / miss / crit / etc.).
    [RelayCommand]
    private void ResetPlayerStats() => _combatTracker.Reset();

    // "Time Analysis" reset — the activity-time breakdown. Because the per-hour
    // rates are measured over this same session time, restarting it also restarts
    // every rate (kills/hr, exp/hr, currency/hr, and both sparklines) via
    // ResetRates — while the Session Statistics totals stay put.
    [RelayCommand]
    private void ResetTimeAnalysis()
    {
        _timeTracker.Reset();
        _activityTracker.ResetRates();
    }

    // "Session Statistics" reset — the running totals and their rates (kills,
    // experience, currency collected / stashed, and the two rate sparklines).
    [RelayCommand]
    private void ResetSessionStats() => _activityTracker.Reset();

    // "Transaction history" button — opens the modeless ledger window (bank
    // deposits + stash-room hides recorded this session).
    [RelayCommand]
    private void OpenTransactionHistory() => _openTransactionHistory();

    // "Players Seen" button — opens the modeless per-character log of players
    // encountered in the world (Also-here matches + room walk-ins).
    [RelayCommand]
    private void OpenPlayersSeen() => _openPlayersSeen();

    // ----- Refresh plumbing --------------------------------------------

    private void OnChanged()
    {
        if (_refreshScheduled) return;
        _refreshScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshScheduled = false;
            if (!_disposed) Refresh();
        });
    }

    private void Refresh()
    {
        Combat = _combatTracker.Snapshot();
        Time = _timeTracker.Snapshot();
        Activity = _activityTracker.Snapshot();
        KillsPerHour = _activityTracker.KillsPerHourSeries(SparklineBuckets);
        ExperiencePerHour = _activityTracker.ExperiencePerHourSeries(SparklineBuckets);

        HpMaHistoryStats hpMa = _hpMaTracker.Snapshot();
        HpLow = hpMa.HpLow;
        HpHigh = hpMa.HpHigh;
        HpAvg = hpMa.HpAvg;
        MaLow = hpMa.MaLow;
        MaHigh = hpMa.MaHigh;
        MaAvg = hpMa.MaAvg;
        HasManaHistory = hpMa.HasMana;
        LowestHpPercent = hpMa.LowestHpPercent;
        LowestMaPercent = hpMa.LowestMaPercent;
        // A shorter loop (or a reset) can pull the max in under the current focus;
        // keep the anchored step from stranding past the tail.
        if (FocusStep > StepViewMax) FocusStep = StepViewMax;

        // The countdown reads live PlayerStats + the wall clock, so it must
        // re-fire every tick even when the Activity snapshot compares equal.
        OnPropertyChanged(nameof(TimeToLevelText));
    }

    private static string Fmt(TimeSpan t) =>
        $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";

    private static string Range(int min, int max) =>
        max <= 0 ? "—" : $"{min}–{max}";

    // Read an int field off an optional game-data row (missing row / field → 0).
    private static int GetInt(JsonElement? rowOpt, string property)
    {
        if (rowOpt is not JsonElement row || row.ValueKind != JsonValueKind.Object) return 0;
        if (!row.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _liveTick.Stop();
        _combatTracker.Changed -= OnChanged;
        _timeTracker.Changed -= OnChanged;
        _activityTracker.Changed -= OnChanged;
        _hpMaTracker.Changed -= OnChanged;
    }
}

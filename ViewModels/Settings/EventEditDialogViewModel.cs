using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;

namespace MudPlay.ViewModels.Settings;

// Modeless editor for one ScheduledEvent, in the order an event runs: WHEN
// (trigger), DO (action — with a STOP AFTER rule for a loop / auto-lair, which
// never ends by itself), then THEN (what happens once the action is done). Each
// is its own radio group, so the user isn't picking from a single mixed list the
// way MegaMUD's dialog forces.
//
// Edit semantics: the VM works on its own field set; the original ScheduledEvent
// isn't touched until Save. Save returns the materialised event (either
// fresh-constructed for a new event, or with the original's mutations applied for
// a modify). Cancel returns null. The caller decides whether the result goes into
// EventManager.Add (new) or EventManager.Replace (modify).
//
// Walk-to / Loop / Auto-lair pickers bind to the live LoopManager / LairManager
// collections + RoomSearchService so the user sees the same names + room
// references the rest of the app uses; the boss pickers list the Bosses tab's
// bosses for the active realm, and the Then-event picker the character's other
// events.
public sealed partial class EventEditDialogViewModel : ObservableObject, IDialogViewModel<ScheduledEvent?>
{
    public event Action<ScheduledEvent?>? CloseRequested;

    private readonly bool _isNew;
    private readonly LoopManager? _loops;
    private readonly LairManager? _lairs;
    private readonly RoomSearchService? _search;

    // Set once the user picks a Then on a new event; until then the default Then
    // follows the action (a command does nothing after, anything else goes back
    // to what was running).
    private bool _thenChosen;
    private bool _settingThenDefault;

    public EventEditDialogViewModel(
        ScheduledEvent existing,
        bool isNew,
        LoopManager? loops = null,
        LairManager? lairs = null,
        RoomSearchService? search = null,
        IReadOnlyList<string>? bossNames = null,
        IReadOnlyList<string>? eventNames = null,
        IReadOnlyList<(string Label, double Fraction)>? bossWindows = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        _isNew = isNew;
        _loops = loops;
        _lairs = lairs;
        _search = search;

        // Dropdown contents — snapshot on open. Edits to the underlying
        // managers while the dialog is open don't ripple through; user closes
        // + re-opens to see new names. Matches every other dropdown in the app.
        if (loops is not null)
            foreach (Loop l in loops.Loops) AvailableLoopNames.Add(l.Name);
        if (lairs is not null)
            foreach (LairSetup s in lairs.Setups) AvailableAutoLairNames.Add(s.Name);
        foreach (string b in bossNames ?? Array.Empty<string>()) AvailableBossNames.Add(b);
        foreach (string n in eventNames ?? Array.Empty<string>())
            if (!string.Equals(n, existing.Name, StringComparison.OrdinalIgnoreCase)) AvailableEventNames.Add(n);

        Name = existing.Name;
        DisabledFlag = existing.Disabled;

        switch (existing.TriggerType)
        {
            case EventTriggerType.Logoff: IsTriggerLogoff = true; break;
            case EventTriggerType.Relog:  IsTriggerRelog  = true; break;
            case EventTriggerType.AtTime: IsTriggerAtTime = true; break;
            case EventTriggerType.Every:  IsTriggerEvery  = true; break;
            case EventTriggerType.State:  IsTriggerState  = true; break;
            case EventTriggerType.Boss:   IsTriggerBoss   = true; break;
            default:                      IsTriggerLogon  = true; break;
        }
        foreach (EventCondition c in existing.Conditions ?? new List<EventCondition>())
            Conditions.Add(new EventConditionRowViewModel(c, RemoveCondition));
        Conditions.CollectionChanged += (_, _) => Refresh();
        AtTime = existing.AtTime ?? "12:00";
        EveryAmount = existing.EveryAmount ?? 30;
        EveryUnit = existing.EveryUnit ?? EventTimeUnit.Seconds;
        BossName = existing.BossName;
        // One option per Bosses-tab column (its early windows, then the full timer),
        // then the kill and a cleanup reset.
        foreach ((string label, double fraction) in bossWindows ?? new[] { ("87.5%", 0.875) })
            _bossMoments.Add(($"{label} column hits 0", EventBossMoment.EarlyWindow, fraction));
        _bossMoments.Add(("Guaranteed (full) hits 0", EventBossMoment.Guaranteed, null));
        _bossMoments.Add(("Is killed", EventBossMoment.Killed, null));
        _bossMoments.Add(("Cleanup reset", EventBossMoment.CleanupReset, null));
        BossMomentOptions = _bossMoments.Select(m => m.Label).ToList();
        EventBossMoment savedMoment = existing.BossMoment ?? EventBossMoment.EarlyWindow;
        int pick = _bossMoments.FindIndex(m => m.Moment == savedMoment
            && (savedMoment != EventBossMoment.EarlyWindow || existing.BossWindowFraction is not { } f
                || Math.Abs(f - m.Fraction!.Value) < 0.001));
        SelectedBossMoment = BossMomentOptions[Math.Max(0, pick)];
        BossLeadMinutes = existing.BossLeadMinutes ?? 0;

        switch (existing.ActionType)
        {
            case EventActionType.Loop:     IsActionLoop     = true; break;
            case EventActionType.AutoLair: IsActionAutoLair = true; break;
            case EventActionType.Command:  IsActionCommand  = true; break;
            case EventActionType.Roomba:   IsActionRoomba   = true; break;
            case EventActionType.Wait:     IsActionWait     = true; break;
            case EventActionType.RestUp:   IsActionRestUp   = true; break;
            case EventActionType.BankTrip: IsActionBankTrip = true; break;
            default:                       IsActionWalkTo   = true; break;
        }
        SelectedRoombaMode = existing.RoombaMode == EventRoombaMode.InventoryOnly
            ? RoombaModeOptions[1]
            : RoombaModeOptions[0];
        WalkToText = existing.WalkToTarget is { } t ? $"{t.Map}/{t.Room}" : string.Empty;
        LoopName = existing.LoopName;
        AutoLairSetupName = existing.AutoLairSetupName;
        CommandText = existing.CommandText ?? string.Empty;
        WaitSeconds = existing.WaitSeconds ?? 30;

        StopAfterLapsOn = existing.StopAfterLaps is > 0;
        StopAfterLaps = existing.StopAfterLaps is > 0 and var laps ? laps : 1;
        StopAfterMinutesOn = existing.StopAfterMinutes is > 0;
        StopAfterMinutes = existing.StopAfterMinutes is > 0 and var minutes ? minutes : 30;
        StopWhenBossKilledOn = !string.IsNullOrWhiteSpace(existing.StopWhenBossKilled);
        StopBossName = existing.StopWhenBossKilled;
        foreach (EventCondition c in existing.StopConditions ?? new List<EventCondition>())
            StopConditions.Add(new EventConditionRowViewModel(c, RemoveStopCondition));
        StopConditions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ThenNeverRuns));

        _thenChosen = !isNew;
        SetThen(isNew ? DefaultThen() : existing.ResolvedThen);
        ThenLoopName = existing.ThenLoopName;
        ThenAutoLairSetupName = existing.ThenAutoLairSetupName;
        ThenWalkToText = existing.ThenWalkTo is { } tw ? $"{tw.Map}/{tw.Room}" : string.Empty;
        ThenEventName = existing.ThenEventName;
    }

    public string DialogTitle => _isNew ? "New Event" : "Edit Event";

    // ----- Common fields ---------------------------------------------

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _disabledFlag;

    // ----- WHEN (trigger) — mutually-exclusive flags managed by XAML radios -----

    [ObservableProperty] private bool _isTriggerLogon;
    [ObservableProperty] private bool _isTriggerLogoff;
    [ObservableProperty] private bool _isTriggerRelog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AtTimeError))]
    private bool _isTriggerAtTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AtTimeError))]
    private string _atTime = "12:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EveryError))]
    private bool _isTriggerEvery;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EveryError))]
    private int _everyAmount = 30;

    [ObservableProperty] private EventTimeUnit _everyUnit = EventTimeUnit.Seconds;

    // "When" — fires once the listed conditions all hold (EventTriggerType.State).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConditionsError))]
    private bool _isTriggerState;

    public ObservableCollection<EventConditionRowViewModel> Conditions { get; } = new();

    public string ConditionsError =>
        IsTriggerState && Conditions.Count == 0 ? "Add at least one condition." : string.Empty;
    public bool HasConditionsError => ConditionsError.Length > 0;

    [RelayCommand]
    private void AddCondition()
    {
        Conditions.Add(new EventConditionRowViewModel(
            new EventCondition { Stat = EventConditionStat.Money, Comparison = EventComparison.AtLeast },
            RemoveCondition));
        IsTriggerState = true;
    }

    private void RemoveCondition(EventConditionRowViewModel row) => Conditions.Remove(row);

    // Boss timer — a moment on one boss's timer (EventTriggerType.Boss).
    [ObservableProperty] private bool _isTriggerBoss;
    [ObservableProperty] private string? _bossName;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BossLeadApplies))]
    private string _selectedBossMoment = string.Empty;
    [ObservableProperty] private int _bossLeadMinutes;

    private readonly List<(string Label, EventBossMoment Moment, double? Fraction)> _bossMoments = new();
    public IReadOnlyList<string> BossMomentOptions { get; }
    public ObservableCollection<string> AvailableBossNames { get; } = new();
    // "N minutes early" means nothing for a kill.
    public bool BossLeadApplies => SelectedBoss() != EventBossMoment.Killed;

    // Picker source for the EveryUnit ComboBox.
    public IReadOnlyList<EventTimeUnit> EveryUnits { get; } =
        new[] { EventTimeUnit.Seconds, EventTimeUnit.Minutes, EventTimeUnit.Hours };

    public string AtTimeError =>
        IsTriggerAtTime && !LooksLikeHHmm(AtTime) ? "Use HH:mm (24-hour)." : string.Empty;

    public string EveryError =>
        IsTriggerEvery && EveryAmount <= 0 ? "Must be ≥ 1." : string.Empty;

    // ----- DO (action) ------------------------------------------------

    [ObservableProperty] private bool _isActionWalkTo;
    [ObservableProperty] private string _walkToText = string.Empty;

    [ObservableProperty] private bool _isActionLoop;
    [ObservableProperty] private string? _loopName;

    // Saved loops the user can pick from for the Loop action (and a Then loop).
    public ObservableCollection<string> AvailableLoopNames { get; } = new();

    [ObservableProperty] private bool _isActionAutoLair;
    [ObservableProperty] private string? _autoLairSetupName;

    // Saved auto-lair setups for the AutoLair action (and a Then auto-lair).
    public ObservableCollection<string> AvailableAutoLairNames { get; } = new();

    [ObservableProperty] private bool _isActionCommand;
    [ObservableProperty] private string _commandText = string.Empty;

    [ObservableProperty] private bool _isActionRoomba;
    public IReadOnlyList<string> RoombaModeOptions { get; } = new[] { "Sort", "Inventory only" };
    [ObservableProperty] private string _selectedRoombaMode = "Sort";

    [ObservableProperty] private bool _isActionWait;
    [ObservableProperty] private int _waitSeconds = 30;

    [ObservableProperty] private bool _isActionRestUp;
    [ObservableProperty] private bool _isActionBankTrip;

    // ----- STOP AFTER (loop / auto-lair) -------------------------------

    public bool ShowsStopAfter => IsActionLoop || IsActionAutoLair;

    [ObservableProperty] private bool _stopAfterLapsOn;
    [ObservableProperty] private int _stopAfterLaps = 1;
    [ObservableProperty] private bool _stopAfterMinutesOn;
    [ObservableProperty] private int _stopAfterMinutes = 30;
    [ObservableProperty] private bool _stopWhenBossKilledOn;
    [ObservableProperty] private string? _stopBossName;

    public ObservableCollection<EventConditionRowViewModel> StopConditions { get; } = new();

    [RelayCommand]
    private void AddStopCondition() =>
        StopConditions.Add(new EventConditionRowViewModel(
            new EventCondition { Stat = EventConditionStat.Encumbrance, Comparison = EventComparison.AtLeast, Value = 80 },
            RemoveStopCondition));

    private void RemoveStopCondition(EventConditionRowViewModel row) => StopConditions.Remove(row);

    // ----- THEN (after the action) ------------------------------------

    [ObservableProperty] private bool _isThenNothing;
    [ObservableProperty] private bool _isThenResume;
    [ObservableProperty] private bool _isThenLoop;
    [ObservableProperty] private bool _isThenAutoLair;
    [ObservableProperty] private bool _isThenWalkTo;
    [ObservableProperty] private bool _isThenEvent;
    [ObservableProperty] private string? _thenLoopName;
    [ObservableProperty] private string? _thenAutoLairSetupName;
    [ObservableProperty] private string _thenWalkToText = string.Empty;
    [ObservableProperty] private string? _thenEventName;

    // The character's other events, for a Then that fires one.
    public ObservableCollection<string> AvailableEventNames { get; } = new();

    // Then runs only once the action is done — a loop / auto-lair with no stop
    // rule never is, so say so.
    public bool ThenNeverRuns =>
        ShowsStopAfter && !IsThenNothing
        && !(StopAfterLapsOn && IsActionLoop) && !StopAfterMinutesOn && !StopWhenBossKilledOn
        && StopConditions.Count == 0;

    // WHAT-side validation happens on Save (popup), not inline — fewer red labels
    // cluttering the form. WHEN-side format errors stay inline because they're
    // objectively wrong syntax the user can see at a glance. Command never errors
    // at edit time: an empty CommandText is a valid event whose Fire sends a bare
    // carriage return.
    public bool HasAtTimeError => AtTimeError.Length > 0;
    public bool HasEveryError  => EveryError.Length  > 0;

    public bool CanSave =>
        AtTimeError.Length == 0
        && EveryError.Length == 0
        && ConditionsError.Length == 0;

    // ----- Save / Cancel ----------------------------------------------

    [RelayCommand]
    private void Save()
    {
        if (!CanSave) return;

        // WHAT-side validation: if the user selected an action but
        // didn't fill in its target, popup a message + stay open.
        // Doesn't apply to Command (empty = bare CR is intentional).
        if (TryGetMissingTargetMessage() is { } missing)
        {
            // Try the popup; fall through silently if AppServices isn't
            // initialized (tests). Either way, we don't fire CloseRequested.
            try { AppServices.Current.Dialogs.ShowInfo("Event not saved", missing); }
            catch (InvalidOperationException) { /* AppServices uninitialized — tests */ }
            return;
        }

        ScheduledEvent result = new()
        {
            Name = Name?.Trim() ?? string.Empty,
            Disabled = DisabledFlag,
            TriggerType = SelectedTriggerType(),
            ActionType = SelectedActionType(),
            Then = SelectedThen(),
        };

        switch (result.TriggerType)
        {
            case EventTriggerType.AtTime:
                result.AtTime = AtTime;
                break;
            case EventTriggerType.Every:
                result.EveryAmount = EveryAmount;
                result.EveryUnit = EveryUnit;
                break;
            case EventTriggerType.State:
                result.Conditions = Conditions.Select(static c => c.ToModel()).ToList();
                break;
            case EventTriggerType.Boss:
                result.BossName = BossName;
                result.BossMoment = SelectedBoss();
                result.BossWindowFraction = SelectedBossEntry().Fraction;
                result.BossLeadMinutes = BossLeadApplies && BossLeadMinutes > 0 ? BossLeadMinutes : null;
                break;
        }

        switch (result.ActionType)
        {
            case EventActionType.WalkTo:
                if (ResolveRoom(WalkToText) is { Ok: true } wt)
                    result.WalkToTarget = new RoomRef(wt.Map!.Value, wt.Room!.Value);
                break;
            case EventActionType.Loop:
                result.LoopName = LoopName;
                break;
            case EventActionType.AutoLair:
                result.AutoLairSetupName = AutoLairSetupName;
                break;
            case EventActionType.Command:
                result.CommandText = CommandText;
                break;
            case EventActionType.Roomba:
                result.RoombaMode = SelectedRoombaMode == RoombaModeOptions[1]
                    ? EventRoombaMode.InventoryOnly
                    : EventRoombaMode.Sort;
                break;
            case EventActionType.Wait:
                result.WaitSeconds = WaitSeconds;
                break;
        }

        if (ShowsStopAfter)
        {
            if (IsActionLoop && StopAfterLapsOn) result.StopAfterLaps = StopAfterLaps;
            if (StopAfterMinutesOn) result.StopAfterMinutes = StopAfterMinutes;
            if (StopWhenBossKilledOn) result.StopWhenBossKilled = StopBossName;
            if (StopConditions.Count > 0)
                result.StopConditions = StopConditions.Select(static c => c.ToModel()).ToList();
        }

        switch (result.Then)
        {
            case EventThenType.Loop:
                result.ThenLoopName = ThenLoopName;
                break;
            case EventThenType.AutoLair:
                result.ThenAutoLairSetupName = ThenAutoLairSetupName;
                break;
            case EventThenType.WalkTo:
                if (ResolveRoom(ThenWalkToText) is { Ok: true } tw)
                    result.ThenWalkTo = new RoomRef(tw.Map!.Value, tw.Room!.Value);
                break;
            case EventThenType.Event:
                result.ThenEventName = ThenEventName;
                break;
        }

        CloseRequested?.Invoke(result);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    // Internal so tests can poke the validation path without going through a
    // dispatcher / DialogService.
    internal string? TryGetMissingTargetMessage()
    {
        if (IsTriggerBoss && string.IsNullOrWhiteSpace(BossName))
            return "No boss selected. Pick one from the Bosses tab's list.";
        if (IsActionWalkTo)
        {
            WalkToResolution wt = ResolveWalkTo();
            if (!wt.Ok)
                return wt.ErrorMessage
                    ?? "No walk-to target selected. Enter a coordinate (e.g. 1/297) or an unambiguous room name.";
        }
        if (IsActionLoop && string.IsNullOrWhiteSpace(LoopName))
            return "No loop selected. Pick a saved loop from the dropdown.";
        if (IsActionAutoLair && string.IsNullOrWhiteSpace(AutoLairSetupName))
            return "No auto-lair setup selected. Pick a saved setup from the dropdown.";
        if (IsActionWait && WaitSeconds <= 0)
            return "Wait needs a number of seconds.";
        if (ShowsStopAfter && StopWhenBossKilledOn && string.IsNullOrWhiteSpace(StopBossName))
            return "No boss selected for \"until this boss is killed\".";
        if (IsThenLoop && string.IsNullOrWhiteSpace(ThenLoopName))
            return "No loop selected for Then.";
        if (IsThenAutoLair && string.IsNullOrWhiteSpace(ThenAutoLairSetupName))
            return "No auto-lair setup selected for Then.";
        if (IsThenWalkTo && ResolveRoom(ThenWalkToText) is { Ok: false } then)
            return then.ErrorMessage ?? "No room for Then's walk-to. Enter a coordinate (e.g. 1/297) or an unambiguous room name.";
        if (IsThenEvent && string.IsNullOrWhiteSpace(ThenEventName))
            return "No event selected for Then.";
        return null;
    }

    // ----- Radio groups -----------------------------------------------

    // The XAML radios enforce mutual exclusion via GroupName, but tests +
    // programmatic callers don't go through the radios — so each flag, when it
    // flips on, clears its siblings. This way the VM contract — "exactly one
    // trigger, one action, one Then" — holds however a property was set.
    partial void OnIsTriggerLogonChanged(bool value)  { if (value) SetTrigger(EventTriggerType.Logon);  Refresh(); }
    partial void OnIsTriggerLogoffChanged(bool value) { if (value) SetTrigger(EventTriggerType.Logoff); Refresh(); }
    partial void OnIsTriggerRelogChanged(bool value)  { if (value) SetTrigger(EventTriggerType.Relog);  Refresh(); }
    partial void OnIsTriggerAtTimeChanged(bool value) { if (value) SetTrigger(EventTriggerType.AtTime); Refresh(); }
    partial void OnIsTriggerEveryChanged(bool value)  { if (value) SetTrigger(EventTriggerType.Every);  Refresh(); }
    partial void OnIsTriggerStateChanged(bool value)  { if (value) SetTrigger(EventTriggerType.State);  Refresh(); }
    partial void OnIsTriggerBossChanged(bool value)   { if (value) SetTrigger(EventTriggerType.Boss);   Refresh(); }

    private void SetTrigger(EventTriggerType t)
    {
        IsTriggerLogon  = t == EventTriggerType.Logon;
        IsTriggerLogoff = t == EventTriggerType.Logoff;
        IsTriggerRelog  = t == EventTriggerType.Relog;
        IsTriggerAtTime = t == EventTriggerType.AtTime;
        IsTriggerEvery  = t == EventTriggerType.Every;
        IsTriggerState  = t == EventTriggerType.State;
        IsTriggerBoss   = t == EventTriggerType.Boss;
    }

    partial void OnIsActionWalkToChanged(bool value)   { if (value) SetAction(EventActionType.WalkTo);   Refresh(); }
    partial void OnIsActionLoopChanged(bool value)     { if (value) SetAction(EventActionType.Loop);     Refresh(); }
    partial void OnIsActionAutoLairChanged(bool value) { if (value) SetAction(EventActionType.AutoLair); Refresh(); }
    partial void OnIsActionCommandChanged(bool value)  { if (value) SetAction(EventActionType.Command);  Refresh(); }
    partial void OnIsActionRoombaChanged(bool value)   { if (value) SetAction(EventActionType.Roomba);   Refresh(); }
    partial void OnIsActionWaitChanged(bool value)     { if (value) SetAction(EventActionType.Wait);     Refresh(); }
    partial void OnIsActionRestUpChanged(bool value)   { if (value) SetAction(EventActionType.RestUp);   Refresh(); }
    partial void OnIsActionBankTripChanged(bool value) { if (value) SetAction(EventActionType.BankTrip); Refresh(); }

    private void SetAction(EventActionType a)
    {
        IsActionWalkTo   = a == EventActionType.WalkTo;
        IsActionLoop     = a == EventActionType.Loop;
        IsActionAutoLair = a == EventActionType.AutoLair;
        IsActionCommand  = a == EventActionType.Command;
        IsActionRoomba   = a == EventActionType.Roomba;
        IsActionWait     = a == EventActionType.Wait;
        IsActionRestUp   = a == EventActionType.RestUp;
        IsActionBankTrip = a == EventActionType.BankTrip;
        if (!_thenChosen) SetThenDefault();
    }

    partial void OnIsThenNothingChanged(bool value)  { if (value) ChooseThen(EventThenType.Nothing);  Refresh(); }
    partial void OnIsThenResumeChanged(bool value)   { if (value) ChooseThen(EventThenType.Resume);   Refresh(); }
    partial void OnIsThenLoopChanged(bool value)     { if (value) ChooseThen(EventThenType.Loop);     Refresh(); }
    partial void OnIsThenAutoLairChanged(bool value) { if (value) ChooseThen(EventThenType.AutoLair); Refresh(); }
    partial void OnIsThenWalkToChanged(bool value)   { if (value) ChooseThen(EventThenType.WalkTo);   Refresh(); }
    partial void OnIsThenEventChanged(bool value)    { if (value) ChooseThen(EventThenType.Event);    Refresh(); }

    private void ChooseThen(EventThenType t)
    {
        if (!_settingThenDefault) _thenChosen = true;
        SetThen(t);
    }

    private void SetThen(EventThenType t)
    {
        IsThenNothing  = t == EventThenType.Nothing;
        IsThenResume   = t == EventThenType.Resume;
        IsThenLoop     = t == EventThenType.Loop;
        IsThenAutoLair = t == EventThenType.AutoLair;
        IsThenWalkTo   = t == EventThenType.WalkTo;
        IsThenEvent    = t == EventThenType.Event;
    }

    // A command does nothing after; anything else goes back to what was running.
    private EventThenType DefaultThen() =>
        IsActionCommand ? EventThenType.Nothing : EventThenType.Resume;

    private void SetThenDefault()
    {
        _settingThenDefault = true;
        try { SetThen(DefaultThen()); }
        finally { _settingThenDefault = false; }
    }

    partial void OnAtTimeChanged(string value)          => OnPropertyChanged(nameof(AtTimeError));
    partial void OnEveryAmountChanged(int value)        => OnPropertyChanged(nameof(EveryError));
    partial void OnStopAfterLapsOnChanged(bool value)      => OnPropertyChanged(nameof(ThenNeverRuns));
    partial void OnStopAfterMinutesOnChanged(bool value)   => OnPropertyChanged(nameof(ThenNeverRuns));
    partial void OnStopWhenBossKilledOnChanged(bool value)
    {
        // A boss-triggered event most likely camps that same boss.
        if (value && string.IsNullOrWhiteSpace(StopBossName)) StopBossName = BossName;
        OnPropertyChanged(nameof(ThenNeverRuns));
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(AtTimeError));
        OnPropertyChanged(nameof(EveryError));
        OnPropertyChanged(nameof(HasAtTimeError));
        OnPropertyChanged(nameof(HasEveryError));
        OnPropertyChanged(nameof(ConditionsError));
        OnPropertyChanged(nameof(HasConditionsError));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ShowsStopAfter));
        OnPropertyChanged(nameof(ThenNeverRuns));
    }

    private EventTriggerType SelectedTriggerType()
    {
        if (IsTriggerLogoff) return EventTriggerType.Logoff;
        if (IsTriggerRelog)  return EventTriggerType.Relog;
        if (IsTriggerAtTime) return EventTriggerType.AtTime;
        if (IsTriggerEvery)  return EventTriggerType.Every;
        if (IsTriggerState)  return EventTriggerType.State;
        if (IsTriggerBoss)   return EventTriggerType.Boss;
        return EventTriggerType.Logon;
    }

    private EventActionType SelectedActionType()
    {
        if (IsActionLoop)     return EventActionType.Loop;
        if (IsActionAutoLair) return EventActionType.AutoLair;
        if (IsActionCommand)  return EventActionType.Command;
        if (IsActionRoomba)   return EventActionType.Roomba;
        if (IsActionWait)     return EventActionType.Wait;
        if (IsActionRestUp)   return EventActionType.RestUp;
        if (IsActionBankTrip) return EventActionType.BankTrip;
        return EventActionType.WalkTo;
    }

    private EventThenType SelectedThen()
    {
        if (IsThenResume)   return EventThenType.Resume;
        if (IsThenLoop)     return EventThenType.Loop;
        if (IsThenAutoLair) return EventThenType.AutoLair;
        if (IsThenWalkTo)   return EventThenType.WalkTo;
        if (IsThenEvent)    return EventThenType.Event;
        return EventThenType.Nothing;
    }

    private EventBossMoment SelectedBoss() => SelectedBossEntry().Moment;

    private (string Label, EventBossMoment Moment, double? Fraction) SelectedBossEntry()
    {
        int i = _bossMoments.FindIndex(m => m.Label == SelectedBossMoment);
        return _bossMoments[Math.Max(0, i)];
    }

    // Three-state result of resolving a room box: resolved (Map+Room set, no
    // error), unresolved-with-reason (ErrorMessage set — no match or ambiguous), or
    // empty (everything null — the validator supplies the default "No walk-to
    // target selected" message).
    internal readonly record struct WalkToResolution(int? Map, int? Room, string? ErrorMessage)
    {
        public bool Ok => Map is not null && Room is not null;
    }

    internal WalkToResolution ResolveWalkTo() => ResolveRoom(WalkToText);

    // Resolve a room box via RoomSearchService. Accepts coord (1/297, 1 297,
    // 1,297) directly; for names, requires exactly one room-name match (room-tier
    // only — monster matches don't qualify here since walk-to means a destination,
    // not a mob). Distinguishes no-match vs ambiguous-match in the error so the
    // user-facing popup can say the right thing instead of blanket "no target
    // selected".
    private WalkToResolution ResolveRoom(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(null, null, null);

        // Coord short-circuit — works without a RoomSearchService.
        (int? coordMap, int? coordRoom) = RoomSearchService.TryParseCoordinate(text);
        if (coordMap is int cm && coordRoom is int cr)
            return new(cm, cr, null);

        if (_search is null) return new(null, null, null);

        // Cap chosen large enough to count ambiguity without
        // truncating it into a false "no match". The old cap=5
        // would fill on a popular room-name prefix before the
        // ambiguity could even be observed.
        IReadOnlyList<RoomSearchResult> matches =
            _search.Search(text, source: null, cap: 50, includeAcronyms: false);
        // Want exactly one ROOM match (MonsterTag null). Monster
        // matches are ignored — the editor's WalkTo is for places,
        // not mob lairs.
        List<RoomSearchResult> rooms = matches
            .Where(mm => mm.MonsterTag is null && !mm.IsInformational)
            .ToList();
        if (rooms.Count == 0)
            return new(null, null,
                $"No room matches '{text}'. Try a coordinate (e.g. 1/297) or a more specific name.");
        if (rooms.Count > 1)
            return new(null, null,
                $"'{text}' matches {rooms.Count} rooms — be more specific or use a coordinate (e.g. 1/297).");
        return new(rooms[0].Key.Map, rooms[0].Key.Room, null);
    }

    private static bool LooksLikeHHmm(string s) =>
        TimeOnly.TryParseExact(s, "HH:mm", out _);
}

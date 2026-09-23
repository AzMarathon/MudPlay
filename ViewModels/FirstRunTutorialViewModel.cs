using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels;

// One line of a step's checklist. Done latches its completion signal — a specific
// menu action having been taken (ActionKey non-null, keyed on _doneActions) or the
// step's outcome being reached (ActionKey null). Used for the check-mark +
// highlight; step advancement is driven by TutorialStep.Outcome.
internal sealed record SubStep(string Text, Func<bool> Done, string? ActionKey = null);

// A step of the tour: a titled checklist (Subs) pointing at a menu (TargetName),
// considered complete when Outcome is true.
internal sealed record TutorialStep(
    string Title, IReadOnlyList<SubStep> Subs, string? TargetName, Func<bool> Outcome);

// A checklist line as the card renders it: its text, whether it's ticked, and
// whether it's the current (next-to-do) line to highlight.
public sealed record SubStepView(string Text, bool IsDone, bool IsCurrent)
{
    public string Marker => IsDone ? "☑" : "☐";   // ☑ / ☐
}

// Drives the first-run setup tour: a short, navigable sequence that guides a
// brand-new user through the prerequisites they're missing (add a BBS, add a
// character, import game data) and finishes at Connect. Each step is a checklist
// whose first line is a menu action (ticks when the user takes it) and whose
// remaining lines reflect the outcome (the BBS / character / game-data landing).
// A step is done — and the tour advances — when its outcome is true. Pure
// presentation state: the prerequisite probes and the "don't show again"
// persistence are injected, so this stays free of AppServices and unit-testable.
public sealed partial class FirstRunTutorialViewModel : ObservableObject
{
    // Action keys — the controls the tour tracks + highlights, in click order.
    public const string ActionProfileManagement = "ProfileManagement";
    public const string ActionAddBbs = "AddBbs";
    public const string ActionBbsHostPort = "BbsHostPort";
    public const string ActionBbsSaved = "BbsSaved";
    public const string ActionAddCharacter = "AddCharacter";
    public const string ActionCharacterAdded = "CharacterAdded";
    public const string ActionImportMdb = "ImportMdb";
    public const string ActionGameDataImported = "GameDataImported";

    private readonly Func<bool> _hasGameData;
    private readonly Func<bool> _hasBbs;
    private readonly Func<bool> _hasCharacter;
    private readonly Func<bool> _isConnected;
    private readonly Action _persistDismiss;

    private readonly List<TutorialStep> _steps = new();

    // Menu actions the user has taken during the tour (cumulative) — the
    // completion signal for each step's leading action line.
    private readonly HashSet<string> _doneActions = new(StringComparer.Ordinal);

    // Demo walkthrough (the Program Log test button): show every step as if
    // nothing is set up, no outcome ticked, no auto-advance, and dismissing
    // doesn't persist — so the tour can be reviewed on a configured install. The
    // action lines still tick on real clicks, so the checklist stays interactive.
    private bool _demoMode;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private int _currentIndex;

    public FirstRunTutorialViewModel(
        Func<bool> hasGameData, Func<bool> hasBbs, Func<bool> hasCharacter,
        Func<bool> isConnected, Action persistDismiss)
    {
        _hasGameData = hasGameData ?? throw new ArgumentNullException(nameof(hasGameData));
        _hasBbs = hasBbs ?? throw new ArgumentNullException(nameof(hasBbs));
        _hasCharacter = hasCharacter ?? throw new ArgumentNullException(nameof(hasCharacter));
        _isConnected = isConnected ?? throw new ArgumentNullException(nameof(isConnected));
        _persistDismiss = persistDismiss ?? throw new ArgumentNullException(nameof(persistDismiss));
    }

    public bool AnyPrerequisiteMissing => !_hasGameData() || !_hasBbs() || !_hasCharacter();

    // Show the tour at the first not-yet-done step, built from what's missing now.
    public void Start()
    {
        _demoMode = false;
        BuildSteps();
        CurrentIndex = FirstUndoneIndex();
        IsActive = true;
        RaiseStepProperties();
    }

    // Force-show the full tour as if nothing is configured — the Program Log test
    // button, so it can be reviewed without wiping the install. All steps show, at
    // step 1, with only the action lines interactive.
    public void StartDemo()
    {
        _demoMode = true;
        BuildSteps();
        CurrentIndex = 0;
        IsActive = true;
        RaiseStepProperties();
    }

    private void BuildSteps()
    {
        bool demo = _demoMode;
        Func<bool> never = static () => false;
        // Action lines key off the real click signal, so the checklist responds to
        // clicks even in a demo; only the OUTCOME lines + step completion are forced
        // unmet in demo, so a fully-configured install still walks every step.
        SubStep Action(string text, string key) => new(text, () => _doneActions.Contains(key), key);
        SubStep Outcome(string text, Func<bool> pred) => new(text, demo ? never : pred);

        _doneActions.Clear();
        _steps.Clear();
        if (demo || !_hasBbs())
            _steps.Add(new TutorialStep("Add a BBS", new[]
            {
                Action("File → Profile Management", ActionProfileManagement),
                Action("Click Add (under BBSes)", ActionAddBbs),
                Action("Enter the host/IP + port", ActionBbsHostPort),
                Action("Click OK", ActionBbsSaved),
            }, "FileMenu", demo ? never : _hasBbs));

        if (demo || !_hasCharacter())
            _steps.Add(new TutorialStep("Add a character", new[]
            {
                Action("File → Profile Management", ActionProfileManagement),
                Action("Click Add (under Characters)", ActionAddCharacter),
                Action("Name it, then Save", ActionCharacterAdded),
            }, "FileMenu", demo ? never : _hasCharacter));

        if (demo || !_hasGameData())
            _steps.Add(new TutorialStep("Import game data", new[]
            {
                Action("Game Data → Import .mdb", ActionImportMdb),
                Action("Pick your MajorMUD .mdb file", ActionGameDataImported),
            }, "GameDataMenu", demo ? never : _hasGameData));

        _steps.Add(new TutorialStep("Connect", new[]
        {
            Outcome("File → Connect (Alt+H) to enter the game", _isConnected),
        }, "FileMenu", demo ? never : _isConnected));
    }

    private int FirstUndoneIndex()
    {
        for (int i = 0; i < _steps.Count; i++)
            if (!_steps[i].Outcome()) return i;
        return 0;
    }

    // A step is complete when every checklist line is ticked (all its action
    // signals fired) or its real outcome is reached (a fallback for real runs
    // where the user set things up off-tour). Demo forces the outcome unmet, so a
    // configured install walks each step by its clicks.
    private static bool StepComplete(TutorialStep step) => step.Subs.All(s => s.Done()) || step.Outcome();

    // Re-check the live prerequisites (call when the user returns to the main
    // window). Auto-advances past any completed step, so finishing a task in
    // another window moves the tour forward.
    public void Refresh()
    {
        if (!IsActive || _demoMode) return;
        AdvancePastCompleted();
        RaiseStepProperties();
    }

    // A tracked action the user took (clicking Profile Management, filling the
    // host/port, saving the BBS, …). Ticks its checklist line, advances the
    // highlight, auto-advances the step when its last line lands, and moves the
    // tour to the next step. Works in demo too (the action signals are real).
    public void NotifyActionDone(string actionKey)
    {
        if (!IsActive || string.IsNullOrEmpty(actionKey)) return;
        if (!_doneActions.Add(actionKey)) return;
        AdvancePastCompleted();
        RaiseStepProperties();
    }

    private void AdvancePastCompleted()
    {
        while (CurrentIndex < _steps.Count - 1 && CurrentStep is { } s && StepComplete(s))
            CurrentIndex++;
    }

    private TutorialStep? CurrentStep =>
        CurrentIndex >= 0 && CurrentIndex < _steps.Count ? _steps[CurrentIndex] : null;

    public string CurrentTitle => CurrentStep?.Title ?? "";
    public string? CurrentTargetName => CurrentStep?.TargetName;
    public bool CurrentIsDone => CurrentStep is { } s && StepComplete(s);
    public string StepCounterText => _steps.Count == 0 ? "" : $"Step {CurrentIndex + 1} of {_steps.Count}";
    public bool CanPrev => CurrentIndex > 0;
    public bool IsLastStep => CurrentIndex >= _steps.Count - 1;
    public string NextButtonText => IsLastStep ? "Finish" : "Next";
    public bool AllDone => _steps.Count > 0 && _steps.All(StepComplete);

    // The action the current (next-to-do) checklist line wants the user to take,
    // or null when that line is an outcome line — drives which menu item glows.
    public string? CurrentActionKey
    {
        get
        {
            if (CurrentStep is not { } step) return null;
            SubStep sub = step.Subs[FirstNotDoneSub(step)];
            return sub.Done() ? null : sub.ActionKey;
        }
    }

    public bool HighlightProfileManagement => IsActive && CurrentActionKey == ActionProfileManagement;
    public bool HighlightImportMdb => IsActive && CurrentActionKey == ActionImportMdb;

    // The current step's checklist, with the first not-done line flagged current.
    public IReadOnlyList<SubStepView> CurrentSubs
    {
        get
        {
            if (CurrentStep is not { } step) return Array.Empty<SubStepView>();
            int current = FirstNotDoneSub(step);
            var views = new List<SubStepView>(step.Subs.Count);
            for (int i = 0; i < step.Subs.Count; i++)
            {
                bool done = step.Subs[i].Done();
                views.Add(new SubStepView(step.Subs[i].Text, done, i == current));
            }
            return views;
        }
    }

    private static int FirstNotDoneSub(TutorialStep step)
    {
        for (int i = 0; i < step.Subs.Count; i++)
            if (!step.Subs[i].Done()) return i;
        return step.Subs.Count - 1;
    }

    [RelayCommand]
    private void Prev()
    {
        if (CanPrev) CurrentIndex--;
    }

    [RelayCommand]
    private void Next()
    {
        if (IsLastStep) { Finish(); return; }
        CurrentIndex++;
    }

    // "Skip / Don't show again" — stop the tour and never auto-show it again.
    [RelayCommand]
    private void Skip() => Finish();

    private void Finish()
    {
        IsActive = false;
        // A demo run (test button) never persists the "don't show again" flag —
        // it's a review, not the real dismissal.
        if (!_demoMode) _persistDismiss();
    }

    partial void OnCurrentIndexChanged(int value) => RaiseStepProperties();
    partial void OnIsActiveChanged(bool value) => RaiseStepProperties();

    private void RaiseStepProperties()
    {
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentTargetName));
        OnPropertyChanged(nameof(CurrentSubs));
        OnPropertyChanged(nameof(CurrentIsDone));
        OnPropertyChanged(nameof(CurrentActionKey));
        OnPropertyChanged(nameof(HighlightProfileManagement));
        OnPropertyChanged(nameof(HighlightImportMdb));
        OnPropertyChanged(nameof(StepCounterText));
        OnPropertyChanged(nameof(CanPrev));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(AllDone));
    }
}

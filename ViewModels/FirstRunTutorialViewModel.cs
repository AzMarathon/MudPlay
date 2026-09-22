using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels;

// One line of a step's checklist. Done latches its own completion signal (a menu
// having been opened, or the step's outcome being reached) — used only for the
// check-mark / highlight; step advancement is driven by TutorialStep.Outcome.
internal sealed record SubStep(string Text, Func<bool> Done);

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
// whose lines tick + highlight as the user progresses (a menu opening, then the
// outcome being reached); a step is done — and the tour advances — when its
// outcome is true. Pure presentation state: the prerequisite probes and the
// "don't show again" persistence are injected, so this stays free of AppServices
// and is unit-testable.
public sealed partial class FirstRunTutorialViewModel : ObservableObject
{
    private readonly Func<bool> _hasGameData;
    private readonly Func<bool> _hasBbs;
    private readonly Func<bool> _hasCharacter;
    private readonly Func<bool> _isConnected;
    private readonly Action _persistDismiss;

    private readonly List<TutorialStep> _steps = new();

    // Menus the user has opened during the tour (cumulative) — the completion
    // signal for each step's "open the … menu" checklist line.
    private readonly HashSet<string> _openedMenus = new(StringComparer.Ordinal);

    // Demo walkthrough (the Program Log test button): show every step as if
    // nothing is set up, nothing ticked, no auto-advance, and dismissing doesn't
    // persist — so the tour can be reviewed end-to-end on a configured install.
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
    // step 1, nothing ticked.
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
        // Menu lines always key off the real menu-open signal, so the checklist
        // responds to clicks even during a demo. Only the OUTCOME lines (and step
        // completion) are forced unmet in demo, so a fully-configured install can
        // still walk every step instead of auto-completing.
        SubStep Menu(string text, string key) => new(text, () => _openedMenus.Contains(key));
        SubStep Outcome(string text, Func<bool> pred) => new(text, demo ? never : pred);

        _openedMenus.Clear();
        _steps.Clear();
        if (demo || !_hasBbs())
            _steps.Add(new TutorialStep("Add a BBS", new[]
            {
                Menu("File → Profile Management", "File"),
                Outcome("Add BBS (opens Settings)", _hasBbs),
                Outcome("Fill in the host/IP + port", _hasBbs),
                Outcome("OK", _hasBbs),
            }, "FileMenu", demo ? never : _hasBbs));

        if (demo || !_hasCharacter())
            _steps.Add(new TutorialStep("Add a character", new[]
            {
                Menu("File → Profile Management", "File"),
                Outcome("Add a character on your BBS", _hasCharacter),
                Outcome("Name it, then Save", _hasCharacter),
            }, "FileMenu", demo ? never : _hasCharacter));

        if (demo || !_hasGameData())
            _steps.Add(new TutorialStep("Import game data", new[]
            {
                Menu("Game Data → Import .mdb", "GameData"),
                Outcome("Pick your MajorMUD .mdb file", _hasGameData),
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

    // Re-check the live prerequisites (call when the user returns to the main
    // window). Ticks completed lines and auto-advances past any step whose outcome
    // is now reached, so finishing a task in another window moves the tour forward.
    public void Refresh()
    {
        if (!IsActive || _demoMode) return;
        while (CurrentIndex < _steps.Count - 1 && CurrentStep is { } s && s.Outcome())
            CurrentIndex++;
        RaiseStepProperties();
    }

    // A menu the user opened — the completion signal for a step's "open the … menu"
    // line. Re-renders the checklist so that line ticks and the highlight advances.
    public void NotifyMenuOpened(string menuKey)
    {
        if (!IsActive || string.IsNullOrEmpty(menuKey)) return;
        if (_openedMenus.Add(menuKey)) RaiseStepProperties();
    }

    private TutorialStep? CurrentStep =>
        CurrentIndex >= 0 && CurrentIndex < _steps.Count ? _steps[CurrentIndex] : null;

    public string CurrentTitle => CurrentStep?.Title ?? "";
    public string? CurrentTargetName => CurrentStep?.TargetName;
    public bool CurrentIsDone => CurrentStep?.Outcome() ?? false;
    public string StepCounterText => _steps.Count == 0 ? "" : $"Step {CurrentIndex + 1} of {_steps.Count}";
    public bool CanPrev => CurrentIndex > 0;
    public bool IsLastStep => CurrentIndex >= _steps.Count - 1;
    public string NextButtonText => IsLastStep ? "Finish" : "Next";
    public bool AllDone => _steps.Count > 0 && _steps.All(s => s.Outcome());

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

    private void RaiseStepProperties()
    {
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentTargetName));
        OnPropertyChanged(nameof(CurrentSubs));
        OnPropertyChanged(nameof(CurrentIsDone));
        OnPropertyChanged(nameof(StepCounterText));
        OnPropertyChanged(nameof(CanPrev));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(AllDone));
    }
}

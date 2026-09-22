using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels;

// One step of the first-run setup tour. TargetName is the x:Name of the
// main-window control to spotlight (null centres the card); IsDone re-checks the
// live prerequisite so a completed step shows a check and the tour can advance.
internal sealed record TutorialStep(
    string Title, string Instruction, string? TargetName, Func<bool> IsDone);

// Drives the first-run setup overlay: a short, navigable tour that guides a
// brand-new user through the prerequisites they're missing (import game data,
// add a BBS, add a character) and finishes at Connect. Pure presentation state —
// the missing-prerequisite probes and the "don't show again" persistence are
// injected, so this stays free of AppServices and is unit-testable. The overlay
// view watches CurrentTargetName / IsActive to place its spotlight.
public sealed partial class FirstRunTutorialViewModel : ObservableObject
{
    private readonly Func<bool> _hasGameData;
    private readonly Func<bool> _hasBbs;
    private readonly Func<bool> _hasCharacter;
    private readonly Func<bool> _isConnected;
    private readonly Action _persistDismiss;

    private readonly List<TutorialStep> _steps = new();

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

    // True when a fresh install still lacks something the tour covers — the
    // auto-show gate (combined with the dismissed flag by the caller).
    public bool AnyPrerequisiteMissing => !_hasGameData() || !_hasBbs() || !_hasCharacter();

    // Build the step list from what's missing right now (+ Connect as the finish)
    // and show the overlay at the first not-yet-done step. Re-openable: each call
    // rebuilds against the current state, so a partly-set-up user sees only what's
    // still outstanding.
    public void Start()
    {
        BuildSteps();
        CurrentIndex = FirstUndoneIndex();
        IsActive = true;
        RaiseStepProperties();
    }

    private void BuildSteps()
    {
        _steps.Clear();
        if (!_hasGameData())
            _steps.Add(new TutorialStep(
                "Import game data",
                "Game Data → Import .mdb, then pick your MajorMUD .mdb file.",
                "GameDataMenu", _hasGameData));
        if (!_hasBbs())
            _steps.Add(new TutorialStep(
                "Add a BBS",
                "File → Profile Management → BBSes → Add. Enter host + port, then Save.",
                "FileMenu", _hasBbs));
        if (!_hasCharacter())
            _steps.Add(new TutorialStep(
                "Add a character",
                "Profile Management → Characters → Add. Name it, then Save.",
                "FileMenu", _hasCharacter));
        _steps.Add(new TutorialStep(
            "Connect",
            "File → Connect (Alt+H) to enter the game.",
            "FileMenu", _isConnected));
    }

    private int FirstUndoneIndex()
    {
        for (int i = 0; i < _steps.Count; i++)
            if (!_steps[i].IsDone()) return i;
        return 0;
    }

    // Re-check the live prerequisites (call when the user returns to the main
    // window). Refreshes the check-mark + button state and auto-advances past any
    // step they just completed, so finishing a task in Profile Management moves
    // the tour forward on its own.
    public void Refresh()
    {
        if (!IsActive) return;
        while (CurrentIndex < _steps.Count - 1 && _steps[CurrentIndex].IsDone())
            CurrentIndex++;
        RaiseStepProperties();
    }

    private TutorialStep? CurrentStep =>
        CurrentIndex >= 0 && CurrentIndex < _steps.Count ? _steps[CurrentIndex] : null;

    public string CurrentTitle => CurrentStep?.Title ?? "";
    public string CurrentInstruction => CurrentStep?.Instruction ?? "";
    public string? CurrentTargetName => CurrentStep?.TargetName;
    public bool CurrentIsDone => CurrentStep?.IsDone() ?? false;
    public string StepCounterText => _steps.Count == 0 ? "" : $"Step {CurrentIndex + 1} of {_steps.Count}";
    public bool CanPrev => CurrentIndex > 0;
    public bool IsLastStep => CurrentIndex >= _steps.Count - 1;
    public string NextButtonText => IsLastStep ? "Finish" : "Next";
    public bool AllDone => _steps.Count > 0 && _steps.All(s => s.IsDone());

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
        _persistDismiss();
    }

    partial void OnCurrentIndexChanged(int value) => RaiseStepProperties();

    private void RaiseStepProperties()
    {
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CurrentInstruction));
        OnPropertyChanged(nameof(CurrentTargetName));
        OnPropertyChanged(nameof(CurrentIsDone));
        OnPropertyChanged(nameof(StepCounterText));
        OnPropertyChanged(nameof(CanPrev));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextButtonText));
        OnPropertyChanged(nameof(AllDone));
    }
}

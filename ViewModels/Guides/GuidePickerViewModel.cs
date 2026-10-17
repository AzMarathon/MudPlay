using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Settings;

namespace MudPlay.ViewModels.Guides;

// A guide as the picker lists it: what it covers and how far the user got.
public sealed record GuidePickerRow(string Id, string Title, string Description, string Status, bool CanRestart);

public sealed record GuidePickerGroup(string Area, IReadOnlyList<GuidePickerRow> Rows);

// Help → Visual Guides: every guide grouped by area, each with its progress. Open
// hands the guide to the runner (the floating step card) and closes the picker so
// it doesn't sit over the windows the guide points at.
public sealed partial class GuidePickerViewModel
{
    private readonly GuideViewModel _runner;

    public ObservableCollection<GuidePickerGroup> Groups { get; } = new();

    public event Action? CloseRequested;

    public GuidePickerViewModel(GuideViewModel runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _runner.ProgressChanged += Rebuild;
        Rebuild();
    }

    // The window closed: stop following the runner, which outlives it.
    public void Detach() => _runner.ProgressChanged -= Rebuild;

    private void Rebuild()
    {
        GuideProgress progress = _runner.Progress;
        Groups.Clear();
        foreach (IGrouping<string, GuideDefinition> area in _runner.Guides.GroupBy(g => g.Area))
            Groups.Add(new GuidePickerGroup(area.Key, area.Select(g => ToRow(g, progress)).ToList()));
    }

    private static GuidePickerRow ToRow(GuideDefinition g, GuideProgress progress)
    {
        string status = progress.Finished.ContainsKey(g.Id) ? "✓ Done"
            : g.Resumable && progress.StepReached.TryGetValue(g.Id, out int step)
                ? $"Step {step + 1} of {g.Steps.Count}"
                : g.Resumable ? $"{g.Steps.Count} steps" : "";
        return new GuidePickerRow(g.Id, g.Title, g.Description, status, g.Resumable);
    }

    [RelayCommand]
    private void Open(GuidePickerRow? row)
    {
        if (row is null) return;
        if (row.Id == GuideCatalogue.SetupId) _runner.StartSetup();
        else _runner.Start(row.Id);
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private void Restart(GuidePickerRow? row)
    {
        if (row is null) return;
        _runner.Restart(row.Id);
        CloseRequested?.Invoke();
    }
}

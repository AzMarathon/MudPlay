using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// Read-only viewer for the live trigger-wildcard store — the values triggers have
// captured this session via {name} / {1} placeholders, and what each currently
// holds. Opened from the Triggers tab's "Wildcards" button. Live: subscribes to
// TriggerEngine.WildcardsChanged so a capture that lands while the window is open
// updates the list. Trigger-only by design — this shows the trigger system's own
// store, never alias / macro placeholders (see TriggerEngine's class comment).
public sealed partial class TriggerWildcardsViewModel
    : ObservableObject, Services.IDialogViewModel<bool>, IDisposable
{
    public event Action<bool>? CloseRequested;

    private readonly TriggerEngine _engine;

    public ObservableCollection<WildcardRow> Wildcards { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWildcards))]
    private int _count;

    public bool HasWildcards => Count > 0;

    public TriggerWildcardsViewModel(TriggerEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _engine.WildcardsChanged += Refresh;
        Refresh();
    }

    // Rebuild the row list from the engine's live store, sorted by name so the
    // view is stable across refreshes. WildcardsChanged fires on the UI thread
    // (every dispatch path is marshalled upstream), so no re-marshal is needed.
    private void Refresh()
    {
        Wildcards.Clear();
        foreach (KeyValuePair<string, string> kv in _engine.Variables.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            Wildcards.Add(new WildcardRow(kv.Key, kv.Value));
        Count = Wildcards.Count;
    }

    [RelayCommand]
    private void Clear() => _engine.ClearWildcards();

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(false);

    public void Dispose() => _engine.WildcardsChanged -= Refresh;

    // One row: the wildcard's name and its current captured value.
    public sealed record WildcardRow(string Name, string Value);
}

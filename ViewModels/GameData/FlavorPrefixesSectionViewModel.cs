using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;
using MudPlay.Views.GameData;

namespace MudPlay.ViewModels.GameData;

// Game Data Browser → Flavor Prefixes tab. Edits the active game-data set's shared
// vocabulary of monster flavor adjectives (Services.FlavorPrefixStore) — the words the
// room classifier strips from a prefixed display name ("large giant rat" → "giant rat")
// so it needs no per-monster prefix data. A custom realm that uses different adjectives
// adds them here; edits persist per set at game data/{set}/flavor-prefixes.json.
public sealed partial class FlavorPrefixesSectionViewModel : GameDataSectionViewModel
{
    private readonly FlavorPrefixStore _store;
    private Control? _view;

    public override string Id => "flavor-prefixes";
    public override string Title => "Flavor Prefixes";

    public override IEnumerable<string> SearchableLabels =>
        new[] { Title, "flavor", "prefix", "adjective", "monster", "name" };

    // The active vocabulary, mirrored from the store for the list view.
    public ObservableCollection<string> Prefixes { get; } = new();

    // Bound to the "add" textbox.
    [ObservableProperty] private string _newPrefix = string.Empty;

    // Footer note — which set the vocabulary belongs to, or a warning when none is active
    // (edits with no set stay in memory only, matching the other per-set editors).
    public string SetNote => _store.ActiveSet is { Length: > 0 } set
        ? $"Vocabulary for set: {set}"
        : "No game-data set active — changes can't be saved.";

    public FlavorPrefixesSectionViewModel(FlavorPrefixStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;
        Rebuild();
    }

    // The store reloads on GameDataCache.ActiveSetChanged and fires Changed; marshal the
    // list rebuild to the UI thread in case that ever arrives off it.
    private void OnStoreChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Rebuild();
        else Dispatcher.UIThread.Post(Rebuild);
    }

    private void Rebuild()
    {
        Prefixes.Clear();
        foreach (string p in _store.Prefixes) Prefixes.Add(p);
        OnPropertyChanged(nameof(SetNote));
    }

    [RelayCommand]
    private void AddPrefix()
    {
        if (_store.Add(NewPrefix)) NewPrefix = string.Empty;
    }

    [RelayCommand]
    private void RemovePrefix(string? prefix)
    {
        if (prefix is not null) _store.Remove(prefix);
    }

    [RelayCommand]
    private void ResetToDefaults() => _store.ResetToDefaults();

    public override Control View => _view ??= new FlavorPrefixesSectionView { DataContext = this };

    public override void Dispose() => _store.Changed -= OnStoreChanged;
}

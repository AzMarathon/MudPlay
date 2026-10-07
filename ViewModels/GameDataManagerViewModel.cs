using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Game Data → "Manage Sets…". Two immediate-action sections over
// GameDataSetManager: copy / move the ticked kinds of a set's data to another
// set, and delete a set (tables + loops). No staged Save — each button performs
// its op and reports via Status.
//
// All-windows-modeless rule: the destructive delete can't pop a confirm
// dialog, so it arms in place — the first click flips the button to "Click
// again to confirm"; the second click commits. Picking a different set to
// delete disarms it.
public sealed partial class GameDataManagerViewModel
    : ObservableObject, IDialogViewModel<bool>
{
    public event Action<bool>? CloseRequested;

    private readonly GameDataSetManager _manager;
    private readonly GameDataCache _cache;

    // Imported sets on disk, snapshotted on open and after each delete.
    public ObservableCollection<string> Sets { get; } = new();

    // What can go across, each with how much of it the source set holds.
    public IReadOnlyList<GameDataSetPartOption> Parts { get; } = new GameDataSetPartOption[]
    {
        new(GameDataSetPart.Loops, "Loops and lair setups",
            "Loops, Auto-Lair setups and the nav folders they sit in. Added to the destination's; a loop of the same name is replaced."),
        new(GameDataSetPart.Messages, "Message edits",
            "Your changes to spell and condition messages, monster messages and flavor prefixes. Replaces the destination's."),
        new(GameDataSetPart.RecordOverrides, "Game Data Browser edits",
            "Your changes to items, monsters, spells, rooms and the other tables, at every level: all characters, each realm and each character. Replaces the destination's."),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyOrMove))]
    private string? _sourceSet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyOrMove))]
    private string? _destSet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private string? _deleteSet;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteButtonText))]
    private bool _confirmingDelete;

    // Enabled when both pickers hold a (different) set.
    public bool CanCopyOrMove =>
        !string.IsNullOrWhiteSpace(SourceSet)
        && !string.IsNullOrWhiteSpace(DestSet)
        && !string.Equals(SourceSet, DestSet, StringComparison.OrdinalIgnoreCase);

    public bool CanDelete => !string.IsNullOrWhiteSpace(DeleteSet);

    public string DeleteButtonText => ConfirmingDelete ? "Click again to confirm" : "Delete set";

    public GameDataManagerViewModel(GameDataSetManager manager, GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(cache);
        _manager = manager;
        _cache = cache;
        RefreshSets();
    }

    private void RefreshSets()
    {
        Sets.Clear();
        foreach (string s in _cache.AvailableSets) Sets.Add(s);
    }

    // Re-picking the delete target disarms a pending confirm so a stale
    // "click again" never deletes the wrong set.
    partial void OnDeleteSetChanged(string? value) => ConfirmingDelete = false;

    // Show how much of each kind the picked source holds.
    partial void OnSourceSetChanged(string? value) => RefreshPartCounts();

    private void RefreshPartCounts()
    {
        bool known = !string.IsNullOrWhiteSpace(SourceSet);
        foreach (GameDataSetPartOption option in Parts)
        {
            option.FileCount = known ? _manager.FileCount(SourceSet!, option.Part) : 0;
            option.SourceKnown = known;
        }
    }

    private GameDataSetPart TickedParts()
    {
        GameDataSetPart parts = GameDataSetPart.None;
        foreach (GameDataSetPartOption option in Parts)
            if (option.IsChecked && option.IsAvailable) parts |= option.Part;
        return parts;
    }

    [RelayCommand]
    private void CopyParts()
    {
        Status = _manager.Copy(SourceSet ?? string.Empty, DestSet ?? string.Empty, TickedParts()).Message;
        RefreshPartCounts();
    }

    [RelayCommand]
    private void MoveParts()
    {
        Status = _manager.Move(SourceSet ?? string.Empty, DestSet ?? string.Empty, TickedParts()).Message;
        RefreshPartCounts();
    }

    [RelayCommand]
    private void DeleteSelectedSet()
    {
        if (string.IsNullOrWhiteSpace(DeleteSet)) return;

        if (!ConfirmingDelete)
        {
            ConfirmingDelete = true;
            Status = $"Delete '{DeleteSet}' and its loops permanently? Click the button again to confirm.";
            return;
        }

        string target = DeleteSet!;
        GameDataSetManager.OpResult result = _manager.DeleteSet(target);
        ConfirmingDelete = false;
        Status = result.Message;

        if (!result.Ok) return;
        RefreshSets();
        if (string.Equals(SourceSet, target, StringComparison.OrdinalIgnoreCase)) SourceSet = null;
        if (string.Equals(DestSet, target, StringComparison.OrdinalIgnoreCase)) DestSet = null;
        DeleteSet = null;
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(false);
}

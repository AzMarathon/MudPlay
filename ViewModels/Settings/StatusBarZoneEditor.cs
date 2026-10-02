using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.Settings;
using MudPlay.Services;

namespace MudPlay.ViewModels.Settings;

// One zone of a status-bar row (left, centre or right): its items in order, and the
// picker that adds another.
public sealed partial class StatusBarZoneEditor : ObservableObject
{
    private readonly Action _changed;

    public string Title { get; }
    public ObservableCollection<StatusBarEntryEditor> Entries { get; } = new();
    public IReadOnlyList<StatusBarItemDef> Choices => StatusBarItemCatalogue.All;

    // The picker's selection; Add places it at the end of the zone.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private StatusBarItemDef? _choice;

    public StatusBarZoneEditor(string title, IEnumerable<StatusBarEntry> entries, Action changed)
    {
        Title = title;
        _changed = changed;
        foreach (StatusBarEntry entry in entries)
            if (StatusBarItemCatalogue.Find(entry.Item) is { } def)
                Entries.Add(new StatusBarEntryEditor(this, def, entry.Text));
    }

    public List<StatusBarEntry> ToEntries() => Entries.Select(e => e.ToEntry()).ToList();

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (Choice is not { } def) return;
        Entries.Add(new StatusBarEntryEditor(this, def, text: null));
        _changed();
    }

    private bool CanAdd() => Choice is not null;

    public void Move(StatusBarEntryEditor entry, int by)
    {
        int from = Entries.IndexOf(entry), to = from + by;
        if (from < 0 || to < 0 || to >= Entries.Count) return;
        Entries.Move(from, to);
        _changed();
    }

    public void Remove(StatusBarEntryEditor entry)
    {
        if (Entries.Remove(entry)) _changed();
    }

    public void NotifyChanged() => _changed();
}

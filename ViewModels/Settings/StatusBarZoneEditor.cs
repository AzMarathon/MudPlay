using System.Collections.ObjectModel;
using MudPlay.Models.Settings;
using MudPlay.Services;

namespace MudPlay.ViewModels.Settings;

// One zone of a status-bar row (left, centre or right): its items in order.
public sealed class StatusBarZoneEditor
{
    private readonly Action _changed;

    public StatusBarRowEditor Row { get; }
    public string Title { get; }
    public ObservableCollection<StatusBarEntryEditor> Entries { get; } = new();

    public StatusBarZoneEditor(StatusBarRowEditor row, string title, IEnumerable<StatusBarEntry> entries, Action changed)
    {
        Row = row;
        Title = title;
        _changed = changed;
        foreach (StatusBarEntry entry in entries)
            if (StatusBarItemCatalogue.Find(entry.Item) is { } def)
                Entries.Add(new StatusBarEntryEditor(this, def, entry.Text));
    }

    public List<StatusBarEntry> ToEntries() => Entries.Select(e => e.ToEntry()).ToList();

    // Place an item at the end of the zone.
    public void Add(StatusBarItemDef def, string? text = null)
    {
        ArgumentNullException.ThrowIfNull(def);
        Entries.Add(new StatusBarEntryEditor(this, def, text));
        _changed();
    }

    public bool CanMove(StatusBarEntryEditor entry, int by)
    {
        int from = Entries.IndexOf(entry), to = from + by;
        return from >= 0 && to >= 0 && to < Entries.Count;
    }

    public void Move(StatusBarEntryEditor entry, int by)
    {
        if (!CanMove(entry, by)) return;
        int from = Entries.IndexOf(entry);
        Entries.Move(from, from + by);
        _changed();
    }

    public void Remove(StatusBarEntryEditor entry)
    {
        if (Entries.Remove(entry)) _changed();
    }

    // Take an item out of its own zone and put it on the end of this one — another
    // zone of its row, or a zone of another row.
    public void MoveHere(StatusBarEntryEditor entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (ReferenceEquals(entry.Zone, this) || !entry.Zone.Entries.Remove(entry)) return;
        Add(entry.Def, entry.Text);
    }

    public void NotifyChanged() => _changed();
}

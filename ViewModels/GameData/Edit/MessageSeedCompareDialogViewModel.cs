using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.Import;

namespace MudPlay.ViewModels.GameData.Edit;

// Game Data Browser → Incomplete Messages → "Compare with seed…". Lists each message that
// differs from the shipped seed with the seed's record beside the user's, and lets the
// user put any of them back to the seed. Nothing changes until Apply; Cancel or the
// title-bar X discards every pick.
public sealed partial class MessageSeedCompareDialogViewModel : ObservableObject, IDialogViewModel<MessageSeedCompareResult>
{
    public event Action<MessageSeedCompareResult?>? CloseRequested;

    public ObservableCollection<MessageSeedCompareEntry> Entries { get; }

    public string Title { get; }

    public string Summary { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFields))]
    private MessageSeedCompareEntry? _selectedEntry;

    // Unchanged fields are hidden by default so the differences stand out; ticking this
    // lists them too (dimmed) for context.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFields))]
    private bool _showUnchanged;

    public IReadOnlyList<FieldDiff> SelectedFields
        => SelectedEntry is null
            ? Array.Empty<FieldDiff>()
            : SelectedEntry.Fields.Where(f => ShowUnchanged || f.Changed).ToArray();

    public MessageSeedCompareDialogViewModel(IReadOnlyList<SeedDelta<MessageRecord>.Difference> differences, string? setName)
    {
        ArgumentNullException.ThrowIfNull(differences);
        Entries = new ObservableCollection<MessageSeedCompareEntry>(
            differences.Select(d => new MessageSeedCompareEntry(d)));
        Title = string.IsNullOrWhiteSpace(setName) ? "Compare with seed" : $"Compare with seed — {setName}";
        Summary = Entries.Count == 1
            ? "1 message differs from the shipped seed. Pick what to do with it, then Apply."
            : $"{Entries.Count} messages differ from the shipped seed. Pick what to do with each, then Apply.";
        SelectedEntry = Entries.FirstOrDefault();
    }

    [RelayCommand]
    private void UseSeedForAll()
    {
        foreach (MessageSeedCompareEntry e in Entries) e.UseSeed = true;
    }

    [RelayCommand]
    private void KeepMineForAll()
    {
        foreach (MessageSeedCompareEntry e in Entries) e.UseSeed = false;
    }

    [RelayCommand]
    private void Apply()
    {
        SeedDelta<MessageRecord>.Difference[] useSeed = Entries.Where(e => e.UseSeed).Select(e => e.Difference).ToArray();
        CloseRequested?.Invoke(new MessageSeedCompareResult(useSeed, Entries.Count - useSeed.Length));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

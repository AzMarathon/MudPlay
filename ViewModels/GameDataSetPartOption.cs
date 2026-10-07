using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// One tick box in Manage Game Data Sets: a kind of data the source set may hold,
// with how much of it there is. A kind the source has none of can't be ticked.
public sealed partial class GameDataSetPartOption : ObservableObject
{
    public GameDataSetPart Part { get; }
    public string Label { get; }
    public string Hint { get; }

    [ObservableProperty]
    private bool _isChecked = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    [NotifyPropertyChangedFor(nameof(Amount))]
    private int _fileCount;

    // No source picked yet: nothing is known, so every kind stays tickable.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    [NotifyPropertyChangedFor(nameof(Amount))]
    private bool _sourceKnown;

    public bool IsAvailable => !SourceKnown || FileCount > 0;

    public string Amount => !SourceKnown ? string.Empty
        : FileCount == 0 ? "none in this set"
        : FileCount == 1 ? "1 file"
        : $"{FileCount} files";

    public GameDataSetPartOption(GameDataSetPart part, string label, string hint)
    {
        Part = part;
        Label = label;
        Hint = hint;
    }
}

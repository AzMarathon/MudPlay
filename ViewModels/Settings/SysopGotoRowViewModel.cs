using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.Settings;

// Row view-model for one entry in the BBS section's "Sys Goto locations" editor.
// Wraps a SysopGotoLocation with INotifyPropertyChanged so the table cells two-way
// bind cleanly; any edit dirties the parent section via onDirty. A read-only
// RoomNamePreview resolves the entered Map/Room live (like FavoriteEditDialog) so a
// typo'd coordinate is obvious at a glance.
//
// Map/Room/MinLevel are held as STRINGS, not ints: a plain TextBox bound to an int
// throws mid-edit the instant the field is cleared ("" can't convert to Int32), so
// the cells edit text and parse to int only at the model boundary (empty / garbage
// → 0). Keeps the compact text grid without a numeric spinner per cell.
public sealed partial class SysopGotoRowViewModel : ObservableObject
{
    private readonly Action _onDirty;
    private readonly Func<int, int, string?> _resolveName;

    // The board keyword, sent verbatim after `sys goto`.
    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomNamePreview))]
    private string _map = "0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoomNamePreview))]
    private string _room = "0";

    // 0 = ungated; otherwise the client won't route through / firing is discouraged
    // below this level.
    [ObservableProperty] private string _minLevel = "0";

    // Live "→ Room Name" confirmation for the entered landing coordinate.
    public string RoomNamePreview =>
        _resolveName(ParseOr0(Map), ParseOr0(Room)) is { Length: > 0 } n ? $"→ {n}" : "→ (unknown room)";

    public SysopGotoRowViewModel(Action onDirty, Func<int, int, string?> resolveName)
    {
        ArgumentNullException.ThrowIfNull(onDirty);
        ArgumentNullException.ThrowIfNull(resolveName);
        _onDirty = onDirty;
        _resolveName = resolveName;
    }

    // Parse the cell to an int, forgiving stray whitespace: strip ALL spaces first so
    // a number the user left accidental spaces in ("20 ", " 2 0") still reads as that
    // number. Empty / genuinely non-numeric ("abc") collapses to 0 (a "not set"
    // coordinate → unknown room, an ungated level) rather than throwing mid-type.
    private static int ParseOr0(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        string stripped = new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return int.TryParse(stripped, out int v) ? v : 0;
    }

    public SysopGotoLocation ToModel() => new()
    {
        Name = Name?.Trim() ?? string.Empty,
        Map = ParseOr0(Map),
        Room = ParseOr0(Room),
        MinLevel = ParseOr0(MinLevel),
    };

    public static SysopGotoRowViewModel FromModel(
        SysopGotoLocation model, Action onDirty, Func<int, int, string?> resolveName)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new SysopGotoRowViewModel(onDirty, resolveName)
        {
            Name = model.Name,
            Map = model.Map.ToString(),
            Room = model.Room.ToString(),
            MinLevel = model.MinLevel.ToString(),
        };
    }

    partial void OnNameChanged(string value) => _onDirty();
    partial void OnMapChanged(string value) => _onDirty();
    partial void OnRoomChanged(string value) => _onDirty();
    partial void OnMinLevelChanged(string value) => _onDirty();
}

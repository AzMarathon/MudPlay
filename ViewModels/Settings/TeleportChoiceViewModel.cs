using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Map;

namespace MudPlay.ViewModels.Settings;

// One tick box in Settings → Other's list of teleports automatic walks may use.
public sealed partial class TeleportChoiceViewModel : ObservableObject
{
    private readonly Action _changed;

    public TeleportChoice Choice { get; }
    public string Title { get; }
    public string Detail { get; }

    [ObservableProperty] private bool _isAllowed;

    public TeleportChoiceViewModel(TeleportChoice choice, bool allowed, Action changed)
    {
        Choice = choice;
        _changed = changed;
        _isAllowed = allowed;
        Title = choice.RoomsBeyond > 0
            ? $"{choice.Area}  ({choice.RoomsBeyond:N0} rooms reached no other way)"
            : $"{choice.Area}  (a shortcut: it can be walked to)";
        string count = choice.Exits.Count == 1 ? "1 teleport" : $"{choice.Exits.Count} teleports";
        Detail = $"{count} from {choice.From}"
            + (choice.Commands.Length > 0 ? $"  ·  {choice.Commands}" : string.Empty);
    }

    partial void OnIsAllowedChanged(bool value) => _changed();
}

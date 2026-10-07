using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Map;

namespace MudPlay.ViewModels.Settings;

// One tick box in Settings → Teleports' list of teleports automatic walks may use.
public sealed partial class TeleportChoiceViewModel : ObservableObject
{
    private readonly Action _changed;

    public TeleportChoice Choice { get; }
    public string Title { get; }
    public string Detail { get; }

    [ObservableProperty] private bool _isAllowed;

    // Hidden by the list's filter box.
    [ObservableProperty] private bool _isShown = true;

    public bool Matches(string filter) =>
        filter.Length == 0
        || Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Detail.Contains(filter, StringComparison.OrdinalIgnoreCase);

    public TeleportChoiceViewModel(TeleportChoice choice, bool allowed, Action changed)
    {
        Choice = choice;
        _changed = changed;
        _isAllowed = allowed;
        // Map/room numbers beside both names: a spot the user doesn't know by
        // name can be looked up on the map.
        Title = $"{choice.FromName} ({choice.From.Map}/{choice.From.Room}) {(choice.TwoWay ? "⇄" : "→")} "
            + $"{choice.ToName} ({choice.To.Map}/{choice.To.Room})";
        string leadsTo = choice.RoomsBeyond > 0
            ? $"leads to {choice.Area}: {choice.RoomsBeyond:N0} rooms reached no other way"
            : "a shortcut: both ends can be walked to";
        Detail = (choice.Commands.Length > 0 ? $"{choice.Commands}  ·  " : string.Empty) + leadsTo;
    }

    partial void OnIsAllowedChanged(bool value) => _changed();
}

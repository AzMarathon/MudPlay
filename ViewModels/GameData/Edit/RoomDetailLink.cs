using System.Windows.Input;
using Avalonia.Media;

namespace MudPlay.ViewModels.GameData.Edit;

// One clickable row in the room-detail popup — a monster name (click opens the
// monster's Game Data record) or an exit destination (click centres the
// Navigation map on that room). Detail is an optional muted suffix (a monster
// note like "placed", or an exit hint like "Door" / "Trap: 40 dmg").
public sealed class RoomDetailLink
{
    public string Text { get; }
    public string? Detail { get; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public ICommand Open { get; }

    // Optional per-link text colour, overriding whatever the template's style
    // class sets. Used by the route-details window to tint a monster by its
    // alignment (evil red / neutral cyan / good white). Null leaves the class
    // colour in force (every other consumer).
    public IBrush? Accent { get; init; }

    public RoomDetailLink(string text, string? detail, ICommand open)
    {
        Text = text;
        Detail = detail;
        Open = open;
    }
}

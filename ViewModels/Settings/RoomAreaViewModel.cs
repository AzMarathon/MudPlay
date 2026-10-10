using System.Globalization;
using MudPlay.Game.Map;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.Settings;

// The rooms of one map that share a name, under a spell picked in Settings →
// Periodic Damage Room Spells. Lines holds their links a few to a line, so the list
// that shows them lays out only the lines in view.
public sealed class RoomAreaViewModel
{
    private readonly IReadOnlyList<RoomKey> _rooms;
    private readonly int _perLine;
    private IReadOnlyList<IReadOnlyList<RoomLink>>? _lines;

    public string Label { get; }

    public RoomAreaViewModel(int map, string name, IReadOnlyList<RoomKey> rooms, int perLine)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        _rooms = rooms;
        _perLine = perLine;
        Label = $"Map {map.ToString(CultureInfo.InvariantCulture)} · {name}  ({rooms.Count.ToString("N0", CultureInfo.InvariantCulture)})";
    }

    // Each link opens the Navigation map on its room, as the Game Data room chips do.
    public IReadOnlyList<IReadOnlyList<RoomLink>> Lines => _lines ??= _rooms
        .Select(static k => new RoomLink(
            $"{k.Map.ToString(CultureInfo.InvariantCulture)}/{k.Room.ToString(CultureInfo.InvariantCulture)}", k))
        .Chunk(_perLine)
        .Select(static line => (IReadOnlyList<RoomLink>)line)
        .ToList();
}

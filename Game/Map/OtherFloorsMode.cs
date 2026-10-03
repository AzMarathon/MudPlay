namespace MudPlay.Game.Map;

// Which of the floors reached by up/down exits the Navigation map draws dimmed
// around the one shown. The Other floors chip cycles through these in order:
// Both -> Up -> Down -> Off -> Both. Serialized by name (JsonStringEnumConverter),
// so the declaration order is free to change without breaking saved settings.
public enum OtherFloorsMode
{
    Both,

    // Only floors above the one shown.
    Up,

    // Only floors below the one shown.
    Down,

    Off,
}

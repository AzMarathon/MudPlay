namespace MudPlay.Game.Map;

// How a user-started walk-to, loop or Auto-Lair sets out. CombatOff turns Auto-Combat
// off for the trip there; Sprint turns Sprint Mode on. Either ends once the walk-to
// arrives, the loop begins its circuit, or the Auto-Lair reaches its first lair.
public enum RunStartMode
{
    Normal,
    CombatOff,
    Sprint,
}

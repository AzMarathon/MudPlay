namespace MudPlay.Game;

// The character's `set follow` mode, as the game last stated it.
public enum FollowMode
{
    // Nothing has said yet this session.
    Unknown,
    // A follow move prints the room, as a move of our own does.
    Normal,
    // A follow move prints only the "Following your Party leader" line.
    Blind,
}

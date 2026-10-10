namespace MudPlay.Game.Inventory;

// What printed a "You notice <list> here." line. The two lists are different things:
// a room display lists the visible floor, and the reply to a room search lists only
// the hidden stacks that search found, each under the hidden stack's own count
// (GAME_MECHANICS "Hiding items in a room (stashing)"). The same item can be in both.
public enum FloorSurveySource
{
    // No command echo has been read this session (a statline the client can't
    // split), so the line can't be told apart. Callers keep their older reading.
    Unknown,

    // The line did not follow the echo of a room search: a walk-in, a look.
    RoomDisplay,

    // The line directly followed the echo of a bare `search`.
    SearchReply,
}

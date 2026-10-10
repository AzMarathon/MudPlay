namespace MudPlay.Game.Inventory;

// What printed a "You notice <list> here." line. The two lists are different things:
// a room display lists the visible floor, and the reply to a room search lists only
// the hidden stacks that search found, each under the hidden stack's own count
// (GAME_MECHANICS "Hiding items in a room (stashing)"). The same item can be in both.
// Each is named only on proof; a list with neither proof is Unknown, and a caller
// that cares has to decide from what it was doing at the time.
public enum FloorSurveySource
{
    // Neither proof: no search echo directly ahead of the list, and no room name
    // ahead of it either.
    Unknown,

    // A room's name went by ahead of the list with no prompt between: a walk-in, a
    // look, a redisplay.
    RoomDisplay,

    // The list directly followed the echo of a bare `search`.
    SearchReply,
}

using MudPlay.Models.GameData;

namespace MudPlay.Models.Profile;

// Settings → PvP, stored in the character profile's "Pvp" section. Only read on a
// realm whose "PvP is enabled on this realm" is ticked.
public sealed class PvpSettings
{
    // What to do about an Enemy who is in the room or attacks us. A player's own
    // PvP response (Game Data → Players) replaces it for that player.
    public PvpAction Action { get; set; } = PvpAction.DoNothing;

    // Where a flee goes. Null runs back along the walk or loop instead, RoomsToFlee
    // rooms.
    public RoomRef? FleeTo { get; set; }

    public int RoomsToFlee { get; set; } = 10;

    // "Flee then hang up": seconds between the flee starting and the hang-up.
    public int FleeHangupDelaySeconds { get; set; } = 30;

    // "Flee (come back later)": seconds to stay away once the flee has landed before
    // the interrupted walk or loop is picked up again.
    public int ComeBackAfterSeconds { get; set; } = 60;

    // Tell the gang channel who it was and where, once per encounter.
    public bool NotifyGang { get; set; }

    // Dial back in after a PvP hang-up, this many minutes later.
    public bool ReconnectAfterPvp { get; set; }
    public int ReconnectAfterPvpMinutes { get; set; } = 30;

    // A Friend who attacks us is marked Enemy, the way a Neutral who does is. Off,
    // a Friend stays a Friend whatever they do and is never answered.
    public bool FlipFriendToEnemyIfAttacked { get; set; }
}

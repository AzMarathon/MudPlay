namespace MudPlay.Models.Profile;

// Settings → PvP, stored in the character profile's "Pvp" section. Only read on a
// realm whose "PvP is enabled on this realm" is ticked.
public sealed class PvpSettings
{
    // A Friend who attacks us is marked Enemy, the way a Neutral who does is. Off,
    // a Friend stays a Friend whatever they do and is never answered.
    public bool FlipFriendToEnemyIfAttacked { get; set; }
}

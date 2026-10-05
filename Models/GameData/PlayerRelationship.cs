namespace MudPlay.Models.GameData;

// How we stand with another player on a realm, set by the user on the player's
// record and shared by every character we play there.
//   Neutral — the default for everyone: not attacked on sight, fought back if they
//     attack us.
//   Friend — never attacked.
//   Enemy — the PvP settings' response applies on sight (hang up, flee or fight).
// A Neutral who attacks us is moved to Enemy and saved that way; a Friend is left
// alone unless the PvP settings say to move them too.
public enum PlayerRelationship
{
    Neutral = 0,
    Friend  = 1,
    Enemy   = 2,
}

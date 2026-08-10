using System.Text.Json.Serialization;

namespace MudPlay.Game.Leaderboard;

// One row of a captured "Top Heroes of the Realm" (top N) listing: the player's
// rank, full name, class, gang/guild affiliation, and total experience.
//
// Characters are tracked across captures by FIRST name, not full name — a player
// can change their last name between listings but not their first, so the first
// token is the stable identity. Class is the tiebreaker / reroll signal: a first
// name that reappears under a different class is a different character (a reroll
// reusing the name).
public sealed record LeaderboardEntry(
    int Rank,
    string Name,
    string Class,
    string Guild,
    long Experience)
{
    // Stable identity across captures — the last name can change, the first can't.
    [JsonIgnore]
    public string FirstName
    {
        get
        {
            int sp = Name.IndexOf(' ');
            return sp < 0 ? Name : Name[..sp];
        }
    }
}

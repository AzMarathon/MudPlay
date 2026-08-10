using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace MudPlay.Game.Leaderboard;

// One captured "top N" listing: when it was seen, how many rows the player asked
// for (the N in "top N"), and the ranked entries the server actually returned.
//
// RequestedCount vs Entries.Count is the key to distinguishing a reroll from an
// overtake at the bottom of a capped list. The server builds only as many rows
// as it has (or its cap allows): ask for top 200 on a realm capped at 100 and
// you get 100. So when the server returned FEWER rows than requested, the whole
// ranked pool is visible (IsComplete) — a name that later vanishes is genuinely
// gone. When it returned exactly as many as requested, there may be more players
// below the last shown rank, so a bottom-rank disappearance can just be someone
// unseen finally passing them.
public sealed record LeaderboardSnapshot(
    DateTimeOffset CapturedAtUtc,
    int RequestedCount,
    IReadOnlyList<LeaderboardEntry> Entries)
{
    // True when the server returned fewer rows than asked for — the request
    // out-ran the pool/cap, so every ranked hero is present and nothing sits
    // below the last row. Requires a known request count.
    [JsonIgnore]
    public bool IsComplete => RequestedCount > 0 && Entries.Count < RequestedCount;

    // Lowest experience among the shown entries — the visibility cutoff. On a
    // truncated (not complete) listing, a returning character must exceed this to
    // be shown, so it's the yardstick for "should this dropped name still appear".
    [JsonIgnore]
    public long MinShownExperience =>
        Entries.Count == 0 ? 0 : Entries.Min(e => e.Experience);
}

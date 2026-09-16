using System;
using System.Collections.Generic;
using MudPlay.Game.Map;

namespace MudPlay.Game.Tokens;

// A held token that reaches the walk-to destination faster than walking: the
// token's place, its fixed landing, the gold cost (copper) and use-level, and how
// the routes compare. The picker turns this into the (never-auto) blue token card.
public readonly record struct TokenRouteCandidate(
    string Place, RoomKey Landing, long CostCopper, int MinLevel,
    int TokenWalkSteps, int OverlandSteps, int RoomsSaved, int? Charges);

// Picks the best held token for a walk-to: the one whose landing→destination walk
// is enough rooms shorter than the overland source→destination walk. Pure — the
// caller supplies the overland length and a landing→destination distance function
// (BFS), plus the held tokens with their live charges — so it's unit-tested
// without the map. A token with a KNOWN-zero charge count is skipped (unusable);
// unknown (null, not looked yet) is kept so a not-yet-read token still surfaces.
public static class TokenRouteEvaluator
{
    public static TokenRouteCandidate? Best(
        int overlandSteps,
        IEnumerable<(TokenTeleportInfo Info, int? Charges)> heldTokens,
        Func<RoomKey, int?> stepsFromLanding,
        int minRoomsShorter)
    {
        ArgumentNullException.ThrowIfNull(heldTokens);
        ArgumentNullException.ThrowIfNull(stepsFromLanding);

        TokenRouteCandidate? best = null;
        foreach ((TokenTeleportInfo info, int? charges) in heldTokens)
        {
            if (charges is 0) continue;                              // known depleted
            if (stepsFromLanding(info.Destination) is not { } landSteps) continue;  // landing unreachable
            int saved = overlandSteps - landSteps;
            if (saved < minRoomsShorter) continue;
            if (best is null || saved > best.Value.RoomsSaved)
                best = new TokenRouteCandidate(
                    info.Place, info.Destination, info.CostCopper, info.MinLevel,
                    landSteps, overlandSteps, saved, charges);
        }
        return best;
    }
}

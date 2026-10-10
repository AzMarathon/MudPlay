using MudPlay.Game;

namespace MudPlay.Game.Map;

// Verb selection + achievability helpers for the door FSM. Captures the
// decision matrix the walker consults at door-handling time.
//
// The "unbashable strength threshold" — the highest StatRequirement a
// door can carry and still be bashable by some reachable build — is
// supplied per game-data set by MaxStrengthIndex.MaxAchievableStrength,
// which walks the Races table and the item +Strength slot matrix. Both
// decision methods take it as a parameter and fall back to
// UnbashableStrengthThreshold when no set is loaded.
public static class DoorPolicy
{
    // Fallback ceiling for "is this door bashable by anyone on this
    // realm?", used when no game-data set is loaded so MaxStrengthIndex
    // can't compute the real maximum. Doors with StatRequirement above the
    // effective ceiling are treated as bash-impossible even when the data
    // marks them (picklocks/strength).
    public const int UnbashableStrengthThreshold = 200;

    // Below this chance per try, a lock this character can only pick is a poor bet,
    // and a route goes round it when a short way round exists. At 25% a run of the
    // default ten tries (OtherSettings.MaxPickAttempts) still opens the door 94
    // times in 100; at 10% it is 65, and every run that fails has cost its ten
    // picks and then needs the way round anyway.
    public const int PoorPickChancePercent = 25;

    // How many steps longer the way round a poor-odds lock may be and still be
    // taken. Ten is the default number of tries the door would get: a detour longer
    // than that isn't clearly cheaper than trying, so the door is tried first and
    // the walk goes round only if the picks run out.
    public const int PoorPickDetourSteps = 10;

    // Chance in 100 that one `pick` opens a lock (GAME_MECHANICS "Locked doors —
    // picking, opening and bashing", read from the Stock engine): one roll under
    // Picklocks less the lock's N − 1, and an "any" lock adds to the skill instead.
    // No Picklocks never opens anything.
    public static int PickChancePercent(int statRequirement, int playerPicklocks)
    {
        if (playerPicklocks <= 0) return 0;
        return statRequirement > 0
            ? Math.Clamp(playerPicklocks - statRequirement + 1, 0, 100)
            : Math.Min(playerPicklocks + 1, 100);
    }

    // True when picking is this character's only way through the door and each try
    // has a poor chance. A door it can bash is never one: bashing has no attempt
    // cap, so it opens in the end.
    public static bool IsPoorOddsPick(
        int statRequirement, bool canBash, int playerStrength, int playerPicklocks,
        int maxBashableStrength = UnbashableStrengthThreshold) =>
        ChooseVerb(statRequirement, canBash, playerStrength, playerPicklocks,
            preferPickOverBash: false, maxBashableStrength) == "pick"
        && PickChancePercent(statRequirement, playerPicklocks) < PoorPickChancePercent;

    // Why a route keeps off this door, for the log and the bug report: "bash needs
    // 301 Strength, you have 120; pick chance 3% (Picklocks 303)". The chance is
    // left out where the realm's pick formula isn't known.
    public static string DescribeOdds(
        int statRequirement, bool canBash, int playerStrength, int playerPicklocks,
        int maxBashableStrength, bool pickChanceKnown)
    {
        string bash = !canBash ? "can't be bashed"
            : statRequirement > maxBashableStrength
                ? $"bash needs {statRequirement} Strength, more than any character can reach"
            : $"bash needs {statRequirement} Strength, you have {playerStrength}";
        string pick = pickChanceKnown
            ? $"pick chance {PickChancePercent(statRequirement, playerPicklocks)}% (Picklocks {playerPicklocks})"
            : $"pick needs {statRequirement} Picklocks, you have {playerPicklocks}";
        return $"{bash}; {pick}";
    }

    // True when the door has at least one viable opening path for the
    // current character — bash, pick, or "no req at all". Consulted by the
    // walker before sending the first verb so an impossible door fails fast
    // with a clean reason instead of burning bash/pick attempts at the
    // server. maxBashableStrength is the active set's
    // MaxAchievableStrength — the highest Strength any build can reach; a
    // door needing more than this can't be bashed by anyone.
    public static bool IsAchievable(
        int statRequirement, bool canBash, int playerStrength, int playerPicklocks,
        int maxBashableStrength = UnbashableStrengthThreshold)
    {
        if (statRequirement <= 0)
        {
            // "(Door)" / "(Door [any picklocks/strength])" — anyone
            // can open. Bash succeeds for any non-zero strength.
            return canBash || playerPicklocks > 0;
        }

        bool bashable = canBash
                     && statRequirement <= maxBashableStrength
                     && playerStrength >= statRequirement;
        bool pickable = playerPicklocks >= statRequirement;
        return bashable || pickable;
    }

    // Decide which verb ("bash" or "pick") to attempt first for a door.
    // The walker calls this once per request; the FSM may fall back to the
    // other verb on repeated failure. maxBashableStrength is the active
    // set's MaxAchievableStrength — bash is never chosen for a door needing
    // more than that. Returns "bash", "pick", or null when neither verb can
    // succeed (caller surfaces a "no viable verb" failure).
    public static string? ChooseVerb(
        int statRequirement,
        bool canBash,
        int playerStrength,
        int playerPicklocks,
        bool preferPickOverBash,
        int maxBashableStrength = UnbashableStrengthThreshold)
    {
        bool bashOk = canBash
                   && statRequirement <= maxBashableStrength
                   && (statRequirement <= 0 || playerStrength >= statRequirement);
        bool pickOk = statRequirement <= 0 || playerPicklocks >= statRequirement;

        if (preferPickOverBash)
        {
            if (pickOk) return "pick";
            if (bashOk) return "bash";
        }
        else
        {
            if (bashOk) return "bash";
            if (pickOk) return "pick";
        }
        return null;
    }
}

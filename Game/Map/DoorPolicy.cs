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

    // The pick requirement of a bare `(Key: N)` lock. The data's `[N picklocks]` is a
    // lock modifier of −(N − 1), and a bare cell is a modifier of 0: `[1 picklocks]`,
    // a pick rolled under the character's Picklocks.
    public const int BareKeyLockPickRequirement = 1;

    // What the door handler is told opens this door or keyed door besides its key: the
    // pick / bash requirement and whether a bash is open. A bare `(Key: N)` lock is
    // picked like a `[1 picklocks]` one on both exit kinds the cell may be. It is
    // bashed only where it is known to be a door or gate (BareKeyLockBook): a key
    // exit can't be bashed at all, and there `bash <dir>` is taken as an attack on
    // whatever the direction word names (GAME_MECHANICS "Locked doors — picking,
    // opening and bashing"). One not known either way goes over as pick-only; the
    // handler itself decides at the door whether a bash is safe to try.
    public static (int StatRequirement, bool CanBash) Openers(in RoomExit exit) =>
        exit.IsBareKeyLock
            ? (BareKeyLockPickRequirement, exit.BareLockBash == BareLockBash.Bashable)
            : (exit.StatRequirement, exit.CanBash);

    // What a route counts on to open it: as Openers, but a bare lock not known
    // either way counts on the bash the handler will try (user, 2026-10-18).
    public static (int StatRequirement, bool CanBash) PlannedOpeners(in RoomExit exit) =>
        exit.IsBareKeyLock
            ? (BareKeyLockPickRequirement, exit.BareLockBash != BareLockBash.NotBashable)
            : (exit.StatRequirement, exit.CanBash);

    // Chance in 100 that one `pick` opens a lock (GAME_MECHANICS "Locked doors —
    // picking, opening and bashing": read from the Stock engine, and the same on
    // Paradigm as far as the user knows): one roll under Picklocks less the lock's
    // N − 1, and an "any" lock adds to the skill instead. No Picklocks never opens
    // anything.
    public static int PickChancePercent(int statRequirement, int playerPicklocks)
    {
        if (playerPicklocks <= 0) return 0;
        return statRequirement > 0
            ? Math.Clamp(playerPicklocks - statRequirement + 1, 0, 100)
            : Math.Min(playerPicklocks + 1, 100);
    }

    // What the door handler tries, in order. The one place the order is set.
    //   - A keyless door: the preferred verb (bash, or pick with Picklocks-over-bash
    //     on), then the other one.
    //   - A keyed door — Client policy (user's rulings, 2026-10-18): a pick first when
    //     the character has Picklocks, whatever the odds and whether or not the key is
    //     carried (up to the pick-try cap); then a bash, where one is allowed; then the
    //     key, as the last resort (a floor grab when it isn't carried, then `use`).
    // A verb this character can't open the door with is left out.
    public static IReadOnlyList<DoorOpener> OpeningOrder(
        int statRequirement, bool canBash, int playerStrength, int playerPicklocks,
        bool keyed, bool preferPickOverBash,
        int maxBashableStrength = UnbashableStrengthThreshold)
    {
        bool bashOk = canBash
                   && statRequirement <= maxBashableStrength
                   && (statRequirement <= 0 || playerStrength >= statRequirement);
        bool pickOk = playerPicklocks > 0 && (statRequirement <= 0 || playerPicklocks >= statRequirement);
        List<DoorOpener> order = new(3);
        if (!keyed)
        {
            if (preferPickOverBash && pickOk) order.Add(DoorOpener.Pick);
            if (bashOk) order.Add(DoorOpener.Bash);
            if (!preferPickOverBash && pickOk) order.Add(DoorOpener.Pick);
            return order;
        }
        if (pickOk) order.Add(DoorOpener.Pick);
        if (bashOk) order.Add(DoorOpener.Bash);
        order.Add(DoorOpener.Key);
        return order;
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
    // 301 Strength, you have 120; pick chance 3% (Picklocks 303)".
    // A bare `(Key: N)` lock says what is known of its bash instead (BareLockBash).
    public static string DescribeOdds(
        int statRequirement, bool canBash, int playerStrength, int playerPicklocks,
        int maxBashableStrength, BareLockBash? bareLock = null)
    {
        string bash = bareLock switch
        {
            BareLockBash.NotBashable => "can't be bashed (a key exit)",
            BareLockBash.Unknown => $"a bash is tried at Strength {playerStrength} (not known whether it is a door)",
            BareLockBash.Bashable => $"bashes at Strength {playerStrength} in 100",
            _ => null,
        } ?? (!canBash ? "can't be bashed"
            : statRequirement > maxBashableStrength
                ? $"bash needs {statRequirement} Strength, more than any character can reach"
            : $"bash needs {statRequirement} Strength, you have {playerStrength}");
        return $"{bash}; pick chance {PickChancePercent(statRequirement, playerPicklocks)}% (Picklocks {playerPicklocks})";
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
        // No Picklocks never opens a lock, "any" lock or not (PickChancePercent).
        bool pickOk = playerPicklocks > 0 && (statRequirement <= 0 || playerPicklocks >= statRequirement);

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

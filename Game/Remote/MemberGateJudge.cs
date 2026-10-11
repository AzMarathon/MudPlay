using MudPlay.Game.Map;

namespace MudPlay.Game.Remote;

// Whether an exit that admits only some would have let one party member through,
// from what a leader's client holds about that member. A leader on a loop asks it
// when the game drops a follower behind a step through such an exit: turned away
// by the exit, the member would be left there again every lap; stopped by
// something else (a hold, their weight), they are worth going back for.
//
// Three answers. False: something the exit asks for is known to be missing. True:
// every condition the exit sets is known to be met, so the exit didn't stop them.
// Null: at least one condition can't be judged (a level never probed, an item
// nobody was asked about, an alignment window), and none is known to fail.
public static class MemberGateJudge
{
    // What is known of the member. Each is null when it isn't known. CopiesHeld
    // answers for one item id: the copies they are remembered to hold, or null.
    public readonly record struct Facts(
        int? Level = null,
        int? ClassNumber = null,
        int? RaceNumber = null,
        long? PurseCopper = null,
        Func<int, int?>? CopiesHeld = null);

    public static bool? CanPass(in RoomExit exit, in Facts member)
    {
        bool unknown = false;
        foreach (bool? met in Conditions(exit, member))
        {
            if (met == false) return false;
            if (met is null) unknown = true;
        }
        return unknown ? null : true;
    }

    private static IEnumerable<bool?> Conditions(RoomExit exit, Facts member)
    {
        if (exit.HasLevelGate)
            yield return member.Level is { } level
                ? (exit.MinLevel <= 0 || level >= exit.MinLevel) && (exit.MaxLevel <= 0 || level <= exit.MaxLevel)
                : null;
        if (exit.HasClassGate)
            yield return member.ClassNumber is { } klass ? klass == exit.ClassGate : null;
        if (exit.HasRaceGate)
            yield return member.RaceNumber is { } race ? race == exit.RaceGate : null;
        // Nobody reports their alignment, so a window on it can't be judged.
        if (exit.HasAlignmentGate)
            yield return null;
        // Gold on a toll exit, copper on a fare; a hundred copper to the gold.
        long costCopper = (exit.Hint == RoomExitHint.Toll ? Math.Max(0, exit.TollGold) * 100L : 0)
            + Math.Max(0, exit.FareCopper);
        if (costCopper > 0)
            yield return member.PurseCopper is { } purse ? purse >= costCopper : null;
        foreach (int itemId in ExitGateItems.Of(exit))
            yield return member.CopiesHeld?.Invoke(itemId) is { } copies ? copies > 0 : null;
    }
}

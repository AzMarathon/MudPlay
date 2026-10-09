namespace MudPlay.Game.Train;

// Decides whether a plan row was really applied, from what the game showed
// afterwards rather than from the keystrokes sent. A row cleared on keystrokes
// alone was lost whenever the form took none of them: the plan read the stats off
// a buffed `stat` screen, typed nothing, and the row was deleted with the CP
// unspent (report paradigm-20260930-160602).
public static class CpApplyCheck
{
    // From the form itself, read before the first keystroke and again before the
    // closing SAVE. The row is applied when the form shows every one of its stats
    // at or past the row's value, whoever put them there.
    public static CpApplyOutcome FromForm(TrainStatsScreen before, TrainStatsScreen after, int[] rowTargets)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(rowTargets);

        bool reached = true;
        for (int i = 0; i < rowTargets.Length && i < after.Stats.Length; i++)
            if (after.Stats[i] < rowTargets[i]) { reached = false; break; }
        if (reached) return CpApplyOutcome.Applied;
        return after.CpLeft < before.CpLeft ? CpApplyOutcome.Partial : CpApplyOutcome.NothingSpent;
    }

    // From the CP on the `stat` screens either side of the form, for a form that
    // couldn't be read. The row is applied only when the whole row was typed and
    // exactly its cost left the character: a stat line can be buffed, the CP can't.
    public static CpApplyOutcome FromStat(int cpBefore, int cpAfter, int expectedCost, bool wholeRowTyped)
    {
        int spent = cpBefore - cpAfter;
        if (spent <= 0) return CpApplyOutcome.NothingSpent;
        return wholeRowTyped && spent == expectedCost ? CpApplyOutcome.Applied : CpApplyOutcome.Partial;
    }
}

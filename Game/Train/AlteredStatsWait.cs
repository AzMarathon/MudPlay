namespace MudPlay.Game.Train;

// How long a train run stands at a Stock trainer waiting for an altered stat to
// clear before it gives the CP plan up for that run. Stock refuses `train stats`
// while a spell or item is on a stat (GAME_MECHANICS "Trainers: level band, class
// restriction, and `train stats`"), so the run reads `stat` again at intervals
// rather than send a command the game will bounce. The wait is the user's to set
// (AutoTrainerSettings.AlteredStatsWaitSeconds) and always bounded: a buff
// something keeps recasting would otherwise park the run for good.
//
// Kept apart from TrainerWalkManager (which needs a live map, walker and wire to
// construct) so the bound can be tested on its own.
public sealed class AlteredStatsWait
{
    private int _rereads;

    // True for the first re-read of a run, the one worth announcing.
    public bool JustBegan => _rereads == 1;

    // A new run starts its wait over.
    public void Reset() => _rereads = 0;

    // How many times `stat` is read again over a wait of waitSeconds, one read
    // every interval: enough to cover the whole wait, none for a wait of zero.
    public static int Rereads(int waitSeconds, TimeSpan interval)
    {
        if (waitSeconds <= 0 || interval <= TimeSpan.Zero) return 0;
        return (int)Math.Ceiling(waitSeconds / interval.TotalSeconds);
    }

    // True when the run may wait and read `stat` once more; false once maxRereads
    // are used up and the plan is to be left for later.
    public bool TryClaimReread(int maxRereads)
    {
        if (_rereads >= maxRereads) return false;
        _rereads++;
        return true;
    }
}

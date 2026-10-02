namespace MudPlay.Game.Train;

// What a train run does with the funding router's answer to Begin.
public enum TrainFundingHandoff
{
    // The purse covers it: no errand, the run carries on.
    Proceed,

    // An errand is collecting. The run waits in its funding phase for Finished.
    AwaitErrand,

    // Short before the trip started. Nothing is trained yet, so the run just ends.
    Abandon,

    // Short at the trainer, part-way through a run. The router is idle, so the run
    // must stop its own train loop: levels already trained still need their stat
    // refresh, CP apply and report.
    StopShort,
}

// Only Collecting hands the run to the errand. A Short answer comes back with the
// router already idle, so a run left waiting on it waits forever: no Finished is
// coming, and the armed trigger and Train Now stay blocked behind a busy run.
//
// Kept apart from TrainerWalkManager (which needs a live map, walker and wire to
// construct) so the rule can be tested on its own.
public static class TrainFundingHandoffRule
{
    // midRun: the trainer has already refused for money and the run is re-pricing
    // from the trainer's room, rather than pricing the trip before it sets out.
    public static TrainFundingHandoff For(TrainFundingStart start, bool midRun) => start switch
    {
        TrainFundingStart.Funded => TrainFundingHandoff.Proceed,
        TrainFundingStart.Collecting => TrainFundingHandoff.AwaitErrand,
        _ => midRun ? TrainFundingHandoff.StopShort : TrainFundingHandoff.Abandon,
    };
}

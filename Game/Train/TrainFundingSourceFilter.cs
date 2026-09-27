using System;
using System.Collections.Generic;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Train;

// Narrows the stash and bank funding sources to what the user allows auto-train to
// draw on (Settings → Auto-Trainer → "When short on cash"). The planner never sees
// an excluded source, so a run it can't fund from the allowed ones is simply short,
// stays armed and keeps looping.
public static class TrainFundingSourceFilter
{
    public static List<TrainFundingSource> Apply(
        IEnumerable<TrainFundingSource> sources, AutoTrainerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(settings);

        List<TrainFundingSource> kept = new();
        foreach (TrainFundingSource s in sources)
        {
            bool allowed = s.Kind switch
            {
                TrainFundingSourceKind.Stash => UsesStashes(settings.FundingMode)
                    && (settings.FundingStash is not { } stash
                        || (stash.Map == s.Room.Map && stash.Room == s.Room.Room)),
                TrainFundingSourceKind.Bank => UsesBanks(settings.FundingMode)
                    && (string.IsNullOrWhiteSpace(settings.FundingBank)
                        || string.Equals(settings.FundingBank, s.Name, StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
            if (allowed) kept.Add(s);
        }
        return kept;
    }

    // Whether a bank may fund a run. When it can't, there's no point asking `bank`
    // for balances first.
    public static bool UsesBanks(TrainFundingMode mode) =>
        mode is TrainFundingMode.StashAndBank or TrainFundingMode.BankOnly;

    public static bool UsesStashes(TrainFundingMode mode) =>
        mode is TrainFundingMode.StashAndBank or TrainFundingMode.StashOnly;
}

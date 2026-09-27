using MudPlay.Models.Profile;

namespace MudPlay.Models.GameData;

// One check in a State-triggered event: Stat, compared by Comparison, with Value.
// Money is entered as an amount of Denomination (5 Platinum), the same way the
// keep-on-hand setting is; Denomination is ignored for the other stats.
public sealed class EventCondition
{
    public EventConditionStat Stat { get; set; }
    public EventComparison Comparison { get; set; }
    public long Value { get; set; }
    public CoinDenomination Denomination { get; set; } = CoinDenomination.Copper;
}

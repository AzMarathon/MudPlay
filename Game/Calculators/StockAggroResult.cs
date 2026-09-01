using System.Collections.Generic;

namespace MudPlay.Game.Calculators;

// One member's Stock aggro outcome: whether the monster is aggroed onto them (with
// a short reason), and — for aggroed members — their chance of being THIS beat's
// spread target on a fresh unlocked pick (0-100%). Non-aggroed members carry 0%.
public sealed record StockAggroMemberResult(
    string Name, bool Aggroed, string Reason, double SpreadPercent);

// The whole party's Stock result: per-member outcomes, how many the mob opens on,
// and the monster-level Follow% stickiness readout.
public sealed record StockAggroResult(
    IReadOnlyList<StockAggroMemberResult> Members,
    int AggroedCount,
    string Stickiness);

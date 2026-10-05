using MudPlay.Game.Map;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Cash;

// Where a running stash transfer stands, for the Navigation chip's tooltip.
//
// PileRead is false until this transfer has searched the stash; LeftCopper is then
// only the ledger's belief. Once read, LeftCopper and LeftCoins are what the last
// search showed less what this trip took (the coin is searched, taken, then banked,
// so the pile already excludes the load on its way to the bank). LeftCoins is empty
// when the coin wasn't read by type.
//
// TripsToGo counts the trips after the one under way, at the rate the last trip
// emptied the pile; null until a trip has taken something. Eta is rough: those
// trips at the pace of the last full one (or of the walk there and back, before one
// has been timed), plus the leg still to walk with the current load.
public sealed record StashTransferProgress(
    RoomKey Stash,
    string BankName,
    string Doing,
    bool PileRead,
    long LeftCopper,
    IReadOnlyDictionary<CoinDenomination, long> LeftCoins,
    int Trips,
    long MovedCopper,
    int? TripsToGo,
    TimeSpan? Eta)
{
    // The tooltip text, one fact per line. runicName is the board's name for the top
    // coin.
    public string Describe(string? runicName = null)
    {
        List<string> lines = new() { $"Stash {Stash.Map}/{Stash.Room} to {BankName} — {Doing}" };

        if (!PileRead)
        {
            lines.Add(LeftCopper > 0
                ? $"Last known in the stash: {CurrencyFormat.Full(LeftCopper, runicName)} (not searched yet on this transfer)"
                : "The stash hasn't been searched yet on this transfer");
            lines.Add("Trips and time are worked out once it has been searched");
        }
        else if (LeftCopper <= 0)
        {
            lines.Add("Nothing left in the stash — this is the last trip");
        }
        else
        {
            lines.Add($"Left in the stash: {CurrencyFormat.Full(LeftCopper, runicName)}");
            if (CoinList(runicName) is { Length: > 0 } coins) lines.Add($"As coin: {coins}");
            if (TripsToGo is { } trips)
                lines.Add($"About {trips} more trip{(trips == 1 ? "" : "s")} to empty it"
                    + (Eta is { } eta ? $", roughly {RouteEtaEstimator.FormatCompact(RoundUp(eta))}" : ""));
        }

        if (Trips > 0)
            lines.Add(MovedCopper > 0
                ? $"Banked so far: {CurrencyFormat.Full(MovedCopper, runicName)} in {Trips} trip{(Trips == 1 ? "" : "s")}"
                : $"Trip {Trips} under way, nothing banked yet");
        return string.Join("\n", lines);
    }

    // The coin left, dearest first: "44 platinum, 41 gold, 8 silver".
    private string CoinList(string? runicName)
    {
        List<string> parts = new();
        foreach (CoinDenomination coin in Enum.GetValues<CoinDenomination>().Reverse())
            if (LeftCoins.TryGetValue(coin, out long count) && count > 0)
                parts.Add($"{count:N0} {(coin == CoinDenomination.Runic && !string.IsNullOrWhiteSpace(runicName) ? runicName : coin.ToString().ToLowerInvariant())}");
        return string.Join(", ", parts);
    }

    // A rough figure reads better in whole minutes once it passes one.
    private static TimeSpan RoundUp(TimeSpan eta) =>
        eta.TotalSeconds <= 60 ? eta : TimeSpan.FromMinutes(Math.Ceiling(eta.TotalMinutes));
}

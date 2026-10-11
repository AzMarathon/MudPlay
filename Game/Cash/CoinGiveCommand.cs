namespace MudPlay.Game.Cash;

// The command that hands coins of one denomination to another player:
// `give {amount} {currency} to {someone}` (GAME_MECHANICS "Giving items and coins
// to another player"). One place for it, so every hand-over names the fifth coin
// the way the board does.
public static class CoinGiveCommand
{
    // currency is a CurrencyHoldings ladder word (copper, silver, gold, platinum,
    // runic); runicName is the board's own word for the fifth coin.
    public static string For(string currency, long count, string recipient, string runicName) =>
        $"give {count} {(currency == "runic" ? runicName : currency)} to {recipient}";
}

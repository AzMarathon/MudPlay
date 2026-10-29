using System.Text.Json;

namespace MudPlay.Game.Inventory;

public sealed partial class AutoBuyManager
{
    // How long a `list` waits for the server's stock before it is read from the
    // text table instead. The server answers a request at once; a request that
    // went out on a dropping link, or an answer lost to a reset, never comes.
    public static readonly TimeSpan GmcpShopWait = TimeSpan.FromSeconds(3);

    public Func<bool>? GmcpOwnsShop { get; set; }
    public Action? RequestGmcpShop { get; set; }

    private bool _awaitingGmcpList;
    private IDisposable? _gmcpShopWait;
    // The text table of the read the server's list is awaited for, once it has
    // ended: the fallback when the server's list doesn't come.
    private IReadOnlyList<ShopListParser.StockRow>? _heldTextStock;
    private IReadOnlyList<ShopListParser.StockRow> _gmcpStock = Array.Empty<ShopListParser.StockRow>();

    // A `list` read began while the server owns the shop: its stock comes from a
    // fresh server list, asked for now.
    private void BeginGmcpShopRead()
    {
        EndGmcpShopWait();
        _awaitingGmcpList = true;
        _gmcpShopWait = _schedule(GmcpShopWait, () => FallBackToTextShop($"no server shop list within {GmcpShopWait.TotalSeconds:0} s"));
        RequestGmcpShop?.Invoke();
    }

    // The server's list can't answer this read (the request failed, or the server
    // no longer owns the shop): the text table answers it instead, now if it has
    // ended, or when it does.
    public void FallBackToTextShop(string why)
    {
        if (!_awaitingGmcpList) return;
        IReadOnlyList<ShopListParser.StockRow>? held = _heldTextStock;
        EndGmcpShopWait();
        _log?.Info(LogCategory, $"{why} — reading the shop's text list");
        if (held is not null) ListStock(held);
    }

    private void EndGmcpShopWait()
    {
        _awaitingGmcpList = false;
        _gmcpShopWait?.Dispose();
        _gmcpShopWait = null;
        _heldTextStock = null;
    }

    public void ApplyGmcpShop(JsonElement? value)
    {
        if (value is null)
        {
            _gmcpStock = Array.Empty<ShopListParser.StockRow>();
            FallBackToTextShop("the server no longer reports the shop");
            return;
        }

        List<ShopListParser.StockRow> rows = new();
        foreach (JsonElement item in value.Value.GetProperty("items").EnumerateArray())
        {
            bool usable = item.GetProperty("canUse").GetBoolean();
            bool forLevel = item.GetProperty("cannotUseBecause").EnumerateArray().Any(r => r.GetString() == "level");
            JsonElement price = item.GetProperty("price");
            string currency = price.GetProperty("currency").GetString() switch
            {
                "gold" => "gold crowns",
                "silver" => "silver nobles",
                "platinum" => "platinum pieces",
                "runic" => "runic coins",
                _ => "copper farthings",
            };
            rows.Add(new(
                item.GetProperty("name").GetString()!,
                item.GetProperty("stock").GetInt32(),
                $"{price.GetProperty("amount").GetInt32()} {currency}",
                !usable && !forLevel,
                !usable && forLevel));
        }
        _gmcpStock = rows;
        if (value.Value.GetProperty("shop").ValueKind == JsonValueKind.Null) DropQueue("server reports no shop here");

        // An arrival supplies stock, but only an explicit list read may start shopping.
        if (!_awaitingGmcpList) return;
        EndGmcpShopWait();
        // The text table still being read answers nothing now.
        _capturing = false;
        _body.Clear();
        ListStock(_gmcpStock);
    }
}

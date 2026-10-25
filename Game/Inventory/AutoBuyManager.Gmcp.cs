using System.Text.Json;

namespace MudPlay.Game.Inventory;

public sealed partial class AutoBuyManager
{
    public Func<bool>? GmcpOwnsShop { get; set; }
    public Action? RequestGmcpShop { get; set; }
    private bool _awaitingGmcpList;
    private IReadOnlyList<ShopListParser.StockRow> _gmcpStock = Array.Empty<ShopListParser.StockRow>();

    public void CancelGmcpRead()
    {
        _awaitingGmcpList = false; _capturing = false; _body.Clear();
        DropQueue("server shop read ended");
    }

    public void ApplyGmcpShop(JsonElement? value)
    {
        if (value is null)
        {
            _gmcpStock = Array.Empty<ShopListParser.StockRow>();
            CancelGmcpRead();
            return;
        }
        List<ShopListParser.StockRow> rows = new();
        foreach (JsonElement item in value.Value.GetProperty("items").EnumerateArray())
        {
            bool usable = item.GetProperty("canUse").GetBoolean();
            bool level = item.GetProperty("cannotUseBecause").EnumerateArray().Any(r => r.GetString() == "level");
            JsonElement price = item.GetProperty("price");
            string currency = price.GetProperty("currency").GetString() switch
            { "gold" => "gold crowns", "silver" => "silver nobles", "platinum" => "platinum pieces", "runic" => "runic coins", _ => "copper farthings" };
            rows.Add(new(item.GetProperty("name").GetString()!, item.GetProperty("stock").GetInt32(),
                $"{price.GetProperty("amount").GetInt32()} {currency}", !usable && !level, !usable && level));
        }
        _gmcpStock = rows;
        if (value.Value.GetProperty("shop").ValueKind == JsonValueKind.Null) DropQueue("server reports no shop here");
        // An arrival supplies stock, but only an explicit list read may start shopping.
        if (_awaitingGmcpList) { _awaitingGmcpList = false; FinishCapture(); }
    }
}

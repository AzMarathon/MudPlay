using System.Text.Json;

namespace MudPlay.Game.Inventory;

public sealed partial class InventoryManager
{
    public Func<bool>? GmcpOwnsItems { get; set; }
    public Func<int, bool>? ServerItemIsKey { get; set; }
    public event Action<InventorySnapshot>? TextInventoryObserved;

    public void ApplyGmcpItems(JsonElement value, bool fullSnapshot = true)
    {
        JsonElement coins = value.GetProperty("coins");
        List<string> carried = new(); List<string> keys = new(); List<EquippedItem> worn = new();
        foreach (JsonElement item in value.GetProperty("inv").EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            if (_isKey?.Invoke(name) == true || ServerItemIsKey?.Invoke(item.GetProperty("num").GetInt32()) == true) keys.Add(name); else carried.Add(name);
        }
        foreach (JsonElement item in value.GetProperty("worn").EnumerateArray())
            worn.Add(new(item.GetProperty("name").GetString()!, ServerSlot(item.GetProperty("slot").GetString()!)));
        lock (_lock)
        {
            _copper = coins.GetProperty("copper").GetInt32(); _silver = coins.GetProperty("silver").GetInt32();
            _gold = coins.GetProperty("gold").GetInt32(); _platinum = coins.GetProperty("platinum").GetInt32(); _runic = coins.GetProperty("runic").GetInt32();
            _wealthCopper = ComputeWealth(_copper, _silver, _gold, _platinum, _runic);
            if (_readiedLight is { } light)
            {
                int index = carried.FindIndex(name => name.Equals(light.Name, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) carried.RemoveAt(index);
                else _readiedLight = null;
            }
            _pendingDepositCopper = 0; _equipped = worn; _carried = NormalizeStacks(carried); _keys = keys;
            _loaded = true; _lastUpdated = DateTimeOffset.Now;
        }
        _log?.Debug(LogCategory, $"server inventory: {carried.Count} carried, {worn.Count} worn, {keys.Count} keys");
        Changed?.Invoke(); if (fullSnapshot) FullInventoryParsed?.Invoke();
    }

    public void ResetGmcpItems()
    {
        lock (_lock)
        {
            _copper = _silver = _gold = _platinum = _runic = 0; _wealthCopper = 0; _pendingDepositCopper = 0;
            _equipped = Array.Empty<EquippedItem>(); _carried = Array.Empty<string>(); _keys = Array.Empty<string>(); _loaded = false;
            _readiedLight = null; _litSinceRead = null; _curWeight = _maxWeight = _percentage = 0; _category = EncumbranceLevel.Unknown;
        }
        Changed?.Invoke();
    }

    private static string ServerSlot(string slot) => slot switch
    {
        "weapon" => "Weapon Hand", "off-hand" => "Off-Hand",
        _ => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slot)
    };
}

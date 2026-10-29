using System.Text.Json;
using MudPlay.Game.Inventory;
using MudPlay.Game.Spells;

namespace MudPlay.Game.Gmcp;

public sealed partial class GmcpIntegration
{
    private InventoryManager? _inventory;
    private AutoBuyManager? _shop;
    private SpellbookState? _book;
    private PartyManager? _party;
    private ChatRouter? _chat;
    private AbilBreakdownParser? _abilities;
    private readonly HashSet<int> _requestedCards = new();
    private DateTime _effectsObserved;
    private bool _itemsApplied, _groupApplied, _shopApplied, _spellsApplied, _abilitiesApplied;
    private int _m2Generation;
    private readonly Queue<ChatLogEntry> _shadowText = new();
    private readonly Queue<JsonElement> _shadowEvents = new();

    public void BindMilestone2(InventoryManager inventory, AutoBuyManager shop, SpellbookState book, SpellListParser spellParser,
        PartyManager party, ChatRouter chat, AbilBreakdownParser abilities, CastingDirector director)
    {
        (_inventory, _shop, _book, _party, _chat, _abilities) = (inventory, shop, book, party, chat, abilities);
        inventory.GmcpOwnsItems = () => State.Owns("Anomaly.Items.List");
        inventory.ServerItemIsKey = num => State.ItemCards.TryGetValue(num, out JsonElement card)
            && card.TryGetProperty("type", out JsonElement type) && type.GetString() == "key";
        spellParser.GmcpOwnsSpells = () => State.Owns("Anomaly.Spells.List");
        party.GmcpOwnsGroup = () => State.Owns("Anomaly.Group.Members");
        abilities.GmcpOwnsAbilities = () => State.Owns("Anomaly.Char.Abilities");
        chat.GmcpOwnsChannel = channel => State.OwnsCommunication && ChatRouter.HasGmcpChannel(channel);
        chat.LocalName = () => party.LocalCharacterName;
        chat.TextClassified += ObserveChat;
        inventory.TextInventoryObserved += ObserveInventory;
        spellParser.TextSpellsObserved += ObserveSpells;
        abilities.BreakdownParsed += ObserveAbility;
        shop.GmcpOwnsShop = () => State.Owns("Anomaly.Shop.List");
        shop.RequestGmcpShop = () => _ = SendRequestAsync("Anomaly.Shop.List");
        director.GmcpOwnsEffects = () => State.Owns("Anomaly.Char.Effects");
        director.GmcpSelfEffect = SelfEffect;
        director.GmcpTimers = EffectTimers;
    }

    public Task RequestItemDetailAsync(int num) => RequestItemAsync("Anomaly.Items.Detail", num);
    public Task RequestItemUsabilityAsync(int num) => RequestItemAsync("Anomaly.Items.Usable", num);

    private async Task RequestItemAsync(string package, int num)
    {
        if (num <= 0 || _send is null || !State.Compatible || !State.InSession) return;
        await _send("Anomaly.Request", new { package, num });
    }

    private async Task SendRequestAsync(string package, int? num = null)
    {
        try
        {
            if (_send is null || !State.Compatible || !State.InSession) return;
            if (num is { } id) await _send("Anomaly.Request", new { package, num = id });
            else await _send("Anomaly.Request", new { package });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            if (num is { } id) _requestedCards.Remove(id);
            if (package == "Anomaly.Shop.List") _shop?.CancelGmcpRead();
            _log.Warn("GMCP", $"Request {package} failed: {ex.Message}");
        }
    }

    private void RequestMissingCards()
    {
        if (!State.Compatible || !State.InSession || _send is null || State.Mode("Anomaly.Items.Detail") == GmcpMode.Off) return;
        IEnumerable<JsonElement> carried = State.Snapshot("Anomaly.Items.List") is { } inv
            ? inv.GetProperty("inv").EnumerateArray().Concat(inv.GetProperty("worn").EnumerateArray()) : Array.Empty<JsonElement>();
        IEnumerable<JsonElement> stock = State.Snapshot("Anomaly.Shop.List") is { } shop
            ? shop.GetProperty("items").EnumerateArray() : Array.Empty<JsonElement>();
        foreach (int id in carried.Concat(stock).Select(i => i.GetProperty("num").TryGetInt32(out int id) ? id : 0).Where(id => id > 0).Distinct())
            if (!State.ItemCards.ContainsKey(id) && _requestedCards.Add(id)) _ = SendRequestAsync("Anomaly.Items.Detail", id);
    }

    private void ApplyMilestone2(string package, ref string applying)
    {
        if (_m2Generation != State.SessionGeneration || !State.Compatible)
        {
            _m2Generation = State.SessionGeneration; _requestedCards.Clear(); _shadowText.Clear(); _shadowEvents.Clear();
        }
        _requestedCards.RemoveWhere(id => !State.ItemCards.ContainsKey(id) && package == "*");
        bool Apply(string name) => (package == "*" || package == name) && State.Owns(name);
        applying = "Anomaly.Items.List";
        if (_itemsApplied && !State.Owns("Anomaly.Items.List")) { _inventory?.ResetGmcpItems(); _itemsApplied = false; }
        if (Apply("Anomaly.Items.List") || package == "Anomaly.Items.Detail" && State.Owns("Anomaly.Items.List")) { _inventory?.ApplyGmcpItems(State.Snapshot("Anomaly.Items.List")!.Value, fullSnapshot: package != "Anomaly.Items.Detail"); _itemsApplied = true; }
        applying = "Anomaly.Shop.List";
        if (Apply("Anomaly.Shop.List")) { _shop?.ApplyGmcpShop(State.Snapshot("Anomaly.Shop.List")); _shopApplied = true; }
        else if (_shopApplied && !State.Owns("Anomaly.Shop.List")) { _shopApplied = false; _shop?.ApplyGmcpShop(null); }
        applying = "Anomaly.Spells.List";
        if (Apply("Anomaly.Spells.List")) { _book?.ApplyGmcpSpells(State.Snapshot("Anomaly.Spells.List")); _spellsApplied = true; }
        else if (_spellsApplied && !State.Owns("Anomaly.Spells.List")) { _spellsApplied = false; _book?.ApplyGmcpSpells(null); }
        applying = "Anomaly.Char.Abilities";
        if (Apply("Anomaly.Char.Abilities")) { _abilities?.ApplyGmcpAbilities(State.Snapshot("Anomaly.Char.Abilities")); _abilitiesApplied = true; }
        else if (_abilitiesApplied && !State.Owns("Anomaly.Char.Abilities")) { _abilitiesApplied = false; _abilities?.ApplyGmcpAbilities(null); }
        applying = "Anomaly.Group.Members";
        if (_groupApplied && !State.Owns("Anomaly.Group.Members"))
        {
            _party?.ApplyGmcpGroup(JsonSerializer.SerializeToElement(new { leader = (string?)null, members = Array.Empty<object>() })); _groupApplied = false;
        }
        if (Apply("Anomaly.Group.Members")) { _party?.ApplyGmcpGroup(State.Snapshot("Anomaly.Group.Members")!.Value); _groupApplied = true; }
        if (package == "Anomaly.Char.Effects") _effectsObserved = DateTime.UtcNow;
    }

    private (bool? Present, DateTime? Until) SelfEffect(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || State.Snapshot("Anomaly.Char.Effects") is not { } effects || _book is null) return (null, null);
        HashSet<int> ids = _book.Available.Where(s => s.Short.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase)).Select(s => s.Number).ToHashSet();
        if (ItemCastToken.TryResolve(code, _book.GetCastItems(), out ClassCastItem item) && !item.IsDraw && item.SpellNumber > 0) ids.Add(item.SpellNumber);
        if (ids.Count == 0) return (null, null);
        foreach (JsonElement effect in effects.GetProperty("effects").EnumerateArray())
        {
            int id = effect.GetProperty("spell").GetInt32();
            if (!ids.Contains(id)) continue;
            return (true, effect.GetProperty("roundsLeft").ValueKind == JsonValueKind.Null ? null
                : _effectsObserved.AddSeconds(effect.GetProperty("roundsLeft").GetInt32() * SpellCalculator.SpellRoundSecondsWallClock));
        }
        return (false, null);
    }

    private IReadOnlyList<ActiveBuffTimer> EffectTimers()
    {
        List<ActiveBuffTimer> timers = new();
        if (State.Snapshot("Anomaly.Char.Effects") is not { } effects) return timers;
        foreach (JsonElement effect in effects.GetProperty("effects").EnumerateArray())
        {
            if (effect.GetProperty("roundsLeft").ValueKind == JsonValueKind.Null) continue;
            double seconds = effect.GetProperty("roundsLeft").GetInt32() * SpellCalculator.SpellRoundSecondsWallClock;
            int id = effect.GetProperty("spell").GetInt32();
            KnownSpell spell = _book is null ? default : _book.Available.FirstOrDefault(s => s.Number == id);
            string code = spell.Number == 0 ? effect.GetProperty("name").GetString()! : spell.Short;
            timers.Add(new("", code, _effectsObserved.AddSeconds(seconds), 0, (int)Math.Clamp(seconds, 0, int.MaxValue)));
        }
        return timers;
    }

    private void OnCommunication(JsonElement value)
    {
        if (State.OwnsCommunication) _chat?.ApplyGmcpChat(value);
        else if (State.Mode("Anomaly.Comm.Line") == GmcpMode.Shadow)
        { _shadowEvents.Enqueue(value); CompareChat(); }
    }

    private void ObserveChat(ChatLogEntry value)
    {
        if (State.Mode("Anomaly.Comm.Line") != GmcpMode.Shadow || !State.Compatible || !State.InSession || !ChatRouter.HasGmcpChannel(value.Channel) || value.DirectedTo is not null) return;
        _shadowText.Enqueue(value); CompareChat();
    }

    private void CompareChat()
    {
        while (_shadowText.Count > 0 && _shadowEvents.Count > 0)
        {
            ChatLogEntry text = _shadowText.Dequeue(); JsonElement packet = _shadowEvents.Dequeue();
            Compare("Comm.Line.text", packet.GetProperty("text").GetString(), text.Message);
        }
        while (_shadowText.Count > 64) _shadowText.Dequeue();
        while (_shadowEvents.Count > 64) _shadowEvents.Dequeue();
    }

    public string Describe()
    {
        string modes = string.Join(", ", Packages.Select(p => $"{p}={State.Mode(p)}"));
        return $"Contract: {State.ServerContract ?? "none"}; boot: {State.Boot ?? "none"}; session: {State.InSession}; room: {State.CurrentRoom ?? "none"}\n"
            + $"Item cards: {State.ItemCards.Count}; current usability responses: {State.ItemUsability.Count}; communication authority: {State.OwnsCommunication}\n{modes}";
    }

    private void ObserveSpells(IReadOnlyList<string> names)
    {
        if (State.Mode("Anomaly.Spells.List") != GmcpMode.Shadow || State.Snapshot("Anomaly.Spells.List") is not { } value) return;
        string Canonical(IEnumerable<string> entries) => string.Join("|", entries.Select(s => s.ToLowerInvariant()).Order(StringComparer.Ordinal));
        Compare("Spells.List.names", Canonical(value.GetProperty("spells").EnumerateArray().Select(s => s.GetProperty("name").GetString()!)), Canonical(names));
    }

    private void ObserveAbility(AbilBreakdown observed)
    {
        if (State.Mode("Anomaly.Char.Abilities") != GmcpMode.Shadow || State.Snapshot("Anomaly.Char.Abilities") is not { } value) return;
        foreach (JsonElement row in value.GetProperty("abilities").EnumerateArray())
            if (row.GetProperty("id").GetInt32() == observed.Code)
            {
                foreach (AbilContribution source in observed.Contributions)
                    if (row.GetProperty("sources").TryGetProperty(source.Source, out JsonElement expected) && expected.ValueKind != JsonValueKind.Null)
                        Compare($"Char.Abilities.{observed.Code}.{source.Source}", expected.GetInt32(), source.Value);
                // Text on older realms sums slices, so compare the total only for a sum ability.
                if (row.GetProperty("aggregation").GetString() == "sum") Compare($"Char.Abilities.{observed.Code}.total", row.GetProperty("total").GetInt32(), observed.Total);
            }
    }

    private void ObserveInventory(InventorySnapshot observed)
    {
        if (State.Owns("Anomaly.Items.List") && State.Snapshot("Anomaly.Items.List") is { } active)
        {
            // Text supplies the readied-light reading that the item snapshot omits.
            _inventory?.ApplyGmcpItems(active, fullSnapshot: false);
            return;
        }
        if (State.Mode("Anomaly.Items.List") != GmcpMode.Shadow || State.Snapshot("Anomaly.Items.List") is not { } value) return;
        JsonElement coins = value.GetProperty("coins");
        long expected = coins.EnumerateObject().Sum(coin => CurrencyHoldings.ToCopper(coin.Name, coin.Value.GetInt64()));
        Compare("Items.List.wealth", expected, observed.Currency.TotalCopperValue);
    }
}

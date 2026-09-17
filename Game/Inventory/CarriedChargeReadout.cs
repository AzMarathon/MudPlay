using System;
using System.Collections.Generic;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// The one realm-aware "how many charges does this carried item have" lookup, shared by
// the Character Info panel's per-item readout and the @uses remote query so the two
// never drift. On Paradigm the count is the authoritative look-derived value
// (ItemChargeTracker); on stock it's max − counted-uses (ItemUseCountTracker). A
// limited-use item whose count isn't known yet reports null ("?") rather than being
// omitted, so the user can see it's charged but unread.
public sealed class CarriedChargeReadout : IDisposable
{
    private readonly GameDataCache _gameData;
    private readonly ItemChargeTracker _paraCharges;
    private readonly ItemUseCountTracker _stockCounts;
    private readonly Func<string, int> _itemNumberOf;
    private readonly Func<IReadOnlyList<string>> _held;   // carried pack + worn/wielded gear + key-ring

    // Re-raised when either underlying tracker's counts change, so a single subscriber
    // (Character Info) refreshes without knowing which realm's tracker moved.
    public event Action? Changed;

    public CarriedChargeReadout(
        GameDataCache gameData,
        ItemChargeTracker paraCharges,
        ItemUseCountTracker stockCounts,
        Func<string, int> itemNumberOf,
        Func<IReadOnlyList<string>> heldItems)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _paraCharges = paraCharges ?? throw new ArgumentNullException(nameof(paraCharges));
        _stockCounts = stockCounts ?? throw new ArgumentNullException(nameof(stockCounts));
        _itemNumberOf = itemNumberOf ?? throw new ArgumentNullException(nameof(itemNumberOf));
        _held = heldItems ?? throw new ArgumentNullException(nameof(heldItems));
        _paraCharges.Changed += RaiseChanged;
        _stockCounts.Changed += RaiseChanged;
    }

    private void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
        _paraCharges.Changed -= RaiseChanged;
        _stockCounts.Changed -= RaiseChanged;
    }

    private bool OnParadigm => _gameData.ActiveRealm == MudPlay.Game.RealmType.ParaMud;

    // Remaining charges for a carried item by name, realm-aware. Null when unknown
    // (Paradigm: never looked) or the item isn't a limited-use item.
    public int? RemainingForName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return OnParadigm ? _paraCharges.RemainingForName(name) : _stockCounts.RemainingFor(_itemNumberOf(name));
    }

    // Whether a carried item is a limited-use (charged) item per the active set.
    public bool IsLimitedUse(string name)
        => !string.IsNullOrWhiteSpace(name)
           && ItemChargeMeta.Read(_gameData, _itemNumberOf(name)) is { IsLimitedUse: true };

    public readonly record struct ChargedItem(string Name, int? Remaining);

    // Every held charged item (carried or worn) with its remaining charges (null =
    // unread), one row per distinct item name. Includes items known to be charged even
    // when the count isn't read yet.
    public IReadOnlyList<ChargedItem> AllCharged()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ChargedItem>();
        foreach (string name in _held())
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) continue;
            int? remaining = RemainingForName(name);
            if (remaining is not null || IsLimitedUse(name))
                list.Add(new ChargedItem(name, remaining));
        }
        return list;
    }

    // Best-match a query to a held item name (carried or worn) — exact (case-insensitive),
    // else the first name the query is a prefix of, else the first that contains it. The
    // loose resolution the game does for a partial. Null when nothing matches.
    public string? ResolveCarried(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        string q = query.Trim().ToLowerInvariant();
        IReadOnlyList<string> held = _held();
        foreach (string name in held)
            if (!string.IsNullOrWhiteSpace(name) && name.ToLowerInvariant() == q) return name;
        foreach (string name in held)
            if (!string.IsNullOrWhiteSpace(name) && name.ToLowerInvariant().StartsWith(q, StringComparison.Ordinal)) return name;
        foreach (string name in held)
            if (!string.IsNullOrWhiteSpace(name) && name.ToLowerInvariant().Contains(q)) return name;
        return null;
    }
}

using System;
using System.Collections.Generic;
using MudPlay.Game.Map;          // BossTimerMath
using MudPlay.Models.Profile;
using MudPlay.Services;          // GameDataCache, ProfileService, LogService, BossCleanupConfig

namespace MudPlay.Game.Inventory;

// Stock-realm charge tracking for limited-use items. Stock prints no "Uses remaining"
// on look (that's a Paradigm feature — handled by ItemChargeTracker), so the client
// counts the uses it SENDS and derives remaining = max − used, with max from
// ItemChargeMeta (UseCount). Counts persist on the character profile
// (CharacterProfile.ItemUseCounts, keyed by item number):
//   • a RECHARGEABLE item (Retain After Uses) restocks to max at the BBS cleanup time,
//     so a used-count recorded before the last cleanup reads as 0 (reusing the boss-
//     timer NextCleanup math);
//   • a FINITE item's count is permanent.
// Gated to stock so it never double-counts against the Paradigm look tracker.
public sealed class ItemUseCountTracker
{
    private readonly GameDataCache _gameData;
    private readonly Func<IReadOnlyList<string>> _held;   // carried pack + worn/wielded gear
    private readonly Func<string, int> _itemNumberOf;
    private readonly Func<bool> _onStock;
    private readonly Func<BossCleanupConfig?> _cleanupConfig;
    private readonly ProfileService _profile;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    // Fired after a use is counted (or a profile reload) so Character Info refreshes.
    public event Action? Changed;

    public ItemUseCountTracker(
        GameDataCache gameData,
        Func<IReadOnlyList<string>> heldItems,
        Func<string, int> itemNumberOf,
        Func<bool> onStock,
        Func<BossCleanupConfig?> cleanupConfig,
        ProfileService profile,
        Func<DateTimeOffset>? now = null,
        LogService? log = null)
    {
        _gameData = gameData ?? throw new ArgumentNullException(nameof(gameData));
        _held = heldItems ?? throw new ArgumentNullException(nameof(heldItems));
        _itemNumberOf = itemNumberOf ?? throw new ArgumentNullException(nameof(itemNumberOf));
        _onStock = onStock ?? throw new ArgumentNullException(nameof(onStock));
        _cleanupConfig = cleanupConfig ?? throw new ArgumentNullException(nameof(cleanupConfig));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    // Remaining charges for a held limited-use item, or null when it isn't a finite
    // limited-use item (infinite / non-charged). Recharge-adjusted.
    public int? RemainingFor(int itemNumber)
        => ItemChargeMeta.Read(_gameData, itemNumber) is { IsLimitedUse: true } meta
            ? Math.Max(0, meta.MaxUses - EffectiveUsed(itemNumber, meta))
            : null;

    // Outbound `use <item>` / `use <item> <target>` for a held limited-use item (carried
    // or worn) → count a use. Stock only — Paradigm reads charges from the look reply.
    public void ObserveOutbound(byte[] data)
    {
        if (!_onStock() || data is null || data.Length == 0) return;
        string text = System.Text.Encoding.Latin1.GetString(data);
        foreach (string raw in text.Split('\r', '\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("use ", StringComparison.OrdinalIgnoreCase)) continue;
            if (ResolveHeld(line[4..].Trim()) is not { } name) continue;
            int number = _itemNumberOf(name);
            if (number > 0) RecordUse(number);
        }
    }

    private void RecordUse(int number)
    {
        if (ItemChargeMeta.Read(_gameData, number) is not { IsLimitedUse: true } meta) return;
        if (_profile.Current is not { } prof) return;

        int used = Math.Min(meta.MaxUses, EffectiveUsed(number, meta) + 1);
        prof.ItemUseCounts ??= new Dictionary<int, ItemUseRecord>();
        prof.ItemUseCounts[number] = new ItemUseRecord(used, _now());
        _profile.Save();
        _log?.Info("Items", $"use counted: item #{number} → {used}/{meta.MaxUses} used");
        Changed?.Invoke();
    }

    // Used count as of now: a rechargeable item whose record predates the last cleanup
    // has restocked (reads 0); otherwise the stored count. Finite items ignore cleanup.
    private int EffectiveUsed(int number, ItemChargeMeta meta)
    {
        if (_profile.Current?.ItemUseCounts is not { } map
            || !map.TryGetValue(number, out ItemUseRecord? rec))
            return 0;
        if (meta.Recharges && _cleanupConfig() is { } cfg
            && _now() >= BossTimerMath.NextCleanup(rec.UpdatedUtc, cfg.TimeOfDay, cfg.Tz))
            return 0;
        return rec.Used;
    }

    // The held item (carried or worn) a `use` arg refers to: a held name that is a prefix
    // of the arg (item-then-target), else one whose name contains the arg's first word.
    // First match wins — the loose resolution the game does for a partial. Worn gear is
    // included because cast-on-use rechargeables (a wielded mace) are used while worn.
    private string? ResolveHeld(string arg)
    {
        if (arg.Length < 2) return null;
        string a = arg.ToLowerInvariant();
        foreach (string c in _held())
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            string cl = c.ToLowerInvariant();
            if (a == cl || a.StartsWith(cl + " ", StringComparison.Ordinal)) return c;
        }
        string first = a.Split(' ')[0];
        if (first.Length >= 3)
            foreach (string c in _held())
                if (!string.IsNullOrWhiteSpace(c) && c.ToLowerInvariant().Contains(first)) return c;
        return null;
    }
}

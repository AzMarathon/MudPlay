using System;
using System.Collections.Generic;
using MudPlay.Game.Calculators;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.Game.Train;

// One place a spell's scroll can be bought: the teaching item, the shop room, and
// what that shop charges this character for it.
public readonly record struct ShopSpellSource(
    int ItemNumber, string ItemName, int ShopNumber, string ShopName, RoomKey Room, long PriceCopper);

// A spell the class can learn from an item some shop restocks.
public sealed record ShopSpellOffer(
    int SpellNumber, string SpellName, int ReqLevel, IReadOnlyList<ShopSpellSource> Sources);

// Joins the class's learnable spells to the shops that sell their teaching items.
public static class ShopSpellCatalog
{
    // learnable is the class's spell list with the level gate ignored; each offer
    // carries its own ReqLevel. A stock slot that never restocks is left out: the
    // shop holds one only when a player has sold it there, so it's no place to send
    // a trip. A shop restricted to another class is left out too.
    public static IReadOnlyList<ShopSpellOffer> Build(
        GameDataCache cache,
        IReadOnlyList<KnownSpell> learnable,
        IReadOnlyList<TeachingItem> teachingItems,
        int classNumber,
        int charm,
        Func<int, IReadOnlyCollection<int>> shopsSelling,
        Func<int, IReadOnlyList<RoomKey>> roomsOfShop)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(learnable);
        ArgumentNullException.ThrowIfNull(teachingItems);
        ArgumentNullException.ThrowIfNull(shopsSelling);
        ArgumentNullException.ThrowIfNull(roomsOfShop);

        Dictionary<int, KnownSpell> spells = new();
        foreach (KnownSpell s in learnable) spells.TryAdd(s.Number, s);

        Dictionary<int, ShopDefinition?> shopCache = new();
        Dictionary<int, List<ShopSpellSource>> sources = new();
        foreach (TeachingItem item in teachingItems)
        {
            if (!spells.ContainsKey(item.SpellNumber)) continue;
            foreach (int shopNumber in shopsSelling(item.ItemNumber))
            {
                if (!shopCache.TryGetValue(shopNumber, out ShopDefinition? shop))
                    shopCache[shopNumber] = shop = ShopInventoryReader.Read(cache, shopNumber);
                if (shop is null) continue;
                if (shop.ClassRestriction != 0 && shop.ClassRestriction != classNumber) continue;
                if (PriceAt(shop, item.ItemNumber, charm) is not { } price) continue;

                foreach (RoomKey room in roomsOfShop(shopNumber))
                {
                    if (!sources.TryGetValue(item.SpellNumber, out List<ShopSpellSource>? list))
                        sources[item.SpellNumber] = list = new();
                    list.Add(new(item.ItemNumber, item.ItemName, shopNumber, shop.Name, room, price));
                }
            }
        }

        List<ShopSpellOffer> offers = new(sources.Count);
        foreach ((int spellNumber, List<ShopSpellSource> list) in sources)
        {
            KnownSpell s = spells[spellNumber];
            offers.Add(new(spellNumber, s.Name.Trim(), s.ReqLevel, list));
        }
        offers.Sort(static (a, b) =>
        {
            int byLevel = a.ReqLevel.CompareTo(b.ReqLevel);
            return byLevel != 0 ? byLevel : string.Compare(a.SpellName, b.SpellName, StringComparison.OrdinalIgnoreCase);
        });
        return offers;
    }

    private static long? PriceAt(ShopDefinition shop, int itemNumber, int charm)
    {
        foreach (ShopStockEntry entry in shop.Stock)
        {
            if (entry.ItemId != itemNumber) continue;
            if (entry.RestockPercent <= 0 || entry.MaxStock <= 0) return null;
            return (long)Math.Ceiling(ShopPriceCalculator.BuyCopper(entry.BaseCopper, shop.MarkupPercent, charm));
        }
        return null;
    }
}

using System;
using System.Collections.Generic;
using MudPlay.Game.Map;

namespace MudPlay.Game.Train;

// One scroll to buy and read at a stop.
public readonly record struct ShopSpellPurchase(
    int SpellNumber, string SpellName, int ReqLevel, int ItemNumber, string ItemName, long PriceCopper);

public sealed record ShopSpellStop(RoomKey Room, string ShopName, IReadOnlyList<ShopSpellPurchase> Purchases);

// Stops are in visiting order (nearest first). Unaffordable and Unreachable name
// the wanted spells the plan had to leave out, for the log.
public sealed record ShopSpellPlan(
    IReadOnlyList<ShopSpellStop> Stops,
    long CostCopper,
    IReadOnlyList<string> Unaffordable,
    IReadOnlyList<string> Unreachable)
{
    public static ShopSpellPlan Empty { get; } =
        new(Array.Empty<ShopSpellStop>(), 0, Array.Empty<string>(), Array.Empty<string>());
}

// Decides which scrolls a train trip goes on to buy, and where.
//
// Wanted: every shop-sold spell the class can learn at this level that isn't in
// the spellbook and that the user hasn't switched off. That deliberately reaches
// back past the levels just trained — many scrolls restock on a small chance, so a
// spell missed on one trip has to be picked up by a later one.
//
// A shop counts only when the route there AND a route on to where the run resumes
// both exist. The distance metric is the walker's own, so a level-gated area
// (Port Blackwater below 25) is simply unreachable and drops out here.
//
// Money: lowest-level spells first, each included while the budget still covers
// it. Training is paid before this runs, so the budget is what's left.
//
// Pure: offers, the spellbook and the distance metric are injected.
public static class ShopSpellPlanner
{
    public static ShopSpellPlan Plan(
        IReadOnlyList<ShopSpellOffer> offers,
        int level,
        Func<int, bool> isObtained,
        IReadOnlyCollection<string> skipped,
        RoomKey from,
        RoomKey returnTo,
        Func<RoomKey, RoomKey, int?> distance,
        long? budgetCopper = null,
        IReadOnlyCollection<RoomKey>? excludedRooms = null)
    {
        ArgumentNullException.ThrowIfNull(offers);
        ArgumentNullException.ThrowIfNull(isObtained);
        ArgumentNullException.ThrowIfNull(skipped);
        ArgumentNullException.ThrowIfNull(distance);

        HashSet<string> off = new(skipped, StringComparer.OrdinalIgnoreCase);
        // (steps there, steps there + on to the resume point), null when either leg has no route.
        Dictionary<RoomKey, (int There, int Total)?> legs = new();
        (int There, int Total)? Legs(RoomKey room)
        {
            if (legs.TryGetValue(room, out (int There, int Total)? known)) return known;
            (int There, int Total)? leg = distance(from, room) is { } there && distance(room, returnTo) is { } back
                ? (there, there + back)
                : null;
            legs[room] = leg;
            return leg;
        }

        Dictionary<RoomKey, (string Shop, List<ShopSpellPurchase> Buys)> stops = new();
        List<string> unaffordable = new();
        List<string> unreachable = new();
        long cost = 0;

        // offers arrive ordered by ReqLevel then name, which is the buying order.
        foreach (ShopSpellOffer offer in offers)
        {
            if (offer.ReqLevel > level || isObtained(offer.SpellNumber) || off.Contains(offer.SpellName)) continue;

            // A shop already on the route beats a nearer one that isn't: the stop is
            // paid for. Otherwise the shop that adds the fewest steps; ties to the
            // cheaper scroll.
            ShopSpellSource? pick = null;
            bool pickOnRoute = false;
            int pickDetour = int.MaxValue;
            foreach (ShopSpellSource s in offer.Sources)
            {
                if (excludedRooms?.Contains(s.Room) == true) continue;
                if (Legs(s.Room) is not { } leg) continue;
                int detour = leg.Total;
                bool onRoute = stops.ContainsKey(s.Room);
                bool better = pick is null
                    || (onRoute && !pickOnRoute)
                    || (onRoute == pickOnRoute
                        && (detour < pickDetour || (detour == pickDetour && s.PriceCopper < pick.Value.PriceCopper)));
                if (!better) continue;
                pick = s;
                pickOnRoute = onRoute;
                pickDetour = detour;
            }

            if (pick is not { } source)
            {
                unreachable.Add(offer.SpellName);
                continue;
            }
            if (budgetCopper is { } budget && cost + source.PriceCopper > budget)
            {
                unaffordable.Add(offer.SpellName);
                continue;
            }

            cost += source.PriceCopper;
            if (!stops.TryGetValue(source.Room, out (string Shop, List<ShopSpellPurchase> Buys) stop))
                stops[source.Room] = stop = (source.ShopName, new());
            stop.Buys.Add(new(offer.SpellNumber, offer.SpellName, offer.ReqLevel,
                source.ItemNumber, source.ItemName, source.PriceCopper));
        }

        List<ShopSpellStop> ordered = new(stops.Count);
        foreach ((RoomKey room, (string shop, List<ShopSpellPurchase> buys)) in stops)
            ordered.Add(new(room, shop, buys));
        ordered.Sort((a, b) =>
        {
            int byDistance = (Legs(a.Room)?.There ?? int.MaxValue).CompareTo(Legs(b.Room)?.There ?? int.MaxValue);
            if (byDistance != 0) return byDistance;
            int byMap = a.Room.Map.CompareTo(b.Room.Map);
            return byMap != 0 ? byMap : a.Room.Room.CompareTo(b.Room.Room);
        });

        return new(ordered, cost, unaffordable, unreachable);
    }
}

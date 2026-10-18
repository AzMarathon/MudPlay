using MudPlay.Game.Inventory;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Light;

// Pure decision core for the auto-light engine. Given the darkness of the route
// ahead (RouteLightScanner), the character's worn illumination, the currently
// readied light, the lights carried in the pack, the buyable light catalogue, and
// the AutoLightSettings knobs, it answers what to do right now as an
// AutoLightPlan — a carried light already covers (no buy), buy a provisioning
// batch, top up a dwindling supply, or nothing. It never touches the wire or the
// game state; the wiring layer turns Buy and Reorder into shop trips, and lights
// are used room by room, not off this plan.
//
// Preferred vs. auto: a named PreferredLightName is used as-is (the user's
// explicit pick, even if a stronger light would be needed to fully clear the
// darkest room); the auto sentinel (null name) lets the planner pick the weakest
// catalogue light that still reaches the see threshold for the route's darkest
// room, avoiding overkill. Coverage is measured against the worn-only illu, so
// the chosen light is the one that — once readied — covers the room on its own.
public static class AutoLightPlanner
{
    // Global light tick: one Readied/N point drains every 30 s.
    private const double SecondsPerReadiedPoint = 30.0;

    public static AutoLightPlan Plan(
        RouteLightScan routeScan,
        int wornIllu,
        int roomLightSpellIllu,
        ReadiedLight? readied,
        IReadOnlyList<LightItem> carriedLights,
        IReadOnlyList<LightItem> catalogue,
        AutoLightSettings settings)
    {
        ArgumentNullException.ThrowIfNull(carriedLights);
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(settings);

        // In spell-only mode the engine never buys or readies light items — worn
        // gear + the configured room-light spell are the whole story.
        bool spellOnly = settings.UseRoomLightSpellOnly;
        bool provisioningOn = !spellOnly && settings.CarryHours > 0;
        LightItem? preferred = FindByName(settings.PreferredLightName, catalogue);

        // 1. Reorder — a readied light dwindling below the minute threshold gets
        //    topped up. Only meaningful when provisioning is on (there's a carry
        //    target to restock to) and we can identify what to rebuy.
        if (provisioningOn && settings.ReorderThresholdMinutes > 0 && readied is { } rl)
        {
            double remainingMinutes = rl.Readied * SecondsPerReadiedPoint / 60.0;
            if (remainingMinutes < settings.ReorderThresholdMinutes)
            {
                // The Preferred light, as for any buy; under Automatic, more of
                // the kind that is lit.
                LightItem? restock = preferred ?? FindByName(rl.Name, catalogue);
                if (restock is { } rs)
                    return AutoLightPlan.ReorderLight(
                        rs.Name, CarryCount(settings.CarryHours, rs.BurnTime),
                        $"reorder: {rl.Name} at ~{remainingMinutes:0} min left");
            }
        }

        // 2. Route runs dark — ready a carried light that covers it, else buy one.
        if (routeScan.NeedsLight)
        {
            // Item need is measured against worn gear PLUS the configured
            // room-light spell — the spell is a standing baseline the player
            // maintains, so a light item only has to cover the remainder. When
            // gear + spell already clear the darkest room, no item is needed.
            int minStrength = LightModel.IlluGapToSee(wornIllu + roomLightSpellIllu, routeScan.DarkestRoomLight);
            if (minStrength <= 0)
                return AutoLightPlan.Nothing(
                    $"route dark: worn gear + room-light spell cover "
                    + $"(worn need {routeScan.NeededLightStrength}, spell illu {roomLightSpellIllu})");

            // Spell-only mode never buys/readies items — rely on gear + spell and
            // accept the darkness penalty if they fall short.
            if (spellOnly)
                return AutoLightPlan.Nothing(
                    $"route dark: spell-only mode, gear + spell short by {minStrength} — no light items");

            // A light is already lit whose strength alone clears the darkest room
            // from the worn baseline — leave it be. The reorder branch above tops
            // it up as its charge dwindles; swapping to another light here would
            // re-fire `use` on every planned hop of the same dark run. (Coverage
            // is a Strength question, so this holds even for a ground-picked light
            // whose remaining charge we can't yet know.)
            if (readied is { } lit && FindByName(lit.Name, catalogue) is { } litLight
                && litLight.Strength >= minStrength)
                return AutoLightPlan.Nothing(
                    $"route dark: readied {lit.Name} already covers (illu {litLight.Strength} >= need {minStrength})");

            // a. Ready a carried light — no shop trip. The Preferred light decides
            //    what is bought, not whether a carried light is used: the preferred
            //    one when carried and strong enough, else the weakest carried light
            //    that reaches the darkest room, else a carried preferred light (the
            //    user's pick, as a buy would be).
            if (CoveringCarried(carriedLights, minStrength, preferred) is { } carried)
                return AutoLightPlan.ReadyLight(
                    carried.Name, $"route dark: ready {carried.Name} (need illu {minStrength})");

            // b. Nothing carried covers it — provision one from a shop.
            if (provisioningOn && Choose(catalogue, minStrength, preferred) is { } buy)
                return AutoLightPlan.BuyLight(
                    buy.Name, CarryCount(settings.CarryHours, buy.BurnTime),
                    $"route dark: provision {buy.Name} (need illu {minStrength})");

            // c. Can't buy (provisioning off / nothing buyable): nothing to do here;
            //    the room-by-room path lights the best carried light on the way.
            return AutoLightPlan.Nothing("route dark: no carried light covers and none can be bought");
        }

        return AutoLightPlan.Nothing("route lit");
    }

    // Pick a light from the list: the user's preferred when set (and present in
    // the list), otherwise the weakest light that still reaches the darkest room
    // (Strength >= minStrength). Null when nothing qualifies.
    private static LightItem? Choose(IReadOnlyList<LightItem> from, int minStrength, LightItem? preferred)
    {
        if (preferred is { } p)
            return FindByName(p.Name, from);

        LightItem? best = null;
        foreach (LightItem l in from)
        {
            if (l.Strength < minStrength) continue;
            if (best is not { } b || l.Strength < b.Strength) best = l;
        }
        return best;
    }

    private static LightItem? CoveringCarried(IReadOnlyList<LightItem> carried, int minStrength, LightItem? preferred)
    {
        LightItem? mine = preferred is { } p ? FindByName(p.Name, carried) : null;
        if (mine is { } m && m.Strength >= minStrength) return m;
        return Choose(carried, minStrength, preferred: null) ?? mine;
    }

    private static LightItem? FindByName(string? name, IReadOnlyList<LightItem> lights)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (LightItem l in lights)
            if (string.Equals(l.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                return l;
        return null;
    }

    // How many of a light to buy to cover carryHours of lit time, rounding up. At
    // least one when provisioning; a light with no burn budget yields a single copy.
    private static int CarryCount(int carryHours, TimeSpan burnTime)
    {
        if (burnTime.TotalHours <= 0) return 1;
        return Math.Max(1, (int)Math.Ceiling(carryHours / burnTime.TotalHours));
    }
}

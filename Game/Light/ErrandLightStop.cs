using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Light;

// The light stop an errand makes on its own walks: a training walk, a funding leg,
// a stash transfer, a sell detour, a spell-shop leg. While an errand has the walker
// the walk-to light router stands down (AutoLightShopRouter's engineWalkActive), so
// the errand provisions its own walks through this.
//
// Before a leg, the leg and the legs the errand plans after it are scanned
// (AutoLightProvisioner.PlanRouteBuy): spell first, a carried light only for what it
// leaves short, nothing bought in spell-only mode or with Carry hours 0. When they
// cross a dark room nothing covers, the walk goes by a light shop, buys the
// Carry-hours batch (LightShopBuy) and walks on to the leg's room. The shop is the
// closest one the walk there needs no light for, else the closest of all
// (AutoLightProvisioner.TrySelectErrandShop, the bank run's choice too): lights
// before the dark where that can be done, and through the dark as it is where it
// can't (user, 2026-10-17). A light found on the way is used: at the shop the walk
// on is scanned again and nothing is bought when it is covered now. When the leg's
// own room is the shop, the light is bought there on arrival, alongside the
// errand's own work in it.
//
// The purse is checked first (AutoLightProvisioner.AffordableBuy), leaving what
// the errand holds back (a training trip's bill). A shop whose buy came to nothing
// for any errand is passed over for a while (AutoLightProvisioner.NoteErrandShop),
// so the next errand or the next run doesn't walk back to it.
//
// The errand sees one walk. While the stop has it, the stop takes the walker's
// events; the walk on to the leg's room reports to the errand as any walk of its
// own does. A shop that can't be reached, or a buy that doesn't land, walks on
// without the light. A walk stopped from outside on the way to the shop, one
// started while the stop buys, or the connection dropping then, is the errand's
// stopped walk: the stop hands it over through the errand's callback.
//
// One stop per errand run (BeginRun). UI thread only, like the walker events that
// drive it.
public sealed class ErrandLightStop
{
    private enum Phase { Idle, ToShop, Buying }

    private readonly string _errand;
    private readonly Func<RoomKey, RoomKey, IReadOnlyList<RoomKey>?> _route;
    private readonly AutoLightProvisioner _provisioner;
    private readonly AutoLightShopRouter _shops;
    private readonly Func<RoomKey?> _currentRoom;
    private readonly LightShopBuy _buyer;
    private readonly Action<string> _send;
    private readonly Func<RoomKey?> _lastUnpaidFailure;
    private readonly LogService? _log;

    private Phase _phase = Phase.Idle;
    // This run has had its stop (or found nowhere to make one).
    private bool _tried;
    // Set while the stop starts the walk to the shop itself: a walk that ends inside
    // that call (already there, one room short of a stop-before boss room) is
    // handled once the call is back, and a Stopped it raises for a walk it
    // supersedes is the stop's own.
    private bool _driving;
    private WalkEvent? _heldEnd;
    private Leg? _leg;
    // The leg's own room sells the light: the buy goes out on arrival there.
    private (RoomKey Room, AutoLightBuyRequest Buy)? _buyOnArrival;

    private sealed record Leg(RoomKey Target, IReadOnlyList<RoomKey> Ahead, long Keep, RoomKey Shop,
        AutoLightBuyRequest Buy, Func<RoomKey, bool> Walk, Action<string> Stopped);

    // errand names the errand in the log ("the stash transfer"). route is the walk
    // the walker would plan between two rooms (AutoWalkManager.TryComputeRouteKeys).
    public ErrandLightStop(string errand, Func<RoomKey, RoomKey, IReadOnlyList<RoomKey>?> route,
        AutoLightProvisioner provisioner, AutoLightShopRouter shops, Func<RoomKey?> currentRoom,
        LightShopBuy buyer, Action<string> send, LogService? log = null,
        // Where the last walk asked for found no route for want of a toll or fare
        // (AutoWalkManager.LastUnpaidFailure): a shop so missed isn't held against.
        Func<RoomKey?>? lastUnpaidFailure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errand);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(shops);
        ArgumentNullException.ThrowIfNull(currentRoom);
        ArgumentNullException.ThrowIfNull(buyer);
        ArgumentNullException.ThrowIfNull(send);
        _errand = errand;
        _route = route;
        _provisioner = provisioner;
        _shops = shops;
        _currentRoom = currentRoom;
        _buyer = buyer;
        _send = send;
        _lastUnpaidFailure = lastUnpaidFailure ?? (static () => null);
        _log = log;
    }

    // A new run of the errand: it may make its one stop again.
    public void BeginRun()
    {
        Cancel();
        _tried = false;
    }

    // The user started the errand (Train Now): the shops an earlier errand found
    // wanting are tried again, as a loop the user starts lifts its back-off.
    public void NoteUserStarted() => _provisioner.ClearErrandShopHolds($"{_errand} started by the user");

    // Walk the errand's leg to target with its own walk, by a light shop first when
    // the leg and the rooms ahead (the errand's later legs, in order) run dark. True
    // when a walk is under way (or the buy, standing in the shop), as the errand's
    // walk would answer. stopped(detail) is called when the errand's walk is
    // stopped while the stop has it. keepCopper is what the buy must leave in the
    // purse.
    public bool Walk(RoomKey target, Func<RoomKey, bool> walk, Action<string> stopped,
        IReadOnlyList<RoomKey>? ahead = null, long keepCopper = 0)
    {
        ArgumentNullException.ThrowIfNull(walk);
        ArgumentNullException.ThrowIfNull(stopped);
        Cancel();
        ahead ??= Array.Empty<RoomKey>();
        if (_tried || _currentRoom() is not { } from || from.Equals(target)) return walk(target);
        if (PlanStop(from, target, ahead, keepCopper) is not { } stop) return walk(target);

        if (stop.Shop.Equals(target))
        {
            _log?.Info(AutoLightProvisioner.LogCategory,
                $"{_errand}: the walk on from {target} runs dark and {target} sells '{stop.Buy.LightName}' — buying x{stop.Buy.Count} there");
            _buyOnArrival = (target, stop.Buy);
            return walk(target);
        }

        _log?.Info(AutoLightProvisioner.LogCategory,
            $"{_errand}: the walk to {target} runs dark — buying '{stop.Buy.LightName}' x{stop.Buy.Count} at {stop.Shop} first");
        _leg = new Leg(target, ahead, keepCopper, stop.Shop, stop.Buy, walk, stopped);
        if (from.Equals(stop.Shop))
        {
            BeginBuy();
            return true;
        }

        _phase = Phase.ToShop;
        _heldEnd = null;
        _driving = true;
        bool started;
        try { started = walk(stop.Shop); }
        finally { _driving = false; }
        if (_heldEnd is { } ended)
        {
            _heldEnd = null;
            return ShopLegEnded(ended);
        }
        if (started) return true;
        _log?.Info(AutoLightProvisioner.LogCategory, $"{_errand}: can't walk to the light shop at {stop.Shop} — walking on without it");
        NoteIfUnreachable(from, stop.Shop, stop.Buy);
        return WalkOn();
    }

    // The walker's events, ahead of the errand's own handling. True when the stop
    // took the event and the errand must not see it.
    public bool OnWalkEvent(WalkEvent e)
    {
        if (_phase == Phase.Idle)
        {
            // Only the end of the walk to the leg's own room settles a buy there.
            if (_buyOnArrival is { } here && (e.Destination == here.Room || e.Requested == here.Room)
                && e.Kind is WalkEventKind.Finished or WalkEventKind.Failed or WalkEventKind.Stopped)
            {
                _buyOnArrival = null;
                if (e.Kind == WalkEventKind.Finished && _currentRoom() is { } room && room.Equals(here.Room))
                    _buyer.Begin(here.Buy, _send, bought => _provisioner.NoteErrandShop(here.Room, here.Buy, bought));
            }
            return false;
        }
        if (_driving)
        {
            if (e.Kind is WalkEventKind.Finished or WalkEventKind.Failed) _heldEnd = e;
            return true;
        }
        if (_leg is not { } leg) return true;

        if (_phase == Phase.ToShop)
        {
            // A walk re-pointed one room short of a stop-before boss room names the
            // shop as the room it was asked for.
            bool toShop = e.Destination is null || e.Destination == leg.Shop || e.Requested == leg.Shop;
            if (!toShop) return true;
            if (e.Kind is WalkEventKind.Finished or WalkEventKind.Failed) ShopLegEnded(e);
            else if (e.Kind == WalkEventKind.Stopped) HandOver(leg, $"the walk to the light shop stopped ({e.Detail})");
            return true;
        }

        // Nothing of the errand walks while the stop buys: a walk starting is
        // someone else's. It is let run, and the errand ends where it stands.
        if (e.Kind == WalkEventKind.Started)
            HandOver(leg, $"a walk started during the light stop ({e.Detail})");
        return true;
    }

    // The buy's inventory poll (InventoryManager.Changed).
    public void OnInventoryChanged() => _buyer.OnInventoryChanged();

    // The connection dropped: a stop under way is over, and so is the errand's walk
    // it held, rather than walking on in whatever session comes next.
    public void NoteDisconnected()
    {
        if (_phase != Phase.Idle && _leg is { } leg) HandOver(leg, "the connection dropped during the light stop");
        _buyer.Cancel();
        _buyOnArrival = null;
    }

    // The errand is over or torn down: stand down, leave the walker alone.
    public void Cancel()
    {
        if (_phase == Phase.Buying) _buyer.Cancel();
        _phase = Phase.Idle;
        _leg = null;
        _heldEnd = null;
        _buyOnArrival = null;
    }

    // One line for the bug report.
    public string Describe() => _leg is { } leg && _phase != Phase.Idle
        ? $"{(_phase == Phase.ToShop ? "walking to the light shop at" : "buying at")} {leg.Shop}: '{leg.Buy.LightName}' x{leg.Buy.Count}, then on to {leg.Target}"
        : _buyOnArrival is { } b ? $"buying '{b.Buy.LightName}' x{b.Buy.Count} on reaching {b.Room}"
        : _buyer.Buying is { } buying ? $"buying '{buying.LightName}' x{buying.Count} where it stands"
        : _tried ? "idle (this run's light stop is made)" : "idle";

    // The light to buy for the walk from `from` to target and on through ahead, and
    // the shop to buy it at; null when the walk is covered or nothing is to be
    // bought. A buy with nowhere to make it is this run's stop too.
    private (RoomKey Shop, AutoLightBuyRequest Buy)? PlanStop(RoomKey from, RoomKey target, IReadOnlyList<RoomKey> ahead, long keep)
    {
        if (RoomsAhead(from, target, ahead) is not { } rooms)
        {
            _log?.Info(AutoLightProvisioner.LogCategory,
                $"{_errand}: no plain route from {from} to {target} to read for darkness — no light check for this walk");
            return null;
        }
        if (_provisioner.PlanRouteBuy(rooms) is not { } wanted) return null;
        _tried = true;
        if (!_provisioner.TrySelectErrandShop(_shops, _route, from, wanted, out RoomKey shop))
        {
            _log?.Info(AutoLightProvisioner.LogCategory,
                $"{_errand}: the walk to {target} runs dark but no reachable shop sells '{wanted.LightName}' (or each one let an errand down lately) — walking on with what is carried");
            return null;
        }
        if (_provisioner.AffordableBuy(wanted, shop, keep, out _) is { } buy) return (shop, buy);
        _log?.Info(AutoLightProvisioner.LogCategory, $"{_errand}: the walk to {target} runs dark but the purse can't pay for '{wanted.LightName}' at {shop} — walking on with what is carried");
        return null;
    }

    // Every room from `from` to target, then on through each room of ahead in turn.
    // Null when target can't be routed to; a later leg that can't is left out.
    private IReadOnlyList<RoomKey>? RoomsAhead(RoomKey from, RoomKey target, IReadOnlyList<RoomKey> ahead)
    {
        if (_route(from, target) is not { } leg) return null;
        List<RoomKey> rooms = new(leg);
        RoomKey at = target;
        foreach (RoomKey next in ahead)
        {
            if (!next.Equals(at) && _route(at, next) is { } more) rooms.AddRange(more);
            at = next;
        }
        return rooms;
    }

    // The walk to the shop is over. In the shop: buy what the walk on still needs
    // (a light found on the way may cover it now). Anywhere else: walk on.
    private bool ShopLegEnded(WalkEvent e)
    {
        if (_leg is not { } leg) return false;
        if (e.Kind == WalkEventKind.Finished && _currentRoom() is { } here && here.Equals(leg.Shop))
        {
            if (!_provisioner.Enabled)
            {
                _log?.Info(AutoLightProvisioner.LogCategory, $"{_errand}: Auto-Light was turned off on the way — nothing bought, walking on to {leg.Target}");
                return WalkOn();
            }
            if (RoomsAhead(here, leg.Target, leg.Ahead) is { } rooms && _provisioner.PlanRouteBuy(rooms) is { } still
                && _provisioner.AffordableBuy(still, leg.Shop, leg.Keep, out _) is { } buy)
            {
                _leg = leg with { Buy = buy };
                BeginBuy();
                return true;
            }
            _log?.Info(AutoLightProvisioner.LogCategory, $"{_errand}: at the light shop, the walk on to {leg.Target} is covered now (or the purse can't pay) — nothing bought");
            return WalkOn();
        }
        _log?.Info(AutoLightProvisioner.LogCategory, $"{_errand}: couldn't reach the light shop at {leg.Shop} — walking on to {leg.Target}");
        if (e.Kind == WalkEventKind.Failed && _currentRoom() is { } at) NoteIfUnreachable(at, leg.Shop, leg.Buy);
        return WalkOn();
    }

    // A walk to the shop that failed holds the shop back only when the map gives
    // no way there: a toll or fare the purse can't pay, a monster in the way or a
    // dropped connection is no word on the shop.
    private void NoteIfUnreachable(RoomKey from, RoomKey shop, AutoLightBuyRequest buy)
    {
        if (_lastUnpaidFailure() == shop || _route(from, shop) is not null) return;
        _provisioner.NoteErrandShop(shop, buy, bought: false);
    }

    private void BeginBuy()
    {
        if (_leg is not { } leg) return;
        _phase = Phase.Buying;
        _buyer.Begin(leg.Buy, _send, bought =>
        {
            if (_phase != Phase.Buying) return;
            _provisioner.NoteErrandShop(leg.Shop, leg.Buy, bought);
            _log?.Info(AutoLightProvisioner.LogCategory, bought
                ? $"{_errand}: '{leg.Buy.LightName}' bought — walking on to {leg.Target}"
                : $"{_errand}: the light buy didn't land — walking on to {leg.Target} with what is carried");
            WalkOn();
        });
    }

    // The stop is over: the errand's own walk to the leg's room. It reports to the
    // errand directly, a failure to start included (the walker raises Failed).
    private bool WalkOn()
    {
        if (_leg is not { } leg) return false;
        _phase = Phase.Idle;
        _leg = null;
        return leg.Walk(leg.Target);
    }

    private void HandOver(Leg leg, string detail)
    {
        _log?.Info(AutoLightProvisioner.LogCategory, $"{_errand}: {detail} — the errand's walk is taken over");
        Cancel();
        leg.Stopped(detail);
    }
}

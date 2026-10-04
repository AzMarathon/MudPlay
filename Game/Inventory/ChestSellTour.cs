using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Sells the Chest Offload list shop by shop: walk to each stop (the full "Walk here"
// path), sell its items, wait for the game to confirm, then on to the next. A single
// shop's Sell All, or one item's Sell, is just a one-stop tour.
//
// It never sells what the player already owned. Every quantity is capped, right before
// its `sell` goes out, at how many of that item the chests gave and are still carried
// (`chestCount`, from ChestOpenTracker's list) — so with 3 moonstones of your own and 2
// from a chest, at most 2 are sold, whatever the plan said. An item not on the list
// sells 0.
//
// App-lifetime so closing the window doesn't strand a tour mid-walk. UI thread only:
// walker and inventory events arrive through `post`.
public sealed class ChestSellTour : IDisposable
{
    public const string LogCategory = "ChestOffload";

    // A shop that goes this long with no sale confirmed or refused is done: whatever
    // didn't confirm was refused or never went out, and the tour moves on.
    private const int QuietMs = 5000;

    public sealed record Stop(RoomKey Room, int Shop, string ShopName, IReadOnlyList<(string Name, int Qty)> Items);

    private enum Phase { Idle, Walking, Selling }

    private readonly Func<int?> _currentShop;
    private readonly Action<RoomKey> _goWalk;
    private readonly Func<string, int> _chestCount;
    private readonly Action<IReadOnlyList<string>> _sendPaced;
    private readonly Func<bool> _isParadigm;
    private readonly InventoryManager _inventory;
    private readonly Action<int, Action> _schedule;
    private readonly Action<Action> _post;
    private readonly LogService? _log;

    private Phase _phase = Phase.Idle;
    private IReadOnlyList<Stop> _stops = Array.Empty<Stop>();
    private int _index;
    private Dictionary<string, int> _expected = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;

    // What the tour is doing, or why it stopped — shown under the window's toolbar.
    public string Status { get; private set; } = "";
    public bool IsRunning => _phase != Phase.Idle;
    public event Action? Changed;

    public ChestSellTour(
        Func<int?> currentShop, Action<RoomKey> goWalk, Func<string, int> chestCount,
        Action<IReadOnlyList<string>> sendPaced, Func<bool> isParadigm, InventoryManager inventory,
        Action<int, Action> schedule, Action<Action> post, LogService? log = null)
    {
        _currentShop = currentShop ?? throw new ArgumentNullException(nameof(currentShop));
        _goWalk = goWalk ?? throw new ArgumentNullException(nameof(goWalk));
        _chestCount = chestCount ?? throw new ArgumentNullException(nameof(chestCount));
        _sendPaced = sendPaced ?? throw new ArgumentNullException(nameof(sendPaced));
        _isParadigm = isParadigm ?? throw new ArgumentNullException(nameof(isParadigm));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _log = log;
        _inventory.ItemSold += OnItemSold;
    }

    // Start selling at these stops in order. Refused while a tour is already running.
    public bool Start(IReadOnlyList<Stop> stops)
    {
        if (IsRunning)
        {
            SetStatus("A sell tour is already running — cancel it first.");
            return false;
        }
        _stops = stops.Where(s => s.Items.Count > 0).ToList();
        _index = 0;
        _log?.Info(LogCategory, $"sell tour started — {_stops.Count} shop(s): {string.Join(" → ", _stops.Select(s => s.ShopName))}");
        GoToStop();
        return true;
    }

    public void Cancel() => Finish("Sell tour cancelled.");

    private void GoToStop()
    {
        ++_generation;
        if (_index >= _stops.Count)
        {
            Finish(_stops.Count > 1 ? "Sell tour done." : "");
            return;
        }
        Stop stop = _stops[_index];
        if (_currentShop() == stop.Shop)
        {
            SellHere();
            return;
        }
        _phase = Phase.Walking;
        SetStatus($"{StopLabel()}walking to {stop.ShopName} ({stop.Room.Map}/{stop.Room.Room}) to sell…");
        _log?.Info(LogCategory, $"walking to '{stop.ShopName}' {stop.Room.Map}/{stop.Room.Room} to sell there");
        _goWalk(stop.Room);
    }

    // The walk this tour started: sell when it arrives; stop if it's stopped, fails,
    // or another walk replaces it.
    public void OnWalkerEvent(WalkEvent e) => _post(() =>
    {
        if (_phase != Phase.Walking) return;
        Stop stop = _stops[_index];
        bool ours = e.Destination is { } d && d.Equals(stop.Room);
        switch (e.Kind)
        {
            case WalkEventKind.Started when !ours:
                Finish($"Another walk started — stopped before {stop.ShopName}; nothing more sold.");
                break;
            case WalkEventKind.Finished when ours:
                if (_currentShop() == stop.Shop) SellHere();
                else Finish($"The walk ended outside {stop.ShopName} — nothing more sold.");
                break;
            case WalkEventKind.Stopped or WalkEventKind.Failed when ours || e.Destination is null:
                Finish($"Walk to {stop.ShopName} {(e.Kind == WalkEventKind.Failed ? "failed" : "stopped")}" +
                       (e.Detail.Length > 0 ? $" ({e.Detail})" : "") + " — nothing more sold.");
                break;
        }
    });

    private void SellHere()
    {
        Stop stop = _stops[_index];
        _phase = Phase.Selling;
        bool paradigm = _isParadigm();
        var commands = new List<string>();
        _expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, int planned) in stop.Items)
        {
            int fromChests = _chestCount(name);
            int qty = Math.Min(planned, fromChests);
            if (qty < planned)
                _log?.Info(LogCategory, $"sell {name}: planned {planned}, only {fromChests} from the chests still carried — selling {qty}");
            if (qty <= 0) continue;
            _expected[name] = _expected.GetValueOrDefault(name) + qty;
            _log?.Info(LogCategory, $"sell {qty} {name} at '{stop.ShopName}'");
            CountedCommand.Emit(commands.Add, "sell", qty, name, paradigm);
        }
        if (commands.Count == 0)
        {
            _log?.Info(LogCategory, $"nothing left to sell at '{stop.ShopName}'");
            Advance();
            return;
        }
        SetStatus($"{StopLabel()}selling at {stop.ShopName}…");
        _sendPaced(commands);
        ArmQuiet();
    }

    private void OnItemSold(string name, int count, long _) => _post(() =>
    {
        if (_phase != Phase.Selling || !_expected.TryGetValue(name, out int left)) return;
        left -= count;
        if (left > 0) _expected[name] = left;
        else _expected.Remove(name);
        if (_expected.Count == 0) Advance();
        else ArmQuiet();
    });

    private void ArmQuiet()
    {
        int gen = ++_generation;
        _schedule(QuietMs, () =>
        {
            if (gen != _generation || _phase != Phase.Selling) return;
            _log?.Info(LogCategory,
                $"'{_stops[_index].ShopName}': no sale confirmed for " +
                string.Join(", ", _expected.Select(kv => $"{kv.Value} {kv.Key}")) + " — moving on");
            Advance();
        });
    }

    private void Advance()
    {
        _index++;
        GoToStop();
    }

    private void Finish(string status)
    {
        ++_generation;
        bool wasRunning = IsRunning;
        _phase = Phase.Idle;
        _expected.Clear();
        if (wasRunning && status.Length > 0) _log?.Info(LogCategory, status);
        SetStatus(status);
    }

    private string StopLabel() => _stops.Count > 1 ? $"Shop {_index + 1} of {_stops.Count}: " : "";

    private void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke();
    }

    public void Dispose() => _inventory.ItemSold -= OnItemSold;
}

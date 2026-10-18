using System.Threading;
using MudPlay.Services;

namespace MudPlay.Game.Light;

// Buying a light at the shop an errand has walked to: one `buy <light>` per copy
// still short of the request's target count, then the first fresh copy landing
// ends it (one covers the dark ahead while the rest of the batch still buys), or
// the buy window passes with none (no gold, out of stock). The errand that owns
// the walk — the bank run's stop on the way back, the training walk's stop before
// the trainer — owns this too and is told how it went; it never touches the walker.
public sealed class LightShopBuy : IDisposable
{
    private readonly Func<int, int> _carriedCount;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly TimeSpan _timeout;
    private readonly LogService? _log;
    private readonly string _logCategory;

    private AutoLightBuyRequest? _buy;
    private int _baseCount;
    private Action<bool>? _done;
    private IDisposable? _window;

    // schedule(delay, action): runs action once after delay on the UI thread; the
    // handle cancels it. LightShopBuy.PostedTimer builds one for the client.
    public LightShopBuy(Func<int, int> carriedCount, Func<TimeSpan, Action, IDisposable> schedule,
        TimeSpan? timeout = null, LogService? log = null, string logCategory = AutoLightProvisioner.LogCategory)
    {
        ArgumentNullException.ThrowIfNull(carriedCount);
        ArgumentNullException.ThrowIfNull(schedule);
        _carriedCount = carriedCount;
        _schedule = schedule;
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
        _log = log;
        _logCategory = logCategory;
    }

    // A one-shot threadpool timer whose callback is posted back onto the UI thread.
    public static Func<TimeSpan, Action, IDisposable> PostedTimer(Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return (delay, action) => new Timer(_ => post(action), null, delay, Timeout.InfiniteTimeSpan);
    }

    // The buy in progress, for the owner's status line.
    public AutoLightBuyRequest? Buying => _buy;

    // Send the buys and wait. done(true) once a fresh copy lands, done(false) when
    // the window passes without one. A buy already running is replaced.
    public void Begin(AutoLightBuyRequest buy, Action<string> send, Action<bool> done)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(done);
        Cancel();
        _buy = buy;
        _done = done;
        _baseCount = _carriedCount(buy.ItemId);
        int toBuy = Math.Max(1, buy.Count - _baseCount);
        _log?.Info(_logCategory, $"at light shop — buying '{buy.LightName}' x{toBuy}");
        for (int i = 0; i < toBuy; i++) send($"buy {buy.LightName}");
        _window = _schedule(_timeout, OnTimeout);
    }

    // Inventory-change poll: the first fresh copy ends the buy.
    public void OnInventoryChanged()
    {
        if (_buy is not { } buy || _carriedCount(buy.ItemId) <= _baseCount) return;
        _log?.Info(_logCategory, $"'{buy.LightName}' acquired");
        End(bought: true);
    }

    // Stand down without telling the owner (it is being torn down itself).
    public void Cancel()
    {
        _window?.Dispose();
        _window = null;
        _buy = null;
        _done = null;
    }

    public void Dispose() => Cancel();

    private void OnTimeout()
    {
        if (_buy is not { } buy) return;
        if (_carriedCount(buy.ItemId) > _baseCount)
        {
            End(bought: true);
            return;
        }
        _log?.Info(_logCategory, $"buy of '{buy.LightName}' did not complete in time");
        End(bought: false);
    }

    private void End(bool bought)
    {
        Action<bool>? done = _done;
        Cancel();
        done?.Invoke(bought);
    }
}

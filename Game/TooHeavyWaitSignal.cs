using Avalonia.Threading;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game;

// "You are too heavy to move" — our carried weight is over our max encumbrance,
// usually because a debuff (frail's Encum% cut) lowered the max mid-fight. It
// isn't a hold: freedom / cure paralysis do nothing, and the leader can't see it.
// So we tell the leader ourselves — @wait (too heavy to move) — and read `i` for
// the lowered max. The wait holds until the weight is back under the max, either
// because items were dropped or because the debuff wore off; a fresh `i` every
// RecheckInterval is what notices the max coming back up.
public sealed class TooHeavyWaitSignal : IDisposable
{
    private const string LogCategory = "Party";
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(15);

    private readonly InventoryManager _inventory;
    private readonly PartyRestSync _restSync;
    private readonly LogService? _log;
    private readonly IDisposable _sub;
    private readonly WireSender _wire = new();
    private DispatcherTimer? _timer;
    private bool _tooHeavy;
    // The snapshot's reading predates the debuff until a fresh `i` lands, so a
    // Changed before then (a coin pickup) must not read as "back under max".
    private bool _awaitingFreshRead;
    private bool _disposed;

    public bool IsTooHeavy => _tooHeavy;

    internal List<byte[]> LastSentForTests => _wire.LastSentForTests;

    public TooHeavyWaitSignal(
        MessageRouter router, InventoryManager inventory, PartyRestSync restSync, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(restSync);
        _inventory = inventory;
        _restSync = restSync;
        _log = log;
        _sub = router.Subscribe(KnownPatterns.MovementFailedHeavy, _ => OnTooHeavy());
        _inventory.Changed += OnInventoryChanged;
        _inventory.FullInventoryParsed += OnFullInventoryParsed;
    }

    public void SetWireSender(Action<byte[]> sender) => _wire.Bind(sender);

    private void OnTooHeavy()
    {
        if (!_tooHeavy)
        {
            _tooHeavy = true;
            _log?.Info(LogCategory, "Too heavy to move — asking the leader to @wait until we're under our max encumbrance.");
        }
        // Resend: the leader may already be waiting on us for another reason, and
        // this one is worth naming.
        // The note comes from PartyRestSync.DefaultNote (TooHeavyNote) so the
        // string lives in exactly one place.
        _restSync.RequestWait(WaitReason.TooHeavy, resend: true);
        _awaitingFreshRead = true;
        Recheck();
        StartTimer();
    }

    private void OnFullInventoryParsed()
    {
        _awaitingFreshRead = false;
        OnInventoryChanged();
    }

    private void OnInventoryChanged()
    {
        if (!_tooHeavy || _awaitingFreshRead) return;
        EncumbranceReading enc = _inventory.Snapshot.Encumbrance;
        if (enc.MaxWeight <= 0 || enc.CurrentWeight > enc.MaxWeight) return;
        _tooHeavy = false;
        StopTimer();
        _log?.Info(LogCategory, $"Back under max encumbrance ({enc.CurrentWeight}/{enc.MaxWeight}) — sending @ok.");
        _restSync.RequestOk(WaitReason.TooHeavy);
    }

    private void Recheck() => _wire.Send("i");

    internal void RecheckForTests() => Recheck();

    private void StartTimer()
    {
        if (_timer is not null) return;
        _timer = new DispatcherTimer(RecheckInterval, DispatcherPriority.Background, (_, _) =>
        {
            if (_tooHeavy) Recheck();
            else StopTimer();
        });
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sub.Dispose();
        _inventory.Changed -= OnInventoryChanged;
        _inventory.FullInventoryParsed -= OnFullInventoryParsed;
        StopTimer();
    }
}

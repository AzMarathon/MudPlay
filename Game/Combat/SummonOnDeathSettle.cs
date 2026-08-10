using System;
using System.Text;
using Avalonia.Threading;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// Holds the movement loop for a brief settle when the auto-combat engine kills a
// monster that summons another on death, sending a bare CR to re-scan the room
// first (report paradigm-20260729-211336).
//
// The race this closes: killing the summoner clears the Combat gate and steps the
// walker synchronously (RemoveDeadEntity → EntitiesObserved → CombatStateTracker
// clears the gate → walker's next move) — all before the server's summon-arrival
// line is even received. The walker then drags the fresh summon into the next
// room, stacking rooms fast and making them deadly.
//
// So on a MonsterDied whose monster's DeathSpell summons (MonsterDeathSummonIndex),
// we assert SummonDeathSettleGate FIRST (this handler is wired before the roster-
// resync handler, so it runs before RemoveDeadEntity), keeping the walker paused
// even as the Combat gate clears on the now-empty observation, and send one CR.
// On the CR re-display one of two things happens: the summon surfaced and
// CombatStateTracker re-asserts the Combat gate to take over the hold (the walker
// fights it in-room), or nothing surfaced (a clean kill) and we clear on that same
// observation so the walker steps on — a delay bounded by one CR round-trip. A
// short timeout is the fallback for when no re-display lands. Only fires while a
// movement engine is actually driving, so a hand-fighting player gets no stray CR.
// The gate lives in the engine-wait tier (like CombatRedisplaySettleGate) so it
// never flips the toolbar Start/Pause/Stop.
public sealed class SummonOnDeathSettle : IDisposable
{
    public const string LogCategory = "SummonOnDeathSettle";

    public static readonly TimeSpan DefaultSettleWindow = TimeSpan.FromMilliseconds(1500);

    private readonly MonsterDeathWatcher _deathWatcher;
    private readonly RoomEntityClassifier _classifier;
    private readonly MovementCoordinator _coordinator;
    private readonly MonsterDeathSummonIndex _summonIndex;
    private readonly Func<string?> _currentTargetName;   // lazy Combat.CurrentTarget (assigned after this ctor)
    private readonly Func<bool> _movementActive;         // only hold/CR while an engine drives
    private readonly LogService? _log;
    private readonly TimeSpan _window;
    private readonly Action<TimeSpan, Action>? _scheduleOverride;

    private Action<byte[]>? _wireSender;
    private DispatcherTimer? _timer;
    private bool _asserted;
    private bool _disposed;

    public SummonOnDeathSettle(
        MonsterDeathWatcher deathWatcher,
        RoomEntityClassifier classifier,
        MovementCoordinator coordinator,
        MonsterDeathSummonIndex summonIndex,
        Func<string?> currentTargetName,
        Func<bool>? movementActive = null,
        LogService? log = null,
        TimeSpan? window = null,
        Action<TimeSpan, Action>? scheduleOverride = null)
    {
        ArgumentNullException.ThrowIfNull(deathWatcher);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(summonIndex);
        ArgumentNullException.ThrowIfNull(currentTargetName);
        _deathWatcher = deathWatcher;
        _classifier = classifier;
        _coordinator = coordinator;
        _summonIndex = summonIndex;
        _currentTargetName = currentTargetName;
        _movementActive = movementActive ?? (() => true);
        _log = log;
        _window = window ?? DefaultSettleWindow;
        _scheduleOverride = scheduleOverride;
        _deathWatcher.MonsterDied += OnMonsterDied;
        _classifier.EntitiesObserved += OnEntitiesObserved;
    }

    // Main-window VM supplies the EngineSendGate-wrapped SendUserInput. Without it
    // the CR can't be sent, so the settle stays inert.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    private void OnMonsterDied(MonsterDeathEvent evt)
    {
        if (_disposed || _wireSender is null || _asserted) return;
        if (!_movementActive()) return;             // hand-fighting player — no stray CR
        if (!DyingMonsterSummons(evt)) return;

        _asserted = true;
        _coordinator.AssertGate(MovementCoordinator.SummonDeathSettleGate, LogCategory,
            "summon-on-death kill — CR recheck before moving");
        _log?.Info(LogCategory, "summon-on-death kill — holding walker + sending CR recheck");
        _wireSender(Encoding.Latin1.GetBytes("\r"));
        RestartTimer();
    }

    // True when the dying monster's DeathSpell summons another. The specific-death
    // path carries the monster Number; the fallback path (no number) resolves the
    // current target's RawName against the room roster still present (removal
    // happens in a later handler).
    private bool DyingMonsterSummons(MonsterDeathEvent evt)
    {
        foreach (MonsterDeathIdentity id in evt.Candidates)
            if (id.Number is { } n && _summonIndex.SummonsOnDeath(n))
                return true;

        if (_currentTargetName() is { } target
            && _classifier.Current is { } obs)
        {
            foreach (RoomEntity e in obs.Entities)
                if (e.Kind == EntityKind.Monster
                    && e.MonsterNumber is { } mn
                    && string.Equals(e.RawName, target, StringComparison.OrdinalIgnoreCase)
                    && _summonIndex.SummonsOnDeath(mn))
                    return true;
        }
        return false;
    }

    // The CR re-display (or any fresh room render) landed — clear the settle gate.
    // If a summon surfaced, CombatStateTracker asserted the Combat gate on this same
    // observation and takes over the hold; if the room is empty, the walker steps on.
    private void OnEntitiesObserved(RoomEntitiesObservation _)
    {
        if (_disposed || !_asserted) return;
        ClearGate("room re-scanned");
    }

    private void RestartTimer()
    {
        if (_scheduleOverride is not null)
        {
            _scheduleOverride(_window, OnSettleElapsed);
            return;
        }
        _timer ??= new DispatcherTimer();
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _timer.Tick += OnTimerTick;
        _timer.Interval = _window;
        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        OnSettleElapsed();
    }

    private void OnSettleElapsed()
    {
        if (!_asserted) return;
        ClearGate("settle window elapsed — no re-display");
    }

    private void ClearGate(string reason)
    {
        _asserted = false;
        _timer?.Stop();
        _coordinator.ClearGate(MovementCoordinator.SummonDeathSettleGate, LogCategory, reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _deathWatcher.MonsterDied -= OnMonsterDied;
        _classifier.EntitiesObserved -= OnEntitiesObserved;
        if (_timer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnTimerTick;
            _timer = null;
        }
        if (_asserted)
        {
            _asserted = false;
            _coordinator.ClearGate(MovementCoordinator.SummonDeathSettleGate, LogCategory, "disposed");
        }
    }
}

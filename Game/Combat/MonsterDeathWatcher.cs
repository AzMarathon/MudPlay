using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Combat;

// Recognises monster deaths from the always-present exp signal. A kill prints, in a
// fixed order, the monster's death-flavor line → "You gain N experience." → *Combat
// Off*. The exp line is the reliable, monster-agnostic kill signal (see GAME_MECHANICS
// "Monster-kill message order"): an exp gain followed by a *Combat Off* within a short
// window is a death. Fires MonsterDied with IsFallback = true and no Candidates —
// consumers attribute the kill to whatever they were fighting
// (CombatManager.CurrentTarget) and force a roster re-display to pick the survivor.
//
// Per-monster death MESSAGES were retired: they're arbitrary per-monster flavor with
// no shared keyword and no distinctive colour, so a generic wording/colour matcher is
// infeasible. The exp line is the only reliable generic signal, and our own targeting
// names the mob — so the per-monster DeathLine data bought nothing and is gone.
public sealed class MonsterDeathWatcher : IDisposable
{
    // LogService category — appears as [MonsterDeath] rows per fire.
    public const string LogCategory = "MonsterDeath";

    // Window after a "You gain N exp." line within which a *Combat Off* qualifies as a
    // kill confirmation.
    private static readonly TimeSpan ExpToCombatOffWindow = TimeSpan.FromSeconds(5);

    private readonly LogService? _log;
    private readonly IDisposable _expSub;
    private readonly IDisposable _combatStatusSub;

    private DateTimeOffset? _lastExpAt;
    private int? _lastExpAmount;
    private bool _disposed;

    // Fires once per observed death. Subscribers run on the line-emitting thread.
    public event Action<MonsterDeathEvent>? MonsterDied;

    // Test seam — overrides the wall clock for the exp / Combat-Off correlation window.
    public Func<DateTimeOffset> NowProvider { get; set; } = () => DateTimeOffset.Now;

    public MonsterDeathWatcher(MessageRouter router, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _log = log;
        _expSub          = router.Subscribe(KnownPatterns.UserGainExperience, OnExp);
        _combatStatusSub = router.Subscribe(KnownPatterns.CombatStatus,        OnCombatStatus);
    }

    private void OnExp(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        if (!int.TryParse(m.Groups[0], out int exp)) return;
        _lastExpAt = NowProvider();
        _lastExpAmount = exp;
    }

    private void OnCombatStatus(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        if (!string.Equals(m.Groups[0], "Off", StringComparison.OrdinalIgnoreCase)) return;
        if (_lastExpAt is not { } expAt) return;

        DateTimeOffset now = NowProvider();
        if (now - expAt > ExpToCombatOffWindow) return;

        MonsterDeathEvent evt = new(
            Candidates:       Array.Empty<MonsterDeathIdentity>(),
            ExperienceGained: _lastExpAmount,
            At:               now,
            IsFallback:       true);
        _log?.Info(LogCategory,
            $"death — exp={_lastExpAmount} + *Combat Off* within {ExpToCombatOffWindow.TotalSeconds:F0}s");
        MonsterDied?.Invoke(evt);

        // Consumed — a second *Combat Off* must not re-fire on the same exp.
        _lastExpAt = null;
        _lastExpAmount = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _expSub.Dispose();
        _combatStatusSub.Dispose();
    }
}

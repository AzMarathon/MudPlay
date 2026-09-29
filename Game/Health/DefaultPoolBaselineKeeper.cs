using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Health;

// Keeps the Default-gear pool baseline (Models.Profile.DefaultPoolBaseline) the rest
// engine resolves its percentages against. Its values come only from a `stat` screen
// that showed Hits and the pool while the Default set was worn (user, 2026-09-28), so
// a Pre-rest set, or an `exp` screen carrying an older `stat`'s maxima, can't move it
// (report paradigm-20260928-223148). It only goes stale when the level changes or the
// Default set's own max-pool bonus does; then a `stat` goes out the next time the
// Default set is worn and nothing is in progress, and the old values stay in use until
// it lands.
public sealed class DefaultPoolBaselineKeeper
{
    public const string LogCategory = "PoolBaseline";

    private static readonly TimeSpan RefreshRetry = TimeSpan.FromSeconds(60);

    private readonly Func<DefaultPoolBaseline?> _read;
    private readonly Action<DefaultPoolBaseline> _write;
    private readonly Func<int> _level;
    private readonly Func<(int Hp, int Ma)?> _defaultGearBonus;
    private readonly Func<bool> _defaultWorn;
    private readonly Func<bool> _canCheckNow;
    private readonly Action _sendStat;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;
    private DateTimeOffset _lastAsked = DateTimeOffset.MinValue;

    // defaultGearBonus: the Default set's summed +MaxHP / +MaxMana, null when there's
    // no Default set. defaultWorn: the Default set is on right now.
    public DefaultPoolBaselineKeeper(
        Func<DefaultPoolBaseline?> read, Action<DefaultPoolBaseline> write,
        Func<int> level, Func<(int Hp, int Ma)?> defaultGearBonus, Func<bool> defaultWorn,
        Func<bool> canCheckNow, Action sendStat,
        Func<DateTimeOffset>? now = null, LogService? log = null)
    {
        _read = read;
        _write = write;
        _level = level;
        _defaultGearBonus = defaultGearBonus;
        _defaultWorn = defaultWorn;
        _canCheckNow = canCheckNow;
        _sendStat = sendStat;
        _now = now ?? (static () => DateTimeOffset.UtcNow);
        _log = log;
    }

    public DefaultPoolBaseline? Current => _read();

    // No baseline yet, or the level / Default set's pool bonus moved since it was read.
    public bool IsStale
    {
        get
        {
            if (_read() is not { } b) return true;
            if (_level() > 0 && b.Level != _level()) return true;
            return _defaultGearBonus() is { } g && (g.Hp != b.DefaultGearHp || g.Ma != b.DefaultGearMa);
        }
    }

    // A `stat` screen showed Hits and the pool. Recorded only with the Default set on.
    public void OnStatScreen(int maxHp, int maxMa)
    {
        if (maxHp <= 0 || _defaultGearBonus() is not { } gear || !_defaultWorn()) return;
        DefaultPoolBaseline next = new()
        {
            MaxHp = maxHp, MaxMa = maxMa, Level = _level(),
            DefaultGearHp = gear.Hp, DefaultGearMa = gear.Ma, RecordedAt = _now(),
        };
        if (_read() is { } cur && cur.MaxHp == next.MaxHp && cur.MaxMa == next.MaxMa
            && cur.Level == next.Level && cur.DefaultGearHp == next.DefaultGearHp && cur.DefaultGearMa == next.DefaultGearMa)
            return;
        _write(next);
        _log?.Info(LogCategory, $"Default-gear maxima recorded: HP {maxHp}, pool {maxMa} (level {next.Level})");
    }

    // Heartbeat: a stale baseline asks for a `stat` once the Default set is on and
    // nothing is in progress (retried at most once a minute).
    public void Poll()
    {
        if (!IsStale || _defaultGearBonus() is null || !_defaultWorn() || !_canCheckNow()) return;
        DateTimeOffset now = _now();
        if (now - _lastAsked < RefreshRetry) return;
        _lastAsked = now;
        _log?.Info(LogCategory, _read() is null
            ? "no Default-gear maxima yet — checking `stat`"
            : "level or Default gear changed — re-reading `stat` for the Default-gear maxima");
        _sendStat();
    }
}

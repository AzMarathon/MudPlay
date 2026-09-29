using MudPlay.Game.Combat;
using MudPlay.Game.Spells;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game;

// Keeps party members' HP moving between `par` polls: damage the round ledger credits
// to a member comes off, heals seen landing on them go on (HealLineReader). `par` and
// a member's @health reply stay the truth — PartyManager, which owns the HP fields,
// drops the estimate whenever either states the real value. The point is the party
// heal: CastingDirector reads PartyMember.HpPercent, so it can react to a member's
// dip in the same round rather than after the next poll.
//
// Only members whose max HP is known (the @health exchange between MudPlay clients)
// are estimated; the rest stay on `par` alone. With none in the party, every line
// leaves on that one check.
public sealed class PartyHpEstimator : IDisposable
{
    public const string LogSource = "PartyHp";

    // A `par` correction this large is worth an Info row; smaller ones are routine
    // rounding and drift, logged at Debug.
    private const int NotableCorrectionPercent = 10;

    private readonly MessageRouter _router;
    private readonly RoundDamageTracker _damage;
    private readonly PartyManager _party;
    private readonly Func<HealLineReader> _buildReader;
    private readonly Func<int> _ownLevel;
    private readonly LogService? _log;
    private readonly Func<string?, bool, int> _casterLevel;
    private HealLineReader? _reader;
    private bool _disposed;

    // buildReader makes the heal reader from the active game data; it's called
    // lazily, the first time a line needs it after Invalidate. ownLevel is our level
    // (0 when unknown), for averaging our own unnumbered heals.
    public PartyHpEstimator(
        MessageRouter router, RoundDamageTracker damage, PartyManager party,
        Func<HealLineReader> buildReader, Func<int> ownLevel, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(damage);
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(buildReader);
        ArgumentNullException.ThrowIfNull(ownLevel);
        _router = router;
        _damage = damage;
        _party = party;
        _buildReader = buildReader;
        _ownLevel = ownLevel;
        _log = log;
        _casterLevel = CasterLevel;

        _router.LineDispatched += OnLine;
        _damage.Attributed += OnAttributed;
        _party.HpEstimateCorrected += OnCorrected;
    }

    // The heal spells or their messages changed (a game-data set switch, a message
    // edit): rebuild the reader on next use.
    public void Invalidate() => _reader = null;

    // "N heal lines from M spells" once the reader is built, for the bug report.
    public string ReaderSummary => _reader is { } r
        ? $"{r.TemplateCount} heal lines from {r.SpellCount} spells"
        : "(not built yet — builds on the first line seen with an estimable member)";

    private HealLineReader Reader()
    {
        if (_reader is { } built) return built;
        HealLineReader reader = _buildReader();
        _log?.Info(LogSource, $"Heal reader built: {reader.TemplateCount} heal lines from {reader.SpellCount} spells.");
        return _reader = reader;
    }

    private void OnAttributed(AttributedLine line)
    {
        string? target = line.Sides.Target;
        if (target is null || target == DamageLineAttributor.Self || line.Sides.Amount <= 0) return;
        if (!_party.HasHpEstimableMember()) return;
        // A few heals word their amount "for N damage" (close wounds), which the ledger
        // reads as a hit; the heal side counts that line instead.
        if (Reader().TryRead(line.Text, _casterLevel, out _)) return;
        if (_party.AdjustMemberHp(target, -line.Sides.Amount) is { } hp)
            LogAdjust(hp, -line.Sides.Amount, "damage");
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        if (!_party.HasHpEstimableMember()) return;
        if (!Reader().TryRead(line.Text, _casterLevel, out HealSeen heal)) return;
        Apply(heal);
    }

    private void Apply(HealSeen heal)
    {
        string why = WhyText(heal);
        if (heal.Target is { } target)
        {
            if (_party.AdjustMemberHp(target, heal.Amount) is { } hp) LogAdjust(hp, heal.Amount, why);
            return;
        }

        // A party heal heals the caster's party — ours only when we cast it, felt it,
        // or a member of ours cast it.
        bool ourParty = heal.Ours || heal.ViewedAsMember
            || (heal.Caster is { } caster && FindMember(caster) is not null);
        if (!ourParty)
        {
            if (_log?.IsDebugEnabled == true)
                _log.Debug(LogSource, $"Ignored {why}: the caster isn't in our party.");
            return;
        }
        List<string> names = new();
        foreach (PartyMember m in _party.State.Members)
            if (!m.IsSelf) names.Add(m.Name);
        foreach (string name in names)
            if (_party.AdjustMemberHp(name, heal.Amount) is { } hp) LogAdjust(hp, heal.Amount, why);
    }

    // Caster's level for an unnumbered heal: ours, a member's known level, else 0 (the
    // spell's lowest level).
    private int CasterLevel(string? caster, bool ours)
    {
        if (ours) return _ownLevel();
        return caster is not null && FindMember(caster) is { } m ? m.KnownLevel : 0;
    }

    private PartyMember? FindMember(string name)
    {
        string given = GivenName(name);
        foreach (PartyMember m in _party.State.Members)
            if (!m.IsSelf && GivenName(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    private static string GivenName(string name)
    {
        string t = name.Trim();
        int space = t.IndexOf(' ');
        return space < 0 ? t : t[..space];
    }

    private static string WhyText(HealSeen heal)
    {
        string by = heal.Ours ? "us" : heal.Caster ?? "unknown caster";
        return heal.AmountShown
            ? $"{heal.SpellName} by {by}"
            : $"{heal.SpellName} by {by}, average at level {(heal.CasterLevel > 0 ? heal.CasterLevel.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown")}";
    }

    private void LogAdjust((string Name, int Hp, int MaxHp) hp, int delta, string why)
    {
        if (_log?.IsDebugEnabled != true) return;
        _log.Debug(LogSource, $"{hp.Name} {delta:+#;-#} ({why}) → ~{hp.Hp}/{hp.MaxHp}");
    }

    private void OnCorrected(string name, int estimatedPct, int statedPct, string source)
    {
        if (_log is null) return;
        string text = $"{source} corrected {name}: estimate {estimatedPct}% → {statedPct}%.";
        if (Math.Abs(estimatedPct - statedPct) >= NotableCorrectionPercent) _log.Info(LogSource, text);
        else _log.Debug(LogSource, text);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.LineDispatched -= OnLine;
        _damage.Attributed -= OnAttributed;
        _party.HpEstimateCorrected -= OnCorrected;
    }
}

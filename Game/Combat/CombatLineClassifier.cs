using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Terminal;
using static MudPlay.Terminal.LineExtractor;

namespace MudPlay.Game.Combat;

// Generic, monster-agnostic combat-line recognizer. Subscribes to every dispatched
// line and, for lines inside a *Combat Engaged* … *Combat Off* window (the gating
// the realm itself emits), classifies the outcome from LINE COLOUR + wording +
// target — with no per-monster message data, so it works for any monster including
// the 15-20k a custom game can add.
//
// Colour signals (indexed SGR foreground): cyan (6, or bright 14) = a swing that
// missed or was dodged; dark-red (1, NON-bold) = a swing your armor deflected. The
// player's own hits render bright-red (1 + bold) and always carry a "for N damage"
// tail, so they classify by wording, not colour. Target is split us-vs-others by
// whether the line addresses "you".
//
// This PR only SURFACES the classification (event + LastKind) for the Wire
// Inspector's classified view and future recognition consumers; the per-monster
// MonsterMessages data remains the authoritative fallback for the engine.
public sealed class CombatLineClassifier : IDisposable
{
    private readonly MessageRouter _router;
    private bool _inCombatWindow;
    private bool _disposed;

    // Recent combat-window lines + how each was classified, for the Wire Inspector's
    // classified view and the bug-report capture. Only lines inside a combat window
    // (plus the *Combat Engaged*/*Combat Off* markers) are kept, so the log is a
    // focused combat trace rather than the whole terminal stream. Bounded ring.
    private const int LogCap = 1000;
    private readonly object _logLock = new();
    private readonly Queue<ClassifiedLine> _log = new();

    // The most recent line's classification (None for non-combat / out-of-window
    // lines). The Wire Inspector reads this per dispatched line.
    public CombatLineKind LastKind { get; private set; }

    // Fired for each line that classifies as a real combat outcome (kind != None).
    public event Action<EmittedLine, CombatLineKind>? LineClassified;

    public CombatLineClassifier(MessageRouter router)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _router.LineDispatched += OnLine;
    }

    private void OnLine(EmittedLine line)
    {
        // Track the combat window off the server's own markers — these lines are not
        // combat outcomes themselves. LineDispatched fires per line, so the window is
        // already set by the time the round's outcome lines arrive.
        if (line.Text.StartsWith("*Combat Engaged*", StringComparison.Ordinal))
        {
            _inCombatWindow = true;
            LastKind = CombatLineKind.None;
            Record(line.Text, CombatLineKind.None);
            return;
        }
        if (line.Text.StartsWith("*Combat Off*", StringComparison.Ordinal))
        {
            Record(line.Text, CombatLineKind.None);   // record before closing the window
            _inCombatWindow = false;
            LastKind = CombatLineKind.None;
            return;
        }

        (TerminalColor fg, bool bold) = DominantForeground(line);
        CombatLineKind kind = Classify(line.Text, fg, bold, _inCombatWindow);
        LastKind = kind;
        if (_inCombatWindow)
            Record(line.Text, kind);
        if (kind != CombatLineKind.None)
            LineClassified?.Invoke(line, kind);
    }

    // Pure classification (unit-tested directly). Only lines inside a combat window
    // classify; outside, everything is None.
    public static CombatLineKind Classify(string text, TerminalColor fg, bool bold, bool inWindow)
    {
        if (!inWindow || string.IsNullOrEmpty(text)) return CombatLineKind.None;

        bool hit = HitDamage.IsMatch(text);

        // The local player's own swing ("You hurl … for N damage!" / "You miss …!").
        if (text.StartsWith("You ", StringComparison.Ordinal))
        {
            if (hit) return CombatLineKind.PlayerHit;
            if (IsCyan(fg)) return CombatLineKind.PlayerMiss;
            return CombatLineKind.None;
        }

        // Incoming / third-party. Addressing "you"/"your" marks it as against us;
        // otherwise it landed on another player.
        bool vsYou = You.IsMatch(text);

        if (hit)
            return vsYou ? CombatLineKind.MonsterHitYou : CombatLineKind.MonsterHitOther;

        if (IsCyan(fg))
        {
            bool dodge = text.Contains("dodge", StringComparison.OrdinalIgnoreCase);
            if (dodge) return vsYou ? CombatLineKind.DodgeYou : CombatLineKind.DodgeOther;
            return vsYou ? CombatLineKind.MonsterMissYou : CombatLineKind.MonsterMissOther;
        }

        if (IsDarkRed(fg, bold))
            return vsYou ? CombatLineKind.ArmorBlockYou : CombatLineKind.ArmorBlockOther;

        return CombatLineKind.None;
    }

    // "… for N damage" is the hit marker, colour-independent.
    private static readonly Regex HitDamage =
        new(@"\bfor \d+ damage\b", RegexOptions.Compiled);
    // "you" / "your" as an addressee — the us-vs-others split.
    private static readonly Regex You =
        new(@"\byou(?:r)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // cyan = indexed 6 (standard) or 14 (bright).
    private static bool IsCyan(TerminalColor fg) =>
        fg.Kind == ColorKind.Indexed && (fg.Value == 6 || fg.Value == 14);

    // dark red = indexed 1 without bold. Bright red (indexed 1 + bold) is the
    // player's own hit colour, NOT an armor block — so bold disqualifies.
    private static bool IsDarkRed(TerminalColor fg, bool bold) =>
        fg.Kind == ColorKind.Indexed && fg.Value == 1 && !bold;

    // The line's colour: the foreground of its first visible (non-space) glyph.
    // Combat lines are emitted in a single colour, so the first glyph is
    // representative; blanks fall back to Default.
    private static (TerminalColor Fg, bool Bold) DominantForeground(EmittedLine line)
    {
        CellAttributes[] attrs = line.Attributes;
        string text = line.Text;
        int n = Math.Min(text.Length, attrs.Length);
        for (int i = 0; i < n; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return (attrs[i].Foreground, (attrs[i].Flags & CellFlags.Bold) != 0);
        }
        return (TerminalColor.Default, false);
    }

    private void Record(string text, CombatLineKind kind)
    {
        lock (_logLock)
        {
            _log.Enqueue(new ClassifiedLine(text, kind));
            while (_log.Count > LogCap) _log.Dequeue();
        }
    }

    // The most recent up-to-`max` combat-window lines with their classification.
    public IReadOnlyList<ClassifiedLine> SnapshotLog(int max = LogCap)
    {
        lock (_logLock)
        {
            int skip = Math.Max(0, _log.Count - max);
            return _log.Skip(skip).ToArray();
        }
    }

    // Render the classified log as text: each line, with a "[Combat: <label>]" tag
    // appended to the lines that classified. Shared by the Wire Inspector's
    // classified pane and the bug-report capture.
    public string RenderLog(int max = LogCap)
    {
        var sb = new System.Text.StringBuilder();
        foreach (ClassifiedLine e in SnapshotLog(max))
        {
            sb.Append(e.Text);
            string label = Label(e.Kind);
            if (label.Length != 0) sb.Append("    [Combat: ").Append(label).Append(']');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    // Drop the classified-line log (Wire Inspector's Clear button).
    public void Clear()
    {
        lock (_logLock) _log.Clear();
    }

    // Human-readable tag for the classified view; empty for None.
    public static string Label(CombatLineKind kind) => kind switch
    {
        CombatLineKind.PlayerHit        => "You Hit",
        CombatLineKind.PlayerMiss       => "You Miss",
        CombatLineKind.MonsterHitYou    => "Monster Hit (you)",
        CombatLineKind.MonsterHitOther  => "Monster Hit (other)",
        CombatLineKind.ArmorBlockYou    => "Armor Block (you)",
        CombatLineKind.ArmorBlockOther  => "Armor Block (other)",
        CombatLineKind.DodgeYou         => "You Dodge",
        CombatLineKind.DodgeOther       => "Other Dodge",
        CombatLineKind.MonsterMissYou   => "Monster Miss (you)",
        CombatLineKind.MonsterMissOther => "Monster Miss (other)",
        _ => "",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.LineDispatched -= OnLine;
    }
}

// One classified combat-window line: the (de-ANSI'd) text and how it was read.
public readonly record struct ClassifiedLine(string Text, CombatLineKind Kind);

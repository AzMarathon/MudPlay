using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game.Combat;

// Tells a command's *Combat Off* from a kill's, for the line being dispatched.
//
// The game prints *Combat Off* for two unrelated reasons. A kill prints it straight
// after its "You gain N experience." line, with at most a redrawn prompt between the
// two, on both realms (GAME_MECHANICS "Kill detection and monster-kill message
// order"). A command that stops a running fight (a new attack, a cast, `break`,
// `rest`, a gear command) prints it in answer to that command (GAME_MECHANICS
// "Non-swing actions break combat (casting, equipping)").
//
// Taking the second kind for the first cost a fight: the Off of an attack sent at a
// room spell's survivor, right after that spell's kills, was read as one more kill,
// the survivor and the roster were dropped, and the buff cast a moment later had
// nothing to resume (report paradigm-20261010-145330).
//
// Two rules, in this order:
//   1. An Off straight after an exp line is that kill's, whatever else was read.
//      Text the game appends to a prompt row without clearing it reads as an echo
//      (another player's attack announce arrives that way), and this is what keeps
//      such a row from ever costing a real kill.
//   2. Any other Off is a command's when a command has been echoed since the last
//      exp line. Usually the echo is the line before it. It need not be: the prompt
//      may be redrawn in between, and so may other lines land there, if the game
//      echoes a command typed ahead before the round it then answers it after.
//      Rule 1 is what makes looking that far back safe: a kill in between brings
//      its own exp line, which also drops the echo.
//
// With no echo read since the exp line the answer is null and callers keep the
// timing rules they had. That covers a statline the extractor can't split, an echo
// the game printed on a row of its own, and an Off no command drew (a stun, a
// target gone at the round, an attack that stops after each strike).
public sealed class CommandOffProbe : IDisposable
{
    private readonly MessageRouter _router;
    private readonly IDisposable _expSub;
    private readonly IDisposable _statusSub;

    private bool _lineIsExp;
    private bool _previousLineWasExp;
    // The command echoed on the line being dispatched, and the last one echoed on a
    // line before it since the exp line. Kept apart so that a line is never asked
    // whether it answers itself.
    private string? _echoOnLine;
    private string? _echoSinceExp;
    private bool _disposed;

    public CommandOffProbe(MessageRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
        _router.LineDispatched += OnLineDispatched;
        _expSub = router.Subscribe(KnownPatterns.UserGainExperience, OnExp);
        // A *Combat Off* or *Combat Engaged* the game left on a prompt row is its own
        // line, not a command, and must not answer for the next Off.
        _statusSub = router.Subscribe(KnownPatterns.CombatStatus, _ => _echoOnLine = null);
    }

    // The echoed command the line now being dispatched answers, or null when it
    // directly follows the exp line or no command was echoed since. Only meaningful
    // from a handler of that line.
    public string? CommandAnswered => _previousLineWasExp ? null : _echoSinceExp;

    // Runs ahead of every pattern handler of the same line, so by the time one asks,
    // the fields are about the lines before it. A redrawn prompt is not a line of
    // its own: it neither separates an exp line from its Off nor ends an echo.
    private void OnLineDispatched(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        _previousLineWasExp = _lineIsExp;
        _lineIsExp = false;
        if (_echoOnLine is not null) _echoSinceExp = _echoOnLine;
        _echoOnLine = _router.LineIsCommandEcho && line.Text.Trim() is { Length: > 0 } echo
            ? echo
            : null;
    }

    // After OnLineDispatched for the same line, so an exp line that itself sat on a
    // prompt row is cleared here and never counts as a command.
    private void OnExp(MatchResult _)
    {
        _lineIsExp = true;
        _echoOnLine = null;
        _echoSinceExp = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.LineDispatched -= OnLineDispatched;
        _expSub.Dispose();
        _statusSub.Dispose();
    }
}

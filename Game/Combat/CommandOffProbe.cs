using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game.Combat;

// Tells a command's *Combat Off* from a kill's, for the line being dispatched.
//
// The game prints *Combat Off* for two unrelated reasons. A kill prints it straight
// after its "You gain N experience." line. A command that stops a running fight (a
// new attack, a cast, `break`, `rest`, a gear command) prints it as the first line
// after that command's echo, ahead of anything else the command says (GAME_MECHANICS
// "Non-swing actions break combat (casting, equipping)"). The two can't be the same
// line: a kill's exp line and its Off come in one burst from the game (GAME_MECHANICS
// "Kill detection and monster-kill message order"), so no echo lands between them,
// and a command that does kill prints that exp line before the kill's Off.
//
// Taking the second kind for the first cost a fight: the Off of an attack sent at a
// room spell's survivor, right after that spell's kills, was read as one more kill,
// the survivor and the roster were dropped, and the buff cast a moment later had
// nothing to resume (report paradigm-20261010-145330).
//
// An Off straight after the exp line is the kill's whatever the router made of the
// row before it: text the game appends to a prompt row without clearing it (another
// player's attack announce arrives that way) reads as an echo too. With no echo read
// (a statline the extractor can't split) the answer is null, and callers keep the
// timing rules they had.
public sealed class CommandOffProbe : IDisposable
{
    private readonly MessageRouter _router;
    private readonly IDisposable _expSub;

    private bool _lineIsExp;
    private bool _previousLineWasExp;
    private bool _disposed;

    public CommandOffProbe(MessageRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
        _router.LineDispatched += OnLineDispatched;
        _expSub = router.Subscribe(KnownPatterns.UserGainExperience, _ => _lineIsExp = true);
    }

    // The echoed command the line now being dispatched answers, or null when no echo
    // was read ahead of it or the exp line was. Only meaningful from a handler of
    // that line.
    public string? CommandAnswered
        => !_previousLineWasExp && _router.CommandEchoedBeforeLine is { Length: > 0 } echo
            ? echo
            : null;

    // Runs ahead of every pattern handler of the same line, so by the time one asks,
    // _previousLineWasExp is about the line before it. A redrawn prompt between the
    // two is not a line of its own.
    private void OnLineDispatched(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        _previousLineWasExp = _lineIsExp;
        _lineIsExp = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _router.LineDispatched -= OnLineDispatched;
        _expSub.Dispose();
    }
}

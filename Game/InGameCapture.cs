using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;

namespace MudPlay.Game;

// Tracks the time spent inside the game, so a bug report copies only that much of
// the terminal. A report carries the backscroll, and one made at login would
// otherwise carry the board's login screen with it: the account name as typed, and
// whatever the board shows around the password. The terminal, the Backscroll window
// and the Wire Inspector still show all of it; only what a report copies out of
// them is held to these stretches (user, 2026-10-08).
//
// In: the first statline prompt (the same signal every other "are we in the game"
// latch uses). It is seen before the terminal draws the bytes that carried it, so
// the room the character lands in counts as in the game with it.
// Out: the link dropping, a new link starting, the realm's "Your character has been
// saved" on exit, or the board's main menu. The last two arrive mid-chunk, so the
// screen that carries them counts and the ones after it don't. A chat line shaped
// like either can only cost the few rows before the next prompt opens it again.
public sealed class InGameCapture : IDisposable
{
    private const string LogCategory = "Capture";

    private readonly WireBuffer _wire;
    private readonly WirePromptScanner _prompts;
    private readonly LogService? _log;
    private readonly IDisposable _savedSub;
    private readonly IDisposable _menuSub;

    public InGameCapture(MessageRouter router, WirePromptScanner prompts, WireBuffer wire, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(wire);
        _prompts = prompts;
        _wire = wire;
        _log = log;
        _prompts.PromptObserved += OnPrompt;
        _savedSub = router.Subscribe(KnownPatterns.RealmExitSaved, _ => Leave("the character was saved on exit"));
        _menuSub = router.Subscribe(KnownPatterns.MainMenuEnterRealm, _ => Leave("the board's main menu is showing"));
    }

    // The stretches spent in the game; a transcript row counts when it was
    // written inside one (TranscriptSnapshot.Tail's `only`).
    public CaptureWindow Window { get; } = new(on: false);

    public bool InGame => Window.IsOn;

    // How much of the wire had been read when the game was last entered; null
    // while out of it. A report's raw wire section starts from here.
    public long? WireMark { get; private set; }

    public void NotifyConnected() => Leave("a new connection");
    public void NotifyDisconnected() => Leave("the link dropped");

    private void OnPrompt(PromptObservation _)
    {
        if (Window.IsOn) return;
        Window.Set(true, DateTimeOffset.Now);
        WireMark = _wire.TotalBytes;
        _log?.Info(LogCategory, "in the game: a bug report copies the terminal from here");
    }

    private void Leave(string why)
    {
        if (!Window.IsOn) return;
        Window.Set(false, DateTimeOffset.Now);
        WireMark = null;
        _log?.Info(LogCategory, $"out of the game ({why}): a bug report copies nothing from here until the next game prompt");
    }

    public void Dispose()
    {
        _prompts.PromptObserved -= OnPrompt;
        _savedSub.Dispose();
        _menuSub.Dispose();
    }
}

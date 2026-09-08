using System.Text;
using System.Threading.Tasks;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// Turns the passive SysRoomStatusParser into a request/response: send a sysop
// status and await the room block. Callers (location recovery, Roomba recon)
// want to ask, not to overhear. Same split as AbilBreakdownParser (parses) and
// ManaRegenReroller (sends `abil 145` and consumes the result).
//
// Capability is two-layered, because sysop access can't be detected reliably:
//
//   1. BbsCredentials.SysopStatus — the character's per-BBS "I have sysop
//      status powers" flag, resolved through AppServices.SysopStatusEnabledHere
//      and passed in as the capabilityEnabled gate. Default off; until it's on,
//      nothing is ever sent.
//   2. Auto-disable — a probe that produces no parseable block within the
//      timeout switches the capability off for the rest of the session. This is
//      deliberately NOT a match on a denial message: the wording a denied sysop
//      command produces is unconfirmed, and a timeout is the right answer
//      whatever the server said. Every caller treats a null result as normal and
//      falls back to what it did before.
//
// Only the no-argument form is implemented. `sysop status room <n>` would let us
// read any room without travelling (boss presence, for one), but the game's help
// describes its argument as a bare room number while rooms are map/room pairs
// here, and that wire format is unconfirmed — so it waits for its own design
// rather than being guessed at.
public sealed class SysStatusProbe : IDisposable
{
    private const string LogCategory = "SysStatus";
    private const string StatusCommand = "sys st";

    private readonly SysRoomStatusParser _parser;
    private readonly Func<bool> _capabilityEnabled;
    private readonly LogService? _log;

    private Action<byte[]>? _wireSender;
    private TaskCompletionSource<SysRoomStatus?>? _pending;
    private bool _disposed;

    // How long to wait for the block before giving up and auto-disabling.
    // Generous: the dump is several lines and a busy BBS can be slow.
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(6);

    // Test seam — lets a test drive the timeout without real time.
    public Func<TimeSpan, Task> DelayProvider { get; set; } = Task.Delay;

    // True while probing is switched off after a timeout. NOT permanent: a single
    // unanswered probe used to disable sysop status for the rest of the session,
    // which cost one user ~7 hours of position recovery after one slow reply — the
    // same command answered fine 16 minutes later. The point of auto-disable is to
    // stop hammering an account that lacks the privilege, and one retry every few
    // minutes does that just as well while surviving a hiccup.
    public bool AutoDisabled
        => !_provenAvailable && _disabledUntilUtc is { } until && DateTimeOffset.UtcNow < until;

    // Set the first time a probe actually returns a room block. That answers the
    // only question auto-disable exists to ask — does this account have the
    // privilege — and answers it permanently for the session. Every later timeout
    // is lag, a mangled block, or output racing the window, none of which are
    // reasons to stop using a capability we have watched work.
    private bool _provenAvailable;

    // How long a timeout switches probing off for. Long enough that an account
    // without sysop powers sends a handful of refused commands an hour rather than
    // one per recovery; short enough that a transient failure costs minutes.
    public TimeSpan AutoDisableFor { get; set; } = TimeSpan.FromMinutes(5);

    private DateTimeOffset? _disabledUntilUtc;

    // Whether a probe would actually be sent right now.
    public bool Available => !_disposed && !AutoDisabled && _wireSender is not null && _capabilityEnabled();

    public SysStatusProbe(SysRoomStatusParser parser, Func<bool> capabilityEnabled, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(capabilityEnabled);
        _parser = parser;
        _capabilityEnabled = capabilityEnabled;
        _log = log;
        _parser.StatusParsed += OnStatusParsed;
    }

    // Bind the wire-sender. MainWindowViewModel supplies SendUserInput.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Clear a session's auto-disable — called on profile load, so switching
    // characters doesn't inherit a previous session's failed probe.
    public void ResetAutoDisable()
    {
        if (_disabledUntilUtc is null) return;
        _disabledUntilUtc = null;
        _log?.Log(LogSeverity.Info, LogCategory, "Auto-disable cleared.");
    }

    // Send a sysop status for the current room and await the parsed block.
    // Returns null when the capability is off, a probe is already in flight and
    // its own wait times out, or nothing parseable came back. Null is an
    // ordinary outcome; callers fall through to their existing behaviour.
    public async Task<SysRoomStatus?> QueryAsync()
    {
        if (!Available)
        {
            _log?.Log(LogSeverity.Debug, LogCategory, "Probe skipped — capability unavailable.");
            return null;
        }

        // One probe at a time — the wire serialises anyway, and a second
        // in-flight send would race two blocks onto one completion source.
        if (_pending is { Task: { IsCompleted: false } inFlight })
            return await inFlight.ConfigureAwait(false);

        TaskCompletionSource<SysRoomStatus?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;

        _log?.Log(LogSeverity.Info, LogCategory, $"Probing with `{StatusCommand}`.");
        _wireSender?.Invoke(Encoding.Latin1.GetBytes(StatusCommand + "\r\n"));

        Task completed = await Task.WhenAny(tcs.Task, DelayProvider(Timeout)).ConfigureAwait(false);
        if (completed != tcs.Task)
        {
            _pending = null;
            if (_provenAvailable)
            {
                _log?.Log(LogSeverity.Info, LogCategory,
                    $"No room block within {Timeout.TotalSeconds:0}s. Sysop status has worked this "
                    + "session, so this is a hiccup — staying enabled.");
                return null;
            }
            _disabledUntilUtc = DateTimeOffset.UtcNow + AutoDisableFor;
            _log?.Log(LogSeverity.Info, LogCategory,
                $"No room block within {Timeout.TotalSeconds:0}s and none has ever come back — sysop "
                + $"status off for {AutoDisableFor.TotalMinutes:0} minute(s), then retried.");
            return null;
        }

        return await tcs.Task.ConfigureAwait(false);
    }

    private void OnStatusParsed(SysRoomStatus status)
    {
        // Any block at all — solicited or a hand-typed `sys st` — proves the
        // privilege exists.
        if (!_provenAvailable)
        {
            _provenAvailable = true;
            _disabledUntilUtc = null;
            _log?.Log(LogSeverity.Info, LogCategory,
                "Sysop status confirmed working — it won't be auto-disabled again this session.");
        }

        TaskCompletionSource<SysRoomStatus?>? pending = _pending;
        _pending = null;
        pending?.TrySetResult(status);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _parser.StatusParsed -= OnStatusParsed;
        _pending?.TrySetResult(null);
        _pending = null;
    }
}

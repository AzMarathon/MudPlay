using MudPlay.Terminal;

namespace MudPlay.Game.Map;

// Watches LineExtractor for the server echoing a typed command back on the
// prompt line ("[HP=..]:e") and reports it to RoomTracker. That echo is the
// causal signal that a move actually executed: the game echoes the command as it
// processes it, immediately before rendering the destination room, whereas a
// spontaneous redisplay (an NPC walking through, a regen-tick refresh, a
// post-combat redraw) carries no echo. RoomTracker uses it to tell a move's real
// landing from a stray re-look in an identically-named grid (issue #478) without
// relying on the fragile round-trip timing heuristic.
//
// LineExtractor splits a prompt row that carries trailing text ("[HP=..]:e") into
// two back-to-back EmittedLines — the prompt half (IsPromptLine, "[HP=..]:") then
// the trailing content ("e") — BOTH stamped with the same row timestamp. A bare
// prompt ("[HP=..]:" with nothing typed) emits only the prompt line; the room
// display that follows is a separate row with a later timestamp. So the echoed
// command is exactly "a non-prompt line immediately following a prompt line and
// sharing its timestamp"; the same-timestamp test is what rejects the room-name
// line that follows a bare prompt (which would otherwise clobber the real echo).
// RoomTracker.NoteInboundMoveEcho is the final authority — it keeps only an echo
// that names the move in flight — so forwarding a non-move echo here is harmless.
public sealed class InboundMoveEchoScanner : IDisposable
{
    private readonly LineExtractor _lines;
    private readonly RoomTracker _tracker;

    private bool _prevWasPrompt;
    private DateTimeOffset _prevPromptAt;

    public InboundMoveEchoScanner(LineExtractor lines, RoomTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(tracker);
        _lines = lines;
        _tracker = tracker;
        _lines.LineEmitted += OnLineEmitted;
    }

    public void Dispose() => _lines.LineEmitted -= OnLineEmitted;

    internal void FeedTestLine(LineExtractor.EmittedLine line) => OnLineEmitted(line);

    private void OnLineEmitted(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine)
        {
            _prevWasPrompt = true;
            _prevPromptAt = line.Timestamp;
            return;
        }

        bool isPromptEcho = _prevWasPrompt && line.Timestamp == _prevPromptAt;
        _prevWasPrompt = false;
        if (!isPromptEcho) return;

        string command = line.Text.Trim();
        if (command.Length == 0) return;
        _tracker.NoteInboundMoveEcho(command, line.Timestamp);
    }
}

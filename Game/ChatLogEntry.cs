namespace MudPlay.Game;

// One classified chat / realm-event line. Emitted by ChatRouter and consumed
// by ChatHistoryStore, the ConversationWindow, and any logging / alerting
// subsystem.
//
// Timestamp: wall-clock time the originating line was emitted.
// Channel: which channel the line is classified into.
// Speaker: player name when known — for an outgoing telepath, the recipient the
//   echo names. null for realm-events with no named subject and for our own says
//   and yells. Specific semantics per channel are documented inline in
//   ChatRouter. A yell heard from the next room does not name the yeller; its
//   Speaker is the word the game prints, "Someone".
// Message: the message text. For realm events this is a short verb phrase
//   ("entered the Realm" / "left the Realm" / "disconnected").
// RawText: the original LineExtractor.EmittedLine.Text so downstream consumers
//   can fall back to the verbatim form when the classified split isn't
//   sufficient.
// DirectedTo: for a directed say (`X says (to Target) "msg"`, or our own outgoing
//   `>Target msg`), the target the say is aimed at. null for an undirected say and
//   every other channel. The conversation window renders it as `X (to Target)`.
// HeardFrom: for a yell heard from the next room, where it came from in the game's
//   own words ("above", "below", "the north"). null for every other line, including
//   a same-room or our own yell. Kept apart from DirectedTo because that one is a
//   player's name. The conversation window renders it as `Someone (from the north)`.
public readonly record struct ChatLogEntry(
    DateTimeOffset Timestamp,
    ChatChannel Channel,
    string? Speaker,
    string Message,
    string RawText,
    string? DirectedTo = null,
    string? HeardFrom = null);

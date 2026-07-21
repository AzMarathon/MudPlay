using System.Collections.Generic;
using FujinTerm.Services;

namespace FujinTerm.Game.Leaderboard;

// Passively captures a "top N" listing as it scrolls past on the live terminal
// and hands the finished snapshot to the per-BBS store. Capture is entirely
// player-driven: the user runs `top <N>` in-game whenever they want a fresh
// reading (each realm rebuilds its rankings on its own cadence), and this watches
// the emitted lines for the block.
//
// State machine over LineExtractor.LineEmitted:
//   idle       — watch for the echoed "top N" request (records the count) and the
//                "Rank Name … Experience" header (derives column layout, begins).
//   collecting — accumulate ranked rows until a non-row / prompt line ends it,
//                then commit the snapshot.
// The trailing prompt line ([HP=..]:) is the reliable block terminator.
public sealed class LeaderboardCaptureTracker : IDisposable
{
    private readonly LeaderboardSnapshotStore _store;
    private readonly LogService? _log;

    private Terminal.LineExtractor? _lines;
    private bool _disposed;

    // Pending capture state.
    private int _pendingRequested;
    private bool _collecting;
    private LeaderboardParser.Columns _columns;
    private DateTimeOffset _capturedAt;
    private readonly List<LeaderboardEntry> _entries = new();

    public LeaderboardCaptureTracker(LeaderboardSnapshotStore store, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _log = log;
    }

    // Bind the per-session LineExtractor — the source of the completed terminal
    // lines the block is stitched from. Same shape as GroundItemTracker.
    public void AttachLineExtractor(Terminal.LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    private void OnLine(Terminal.LineExtractor.EmittedLine line)
    {
        // The prompt after the last row is the clean end-of-block marker; while
        // idle it's just noise.
        if (line.IsPromptLine)
        {
            if (_collecting) CommitBlock();
            return;
        }

        string text = line.Text;

        if (_collecting)
        {
            if (LeaderboardParser.TryParseRow(text, _columns, out LeaderboardEntry entry))
            {
                _entries.Add(entry);
                return;
            }
            // The header's "=-=-" underline arrives before any rows — skip it and
            // keep waiting. Once rows are in, any non-row line ends the block.
            if (_entries.Count > 0) CommitBlock();
            return;
        }

        int requested = LeaderboardParser.ParseRequestedCount(text);
        if (requested > 0)
        {
            _pendingRequested = requested;
            return;
        }

        if (LeaderboardParser.TryParseHeader(text, out LeaderboardParser.Columns cols))
        {
            _collecting = true;
            _columns = cols;
            _capturedAt = line.Timestamp;
            _entries.Clear();
        }
    }

    private void CommitBlock()
    {
        if (_entries.Count > 0)
        {
            var snapshot = new LeaderboardSnapshot(
                _capturedAt, _pendingRequested, _entries.ToList());
            _store.Add(snapshot);
        }

        _collecting = false;
        _pendingRequested = 0;
        _entries.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}

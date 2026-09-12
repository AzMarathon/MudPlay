using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game;

// Watches the wire for lines that look like a spell/buff/debuff/proc message
// the Messages catalogue doesn't have an entry for, and stages them in
// MessageCandidateStore for human review — the client noticing its own gaps
// instead of silently mis-parsing until navigation/combat/condition-tracking
// misbehaves and someone goes digging through logs to find out why.
//
// A line survives to become a candidate only if it clears every exclusion in
// order: not a prompt line, not near-empty, not our own echoed command, not the
// room's light announcement, and unrecognized by all four catalogues the app
// already carries —
//   * MessageStore's LITERAL slots, compared as text, plus the Confused records'
//     ConfuseFumbleLine (rebuilt on MessageStore.Messages.CollectionChanged,
//     mirroring ConditionTracker's identical trigger);
//   * MessageStore's TEMPLATED slots via MessageTemplateIndex — the
//     {source} casts {spellname} on {target}! shapes, which are the bulk of the
//     catalogue and cannot be compared as text;
//   * the stateful block parsers that register no router pattern (the `par`
//     roster), each asked through its own matcher;
//   * every pattern in MessageRouter's catalog (movement, combat-round text,
//     chat, item get/drop, party, doors, and more) — reusing already-reviewed
//     domain knowledge instead of inventing a "looks like a spell line" heuristic.
// What's left is genuinely unclassified text.
//
// Staging lags the wire by one line so a monster's death flavour, which no
// catalogue can hold (realms author it per species and don't publish it), can be
// identified by the experience line that always follows it and dropped.
//
// Several multi-line parsers in this codebase (who-list, spellbook, stat
// screen, inventory, shop stock) read the wire directly and never register an
// IMessagePattern, so every row of one of those listings would otherwise look
// exactly like "never seen, no pattern matched" and flood the candidate
// queue. The burst cap below bounds that: a real spell/buff/proc line arrives
// solo or as a small 1-3-line caster/target/witness cluster, never a 6+-line
// burst, so capping distinct-never-before-seen lines within a short window
// stops a dump cold without meaningfully risking a genuine miss.
//
// Never writes a MessageRecord itself — a bare line can't say which
// perspective slot it belongs in or what MessageFlags apply. That's always a
// human decision made through the LogPane double-click flow or the Game Data
// Browser's Unrecognized Lines tab, both of which reuse the existing
// MessageEditDialogViewModel via ViewModels.GameData.Edit.MessageCandidateCommit.
public sealed class MessageCandidateWatcher : IDisposable
{
    // LogService category — appears as [MessageCandidate] rows. Also the
    // RegisterDetailHandler key App.axaml.cs wires the review dialog to.
    public const string LogCategory = "MessageCandidate";

    // Below this trimmed length a line is near-empty noise ("OK", a blank
    // continuation) rather than a candidate worth staging.
    private const int MinLineLength = 4;

    // Provisional, tunable — reasoned estimates about client-observed
    // line-delivery timing for a listing/dump, NOT a confirmed game
    // mechanic. Adjust once real captures show how tight/loose a genuine
    // multi-line dump actually arrives versus a real spell/buff cluster.
    private static readonly TimeSpan BurstWindow = TimeSpan.FromMilliseconds(1500);
    private const int BurstCap = 6;

    private readonly MessageRouter _router;
    private readonly MessageStore _messages;
    private readonly MessageCandidateStore _candidates;
    private readonly Func<RoomKey?>? _currentRoom;
    private readonly Func<string, bool>? _isKnownRoomName;
    private readonly Func<string, bool>? _isRecognizedByDirectParser;
    private readonly LogService? _log;

    // Built from MessageStore on every CollectionChanged — trimmed text of every
    // non-empty LITERAL perspective slot across every record. Exact-match: a
    // literal slot is the whole line the server prints, so anything looser would
    // start swallowing lines the catalogue doesn't actually describe.
    private HashSet<string> _knownLines = new(StringComparer.Ordinal);

    // The TEMPLATED slots ({source} casts {spellname} on {target}!) — never
    // comparable as text, so they're compiled and matched. Without this every
    // templated message in the catalogue reads as unrecognized, which is what used
    // to stage known casts ("Raijin casts minor healing on Raijin!") for review.
    private MessageTemplateIndex _templates;

    // Literal AppliedEndsWith wordings, matched as SUBSTRINGS to mirror what
    // ConditionTracker actually does with them. The stored wording omits the
    // server's trailing punctuation ("The effects of way of the tiger wear off"
    // vs the wire's "… wear off!"), so an exact test never fires and every
    // buff-expiry line in the game looked unrecognized.
    private List<string> _appliedEndsWith = new();

    private LineExtractor? _lines;
    private bool _disposed;

    // Burst suppression slides on the LAST line, not the window's first, so a long
    // dump (stat screen / inventory listing) stays suppressed after the first few
    // rather than leaking another BurstCap every BurstWindow.
    private DateTimeOffset _lastBurstLine;
    private int _burstCount;
    private bool _burstSuppressedLogged;

    // True once we're actually in the realm this session — set on the first game
    // prompt (PromptScanner), reset when a new session's LineExtractor is attached.
    // Gates out everything before the game: the startup splash, the BBS login menu /
    // banner, and the client's own connect / import status lines.
    private bool _inGame;

    // Commands the user just sent, kept briefly so their echo (the server bounces
    // typed input back on the prompt line) isn't staged as an "unrecognized" line.
    // Fed by ObserveOutbound from the same SendUserInput path the other outbound
    // observers use; pruned lazily on lookup.
    private static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(3);
    private readonly Dictionary<string, DateTimeOffset> _recentCommands = new(StringComparer.Ordinal);

    // Live gate mirroring LogDiagnosticState.CaptureUnrecognizedMessages —
    // AppServices pushes updates on Changed, matching HopTimingCalibrator's
    // wiring. Defaults true so the watcher behaves correctly even if
    // something forgets to wire it explicitly.
    public bool Enabled { get; set; } = true;

    // currentRoom is a copy-out of the player's live position, read at capture
    // time so a staged candidate carries a "first seen here" locator hint.
    // Null-tolerant: a null provider (or a null return before the room is
    // known) simply stages the candidate without a location.
    // isRecognizedByDirectParser covers the stateful block parsers that read the wire
    // straight and register no router pattern, so AnyPatternMatches can't speak for
    // them — the `par` roster rows PartyManager consumes are the case that drove this.
    // Composed in AppServices from each parser's own matcher so the shapes aren't
    // duplicated here.
    public MessageCandidateWatcher(MessageRouter router, MessageStore messages,
        MessageCandidateStore candidates, Func<RoomKey?>? currentRoom = null,
        LogService? log = null, Func<string, bool>? isKnownRoomName = null,
        Func<string, bool>? isRecognizedByDirectParser = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(candidates);
        _router = router;
        _messages = messages;
        _candidates = candidates;
        _currentRoom = currentRoom;
        _isKnownRoomName = isKnownRoomName;
        _isRecognizedByDirectParser = isRecognizedByDirectParser;
        _log = log;

        _templates = new MessageTemplateIndex(messages.Messages);
        RebuildIndex();
        _messages.Messages.CollectionChanged += OnMessagesChanged;
    }

    // Bind to the per-session LineExtractor so every inbound line is scanned.
    // Idempotent — re-attaching to the same extractor is a no-op. Mirrors
    // ConditionTracker.AttachLineExtractor.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
        // A new session's extractor means we're back at the splash / login — hold
        // capture until the first in-game prompt of this session (NotifyInGame).
        _inGame = false;
        _pending = null;
    }

    // Set on the first game prompt this session (PromptScanner.PromptObserved, wired
    // in AppServices). Gates capture on so the pre-game stream — splash, BBS login
    // menu / banner, connect status — never stages candidates.
    public void NotifyInGame() => _inGame = true;

    // Note a command the user just sent so its echo (the server bounces typed input
    // back) isn't staged as an unrecognized line. Called from SendUserInput next to
    // the other outbound observers. Latin-1 to match the wire; each CR/LF-split piece
    // is tagged with the send time.
    public void ObserveOutbound(byte[] data)
    {
        if (data is null || data.Length == 0) return;
        foreach (string part in System.Text.Encoding.Latin1.GetString(data).Split('\r', '\n'))
        {
            string cmd = part.Trim();
            if (cmd.Length > 0) _recentCommands[cmd] = DateTimeOffset.UtcNow;
        }
    }

    private void OnMessagesChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RebuildIndex();

    private void RebuildIndex()
    {
        HashSet<string> known = new(StringComparer.Ordinal);
        List<string> endsWith = new();
        foreach (MessageRecord r in _messages.Messages)
        {
            AddIfNotEmpty(known, r.CasterMessage);
            AddIfNotEmpty(known, r.TargetMessage);
            AddIfNotEmpty(known, r.WitnessMessage);
            AddIfNotEmpty(known, r.AppliedMessage);
            // Substring, not exact — see _appliedEndsWith. Templated wordings are
            // the template index's job; only literals land here.
            if (!MessageRecord.IsBlankOrAbsent(r.AppliedEndsWith)
                && r.AppliedEndsWith!.Trim() is { Length: > 0 } ends
                && !ends.Contains('{'))
                endsWith.Add(ends);
            // ConfuseFumbleLine is a recognized wire line too (it drives
            // MovementRefusalDetector via ConditionTracker.IsConfuseFumbleLine),
            // but it reaches the app through a predicate, NOT a router pattern —
            // so without indexing it here a known fumble line ("You look around
            // stupidly.") would be falsely staged as an unrecognized candidate.
            // Multi-line (one wording per row), same split ConditionTracker uses.
            if (!MessageRecord.IsBlankOrAbsent(r.ConfuseFumbleLine))
                foreach (string wording in r.ConfuseFumbleLine.Split('\n'))
                    AddIfNotEmpty(known, wording);
        }
        _knownLines = known;
        _appliedEndsWith = endsWith;
        _templates = new MessageTemplateIndex(_messages.Messages);
    }

    // Templated text is skipped: a slot carrying a {placeholder} can't be compared
    // to a line as text, so it belongs to MessageTemplateIndex instead.
    private static void AddIfNotEmpty(HashSet<string> set, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string trimmed = text.Trim();
        if (!trimmed.Contains('{')) set.Add(trimmed);
    }

    // True when a literal AppliedEndsWith wording appears in the line, matching
    // ConditionTracker's own Contains semantics for these.
    private bool MatchesAppliedEndsWith(string text)
    {
        foreach (string wording in _appliedEndsWith)
            if (text.Contains(wording, StringComparison.Ordinal)) return true;
        return false;
    }

    // A full-line "[ … ]" is the client's own WriteTerminalStatus notice
    // ("[CONNECTING TO: …]", "[… Quest is Now Available]", "[MDB IMPORT …]") — never
    // a server message, so drop it regardless of in-game state.
    private static bool IsClientStatusLine(string text) =>
        text.Length >= 2 && text[0] == '[' && text[^1] == ']';

    // The bright-cyan title line of a room display. RoomDisplayParser reads room
    // displays straight off the wire and registers no MessageRouter pattern, so a
    // room name looks like "never seen, no pattern matched" and gets staged (the
    // description lines that follow are caught by the burst cap, which is why only
    // the title leaks). We key off the Rooms table, NOT the colour: player- and
    // monster-spell lines can share the room-name colour depending on the user's
    // palette, so a colour test would suppress the very unrecognized spell messages
    // this watcher exists to catch — whereas a real spell message is never a Rooms
    // row. Predicate keeps the watcher decoupled from GameDataCache; null / no data
    // → no skip (unchanged behaviour before a set is imported).
    private bool IsKnownRoomName(string text) => _isKnownRoomName?.Invoke(text) ?? false;

    // True when text is the echo of a command the user sent within EchoWindow.
    // Prunes stale entries opportunistically (the set is tiny).
    private bool IsRecentCommand(string text, DateTimeOffset now)
    {
        if (_recentCommands.Count == 0) return false;
        if (_recentCommands.TryGetValue(text, out DateTimeOffset when) && now - when <= EchoWindow)
            return true;
        if (_recentCommands.Count > 32)
        {
            List<string> stale = new();
            foreach (KeyValuePair<string, DateTimeOffset> kv in _recentCommands)
                if (now - kv.Value > EchoWindow) stale.Add(kv.Key);
            foreach (string k in stale) _recentCommands.Remove(k);
        }
        return false;
    }

    private void OnLine(LineExtractor.EmittedLine line)
    {
        if (!Enabled) return;
        if (line.IsPromptLine) return;
        // Before the game: splash animation, BBS login menu / banner, and the
        // client's connect / import status. Nothing there is a server message.
        if (!_inGame) return;

        string text = line.Text.Trim();

        // A monster's death flavour is the line immediately before the experience
        // line. Realms author those per species and don't publish them, so the
        // catalogue can never recognize one and every kill would stage a fresh
        // candidate. Staging is therefore held back by a single line: an experience
        // gain retires the held line as death flavour instead of committing it.
        // Room-spell flavour is unaffected — nothing follows it.
        if (IsExperienceGain(line))
        {
            RetirePendingAsDeathLine();
            return;
        }

        // Cheap own-output filters first, before the costlier catalogue scans:
        //  - near-empty noise ("OK", a blank continuation)
        //  - the client's own "[…]" status notices (WriteTerminalStatus)
        //  - the echo of a command we or the user just sent
        // None of these can become a candidate, and none may RELEASE the held one
        // either: a blank line or a status notice landing between the death message
        // and the experience line would otherwise defeat the positional rule above.
        if (text.Length < MinLineLength) return;
        if (IsClientStatusLine(text)) return;
        if (IsRecentCommand(text, line.Timestamp)) return;

        // A real server line that isn't the experience gain — whatever was held can't
        // be death flavour, so stage it.
        CommitPending();

        // Known from game data, so never a review candidate.
        if (Light.LightModel.IsRoomLightPhrase(text)) return;
        if (_knownLines.Contains(text)) return;
        if (IsKnownRoomName(text)) return;
        if (_isRecognizedByDirectParser?.Invoke(text) == true) return;
        if (MatchesAppliedEndsWith(text)) return;
        if (_templates.Matches(text)) return;
        if (_router.AnyPatternMatches(line)) return;
        // A dismissed candidate is a final verdict — drop every recurrence
        // outright: no re-add, no occurrence bump, no re-alert.
        if (_candidates.IsDismissed(text)) return;

        DateTimeOffset now = line.Timestamp;

        // A repeat of an already-staged candidate is always let through —
        // dedup via RecordSighting is free and doesn't grow the catalogue —
        // only a genuinely new candidate is subject to the burst cap. The window
        // slides on the LAST line so a long dump (stat screen / inventory) stays
        // suppressed after the first BurstCap, instead of leaking another every
        // BurstWindow.
        if (!_candidates.Contains(text))
        {
            if (now - _lastBurstLine > BurstWindow)
            {
                _burstCount = 0;
                _burstSuppressedLogged = false;
            }
            _lastBurstLine = now;
            _burstCount++;
            if (_burstCount > BurstCap)
            {
                if (!_burstSuppressedLogged)
                {
                    _burstSuppressedLogged = true;
                    _log?.Debug(LogCategory,
                        $"suppressed a burst of unrecognized lines (>{BurstCap} distinct within "
                        + $"{BurstWindow.TotalMilliseconds:F0}ms) — likely a listing/dump");
                }
                return;
            }
        }

        (int? map, int? room) = _currentRoom?.Invoke() is { } key
            ? ((int?)key.Map, (int?)key.Room)
            : (null, null);
        // Held rather than staged — see the death-flavour rule at the top of OnLine.
        _pending = new PendingCandidate(text, now, map, room);
    }

    // A vetted line waiting to see whether an experience gain follows it (which
    // would make it a monster's death flavour rather than a candidate).
    private sealed record PendingCandidate(string Text, DateTimeOffset When, int? Map, int? Room);

    private PendingCandidate? _pending;

    private bool IsExperienceGain(LineExtractor.EmittedLine line) =>
        _router.TryGetPattern(Services.Patterns.KnownPatterns.UserGainExperience,
            out IMessagePattern pattern) && pattern.TryMatch(line, out _);

    // Stage the held line for real.
    private void CommitPending()
    {
        if (_pending is not { } p) return;
        _pending = null;
        (_, bool isNew) = _candidates.RecordSighting(p.Text, p.When, p.Map, p.Room);
        if (isNew)
            _log?.Warn(LogCategory,
                $"unrecognized line — double-click to review: '{Truncate(p.Text, 80)}'",
                context: p.Text);
    }

    // The held line turned out to be death flavour. Drop it, and also un-stage any
    // row an earlier session already captured for the same text so the queue heals
    // itself instead of carrying death messages forever. A room-spell line that
    // happens to land right before an experience gain once loses its row and
    // re-stages on its next sighting — cheaper than leaving every kill in the queue.
    private void RetirePendingAsDeathLine()
    {
        if (_pending is not { } p) return;
        _pending = null;
        // Only the clean-up is worth a log line — every kill hits this path, so
        // logging the routine case would bury the log during a grind.
        if (!_candidates.Contains(p.Text)) return;
        _candidates.Remove(MessageCandidateRecord.ComputeId(p.Text));
        _log?.Debug(LogCategory,
            $"un-staged a monster death message (it precedes the experience line): '{Truncate(p.Text, 80)}'");
    }

    // Test-only counter so each SimulateCapture() call injects a distinct
    // never-before-seen line (staging a fresh candidate rather than bumping one).
    private int _simCounter;

    // Feed a synthetic, guaranteed-unrecognized line straight through the real
    // capture path (OnLine → exclusion checks → room tag → dedup → stage) so the
    // Unrecognized Lines tab can be exercised without waiting for the game to
    // emit an unknown message. Honours the Enabled gate exactly like a real line:
    // with capture off it stages nothing (the point of the feature). Returns the
    // line it injected. Wired to the tab's test-only "Simulate entry" button.
    public string SimulateCapture()
    {
        _inGame = true;   // the test button stands in for a real in-game line
        string line = $"A shimmering test rune flickers and fades. [sim {++_simCounter}]";
        OnLine(new LineExtractor.EmittedLine(
            line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        // Real capture defers staging by one line to catch death flavour; the test
        // button has no following line, so commit now or nothing would ever appear.
        CommitPending();
        return line;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _messages.Messages.CollectionChanged -= OnMessagesChanged;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}

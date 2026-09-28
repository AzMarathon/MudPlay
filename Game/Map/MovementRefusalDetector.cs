using System.Linq;
using System.Text.RegularExpressions;
using MudPlay.Services;
using MudPlay.Terminal;

namespace MudPlay.Game.Map;

// Watches for the canonical MajorMUD "your move didn't happen" lines and
// notifies RoomTracker so a Pending move reverts to Located at the previous
// room. The pattern set is extended as new refusal phrasings turn up in real
// sessions — keep the patterns anchored (^…$) so chat lines that quote these
// phrases don't false-trigger.
public sealed partial class MovementRefusalDetector : IDisposable
{
    private readonly LineExtractor _lines;
    private readonly RoomTracker _tracker;
    private readonly LogService? _log;

    // Recognizes a confusion-fumble wire line as a move-refusal, from game data rather
    // than a hardcoded regex: the fumble wordings ("You fumble in confusion!", plus a
    // spell's own wording like convulsions' "You convulse violently") live on Confused
    // MessageRecords' ConfuseFumbleLine and are queried via ConditionTracker. Left null
    // in tests that don't exercise the confusion path.
    private readonly Func<string, bool>? _isConfuseFumbleLine;

    public MovementRefusalDetector(LineExtractor lines, RoomTracker tracker, LogService? log = null,
        Func<string, bool>? isConfuseFumbleLine = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(tracker);
        _lines = lines;
        _tracker = tracker;
        _log = log;
        _isConfuseFumbleLine = isConfuseFumbleLine;
        _lines.LineEmitted += OnLineEmitted;
    }

    public void Dispose() => _lines.LineEmitted -= OnLineEmitted;

    internal void FeedTestLine(string text, DateTimeOffset? when = null)
        => HandleLine(text, when ?? DateTimeOffset.UtcNow);

    private void OnLineEmitted(LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        HandleLine(line.Text, line.Timestamp);
    }

    private void HandleLine(string text, DateTimeOffset when)
    {
        // A closed-door refusal reverts the move like any other, but ALSO clears
        // the stale "door open" flag for the attempted direction — the door shut
        // since we last saw the room, so the next attempt must re-open it rather
        // than bonk the shut door again (the mid-combat door-closed bonk loop).
        if (DoorIsClosed().IsMatch(text))
        {
            _tracker.NoteDoorClosed(when);
            _log?.Info("MoveRefusal", $"door closed: {text.Trim()}");
            return;
        }

        // Door and exit changes someone else made, which name the direction
        // (Stock DLL _cmd_open / _cmd_close / _background_update_exits; the same
        // on Paradigm). The tracker decides how each applies to where we are.
        if (DoorToDirectionChanged().Match(text) is { Success: true } doorChange
            && DirectionExtensions.TryFromLongName(doorChange.Groups["dir"].Value, out Direction doorDir))
        {
            if (doorChange.Groups["what"].Value.Equals("opened", StringComparison.OrdinalIgnoreCase))
                _tracker.NoteNamedDoorOpened(doorDir);
            else
                _tracker.NoteNamedDoorClosed(doorDir, when);
            _log?.Info("MoveRefusal", $"door to {doorDir.ToLongName()} {doorChange.Groups["what"].Value}: {text.Trim()}");
            return;
        }
        if (SeenDoorChanged().Match(text) is { Success: true } seen
            && DirectionExtensions.TryFromLongName(seen.Groups["dir"].Value, out Direction seenDir))
        {
            if (seen.Groups["what"].Value.Equals("open", StringComparison.OrdinalIgnoreCase))
                _tracker.NoteNamedDoorOpened(seenDir);
            else
                _tracker.NoteNamedDoorClosed(seenDir, when);
            _log?.Info("MoveRefusal", $"saw the door to {seenDir.ToLongName()} {seen.Groups["what"].Value}ed: {text.Trim()}");
            return;
        }
        if (ExitToDirectionChanged().Match(text) is { Success: true } exitChange
            && DirectionExtensions.TryFromLongName(exitChange.Groups["dir"].Value, out Direction exitDir))
        {
            bool opened = exitChange.Groups["what"].Value.Equals("opened", StringComparison.OrdinalIgnoreCase);
            _tracker.NoteNamedExitChanged(exitDir, opened);
            _log?.Info("MoveRefusal", $"exit to {exitDir.ToLongName()} {(opened ? "opened" : "closed")}: {text.Trim()}");
            return;
        }

        // The typing-rate limiter silently dropped the command we just sent
        // ("You are typing too quickly - command ignored"). If that command was
        // the move in flight, it never executed — un-count it so the tracker
        // doesn't run a room ahead through a same-named grid (issue #478).
        // NoteCommandDropped self-guards: it reverts only a recently-sent,
        // still-Pending move, since this line doesn't name what it dropped.
        // "You are too afraid!" refuses whatever we sent while feared, move or not,
        // so it reverts a move only through the same self-guarding path.
        if (TypingTooQuickly().IsMatch(text) || TooAfraid().IsMatch(text))
        {
            _tracker.NoteCommandDropped(when);
            _log?.Info("MoveRefusal", $"command dropped (typing too quickly): {text.Trim()}");
            return;
        }

        // A confusion fumble consumes the just-sent command — for a MOVE the step never
        // lands, so revert like any other refusal. The wordings come from game data
        // (Confused records' ConfuseFumbleLine) via the injected predicate, not a
        // hardcoded regex; combat re-sends its own lost swing on ConditionTracker.ActionFailed.
        if (!Patterns.Any(p => p.IsMatch(text)) && _isConfuseFumbleLine?.Invoke(text) != true) return;

        _tracker.NoteMoveBlocked(when);
        _log?.Info("MoveRefusal", $"blocked: {text.Trim()}");
    }

    // Refusal patterns. Anchored to the whole line so quoted chat doesn't
    // false-trigger. Terminators tolerate both '.' and '!' — Paradigm ends its
    // refusal lines with '!' ("There is no exit in that direction!") where stock
    // uses '.', and a bonked move that never matched left the tracker's Pending
    // move stranded. Add new variants here as we observe them in real sessions.
    private static readonly Regex[] Patterns =
    {
        CantMoveDirection(),
        CantGoThatWay(),
        NoExitThatDirection(),
        TooImpairedToMove(),
        CantSeeWellEnoughToMove(),
        TooEncumberedToMove(),
        FlatOnYourBack(),
        AlignmentBlocksExit(),
    };

    [GeneratedRegex(
        @"^\s*You can't move (in )?that direction[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CantMoveDirection();

    [GeneratedRegex(
        @"^\s*You can't go that way[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CantGoThatWay();

    [GeneratedRegex(
        @"^\s*There is no exit (in )?that direction[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoExitThatDirection();

    // Paralyzed / confused / stunned variants — "You are too <state> to move.";
    // the Stock engine says "You are too stunned to move anywhere!".
    [GeneratedRegex(
        @"^\s*You are too (paralyzed|confused|stunned|dazed) to move(?: anywhere)?[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TooImpairedToMove();

    [GeneratedRegex(
        @"^\s*You can't see well enough to move[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CantSeeWellEnoughToMove();

    [GeneratedRegex(
        @"^\s*You are too encumbered to move[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TooEncumberedToMove();

    // Knocked down — the server refuses the move with this while we're held.
    // SelfHeldResponder normally holds the loop before a move goes out, but a
    // move already in flight when the knockdown lands (or a manual move while
    // down) still bonks this way; recognizing it keeps the tracker from
    // stranding on the unresolved step. Also the knockdown NOTICE itself, which
    // NoteMoveBlocked treats as a no-op when nothing is pending.
    [GeneratedRegex(
        @"^\s*You are flat on your back[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FlatOnYourBack();

    // Door / gate blocking — server returns this when the user issues a direction
    // whose exit is shut. Both the plain and the "in that direction" long form are
    // covered, Stock's "There is a closed door in that direction!", and "gate" as
    // well as "door" (a fortress gate opened by a `pull
    // winch` prerequisite bonks with "The gate is closed!" — without matching it,
    // the pending move never reverts and the tracker latches in Pending, swallowing
    // even the post-open redisplay: the walker stalls forever, report
    // paradigm-20260827-113513).
    [GeneratedRegex(
        @"^\s*(?:The (?:door|gate) is closed(?: in that direction)?|There is a closed door in that direction)[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoorIsClosed();

    // A door or gate in this room changed from the other side or by itself: "The
    // door to the north just closed.", "…just opened.", "The gate to the east just
    // locked!" (a timed re-lock also shuts it).
    [GeneratedRegex(
        @"^\s*The (?:door|gate) to the (?<dir>\w+) just (?<what>closed|opened|locked)[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DoorToDirectionChanged();

    // Someone in this room opened or closed one of its doors: "You see Bob close
    // the door to the north."
    [GeneratedRegex(
        @"^\s*You see .+? (?<what>open|close) the (?:door|gate) to the (?<dir>\w+)[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeenDoorChanged();

    // A timed or action-opened exit (a lever's hidden passage) opened or shut:
    // "The exit to the west just opened!"
    [GeneratedRegex(
        @"^\s*The exit to the (?<dir>\w+) just (?<what>opened|closed)[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExitToDirectionChanged();

    // Alignment-gated exit — Paradigm refuses an exit whose "(Alignment: X to Y)"
    // band excludes the mover ("Your current alignment prevents you from entering
    // this exit."). The router isn't alignment-aware yet, so a route planned through
    // such an exit bonks here; recognizing it reverts the pending move cleanly
    // instead of stranding the tracker (report paradigm-20260827-144553).
    [GeneratedRegex(
        @"^\s*Your current alignment prevents you from entering this exit[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlignmentBlocksExit();

    // Typing-rate limiter dropped the just-sent command. Unlike the refusals
    // above this is NOT necessarily a move (it drops whatever was typed), so it
    // routes to NoteCommandDropped — which self-guards on a recently-sent,
    // still-Pending move — rather than the unconditional NoteMoveBlocked. The
    // preceding "Why don't you slow down for a few seconds?" warning carries no
    // drop, so only the "command ignored" line reverts anything.
    [GeneratedRegex(
        @"^\s*You are typing too quickly - command ignored[.!]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypingTooQuickly();

    // Feared: the game refuses any action (attack, item use, a move) with this.
    [GeneratedRegex(
        @"^\s*You are too afraid!\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TooAfraid();

    // Confusion-fumble wordings ("You fumble in confusion!", convulsions' "You convulse
    // violently" / "You look around stupidly and do nothing") are no longer hardcoded
    // here — they live on Confused MessageRecords' ConfuseFumbleLine and reach HandleLine
    // through the _isConfuseFumbleLine predicate (ConditionTracker.IsConfuseFumbleLine),
    // so the user can correct a spell's fumble wording in game data without an engine edit.
}

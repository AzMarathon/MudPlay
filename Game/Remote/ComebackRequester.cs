using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Remote;

// Follower-side @comeback sender. A follower the party leader walked off without
// telepaths one @comeback <map>/<room> (bare when our room isn't Confirmed) so the
// leader's PartyComebackManager comes back for it. A telepath is all it sends:
// nothing here moves this character.
//
// How the client knows it was left behind (GAME_MECHANICS "@comeback (follower →
// leader)"):
//   - "You are no longer following X." right behind a follow move we couldn't
//     make: a move refusal just before it, a hold in force, or the leader seen
//     walking out with no follow move of ours after it.
//   - A move refusal nobody asked for. A follower sends no moves, so an exit
//     refusal with no command of ours in flight answers the follow move the game
//     made for us. Stock ends the follow there without another word (a closed
//     door, a missing item, a toll, a level / class / alignment gate), so that
//     line is all there is to go on.
//   - The leader seen leaving ("X just left to the north.") with no follow line
//     and no new room for us by SettleTime.
//   - Our own `par` listing the leader as [Invited]
//     (PartyManager.LeaderListedAsInvited).
//
// What is never one: the same "no longer following" line with none of that before
// it (an uninvite, a disband, the leader teleported or gone), our own `leave` or a
// move of our own, our death or the leader's, and a party teleport the leader
// relayed or walked us into, where the leader's client regroups on landing.
//
// One request per incident. Once a split has been answered, sent or withheld,
// nothing more goes out until we are following again; a leader who declines is
// logged and not asked a second time.
public sealed partial class ComebackRequester : IDisposable
{
    private const string LogCategory = "Comeback";

    // Longest gap between the evidence of a failed follow move (a refusal, the
    // leader's departure) and the "You are no longer following X." it explains.
    // The game prints them in one tick, so this only covers lag.
    private static readonly TimeSpan LeftBehindWindow = TimeSpan.FromSeconds(3);

    // How long a refusal or the leader's departure waits before it is called a
    // left-behind. The follow move is made in the same tick as the leader's, so a
    // follow line or the "no longer following" line that names the cause is here
    // well inside it.
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    // A refusal this soon after a move of our own answers that move, not a follow
    // move. The leader side reads its own moves over the same window.
    private static readonly TimeSpan OwnMoveWindow = TimeSpan.FromSeconds(5);

    // A typed room command can be refused with an exit's wording ("A strange power
    // holds you back!"), and its answer comes at once.
    private static readonly TimeSpan TypedReplyWindow = TimeSpan.FromSeconds(2);

    // A `leave` or a move of our own this recently is why the follow ended.
    private static readonly TimeSpan VoluntaryWindow = TimeSpan.FromSeconds(5);

    // A relayed party teleport can wait several seconds before it moves anyone,
    // and the split comes with it.
    private static readonly TimeSpan TeleportWindow = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan DeathWindow = TimeSpan.FromSeconds(10);

    // How long the leader's telepathed answer to our request is watched for.
    private static readonly TimeSpan AnswerWindow = TimeSpan.FromSeconds(60);

    private readonly RoomTracker _tracker;
    private readonly PartyState? _party;
    private readonly LogService? _log;
    private readonly Func<bool>? _isMovementPrevented;
    private readonly Func<bool> _isAutoEnabled;
    private readonly Func<bool>? _isSelfDown;
    private readonly Func<string?>? _sendBlocked;
    private readonly List<IDisposable> _subs = new();
    private readonly DispatcherTimer _settleTimer;

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    // ----- evidence, each stamped when its line or command went by -----
    private DateTimeOffset _cantMoveAt = DateTimeOffset.MinValue;
    private string _cantMoveText = string.Empty;
    private DateTimeOffset _exitRefusedAt = DateTimeOffset.MinValue;
    private string _exitRefusedText = string.Empty;
    private DateTimeOffset _leaderLeftAt = DateTimeOffset.MinValue;
    private string _leaderLeftWord = string.Empty;
    private DateTimeOffset _ownMoveAt = DateTimeOffset.MinValue;
    private DateTimeOffset _ownMoveRefusedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _manualMoveAt = DateTimeOffset.MinValue;
    private DateTimeOffset _typedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _typedLeaveAt = DateTimeOffset.MinValue;
    private DateTimeOffset _partyRelayAt = DateTimeOffset.MinValue;
    private DateTimeOffset _selfDiedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _leaderDownAt = DateTimeOffset.MinValue;

    // A refusal or a departure waiting out SettleTime: whose party, and the room
    // we stood in.
    private string? _pendingLeader;
    private RoomKey? _pendingRoom;

    // The split in hand has had its answer. Reset when we are following again.
    private bool _incidentAnswered;

    // The leader we asked, while their answer is still watched for.
    private string? _askedLeader;
    private DateTimeOffset _askedAt;

    private string? _lastIncident;
    private DateTimeOffset _lastIncidentAt;
    private string? _lastAnswer;

    // Test seam for the clock so the windows are deterministic.
    internal Func<DateTimeOffset> NowProvider { get; set; } = static () => DateTimeOffset.Now;

    // Mirrors OtherSettings.AutoRequestComebackWhenLeftBehind. When false a
    // left-behind is still detected and logged, but no @comeback is sent.
    public bool Enabled { get; set; } = true;

    // Test-visible record of every wire payload sent.
    internal List<byte[]> LastSentForTests { get; } = new();

    // For the bug report: the last left-behind incident, with what was done about
    // it and what the leader answered.
    public string LastIncidentSummary
    {
        get
        {
            if (_lastIncident is null) return "(none this session)";
            string answer = _lastAnswer is null ? string.Empty : $"; leader answered: {_lastAnswer}";
            return $"{_lastIncidentAt.ToLocalTime():HH:mm:ss} {_lastIncident}{answer}";
        }
    }

    // For the bug report: a refusal or a departure still waiting out SettleTime.
    public string? PendingCheckFor => _pendingLeader;

    // isMovementPrevented: a movement-blocking affliction (knockdown / held / stun)
    // is active right now. party: read only, to know whom we believe we follow.
    // isAutoEnabled: false while the master switch is off, when nothing automatic
    // is sent. isSelfDown: we are at 0 HP or below. sendBlocked: why an engine send
    // would be dropped right now (a held send gate, the board menu), or null.
    public ComebackRequester(MessageRouter router, RoomTracker tracker, LogService? log = null,
        Func<bool>? isMovementPrevented = null, PartyState? party = null,
        Func<bool>? isAutoEnabled = null, Func<bool>? isSelfDown = null,
        Func<string?>? sendBlocked = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(tracker);
        _tracker = tracker;
        _party = party;
        _log = log;
        _isMovementPrevented = isMovementPrevented;
        _isAutoEnabled = isAutoEnabled ?? (static () => true);
        _isSelfDown = isSelfDown;
        _sendBlocked = sendBlocked;

        _subs.Add(router.Subscribe(KnownPatterns.MovementFailedStuck, OnCantMove));
        _subs.Add(router.Subscribe(KnownPatterns.MovementFailedHeavy, OnCantMove));
        _subs.Add(router.Subscribe(KnownPatterns.DirectionFailed, OnExitRefused));
        _subs.Add(router.Subscribe(KnownPatterns.RoomEntryDeparture, OnDeparture));
        _subs.Add(router.Subscribe(KnownPatterns.PartyYouNoLongerFollowing, OnNoLongerFollowing));
        _subs.Add(router.Subscribe(KnownPatterns.PartyFollowMove, _ => OnFollowingAgain()));
        _subs.Add(router.Subscribe(KnownPatterns.PartyYouFollowing, _ => OnFollowingAgain()));
        _subs.Add(router.Subscribe(KnownPatterns.PartyMemberDeath, OnPlayerDown));
        _subs.Add(router.Subscribe(KnownPatterns.PartyMemberDied, OnPlayerDown));
        _subs.Add(router.Subscribe(KnownPatterns.PartyMemberDropped, OnPlayerDown));
        _subs.Add(router.Subscribe(KnownPatterns.ConversationTelepathIn, OnTelepathIn));

        _tracker.ManualMoveObserved += OnManualMove;
        _tracker.PlayerDeathObserved += OnSelfDied;
        if (_party is not null) _party.PropertyChanged += OnPartyChanged;

        _settleTimer = new DispatcherTimer { Interval = SettleTime };
        _settleTimer.Tick += (_, _) => OnSettleElapsed();
    }

    // Bind the outbound wire — the same gate-wrapped sender the other engines use.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (IDisposable sub in _subs) sub.Dispose();
        _subs.Clear();
        _tracker.ManualMoveObserved -= OnManualMove;
        _tracker.PlayerDeathObserved -= OnSelfDied;
        if (_party is not null) _party.PropertyChanged -= OnPartyChanged;
        _settleTimer.Stop();
    }

    // ----- what we sent ourselves ---------------------------------------

    // A line the user typed at the terminal (MainWindowViewModel.SendUserInput; the
    // client's own commands are not passed here). `leave`, or `follow` / `join`
    // bare or naming someone, is the user working the party by hand.
    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 64) return;
        string line = Encoding.Latin1.GetString(bytes).TrimEnd('\r', '\n', '\0').Trim();
        if (line.Length == 0) return;
        _typedAt = NowProvider();
        if (TypedPartyCommand().IsMatch(line)) _typedLeaveAt = _typedAt;
    }

    // A move of our own went out, typed or an engine's (OutboundMovementObserver.MoveSent).
    public void NoteOwnMoveSent() => _ownMoveAt = NowProvider();

    // The leader's `@party <command>` was relayed to our wire
    // (PartyEssentialHandlers.PartyDirectiveRelayed).
    public void NotePartyRelay() => _partyRelayAt = NowProvider();

    // A move the engines didn't make: a keystroke, a macro, a relayed command.
    private void OnManualMove(string _)
    {
        _manualMoveAt = NowProvider();
        _ownMoveAt = _manualMoveAt;
    }

    private void OnSelfDied() => _selfDiedAt = NowProvider();

    // ----- evidence lines ------------------------------------------------

    // "You can't seem to move anywhere!", the stunned line, "…too heavy to move".
    // Whoever asked for the move, we can't make one.
    private void OnCantMove(MatchResult result)
    {
        DateTimeOffset now = NowProvider();
        _cantMoveAt = now;
        _cantMoveText = result.Text.Trim();
        if (now - _ownMoveAt <= OwnMoveWindow) _ownMoveRefusedAt = now;
    }

    // An exit turned a move away. With a command of ours in flight it answers
    // that; with none it answers the follow move the game made for us.
    private void OnExitRefused(MatchResult result)
    {
        // The answer to `look <dir>` at a shut door, never to a move.
        if (ClosedDoorLookReply().IsMatch(result.Text)) return;
        DateTimeOffset now = NowProvider();
        if (now - _ownMoveAt <= OwnMoveWindow)
        {
            _ownMoveRefusedAt = now;
            return;
        }
        if (now - _typedAt <= TypedReplyWindow) return;
        _exitRefusedAt = now;
        _exitRefusedText = result.Text.Trim();
        if (BelievedLeader() is { } leader) BeginSettle(leader);
    }

    // "<Leader> just left to the north." A follower who goes with them gets the
    // follow line in the same tick.
    private void OnDeparture(MatchResult result)
    {
        if (result.Groups.Count < 2) return;
        if (BelievedLeader() is not { } leader) return;
        if (!result.Groups[0].Trim().Equals(leader, StringComparison.OrdinalIgnoreCase)) return;
        string word = result.Groups[1].Trim();
        if (word.Equals("nowhere", StringComparison.OrdinalIgnoreCase)) return;
        _leaderLeftAt = NowProvider();
        _leaderLeftWord = word;
        BeginSettle(leader);
    }

    // The leader, dead or dropped. The party goes with them, and nobody is coming
    // back.
    private void OnPlayerDown(MatchResult result)
    {
        if (result.Groups.Count == 0) return;
        if (BelievedLeader() is not { } leader) return;
        if (result.Groups[0].Trim().Equals(leader, StringComparison.OrdinalIgnoreCase))
            _leaderDownAt = NowProvider();
    }

    // The follow line, or "You are now following X.": whatever split came before
    // is over.
    private void OnFollowingAgain()
    {
        CancelSettle();
        _incidentAnswered = false;
        _askedLeader = null;
        _cantMoveAt = DateTimeOffset.MinValue;
        _exitRefusedAt = DateTimeOffset.MinValue;
        _leaderLeftAt = DateTimeOffset.MinValue;
    }

    private void OnPartyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PartyState.IsInParty) or nameof(PartyState.SelfIsLeader))) return;
        // The party is gone, or we lead: whatever was waiting to be judged was not
        // a follower left standing. The evidence stamps stay for the "no longer
        // following" line that may be on its way through the same fan-out.
        if (BelievedLeader() is null) CancelSettle();
    }

    // ----- triggers --------------------------------------------------------

    private void BeginSettle(string leader)
    {
        if (_incidentAnswered || _pendingLeader is not null) return;
        _pendingLeader = leader;
        _pendingRoom = ConfirmedRoom()?.Key;
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private void CancelSettle()
    {
        _pendingLeader = null;
        _pendingRoom = null;
        _settleTimer.Stop();
    }

    // Test seam — the DispatcherTimer doesn't tick under headless xUnit.
    internal void FireSettleForTests() => OnSettleElapsed();

    private void OnSettleElapsed()
    {
        if (_pendingLeader is not { } leader) return;
        RoomKey? from = _pendingRoom;
        CancelSettle();
        if (_incidentAnswered) return;
        // Somewhere else by now: we went along by an exit that prints no follow line.
        if (from is { } was && ConfirmedRoom() is { } here && here.Key != was)
        {
            _log?.Debug(LogCategory, $"not left behind by {leader}: we stand in {here.Key}, not {was}");
            return;
        }
        Conclude(leader, DescribeEvidence(leader, NowProvider()) ?? "the follow move never came");
    }

    // Our own `par` shows the leader as [Invited]: on the list, following nobody.
    public void NoteLeaderListedAsInvited(string leader)
    {
        if (string.IsNullOrEmpty(leader)) return;
        string? evidence = DescribeEvidence(leader, NowProvider());
        CancelSettle();
        if (_incidentAnswered) return;
        string cause = $"`par` lists {leader} as [Invited]";
        Conclude(leader, evidence is null ? cause : $"{cause}; {evidence}");
    }

    private void OnNoLongerFollowing(MatchResult result)
    {
        string leader = result.Groups.Count > 0 ? result.Groups[0].Trim() : string.Empty;
        if (string.IsNullOrEmpty(leader)) return;
        DateTimeOffset now = NowProvider();
        CancelSettle();
        // The game only says this to someone who was following until now, so it
        // opens a split of its own whatever was decided before it.
        _incidentAnswered = false;

        string? cause = null;
        string? leaderExitWord = null;
        if (now - _cantMoveAt <= LeftBehindWindow)
            cause = $"couldn't move: {_cantMoveText}";
        else if (_isMovementPrevented?.Invoke() == true)
            cause = "held when the leader moved";
        else if (now - _exitRefusedAt <= LeftBehindWindow)
            cause = $"a follow move was refused: {_exitRefusedText}";
        else if (now - _leaderLeftAt <= LeftBehindWindow)
        {
            cause = $"{leader} left {_leaderLeftWord} and the follow ended where we stood";
            leaderExitWord = _leaderLeftWord;
        }
        // Consumed, so none of it can explain a later line.
        _cantMoveAt = DateTimeOffset.MinValue;
        _exitRefusedAt = DateTimeOffset.MinValue;
        _leaderLeftAt = DateTimeOffset.MinValue;

        if (cause is null)
        {
            Withhold($"no longer following {leader}, with no follow move of ours refused", WhyTheFollowEnded(leader, now), now);
            return;
        }
        Conclude(leader, cause, leaderExitWord);
    }

    // ----- the decision ------------------------------------------------------

    // We were left behind. Send the one request, unless this is one of the splits
    // nobody is to be called back for.
    private void Conclude(string leader, string cause, string? leaderExitWord = null)
    {
        if (_incidentAnswered) return;
        DateTimeOffset now = NowProvider();
        string incident = $"left behind by {leader} ({cause})";
        if (ExclusionFor(leader, now, leaderExitWord) is { } why)
        {
            Withhold(incident, why, now);
            return;
        }

        // Only a room we're sure of: a stale guess would send the leader to the
        // wrong place. A bare @comeback has them backtrack the way they came.
        string payload = ConfirmedRoom() is { } room ? $"@comeback {room.Key}" : "@comeback";
        byte[] bytes = Encoding.Latin1.GetBytes($"/{leader} {payload}\r");
        LastSentForTests.Add(bytes);
        _wireSender?.Invoke(bytes);
        _incidentAnswered = true;
        _askedLeader = leader;
        _askedAt = now;
        Record($"{incident}: sent `{payload}`", now);
        _log?.Info(LogCategory, $"{incident} — sent {payload}");
    }

    private void Withhold(string incident, string why, DateTimeOffset now)
    {
        _incidentAnswered = true;
        Record($"{incident}: no @comeback, {why}", now);
        _log?.Info(LogCategory, $"{incident} — no @comeback: {why}");
    }

    private void Record(string text, DateTimeOffset now)
    {
        _lastIncident = text;
        _lastIncidentAt = now;
        _lastAnswer = null;
    }

    // Why a left-behind follower is not asking, or null when it should.
    private string? ExclusionFor(string leader, DateTimeOffset now, string? leaderExitWord)
    {
        if (now - _typedLeaveAt <= VoluntaryWindow) return "you left the party by command";
        if (ManualMoveStands(now)) return "a move of your own took you out of the party";
        if (now - _selfDiedAt <= DeathWindow || _isSelfDown?.Invoke() == true) return "we died or dropped";
        if (now - _leaderDownAt <= DeathWindow) return $"{leader} died or dropped";
        if (now - _partyRelayAt <= TeleportWindow)
            return $"a party teleport {leader} relayed split us, and their client regroups the party";
        if (LeaderExitCasts(leaderExitWord))
            return $"the exit {leader} took casts a spell (a teleport split), and their client regroups the party";
        if (!Enabled) return "\"Auto-request @comeback when left behind\" is off";
        if (!_isAutoEnabled()) return "the master switch is off";
        if (_sendBlocked?.Invoke() is { Length: > 0 } blocked) return blocked;
        return null;
    }

    // What ended a follow that no failed follow move explains: the best the lines
    // let us say, since the game prints the one line for all of them.
    private string WhyTheFollowEnded(string leader, DateTimeOffset now)
    {
        if (now - _typedLeaveAt <= VoluntaryWindow) return "you left the party by command";
        if (ManualMoveStands(now)) return "a move of your own took you out of the party";
        if (now - _selfDiedAt <= DeathWindow || _isSelfDown?.Invoke() == true) return "we died or dropped";
        if (now - _leaderDownAt <= DeathWindow) return $"{leader} died or dropped";
        if (now - _partyRelayAt <= TeleportWindow)
            return $"a party teleport {leader} relayed split us, and their client regroups the party";
        return $"{leader} uninvited us or disbanded, or was teleported or left the game";
    }

    // A manual move that wasn't turned away: we walked out of the party ourselves.
    // One that bonked moved nobody.
    private bool ManualMoveStands(DateTimeOffset now) =>
        now - _manualMoveAt <= VoluntaryWindow && _ownMoveRefusedAt < _manualMoveAt;

    // The exit the leader went through casts a spell on whoever takes it. A spell
    // that teleports drops everyone following them, with the same line a refused
    // follow move ends on.
    private bool LeaderExitCasts(string? word)
    {
        if (word is null || ConfirmedRoom() is not { } room) return false;
        string longName = word.ToLowerInvariant() switch
        {
            "upwards" or "above" => "up",
            "downwards" or "below" => "down",
            string other => other,
        };
        if (!DirectionExtensions.TryFromLongName(longName, out Direction direction)) return false;
        return room.Exits.TryGetValue(direction, out RoomExit exit) && exit.CastsOnWalk;
    }

    // The evidence still fresh at the end of a settle, for the log and the bug report.
    private string? DescribeEvidence(string leader, DateTimeOffset now)
    {
        TimeSpan fresh = SettleTime + LeftBehindWindow;
        string? left = now - _leaderLeftAt <= fresh ? $"{leader} left {_leaderLeftWord} and no follow move came" : null;
        string? refused = now - _exitRefusedAt <= fresh ? $"a follow move was refused: {_exitRefusedText}" : null;
        if (left is not null && refused is not null) return $"{left}; {refused}";
        return left ?? refused;
    }

    // ----- the leader's answer ---------------------------------------------

    // The leader's client answers a @comeback with a braced telepath. It is logged
    // and kept for the bug report; a decline is not argued with.
    private void OnTelepathIn(MatchResult result)
    {
        if (_askedLeader is not { } leader || result.Groups.Count < 2) return;
        if (NowProvider() - _askedAt > AnswerWindow)
        {
            _askedLeader = null;
            return;
        }
        if (!result.Groups[0].Trim().Equals(leader, StringComparison.OrdinalIgnoreCase)) return;
        string body = result.Groups[1].Trim();
        if (body.Length < 3 || body[0] != '{' || body[^1] != '}') return;
        string answer = body[1..^1].Trim();
        bool declined = LeaderDeclined().IsMatch(answer);
        _lastAnswer = answer;
        _log?.Info(LogCategory, declined
            ? $"{leader} isn't coming: {answer} — not asking again"
            : $"{leader} answered the @comeback: {answer}");
        if (declined) _askedLeader = null;
    }

    // ----- helpers -----------------------------------------------------------

    // The leader we believe we follow, as a given name; null when solo or leading.
    private string? BelievedLeader()
    {
        if (_party is not { IsInParty: true, SelfIsLeader: false, LeaderName: { Length: > 0 } name }) return null;
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }

    private Room? ConfirmedRoom() =>
        _tracker.State.Confidence == RoomConfidence.Confirmed ? _tracker.State.CurrentRoom : null;

    [GeneratedRegex(@"^\s*The (?:door|gate) is closed in that direction!", RegexOptions.CultureInvariant)]
    private static partial Regex ClosedDoorLookReply();

    [GeneratedRegex(@"^(?:leave(?:\s.*)?|(?:follow|join)(?:\s+[a-z].*)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypedPartyCommand();

    // The answers PartyComebackManager gives when it is not coming.
    [GeneratedRegex(@"I can't I'm idle|my party is full|can't find a path|can't come|forget me|going idle",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeaderDeclined();
}

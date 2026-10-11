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
//     make: a move refusal just before it, a hold in force, the leader seen
//     walking out with no follow move of ours after it, or the follow line itself
//     with something said after it and no arrival (Paradigm prints the follow line
//     ahead of an exit's refusal, whose wording is the exit's own).
//   - A move refusal nobody asked for. A follower sends no moves, so an exit
//     refusal with no move or command of ours unanswered answers the follow move
//     the game made for us. Stock ends the follow there without another word (a
//     missing item, a toll, a level / class / alignment gate), so that line is all
//     there is to go on.
//   - The leader seen leaving ("X just left to the north.") with no follow line
//     and no new room for us by SettleTime.
//   - Our own `par` listing the leader as [Invited]
//     (PartyManager.LeaderListedAsInvited).
//
// What is never one: the same "no longer following" line with none of that before
// it (an uninvite, a disband, the leader teleported or gone), our own `leave` or a
// move of our own, our death or the leader's, and the leader stepping through an
// exit that teleports whoever walks it.
//
// One request per incident. Once a split has been answered, sent or withheld,
// nothing more goes out until we are following again; a leader who declines is
// logged and not asked a second time. A request that can't go out at the time
// (the master switch off, the send gate held, the leader's party train trip
// still under way) goes out when that clears, while the split is still fresh. A
// dropped link hands the split to PartyRejoinCoordinator, which sends the
// reconnect's own request.
//
// When nobody is coming (the leader refused, set out and gave up, or neither
// answered nor came in time), the follow we may still believe in is given up
// (FollowGivenUp says why that is needed): the one thing here that changes what
// this client does next, and still nothing that moves the character.
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
    // well inside it, and so is the prompt that shows a drop.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(2);

    // A typed room command can be refused with an exit's wording ("A strange power
    // holds you back!"), and its answer comes at once.
    private static readonly TimeSpan TypedReplyWindow = TimeSpan.FromSeconds(2);

    // A `leave` or a move of our own this recently is why the follow ended.
    private static readonly TimeSpan VoluntaryWindow = TimeSpan.FromSeconds(5);

    // A relayed party teleport can wait several seconds before it moves anyone,
    // and the split comes with it.
    private static readonly TimeSpan TeleportWindow = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan DeathWindow = TimeSpan.FromSeconds(10);

    // How long a leader who answered that they are coming, or coming later (a train
    // trip to finish, Auto-Lair's next pass), is waited on before the follow is
    // given up. The same bound a train trip nobody called off is given.
    private static readonly TimeSpan PromisedWait = TimeSpan.FromMinutes(15);

    // The least a leader is given to answer, whatever the window is set to (at 0
    // it means "no reconnect rejoin", not "nobody ever answers").
    private static readonly TimeSpan ShortestWait = TimeSpan.FromMinutes(1);

    // How often a request that couldn't be sent is looked at again.
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    private readonly MessageRouter _router;
    private readonly RoomTracker _tracker;
    private readonly PartyState? _party;
    private readonly LogService? _log;
    private readonly Func<bool>? _isMovementPrevented;
    private readonly Func<bool>? _isSelfDown;
    private readonly Func<string?>? _sendBlocked;
    private readonly Func<string, bool>? _inTrainTrip;
    private readonly List<IDisposable> _subs = new();
    private readonly DispatcherTimer _settleTimer;
    private readonly DispatcherTimer _retryTimer;
    private readonly DispatcherTimer _lapseTimer;

    private Action<byte[]>? _wireSender;
    private bool _disposed;

    // ----- evidence, each stamped when its line or command went by -----
    private DateTimeOffset _cantMoveAt = DateTimeOffset.MinValue;
    private string _cantMoveText = string.Empty;
    private DateTimeOffset _exitRefusedAt = DateTimeOffset.MinValue;
    private string _exitRefusedText = string.Empty;
    private DateTimeOffset _leaderLeftAt = DateTimeOffset.MinValue;
    private string _leaderLeftWord = string.Empty;
    private DateTimeOffset _followAttemptAt = DateTimeOffset.MinValue;
    private string _followAttemptWord = string.Empty;
    private int _linesSinceFollowLine;
    private DateTimeOffset _ownMoveRefusedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _manualMoveAt = DateTimeOffset.MinValue;
    private DateTimeOffset _typedCommandAt = DateTimeOffset.MinValue;
    private DateTimeOffset _typedLeaveAt = DateTimeOffset.MinValue;
    private DateTimeOffset _partyTeleportAt = DateTimeOffset.MinValue;
    private DateTimeOffset _selfDiedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _leaderDownAt = DateTimeOffset.MinValue;

    // What is waiting out SettleTime: whose party, and either the room we stood
    // in (evidence still to be judged) or the verdict a "no longer following"
    // line reached, held until the prompt has shown whether we dropped.
    private string? _pendingLeader;
    private RoomKey? _pendingRoom;
    private string? _pendingVerdict;
    private string? _pendingExitWord;

    // The split in hand has had its answer. Reset when we are following again.
    private bool _incidentAnswered;

    // A request the client couldn't send when the split was judged.
    private string? _heldLeader;
    private string? _heldIncident;
    private string? _heldWhy;
    private DateTimeOffset _heldAt;

    // The leader we asked, while we wait on them: until we follow again, they
    // refuse, or the wait runs out.
    private string? _askedLeader;

    private string? _lastIncident;
    private DateTimeOffset _lastIncidentAt;
    private string? _lastAnswer;
    private string? _lastGivenUp;

    // Test seam for the clock so the windows are deterministic.
    internal Func<DateTimeOffset> NowProvider { get; set; } = static () => DateTimeOffset.Now;

    // Mirrors OtherSettings.AutoRequestComebackWhenLeftBehind. When false a
    // left-behind is still detected and logged, but no @comeback is sent.
    public bool Enabled { get; set; } = true;

    // The master switch (true = off). Off, no @comeback is sent; one that was due
    // is held back and goes out when the switch is back on, while still fresh.
    public Func<bool>? MasterSwitchOff { get; set; }

    // How long a request that couldn't be sent stays worth sending: the Party
    // tab's "If leading, accept @comeback for", past which a leader has moved on.
    public TimeSpan RetryWindow { get; set; } = TimeSpan.FromMinutes(2);

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
            string givenUp = _lastGivenUp is null ? string.Empty : $"; follow given up: {_lastGivenUp}";
            return $"{_lastIncidentAt.ToLocalTime():HH:mm:ss} {_lastIncident}{answer}{givenUp}";
        }
    }

    // Told, with the leader and why, when we stop believing we follow them: they
    // refused our request, or nobody came for it in time. Stock ends a follow at an
    // exit without a word to the follower, so nothing else would ever end it there,
    // and the client would hold its own engines for a leader who isn't coming
    // (user, 2026-10-10). Where the game's own line has ended the follow already
    // (Paradigm prints one) there is nothing left to tell.
    public Action<string, string>? FollowGivenUp { get; set; }

    // Whether that player is the leader we asked to come back for us, and hasn't
    // declined. A member an exit turned away is out of the party and needs a fresh
    // invite to rejoin; having asked for the pickup is our consent to take it, so
    // AutoPartyManager follows that leader's invite whatever the per-player "join
    // if invited" box says. Lapses once we follow again, or after AskedLife.
    public bool IsLeaderWeAsked(string name)
    {
        if (_askedBack is not { } asked || string.IsNullOrEmpty(name)) return false;
        if (NowProvider() - asked.At > AskedLife)
        {
            _askedBack = null;
            return false;
        }
        int space = name.IndexOf(' ');
        string given = space >= 0 ? name[..space] : name;
        return given.Equals(asked.Leader, StringComparison.OrdinalIgnoreCase);
    }

    private (string Leader, DateTimeOffset At)? _askedBack;
    // Long enough for a leader to finish a fight and walk back its return distance.
    private static readonly TimeSpan AskedLife = TimeSpan.FromMinutes(10);

    // For the bug report: a refusal or a departure still waiting out SettleTime,
    // and a request waiting for the client to be able to send.
    public string? PendingCheckFor => _pendingLeader;
    public string? HeldBack => _heldLeader is null ? null : $"{_heldIncident} ({_heldWhy})";

    // isMovementPrevented: a movement-blocking affliction (knockdown / held / stun)
    // is active right now. party: read only, to know whom we believe we follow.
    // isSelfDown: we are at 0 HP or below. sendBlocked: why an engine send
    // would be dropped right now (a held send gate, the board menu), or null.
    // inTrainTrip: that leader has a party train trip under way that we set out on.
    public ComebackRequester(MessageRouter router, RoomTracker tracker, LogService? log = null,
        Func<bool>? isMovementPrevented = null, PartyState? party = null,
        Func<bool>? isSelfDown = null, Func<string?>? sendBlocked = null,
        Func<string, bool>? inTrainTrip = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(tracker);
        _router = router;
        _tracker = tracker;
        _party = party;
        _log = log;
        _isMovementPrevented = isMovementPrevented;
        _isSelfDown = isSelfDown;
        _sendBlocked = sendBlocked;
        _inTrainTrip = inTrainTrip;

        // Every line, to know whether the game said anything between a follow line
        // and the end of the follow, and the lines a room that shows nothing is
        // entered with: each of those is an arrival all the same.
        _router.LineDispatched += OnAnyLine;
        _subs.Add(router.Subscribe(KnownPatterns.RoomPitchBlack, _ => OnArrived()));
        _subs.Add(router.Subscribe(KnownPatterns.RoomVeryDark, _ => OnArrived()));
        _subs.Add(router.Subscribe(KnownPatterns.BlindMoveStarved, _ => OnArrived()));

        _subs.Add(router.Subscribe(KnownPatterns.MovementFailedStuck, OnCantMove));
        _subs.Add(router.Subscribe(KnownPatterns.MovementFailedHeavy, OnCantMove));
        _subs.Add(router.Subscribe(KnownPatterns.DirectionFailed, OnExitRefused));
        _subs.Add(router.Subscribe(KnownPatterns.RoomEntryDeparture, OnDeparture));
        _subs.Add(router.Subscribe(KnownPatterns.PartyYouNoLongerFollowing, OnNoLongerFollowing));
        _subs.Add(router.Subscribe(KnownPatterns.PartyFollowMove, OnFollowLine));
        _subs.Add(router.Subscribe(KnownPatterns.PartyYouFollowing, _ => OnFollowingAgain()));
        _subs.Add(router.Subscribe(KnownPatterns.PartyMemberDeath, OnPlayerDown));
        _subs.Add(router.Subscribe(KnownPatterns.PartyMemberDied, OnPlayerDown));
        _subs.Add(router.Subscribe(KnownPatterns.PartyMemberDropped, OnPlayerDown));
        _subs.Add(router.Subscribe(KnownPatterns.ConversationTelepathIn, OnTelepathIn));

        _tracker.ManualMoveObserved += OnManualMove;
        _tracker.PlayerDeathObserved += OnSelfDied;
        _tracker.MoveConfirmed += OnArrived;
        _tracker.StateChanged += OnRoomStateChanged;
        if (_party is not null) _party.PropertyChanged += OnPartyChanged;

        _settleTimer = new DispatcherTimer { Interval = SettleTime };
        _settleTimer.Tick += (_, _) => OnSettleElapsed();
        _retryTimer = new DispatcherTimer { Interval = RetryInterval };
        _retryTimer.Tick += (_, _) => OnRetryDue();
        _lapseTimer = new DispatcherTimer();
        _lapseTimer.Tick += (_, _) => OnWaitLapsed();
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
        _tracker.MoveConfirmed -= OnArrived;
        _tracker.StateChanged -= OnRoomStateChanged;
        _router.LineDispatched -= OnAnyLine;
        if (_party is not null) _party.PropertyChanged -= OnPartyChanged;
        _settleTimer.Stop();
        _retryTimer.Stop();
        _lapseTimer.Stop();
    }

    // ----- what we sent ourselves, and what happened to us ---------------

    // A line the user typed at the terminal (MainWindowViewModel.SendUserInput; the
    // client's own commands are not passed here). `leave` and a bare `follow` end
    // our own follow. Anything else that isn't talk may be a room command, which an
    // exit's wording can refuse.
    public void ObserveOutbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > 64) return;
        string line = Encoding.Latin1.GetString(bytes).TrimEnd('\r', '\n', '\0').Trim();
        if (line.Length == 0) return;
        if (TypedLeave().IsMatch(line)) _typedLeaveAt = NowProvider();
        else if (!TypedTalk().IsMatch(line)) _typedCommandAt = NowProvider();
    }

    // A member's `@party <command>` was relayed to our wire
    // (PartyEssentialHandlers.PartyDirectiveRelayed). Only the leader's own, and
    // only the keyword of a teleport out of the room we stand in, is the leader
    // taking the party across a split: a hand-over before an item gate or a
    // `@party rest` says nothing about why a follow ends.
    public void NotePartyRelay(string sender, string command)
    {
        if (BelievedLeader() is not { } leader) return;
        if (!leader.Equals(sender, StringComparison.OrdinalIgnoreCase)) return;
        if (ConfirmedRoom() is not { } room) return;
        foreach (RoomExit exit in room.Exits.Values)
        {
            if (exit.Hint != RoomExitHint.Teleport || exit.TextCommands is not { } keywords) continue;
            foreach (string keyword in keywords)
            {
                if (!keyword.Equals(command.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                _partyTeleportAt = NowProvider();
                return;
            }
        }
    }

    // Test seam — a teleport edge takes a room command chain to build, which the
    // test graph doesn't carry.
    internal void StampPartyTeleportForTests() => _partyTeleportAt = NowProvider();

    // The link dropped. Whatever split there is now is the reconnect's:
    // PartyRejoinCoordinator sends that request, once and inside its own window,
    // so nothing here may add a second one before we follow again.
    public void NoteDisconnected()
    {
        CancelSettle();
        DropHeld();
        StopWaiting();
        ClearEvidence();
        if (BelievedLeader() is not { } leader) return;
        _incidentAnswered = true;
        DateTimeOffset now = NowProvider();
        Record($"the link dropped while following {leader}: the reconnect's @comeback is the rejoin's to send", now);
        _log?.Info(LogCategory, $"link dropped while following {leader} — left-behind requests stand down until we follow again");
    }

    // A move the engines didn't make: a keystroke, a macro, a relayed command.
    private void OnManualMove(string _) => _manualMoveAt = NowProvider();

    private void OnSelfDied() => _selfDiedAt = NowProvider();

    // ----- evidence lines ------------------------------------------------

    // "You can't seem to move anywhere!", the stunned line, "…too heavy to move".
    // Whoever asked for the move, we can't make one.
    private void OnCantMove(MatchResult result)
    {
        DateTimeOffset now = NowProvider();
        _cantMoveAt = now;
        _cantMoveText = result.Text.Trim();
        if (_tracker.OwnMoveInFlight) _ownMoveRefusedAt = now;
    }

    // An exit turned a move away. With a move of ours unanswered it answers that,
    // however late; with none it answers the follow move the game made for us.
    private void OnExitRefused(MatchResult result)
    {
        // The answer to `look <dir>` at a shut door, never to a move.
        if (ClosedDoorLookReply().IsMatch(result.Text)) return;
        DateTimeOffset now = NowProvider();
        if (_tracker.OwnMoveInFlight)
        {
            _ownMoveRefusedAt = now;
            return;
        }
        if (now - _typedCommandAt <= TypedReplyWindow) return;
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

    // " -- Following your Party leader east --". We were following when it was
    // printed, so whatever split came before is over. It is not proof the move was
    // made: Paradigm prints it ahead of the exit's own refusal, and the follow then
    // ends on the line after that. The shape of a follow that failed is this line,
    // then something the game says that is no arrival (the refusal, in whatever
    // words the exit has), then the end of the follow. With nothing at all between
    // the two it is an uninvite that happened to come soon after a move.
    private void OnFollowLine(MatchResult result)
    {
        OnFollowingAgain();
        _followAttemptAt = NowProvider();
        _followAttemptWord = result.Groups.Count > 0 ? result.Groups[0].Trim() : string.Empty;
        _linesSinceFollowLine = 0;
    }

    // The move the follow line promised was made: its room was confirmed, we stand
    // somewhere new, a room's exits were shown, or the game said the room is too
    // dark to show or that we are blind. A refused move shows no room.
    private void OnArrived() => _followAttemptAt = DateTimeOffset.MinValue;

    // Runs ahead of the pattern handlers for the same line.
    private void OnAnyLine(Terminal.LineExtractor.EmittedLine line)
    {
        if (_followAttemptAt == DateTimeOffset.MinValue || line.IsPromptLine) return;
        string text = line.Text.Trim();
        if (text.Length == 0) return;
        // A room's exits, or the one line a room too bright to see in is shown as
        // (the wording blindness sets in with; GAME_MECHANICS "Dark rooms — no name,
        // no exits, traversal inferred from no bonk").
        if (text.StartsWith("Obvious exits:", StringComparison.Ordinal) || text == "You are blind!")
        {
            OnArrived();
            return;
        }
        // The line that ends the follow is not what came between.
        if (text.StartsWith("You are no longer following ", StringComparison.Ordinal)) return;
        _linesSinceFollowLine++;
    }

    private void OnRoomStateChanged(RoomTransition transition)
    {
        if (transition.NewRoom is { } now && transition.PreviousRoom?.Key != now.Key) OnArrived();
    }

    // The follow line, or "You are now following X.": whatever split came before
    // is over, and so is whatever we typed before it.
    private void OnFollowingAgain()
    {
        CancelSettle();
        DropHeld();
        ClearEvidence();
        _incidentAnswered = false;
        StopWaiting();
        _askedBack = null;
        _typedLeaveAt = DateTimeOffset.MinValue;
    }

    private void StopWaiting()
    {
        _askedLeader = null;
        _lapseTimer.Stop();
    }

    private void ClearEvidence()
    {
        _cantMoveAt = DateTimeOffset.MinValue;
        _exitRefusedAt = DateTimeOffset.MinValue;
        _leaderLeftAt = DateTimeOffset.MinValue;
        _followAttemptAt = DateTimeOffset.MinValue;
    }

    private void OnPartyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PartyState.IsInParty) or nameof(PartyState.SelfIsLeader))) return;
        // The party is gone, or we lead: evidence waiting to be judged was not a
        // follower left standing. The stamps stay for the "no longer following"
        // line that may be on its way through the same fan-out, and so does a
        // verdict that line already reached.
        if (_pendingVerdict is null && BelievedLeader() is null) CancelSettle();
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
        _pendingVerdict = null;
        _pendingExitWord = null;
        _settleTimer.Stop();
    }

    // Test seam — the DispatcherTimer doesn't tick under headless xUnit.
    internal void FireSettleForTests() => OnSettleElapsed();

    private void OnSettleElapsed()
    {
        if (_pendingLeader is not { } leader) return;
        RoomKey? from = _pendingRoom;
        string? verdict = _pendingVerdict;
        string? exitWord = _pendingExitWord;
        CancelSettle();
        if (_incidentAnswered) return;
        if (verdict is not null)
        {
            Conclude(leader, verdict, exitWord);
            return;
        }
        // No longer in the room we were sure of: we went along by an exit that
        // prints no follow line, or the map has lost us. Either way this isn't a
        // follower seen standing where the leader left it.
        if (from is { } was && ConfirmedRoom()?.Key != was)
        {
            _log?.Debug(LogCategory, $"not judged left behind by {leader}: no longer surely in {was}");
            return;
        }
        Conclude(leader, DescribeEvidence(leader, NowProvider()) ?? "the follow move never came");
    }

    // Our own `par` shows the leader as [Invited]: on the list, following nobody.
    public void NoteLeaderListedAsInvited(string leader)
    {
        if (string.IsNullOrEmpty(leader)) return;
        string? evidence = DescribeEvidence(leader, NowProvider());
        if (_pendingVerdict is not null) return;   // the line's own verdict is on its way
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
        DropHeld();
        StopWaiting();
        // The game only says this to someone who was following until now, so it
        // opens a split of its own whatever was decided before it.
        _incidentAnswered = false;

        string? cause = null;
        string? exitWord = null;
        bool noRefusalSeen = false;
        if (now - _cantMoveAt <= LeftBehindWindow)
            cause = $"couldn't move: {_cantMoveText}";
        else if (_isMovementPrevented?.Invoke() == true)
            cause = "held when the leader moved";
        else if (now - _exitRefusedAt <= LeftBehindWindow)
            cause = $"a follow move was refused: {_exitRefusedText}";
        else if (now - _followAttemptAt <= LeftBehindWindow && _linesSinceFollowLine > 0)
        {
            // The shape of a follow move that failed, whatever the exit said.
            cause = $"the follow move {_followAttemptWord} never arrived";
            exitWord = _followAttemptWord;
            noRefusalSeen = true;
            // The map booked the drag off the follow line, and no refusal it knows
            // took it back: it never happened, and we stand where we stood.
            _tracker.NoteFollowDragRefused(now);
        }
        else if (now - _leaderLeftAt <= LeftBehindWindow)
        {
            cause = $"{leader} left {_leaderLeftWord} and the follow ended where we stood";
            exitWord = _leaderLeftWord;
            noRefusalSeen = true;
        }
        ClearEvidence();   // consumed, so none of it can explain a later line

        if (cause is null)
        {
            Withhold($"no longer following {leader}", WhyTheFollowEnded(leader, now), now);
            return;
        }
        if (!noRefusalSeen)
        {
            Conclude(leader, cause);
            return;
        }
        // With no refusal the client knows before it, the line may be our own drop
        // in the monsters' parting attack, and the prompt that carries the HP comes
        // after it. The verdict waits for that prompt.
        _pendingLeader = leader;
        _pendingVerdict = cause;
        _pendingExitWord = exitWord;
        _settleTimer.Stop();
        _settleTimer.Start();
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
        if (CannotSendNow(leader) is { } blocked)
        {
            _incidentAnswered = true;
            _heldLeader = leader;
            _heldIncident = incident;
            _heldWhy = blocked;
            _heldAt = now;
            Record($"{incident}: @comeback held back, {blocked}", now);
            _log?.Info(LogCategory,
                $"{incident} — @comeback held back: {blocked}; it goes out within {RetryWindow.TotalMinutes:0.#} min of that clearing");
            _retryTimer.Stop();
            _retryTimer.Start();
            return;
        }
        Ask(leader, incident, now);
    }

    // Test seam — the DispatcherTimer doesn't tick under headless xUnit.
    internal void FireRetryForTests() => OnRetryDue();

    // A request that couldn't be sent: out it goes once the client can send, as
    // long as we still aren't following and the split is fresh.
    private void OnRetryDue()
    {
        if (_heldLeader is not { } leader || _heldIncident is not { } incident)
        {
            _retryTimer.Stop();
            return;
        }
        DateTimeOffset now = NowProvider();
        // A train trip runs as long as it runs, and the leader expects to be asked
        // at the end of it: the split only starts to age once the trip is over.
        if (_inTrainTrip?.Invoke(leader) == true)
        {
            _heldAt = now;
            return;
        }
        if (now - _heldAt > RetryWindow)
        {
            DropHeld();
            Record($"{incident}: no @comeback, it couldn't be sent for {RetryWindow.TotalMinutes:0.#} min", now);
            _log?.Info(LogCategory, $"{incident} — the held-back @comeback is dropped: {RetryWindow.TotalMinutes:0.#} min on, {leader} has moved on");
            GiveUpTheFollow(leader, $"the @comeback couldn't be sent for {RetryWindow.TotalMinutes:0.#} min");
            return;
        }
        if (CannotSendNow(leader) is not null) return;
        DropHeld();
        if (_isSelfDown?.Invoke() == true)
        {
            Withhold(incident, "we died or dropped", now);
            return;
        }
        Ask(leader, incident, now);
    }

    private void DropHeld()
    {
        _heldLeader = null;
        _heldIncident = null;
        _heldWhy = null;
        _retryTimer.Stop();
    }

    private void Ask(string leader, string incident, DateTimeOffset now)
    {
        _incidentAnswered = true;
        // Only a room we're sure of: a stale guess would send the leader to the
        // wrong place. A bare @comeback has them backtrack the way they came.
        string payload = ConfirmedRoom() is { } room ? $"@comeback {room.Key}" : "@comeback";
        byte[] bytes = Encoding.Latin1.GetBytes($"/{leader} {payload}\r");
        LastSentForTests.Add(bytes);
        _wireSender?.Invoke(bytes);
        _askedLeader = leader;
        _askedBack = (leader, now);
        Record($"{incident}: sent `{payload}`", now);
        _log?.Info(LogCategory, $"{incident} — sent {payload}");
        // A leader takes the request for this long ("If leading, accept @comeback
        // for"): with no word from them by then, nobody is coming.
        WaitFor(RetryWindow);
    }

    private void WaitFor(TimeSpan wait)
    {
        _lapseTimer.Stop();
        _lapseTimer.Interval = wait < ShortestWait ? ShortestWait : wait;
        _lapseTimer.Start();
    }

    // Test seam — the DispatcherTimer doesn't tick under headless xUnit.
    internal void FireWaitLapsedForTests() => OnWaitLapsed();
    internal TimeSpan? WaitingForTests => _lapseTimer.IsEnabled ? _lapseTimer.Interval : null;

    private void OnWaitLapsed()
    {
        _lapseTimer.Stop();
        if (_askedLeader is not { } leader) return;
        _askedLeader = null;
        GiveUpTheFollow(leader, $"nobody came within {_lapseTimer.Interval.TotalMinutes:0.#} min of the @comeback");
    }

    // We stop believing we follow that leader, if we still do (FollowGivenUp says
    // when that is). The leader's invite, if they do come, is taken or not by the
    // usual rules.
    private void GiveUpTheFollow(string leader, string why)
    {
        if (BelievedLeader() is not { } still || !still.Equals(leader, StringComparison.OrdinalIgnoreCase)) return;
        _lastGivenUp = why;
        _log?.Info(LogCategory,
            $"no longer counting ourselves as following {leader}: {why}. The game ended that follow without a line; our own engines are free again.");
        FollowGivenUp?.Invoke(leader, why);
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
        _lastGivenUp = null;
    }

    // Why a left-behind follower is not asking and never will for this split, or
    // null when it should.
    private string? ExclusionFor(string leader, DateTimeOffset now, string? leaderExitWord)
    {
        if (now - _typedLeaveAt <= VoluntaryWindow) return "you left the party by command";
        if (ManualMoveStands(now)) return "a move of your own took you out of the party";
        if (now - _selfDiedAt <= DeathWindow || _isSelfDown?.Invoke() == true) return "we died or dropped";
        if (now - _leaderDownAt <= DeathWindow) return $"{leader} died or dropped";
        // A verdict with no refusal the client knows behind it rests on timing, and
        // the leader's own teleport ends a follow with the same line: relayed just
        // before, that is what this was.
        if (leaderExitWord is not null && now - _partyTeleportAt <= TeleportWindow)
            return $"{leader} relayed a party teleport: everyone crosses by themselves and is re-invited where it lands";
        if (LeaderExitCasts(leaderExitWord))
            return $"the exit {leader} took casts a spell on whoever walks it, so the follow ended on a teleport and not on a move we couldn't make";
        if (!Enabled) return "\"Auto-request @comeback when left behind\" is off";
        return null;
    }

    // Why nothing is to be sent just now, though it may be later. A party train
    // trip is one: a @comeback in the middle of it would stop the trip, and the
    // leader fetches whoever it left on the way once the training is done (user,
    // 2026-10-10).
    private string? CannotSendNow(string leader)
    {
        if (MasterSwitchOff?.Invoke() == true) return "the master switch is off";
        if (_sendBlocked?.Invoke() is { Length: > 0 } blocked) return blocked;
        return _inTrainTrip?.Invoke(leader) == true
            ? $"{leader}'s party train trip is under way, and they are asked when the training is done"
            : null;
    }

    // What ended a follow that no failed follow move explains: the best the lines
    // let us say, since the game prints the one line for all of them.
    private string WhyTheFollowEnded(string leader, DateTimeOffset now)
    {
        if (now - _typedLeaveAt <= VoluntaryWindow) return "you left the party by command";
        if (ManualMoveStands(now)) return "a move of your own took you out of the party";
        if (now - _selfDiedAt <= DeathWindow || _isSelfDown?.Invoke() == true) return "we died or dropped";
        if (now - _leaderDownAt <= DeathWindow) return $"{leader} died or dropped";
        if (now - _partyTeleportAt <= TeleportWindow)
            return $"{leader} relayed a party teleport: everyone crosses by themselves and is re-invited where it lands";
        return $"no sign you were left behind: uninvited, disbanded, or {leader} teleported or left the game";
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
    // and kept for the bug report; a decline is not argued with, and ends the
    // follow we still believe in (GiveUpTheFollow). Any other answer says they are
    // coming, now or later, and they are waited on for longer.
    private void OnTelepathIn(MatchResult result)
    {
        if (_askedLeader is not { } leader || result.Groups.Count < 2) return;
        if (!result.Groups[0].Trim().Equals(leader, StringComparison.OrdinalIgnoreCase)) return;
        string body = result.Groups[1].Trim();
        if (body.Length < 3 || body[0] != '{' || body[^1] != '}') return;
        string answer = body[1..^1].Trim();
        bool declined = LeaderDeclined().IsMatch(answer);
        _lastAnswer = answer;
        _log?.Info(LogCategory, declined
            ? $"{leader} isn't coming: {answer} — not asking again"
            : $"{leader} answered the @comeback: {answer}");
        if (declined)
        {
            StopWaiting();
            _askedBack = null;
            GiveUpTheFollow(leader, $"{leader} refused the @comeback ({answer})");
        }
        else if (LeaderGaveUp().IsMatch(answer))
        {
            // They tried and went back to what they were doing. Nobody is coming
            // now, but an invite of theirs is still one we asked for.
            StopWaiting();
            GiveUpTheFollow(leader, $"{leader} came for us and gave up ({answer})");
        }
        else
        {
            WaitFor(PromisedWait);
        }
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

    // What ends our own follow: `leave`, alone or with one more word (`leave
    // gang` leaves the gang instead; with three words or more the game does
    // nothing), and `follow` with nobody named. `follow <name>` and `join <name>`
    // join someone, which is how a member rejoins. The shortened spellings are the
    // game's own: `le` … `leave`, `fo` … `follow` (GAME_MECHANICS "Party commands").
    [GeneratedRegex(@"^(?:le(?:a(?:ve?)?)?(?:\s+(?!gang\s*$)\S+)?|fo(?:l(?:l(?:ow?)?)?)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypedLeave();

    // Talk, which no exit answers: the punctuation leads (telepath, directed say,
    // channel broadcast, the client's say prefix) and the talk verbs.
    [GeneratedRegex(@"^(?:[/>'\-.""]|(?:gos\w*|auc\w*|bg|gb|br|broadg\w*|say|whi\w*)(?:\s|$))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypedTalk();

    // The answers PartyComebackManager gives when it is not coming. "I can't yet"
    // is not one: a train trip comes when the training is done, and Auto-Lair
    // invites on its next pass.
    [GeneratedRegex(@"I can't(?! yet)|my party is full|can't find a path|can't come|forget me|going idle",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeaderDeclined();

    // The answers it gives when it set out for us and went back to its own
    // business: the walk failed, or we never followed its invite.
    [GeneratedRegex(@"can't reach|path failed|follow timed out",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeaderGaveUp();
}

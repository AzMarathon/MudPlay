using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Cash;

// Carries a stash room's coin to a bank: walk to the stash, search, take what the
// cash settings let us carry, walk to the bank, deposit it, and go back for more
// until the search shows nothing left. It ends in the bank after the last deposit.
//
// A stash is a belief until it is searched (StashLedger): anyone can have drawn on
// it, and a pile is bigger than one trip can carry. So every trip searches again
// and reads the pile before taking anything, and the ledger is set to what the
// search showed less what was taken (GAME_MECHANICS "Hiding coin in a room
// (stashing)": the untaken part of a searched pile stays hidden).
//
// Each deposit is everything gained since the transfer started — the stash's coin
// and whatever was picked up off the ground between the two rooms — so the purse
// ends where it started and the keep-on-hand float is never drawn into the bank.
//
// A party leader can have the members carry too (Settings → Cash): once the
// leader has taken its own load, each member is telepathed `@do sea` and
// `@do get N <coin>` for an even share of what is left, and `@deposit-all` at the
// bank. Their answers aren't read; a second search afterwards counts what is
// really left.
//
// Walker, wire and collect engine are reached through delegates, like
// TrainFundingRouter, so the trips can be unit-tested without a map.
public sealed class StashTransferRunner
{
    public const string LogCategory = "StashTransfer";

    // How long a `sea` gets to answer before the pile is read.
    public TimeSpan SurveyWindow { get; set; } = TimeSpan.FromSeconds(1.5);

    // How long the `get`s get to land before the trip counts what it took.
    public TimeSpan CollectWindow { get; set; } = TimeSpan.FromSeconds(3);

    // How long a `dep` gets to echo.
    public TimeSpan DepositWindow { get; set; } = TimeSpan.FromSeconds(4);

    // How long the members get to act on their telepathed orders: the telepaths are
    // paced, and each member's client then sends its own commands.
    public TimeSpan PartyWindow { get; set; } = TimeSpan.FromSeconds(6);

    private enum Phase
    {
        Idle, WalkingToStash, Surveying, Collecting,
        // Party share: the members are taking theirs, then the pile is searched
        // again to count what is left.
        PartyTaking, Recounting,
        WalkingToBank, Depositing,
        // The members are depositing.
        PartyDepositing,
    }

    private readonly Func<RoomKey?> _currentRoom;
    private readonly Func<long> _onHandCopper;
    private readonly Func<RoomKey, bool> _walkTo;
    private readonly Action<string> _send;
    private readonly Action<TimeSpan, Action> _armTimer;
    private readonly Action<long?> _limitCollection;
    private readonly Func<long> _surveyedCopper;
    private readonly Action<long> _collectSurveyed;
    private readonly Action<bool> _forceAutoGetCash;
    private readonly Action<RoomKey, long> _reconcileStash;
    private readonly Action<string> _notice;
    private readonly Func<IReadOnlyList<string>>? _partyMembers;
    private readonly Func<IReadOnlyList<(string Noun, long Count)>>? _surveyedCoinsLeft;
    private readonly LogService? _log;

    private Phase _phase = Phase.Idle;
    private int _session;
    private long _purseAtStart;
    private long _purseBefore;
    private long _shown;
    private bool _forcedAutoGetCash;

    // The members sent to the pile on this trip; the same ones are told to deposit.
    private IReadOnlyList<string> _partyOnTrip = Array.Empty<string>();

    // Set while this runner is itself starting a walk: WalkTo raises Stopped when it
    // supersedes one in flight, and that is ours, not a user stop.
    private bool _drivingWalker;

    public RoomKey Stash { get; private set; }
    public RoomKey Bank { get; private set; }
    public string BankName { get; private set; } = string.Empty;
    public int Trips { get; private set; }
    public long MovedCopper { get; private set; }

    // What the last search showed still hidden once this trip's coin was taken.
    public long LeftCopper { get; private set; }

    public bool IsBusy => _phase != Phase.Idle;

    // Raised when a transfer starts or ends.
    public event Action? StateChanged;

    // Raised once a started transfer is over, after StateChanged.
    public event Action<StashTransferOutcome>? Ended;

    public StashTransferRunner(
        Func<RoomKey?> currentRoom,
        Func<long> onHandCopper,
        Func<RoomKey, bool> walkTo,
        Action<string> send,
        Action<TimeSpan, Action> armTimer,
        // The collect engine's errand hooks: hold pickup at a copper ceiling (zero
        // takes nothing, null lifts it), read the value the room surveys have shown
        // since, and take up to a value of that surveyed coin.
        Action<long?> limitCollection,
        Func<long> surveyedCopper,
        Action<long> collectSurveyed,
        // Coin pickup has to be on for a survey to be read at all; borrowed for the
        // stash stop only and put back.
        Action<bool> forceAutoGetCash,
        Action<RoomKey, long> reconcileStash,
        Action<string> notice,
        LogService? log = null,
        // The party members to share the carrying with (given names): empty unless
        // the option is on and we lead a party. With it, the surveyed coin the
        // leader hasn't asked for, by wire noun.
        Func<IReadOnlyList<string>>? partyMembers = null,
        Func<IReadOnlyList<(string Noun, long Count)>>? surveyedCoinsLeft = null)
    {
        _currentRoom = currentRoom ?? throw new ArgumentNullException(nameof(currentRoom));
        _onHandCopper = onHandCopper ?? throw new ArgumentNullException(nameof(onHandCopper));
        _walkTo = walkTo ?? throw new ArgumentNullException(nameof(walkTo));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _armTimer = armTimer ?? throw new ArgumentNullException(nameof(armTimer));
        _limitCollection = limitCollection ?? throw new ArgumentNullException(nameof(limitCollection));
        _surveyedCopper = surveyedCopper ?? throw new ArgumentNullException(nameof(surveyedCopper));
        _collectSurveyed = collectSurveyed ?? throw new ArgumentNullException(nameof(collectSurveyed));
        _forceAutoGetCash = forceAutoGetCash ?? throw new ArgumentNullException(nameof(forceAutoGetCash));
        _reconcileStash = reconcileStash ?? throw new ArgumentNullException(nameof(reconcileStash));
        _notice = notice ?? throw new ArgumentNullException(nameof(notice));
        _log = log;
        _partyMembers = partyMembers;
        _surveyedCoinsLeft = surveyedCoinsLeft;
    }

    // Starts the transfer. Null when it is under way; otherwise why it isn't.
    public string? Start(RoomKey stash, RoomKey bank, string bankName)
    {
        if (IsBusy) return "a stash transfer is already running";
        if (_currentRoom() is null) return "your current room is unknown";

        Stash = stash;
        Bank = bank;
        BankName = bankName;
        Trips = 0;
        MovedCopper = 0;
        LeftCopper = 0;
        _partyOnTrip = Array.Empty<string>();
        _purseAtStart = _onHandCopper();
        _session++;

        _log?.Info(LogCategory, $"transfer started: stash {stash} → {bankName} ({bank})");
        string? refused = GoToStash();
        if (refused is not null)
        {
            _log?.Info(LogCategory, $"transfer not started — {refused}");
            return refused;
        }
        _notice($"[Stash Transfer Started: {stash.Map}/{stash.Room} -> {bankName}]");
        StateChanged?.Invoke();
        return null;
    }

    // Stopped from outside: the user's Stop, Reset States, a disconnect.
    public void Cancel(string reason)
    {
        if (!IsBusy) return;
        End($"stopped ({reason})", StashTransferOutcome.Stopped);
    }

    // Wired to the walker's event stream by the owner.
    public void OnWalkEvent(WalkEventKind kind)
    {
        if (_phase is not (Phase.WalkingToStash or Phase.WalkingToBank)) return;

        switch (kind)
        {
            case WalkEventKind.Stopped:
                if (_drivingWalker) return;
                End("stopped (movement stopped)", StashTransferOutcome.Stopped);
                break;
            case WalkEventKind.Failed:
                End(_phase == Phase.WalkingToStash
                    ? "the walk to the stash failed"
                    : $"the walk to {BankName} failed");
                break;
            case WalkEventKind.Finished:
                if (_phase == Phase.WalkingToStash) BeginSurvey();
                else BeginDeposit();
                break;
        }
    }

    // Null when the leg is under way (or the survey, when we already stand there).
    // Coin on the ground along the way is picked up as usual (report
    // paradigm-20261002-101410: it was held off, so an emptied purse walked past
    // every drop); the next deposit banks it with the stash's.
    private string? GoToStash()
    {
        if (_currentRoom() is { } here && here.Equals(Stash))
        {
            BeginSurvey();
            return null;
        }
        if (!Walk(Stash)) return $"no route to the stash room {Stash.Map}/{Stash.Room}";
        _phase = Phase.WalkingToStash;
        return null;
    }

    private bool Walk(RoomKey target)
    {
        _drivingWalker = true;
        try { return _walkTo(target); }
        finally { _drivingWalker = false; }
    }

    private void BeginSurvey()
    {
        // Search surfaces our hidden coin, and is the only way to learn what the pile
        // holds now. Held at zero, the collect engine reads the survey and takes
        // nothing; setting the ceiling also forgets coin seen on the way here.
        ForceAutoGetCash();
        Search(Phase.Surveying, OnSurveyed);
    }

    private void Search(Phase phase, Action<int> then)
    {
        _limitCollection(0);
        _phase = phase;
        int session = _session;
        _send("sea");
        _armTimer(SurveyWindow, () => then(session));
    }

    private void OnSurveyed(int session)
    {
        if (session != _session || _phase != Phase.Surveying) return;

        _shown = Math.Max(0, _surveyedCopper());
        if (_shown <= 0)
        {
            _reconcileStash(Stash, 0);
            LeftCopper = 0;
            End(Trips == 0 ? "the stash is empty, nothing to transfer" : "the stash is empty", StashTransferOutcome.Done);
            return;
        }

        _purseBefore = _onHandCopper();
        _phase = Phase.Collecting;
        // The weight limits in Settings → Cash decide how much of the pile this trip
        // carries; the collect engine applies them.
        _collectSurveyed(_shown);
        _armTimer(CollectWindow, () => OnCollected(session));
    }

    private void OnCollected(int session)
    {
        if (session != _session || _phase != Phase.Collecting) return;

        long taken = Math.Max(0, _onHandCopper() - _purseBefore);
        LeftCopper = Math.Max(0, _shown - taken);
        _reconcileStash(Stash, LeftCopper);

        if (taken <= 0)
        {
            End("nothing could be picked up - check the coin weight limits in Settings, Cash");
            return;
        }

        Trips++;
        _log?.Info(LogCategory, $"trip {Trips}: took {taken:N0} copper, {LeftCopper:N0} left hidden");

        _partyOnTrip = Array.Empty<string>();
        if (LeftCopper > 0 && SendPartyToThePile())
        {
            _phase = Phase.PartyTaking;
            _armTimer(PartyWindow, () => OnPartyTook(session));
            return;
        }
        GoToBank();
    }

    // Tells each member to search and take an even share of each coin the leader
    // left. False when there is no one to tell or no whole coin to share.
    private bool SendPartyToThePile()
    {
        IReadOnlyList<string> members = _partyMembers?.Invoke() ?? Array.Empty<string>();
        if (members.Count == 0 || _surveyedCoinsLeft is null) return false;

        List<(string Noun, long Share)> shares = new();
        foreach ((string noun, long count) in _surveyedCoinsLeft())
            if (count / members.Count >= 1) shares.Add((noun, count / members.Count));
        if (shares.Count == 0) return false;

        foreach (string member in members)
        {
            _send($"/{member} @do sea");
            foreach ((string noun, long share) in shares) _send($"/{member} @do get {share} {noun}");
        }
        _partyOnTrip = members;
        _log?.Info(LogCategory, $"trip {Trips}: asked {string.Join(", ", members)} to take "
            + string.Join(", ", shares.Select(s => $"{s.Share} {s.Noun}")) + " each");
        return true;
    }

    // The members have had their time. What they managed isn't reported back, so the
    // pile is searched again and counted.
    private void OnPartyTook(int session)
    {
        if (session != _session || _phase != Phase.PartyTaking) return;
        Search(Phase.Recounting, OnRecounted);
    }

    private void OnRecounted(int session)
    {
        if (session != _session || _phase != Phase.Recounting) return;

        LeftCopper = Math.Max(0, _surveyedCopper());
        _reconcileStash(Stash, LeftCopper);
        _log?.Info(LogCategory, $"trip {Trips}: {LeftCopper:N0} copper left hidden after the party's share");
        GoToBank();
    }

    private void GoToBank()
    {
        ReleaseCollection();
        if (_currentRoom() is { } here && here.Equals(Bank))
        {
            BeginDeposit();
            return;
        }
        if (!Walk(Bank))
        {
            End($"no route to {BankName}");
            return;
        }
        _phase = Phase.WalkingToBank;
    }

    private void BeginDeposit()
    {
        _purseBefore = _onHandCopper();
        long gained = _purseBefore - _purseAtStart;
        _phase = Phase.Depositing;
        int session = _session;
        if (gained <= 0)
        {
            // Tolls or a purchase on the way ate this trip's coin; nothing to bank.
            _log?.Info(LogCategory, $"trip {Trips}: nothing above the starting purse to deposit");
            AfterOwnDeposit(session);
            return;
        }
        _send($"dep {gained}");
        _armTimer(DepositWindow, () => OnDeposited(session));
    }

    private void OnDeposited(int session)
    {
        if (session != _session || _phase != Phase.Depositing) return;

        long deposited = Math.Max(0, _purseBefore - _onHandCopper());
        if (deposited <= 0)
        {
            // Going back for more would only pile up coin the bank isn't taking.
            End($"{BankName} took no deposit");
            return;
        }
        MovedCopper += deposited;
        _log?.Info(LogCategory, $"trip {Trips}: deposited {deposited:N0} copper at {BankName}");
        AfterOwnDeposit(session);
    }

    private void AfterOwnDeposit(int session)
    {
        if (_partyOnTrip.Count == 0)
        {
            NextTripOrDone();
            return;
        }
        foreach (string member in _partyOnTrip) _send($"/{member} @deposit-all");
        _phase = Phase.PartyDepositing;
        _armTimer(PartyWindow, () =>
        {
            if (session != _session || _phase != Phase.PartyDepositing) return;
            NextTripOrDone();
        });
    }

    private void NextTripOrDone()
    {
        if (LeftCopper <= 0)
        {
            End(null);
            return;
        }
        _phase = Phase.Idle;
        if (GoToStash() is { } refused) End(refused);
    }

    // why: null when the stash was emptied into the bank. The notice goes to the
    // terminal, whose CP437 font has no arrows or long dashes, and follows the
    // client's bracketed-notice form.
    private void End(string? why, StashTransferOutcome outcome = StashTransferOutcome.Failed)
    {
        if (why is null) outcome = StashTransferOutcome.Done;
        _phase = Phase.Idle;
        _session++;
        ReleaseCollection();

        long carrying = Math.Max(0, _onHandCopper() - _purseAtStart);
        string moved = Trips == 0 || MovedCopper <= 0
            ? "nothing moved"
            : $"{CurrencyFormat.Full(MovedCopper)} moved to {BankName} in {Trips} trip{(Trips == 1 ? "" : "s")}";
        string text = why is null
            ? $"[Stash Transfer Done: {moved}]"
            : $"[Stash Transfer Ended: {why}; {moved}"
              + (carrying > 0 ? $"; carrying {CurrencyFormat.Full(carrying)} not yet deposited" : "")
              + (LeftCopper > 0 ? $"; {CurrencyFormat.Full(LeftCopper)} still stashed" : "")
              + "]";
        _log?.Info(LogCategory, text);
        _notice(text);
        StateChanged?.Invoke();
        Ended?.Invoke(outcome);
    }

    private void ForceAutoGetCash()
    {
        if (_forcedAutoGetCash) return;
        _forcedAutoGetCash = true;
        _forceAutoGetCash(true);
    }

    // Runs on every exit from the stash stop and from the transfer, so the pickup
    // ceiling and the borrowed toggle never outlive it.
    private void ReleaseCollection()
    {
        _limitCollection(null);
        if (!_forcedAutoGetCash) return;
        _forcedAutoGetCash = false;
        _forceAutoGetCash(false);
    }

    // One line for the bug report.
    public string Describe() => IsBusy
        ? $"{_phase} — stash {Stash} → {BankName} ({Bank}), trip {Trips}, moved {MovedCopper:N0} copper, "
          + $"{LeftCopper:N0} believed left, purse at start {_purseAtStart:N0}"
          + (_partyOnTrip.Count > 0 ? $", party on this trip: {string.Join(", ", _partyOnTrip)}" : "")
        : "idle";
}

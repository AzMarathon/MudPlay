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
// The deposit is exactly what the trip took, so the purse ends where it started:
// the stash goes to the bank, not the keep-on-hand float.
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

    private enum Phase { Idle, WalkingToStash, Surveying, Collecting, WalkingToBank, Depositing }

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
    private readonly LogService? _log;

    private Phase _phase = Phase.Idle;
    private int _session;
    private long _purseBefore;
    private long _shown;
    private long _carried;
    private bool _forcedAutoGetCash;

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
        LogService? log = null)
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
        _carried = 0;
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
        End($"stopped ({reason})");
    }

    // Wired to the walker's event stream by the owner.
    public void OnWalkEvent(WalkEventKind kind)
    {
        if (_phase is not (Phase.WalkingToStash or Phase.WalkingToBank)) return;

        switch (kind)
        {
            case WalkEventKind.Stopped:
                if (_drivingWalker) return;
                End("stopped (movement stopped)");
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
    private string? GoToStash()
    {
        // Nothing is picked up on the way: coin that isn't the stash's would be
        // counted as taken from it and banked with it.
        _limitCollection(0);
        if (_currentRoom() is { } here && here.Equals(Stash))
        {
            BeginSurvey();
            return null;
        }
        if (!Walk(Stash))
        {
            _limitCollection(null);
            return $"no route to the stash room {Stash.Map}/{Stash.Room}";
        }
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
        // nothing; setting the ceiling again also forgets coin seen on the way here.
        ForceAutoGetCash();
        _limitCollection(0);
        _phase = Phase.Surveying;
        int session = _session;
        _send("sea");
        _armTimer(SurveyWindow, () => OnSurveyed(session));
    }

    private void OnSurveyed(int session)
    {
        if (session != _session || _phase != Phase.Surveying) return;

        _shown = Math.Max(0, _surveyedCopper());
        if (_shown <= 0)
        {
            _reconcileStash(Stash, 0);
            LeftCopper = 0;
            End(Trips == 0 ? "the stash is empty, nothing to transfer" : "the stash is empty");
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
        ReleaseCollection();

        if (taken <= 0)
        {
            End("nothing could be picked up - check the coin weight limits in Settings, Cash");
            return;
        }

        Trips++;
        _carried = taken;
        _log?.Info(LogCategory, $"trip {Trips}: took {taken:N0} copper, {LeftCopper:N0} left hidden");

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
        _phase = Phase.Depositing;
        int session = _session;
        _send($"dep {_carried}");
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
        _carried = Math.Max(0, _carried - deposited);
        _log?.Info(LogCategory, $"trip {Trips}: deposited {deposited:N0} copper at {BankName}");

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
    private void End(string? why)
    {
        _phase = Phase.Idle;
        _session++;
        ReleaseCollection();

        string moved = Trips == 0 || MovedCopper <= 0
            ? "nothing moved"
            : $"{CurrencyFormat.Full(MovedCopper)} moved to {BankName} in {Trips} trip{(Trips == 1 ? "" : "s")}";
        string text = why is null
            ? $"[Stash Transfer Done: {moved}]"
            : $"[Stash Transfer Ended: {why}; {moved}"
              + (_carried > 0 ? $"; carrying {CurrencyFormat.Full(_carried)} from the stash" : "")
              + (LeftCopper > 0 ? $"; {CurrencyFormat.Full(LeftCopper)} still stashed" : "")
              + "]";
        _carried = 0;
        _log?.Info(LogCategory, text);
        _notice(text);
        StateChanged?.Invoke();
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
          + $"{LeftCopper:N0} believed left, carrying {_carried:N0}"
        : "idle";
}

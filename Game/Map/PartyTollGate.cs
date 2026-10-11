using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Remote;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// A party at a crossing that charges each crosser: a toll exit, an NPC's paid
// transport, a sea captain's sailing. The game charges every member and leaves
// behind whoever can't pay, so a leader's engine doesn't take one until it knows
// the whole party gets through (user, 2026-10-10: "either all party members can
// make it, or we go around if possible, if not possible abandon the walk. we read
// all of their purses when we think we're going through it to verify, so if they
// dont respond, assume they cant, and if we can pay for it, for them, give them
// the money they need and continue through it").
//
// At the step through one (BeforeTollStep, BeforeSailing):
//   - every follower's purse is asked for (@wealth) unless an answer no older
//     than the freshness window is in hand, and the step is held meanwhile;
//   - all can pay: the step goes;
//   - someone is short or gave no answer, funding is on (Settings → Cash + Items,
//     "Fund party members' tolls / rides if short") and what we can spare covers
//     them: each is handed what they lack (a silent member the whole price), the
//     step held until the game confirms every hand-over, then it goes;
//   - otherwise the crossing is closed to the party (Closes) and the engine plans
//     again: round it where a way exists, and to a stop naming the crossing and
//     who can't pay where none does.
//
// What we can spare is the purse less what we keep (Kept): our own toll or fare
// with what a trip has set aside for its fees, or the Cash tab's keep-on-hand
// amount when that is more. A hand-over is exact change when the coins carried
// make it, and otherwise the smallest amount above it they make, as long as that
// too can be spared (user, 2026-10-10: overpay "if it doesnt make the leader
// unable to cross").
//
// Everything here runs on the UI thread except Closes and DescribeClosed, which a
// route search can call from a planning thread: they read the purse readings
// through PartyWealthTracker's lock and the marks below through _marksLock.
public sealed class PartyTollGate
{
    private const string LogCategory = "PartyToll";

    // What an engine does with a step through a paid crossing. Hold: the PartyToll
    // movement gate is up, and the step is asked about again when it clears.
    public enum StepVerdict { Go, Hold, Closed }

    internal enum Outcome { Clear, Unverified, Fund, Closed }

    // One follower against one crossing. Unable: why they can't be paid for here,
    // null when nothing says so.
    internal readonly record struct Member(
        string Name, PartyWealthTracker.PurseKnowledge Knowledge, long Copper, string? Unable = null);

    // MemberUnable: closed because someone short can't be paid for, which coin of
    // ours coming in later doesn't change.
    internal readonly record struct Judgement(
        Outcome Outcome, IReadOnlyList<(string Name, long Need)> Needs, string Summary, bool MemberUnable = false);

    // What one short member is handed: the coins, what they are worth, and what
    // the member lacked.
    internal readonly record struct HandOver(
        string Name, long Need, IReadOnlyList<(string Currency, long Count)> Coins, long Copper)
    {
        public long Over => Copper - Need;
    }

    // A crossing closed to the party, kept for the trip it was closed on: who the
    // party was, what each short member lacked, and the words for it.
    private sealed record Closure(
        HashSet<string> Followers, IReadOnlyList<(string Name, long Need)> Needs, bool MemberUnable, string Summary);

    // A paid crossing as a route search hands it over: where it leads, its price
    // for each crosser, and whether that is a fare (an NPC's transport, a sailing)
    // or an exit's toll.
    private readonly record struct Crossing(RoomKey Target, long Copper, bool Fare)
    {
        public string Word => Fare ? "fare" : "toll";
    }

    private sealed class Funding(Crossing crossing, string described, IDisposable? timer)
    {
        public Crossing Crossing { get; } = crossing;
        public string Described { get; } = described;
        public IDisposable? Timer { get; } = timer;
        // Per member: copper asked for, copper the game has confirmed, and the give
        // commands whose lines are still to come.
        public Dictionary<string, (long Planned, long Confirmed, int GivesLeft)> Pending { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        // Per member: what each give sent and not yet answered was worth, in copper.
        public Dictionary<string, List<long>> Unanswered { get; } = new(StringComparer.OrdinalIgnoreCase);
        // The game answered one of several gives with its line for a target it
        // can't find for us, which names nobody.
        public bool Misaimed { get; set; }
    }

    private readonly PartyWealthTracker _wealth;
    private readonly Func<long?> _ownPurse;
    private readonly Func<long> _reservedCopper;
    private readonly Func<CurrencyHoldings?> _holdings;
    private readonly Func<string> _runicName;
    private readonly Action<string> _send;
    private readonly Action<string> _assertGate;
    private readonly Action<string> _clearGate;
    private readonly Func<TimeSpan, Action, IDisposable?> _schedule;
    private readonly Func<RoomKey, string?> _roomName;
    private readonly Func<DateTime> _clock;
    private readonly LogService? _log;

    // Guards the mark tables, which a route search reads.
    private readonly object _marksLock = new();
    // What each member was last handed at each crossing, in copper, and whether it
    // has been crossed since: an answer of theirs after that is their own word,
    // not the echo of what we handed them.
    private readonly Dictionary<(string Name, Crossing At), (long Copper, bool CrossedSince)> _funded =
        new(FundedComparer.Instance);
    // Members who can't be paid for at a crossing for now, and why: a hand-over
    // the game refused or never confirmed, or one that didn't get them through.
    private readonly Dictionary<(string Name, Crossing At), (DateTime At, string Why)> _unable =
        new(FundedComparer.Instance);

    // Crossings closed to the party on the trip under way. A closure outlives the
    // answers it was made on: asked again every 30 s, a toll with no way through
    // would be walked up to and turned from over and over.
    private readonly Dictionary<Crossing, Closure> _closed = new();
    // Members known not to be with us, as last asked on the UI thread (a route
    // search reads this copy, never the comeback manager's own lists). They are
    // left out of a crossing's count: they aren't following through it. Swapped whole.
    private volatile IReadOnlyCollection<string> _leftBehind = [];

    // Hand-overs whose window passed with no line from the game, by member: the
    // crossing they were for and when the window passed. A line that comes late
    // still means the coin went, so it is still credited.
    private readonly Dictionary<string, (Crossing For, DateTime At, List<long> Unanswered)> _lateGives =
        new(StringComparer.OrdinalIgnoreCase);

    private Crossing? _asking;
    private Funding? _funding;

    // How long after its window a hand-over's line is still taken as its own.
    public TimeSpan LateConfirmationWindow { get; set; } = TimeSpan.FromMinutes(2);

    // The members the client KNOWS are not with us (PartyComebackManager.
    // KnownLeftBehind). Only that leaves a listed member out of the count: one who
    // is merely not shown in the room may be hidden, or have arrived after it was
    // drawn, and the ruling is that everyone gets through or nobody does.
    public Func<IReadOnlyCollection<string>>? KnownLeftBehind { get; set; }

    // Asked on the UI thread, at each paid step and when a round of answers is in.
    public void RefreshLeftBehind() => _leftBehind = KnownLeftBehind?.Invoke() ?? [];

    // How long a hand-over may go unconfirmed before the member counts as unable.
    // The @wealth window's length: one command each way.
    public TimeSpan GiveConfirmWindow { get; set; } = TimeSpan.FromSeconds(4);

    // The master switch (true = off): off, nothing is asked, given or held.
    public Func<bool>? MasterSwitchOff { get; set; }

    // Settings → Cash + Items, "Fund party members' tolls / rides if short"
    // (CashSettings.FundPartyTolls, off until ticked). Off: no coin is handed
    // over, so a short or silent member closes the crossing.
    //
    // This and KeepOnHandCopper are values, set on the UI thread when the profile
    // loads and when it is saved, and not read from the profile when asked: Closes
    // is called from a route search's thread, and the profile's settings are a
    // plain dictionary a Settings save writes.
    public bool FundsShortMembers
    {
        get => _fundsShortMembers;
        set => _fundsShortMembers = value;
    }
    private volatile bool _fundsShortMembers;

    public bool FundingOn => _fundsShortMembers;

    // Settings → Cash + Items, "Minimum cash to keep on hand", in copper. What we
    // may not hand over is the larger of this and what we need to cross ourselves
    // (user, 2026-10-10: "first protect the leaders crossing requirement, if their
    // keep on hand is less than that, protect the crossing amount, if their keep
    // on hand is more than that, protect the keep on hand amount").
    public long KeepOnHandCopper
    {
        get => Volatile.Read(ref _keepOnHandCopper);
        set => Volatile.Write(ref _keepOnHandCopper, Math.Max(0, value));
    }
    private long _keepOnHandCopper;

    internal const string FundingSettingName = "Fund party members' tolls / rides if short";

    // Told what is handed to whom at a crossing, and how much over the shortfall
    // it is, as the hand-over starts: for the terminal.
    public Action<string>? Notice { get; set; }

    // Whether we have already gone back for this member from this exit on the run
    // under way (PartyComebackManager.WentBackFor): the crossing turned them away once.
    public Func<string, RoomKey, RoomKey, bool>? WentBackFor { get; set; }

    // For the bug report: the latest decisions made at paid crossings, oldest
    // first, each with its time, and the purses the last one was made on. A
    // hand-over and the crossing it paid for are two decisions, so one alone would
    // hide the first.
    private const int DecisionsKept = 8;
    private readonly List<string> _decisions = new();
    private readonly object _decisionsLock = new();
    public IReadOnlyList<string> Decisions
    {
        get { lock (_decisionsLock) return _decisions.ToArray(); }
    }
    public string LastReadings { get; private set; } = "(none)";

    public PartyTollGate(
        PartyWealthTracker wealth,
        Func<long?> ownPurse,
        Func<long> reservedCopper,
        Func<CurrencyHoldings?> holdings,
        Func<string> runicName,
        Action<string> send,
        Action<string> assertGate,
        Action<string> clearGate,
        Func<TimeSpan, Action, IDisposable?> schedule,
        Func<RoomKey, string?> roomName,
        LogService? log = null,
        Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(wealth);
        ArgumentNullException.ThrowIfNull(ownPurse);
        ArgumentNullException.ThrowIfNull(reservedCopper);
        ArgumentNullException.ThrowIfNull(holdings);
        ArgumentNullException.ThrowIfNull(runicName);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(assertGate);
        ArgumentNullException.ThrowIfNull(clearGate);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(roomName);
        _wealth = wealth;
        _ownPurse = ownPurse;
        _reservedCopper = reservedCopper;
        _holdings = holdings;
        _runicName = runicName;
        _send = send;
        _assertGate = assertGate;
        _clearGate = clearGate;
        _schedule = schedule;
        _roomName = roomName;
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    // ----- the judgement -----------------------------------------------------

    // cost: the toll or fare in copper. spare: our own purse less what we keep
    // (Kept); null while our purse isn't known. fund: whether a short member is
    // handed coin at all. word: "toll" or "fare". keeping: what the spare was
    // counted after, "your own toll" unless something else was said.
    internal static Judgement Judge(
        long cost, long? spare, IReadOnlyList<Member> members, bool fund = true, string word = "toll",
        string? keeping = null)
    {
        keeping ??= $"your own {word}";
        ArgumentNullException.ThrowIfNull(members);
        List<string> unasked = members
            .Where(m => m.Knowledge == PartyWealthTracker.PurseKnowledge.Unasked).Select(m => m.Name).ToList();
        if (unasked.Count > 0)
            return new Judgement(Outcome.Unverified, [], $"not asked yet: {string.Join(", ", unasked)}");

        List<(string Name, long Need)> needs = new();
        List<string> said = new();
        List<string> unable = new();
        foreach (Member m in members)
        {
            long need = m.Knowledge == PartyWealthTracker.PurseKnowledge.Silent ? cost : Math.Max(0, cost - m.Copper);
            if (need <= 0) continue;
            needs.Add((m.Name, need));
            said.Add(m.Knowledge == PartyWealthTracker.PurseKnowledge.Silent
                ? $"{m.Name} gave no answer to @wealth"
                : $"{m.Name} holds {CurrencyFormat.Full(m.Copper)}, {CurrencyFormat.Full(need)} short");
            if (m.Unable is { } why) unable.Add($"{m.Name} {why}");
        }
        if (needs.Count == 0) return new Judgement(Outcome.Clear, needs, $"every party member holds the {word}");

        string who = string.Join("; ", said);
        long total = needs.Sum(n => n.Need);
        if (!fund)
            return new Judgement(Outcome.Closed, needs,
                $"{who}; \"{FundingSettingName}\" is off (Settings, Cash + Items), so no coin is handed over");
        if (unable.Count > 0)
            return new Judgement(Outcome.Closed, needs, $"{who}; {string.Join("; ", unable)}", MemberUnable: true);
        if (spare is not { } free)
            return new Judgement(Outcome.Closed, needs,
                $"{who}; your own purse isn't known (inventory not read), so nothing can be handed over");
        if (free < total)
            return new Judgement(Outcome.Closed, needs,
                $"{who}; covering that takes {CurrencyFormat.Full(total)} and you can spare "
                + $"{CurrencyFormat.Full(Math.Max(0, free))} after {keeping}");
        return new Judgement(Outcome.Fund, needs, $"{who}; you can spare it");
    }

    private bool Applies(out IReadOnlyList<string> followers)
    {
        followers = [];
        if (!_wealth.Leading) return false;
        followers = _wealth.Followers();
        return followers.Count > 0;
    }

    // An exit that charges: its toll, or the fare an NPC's transport carries. Null
    // for an exit that charges nothing.
    private static Crossing? KeyOf(in RoomExit exit)
    {
        if (exit.Hint == RoomExitHint.Toll && exit.TollGold > 0)
            return new Crossing(exit.Target, (long)exit.TollGold * 100, Fare: false);
        return exit.FareCopper > 0 ? new Crossing(exit.Target, exit.FareCopper, Fare: true) : null;
    }

    // A sailing, keyed as a fare into its port: what MovementFilter asks Closes
    // about, since a sailing is no exit.
    private static Crossing? KeyOf(in BoatPassage passage) =>
        passage.FareCopper > 0 ? new Crossing(passage.ArrivalRoom, passage.FareCopper, Fare: true) : null;

    // later: what the rest of the route will charge us ourselves, which the step
    // through the crossing knows and a route search doesn't (Kept).
    private Judgement JudgeNow(Crossing crossing, IReadOnlyList<string> followers, long later = 0)
    {
        DateTime now = _clock();
        List<Member> members = new(followers.Count);
        IReadOnlyCollection<string> leftBehind = _leftBehind;
        foreach (string name in followers)
        {
            if (leftBehind.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            (PartyWealthTracker.PurseKnowledge knowledge, long copper) = _wealth.FreshPurse(name);
            string? unable = null;
            lock (_marksLock)
            {
                // Silent, but handed coin here with it not crossed since: they hold
                // at least that, which is all there is to go on.
                if (knowledge == PartyWealthTracker.PurseKnowledge.Silent
                    && _funded.TryGetValue((name, crossing), out (long Copper, bool CrossedSince) paid) && !paid.CrossedSince)
                    (knowledge, copper) = (PartyWealthTracker.PurseKnowledge.Read, paid.Copper);
                if (_unable.TryGetValue((name, crossing), out (DateTime At, string Why) mark)
                    && now - mark.At <= _wealth.FreshnessWindow)
                    unable = mark.Why;
            }
            members.Add(new Member(name, knowledge, copper, unable));
        }
        // The purse and the Cash settings are read only when someone is short, which
        // is when they decide anything: a route search asks this of every toll and
        // fare in its frontier, from its own thread.
        bool someoneShort = members.Any(m => m.Knowledge == PartyWealthTracker.PurseKnowledge.Silent
            || (m.Knowledge == PartyWealthTracker.PurseKnowledge.Read && m.Copper < crossing.Copper));
        return someoneShort
            ? Judge(crossing.Copper, Spare(crossing.Copper, later), members, FundingOn, crossing.Word, Keeping(crossing, later))
            : Judge(crossing.Copper, spare: null, members, word: crossing.Word);
    }

    // What we may not hand over at a crossing of this price: what we need to
    // cross ourselves (our own toll or fare here, our own tolls and fares on the
    // rest of the route, and what a trip has set aside for its fees), or the
    // keep-on-hand amount when that is more.
    private long Kept(long cost, long later) =>
        Math.Max(cost + Math.Max(0, later) + Math.Max(0, _reservedCopper()), KeepOnHandCopper);

    // What we can hand over there: the purse on record less Kept. Null while the
    // purse isn't known.
    private long? Spare(long cost, long later = 0) => _ownPurse() is { } purse ? purse - Kept(cost, later) : null;

    // The words for what Spare was counted after.
    private string Keeping(Crossing crossing, long later = 0)
    {
        long keep = KeepOnHandCopper;
        if (keep > crossing.Copper + Math.Max(0, later) + Math.Max(0, _reservedCopper()))
            return $"the {CurrencyFormat.Full(keep)} you keep on hand";
        return later > 0
            ? $"your own {crossing.Word} and the {CurrencyFormat.Full(later)} of your own tolls and fares further on this route"
            : $"your own {crossing.Word}";
    }

    // The judgement on what is known now, with the trip's closures kept and let go:
    // a closure stands while nothing fresh says otherwise, and falls when the party
    // is no longer the one it was made for, when what the short members lacked is
    // covered by their own fresh answers and what we can now spare, or when a
    // fresh judgement lets the party through.
    // atTheStep: asked by the step through the crossing, which records its own
    // decision; a closure first made for a route search is recorded here, since
    // nothing else will say why the route turned.
    private Judgement JudgeForTrip(
        Crossing crossing, IReadOnlyList<string> followers, bool atTheStep = false, long later = 0)
    {
        Judgement fresh = JudgeNow(crossing, followers, later);
        long cost = crossing.Copper;
        if (fresh.Outcome == Outcome.Closed)
        {
            // Closed only for what is kept back for our own crossings further on,
            // which a route search can't see: it would judge this one coverable and
            // send the walk straight back. So the short members stand as unable
            // here, as after a hand-over that failed.
            bool keptForLater = later > 0 && !fresh.MemberUnable
                && JudgeNow(crossing, followers).Outcome == Outcome.Fund;
            if (keptForLater)
                foreach ((string name, _) in fresh.Needs)
                    MarkUnable(name, crossing,
                        $"isn't paid for here: {CurrencyFormat.Full(later)} is kept for your own tolls and fares further on this route");
            bool isNew;
            lock (_marksLock)
            {
                isNew = !_closed.ContainsKey(crossing);
                _closed[crossing] = new Closure(
                    new HashSet<string>(followers, StringComparer.OrdinalIgnoreCase),
                    fresh.Needs, fresh.MemberUnable || keptForLater, fresh.Summary);
            }
            // Said with the lock let go: the log is not ours to hold it across.
            if (isNew && !atTheStep)
                Decide($"the {crossing.Word} into {crossing.Target} ({CurrencyFormat.Full(cost)}): "
                    + $"closed to the party for this trip. {fresh.Summary}");
            return fresh;
        }
        Closure? closure;
        lock (_marksLock)
        {
            if (fresh.Outcome != Outcome.Unverified || !_closed.TryGetValue(crossing, out closure))
            {
                _closed.Remove(crossing);
                return fresh;
            }
            if (!closure.Followers.SetEquals(followers))
            {
                _closed.Remove(crossing);
                return fresh;
            }
        }
        // Outside the lock: the purse readings have their own.
        long stillLacking = 0;
        foreach ((string name, long need) in closure.Needs)
            if (_wealth.FreshPurse(name) is not (PartyWealthTracker.PurseKnowledge.Read, long held) || held < cost)
                stillLacking += need;
        if (stillLacking == 0
            || (FundingOn && !closure.MemberUnable && Spare(cost) is { } spare && spare >= stillLacking))
        {
            lock (_marksLock) _closed.Remove(crossing);
            return fresh;
        }
        return new Judgement(Outcome.Closed, closure.Needs, closure.Summary, closure.MemberUnable);
    }

    // ----- route searches ------------------------------------------------------

    // Whether the party can't be taken through this toll or fare as things stand.
    // False while a follower hasn't been asked: the check at the crossing itself asks.
    public bool Closes(in RoomExit exit)
    {
        if (KeyOf(in exit) is not { } crossing || !Applies(out IReadOnlyList<string> followers)) return false;
        return JudgeForTrip(crossing, followers).Outcome == Outcome.Closed;
    }

    // Who can't pay and by how much, for the line a walk with no other way gives.
    // Null when the party doesn't close this crossing.
    public string? DescribeClosed(in RoomExit exit)
    {
        if (KeyOf(in exit) is not { } crossing || !Applies(out IReadOnlyList<string> followers)) return null;
        Judgement judged = JudgeForTrip(crossing, followers);
        return judged.Outcome == Outcome.Closed ? judged.Summary : null;
    }

    // ----- a trip's bank stop --------------------------------------------------

    // What handing the party through crossings that charge each member this much
    // in all would take from our purse, on the purses as they are fresh now: a
    // short member what they lack, a silent one all of it, one nobody has asked
    // nothing. For a bank stop a trip is making anyway (TrainFundingRouter), which
    // draws that on top of its own bill. Nothing when nobody is led, funding is
    // off, or the master switch is.
    public (long Copper, string Who) FundingNeededFor(long perMemberCopper)
    {
        if (perMemberCopper <= 0 || MasterSwitchOff?.Invoke() == true || !FundingOn
            || !Applies(out IReadOnlyList<string> followers))
            return (0, string.Empty);
        long total = 0;
        List<string> who = new();
        IReadOnlyCollection<string> leftBehind = _leftBehind;
        foreach (string name in followers)
        {
            if (leftBehind.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            (PartyWealthTracker.PurseKnowledge knowledge, long copper) = _wealth.FreshPurse(name);
            long need = knowledge switch
            {
                PartyWealthTracker.PurseKnowledge.Read => Math.Max(0, perMemberCopper - copper),
                PartyWealthTracker.PurseKnowledge.Silent => perMemberCopper,
                _ => 0,
            };
            if (need <= 0) continue;
            total += need;
            who.Add(knowledge == PartyWealthTracker.PurseKnowledge.Silent
                ? $"{name} gave no answer to @wealth, {CurrencyFormat.Full(need)}"
                : $"{name} holds {CurrencyFormat.Full(copper)}, {CurrencyFormat.Full(need)} short");
        }
        return (total, string.Join("; ", who));
    }

    // Runs `then` once the followers' purses are as fresh as an ask makes them
    // (one @wealth round, no longer than the probe's window): at once when nobody
    // is led, funding is off, or the master switch is.
    public void AfterReadingPurses(Action then)
    {
        ArgumentNullException.ThrowIfNull(then);
        if (MasterSwitchOff?.Invoke() == true || !FundingOn || !Applies(out _))
        {
            then();
            return;
        }
        RefreshLeftBehind();
        _wealth.Verify(then);
    }

    // ----- the step through a paid crossing ------------------------------------

    // A step through a toll exit, or an exit that is an NPC's paid transport.
    // laterOwnCopper: what the rest of the engine's route will charge us ourselves
    // (its tolls, fares and sailings past this step), which is kept back with our
    // own price here before anything is handed over.
    public StepVerdict BeforeTollStep(RoomKey from, Direction direction, in RoomExit exit, long laterOwnCopper = 0)
    {
        if (KeyOf(in exit) is not { } crossing || !Checks(out IReadOnlyList<string> followers)) return StepVerdict.Go;
        return BeforeCrossing(from, crossing, followers,
            PaidCrossingDescriber.Describe(from, direction, in exit, _roomName), laterOwnCopper);
    }

    // The verb that boards a sailing, at its dock.
    public StepVerdict BeforeSailing(in BoatPassage passage, long laterOwnCopper = 0)
    {
        if (KeyOf(in passage) is not { } crossing || !Checks(out IReadOnlyList<string> followers)) return StepVerdict.Go;
        return BeforeCrossing(passage.DockRoom, crossing, followers,
            PaidCrossingDescriber.DescribeBoat(in passage, _roomName), laterOwnCopper);
    }

    private bool Checks(out IReadOnlyList<string> followers)
    {
        followers = [];
        return MasterSwitchOff?.Invoke() != true && Applies(out followers);
    }

    private StepVerdict BeforeCrossing(
        RoomKey from, Crossing crossing, IReadOnlyList<string> followers, string described, long later)
    {
        // The gate is up for an ask or a hand-over: the step comes back when it clears.
        if (_asking is not null || _funding is not null) return StepVerdict.Hold;
        RefreshLeftBehind();

        later = Math.Max(0, later);
        NoteFundedAndTurnedAway(from, crossing, followers);

        Judgement judged = JudgeForTrip(crossing, followers, atTheStep: true, later);
        LastReadings = DescribeReadings(followers, crossing, later);
        switch (judged.Outcome)
        {
            case Outcome.Clear:
                Decide($"{described}: every party member with us can pay ({LastReadings}); going through");
                NoteTollTaken();
                return StepVerdict.Go;

            case Outcome.Closed:
                Decide($"{described}: not taken with the party. {judged.Summary}");
                return StepVerdict.Closed;

            case Outcome.Unverified:
                _asking = crossing;
                _log?.Info(LogCategory,
                    $"{described}: asking {string.Join(", ", followers)} what they hold (@wealth) before going through "
                    + $"(funding short members is {(FundingOn ? "on" : "off")}).");
                _assertGate($"asking the party's purses at {described}");
                _wealth.Verify(() => OnVerified(described));
                return StepVerdict.Hold;

            default:
                return BeginFunding(crossing, described, judged, later);
        }
    }

    private void OnVerified(string described)
    {
        if (_asking is null) return;
        _asking = null;
        _log?.Info(LogCategory, $"{described}: the party's purses are read.");
        _clearGate("the party's purses are read");
    }

    // A member we handed coin at this crossing, gone back for from it since, and
    // short again: the hand-over didn't get them through, and they aren't paid
    // twice. One who says, after a crossing, that they hold the price themselves
    // is no longer about what we handed them.
    private void NoteFundedAndTurnedAway(RoomKey from, Crossing crossing, IReadOnlyList<string> followers)
    {
        foreach (string name in followers)
        {
            (long Copper, bool CrossedSince) given;
            lock (_marksLock)
                if (!_funded.TryGetValue((name, crossing), out given)) continue;
            (PartyWealthTracker.PurseKnowledge knowledge, long copper) = _wealth.FreshPurse(name);
            if (knowledge == PartyWealthTracker.PurseKnowledge.Unasked) continue;
            if (knowledge == PartyWealthTracker.PurseKnowledge.Read && copper >= crossing.Copper)
            {
                if (given.CrossedSince)
                    lock (_marksLock) _funded.Remove((name, crossing));
                continue;
            }
            if (WentBackFor?.Invoke(name, from, crossing.Target) != true) continue;
            lock (_marksLock)
                _unable[(name, crossing)] = (_clock(),
                    $"was handed {CurrencyFormat.Full(given.Copper)} at this {crossing.Word} already and was still turned away: not paid for twice");
        }
    }

    private StepVerdict BeginFunding(Crossing crossing, string described, Judgement judged, long later)
    {
        long cost = crossing.Copper;
        if (_holdings() is not { } holdings || _ownPurse() is not { } purse)
        {
            foreach ((string name, _) in judged.Needs)
                MarkUnable(name, crossing, "can't be handed coin: the inventory isn't read");
            Decide($"{described}: not taken with the party. {judged.Summary}, but the inventory isn't read, so no coin can be counted out");
            return StepVerdict.Closed;
        }

        // Never what we keep: our own toll or fare here and further on with a
        // trip's fees, or the keep-on-hand amount. An overpay stays inside the same spare.
        long spare = purse - Kept(cost, later);
        if (PlanHandOvers(holdings, judged.Needs, spare, out IReadOnlyList<string> uncovered) is not { } plans)
        {
            foreach ((string name, long need) in judged.Needs.Where(n => uncovered.Contains(n.Name)))
                MarkUnable(name, crossing,
                    $"can't be handed coin: the coins carried don't make {CurrencyFormat.Full(need)} without going past what you can spare");
            Decide($"{described}: not taken with the party. {judged.Summary}, but the coins carried don't make what "
                + $"{string.Join(", ", uncovered)} needs without going past the {CurrencyFormat.Full(Math.Max(0, spare))} "
                + $"you can spare after {Keeping(crossing, later)}");
            return StepVerdict.Closed;
        }

        Funding funding = new(crossing, described, _schedule(GiveConfirmWindow, OnGiveWindowElapsed));
        _funding = funding;
        _assertGate($"handing coin to the party at {described}");
        string handedOver = string.Join(", ", plans.Select(DescribeHandOver));
        Decide($"{described}: {judged.Summary}. Handing over {handedOver}, then going through");
        Notice?.Invoke($"{described}: handing over {handedOver}");
        // Everyone is on the list before the first command goes: a confirmation
        // that came back at once would otherwise find the list empty and end it.
        foreach (HandOver plan in plans)
        {
            funding.Pending[plan.Name] = (plan.Copper, 0, plan.Coins.Count);
            funding.Unanswered[plan.Name] = plan.Coins.Select(c => CurrencyHoldings.ToCopper(c.Currency, c.Count)).ToList();
        }
        string runic = _runicName();
        foreach (HandOver plan in plans)
            foreach ((string currency, long count) in plan.Coins)
                _send(CoinGiveCommand.For(currency, count, plan.Name, runic));
        return StepVerdict.Hold;
    }

    // "3 gold to Bob", or when the coins don't make the shortfall exactly,
    // "1 runic to Bob to cover 3 gold: 99 platinum 97 gold over".
    private static string DescribeHandOver(HandOver plan) =>
        $"{CurrencyFormat.Full(plan.Copper)} to {plan.Name}"
        + (plan.Over > 0
            ? $" to cover {CurrencyFormat.Full(plan.Need)}: {CurrencyFormat.Full(plan.Over)} over"
            : string.Empty);

    // The most a hand-over may come to when the coins carried don't make the
    // shortfall exactly. Nothing but the spare limits it (user, 2026-10-10:
    // overpay "if it doesnt make the leader unable to cross, yes", and asked
    // whether a whole runic for a gap of three gold wants a cap, "yes no cap is
    // fine"). The one place to change if a ceiling is ever wanted.
    internal static long OverpayCeiling(long need, long spare) => spare;

    private static readonly long[] CoinUnits = [1, 10, 100, 10_000, 1_000_000];
    private static readonly string[] CoinNames = ["copper", "silver", "gold", "platinum", "runic"];

    // The coins to hand over for a shortfall, out of what is held and inside what
    // can be spared: the need exactly when the coins make it, and otherwise the
    // smallest amount above it they make. Comes back short of the need when
    // neither fits inside the spare.
    //
    // Each coin is worth a whole number of the one below, so taking as many of the
    // largest as fit and working down finds exact change whenever there is any;
    // and the least that overshoots is, for some coin, "as many as fit of every
    // larger one, then one more of this than fits and nothing smaller".
    internal static (IReadOnlyList<(string Currency, long Count)> Coins, long Copper) CountOut(
        CurrencyHoldings held, long need, long spare)
    {
        if (need <= 0) return ([], 0);
        if (spare < need) return held.PlanCover(need, spare);

        long[] available = [held.Copper, held.Silver, held.Gold, held.Platinum, held.Runic];
        long[] taken = new long[CoinUnits.Length];
        long[]? over = null;
        long overCopper = long.MaxValue;
        long remaining = need;
        for (int i = CoinUnits.Length - 1; i >= 0 && remaining > 0; i--)
        {
            long fit = Math.Min(available[i], remaining / CoinUnits[i]);
            if (fit < available[i])
            {
                long worth = need - remaining + (fit + 1) * CoinUnits[i];
                if (worth < overCopper)
                {
                    overCopper = worth;
                    over = (long[])taken.Clone();
                    over[i] = fit + 1;
                }
            }
            taken[i] = fit;
            remaining -= fit * CoinUnits[i];
        }
        if (remaining > 0 && over is not null && overCopper <= OverpayCeiling(need, spare)) taken = over;

        List<(string Currency, long Count)> coins = new();
        long copper = 0;
        for (int i = CoinUnits.Length - 1; i >= 0; i--)
        {
            if (taken[i] <= 0) continue;
            coins.Add((CoinNames[i], taken[i]));
            copper += taken[i] * CoinUnits[i];
        }
        return (coins, copper);
    }

    // A party is six at most, so five followers and 120 orders.
    private const int MaxMembersPermuted = 6;

    // Every short member's coins out of the one purse, inside what can be spared
    // in all. What the first is handed decides what is left for the next, so the
    // members are tried in every order and the one that hands over least is kept
    // (the order as given on a tie). Null when no order covers everyone, with
    // `uncovered` naming whom the order as given left short.
    internal static IReadOnlyList<HandOver>? PlanHandOvers(
        CurrencyHoldings held, IReadOnlyList<(string Name, long Need)> needs, long spare,
        out IReadOnlyList<string> uncovered)
    {
        ArgumentNullException.ThrowIfNull(needs);
        HandOver[]? best = null;
        long bestCopper = long.MaxValue;
        List<string>? firstShort = null;
        foreach (int[] order in Orders(needs.Count))
        {
            HandOver[] plans = new HandOver[needs.Count];
            List<string> left = new();
            CurrencyHoldings purse = held;
            long spareLeft = spare;
            long total = 0;
            foreach (int i in order)
            {
                (string name, long need) = needs[i];
                (IReadOnlyList<(string Currency, long Count)> coins, long copper) = CountOut(purse, need, spareLeft);
                if (copper < need)
                {
                    left.Add(name);
                    continue;
                }
                plans[i] = new HandOver(name, need, coins, copper);
                spareLeft -= copper;
                total += copper;
                purse = Take(purse, coins, copper);
            }
            firstShort ??= left;
            if (left.Count == 0 && total < bestCopper) (best, bestCopper) = (plans, total);
        }
        uncovered = best is null ? firstShort ?? [] : [];
        return best;
    }

    // The order as given first, then every other (Heap's algorithm).
    private static IEnumerable<int[]> Orders(int count)
    {
        int[] order = Enumerable.Range(0, count).ToArray();
        yield return (int[])order.Clone();
        if (count > MaxMembersPermuted) yield break;
        int[] turns = new int[count];
        for (int i = 0; i < count;)
        {
            if (turns[i] < i)
            {
                int with = i % 2 == 0 ? 0 : turns[i];
                (order[with], order[i]) = (order[i], order[with]);
                yield return (int[])order.Clone();
                turns[i]++;
                i = 0;
            }
            else
            {
                turns[i] = 0;
                i++;
            }
        }
    }

    private static CurrencyHoldings Take(
        CurrencyHoldings held, IReadOnlyList<(string Currency, long Count)> coins, long copper)
    {
        foreach ((string currency, long count) in coins)
        {
            int n = (int)count;
            held = currency switch
            {
                "copper" => held with { Copper = held.Copper - n },
                "silver" => held with { Silver = held.Silver - n },
                "gold" => held with { Gold = held.Gold - n },
                "platinum" => held with { Platinum = held.Platinum - n },
                _ => held with { Runic = held.Runic - n },
            };
        }
        return held with { TotalCopperValue = held.TotalCopperValue - copper };
    }

    // ----- the hand-over's confirmation ------------------------------------------

    // InventoryManager.CoinsGivenAway: the game counted coins of ours over to someone.
    public void OnCoinsGivenAway(string recipient, long copper)
    {
        if (_funding is not { } funding || !funding.Pending.TryGetValue(recipient, out var pending))
        {
            CreditLateHandOver(recipient, copper);
            return;
        }
        funding.Unanswered[recipient].Remove(copper);
        long confirmed = pending.Confirmed + copper;
        int left = pending.GivesLeft - 1;
        if (confirmed >= pending.Planned) Settle(funding, recipient, confirmed, failure: null);
        else if (left <= 0)
            Settle(funding, recipient, confirmed,
                $"was handed only {CurrencyFormat.Full(confirmed)} of {CurrencyFormat.Full(pending.Planned)} by the game");
        else funding.Pending[recipient] = (pending.Planned, confirmed, left);
    }

    // InventoryManager.GiveRefused: the game turned a hand-over down, naming whom.
    public void OnGiveRefused(string? recipient)
    {
        if (recipient is null || _funding is not { } funding
            || !funding.Pending.TryGetValue(recipient, out var pending)) return;
        Settle(funding, recipient, pending.Confirmed, "wasn't handed the coin: the game refused the hand-over");
    }

    // InventoryManager.CoinGiveMisaimed: the engine's answer to a coin give at
    // something that isn't a player, or at a player hidden from us (a hidden
    // member can't be handed coin; a sneaking one can: user, 2026-10-10). It names
    // nobody, so it fails the hand-over in hand when there is just one. With
    // several out it is only noted: it is tied to a member when, at the bound,
    // that member alone is unconfirmed (OnGiveWindowElapsed).
    public void OnCoinGiveMisaimed()
    {
        if (_funding is not { } funding) return;
        if (!FailTheOneInHand(
                "could not be given coin: the game says there is nobody of that name here to give to (not here, or hidden from us)"))
            funding.Misaimed = true;
    }

    // InventoryManager.CoinGiveLackedCoins: the engine's answer to a coin give of
    // more of a coin than we hold. It names nobody either.
    public void OnCoinGiveLackedCoins() => FailTheOneInHand(
        "could not be given coin: the game says we don't hold that many of the coin sent");

    private bool FailTheOneInHand(string failure)
    {
        if (_funding is not { Pending.Count: 1 } funding) return false;
        (string name, (long _, long confirmed, int _)) = funding.Pending.First();
        Settle(funding, name, confirmed, failure);
        return true;
    }

    private void OnGiveWindowElapsed()
    {
        if (_funding is not { } funding) return;
        DateTime now = _clock();
        // The line for a target the game can't find names nobody. With one member
        // left unconfirmed it can only have been theirs; with more, one of them
        // may merely be slow, and nobody is said to be hidden on a guess.
        string failure = funding.Misaimed && funding.Pending.Count == 1
            ? $"could not be given coin: no line named them inside {GiveConfirmWindow.TotalSeconds:0} s, and the game answered "
              + "a give with its line for nobody of that name here (not here, or hidden from us)"
            : $"gave no answer to the hand-over: no line from the game inside {GiveConfirmWindow.TotalSeconds:0} s";
        foreach ((string name, (long _, long confirmed, int _)) in funding.Pending.ToArray())
        {
            // The commands went out, so their lines may still come: what was sent
            // and not yet answered is kept on record.
            _lateGives[name] = (funding.Crossing, now, funding.Unanswered[name]);
            Settle(funding, name, confirmed, failure);
        }
    }

    // A coin line for a member whose hand-over had already been given up on: the
    // coin left our purse all the same, so it counts toward what they hold and
    // toward what they were paid at that crossing, whatever was decided meanwhile.
    // Only a line worth what one of the unanswered gives was worth: coin handed to
    // them by hand, or by another feature, is not this crossing's.
    private void CreditLateHandOver(string recipient, long copper)
    {
        if (copper <= 0
            || !_lateGives.TryGetValue(recipient, out (Crossing For, DateTime At, List<long> Unanswered) late))
            return;
        if (_clock() - late.At > LateConfirmationWindow)
        {
            _lateGives.Remove(recipient);
            return;
        }
        if (!late.Unanswered.Remove(copper)) return;
        if (late.Unanswered.Count == 0) _lateGives.Remove(recipient);
        _wealth.NoteGiven(recipient, copper);
        lock (_marksLock)
        {
            long before = _funded.TryGetValue((recipient, late.For), out (long Copper, bool CrossedSince) paid) ? paid.Copper : 0;
            _funded[(recipient, late.For)] = (before + copper, CrossedSince: false);
        }
        _log?.Info(LogCategory,
            $"A late line from the game: {CurrencyFormat.Full(copper)} did reach {recipient}. Counted toward what they hold and what they were paid.");
    }

    // A failure stands against the member at this crossing (MarkUnable).
    private void Settle(Funding funding, string name, long confirmed, string? failure)
    {
        funding.Pending.Remove(name);
        if (confirmed > 0)
        {
            _wealth.NoteGiven(name, confirmed);
            lock (_marksLock) _funded[(name, funding.Crossing)] = (confirmed, CrossedSince: false);
        }
        if (failure is null)
            _log?.Info(LogCategory, $"{funding.Described}: the game confirmed {CurrencyFormat.Full(confirmed)} handed to {name}.");
        else
        {
            MarkUnable(name, funding.Crossing, failure);
            _log?.Info(LogCategory, $"{funding.Described}: {name} {failure}.");
        }
        if (funding.Pending.Count > 0 || !ReferenceEquals(_funding, funding)) return;
        _funding = null;
        funding.Timer?.Dispose();
        _clearGate("the hand-overs are settled");
    }

    private void MarkUnable(string name, Crossing crossing, string why)
    {
        lock (_marksLock) _unable[(name, crossing)] = (_clock(), why);
    }

    // ----- housekeeping ----------------------------------------------------------

    // The step through a paid crossing is let go: every purse in the party is
    // about to pay or its owner to be left, so nothing read before answers for the
    // next one. Done here, where the step is released, and not on the room change:
    // the engine sends its next step from inside that change, and a second toll
    // right behind the first was judged on what was read before the first was paid.
    // What a member was paid here is another matter: that stands until the
    // crossing is seen to have happened (NoteRoomChanged), since a step let go
    // can still bounce, and a member paid for it must not be paid for the retry.
    private void NoteTollTaken() => _wealth.ExpireReadings();

    // The tracker confirmed a move from one room to another. Through an exit that
    // charges, whoever was paid for it has now spent it or been left at it: what
    // they were handed there no longer stands for what they hold. A sailing is no
    // exit, so a fare is also taken as spent on arrival in the room it leads to.
    public void NoteRoomChanged(Room previous, RoomKey now)
    {
        ArgumentNullException.ThrowIfNull(previous);
        HashSet<Crossing> crossed = new();
        foreach (RoomExit exit in previous.Exits.Values)
            if ((exit.Target == now || exit.Landing == now) && KeyOf(in exit) is { } through)
                crossed.Add(through);
        lock (_marksLock)
            foreach ((string Name, Crossing At) key in _funded.Keys
                         .Where(k => crossed.Contains(k.At) || (k.At.Fare && k.At.Target == now)).ToArray())
                _funded[key] = (_funded[key].Copper, CrossedSince: true);
    }

    // MovementController going idle: the trip a crossing was closed on is over.
    public void NoteTripEnded()
    {
        lock (_marksLock) _closed.Clear();
    }

    // Another character's profile: nothing asked, handed over or closed stands.
    public void Reset()
    {
        lock (_marksLock)
        {
            _funded.Clear();
            _unable.Clear();
            _closed.Clear();
        }
        _leftBehind = [];
        _lateGives.Clear();
        bool held = _asking is not null || _funding is not null;
        _asking = null;
        _funding?.Timer?.Dispose();
        _funding = null;
        if (held) _clearGate("party toll check dropped");
    }

    private void Decide(string text)
    {
        // Locked: a closure made for a route search is recorded from its thread.
        lock (_decisionsLock)
        {
            if (_decisions.Count == DecisionsKept) _decisions.RemoveAt(0);
            _decisions.Add($"{_clock():HH:mm:ss} {text}");
        }
        _log?.Info(LogCategory, text);
    }

    // A member who gave no answer: with what we have handed them here and not yet
    // seen spent, when that is why they count as paid.
    private string PaidAndSilent(string name, Crossing crossing)
    {
        lock (_marksLock)
            if (_funded.TryGetValue((name, crossing), out (long Copper, bool CrossedSince) paid) && !paid.CrossedSince)
                return $"{name} no answer, handed {CurrencyFormat.Full(paid.Copper)} for this {crossing.Word}";
        return $"{name} no answer";
    }

    private string DescribeReadings(IReadOnlyList<string> followers, Crossing crossing, long later)
    {
        IReadOnlyCollection<string> leftBehind = _leftBehind;
        IEnumerable<string> purses = followers.Select(name =>
            leftBehind.Contains(name, StringComparer.OrdinalIgnoreCase)
                ? $"{name} left out (known to be left behind, not following through this {crossing.Word})"
                : _wealth.FreshPurse(name) switch
                {
                    (PartyWealthTracker.PurseKnowledge.Read, long copper) => $"{name} {CurrencyFormat.Full(copper)}",
                    (PartyWealthTracker.PurseKnowledge.Silent, _) => PaidAndSilent(name, crossing),
                    _ => $"{name} not asked yet",
                });
        long keep = KeepOnHandCopper;
        string own =_ownPurse() is { } purse
            ? $"own purse {CurrencyFormat.Full(purse)}"
              + (_reservedCopper() > 0 ? $" with {CurrencyFormat.Full(_reservedCopper())} set aside" : string.Empty)
              + (keep > 0 ? $", {CurrencyFormat.Full(keep)} kept on hand" : string.Empty)
              + (later > 0 ? $", {CurrencyFormat.Full(later)} of our own tolls and fares further on this route" : string.Empty)
            : "own purse not read";
        return $"{crossing.Word} {CurrencyFormat.Full(crossing.Copper)}; {string.Join(", ", purses)}; {own}; "
            + $"funding short members {(FundingOn ? "on" : "off")}";
    }

    // Marks are kept by the member's name whatever its case.
    private sealed class FundedComparer : IEqualityComparer<(string Name, Crossing At)>
    {
        public static readonly FundedComparer Instance = new();

        public bool Equals((string Name, Crossing At) x, (string Name, Crossing At) y) =>
            x.At == y.At && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, Crossing At) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name), key.At);
    }
}

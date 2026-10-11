using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Remote;
using MudPlay.Services;

namespace MudPlay.Game.Map;

// A party at a toll exit. The game charges every crosser and leaves behind
// whoever can't pay, so a leader's engine doesn't step through one until it knows
// the whole party gets through (user, 2026-10-10: "either all party members can
// make it, or we go around if possible, if not possible abandon the walk. we read
// all of their purses when we think we're going through it to verify, so if they
// dont respond, assume they cant, and if we can pay for it, for them, give them
// the money they need and continue through it").
//
// At the step through a toll exit (BeforeTollStep):
//   - every follower's purse is asked for (@wealth) unless an answer no older
//     than the freshness window is in hand, and the step is held meanwhile;
//   - all can pay: the step goes;
//   - someone is short or gave no answer, and what we can spare covers them: each
//     is handed what they lack (a silent member the whole toll), the step held
//     until the game confirms every hand-over, then it goes;
//   - otherwise the exit is closed to the party (Closes) and the engine plans
//     again: round it where a way exists, and to a stop naming the toll and who
//     can't pay where none does.
//
// Tolls only. A fare (an NPC's transport, a boat) is left to
// PartyWealthTracker.MinWealth as before.
//
// Everything here runs on the UI thread except Closes and DescribeClosed, which a
// route search can call from a planning thread: they read the purse readings
// through PartyWealthTracker's lock and the marks below through _marksLock.
public sealed class PartyTollGate
{
    private const string LogCategory = "PartyToll";

    // What an engine does with a step through a toll exit. Hold: the PartyToll
    // movement gate is up, and the step is asked about again when it clears.
    public enum StepVerdict { Go, Hold, Closed }

    internal enum Outcome { Clear, Unverified, Fund, Closed }

    // One follower against one toll. Unable: why they can't be paid for here,
    // null when nothing says so.
    internal readonly record struct Member(
        string Name, PartyWealthTracker.PurseKnowledge Knowledge, long Copper, string? Unable = null);

    // MemberUnable: closed because someone short can't be paid for, which coin of
    // ours coming in later doesn't change.
    internal readonly record struct Judgement(
        Outcome Outcome, IReadOnlyList<(string Name, long Need)> Needs, string Summary, bool MemberUnable = false);

    // A toll closed to the party, kept for the trip it was closed on: who the
    // party was, what each short member lacked, and the words for it.
    private sealed record Closure(
        HashSet<string> Followers, IReadOnlyList<(string Name, long Need)> Needs, bool MemberUnable, string Summary);

    // A toll exit as a route search hands it over: where it leads and its price.
    private readonly record struct TollKey(RoomKey Target, int TollGold);

    private sealed class Funding(TollKey toll, string crossing, IDisposable? timer)
    {
        public TollKey Toll { get; } = toll;
        public string Crossing { get; } = crossing;
        public IDisposable? Timer { get; } = timer;
        // Per member: copper asked for, copper the game has confirmed, and the give
        // commands whose lines are still to come.
        public Dictionary<string, (long Planned, long Confirmed, int GivesLeft)> Pending { get; } =
            new(StringComparer.OrdinalIgnoreCase);
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

    // Guards the two mark tables, which a route search reads.
    private readonly object _marksLock = new();
    // What each member was last handed at each toll, in copper, and whether a toll
    // has been crossed since: an answer of theirs after that is their own word, not
    // the echo of what we handed them.
    private readonly Dictionary<(string Name, TollKey Toll), (long Copper, bool CrossedSince)> _funded =
        new(FundedComparer.Instance);
    // Members who can't be paid for at a toll for now, and why: a hand-over the
    // game refused or never confirmed, or one that didn't get them through.
    private readonly Dictionary<(string Name, TollKey Toll), (DateTime At, string Why)> _unable =
        new(FundedComparer.Instance);

    // Tolls closed to the party on the trip under way. A closure outlives the
    // answers it was made on: asked again every 30 s, a toll with no way through
    // would be walked up to and turned from over and over.
    private readonly Dictionary<TollKey, Closure> _closed = new();
    // Members left out of the count for the trip: a hand-over to them got no line
    // from the game and they aren't listed in the room, so they aren't following
    // from here and the toll is no concern of theirs.
    private readonly HashSet<string> _notHere = new(StringComparer.OrdinalIgnoreCase);

    // Hand-overs whose window passed with no line from the game, by member: the
    // toll they were for and when the window passed. A line that comes late still
    // means the coin went, so it is still credited.
    private readonly Dictionary<string, (TollKey Toll, DateTime At)> _lateGives = new(StringComparer.OrdinalIgnoreCase);

    private TollKey? _asking;
    private Funding? _funding;

    // How long after its window a hand-over's line is still taken as its own.
    public TimeSpan LateConfirmationWindow { get; set; } = TimeSpan.FromMinutes(2);

    // Whether a member is listed among the players in the room as last shown. Not
    // a test of who follows (a follower arrives after the room is drawn, and a
    // hidden one is never listed): only read once a hand-over has gone unanswered.
    public Func<string, bool>? SeenInRoom { get; set; }

    // How long a hand-over may go unconfirmed before the member counts as unable.
    // The @wealth window's length: one command each way.
    public TimeSpan GiveConfirmWindow { get; set; } = TimeSpan.FromSeconds(4);

    // The master switch (true = off): off, nothing is asked, given or held.
    public Func<bool>? MasterSwitchOff { get; set; }

    // Whether we have already gone back for this member from this exit on the run
    // under way (PartyComebackManager.WentBackFor): the toll turned them away once.
    public Func<string, RoomKey, RoomKey, bool>? WentBackFor { get; set; }

    // For the bug report: the latest decisions made at tolls, oldest first, each
    // with its time, and the purses the last one was made on. A hand-over and the
    // crossing it paid for are two decisions, so one alone would hide the first.
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

    // cost: the toll in copper. spare: our own purse less what a trip has set
    // aside and less our own toll; null while our purse isn't known.
    internal static Judgement Judge(long cost, long? spare, IReadOnlyList<Member> members)
    {
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
        if (needs.Count == 0) return new Judgement(Outcome.Clear, needs, "every party member holds the toll");

        string who = string.Join("; ", said);
        long total = needs.Sum(n => n.Need);
        if (unable.Count > 0)
            return new Judgement(Outcome.Closed, needs, $"{who}; {string.Join("; ", unable)}", MemberUnable: true);
        if (spare is not { } free)
            return new Judgement(Outcome.Closed, needs,
                $"{who}; your own purse isn't known (inventory not read), so nothing can be handed over");
        if (free < total)
            return new Judgement(Outcome.Closed, needs,
                $"{who}; covering that takes {CurrencyFormat.Full(total)} and you can spare "
                + $"{CurrencyFormat.Full(Math.Max(0, free))} after your own toll");
        return new Judgement(Outcome.Fund, needs, $"{who}; you can spare it");
    }

    private bool Applies(out IReadOnlyList<string> followers)
    {
        followers = [];
        if (!_wealth.Leading) return false;
        followers = _wealth.Followers();
        return followers.Count > 0;
    }

    private static TollKey KeyOf(in RoomExit exit) => new(exit.Target, exit.TollGold);

    private static bool IsToll(in RoomExit exit) => exit.Hint == RoomExitHint.Toll && exit.TollGold > 0;

    private Judgement JudgeNow(in RoomExit exit, IReadOnlyList<string> followers)
    {
        long cost = (long)exit.TollGold * 100;
        TollKey toll = KeyOf(in exit);
        DateTime now = _clock();
        List<Member> members = new(followers.Count);
        foreach (string name in followers)
        {
            (PartyWealthTracker.PurseKnowledge knowledge, long copper) = _wealth.FreshPurse(name);
            string? unable = null;
            lock (_marksLock)
            {
                if (_notHere.Contains(name)) continue;
                // Silent, but handed coin at this toll with no toll crossed since:
                // they hold at least that, which is all there is to go on.
                if (knowledge == PartyWealthTracker.PurseKnowledge.Silent
                    && _funded.TryGetValue((name, toll), out (long Copper, bool CrossedSince) paid) && !paid.CrossedSince)
                    (knowledge, copper) = (PartyWealthTracker.PurseKnowledge.Read, paid.Copper);
                if (_unable.TryGetValue((name, toll), out (DateTime At, string Why) mark)
                    && now - mark.At <= _wealth.FreshnessWindow)
                    unable = mark.Why;
            }
            members.Add(new Member(name, knowledge, copper, unable));
        }
        return Judge(cost, Spare(cost), members);
    }

    // What we can hand over at a toll of this price: the purse on record less what
    // a trip has set aside and less our own toll. Null while the purse isn't known.
    private long? Spare(long cost) =>
        _ownPurse() is { } purse ? purse - Math.Max(0, _reservedCopper()) - cost : null;

    // The judgement on what is known now, with the trip's closures kept and let go:
    // a closure stands while nothing fresh says otherwise, and falls when the party
    // is no longer the one it was made for, when what the short members lacked is
    // covered by their own fresh answers and what we can now spare, or when a
    // fresh judgement lets the party through.
    // atTheStep: asked by the step through the toll, which records its own decision;
    // a closure first made for a route search is recorded here, since nothing else
    // will say why the route turned.
    private Judgement JudgeForTrip(in RoomExit exit, IReadOnlyList<string> followers, bool atTheStep = false)
    {
        Judgement fresh = JudgeNow(in exit, followers);
        TollKey toll = KeyOf(in exit);
        long cost = (long)exit.TollGold * 100;
        Closure? closure;
        lock (_marksLock)
        {
            if (fresh.Outcome == Outcome.Closed)
            {
                bool isNew = !_closed.ContainsKey(toll);
                _closed[toll] = new Closure(
                    new HashSet<string>(followers, StringComparer.OrdinalIgnoreCase),
                    fresh.Needs, fresh.MemberUnable, fresh.Summary);
                if (isNew && !atTheStep)
                    Decide($"the toll into {toll.Target} ({toll.TollGold} gold): closed to the party for this trip. {fresh.Summary}");
                return fresh;
            }
            if (fresh.Outcome != Outcome.Unverified || !_closed.TryGetValue(toll, out closure))
            {
                _closed.Remove(toll);
                return fresh;
            }
            if (!closure.Followers.SetEquals(followers))
            {
                _closed.Remove(toll);
                return fresh;
            }
        }
        // Outside the lock: the purse readings have their own.
        long stillLacking = 0;
        foreach ((string name, long need) in closure.Needs)
            if (_wealth.FreshPurse(name) is not (PartyWealthTracker.PurseKnowledge.Read, long held) || held < cost)
                stillLacking += need;
        if (stillLacking == 0 || (!closure.MemberUnable && Spare(cost) is { } spare && spare >= stillLacking))
        {
            lock (_marksLock) _closed.Remove(toll);
            return fresh;
        }
        return new Judgement(Outcome.Closed, closure.Needs, closure.Summary, closure.MemberUnable);
    }

    // ----- route searches ------------------------------------------------------

    // Whether the party can't be taken through this toll as things stand. False
    // while a follower hasn't been asked: the check at the toll itself asks.
    public bool Closes(in RoomExit exit)
    {
        if (!IsToll(in exit) || !Applies(out IReadOnlyList<string> followers)) return false;
        return JudgeForTrip(in exit, followers).Outcome == Outcome.Closed;
    }

    // Who can't pay and by how much, for the line a walk with no other way gives.
    // Null when the party doesn't close this toll.
    public string? DescribeClosed(in RoomExit exit)
    {
        if (!IsToll(in exit) || !Applies(out IReadOnlyList<string> followers)) return null;
        Judgement judged = JudgeForTrip(in exit, followers);
        return judged.Outcome == Outcome.Closed ? judged.Summary : null;
    }

    // ----- the step through a toll ---------------------------------------------

    public StepVerdict BeforeTollStep(RoomKey from, Direction direction, in RoomExit exit)
    {
        if (!IsToll(in exit) || MasterSwitchOff?.Invoke() == true) return StepVerdict.Go;
        if (!Applies(out IReadOnlyList<string> followers)) return StepVerdict.Go;
        // The gate is up for an ask or a hand-over: the step comes back when it clears.
        if (_asking is not null || _funding is not null) return StepVerdict.Hold;

        TollKey toll = KeyOf(in exit);
        string crossing = PaidCrossingDescriber.Describe(from, direction, in exit, _roomName);
        long cost = (long)exit.TollGold * 100;
        NoteFundedAndTurnedAway(from, toll, cost, followers);

        Judgement judged = JudgeForTrip(in exit, followers, atTheStep: true);
        LastReadings = DescribeReadings(followers, cost);
        switch (judged.Outcome)
        {
            case Outcome.Clear:
                Decide($"{crossing}: every party member can pay ({LastReadings}); going through");
                NoteTollTaken();
                return StepVerdict.Go;

            case Outcome.Closed:
                Decide($"{crossing}: not taken with the party. {judged.Summary}");
                return StepVerdict.Closed;

            case Outcome.Unverified:
                _asking = toll;
                _log?.Info(LogCategory,
                    $"{crossing}: asking {string.Join(", ", followers)} what they hold (@wealth) before stepping through.");
                _assertGate($"asking the party's purses at {crossing}");
                _wealth.Verify(() => OnVerified(crossing));
                return StepVerdict.Hold;

            default:
                return BeginFunding(toll, crossing, judged);
        }
    }

    private void OnVerified(string crossing)
    {
        if (_asking is null) return;
        _asking = null;
        _log?.Info(LogCategory, $"{crossing}: the party's purses are read.");
        _clearGate("the party's purses are read");
    }

    // A member we handed coin at this toll, gone back for from it since, and short
    // again: the hand-over didn't get them through, and they aren't paid twice.
    // One who says, after a crossing, that they hold the toll themselves is no
    // longer about what we handed them.
    private void NoteFundedAndTurnedAway(RoomKey from, TollKey toll, long cost, IReadOnlyList<string> followers)
    {
        foreach (string name in followers)
        {
            (long Copper, bool CrossedSince) given;
            lock (_marksLock)
                if (!_funded.TryGetValue((name, toll), out given)) continue;
            (PartyWealthTracker.PurseKnowledge knowledge, long copper) = _wealth.FreshPurse(name);
            if (knowledge == PartyWealthTracker.PurseKnowledge.Unasked) continue;
            if (knowledge == PartyWealthTracker.PurseKnowledge.Read && copper >= cost)
            {
                if (given.CrossedSince)
                    lock (_marksLock) _funded.Remove((name, toll));
                continue;
            }
            if (WentBackFor?.Invoke(name, from, toll.Target) != true) continue;
            lock (_marksLock)
                _unable[(name, toll)] = (_clock(),
                    $"was handed {CurrencyFormat.Full(given.Copper)} at this toll already and was still turned away: not paid for twice");
        }
    }

    private StepVerdict BeginFunding(TollKey toll, string crossing, Judgement judged)
    {
        long cost = (long)toll.TollGold * 100;
        if (_holdings() is not { } holdings || _ownPurse() is not { } purse)
        {
            foreach ((string name, _) in judged.Needs)
                MarkUnable(name, toll, "can't be handed coin: the inventory isn't read");
            Decide($"{crossing}: not taken with the party. {judged.Summary}, but the inventory isn't read, so no coin can be counted out");
            return StepVerdict.Closed;
        }

        // Each member's coins are counted out of what the ones before left, and
        // out of what is still spare: never our own toll, never what a trip set aside.
        long spare = purse - Math.Max(0, _reservedCopper()) - cost;
        CurrencyHoldings left = holdings;
        List<(string Name, IReadOnlyList<(string Currency, long Count)> Coins, long Copper)> plans = new();
        List<string> uncovered = new();
        foreach ((string name, long need) in judged.Needs)
        {
            (IReadOnlyList<(string Currency, long Count)> coins, long copper) = CountOut(left, need, spare);
            if (copper < need)
            {
                MarkUnable(name, toll,
                    $"can't be handed coin: can't make {CurrencyFormat.Full(need)} from the coins carried");
                uncovered.Add(name);
                continue;
            }
            plans.Add((name, coins, copper));
            spare -= copper;
            left = Take(left, coins, copper);
        }
        if (uncovered.Count > 0)
        {
            Decide($"{crossing}: not taken with the party. {judged.Summary}, but the coins carried can't make what "
                + $"{string.Join(", ", uncovered)} needs exactly");
            return StepVerdict.Closed;
        }

        Funding funding = new(toll, crossing, _schedule(GiveConfirmWindow, OnGiveWindowElapsed));
        _funding = funding;
        _assertGate($"handing coin to the party at {crossing}");
        Decide($"{crossing}: {judged.Summary}. Handing over "
            + string.Join(", ", plans.Select(p => $"{CurrencyFormat.Full(p.Copper)} to {p.Name}"))
            + ", then going through");
        // Everyone is on the list before the first command goes: a confirmation
        // that came back at once would otherwise find the list empty and end it.
        foreach ((string name, IReadOnlyList<(string Currency, long Count)> coins, long copper) in plans)
            funding.Pending[name] = (copper, 0, coins.Count);
        string runic = _runicName();
        foreach ((string name, IReadOnlyList<(string Currency, long Count)> coins, _) in plans)
            foreach ((string currency, long count) in coins)
                _send(CoinGiveCommand.For(currency, count, name, runic));
        return StepVerdict.Hold;
    }

    // How far over a member's shortfall a hand-over may go when the coins carried
    // don't make it exactly. Nothing: the next coin up can be a platinum piece or a
    // runic for a gap of a few gold, and the ruling is "give them the money they
    // need". The one place to change if some overpaying is ever wanted.
    internal const long OverpayAllowedCopper = 0;

    // The coins to hand over for a shortfall, largest first, out of what is held
    // and inside what can be spared. Comes back short of the need when the coins
    // held can't make it within OverpayAllowedCopper.
    internal static (IReadOnlyList<(string Currency, long Count)> Coins, long Copper) CountOut(
        CurrencyHoldings held, long need, long spare) =>
        held.PlanCover(need, Math.Min(spare, need + OverpayAllowedCopper));

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

    private void OnGiveWindowElapsed()
    {
        if (_funding is not { } funding) return;
        DateTime now = _clock();
        foreach ((string name, (long _, long confirmed, int _)) in funding.Pending.ToArray())
        {
            // The command went out, so its line may still come: kept on record.
            _lateGives[name] = (funding.Toll, now);
            // Nothing came back at all and they aren't listed in the room: they are
            // somewhere else, the give reached nobody, and a member who isn't here
            // isn't following through this toll. Left out of the count for the trip
            // rather than held against the whole party.
            if (confirmed == 0 && SeenInRoom?.Invoke(name) == false)
            {
                lock (_marksLock) _notHere.Add(name);
                Settle(funding, name, confirmed,
                    "got no hand-over line and isn't seen in the room: not counted at tolls on this trip", unable: false);
                continue;
            }
            Settle(funding, name, confirmed,
                $"wasn't seen to get the coin: no confirmation inside {GiveConfirmWindow.TotalSeconds:0} s");
        }
    }

    // A coin line for a member whose hand-over had already been given up on: the
    // coin left our purse all the same, so it counts toward what they hold and
    // toward what they were paid at that toll, whatever was decided meanwhile.
    private void CreditLateHandOver(string recipient, long copper)
    {
        if (copper <= 0 || !_lateGives.TryGetValue(recipient, out (TollKey Toll, DateTime At) late)) return;
        if (_clock() - late.At > LateConfirmationWindow)
        {
            _lateGives.Remove(recipient);
            return;
        }
        _wealth.NoteGiven(recipient, copper);
        lock (_marksLock)
        {
            long before = _funded.TryGetValue((recipient, late.Toll), out (long Copper, bool CrossedSince) paid) ? paid.Copper : 0;
            _funded[(recipient, late.Toll)] = (before + copper, CrossedSince: false);
        }
        _log?.Info(LogCategory,
            $"A late line from the game: {CurrencyFormat.Full(copper)} did reach {recipient}. Counted toward what they hold and what they were paid.");
    }

    // unable: the failure stands against the member at this toll (MarkUnable).
    private void Settle(Funding funding, string name, long confirmed, string? failure, bool unable = true)
    {
        funding.Pending.Remove(name);
        if (confirmed > 0)
        {
            _wealth.NoteGiven(name, confirmed);
            lock (_marksLock) _funded[(name, funding.Toll)] = (confirmed, CrossedSince: false);
        }
        if (failure is null)
            _log?.Info(LogCategory, $"{funding.Crossing}: the game confirmed {CurrencyFormat.Full(confirmed)} handed to {name}.");
        else
        {
            if (unable) MarkUnable(name, funding.Toll, failure);
            _log?.Info(LogCategory, $"{funding.Crossing}: {name} {failure}.");
        }
        if (funding.Pending.Count > 0 || !ReferenceEquals(_funding, funding)) return;
        _funding = null;
        funding.Timer?.Dispose();
        _clearGate("the hand-overs are settled");
    }

    private void MarkUnable(string name, TollKey toll, string why)
    {
        lock (_marksLock) _unable[(name, toll)] = (_clock(), why);
    }

    // ----- housekeeping ----------------------------------------------------------

    // The step through a toll is let go: every purse in the party is about to pay
    // or its owner to be left, so nothing read before answers for the next toll.
    // Done here, where the step is released, and not on the room change: the
    // engine sends its next step from inside that change, and a second toll right
    // behind the first was judged on what was read before the first was paid.
    private void NoteTollTaken()
    {
        _wealth.ExpireReadings();
        lock (_marksLock)
            foreach ((string Name, TollKey Toll) key in _funded.Keys.ToArray())
                _funded[key] = (_funded[key].Copper, CrossedSince: true);
    }

    // MovementController going idle: the trip a toll was closed on is over, and so
    // is the count that left a member out.
    public void NoteTripEnded()
    {
        lock (_marksLock)
        {
            _closed.Clear();
            _notHere.Clear();
        }
    }

    // Another character's profile: nothing asked, handed over or closed stands.
    public void Reset()
    {
        lock (_marksLock)
        {
            _funded.Clear();
            _unable.Clear();
            _closed.Clear();
            _notHere.Clear();
        }
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

    private string DescribeReadings(IReadOnlyList<string> followers, long cost)
    {
        IEnumerable<string> purses = followers.Select(name => _wealth.FreshPurse(name) switch
        {
            (PartyWealthTracker.PurseKnowledge.Read, long copper) => $"{name} {CurrencyFormat.Full(copper)}",
            (PartyWealthTracker.PurseKnowledge.Silent, _) => $"{name} no answer",
            _ => $"{name} not asked yet",
        });
        string own = _ownPurse() is { } purse
            ? $"own purse {CurrencyFormat.Full(purse)}"
              + (_reservedCopper() > 0 ? $" with {CurrencyFormat.Full(_reservedCopper())} set aside" : string.Empty)
            : "own purse not read";
        return $"toll {CurrencyFormat.Full(cost)}; {string.Join(", ", purses)}; {own}";
    }

    // Marks are kept by the member's name whatever its case.
    private sealed class FundedComparer : IEqualityComparer<(string Name, TollKey Toll)>
    {
        public static readonly FundedComparer Instance = new();

        public bool Equals((string Name, TollKey Toll) x, (string Name, TollKey Toll) y) =>
            x.Toll == y.Toll && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, TollKey Toll) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Name), key.Toll);
    }
}

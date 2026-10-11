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

    internal readonly record struct Judgement(
        Outcome Outcome, IReadOnlyList<(string Name, long Need)> Needs, string Summary);

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

    private TollKey? _asking;
    private Funding? _funding;

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
    public IReadOnlyList<string> Decisions => _decisions.ToArray();
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
            return new Judgement(Outcome.Closed, needs, $"{who}; {string.Join("; ", unable)}");
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
                if (_unable.TryGetValue((name, toll), out (DateTime At, string Why) mark)
                    && now - mark.At <= _wealth.FreshnessWindow)
                    unable = mark.Why;
            }
            members.Add(new Member(name, knowledge, copper, unable));
        }
        long? spare = _ownPurse() is { } purse ? purse - Math.Max(0, _reservedCopper()) - cost : null;
        return Judge(cost, spare, members);
    }

    // ----- route searches ------------------------------------------------------

    // Whether the party can't be taken through this toll as things stand. False
    // while a follower hasn't been asked: the check at the toll itself asks.
    public bool Closes(in RoomExit exit)
    {
        if (!IsToll(in exit) || !Applies(out IReadOnlyList<string> followers)) return false;
        return JudgeNow(in exit, followers).Outcome == Outcome.Closed;
    }

    // Who can't pay and by how much, for the line a walk with no other way gives.
    // Null when the party doesn't close this toll.
    public string? DescribeClosed(in RoomExit exit)
    {
        if (!IsToll(in exit) || !Applies(out IReadOnlyList<string> followers)) return null;
        Judgement judged = JudgeNow(in exit, followers);
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

        Judgement judged = JudgeNow(in exit, followers);
        LastReadings = DescribeReadings(followers, cost);
        switch (judged.Outcome)
        {
            case Outcome.Clear:
                Decide($"{crossing}: every party member can pay ({LastReadings}); going through");
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
            (IReadOnlyList<(string Currency, long Count)> coins, long copper) = left.PlanCover(need, spare);
            if (copper < need)
            {
                MarkUnable(name, toll,
                    $"can't be handed {CurrencyFormat.Full(need)}: the coins carried don't make it up inside what can be spared");
                uncovered.Add(name);
                continue;
            }
            plans.Add((name, coins, copper));
            spare -= copper;
            left = Take(left, coins, copper);
        }
        if (uncovered.Count > 0)
        {
            Decide($"{crossing}: not taken with the party. {judged.Summary}, but the coins carried don't make up what "
                + $"{string.Join(", ", uncovered)} needs");
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
        if (_funding is not { } funding || !funding.Pending.TryGetValue(recipient, out var pending)) return;
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
        foreach ((string name, (long _, long confirmed, int _)) in funding.Pending.ToArray())
            Settle(funding, name, confirmed,
                $"wasn't seen to get the coin: no confirmation inside {GiveConfirmWindow.TotalSeconds:0} s");
    }

    private void Settle(Funding funding, string name, long confirmed, string? failure)
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
            MarkUnable(name, funding.Toll, failure);
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

    // The tracker confirmed a move from one room to another. Through a toll exit,
    // every purse in the party has changed or its owner was left: nothing read
    // before it answers for the next toll.
    public void NoteRoomChanged(Room previous, RoomKey now)
    {
        ArgumentNullException.ThrowIfNull(previous);
        foreach (RoomExit exit in previous.Exits.Values)
        {
            if (!IsToll(in exit) || (exit.Target != now && exit.Landing != now)) continue;
            _wealth.ExpireReadings();
            lock (_marksLock)
                foreach ((string Name, TollKey Toll) key in _funded.Keys.ToArray())
                    _funded[key] = (_funded[key].Copper, CrossedSince: true);
            return;
        }
    }

    // Another character, or the party gone: nothing asked or handed over stands.
    public void Reset()
    {
        lock (_marksLock)
        {
            _funded.Clear();
            _unable.Clear();
        }
        bool held = _asking is not null || _funding is not null;
        _asking = null;
        _funding?.Timer?.Dispose();
        _funding = null;
        if (held) _clearGate("party toll check dropped");
    }

    private void Decide(string text)
    {
        if (_decisions.Count == DecisionsKept) _decisions.RemoveAt(0);
        _decisions.Add($"{_clock():HH:mm:ss} {text}");
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

using System.Collections.Generic;
using MudPlay.Services;

namespace MudPlay.Game.Remote;

// What a leader's client knows of its followers' purses, for the crossings that
// charge each crosser. Unlike the level tracker, which keeps every member's level
// warm because a level is stable, wealth drifts with loot and spending, so it is
// asked for only when a route needs it and believed only briefly.
//
// Two users, with two rules:
//   - NPC fares and boat fares (MinWealth): the party's poorest CONFIRMED purse. A
//     follower with no fresh reading is skipped, not counted as broke, so a fare
//     is refused only when someone known can't pay it.
//   - Toll exits (FreshPurse, Verify): every follower is asked before the party
//     steps through one, and one who doesn't answer counts as unable to pay
//     (user, 2026-10-10). PartyTollGate holds the step, asks here and decides.
//
// Nothing here touches the wire from a route search. MinWealth and FreshPurse are
// cache reads: BFS explores toll and fare edges the party will never walk, so a
// probe from there would ask about crossings nobody takes. Probe is the
// route-scoped ask (MovementFilter.WarmForRoute, only when the route a walk would
// take crosses a paid exit), and Verify the ask at the toll itself.
//
// Our own purse is folded into MinWealth from the same live snapshot
// MovementFilter's self-only branch reads; when it is unknown MinWealth stands
// down (null), matching "an unknown wallet never refuses a walk".
public sealed class PartyWealthTracker
{
    // What is known of one follower's purse, as far as a toll about to be crossed
    // goes. Unasked: no answer and no ask that could have had one. Silent: asked,
    // and no usable answer came inside the probe's window. Read: they said.
    public enum PurseKnowledge { Unasked, Silent, Read }

    private readonly PartyState _party;
    private readonly PartyWealthProbe _probe;
    private readonly Func<long?> _selfWealth;
    private readonly Action<Action> _post;
    private readonly Func<DateTime> _clock;
    private readonly LogService? _log;

    // Guards the readings and the silences: a route can be planned off the UI
    // thread while a reply lands on it.
    private readonly object _readingsLock = new();
    private readonly Dictionary<string, (long Copper, DateTime At, long Seq)> _readings =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime At, long Seq)> _silent =
        new(StringComparer.OrdinalIgnoreCase);
    // Readings and silences are numbered as they land, and ExpireReadings voids
    // everything numbered so far: a clock can't order two things in one instant.
    private long _seq;
    private long _expiredThrough;

    private DateTime _lastPollAt = DateTime.MinValue;
    private bool _queryInFlight;
    private readonly List<Action> _onSettled = new();

    // A reading older than this is treated as absent, and a route-scoped poll
    // older than this re-fires on the next Probe. One window governs both: wealth
    // drifts, so a reading only stays trustworthy briefly, and the same horizon
    // debounces the poll so one route expansion fires at most one @wealth round.
    public TimeSpan FreshnessWindow { get; set; } = TimeSpan.FromSeconds(30);

    public PartyWealthTracker(
        PartyState party,
        PartyWealthProbe probe,
        Func<long?> selfWealth,
        Action<Action>? post = null,
        LogService? log = null)
        : this(party, probe, selfWealth, post, clock: null, log: log) { }

    internal PartyWealthTracker(
        PartyState party,
        PartyWealthProbe probe,
        Func<long?> selfWealth,
        Action<Action>? post,
        Func<DateTime>? clock,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(selfWealth);
        _party = party;
        _probe = probe;
        _selfWealth = selfWealth;
        _post = post ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        _clock = clock ?? (() => DateTime.UtcNow);
        _log = log;
    }

    // Record one member's freshly-reported wealth (copper). Bound to the probe's
    // recordWealth seam, so each @wealth reply lands here timestamped.
    public void Record(string givenName, long copper)
    {
        if (string.IsNullOrEmpty(givenName)) return;
        string given = GivenName(givenName);
        lock (_readingsLock)
        {
            _readings[given] = (copper, _clock(), ++_seq);
            _silent.Remove(given);
        }
    }

    // What a member last said they hold, in copper, however old the reading; null
    // when they have never been read. For a judgement made after the fact (did a
    // toll turn them away a moment ago?), where the last word is all there is.
    public long? LastReading(string givenName)
    {
        if (string.IsNullOrEmpty(givenName)) return null;
        lock (_readingsLock)
            return _readings.TryGetValue(GivenName(givenName), out (long Copper, DateTime At, long Seq) r) ? r.Copper : null;
    }

    // The party's minimum CONFIRMED on-hand wealth in copper, or null when the
    // party gate shouldn't apply (solo, not leading, or our own wallet unknown).
    // For fares, which are refused only on a purse someone has reported: a
    // follower with no fresh reading is skipped, not counted as broke.
    public long? MinWealth()
    {
        if (!Leading) return null;
        if (_selfWealth() is not { } self) return null;   // our own wallet unknown → stand down

        long min = self;
        DateTime now = _clock();
        lock (_readingsLock)
        {
            foreach (PartyMember m in _party.Members)
            {
                if (m.IsSelf || string.IsNullOrEmpty(m.Name)) continue;
                if (_readings.TryGetValue(GivenName(m.Name), out (long Copper, DateTime At, long Seq) r)
                 && now - r.At <= FreshnessWindow)
                    min = Math.Min(min, r.Copper);
            }
        }
        return min;
    }

    // Whether this character leads a party: the only case in which anyone else's
    // purse is ours to check.
    public bool Leading => _party.IsInParty && _party.SelfIsLeader;

    // The members who cross with us, by given name: everyone listed but ourselves
    // and those invited and not yet following.
    public IReadOnlyList<string> Followers()
    {
        List<string> followers = new();
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf || m.IsInvited || string.IsNullOrEmpty(m.Name)) continue;
            followers.Add(GivenName(m.Name));
        }
        return followers;
    }

    // What a follower's purse is known to hold as far as a toll goes: their answer
    // or their silence, whichever is later, while it is inside the freshness window
    // and no toll has been crossed since (ExpireReadings).
    public (PurseKnowledge Knowledge, long Copper) FreshPurse(string givenName)
    {
        if (string.IsNullOrEmpty(givenName)) return (PurseKnowledge.Unasked, 0);
        string given = GivenName(givenName);
        DateTime now = _clock();
        lock (_readingsLock)
        {
            bool read = _readings.TryGetValue(given, out (long Copper, DateTime At, long Seq) r)
                && r.Seq > _expiredThrough && now - r.At <= FreshnessWindow;
            bool silent = _silent.TryGetValue(given, out (DateTime At, long Seq) s)
                && s.Seq > _expiredThrough && now - s.At <= FreshnessWindow;
            if (read && (!silent || r.Seq > s.Seq)) return (PurseKnowledge.Read, r.Copper);
            return silent ? (PurseKnowledge.Silent, 0) : (PurseKnowledge.Unasked, 0);
        }
    }

    // Coin we handed a follower and saw the game confirm: they hold at least that
    // on top of what they last said, or at least that when they said nothing.
    public void NoteGiven(string givenName, long copper)
    {
        if (string.IsNullOrEmpty(givenName) || copper <= 0) return;
        string given = GivenName(givenName);
        long held = FreshPurse(given) is (PurseKnowledge.Read, long had) ? had : 0;
        lock (_readingsLock)
        {
            _readings[given] = (held + copper, _clock(), ++_seq);
            _silent.Remove(given);
        }
    }

    // The party has just gone through a toll: every purse paid, or its owner was
    // turned away, so nothing said before it answers for the next one. The
    // readings stay for LastReading, which wants the last word whatever its age.
    public void ExpireReadings()
    {
        lock (_readingsLock) _expiredThrough = _seq;
    }

    // The master switch (true = off): off, no @wealth round-trip is started. The
    // route cards' own count (PartyWealthProbe.QueryAsync) is a hand action and is
    // not gated here.
    public Func<bool>? MasterSwitchOff { get; set; }

    // Fire an @wealth probe when the last one is older than the freshness window.
    // The replies land via Record, and whoever gave none is noted silent, for the
    // check at the toll. Debounced so one route expansion (a multi-segment loop
    // can hit several toll legs) fires at most one round-trip. Called
    // route-scoped from MovementFilter.WarmForRoute, so nothing polls for a paid
    // exit that is only inside the search frontier.
    public void Probe()
    {
        if (MasterSwitchOff?.Invoke() == true) return;
        if (_clock() - _lastPollAt < FreshnessWindow) return;
        _log?.Info("PartyWealth", "A toll or fare is on the planned route: asking the party's purses (@wealth).");
        StartQuery();
    }

    // Ask every member's purse now and run onSettled when the round is over: all
    // have answered, or the probe's window has passed. A round already under way
    // is joined, not doubled. With the master switch off nobody is asked and
    // onSettled runs at once.
    public void Verify(Action onSettled)
    {
        ArgumentNullException.ThrowIfNull(onSettled);
        if (MasterSwitchOff?.Invoke() == true)
        {
            onSettled();
            return;
        }
        _onSettled.Add(onSettled);
        if (!_queryInFlight) StartQuery();
    }

    private void StartQuery()
    {
        _queryInFlight = true;
        _lastPollAt = _clock();
        // Posted: the ask goes on the wire outside the planning or step-sending
        // call that wanted it.
        _post(() => _probe.Query(OnQueryComplete));
    }

    private void OnQueryComplete(PartyWealthProbe.PartyWealthResult result)
    {
        DateTime now = _clock();
        List<string> unanswered = new();
        lock (_readingsLock)
        {
            foreach (string given in Followers())
            {
                if (result.WealthByMember.ContainsKey(given)) continue;
                _silent[given] = (now, ++_seq);
                unanswered.Add(given);
            }
        }
        if (unanswered.Count > 0)
            _log?.Info("PartyWealth",
                $"No purse read from {string.Join(", ", unanswered)} inside the @wealth window: counted as unable to pay a toll.");

        _queryInFlight = false;
        Action[] waiting = _onSettled.ToArray();
        _onSettled.Clear();
        foreach (Action settled in waiting) settled();
        RoundSettled?.Invoke();
    }

    // An @wealth round is over: every answer that was coming is in, and whoever
    // gave none is noted silent. For whoever planned a route on the purses as
    // they stood before it.
    public event Action? RoundSettled;

    private static string GivenName(string name)
    {
        int space = name.IndexOf(' ');
        return space >= 0 ? name[..space] : name;
    }
}

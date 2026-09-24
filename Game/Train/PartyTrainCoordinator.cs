using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Game.GameData;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Train;

// Party-friendly auto-train. Solo auto-train can't run in a group — a follower's
// `train` drops that follower, the leader's disbands the party — so with the
// Auto-train-party toggle on, training becomes a party trip the leader runs:
//
//   Member side (toggle on, following a leader):
//     * reports its own readiness to the leader (`@ptrain st …`) whenever it changes
//       and whenever the leader asks (`@ptrain ask`) — every figure computed from its
//       own settings, purse, bank and earn rate;
//     * obeys the leader's trip orders — `give` (cover a party member's fee),
//       `with` (withdraw its own fee at the bank stop), `train` (train here, never
//       walking on) — and reports `done` once it's back in the party, since a
//       follower's train drops it until the leader re-invites.
//
//   Leader side (toggle on, leading, a loop / auto-lair running):
//     * asks the party for reports when the roster changes and every few minutes;
//     * feeds its own report plus every fresh member report to PartyTrainQuorum —
//       go once the set number of members is ready, the leader counting as one
//       like everyone else, power-levelers not waited for;
//     * on Fire: plans the money (PartyTrainFundingPlanner), the stops
//       (PartyTrainItineraryPlanner — members first, leader last), pauses the engine
//       (TrainerWalkManager.BeginPartyTrip), walks the party round, trains itself
//       last, re-forms the party and resumes the engine.
//
// Members that never report — toggle off, another client — are simply not waited
// for: they follow the leader there and back like any other walk.
//
// Orders are obeyed only from the current party leader, and only while this
// character's own toggle is on, so a member's consent is its own setting.
public sealed class PartyTrainCoordinator : IDisposable
{
    public const string LogCategory = "PartyTrain";

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    // Wire economy: every telepath a leader sends at the wrong moment costs it a
    // little exp/hour, so the handshake is kept to the minimum. A member is asked ONCE
    // per time it joins (and only after JoinGrace, so the join-time @version probe
    // has said whether it's worth asking); after that it pushes its own changes. The
    // repeats are one "ready yet?" `@ptrain ask` to a reporting member still waiting
    // AskBuffer past its projected ready time (its own push usually comes first — this
    // only covers one that didn't), and one `@level` to a member on another client
    // AskBuffer past its projected level-up. Nothing goes out mid-combat.
    private static readonly TimeSpan JoinGrace = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AskBuffer = TimeSpan.FromMinutes(10);
    // A member's train is a realm excursion plus a re-invite; past this the trip
    // goes on without its `done`.
    private static readonly TimeSpan MemberTrainTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan TripCooldown = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(4);

    private readonly PartyState _party;
    private readonly TrainerWalkManager _trainer;
    private readonly Func<double> _expPerHour;
    private readonly Func<CurrencyHoldings?> _holdings;
    private readonly Func<long> _keepOnHandCopper;
    private readonly Func<(string? Name, long Copper)> _largestDeposit;
    private readonly Func<string> _runicName;
    private readonly Func<RoomKey?> _currentRoom;
    private readonly Func<RoomKey, RoomKey, int?> _distance;
    private readonly Func<IReadOnlyList<TrainerShop>> _trainers;
    private readonly Func<string, RoomKey, RoomKey?> _nearestBankBranch;
    private readonly Func<RoomKey, bool> _walkTo;
    private readonly Action<string> _send;
    private readonly Func<string, (string? Version, DateTime? AtUtc)> _recordedVersion;
    private readonly Action<IReadOnlyList<string>> _reformParty;
    private readonly Action<TimeSpan, Action> _armTimer;
    private readonly Func<bool> _inCombat;
    private readonly Func<int> _selfLevel;
    private readonly Func<long> _selfExp;
    private readonly Func<string, int?> _recordedLevel;
    private readonly Func<DateTimeOffset> _now;
    private readonly LogService? _log;

    // Leader side.
    // OurExpAt is our own exp when the reading arrived: a party member gains what we
    // gain from a kill, so the member's exp is shown as its report plus our gains since
    // — an estimate (a split, or a member out of the room, opens gaps). ReAsked marks
    // the one "ready yet?" ask spent on this report.
    private readonly Dictionary<string, (PartyTrainStatus Status, DateTimeOffset At, long OurExpAt, bool ReAsked)> _reports =
        new(StringComparer.OrdinalIgnoreCase);
    // @level readings for members that can't send @ptrain reports (another client, or
    // an older MudPlay) — enough for the Party window's leveling line, never a vote.
    private readonly Dictionary<string, (int Level, long? Needed, string? TheirEta, DateTimeOffset At, long OurExpAt)> _levelReplies =
        new(StringComparer.OrdinalIgnoreCase);
    // Members already asked this membership, and when each was first seen in it.
    private readonly Dictionary<string, DateTimeOffset> _asked = new(StringComparer.OrdinalIgnoreCase);
    // How long an asked member has to answer before it's taken as not taking part.
    private static readonly TimeSpan ReplyWindow = TimeSpan.FromSeconds(30);
    // The highest level each member has announced as trainable (NoteTrainableAnnounce).
    private readonly Dictionary<string, int> _announced = new(StringComparer.OrdinalIgnoreCase);
    // Followers to re-invite after a solo-fallback train (see AllowSoloRun).
    private IReadOnlyList<string> _soloReform = [];
    private readonly Dictionary<string, DateTimeOffset> _joinedAt = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;
    private bool _tripRunning;
    private string _lastReason = "";
    private TaskCompletionSource<bool>? _walkTcs;
    private RoomKey _walkTarget;
    private HashSet<string>? _awaitingDone;
    private readonly HashSet<string> _seenGone = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource<bool>? _doneTcs;

    // Member side. A trained follower's `done` waits until it's back in the party
    // (see FlushPendingDone); SawDrop records that we saw ourselves leave it.
    private sealed record PendingDone(string Leader, int Levels, DateTimeOffset At)
    {
        public bool SawDrop { get; set; }
    }

    private string _lastSentKey = "";
    private PendingDone? _pendingDone;
    // The leader that asked us for reports; we only ever report to it, and only while
    // we're following it. A leader that never asks (toggle off, not using the feature)
    // is never sent anything.
    private string? _reportTo;

    // Our own client may not register the drop a train causes; past this with no drop
    // seen and still apparently following, the `done` goes anyway — the leader also
    // settles us on seeing us leave and rejoin, so a refused `done` costs nothing.
    private static readonly TimeSpan DoneFallback = TimeSpan.FromSeconds(30);

    private bool _disposed;

    public PartyTrainCoordinator(
        PartyState party,
        TrainerWalkManager trainer,
        Func<double> expPerHour,
        Func<CurrencyHoldings?> holdings,
        Func<long> keepOnHandCopper,
        Func<(string? Name, long Copper)> largestDeposit,
        Func<string> runicName,
        Func<RoomKey?> currentRoom,
        Func<RoomKey, RoomKey, int?> distance,
        Func<IReadOnlyList<TrainerShop>> trainers,
        Func<string, RoomKey, RoomKey?> nearestBankBranch,
        Func<RoomKey, bool> walkTo,
        Action<string> send,
        Func<string, (string? Version, DateTime? AtUtc)> recordedVersion,
        Action<IReadOnlyList<string>> reformParty,
        Action<TimeSpan, Action> armTimer,
        Func<bool> inCombat,
        Func<int> selfLevel,
        Func<long> selfExp,
        Func<string, int?> recordedLevel,
        Func<DateTimeOffset>? now = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(trainer);
        _party = party;
        _trainer = trainer;
        _expPerHour = expPerHour ?? throw new ArgumentNullException(nameof(expPerHour));
        _holdings = holdings ?? throw new ArgumentNullException(nameof(holdings));
        _keepOnHandCopper = keepOnHandCopper ?? throw new ArgumentNullException(nameof(keepOnHandCopper));
        _largestDeposit = largestDeposit ?? throw new ArgumentNullException(nameof(largestDeposit));
        _runicName = runicName ?? throw new ArgumentNullException(nameof(runicName));
        _currentRoom = currentRoom ?? throw new ArgumentNullException(nameof(currentRoom));
        _distance = distance ?? throw new ArgumentNullException(nameof(distance));
        _trainers = trainers ?? throw new ArgumentNullException(nameof(trainers));
        _nearestBankBranch = nearestBankBranch ?? throw new ArgumentNullException(nameof(nearestBankBranch));
        _walkTo = walkTo ?? throw new ArgumentNullException(nameof(walkTo));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _recordedVersion = recordedVersion ?? throw new ArgumentNullException(nameof(recordedVersion));
        _reformParty = reformParty ?? throw new ArgumentNullException(nameof(reformParty));
        _armTimer = armTimer ?? throw new ArgumentNullException(nameof(armTimer));
        _inCombat = inCombat ?? throw new ArgumentNullException(nameof(inCombat));
        _selfLevel = selfLevel ?? throw new ArgumentNullException(nameof(selfLevel));
        _selfExp = selfExp ?? throw new ArgumentNullException(nameof(selfExp));
        _recordedLevel = recordedLevel ?? throw new ArgumentNullException(nameof(recordedLevel));
        _now = now ?? (() => DateTimeOffset.Now);
        _log = log;

        _party.PropertyChanged += OnPartyChanged;
        _party.Members.CollectionChanged += OnRosterChanged;
        _armTimer(TickInterval, Tick);
    }

    // ----- bug report / status surface -------------------------------------

    public bool TripRunning => _tripRunning;
    public string LastDecision => _lastReason;
    public DateTimeOffset? CooldownUntil => _cooldownUntil > _now() ? _cooldownUntil : null;
    public string? PendingDoneFor => _pendingDone?.Leader;
    public IReadOnlyList<string> AskedMembers => _asked.Keys.ToList();
    public bool HasPartners => Leading && Settings.AutoTrainParty && Partners().Any();
    public string? ReportingTo => _reportTo;

    public IReadOnlyList<(string Name, PartyTrainStatus Status, DateTimeOffset At)> Reports =>
        _reports.Select(kv => (kv.Key, kv.Value.Status, kv.Value.At)).ToList();

    // This character's own report as it would send it right now.
    public PartyTrainStatus OwnStatus() => BuildOwnStatus();

    // ----- periodic tick ----------------------------------------------------

    private void Tick()
    {
        if (_disposed) return;
        try
        {
            MemberTick();
            LeaderTick();
        }
        finally
        {
            _armTimer(TickInterval, Tick);
        }
    }

    private AutoTrainerSettings Settings => _trainer.CurrentSettings;

    private bool Following =>
        _party.IsInParty && !_party.SelfIsLeader && !string.IsNullOrEmpty(_party.LeaderName);

    private bool Leading => _party.IsInParty && _party.SelfIsLeader;

    private void OnPartyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (!Following)
        {
            _lastSentKey = "";
            _reportTo = null;
            if (_pendingDone is { } p) p.SawDrop = true;
        }
        FlushPendingDone();
    }

    private void OnRosterChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;
        NoteAwaitedPresence();
        FlushPendingDone();
        RefreshTrainInfo();
    }

    // ----- member side ------------------------------------------------------

    private void MemberTick()
    {
        FlushPendingDone();
        if (!Following || _inCombat()) return;
        string leader = GivenName(_party.LeaderName!);
        if (!string.Equals(_reportTo, leader, StringComparison.OrdinalIgnoreCase)) return;
        if (!Settings.AutoTrainParty)
        {
            // Opting out after reporting: tell the leader once, or it would keep
            // counting our last report for as long as we stay in the party.
            if (_lastSentKey.Length > 0)
            {
                SendStatus(BuildOwnStatus() with
                {
                    Readiness = PartyTrainReadiness.Off, LevelsToTrain = 0, CostCopper = 0,
                });
                _lastSentKey = "";
            }
            return;
        }
        PartyTrainStatus status = BuildOwnStatus();
        // Report on a change that matters to the leader — readiness, level, how far a
        // trip would take us — not on every ETA or purse tick.
        string key = $"{leader}|{status.Readiness}|{status.Level}|{status.LevelsToTrain}";
        if (key == _lastSentKey) return;
        SendStatus(status);
    }

    private void SendStatus(PartyTrainStatus status)
    {
        if (!Following) return;
        string leader = GivenName(_party.LeaderName!);
        _lastSentKey = $"{leader}|{status.Readiness}|{status.Level}|{status.LevelsToTrain}";
        _send($"/{leader} @ptrain st {status.Encode()}");
        _log?.Info(LogCategory, $"Reported to {leader}: {Describe(status)}.");
    }

    private PartyTrainStatus BuildOwnStatus()
    {
        PartyTrainSelfAssessment a = _trainer.AssessForParty(_expPerHour());
        long cash = _holdings() is { } h ? h.TotalCopperValue : 0;
        long spare = Math.Max(0, cash - Math.Max(0, _keepOnHandCopper())
                                      - (a.Readiness == PartyTrainReadiness.Ready ? a.CostCopper : 0));
        (string? bankName, long bank) = _largestDeposit();
        return new PartyTrainStatus(a.Readiness, a.Level, a.ClassNumber, a.LevelsToTrain, a.CostCopper,
            cash, spare, bank, bankName, a.EtaSeconds, a.Exp, a.NextExp);
    }

    // `@ptrain ask` — the leader wants our reports from now on. Remembered even with
    // our toggle off, so switching it on later reports without being asked again. The
    // reply itself goes out on the next tick outside combat.
    public void ReceiveAsk(string sender)
    {
        if (!IsLeader(sender)) return;
        _reportTo = GivenName(sender);
        _lastSentKey = "";
        MemberTick();
    }

    // `@ptrain train <target>` — train here, up to the leader's target for us.
    public void ReceiveTrain(string sender, int targetLevel)
    {
        if (!Settings.AutoTrainParty || !IsLeader(sender)) return;
        string leader = GivenName(sender);
        _log?.Info(LogCategory, $"{leader} says train (target level {targetLevel}).");
        _trainer.TrainForParty(targetLevel, (levels, report) =>
        {
            _log?.Info(LogCategory, $"Party train finished: {report}");
            _pendingDone = new PendingDone(leader, levels, _now());
            FlushPendingDone();
        });
    }

    // `@ptrain give <copper> <recipient>` — cover part of someone's fee from our spare.
    public void ReceiveGive(string sender, long copper, string recipient)
    {
        if (!Settings.AutoTrainParty || !IsLeader(sender)) return;
        if (_holdings() is not { } holdings)
        {
            _log?.Info(LogCategory, $"Can't give {recipient} {copper:N0} copper — inventory not parsed yet.");
            return;
        }
        long cap = BuildOwnStatus().SpareCopper;
        GiveCoins(holdings, recipient, copper, cap);
    }

    // `@ptrain with <copper>` — draw our own fee at the bank the party stopped at.
    public void ReceiveWithdraw(string sender, long copper)
    {
        if (!Settings.AutoTrainParty || !IsLeader(sender) || copper <= 0) return;
        _send($"with {copper}");
        _log?.Info(LogCategory, $"Withdrawing {copper:N0} copper for training.");
    }

    // A follower's train drops it from the party until the leader re-invites it, and a
    // `@ptrain done` from outside the party is refused by the leader's whitelist — so
    // it waits until we're following that leader again. A run that trained nothing
    // never left, so it goes at once.
    private void FlushPendingDone()
    {
        if (_pendingDone is not { } pending) return;
        if (pending.Levels > 0)
        {
            bool back = Following
                && string.Equals(GivenName(_party.LeaderName!), pending.Leader, StringComparison.OrdinalIgnoreCase);
            if (!back || (!pending.SawDrop && _now() - pending.At < DoneFallback)) return;
        }
        _pendingDone = null;
        _send($"/{pending.Leader} @ptrain done {pending.Levels}");
    }

    private bool IsLeader(string sender) =>
        Following && string.Equals(GivenName(sender), GivenName(_party.LeaderName!), StringComparison.OrdinalIgnoreCase);

    // ----- leader side ------------------------------------------------------

    // `@ptrain st <payload>` — a member's report.
    public void ReceiveStatus(string sender, string payload)
    {
        if (!Leading) return;
        if (!PartyTrainStatus.TryDecode(payload, out PartyTrainStatus status))
        {
            _log?.Info(LogCategory, $"Unreadable report from {sender}: \"{payload}\"");
            return;
        }
        string given = GivenName(sender);
        _reports[given] = (status, _now(), _selfExp(), false);
        _log?.Info(LogCategory, $"{given}: {Describe(status)}.");
        RefreshTrainInfo();
    }

    // `@ptrain done <levels>` — a member at the current stop has trained and is back.
    public void ReceiveDone(string sender, int levels)
    {
        string given = GivenName(sender);
        _log?.Info(LogCategory, $"{given} done training ({levels} level{(levels == 1 ? "" : "s")}).");
        Settle(given);
    }

    private void Settle(string given)
    {
        if (_awaitingDone is null || !_awaitingDone.Remove(given)) return;
        if (_awaitingDone.Count == 0) _doneTcs?.TrySetResult(true);
    }

    // A member that trained drops out of the roster and comes back on the re-invite;
    // seeing that round trip settles it even when its `done` never gets through.
    private void NoteAwaitedPresence()
    {
        if (_awaitingDone is null) return;
        HashSet<string> present = new(ActiveMemberGivens(), StringComparer.OrdinalIgnoreCase);
        foreach (string name in _awaitingDone.ToList())
        {
            if (!present.Contains(name)) _seenGone.Add(name);
            else if (_seenGone.Remove(name))
            {
                _log?.Info(LogCategory, $"{name} is back in the party after training.");
                Settle(name);
            }
        }
    }

    private void LeaderTick()
    {
        if (_tripRunning) return;
        AutoTrainerSettings s = Settings;
        if (!s.AutoTrainParty || !Leading)
        {
            RefreshTrainInfo();
            return;
        }

        MaybeAsk();
        DropStaleReports();
        RefreshTrainInfo();
        if (_now() < _cooldownUntil) return;
        // Same gate as the solo armed run: auto-train detours a running grind, it
        // doesn't pull an idle party off somewhere.
        if (_trainer.IsBusy || !_trainer.EngineActive) return;

        // A party trip needs a partner — another member on MudPlay with the feature
        // on. Alone, the leader trains by its own solo settings (AllowSoloRun).
        if (!Partners().Any()) return;

        List<PartyTrainParticipant> participants = Participants(out _);
        PartyTrainDecision decision = PartyTrainQuorum.Decide(
            participants, Math.Max(0, s.PartyLevelGap), s.PartyMinReady);

        if (decision.Reason != _lastReason)
        {
            _lastReason = decision.Reason;
            if (decision.Verdict != PartyTrainVerdict.Idle || participants.Count > 1)
                _log?.Info(LogCategory, $"Party train: {decision.Reason}"
                    + (decision.Skipped.Count > 0 ? $" (not waiting for {string.Join(", ", decision.Skipped)})" : ""));
        }

        if (decision.Verdict == PartyTrainVerdict.Fire)
            _ = RunTripAsync(decision, participants);
    }

    // At most one ask per member per membership (see JoinGrace): `@ptrain ask` to a
    // member the join-time @version probe shows can speak it (or that hasn't been
    // probed — it may be on MudPlay), `@level` to the rest when we hold no reading for
    // them. The single repeat: an other-client member once its projected level-up has
    // passed, since it can't push the change itself.
    private void MaybeAsk()
    {
        if (_inCombat()) return;
        foreach (string given in ActiveMemberGivens())
        {
            if (!_joinedAt.TryGetValue(given, out DateTimeOffset joined)) _joinedAt[given] = joined = _now();
            if (_now() - joined < JoinGrace) continue;

            if (Speaks(given))
            {
                if (!_reports.TryGetValue(given, out var report))
                {
                    if (_asked.TryAdd(given, _now())) _send($"/{given} @ptrain ask");
                }
                else if (!report.ReAsked && ReadyAskDue(report.Status, report.At, _expPerHour(), _now()))
                {
                    _reports[given] = report with { ReAsked = true };
                    _send($"/{given} @ptrain ask");
                }
            }
            else if (!_levelReplies.TryGetValue(given, out var reading))
            {
                if (_asked.TryAdd(given, _now())) _send($"/{given} @level");
            }
            else if (LevelReaskDue(reading.Needed, reading.At, _expPerHour(), _now()))
            {
                _levelReplies.Remove(given);   // the reply re-adds it; no reply → no repeat
                _send($"/{given} @level");
            }
        }
    }

    // A reporting member that's still waiting, AskBuffer past when it was projected to
    // be ready, gets one "ready yet?" ask — its own push should have come by then, so
    // this only catches one that didn't. The projection is its own time-to-ready, else
    // its time to next level at OUR rate. Ready / blocked / off members aren't asked.
    public static bool ReadyAskDue(PartyTrainStatus s, DateTimeOffset reportedAt, double ourRate, DateTimeOffset now)
    {
        if (s.Readiness != PartyTrainReadiness.Waiting) return false;
        TimeSpan? projected = s.EtaSeconds >= 0
            ? TimeSpan.FromSeconds(s.EtaSeconds)
            : s.NextExp > 0 ? Calculators.ExperienceTableCalculator.CalcTimeToLevel(s.NextExp, s.Exp, (long)ourRate) : null;
        return projected is { } p && now >= reportedAt + p + AskBuffer;
    }

    // An other-client member's @level reading is worth refreshing once its projected
    // level-up (exp to next level at OUR rate — the party shares the kills) plus a
    // buffer has passed. No projection (no rate, no "needed", already there) → never.
    public static bool LevelReaskDue(long? needed, DateTimeOffset readAt, double ourRate, DateTimeOffset now)
    {
        if (needed is not { } n || n <= 0) return false;
        return Calculators.ExperienceTableCalculator.CalcTimeToLevel(n, 0, (long)ourRate) is { } tnl
            && now >= readAt + tnl + AskBuffer;
    }

    private bool Speaks(string given)
    {
        (string? version, DateTime? atUtc) = _recordedVersion(given);
        return SpeaksPartyTrain(version, atUtc, _now().UtcDateTime);
    }

    // As below, but an OLDER-MudPlay record that wasn't confirmed today counts as
    // unknown: MudPlay updates itself, and the once-a-day join probe can miss (its
    // telepath not delivered), leaving yesterday's version standing all day. Asking
    // costs one telepath; wrongly writing the member off costs the feature.
    public static bool SpeaksPartyTrain(string? version, DateTime? recordedAtUtc, DateTime nowUtc)
    {
        if (SpeaksPartyTrain(version)) return true;
        bool olderMudPlay = version is not null
            && version.StartsWith(AppInfo.DisplayName + " ", StringComparison.OrdinalIgnoreCase);
        bool confirmedToday = recordedAtUtc is { } at && at.ToLocalTime().Date == nowUtc.ToLocalTime().Date;
        return olderMudPlay && !confirmedToday;
    }

    // The first MudPlay that understands @ptrain.
    private static readonly Version PartyTrainSince = new(3, 105, 0);

    // Whether a player's recorded @version reply ("MudPlay 3.105.0", "MegaMud 1.03u")
    // says it can take part. Unknown (never probed) → assume yes.
    public static bool SpeaksPartyTrain(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return true;
        string[] parts = version.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !string.Equals(parts[0], "MudPlay", StringComparison.OrdinalIgnoreCase)) return false;
        // Leading dotted number only — tolerates a build suffix ("3.105.0+abc1234").
        string number = new(parts[1].TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        return Version.TryParse(number, out Version? v) && v >= PartyTrainSince;
    }

    // A parsed @level reply from anyone (PartyLevelProbe) — the join-time probe's
    // reply, or one the user asked for by hand. Shown as-is for members that don't
    // send @ptrain reports. For a member that does, it's a fresher exp reading than
    // the report the row is estimating from, so it re-anchors that estimate: the
    // report's exp becomes the reply's total (MudPlay's reply carries it; a reply with
    // only "needed" is read against the report's next-level mark), counted from now.
    public void NoteLevelProgress(string given, int level, long? needed, string? theirEta, long? totalExp)
    {
        string name = GivenName(given);
        // An @exp reply doesn't always state the level — keep the one we know.
        if (level <= 0)
            level = _levelReplies.TryGetValue(name, out var prior) ? prior.Level
                : _reports.TryGetValue(name, out var rep) ? rep.Status.Level
                : _recordedLevel(name) ?? 0;
        _levelReplies[name] = (level, needed, theirEta, _now(), _selfExp());

        if (_reports.TryGetValue(name, out var report))
        {
            PartyTrainStatus st = report.Status;
            long? exp = totalExp
                ?? (needed is { } n && st.NextExp > 0 ? st.NextExp - n : null);
            if (exp is { } e && e > 0)
            {
                st = st with
                {
                    Exp = e,
                    Level = level > 0 ? level : st.Level,
                    NextExp = st.NextExp > e ? st.NextExp : needed is { } n2 ? e + n2 : st.NextExp,
                };
                _reports[name] = report with { Status = st, OurExpAt = _selfExp() };
            }
        }
        RefreshTrainInfo();
    }

    // A member's "I can now train to level: N" (LevelUpAnnouncer, on whatever channel
    // they picked) — shown on its Party-window line as "can train LN" until its level
    // reaches N. Display only, never a vote: whether a member goes on a party trip is
    // its own @ptrain report, which its client pushes (against its own gates) right
    // behind the announcement. The point is the members that announce but won't
    // auto-train — Auto-train party off, waiting on their own gates, another client —
    // whose line would otherwise read "not ready" or an estimate.
    public void NoteTrainableAnnounce(string sender, int level)
    {
        if (!_party.IsInParty || level <= 1) return;
        string name = GivenName(sender);
        if (!ActiveMemberGivens().Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        if (_announced.TryGetValue(name, out int prior) && prior >= level) return;
        _announced[name] = level;
        _log?.Info(LogCategory, $"{name} announced level {level} is trainable.");
        RefreshTrainInfo();
    }

    // "I can now train to level: N", as LevelUpAnnouncer words it — the say / gossip /
    // gangpath / yell / telepath message body, quotes tolerated.
    public static bool TryParseTrainableAnnounce(string message, out int level)
    {
        level = 0;
        string m = message.Trim().Trim('"').Trim();
        if (!m.StartsWith(LevelUpAnnouncer.AnnounceText, StringComparison.OrdinalIgnoreCase)) return false;
        return int.TryParse(m[LevelUpAnnouncer.AnnounceText.Length..].Trim().TrimEnd('.', '!'), out level) && level > 1;
    }

    // Readings belong to a membership: a member that leaves drops its report, reading
    // and ask record, and is asked afresh if it comes back.
    private void DropStaleReports()
    {
        HashSet<string> present = new(ActiveMemberGivens(), StringComparer.OrdinalIgnoreCase);
        foreach (string name in _joinedAt.Keys.ToList())
        {
            if (present.Contains(name)) continue;
            _joinedAt.Remove(name);
            _asked.Remove(name);
            _reports.Remove(name);
            _levelReplies.Remove(name);
            _announced.Remove(name);
        }
    }

    // Members taking part: on MudPlay, feature on, still in the party.
    private IEnumerable<string> Partners() =>
        ActiveMemberGivens().Where(n => _reports.TryGetValue(n, out var r) && r.Status.Readiness != PartyTrainReadiness.Off);

    // Whether every member present has had its say: reported, known to be on another
    // client, or asked and silent past ReplyWindow. Until then the solo fallback
    // holds off — replies land a few seconds after the party forms.
    private bool PartnersSettled() => ActiveMemberGivens().All(n =>
        _reports.ContainsKey(n)
        || !Speaks(n)
        || (_asked.TryGetValue(n, out DateTimeOffset at) && _now() - at >= ReplyWindow));

    // Gate for the SOLO armed run while in a party. Leading with Auto-train party on
    // and nobody to train with, the leader trains by its own solo settings — the
    // members just follow — and the party is re-formed afterwards (AfterSoloRun), since
    // the leader's train disbands it. Following, or with the toggle off, the solo run
    // stays out of a party as before.
    public bool AllowSoloRun()
    {
        if (!_party.IsInParty) return true;
        if (!Leading || !Settings.AutoTrainParty || _tripRunning) return false;
        if (!PartnersSettled() || Partners().Any()) return false;
        _soloReform = ActiveMemberGivens().ToList();
        return true;
    }

    // After a train run that trained something: re-collect the party a solo-fallback
    // train disbanded. A no-op for any other run.
    public void AfterSoloRun()
    {
        if (_soloReform.Count == 0) return;
        IReadOnlyList<string> followers = _soloReform;
        _soloReform = [];
        _log?.Info(LogCategory, $"Solo train done — re-inviting {string.Join(", ", followers)}.");
        _reformParty(followers);
    }

    private List<PartyTrainParticipant> Participants(out string self)
    {
        self = SelfGiven();
        List<PartyTrainParticipant> list = new() { new(self, IsLeader: true, BuildOwnStatus()) };
        foreach (string given in ActiveMemberGivens())
            if (_reports.TryGetValue(given, out var r))
                list.Add(new(given, IsLeader: false, r.Status));
        return list;
    }

    private async Task RunTripAsync(PartyTrainDecision decision, List<PartyTrainParticipant> participants)
    {
        _tripRunning = true;
        _soloReform = [];   // this trip re-forms the party itself
        bool started = false;
        try
        {
            string self = SelfGiven();
            Dictionary<string, PartyTrainStatus> statuses = participants.ToDictionary(
                p => p.Name, p => p.Status, StringComparer.OrdinalIgnoreCase);
            HashSet<string> trainees = new(decision.Trainees, StringComparer.OrdinalIgnoreCase);

            PartyTrainFundingOutcome funding = PartyTrainFundingPlanner.Plan(participants
                .Select(p => new PartyTrainFunder(p.Name, trainees.Contains(p.Name), p.Status.CostCopper,
                    p.Status.CashCopper, p.Status.SpareCopper, p.Status.BankCopper, p.Status.BankName))
                .ToList());
            foreach (string name in funding.Unfunded)
            {
                trainees.Remove(name);
                _log?.Info(LogCategory, $"{name} can't cover training this trip — sitting it out.");
            }

            if (trainees.Count == 0)
            {
                _log?.Info(LogCategory, "Party train: nobody ready can cover their training — not going.");
                return;
            }

            if (_currentRoom() is not { } here)
            {
                _log?.Info(LogCategory, "Party train: current room unknown — not going.");
                return;
            }

            RoomKey? bankRoom = null;
            if (funding.BankName is { } bankName)
            {
                bankRoom = _nearestBankBranch(bankName, here);
                if (bankRoom is null)
                {
                    foreach (PartyBankWithdrawal w in funding.Withdrawals) trainees.Remove(w.Name);
                    _log?.Info(LogCategory, $"No reachable branch of {bankName} — its withdrawers sit this trip out.");
                }
            }

            PartyTrainee? leader = trainees.Contains(self) && statuses.TryGetValue(self, out PartyTrainStatus ls)
                ? new PartyTrainee(self, ls.Level, ls.ClassNumber, ls.Level + ls.LevelsToTrain)
                : null;
            List<PartyTrainee> members = trainees
                .Where(n => !string.Equals(n, self, StringComparison.OrdinalIgnoreCase) && statuses.ContainsKey(n))
                .Select(n => new PartyTrainee(n, statuses[n].Level, statuses[n].ClassNumber,
                    statuses[n].Level + statuses[n].LevelsToTrain))
                .ToList();

            IReadOnlyList<PartyTrainStop> stops = PartyTrainItineraryPlanner.Plan(
                _trainers(), members, leader, _trainer.CurrentSettings.DisabledTrainers ?? [],
                bankRoom ?? here, _distance);
            if (stops.Count == 0)
            {
                _log?.Info(LogCategory, "Party train: no reachable, allowed trainer serves anyone ready — not going.");
                return;
            }

            if (!_trainer.BeginPartyTrip())
            {
                _log?.Info(LogCategory, "Party train: the trainer is busy — not going.");
                return;
            }
            started = true;
            _log?.Info(LogCategory,
                $"Party train trip: training {string.Join(", ", trainees)} across {stops.Count} stop(s)"
                + (bankRoom is { } br ? $", via the bank at {br.Map}/{br.Room}" : "") + ".");

            // Coin changes hands where we stand, before anyone walks.
            bool gave = false;
            foreach (PartyCoinTransfer t in funding.Transfers)
            {
                if (!trainees.Contains(t.To)) continue;
                gave = true;
                if (string.Equals(t.From, self, StringComparison.OrdinalIgnoreCase))
                {
                    if (_holdings() is { } h) GiveCoins(h, t.To, t.Copper, BuildOwnStatus().SpareCopper);
                }
                else
                {
                    _send($"/{t.From} @ptrain give {t.Copper} {t.To}");
                }
            }
            if (gave) await DelayAsync(SettleDelay);

            if (bankRoom is { } bank)
            {
                if (!await WalkAsync(bank))
                {
                    _trainer.EndPartyTrip("couldn't reach the bank.");
                    started = false;
                    return;
                }
                foreach (PartyBankWithdrawal w in funding.Withdrawals)
                {
                    if (!trainees.Contains(w.Name)) continue;
                    if (string.Equals(w.Name, self, StringComparison.OrdinalIgnoreCase)) _send($"with {w.Copper}");
                    else _send($"/{w.Name} @ptrain with {w.Copper}");
                }
                await DelayAsync(SettleDelay);
            }

            foreach (PartyTrainStop stop in stops)
            {
                RoomKey room = new(stop.Trainer.Map, stop.Trainer.Room);
                if (!await WalkAsync(room))
                {
                    _trainer.EndPartyTrip($"couldn't reach {stop.Trainer.Name} ({room.Map}/{room.Room}).");
                    started = false;
                    return;
                }
                List<string> atStop = stop.Members.ToList();
                if (stop.LeaderTrains) atStop.Add(self);
                _log?.Info(LogCategory, $"At {stop.Trainer.Name} — training {string.Join(", ", atStop)}.");

                if (stop.Members.Count > 0)
                    await TrainMembersAsync(stop.Members, members);

                if (stop.LeaderTrains && leader is { } l)
                {
                    // Our train disbands the party, so note who to pull back first.
                    List<string> followers = ActiveMemberGivens().ToList();
                    (int levels, string report) = await TrainSelfAsync(l.TargetLevel);
                    _log?.Info(LogCategory, $"Leader train: {report}");
                    if (levels > 0 && followers.Count > 0) _reformParty(followers);
                }
            }

            _trainer.EndPartyTrip("all stops done.");
            started = false;
            // A member that trained left and rejoined, so it's asked afresh; one that
            // didn't keeps its ask record and only counts again once it pushes a
            // change — no retrying the same failed trip every cooldown.
            foreach (string name in trainees) _reports.Remove(name);
            RefreshTrainInfo();
        }
        catch (Exception ex)
        {
            _log?.Info(LogCategory, $"Party train trip failed: {ex.Message}");
        }
        finally
        {
            if (started) _trainer.EndPartyTrip("aborted.");
            _tripRunning = false;
            _cooldownUntil = _now() + TripCooldown;
            _walkTcs = null;
            _awaitingDone = null;
            _doneTcs = null;
        }
    }

    private async Task TrainMembersAsync(IReadOnlyList<string> names, List<PartyTrainee> members)
    {
        _awaitingDone = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        _seenGone.Clear();
        _doneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> done = _doneTcs;
        foreach (string name in names)
        {
            int target = members.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).TargetLevel;
            _send($"/{name} @ptrain train {target}");
        }
        _armTimer(MemberTrainTimeout, () => done.TrySetResult(false));
        if (!await done.Task && _awaitingDone is { Count: > 0 } missing)
            _log?.Info(LogCategory, $"No word from {string.Join(", ", missing)} after {MemberTrainTimeout.TotalSeconds:0}s — moving on.");
        _awaitingDone = null;
        _doneTcs = null;
    }

    private Task<(int Levels, string Report)> TrainSelfAsync(int targetLevel)
    {
        TaskCompletionSource<(int, string)> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _trainer.TrainForParty(targetLevel, (levels, report) => tcs.TrySetResult((levels, report)));
        return tcs.Task;
    }

    private Task<bool> WalkAsync(RoomKey room)
    {
        if (_currentRoom() == room) return Task.FromResult(true);
        if (!_walkTo(room)) return Task.FromResult(false);
        _walkTarget = room;
        _walkTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _walkTcs.Task;
    }

    // Walker events, forwarded by AppServices. Only an in-flight trip walk cares.
    public void OnWalkEvent(WalkEventKind kind)
    {
        if (_walkTcs is not { } tcs) return;
        switch (kind)
        {
            case WalkEventKind.Finished:
                _walkTcs = null;
                tcs.TrySetResult(_currentRoom() == _walkTarget);
                break;
            case WalkEventKind.Failed:
            case WalkEventKind.Stopped:
                _walkTcs = null;
                tcs.TrySetResult(false);
                break;
        }
    }

    private Task DelayAsync(TimeSpan delay)
    {
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _armTimer(delay, () => tcs.TrySetResult(true));
        return tcs.Task;
    }

    // The Party window's per-row fields. The level ("L20 Druid") shows in every role
    // from whatever we know. The line under the bars — exp, time to next level at OUR
    // rate (a party shares the kills), ready state — shows only while we lead with
    // the toggle on, from each member's report and our own on the self row. Rows with
    // nothing fresh are cleared, so a stale line never lingers.
    // Our own exp moved ("You gain N experience.") — redraw now rather than on the
    // next tick, so the self row's exp and TNL track every kill.
    public void OnOwnExpChanged() => RefreshTrainInfo();

    private void RefreshTrainInfo()
    {
        // Following too: our own row is the status we'd report, and the others' come
        // from their @level / @exp replies at our (shared) exp/hour. Only the leader
        // holds members' reports.
        bool show = _party.IsInParty && Settings.AutoTrainParty;
        double rate = show ? _expPerHour() : 0;
        foreach (PartyMember m in _party.Members)
        {
            if (string.IsNullOrEmpty(m.Name)) continue;
            PartyTrainStatus? status = null;
            long gain = 0;
            if (show)
            {
                if (m.IsSelf) status = BuildOwnStatus();
                else if (_reports.TryGetValue(GivenName(m.Name), out var r))
                {
                    status = r.Status;
                    gain = Math.Max(0, _selfExp() - r.OurExpAt);
                }
            }

            (int Level, long? Needed, string? TheirEta, DateTimeOffset At, long OurExpAt)? reply =
                !m.IsSelf && _levelReplies.TryGetValue(GivenName(m.Name), out var lr) ? lr : null;

            int level = status?.Level
                ?? reply?.Level
                ?? (m.IsSelf ? _selfLevel() : _recordedLevel(m.Name) ?? 0);
            if (m.KnownLevel != level) m.KnownLevel = level;

            // An announced trainable level stands until they're seen at it (trained).
            int? canTrain = show && !m.IsSelf && _announced.TryGetValue(GivenName(m.Name), out int a) && a > level
                ? a : null;

            string text = status is { } st ? RowText(st, rate, gain, canTrain)
                : show && reply is { } r2 ? LevelReplyText(r2.Level, r2.Needed, r2.TheirEta, rate, Math.Max(0, _selfExp() - r2.OurExpAt),
                    canTrain, otherClient: !Speaks(GivenName(m.Name)))
                : canTrain is { } c ? $"can train L{c}"
                : "";
            if (m.TrainInfo != text) m.TrainInfo = text;
        }
    }

    // "4,120,331 xp · TNL ~1h 5m · ready +2 (12,345c)". Readings aren't refreshed on a
    // timer (the member pushes its own changes), so between reports the exp is the
    // report plus the exp WE've gained since (gain), and the TNL is worked from that.
    // A member that announced a trainable level (canTrain) but isn't going to the
    // trainer with us — off, blocked, or not yet past its own gates — says so ahead of
    // its state: "can train L2 · party train off".
    private static string RowText(PartyTrainStatus s, double ourRate, long gain, int? canTrain = null)
    {
        long exp = s.Exp > 0 ? s.Exp + gain : 0;
        string expText = exp > 0 ? $"{exp:N0} xp" : "? xp";
        string tnlText = $"TNL {Remaining(s.NextExp > 0 && exp > 0 ? s.NextExp - exp : null, ourRate) ?? "?"}";
        string state = s.Readiness switch
        {
            PartyTrainReadiness.Ready => $"ready +{s.LevelsToTrain} ({s.CostCopper:N0}c)",
            PartyTrainReadiness.Blocked => "no party train",
            PartyTrainReadiness.Off => "party train off",
            _ => "not ready",
        };
        if (canTrain is { } c && s.Readiness != PartyTrainReadiness.Ready) state = $"can train L{c} · {state}";
        return $"{expText} · {tnlText} · {state}";
    }

    // A line from a member's @level / @exp reply — a member that doesn't report, or
    // anyone on a follower's screen (reports only go to the leader). Its "needed" only
    // runs to the NEXT level — MegaMUD doesn't count past it the way our banked-aware
    // TNL does — so the line names that level. Their own "will level in" is shown only
    // while our rate is unknown. An announced trainable level (canTrain) outranks the
    // reading, which predates it. otherClient tags a member on another client (or an
    // older MudPlay), which is why it can't report.
    private static string LevelReplyText(int level, long? needed, string? theirEta, double ourRate, long gain,
        int? canTrain = null, bool otherClient = true)
    {
        string tag = otherClient ? " · other client" : "";
        if (canTrain is { } c) return $"can train L{c}{tag}";
        if (needed is not { } n) return $"L{level}{tag}";
        if (n <= 0) return $"L{level + 1} reached · can train{tag}";
        long left = Math.Max(0, n - gain);
        string when = Remaining(left, ourRate) ?? theirEta ?? "?";
        return $"{left:N0} to L{level + 1} · TNL {when}{tag}";
    }

    // Exp still to go, as a time at our rate: "~1h 5m", "due" once the estimate says
    // it's reached (the next report / re-ask settles it), null with no rate or figure.
    private static string? Remaining(long? expLeft, double ourRate)
    {
        if (expLeft is not { } left) return null;
        if (left <= 0) return "due";
        return Calculators.ExperienceTableCalculator.CalcTimeToLevel(left, 0, (long)ourRate) is { } t
            ? $"~{Calculators.ExperienceTableCalculator.FormatTimeToLevel(t)}"
            : null;
    }

    // ----- shared -----------------------------------------------------------

    private void GiveCoins(CurrencyHoldings holdings, string recipient, long copper, long cap)
    {
        (IReadOnlyList<(string Currency, long Count)> coins, long given) = holdings.PlanCover(copper, cap);
        if (coins.Count == 0)
        {
            _log?.Info(LogCategory, $"Nothing spare to give {recipient} ({copper:N0} copper asked).");
            return;
        }
        foreach ((string currency, long count) in coins)
            _send($"give {count} {(currency == "runic" ? _runicName() : currency)} to {recipient}");
        _log?.Info(LogCategory, given >= copper
            ? $"Gave {recipient} {given:N0} copper toward training."
            : $"Gave {recipient} {given:N0} of {copper:N0} copper asked — all we could spare.");
    }

    private IEnumerable<string> ActiveMemberGivens()
    {
        foreach (PartyMember m in _party.Members)
        {
            if (m.IsSelf || m.IsInvited || string.IsNullOrEmpty(m.Name)) continue;
            yield return GivenName(m.Name);
        }
    }

    private string SelfGiven()
    {
        foreach (PartyMember m in _party.Members)
            if (m.IsSelf && !string.IsNullOrEmpty(m.Name)) return GivenName(m.Name);
        return "self";
    }

    private static string Describe(PartyTrainStatus s) => s.Readiness switch
    {
        PartyTrainReadiness.Ready => $"ready, level {s.Level} +{s.LevelsToTrain} for {s.CostCopper:N0} copper",
        PartyTrainReadiness.Blocked => $"nothing to party-train at level {s.Level}",
        PartyTrainReadiness.Off => "switched Auto-train party off",
        _ => s.EtaSeconds >= 0
            ? $"waiting, level {s.Level}, ready in ~{Calculators.ExperienceTableCalculator.FormatTimeToLevel(TimeSpan.FromSeconds(s.EtaSeconds))}"
            : $"waiting, level {s.Level}",
    };

    private static string GivenName(string name)
    {
        string trimmed = name.Trim();
        int space = trimmed.IndexOf(' ');
        return space >= 0 ? trimmed[..space] : trimmed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _party.PropertyChanged -= OnPartyChanged;
        _party.Members.CollectionChanged -= OnRosterChanged;
        _walkTcs?.TrySetResult(false);
        _doneTcs?.TrySetResult(false);
    }
}

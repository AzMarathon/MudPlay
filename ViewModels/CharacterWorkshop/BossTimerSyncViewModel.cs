using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// The @timer sync merge window. Pick a channel, request timers from other clients,
// and each responder's set is compared, boss by boss, with the timer we hold when
// it arrives (user, 2026-10-11: "if we hold an active timer on a boss, and we ask
// for a sync, if the timer they give us is different than what we hold it syncs, if
// we dont hold a timer, it merges in. if we ask in a channel where multiple people
// can or did respond, we do the same thing, compare all responses to the timer we
// hold, if there are conflicts, they all get listed in the menu and the user has to
// pick which timer they want in their data."):
//   - none held: merged in;
//   - the same: nothing;
//   - different: synced when it is the answer of the one player the user
//     telepathed, and listed for a pick when several can or did answer.
// From the one player asked that happens as the answer arrives. Where several can
// answer nothing is decided on the first arrival (user, 2026-10-11: "if we ask on
// gang, say, broadcast we can expect multiple replies... so in the case of
// multiple replies, for us holding no timer, ALL replies have to match each other
// otherwise open the picker, and for us holding a timer ALL replies have to match
// our timer otherwise open the picker"): the answers for a boss we hold no timer
// for are gathered until none has come for QuietPeriod, then merged in if they
// all agree and left listed if they do not. Against a timer we hold, an answer is
// decided as it comes, since neither outcome writes: the same is nothing, and one
// that differs is listed.
// All of it is for answers to a request the USER made. What answers a request the
// client sent for another player's remote command is listed, never written.
// (An earlier rule, 2026-10-11, made every answer on a shared channel a pick;
// superseded the same day. The rule that replaced it merged the first answer on a
// shared channel in as it arrived; refined the same day to wait for them all.)
public sealed partial class BossTimerSyncViewModel : ObservableObject, IDialogViewModel<bool>, IDisposable
{
    private readonly BossStore _bosses;
    private readonly BossTimerStore _timers;
    private readonly GameDataCache _gameData;
    private readonly Action<string> _send;
    private readonly BossTimerSyncCollector _collector;
    private readonly RealmType _realm;
    private readonly LogService _log;
    // The Talk tab's "Disallow @commands from broadcast".
    private readonly Func<bool> _broadcastOff;
    // Tells the app a merge window is open, so a hand-typed request opens no second.
    private readonly Action<bool> _windowActive;
    private readonly Action<Action> _onUiThread;
    private readonly Action<TimeSpan, Action> _armTimer;

    // How long no request and no line of any answer must have come before the
    // answers of a request several players can answer are judged together. A set
    // is sixty timers at most, a dozen or so lines: on a shared channel they are
    // written one after another, and by telepath TelepathPacer.Gap (100 ms) apart,
    // a refused one resent after its round trip. Ten seconds is many times the gap
    // between two lines of one set, and leaves room for a client that answers late.
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(10);

    // Each request and each line of an answer starts the quiet period again: only
    // the last timer armed judges, and none does once the window is gone.
    private int _settleGeneration;
    private bool _settlePending;
    private readonly Dictionary<string, BossTimerSyncRowViewModel> _rowsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _responders = new(StringComparer.OrdinalIgnoreCase);
    // True once an offer has been written to a timer store — merged in or synced as
    // it arrived, or an explicit Apply pick — so closing signals the Bosses tab to
    // refresh even when the user never clicks Apply.
    private bool _wroteAny;
    private bool _disposed;

    public event Action<bool>? CloseRequested;

    public ObservableCollection<BossTimerSyncRowViewModel> Rows { get; } = new();

    // Channel to send the request on: the four a timer sync may go by
    // (TransferChannels). Gang is the common case (whole gang syncs at once);
    // Telepath targets one player; Local hits everyone in the room; Broadcast the
    // players on the numbered channel you have joined.
    public IReadOnlyList<string> Channels { get; } =
        new[] { "Gang", "Telepath (to…)", "Local (say)", "Broadcast (br)" };
    private const int BroadcastChoice = 3;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TelepathTargetEnabled))]
    private int _channelIndex;
    [ObservableProperty] private string _telepathTarget = string.Empty;
    public bool TelepathTargetEnabled => ChannelIndex == 1;

    [ObservableProperty] private string _status = "Pick a channel and request timers.";
    [ObservableProperty] private bool _requested;

    // preArmedOn is set when the window is auto-opened because the user just sent
    // `@timer sync` from the terminal (rather than clicking Request here), and is the
    // channel that request went out on: we begin collecting its answers immediately
    // without re-sending it. preArmedTo is the player the game showed a telepathed
    // one reaching, when the telepath was the user's own; null when the client sent
    // it for another player (a relay-back, a `@do`), whose answer is then a pick.
    // preArmedByUser says which of the two it was, for the window to say.
    public BossTimerSyncViewModel(
        BossStore bosses, BossTimerStore timers, GameDataCache gameData, ChatRouter chat, Action<string> send,
        RemoteChannel? preArmedOn = null, string? preArmedTo = null, bool preArmedByUser = true)
        : this(bosses, timers, gameData, chat, send, preArmedOn,
            AppServices.Current.Log,
            () => AppServices.Current.RemoteCommands.DisableBroadcastChannel,
            AppServices.Current.RemoteCommands.IsSelfEcho,
            active => AppServices.Current.TimerSyncWindowActive = active,
            preArmedTo,
            preArmedByUser: preArmedByUser)
    {
    }

    // The same, with what the window takes from the running app handed in, so the
    // fold can be exercised without one.
    internal BossTimerSyncViewModel(
        BossStore bosses, BossTimerStore timers, GameDataCache gameData, ChatRouter chat, Action<string> send,
        RemoteChannel? preArmedOn, LogService log, Func<bool> broadcastOff, Func<string, bool> isOurName,
        Action<bool> windowActive, string? preArmedTo = null, Action<Action>? onUiThread = null,
        bool preArmedByUser = true, Action<TimeSpan, Action>? armTimer = null)
    {
        ArgumentNullException.ThrowIfNull(bosses);
        ArgumentNullException.ThrowIfNull(timers);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(send);
        _bosses = bosses;
        _timers = timers;
        _gameData = gameData;
        _send = send;
        _realm = gameData.ActiveRealm;
        _log = log;
        _broadcastOff = broadcastOff;
        _windowActive = windowActive;
        // ChatRouter events are already on the UI thread, but marshal defensively:
        // a fold touches the observable Rows collection.
        _onUiThread = onUiThread ?? (act =>
        {
            if (Dispatcher.UIThread.CheckAccess()) act();
            else Dispatcher.UIThread.Post(act);
        });

        // The quiet period runs on the UI thread like everything else here.
        _armTimer = armTimer ?? ((delay, act) => DispatcherTimer.RunOnce(act, delay));

        _collector = new BossTimerSyncCollector(chat, log, broadcastOff, isOurName);
        _collector.ResponseReceived += OnResponse;
        _collector.UserAsked += OnUserAsked;
        _collector.Activity += OnActivity;
        _timers.Changed += OnTimersChanged;
        _windowActive(true);   // suppress a duplicate auto-open

        // Rows are created on demand as responders offer a timer — a boss nobody sent a
        // timer for has nothing to merge, so it never clutters the table.

        if (preArmedOn is { } askedOn)
        {
            _collector.Begin(askedOn, preArmedTo, preArmedByUser);
            Requested = true;
            Status = !preArmedByUser
                ? "Collecting responses to a @timer sync this client sent for another player's remote command, not one you made — nothing is merged or synced: every timer is listed to pick, and written only by Apply Selected…"
                : "Collecting responses to your @timer sync — " + WhatArrivingTimersDo();
        }
    }

    // What becomes of the timers that answer the user's requests as they stand, for
    // the status line.
    private string WhatArrivingTimersDo() => _collector.OnePlayerAsked
        ? "from the player your telepath reached, a timer you had none for is merged in and one that differs from yours replaces it…"
        : $"replies are gathered until none has come for {QuietSeconds} seconds: a timer you had none for is merged in then if every reply for it agrees; where replies differ from each other or from yours, they are listed to pick…";

    private static string QuietSeconds => QuietPeriod.TotalSeconds.ToString("0");

    [RelayCommand]
    private void SendRequest()
    {
        // With the broadcast channel switched off on the Talk tab the answers would
        // not be read, so nothing is asked there. The choice stays in the list and
        // says what to do, as the telepath choice does with no name entered.
        if (ChannelIndex == BroadcastChoice && _broadcastOff())
        {
            Status = "Broadcast is switched off: untick \"Disallow @commands from broadcast\" on Settings → Talk, then request again.";
            return;
        }

        // A telepath with nobody named would go to nobody, and whoever answered
        // would be nobody we asked: nothing is sent.
        string target = TelepathTarget.Trim();
        if (ChannelIndex == 1 && target.Length == 0)
        {
            Status = "Enter a player name to telepath, then request again.";
            return;
        }
        // A telepath is addressed with one word. With two, the game would take the
        // first as the player and the rest as part of the message.
        if (ChannelIndex == 1 && target.Any(char.IsWhiteSpace))
        {
            Status = "A telepath goes to one name: enter the player's first name, with no spaces, then request again.";
            return;
        }

        const string request = "@timer sync";
        (RemoteChannel channel, string wire) = ChannelIndex switch
        {
            1 => (RemoteChannel.Telepath, $"/{target} {request}"),                  // telepath to a named player
            2 => (RemoteChannel.Local, $".{request}"),                              // local say
            BroadcastChoice => (RemoteChannel.Broadcast, $"br {request}"),          // the numbered channel's speak verb
            _ => (RemoteChannel.Gangpath, $"bg {request}"),                         // gang (default)
        };
        // The channel is listened on from now, as a request of the user's: this is
        // their button. Who a telepath reached is not known yet: the name typed may
        // be the first letters of a player's, and the game names the player when it
        // shows the telepath sent, which the collector reads. Only that player's
        // telepathed answer is then taken as an answer to it.
        _collector.Begin(channel);
        Requested = true;
        _send(wire);
        Status = channel == RemoteChannel.Telepath && !_collector.SeveralMayAnswer
            ? "Requested — once the game shows the telepath sent, that player's timers are compared with yours as they arrive: one you had none for is merged in; one that differs replaces yours when they are the only player asked, and is listed to pick otherwise…"
            : "Requested — " + WhatArrivingTimersDo();
    }

    // Test seam: the chat lines the window reads, fed without a chat router.
    internal BossTimerSyncCollector CollectorForTests => _collector;

    private void OnResponse(BossTimerSyncResponse response)
    {
        _onUiThread(() => Fold(response));
    }

    private void OnActivity() => _onUiThread(ArmSettle);

    // Our timers as they stood when the user first asked in this window, brought
    // up to date with what the window itself has merged in or synced since. A
    // timer that is no longer what this says changed after the user asked (a kill
    // of our own, a clear, an edit on the Bosses tab, a pick applied): an answer
    // was asked for against the old one and is never written over the new one as
    // it arrives. The boss is listed instead, like any pick. Null until the user
    // has asked. Not taken again at a later request: a kill made between two
    // requests stays ours to keep or to give up by a pick.
    private Dictionary<string, DateTimeOffset?>? _whenAsked;
    // The bosses whose timer was seen to change since then by anything but this
    // window's own merge or sync. Kept once seen: a kill that was then cleared
    // leaves us holding what we held when asked, and was still our doing.
    private readonly HashSet<string> _changedSinceAsked = new(StringComparer.OrdinalIgnoreCase);
    private bool _writingArrived;

    private void OnUserAsked()
    {
        if (_whenAsked is not null) return;
        _whenAsked = new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        foreach (BossDef boss in _bosses.ResolveForRealm(_realm))
            _whenAsked[boss.Name] = _timers.KilledAt(boss.Name);
    }

    private void OnTimersChanged()
    {
        if (_writingArrived || _whenAsked is null) return;
        foreach (BossDef boss in _bosses.ResolveForRealm(_realm))
            if (_timers.KilledAt(boss.Name) != HeldWhenAsked(boss.Name))
                _changedSinceAsked.Add(boss.Name);
    }

    // A boss added to the list since the user asked had no timer of ours then.
    private DateTimeOffset? HeldWhenAsked(string boss) =>
        _whenAsked is { } then && then.TryGetValue(boss, out DateTimeOffset? was) ? was : null;

    // The comparison with the store is for a change nothing announced (a timer
    // that ran out).
    private bool ChangedSinceAsked(string boss) =>
        _whenAsked is not null && (_changedSinceAsked.Contains(boss) || _timers.KilledAt(boss) != HeldWhenAsked(boss));

    // Write a timer that arrived, as the rule for arriving timers has it.
    private void WriteArrived(string boss, DateTimeOffset killedAt)
    {
        _writingArrived = true;
        try { _timers.MarkKilled(boss, killedAt); }
        finally { _writingArrived = false; }
        _wroteAny = true;
        if (_whenAsked is not null) _whenAsked[boss] = _timers.KilledAt(boss);
    }

    private const string ChangedSinceAskedNote = " · changed since you asked";

    private string OursText(string boss, DateTimeOffset? killedAt) =>
        (killedAt is { } k ? FormatKilled(k) : NoTimerText) + (ChangedSinceAsked(boss) ? ChangedSinceAskedNote : string.Empty);

    // Start the quiet period again, while a request of the user's is out that
    // several players can answer. With one player asked there is nothing to wait
    // for: that player's answer is decided as it arrives.
    private void ArmSettle()
    {
        if (_disposed || !_collector.SeveralMayAnswer) return;
        int generation = ++_settleGeneration;
        _settlePending = true;
        _armTimer(QuietPeriod, () =>
        {
            if (!_disposed && generation == _settleGeneration) Settle();
        });
    }

    // The replies have gone quiet: judge together what was gathered for each boss
    // we hold no timer for. All of them agree (co-kills seconds apart are the
    // same): the timer is merged in. They differ: the row stays as it is, every
    // reply listed, for the user to pick. A row we hold a timer for by now (a kill
    // of our own, a pick applied) keeps only the replies that differ from it.
    // A reply that comes after this starts the quiet period again and is judged
    // the same way with what is on its row then.
    private void Settle()
    {
        _settlePending = false;
        var mergedIn = new List<string>();
        var leftToPick = new List<string>();
        foreach (BossTimerSyncRowViewModel row in Rows)
        {
            IReadOnlyList<BossTimerSyncCellViewModel> gathered = row.Gathered();
            if (gathered.Count == 0) continue;
            Rebased(row);
            if (row.MatchName is not { } name) continue;
            if (row.OursKilledAt is null && AllAgree(gathered) && !ChangedSinceAsked(name))
            {
                DateTimeOffset agreed = gathered[0].KilledAt;
                WriteArrived(name, agreed);
                row.MarkAutoMerged(string.Join(", ", gathered.Select(c => c.Responder)), agreed, FormatKilled(agreed));
                mergedIn.Add(name);
            }
            row.DropCellsMatchingOurs(SameTimer);
            if (row.HasConflict) leftToPick.Add(name);
        }
        if (mergedIn.Count + leftToPick.Count > 0)
            _log.Info("BossTimerSync",
                $"replies settled ({QuietSeconds} s quiet): merged in {mergedIn.Count} boss(es) every reply agreed on" +
                (mergedIn.Count > 0 ? $" ({string.Join(", ", mergedIn)})" : string.Empty) +
                $"; {leftToPick.Count} left to pick" +
                (leftToPick.Count > 0 ? $" ({string.Join(", ", leftToPick)})" : string.Empty));
        if (_responders.Count > 0) UpdateStatus();
    }

    private static bool AllAgree(IReadOnlyList<BossTimerSyncCellViewModel> offers)
    {
        DateTimeOffset earliest = offers.Min(c => c.KilledAt), latest = offers.Max(c => c.KilledAt);
        return SameTimer(earliest, latest);
    }

    // The bosses whose gathered replies have not been judged yet.
    private List<string> AwaitingTheSettle() => !_settlePending
        ? new List<string>()
        : Rows.Where(r => r.OursKilledAt is null && r.Gathered().Count > 0).Select(r => r.BossName).ToList();

    // Take one responder's set into the table.
    internal void Fold(BossTimerSyncResponse r)
    {
        _responders.Add(r.Sender);
        int merged = 0, synced = 0, inSync = 0, conflict = 0, gathered = 0;
        foreach (BossTimerSyncRecord rec in r.Records)
            switch (IngestOffer(EnsureRow(rec.MonsterNumber, rec.Name), r.Sender, rec.KilledAt, r.Channel, r.Standing))
            {
                case TimerMergeKind.AutoMerge: merged++; break;
                case TimerMergeKind.Sync:      synced++; break;
                case TimerMergeKind.InSync:    inSync++; break;
                case TimerMergeKind.Gather:    gathered++; break;
                default:                       conflict++; break;
            }
        string standing = r.Standing switch
        {
            TimerSyncStanding.TheOneAsked => "the one player asked",
            TimerSyncStanding.OneOfSeveral => "one of several who may answer",
            _ => "no answer to a request of the user's",
        };
        _log.Info("BossTimerSync",
            $"folded {r.Sender}'s {r.Records.Count} timer(s) heard on {r.Channel} ({standing}): {merged} merged in, {synced} synced, {inSync} already in sync, {conflict} to pick, {gathered} gathered until the replies settle");
        UpdateStatus();
    }

    // Route one responder's timer for a boss row, returning what we did with it. It
    // is compared with the timer we hold now (the row may have been built before a
    // kill of our own, a timer merged in from earlier responders, or a timer
    // running out). A timer heard on the broadcast channel is marked as such where
    // it is listed.
    private TimerMergeKind IngestOffer(
        BossTimerSyncRowViewModel row, string sender, DateTimeOffset offer, RemoteChannel heardOn, TimerSyncStanding standing)
    {
        Rebased(row);
        TimerMergeKind kind = Classify(row.OursKilledAt, row.Tracked, offer, standing);
        // Ours changed after the user asked: nothing is written over it, or in its
        // place, as an answer arrives. What differs from it is listed.
        if (kind is TimerMergeKind.AutoMerge or TimerMergeKind.Sync or TimerMergeKind.Gather
            && ChangedSinceAsked(row.MatchName!))
            kind = TimerMergeKind.Conflict;
        string listedAs = heardOn == RemoteChannel.Broadcast ? $"{FormatKilled(offer)} · heard on broadcast" : FormatKilled(offer);
        switch (kind)
        {
            case TimerMergeKind.Gather:
                // Shown with the others as it comes, and pickable; written only if
                // every reply for the boss agrees once they have settled.
                row.AddConflict(sender, offer, listedAs, gathered: true);
                break;
            case TimerMergeKind.AutoMerge:
                WriteArrived(row.MatchName!, offer);   // only fires for tracked bosses
                row.MarkAutoMerged(sender, offer, FormatKilled(offer));
                break;
            case TimerMergeKind.Sync:
                WriteArrived(row.MatchName!, offer);   // only fires for tracked bosses
                row.MarkSynced(sender, offer, FormatKilled(offer));
                break;
            case TimerMergeKind.InSync:
                row.MarkInSync(sender);
                break;
            default:
                row.AddConflict(sender, offer, listedAs);
                break;
        }
        return kind;
    }

    // AutoMerge: we held none, theirs is merged in. Sync: ours differed and is
    // replaced by theirs. InSync: the same timer. Conflict: listed for a pick.
    // Gather: we held none and several can answer; listed with the other replies
    // until they have settled, then merged in if all of them agree.
    internal enum TimerMergeKind { AutoMerge, InSync, Conflict, Sync, Gather }

    // Untracked bosses are always a manual pick (adopting one adds it back to your list); a
    // tracked boss with no timer auto-merges; a held timer that agrees with the offer (co-
    // kills land seconds apart, so within-a-minute counts as the same event) is in sync; a
    // held timer that disagrees is a conflict.
    internal static TimerMergeKind Classify(DateTimeOffset? ours, bool tracked, DateTimeOffset offer)
    {
        if (!tracked) return TimerMergeKind.Conflict;
        if (ours is null) return TimerMergeKind.AutoMerge;
        return SameTimer(ours.Value, offer) ? TimerMergeKind.InSync : TimerMergeKind.Conflict;
    }

    // The same, for what the offer is to the requests the user made.
    //   - No answer to a request of the user's: never written. What differs from
    //     ours, and what we hold none for, is listed.
    //   - The answer of the one player the user telepathed: a timer we hold none
    //     for is merged in, and one that differs from ours replaces it.
    //   - One of several that can or did answer: the first to arrive is no better
    //     than the rest. One that differs from ours is listed; one for a boss we
    //     hold no timer for is gathered with the others (Settle).
    internal static TimerMergeKind Classify(
        DateTimeOffset? ours, bool tracked, DateTimeOffset offer, TimerSyncStanding standing)
    {
        TimerMergeKind kind = Classify(ours, tracked, offer);
        if (!tracked || kind == TimerMergeKind.InSync) return kind;
        return standing switch
        {
            TimerSyncStanding.NotAsked => TimerMergeKind.Conflict,
            TimerSyncStanding.TheOneAsked when kind == TimerMergeKind.Conflict => TimerMergeKind.Sync,
            TimerSyncStanding.OneOfSeveral when kind == TimerMergeKind.AutoMerge => TimerMergeKind.Gather,
            _ => kind,
        };
    }

    // Bring a row's "ours" up to what the timer store holds now, when that is no
    // longer what the row was built against. True when it had moved. A row built
    // for a boss that was not on our list is looked up afresh first: the boss may
    // be on it by now, added on the Bosses tab or by an earlier Apply, and a timer
    // of ours for it counts like any other.
    private bool Rebased(BossTimerSyncRowViewModel row)
    {
        if (row.MatchName is null && ResolveOurBoss(row.MonsterNumber, row.SentName) is { } ours)
            row.Track(ours.Name, NoTimerText);
        if (row.MatchName is not { } name) return false;
        DateTimeOffset? now = _timers.KilledAt(name);
        if (now == row.OursKilledAt) return false;
        row.Rebase(now, OursText(name, now));
        return true;
    }

    private static string TrackedKey(BossDef ours) => "b:" + ours.Name.ToLowerInvariant();

    private const string NoTimerText = "— no timer —";

    // A different kill of the same boss is at least a respawn interval (hours) away, so a
    // one-minute window never conflates two respawns — it only absorbs the few-second
    // spread between party members who marked the same kill and the codec's second rounding.
    private const double SameTimerToleranceSeconds = 60;
    internal static bool SameTimer(DateTimeOffset a, DateTimeOffset b)
        => Math.Abs((a - b).TotalSeconds) <= SameTimerToleranceSeconds;

    private void UpdateStatus()
    {
        int waiting = AwaitingTheSettle().Count;
        int conflicts = Rows.Count(r => r.HasConflict) - waiting;
        // Every row a timer was written to, whatever picks it also holds.
        int merged = Rows.Count(r => r.WasAutoMerged);
        int synced = Rows.Count(r => r.WasSynced);
        int inSync = Rows.Count(r => !r.HasConflict && !r.WasAutoMerged && !r.WasSynced && r.ResolvedStatus.Length > 0);
        string counts = $"{conflicts} to pick, {merged} merged in, {synced} synced, {inSync} already in sync";
        Status = _settlePending
            ? $"{_responders.Count} responder(s) so far, replies still being gathered: {waiting} waiting for every reply, {counts}. " +
              $"A timer you hold none for is merged in once no reply has come for {QuietSeconds} seconds and every reply for it agrees; " +
              "until then nothing is written for it unless you pick and Apply Selected, and closing the window writes nothing more."
            : $"{_responders.Count} responder(s): {counts}.";
    }

    // Find our boss for an incoming identity (by MDB number first, else name) so the
    // row shows a real name and — when we track it — an apply target. A responder that
    // sent only a number for a boss we don't track shows as "Monster #N".
    private BossTimerSyncRowViewModel EnsureRow(int? number, string? name)
    {
        BossDef? ours = ResolveOurBoss(number, name);
        string untrackedKey = number is { } n ? "#" + n : "n:" + (name ?? "?").ToLowerInvariant();
        string key = ours is not null ? TrackedKey(ours) : untrackedKey;

        if (_rowsByKey.TryGetValue(key, out BossTimerSyncRowViewModel? existing)) return existing;
        // The row built for this boss while it was not on our list is still its
        // row now that it is (IngestOffer brings it up to date).
        if (ours is not null && _rowsByKey.TryGetValue(untrackedKey, out BossTimerSyncRowViewModel? builtUntracked))
            return builtUntracked;

        string display = ours?.Name ?? (name is { Length: > 0 } ? name : number is { } num ? $"Monster #{num}" : "(unknown)");
        DateTimeOffset? oursKilled = ours is not null ? _timers.KilledAt(ours.Name) : null;
        string oursText = ours is not null ? OursText(ours.Name, oursKilled) : "— not in your list —";

        BossTimerSyncRowViewModel row = new(display, number, ours?.Name, name, oursKilled, oursText);
        _rowsByKey[key] = row;
        Rows.Add(row);
        return row;
    }

    private BossDef? ResolveOurBoss(int? number, string? name)
    {
        IReadOnlyList<BossDef> all = _bosses.ResolveForRealm(_realm);
        if (number is { } n)
        {
            BossDef? byNum = all.FirstOrDefault(b => b.MonsterNumber == n);
            if (byNum is not null) return byNum;
        }
        if (name is { Length: > 0 })
            return all.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private static string FormatKilled(DateTimeOffset at)
    {
        double agoHours = Math.Max(0, (DateTimeOffset.UtcNow - at).TotalHours);
        return $"{at.ToLocalTime():MMM d HH:mm} ({BossTimerMath.FormatHours(agoHours)} ago)";
    }

    // Write the picks. A pick was made against the timer the row showed as ours; if
    // ours has changed since (we killed the boss while the window was open, or its
    // timer ran out), the pick is not written: an older time offered would go over
    // a kill of our own. That row is shown against our timer as it is now, the
    // window stays open, and the user picks again or closes.
    [RelayCommand]
    private void Apply()
    {
        int applied = 0;
        var moved = new List<string>();
        foreach (BossTimerSyncRowViewModel row in Rows)
        {
            if (row.SelectedKilledAt is not { } chosen) continue;
            // Every kind of row: one built for a boss off our list is looked up as
            // the list is now, and held to the same check.
            if (Rebased(row)) { moved.Add(row.BossName); continue; }
            if (row.MatchName is { } target)
            {
                if (row.OursKilledAt == chosen) continue;   // no-op
                _timers.MarkKilled(target, chosen);
            }
            else if (AdoptUntracked(row, chosen) is { } adoptedAs)   // a boss we weren't tracking
            {
                row.Track(adoptedAs, NoTimerText);
            }
            else
            {
                continue;
            }
            // Written: the row now shows that timer as ours and its pick is spent,
            // so a second Apply writes it again for nobody.
            row.Rebase(chosen, FormatKilled(chosen));
            applied++;
        }
        if (applied > 0) _wroteAny = true;
        if (moved.Count > 0)
        {
            _log.Info("BossTimerSync",
                $"applied {applied} pick(s); {moved.Count} not written, our own timer having changed since the pick was offered: {string.Join(", ", moved)}");
            Status = $"Applied {applied} timer update(s). Not written: {string.Join(", ", moved)} — your own timer changed while this window was open. " +
                "Each is now shown against your timer as it is: pick again, or Close.";
            return;
        }
        Status = $"Applied {applied} timer update(s).";
        CloseRequested?.Invoke(_wroteAny);   // auto-merges may have written even if no picks
    }

    // Adopt a timer for a boss not currently in our list: recover its real def from the
    // catalog (a seed boss we'd removed, or an overlay one) — or, failing that, synth a
    // minimal def from the sent name — un-hide it on the active realm, then stamp the
    // timer. A number-only identity we can't resolve to any catalog boss is skipped.
    // Returns the name the boss is now on our list under, or null when skipped.
    private string? AdoptUntracked(BossTimerSyncRowViewModel row, DateTimeOffset chosen)
    {
        BossDef? def = _bosses.FindInCatalog(row.MonsterNumber, row.SentName);
        if (def is null)
        {
            if (row.SentName is not { Length: > 0 } sent) return null;
            def = new BossDef { Name = sent, MonsterNumber = row.MonsterNumber };
        }
        if (_realm == RealmType.ParaMud) def.InParadigm = true; else def.InStock = true;

        List<BossDef> list = _bosses.Resolve()
            .Where(b => !string.Equals(b.Name, def.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        list.Add(def);
        _bosses.Save(list);
        _timers.MarkKilled(def.Name, chosen);
        return def.Name;
    }

    // Closing without applying still keeps any auto-merges already written, so tell the
    // Bosses tab to refresh if one landed.
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(_wroteAny);

    // Closing abandons a gather that has not settled: the timer still pending judges
    // nothing once the window is gone, so what was gathered and not picked is not
    // written. The window is closing and has nowhere left to say so but the log.
    public void Dispose()
    {
        if (_disposed) return;
        if (AwaitingTheSettle() is { Count: > 0 } abandoned)
            _log.Info("BossTimerSync",
                $"closed while replies were still being gathered: nothing was merged in for {abandoned.Count} boss(es) whose replies had not settled ({string.Join(", ", abandoned)})");
        _disposed = true;
        _collector.ResponseReceived -= OnResponse;
        _collector.UserAsked -= OnUserAsked;
        _collector.Activity -= OnActivity;
        _timers.Changed -= OnTimersChanged;
        _collector.Dispose();
        _windowActive(false);
    }
}

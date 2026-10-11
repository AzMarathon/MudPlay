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
// and as each responder's set decodes, its timers fill in as pickable options next to
// your own. A timer you hold is never overwritten without a pick, and a request that
// several players could answer (gang, say, broadcast) writes nothing at all until
// Apply: the user chooses which of the timers received to take in (user, 2026-10-11).
// The one thing written as it arrives is a timer we had none for, from the player a
// telepath asked, while no request has gone where others listen.
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
    // True once a timer heard on the broadcast channel was offered as a pick.
    private bool _offeredFromBroadcast;
    private readonly Dictionary<string, BossTimerSyncRowViewModel> _rowsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _responders = new(StringComparer.OrdinalIgnoreCase);
    // True once an offer has been written to a timer store — via an auto-merge as it
    // arrived or an explicit Apply pick — so closing signals the Bosses tab to refresh
    // even when the user never clicks Apply.
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
    public BossTimerSyncViewModel(
        BossStore bosses, BossTimerStore timers, GameDataCache gameData, ChatRouter chat, Action<string> send,
        RemoteChannel? preArmedOn = null, string? preArmedTo = null)
        : this(bosses, timers, gameData, chat, send, preArmedOn,
            AppServices.Current.Log,
            () => AppServices.Current.RemoteCommands.DisableBroadcastChannel,
            AppServices.Current.RemoteCommands.IsSelfEcho,
            active => AppServices.Current.TimerSyncWindowActive = active,
            preArmedTo,
            telepathWasUsers: AppServices.Current.TimerSyncOrigin.MadeByUser)
    {
    }

    // The same, with what the window takes from the running app handed in, so the
    // fold can be exercised without one.
    internal BossTimerSyncViewModel(
        BossStore bosses, BossTimerStore timers, GameDataCache gameData, ChatRouter chat, Action<string> send,
        RemoteChannel? preArmedOn, LogService log, Func<bool> broadcastOff, Func<string, bool> isOurName,
        Action<bool> windowActive, string? preArmedTo = null, Action<Action>? onUiThread = null,
        Func<ChatLogEntry, bool>? telepathWasUsers = null)
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

        _collector = new BossTimerSyncCollector(chat, log, broadcastOff, isOurName, telepathWasUsers);
        _collector.ResponseReceived += OnResponse;
        _windowActive(true);   // suppress a duplicate auto-open

        // Rows are created on demand as responders offer a timer — a boss nobody sent a
        // timer for has nothing to merge, so it never clutters the table.

        if (preArmedOn is { } askedOn)
        {
            _collector.Begin(askedOn, preArmedTo);
            Requested = true;
            Status = _collector.OnlyTelepathsAsked
                ? "Collecting responses to your @timer sync — a timer you had none for is taken from the player you asked; you only pick on a conflict…"
                : "Collecting responses to your @timer sync — pick the timers to take in, then Apply Selected; nothing is written before that…";
        }
    }

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
        // The channel is listened on from now. Who a telepath reached is not known
        // yet: the name typed may be the first letters of a player's, and the game
        // names the player when it shows the telepath sent, which the collector
        // reads. Only that player's answer is then adopted.
        _collector.Begin(channel);
        Requested = true;
        _send(wire);
        Status = _collector.OnlyTelepathsAsked
            ? "Requested — once the game shows the telepath sent, a timer you had none for is taken from that player as it arrives…"
            : "Requested — pick the timers to take in as they arrive, then Apply Selected; nothing is written before that…";
    }

    // Test seam: the chat lines the window reads, fed without a chat router.
    internal BossTimerSyncCollector CollectorForTests => _collector;

    private void OnResponse(BossTimerSyncResponse response)
    {
        _onUiThread(() => Fold(response));
    }

    // Take one responder's set into the table.
    internal void Fold(BossTimerSyncResponse r)
    {
        _responders.Add(r.Sender);
        int adopted = 0, inSync = 0, conflict = 0;
        foreach (BossTimerSyncRecord rec in r.Records)
            switch (IngestOffer(EnsureRow(rec.MonsterNumber, rec.Name), r.Sender, rec.KilledAt, r.Channel, r.AskedOfSenderAlone))
            {
                case TimerMergeKind.AutoMerge: adopted++; break;
                case TimerMergeKind.InSync:    inSync++;  break;
                default:                       conflict++; break;
            }
        _log.Info("BossTimerSync",
            $"folded {r.Sender}'s {r.Records.Count} timer(s) heard on {r.Channel} ({(r.AskedOfSenderAlone ? "the one player asked" : "one of several who may answer")}): {adopted} adopted, {inSync} already in sync, {conflict} to pick");
        UpdateStatus();
    }

    // Route one responder's timer for a boss row, returning what we did with it. One
    // that matches what we hold is a no-op. A timer for a boss we track but hold no
    // timer for is adopted outright only as the answer of the one player asked, by
    // telepath; from anyone else, or once a request has gone where several players
    // could hear it, it is a pick like a genuine disagreement or a boss we don't
    // track yet (whose adoption adds it to our list). A timer heard on the
    // broadcast channel is marked as such.
    private TimerMergeKind IngestOffer(
        BossTimerSyncRowViewModel row, string sender, DateTimeOffset offer, RemoteChannel heardOn, bool askedOfSenderAlone)
    {
        // The row may have been built before a kill of our own, or a timer running
        // out: the offer is weighed against what we hold now.
        Rebased(row);
        TimerMergeKind kind = Classify(row.OursKilledAt, row.Tracked, offer, askedOfSenderAlone);
        switch (kind)
        {
            case TimerMergeKind.AutoMerge:
                _timers.MarkKilled(row.MatchName!, offer);   // AutoMerge only fires for tracked bosses
                _wroteAny = true;
                row.MarkAutoMerged(sender, offer, FormatKilled(offer));
                break;
            case TimerMergeKind.InSync:
                row.MarkInSync(sender);
                break;
            default:
                bool fromBroadcast = heardOn == RemoteChannel.Broadcast;
                if (fromBroadcast) _offeredFromBroadcast = true;
                row.AddConflict(sender, offer,
                    fromBroadcast ? $"{FormatKilled(offer)} · heard on broadcast" : FormatKilled(offer));
                break;
        }
        return kind;
    }

    internal enum TimerMergeKind { AutoMerge, InSync, Conflict }

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

    // The same, for who the offer is from. Asked of the gang, the room or the
    // broadcast channel, several players may answer with different timers for one
    // boss, and the first to arrive is no better than the rest: nothing is written
    // without a pick (user, 2026-10-11). What is adopted outright is the answer of
    // the one player a telepath asked ("if the timer they asked for, was missing on
    // their end, just adopt it"), and from anyone else it is offered as a choice.
    internal static TimerMergeKind Classify(
        DateTimeOffset? ours, bool tracked, DateTimeOffset offer, bool askedOfSenderAlone)
    {
        TimerMergeKind kind = Classify(ours, tracked, offer);
        return kind == TimerMergeKind.AutoMerge && !askedOfSenderAlone
            ? TimerMergeKind.Conflict
            : kind;
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
        row.Rebase(now, now is { } k ? FormatKilled(k) : NoTimerText);
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
        int conflicts = Rows.Count(r => r.HasConflict);
        // Every row a timer was written to on arrival, whatever picks it also holds.
        int adopted = Rows.Count(r => r.WasAutoMerged);
        int inSync = Rows.Count(r => !r.HasConflict && !r.WasAutoMerged && r.ResolvedStatus.Length > 0);
        Status = $"{_responders.Count} responder(s): {conflicts} to pick, {adopted} adopted, {inSync} already in sync."
            + (_offeredFromBroadcast
                ? " Timers heard on broadcast are never adopted without a pick: anyone can join that channel."
                : string.Empty);
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
        string oursText = oursKilled is { } k ? FormatKilled(k)
            : ours is not null ? NoTimerText : "— not in your list —";

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _collector.ResponseReceived -= OnResponse;
        _collector.Dispose();
        _windowActive(false);
    }
}

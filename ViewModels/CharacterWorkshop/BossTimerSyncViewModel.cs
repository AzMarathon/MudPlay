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
// your own. Folding is always manual — Apply writes only the rows you chose, so a bad
// or stale timer from someone else can't silently overwrite yours.
public sealed partial class BossTimerSyncViewModel : ObservableObject, IDialogViewModel<bool>, IDisposable
{
    private readonly BossStore _bosses;
    private readonly BossTimerStore _timers;
    private readonly GameDataCache _gameData;
    private readonly Action<string> _send;
    private readonly BossTimerSyncCollector _collector;
    private readonly RealmType _realm;
    private readonly Dictionary<string, BossTimerSyncRowViewModel> _rowsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _responders = new(StringComparer.OrdinalIgnoreCase);
    // True once an offer has been written to a timer store — via an auto-merge as it
    // arrived or an explicit Apply pick — so closing signals the Bosses tab to refresh
    // even when the user never clicks Apply.
    private bool _wroteAny;
    private bool _disposed;

    public event Action<bool>? CloseRequested;

    public ObservableCollection<BossTimerSyncRowViewModel> Rows { get; } = new();

    // Channel to broadcast the request on. Gang is the common case (whole gang syncs
    // at once); Telepath targets one player; Local hits everyone in the room.
    public IReadOnlyList<string> Channels { get; } = new[] { "Gang", "Telepath (to…)", "Local (say)" };
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TelepathTargetEnabled))]
    private int _channelIndex;
    [ObservableProperty] private string _telepathTarget = string.Empty;
    public bool TelepathTargetEnabled => ChannelIndex == 1;

    [ObservableProperty] private string _status = "Pick a channel and request timers.";
    [ObservableProperty] private bool _requested;

    // preArmed is set when the window is auto-opened because the user just sent `@timer
    // sync` from the terminal (rather than clicking Request here) — we begin collecting
    // immediately without re-sending the request.
    public BossTimerSyncViewModel(
        BossStore bosses, BossTimerStore timers, GameDataCache gameData, ChatRouter chat, Action<string> send,
        bool preArmed = false)
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

        _collector = new BossTimerSyncCollector(chat);
        _collector.ResponseReceived += OnResponse;
        AppServices.Current.TimerSyncWindowActive = true;   // suppress a duplicate auto-open

        // Rows are created on demand as responders offer a timer — a boss nobody sent a
        // timer for has nothing to merge, so it never clutters the table.

        if (preArmed)
        {
            _collector.Begin();
            Requested = true;
            Status = "Collecting responses to your @timer sync — new timers fold in automatically; you only pick on a conflict…";
        }
    }

    [RelayCommand]
    private void SendRequest()
    {
        _collector.Begin();
        Requested = true;

        const string request = "@timer sync";
        string wire = ChannelIndex switch
        {
            1 => $"/{TelepathTarget.Trim()} {request}",   // telepath to a named player
            2 => $".{request}",                            // local say
            _ => $"bg {request}",                          // gang (default)
        };
        _send(wire);
        Status = ChannelIndex == 1 && TelepathTarget.Trim().Length == 0
            ? "Enter a player name to telepath, then request again."
            : "Requested — responders will fill in as their timers arrive…";
    }

    private void OnResponse(BossTimerSyncResponse response)
    {
        // ChatRouter events are already on the UI thread, but marshal defensively —
        // this touches the observable Rows collection.
        if (Dispatcher.UIThread.CheckAccess()) Handle(response);
        else Dispatcher.UIThread.Post(() => Handle(response));

        void Handle(BossTimerSyncResponse r)
        {
            _responders.Add(r.Sender);
            foreach (BossTimerSyncRecord rec in r.Records)
                IngestOffer(EnsureRow(rec.MonsterNumber, rec.Name), r.Sender, rec.KilledAt);
            UpdateStatus();
        }
    }

    // Route one responder's timer for a boss row. A timer for a boss we track but hold no
    // timer for is adopted outright (nothing of ours to overwrite); one that matches what
    // we hold is a no-op; only a genuine disagreement — or a boss we don't track yet, whose
    // adoption adds it to our list — becomes a pickable conflict the user resolves manually.
    private void IngestOffer(BossTimerSyncRowViewModel row, string sender, DateTimeOffset offer)
    {
        switch (Classify(row.OursKilledAt, row.Tracked, offer))
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
                row.AddConflict(sender, offer, FormatKilled(offer));
                break;
        }
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

    // A different kill of the same boss is at least a respawn interval (hours) away, so a
    // one-minute window never conflates two respawns — it only absorbs the few-second
    // spread between party members who marked the same kill and the codec's second rounding.
    private const double SameTimerToleranceSeconds = 60;
    internal static bool SameTimer(DateTimeOffset a, DateTimeOffset b)
        => Math.Abs((a - b).TotalSeconds) <= SameTimerToleranceSeconds;

    private void UpdateStatus()
    {
        int conflicts = Rows.Count(r => r.HasConflict);
        int adopted = Rows.Count(r => !r.HasConflict && r.WasAutoMerged);
        int inSync = Rows.Count(r => !r.HasConflict && !r.WasAutoMerged && r.ResolvedStatus.Length > 0);
        Status = $"{_responders.Count} responder(s): {conflicts} to resolve, {adopted} adopted, {inSync} already in sync.";
    }

    // Find our boss for an incoming identity (by MDB number first, else name) so the
    // row shows a real name and — when we track it — an apply target. A responder that
    // sent only a number for a boss we don't track shows as "Monster #N".
    private BossTimerSyncRowViewModel EnsureRow(int? number, string? name)
    {
        BossDef? ours = ResolveOurBoss(number, name);
        string key = ours is not null
            ? "b:" + ours.Name.ToLowerInvariant()
            : number is { } n ? "#" + n : "n:" + (name ?? "?").ToLowerInvariant();

        if (_rowsByKey.TryGetValue(key, out BossTimerSyncRowViewModel? existing)) return existing;

        string display = ours?.Name ?? (name is { Length: > 0 } ? name : number is { } num ? $"Monster #{num}" : "(unknown)");
        DateTimeOffset? oursKilled = ours is not null ? _timers.KilledAt(ours.Name) : null;
        string oursText = oursKilled is { } k ? FormatKilled(k)
            : ours is not null ? "— no timer —" : "— not in your list —";

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

    [RelayCommand]
    private void Apply()
    {
        int applied = 0;
        foreach (BossTimerSyncRowViewModel row in Rows)
        {
            if (row.SelectedKilledAt is not { } chosen) continue;
            if (row.MatchName is { } target)
            {
                if (row.OursKilledAt == chosen) continue;   // no-op
                _timers.MarkKilled(target, chosen);
                applied++;
            }
            else if (AdoptUntracked(row, chosen))   // a boss we weren't tracking
            {
                applied++;
            }
        }
        if (applied > 0) _wroteAny = true;
        Status = $"Applied {applied} timer update(s).";
        CloseRequested?.Invoke(_wroteAny);   // auto-merges may have written even if no picks
    }

    // Adopt a timer for a boss not currently in our list: recover its real def from the
    // catalog (a seed boss we'd removed, or an overlay one) — or, failing that, synth a
    // minimal def from the sent name — un-hide it on the active realm, then stamp the
    // timer. A number-only identity we can't resolve to any catalog boss is skipped.
    private bool AdoptUntracked(BossTimerSyncRowViewModel row, DateTimeOffset chosen)
    {
        BossDef? def = _bosses.FindInCatalog(row.MonsterNumber, row.SentName);
        if (def is null)
        {
            if (row.SentName is not { Length: > 0 } sent) return false;
            def = new BossDef { Name = sent, MonsterNumber = row.MonsterNumber };
        }
        if (_realm == RealmType.ParaMud) def.InParadigm = true; else def.InStock = true;

        List<BossDef> list = _bosses.Resolve()
            .Where(b => !string.Equals(b.Name, def.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        list.Add(def);
        _bosses.Save(list);
        _timers.MarkKilled(def.Name, chosen);
        return true;
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
        AppServices.Current.TimerSyncWindowActive = false;
    }
}

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

    public BossTimerSyncViewModel(
        BossStore bosses, BossTimerStore timers, GameDataCache gameData, ChatRouter chat, Action<string> send)
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

        // Seed the table with our own active timers so there's something to compare
        // against the moment responses arrive.
        foreach ((BossDef def, _) in _timers.ActiveTimers(_realm))
            EnsureRow(def.MonsterNumber, def.Name);
    }

    [RelayCommand]
    private void SendRequest()
    {
        string token = Guid.NewGuid().ToString("N")[..4];
        _collector.Begin(token);
        Requested = true;

        string request = $"@timer sync {token}";
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
        if (Dispatcher.UIThread.CheckAccess()) Apply(response);
        else Dispatcher.UIThread.Post(() => Apply(response));

        void Apply(BossTimerSyncResponse r)
        {
            _responders.Add(r.Sender);
            foreach (BossTimerSyncRecord rec in r.Records)
            {
                BossTimerSyncRowViewModel row = EnsureRow(rec.MonsterNumber, rec.Name);
                // One option per responder per boss; a re-send from the same sender
                // replaces (drop any prior cell of theirs on this row).
                foreach (BossTimerSyncCellViewModel dup in
                    row.Responders.Where(c => string.Equals(c.Responder, r.Sender, StringComparison.OrdinalIgnoreCase)).ToList())
                    row.Responders.Remove(dup);
                row.AddResponder(r.Sender, rec.KilledAt, FormatKilled(rec.KilledAt));
            }
            Status = $"{_responders.Count} responder(s), {Rows.Count(x => x.Responders.Count > 0)} boss(es) offered.";
        }
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

        BossTimerSyncRowViewModel row = new(display, number, ours?.Name, oursKilled, oursText);
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
            if (row.SelectedKilledAt is not { } chosen || row.MatchName is not { } target) continue;
            if (row.OursKilledAt == chosen) continue;   // no-op
            _timers.MarkKilled(target, chosen);
            applied++;
        }
        Status = $"Applied {applied} timer update(s).";
        CloseRequested?.Invoke(applied > 0);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _collector.ResponseReceived -= OnResponse;
        _collector.Dispose();
    }
}

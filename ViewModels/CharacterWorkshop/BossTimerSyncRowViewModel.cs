using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One boss in the sync merge table. A row is either RESOLVED — its offered timer was
// auto-adopted (we held none) or already matched what we hold — in which case it shows a
// muted status line and no buttons; or a CONFLICT — a responder's timer differs from
// ours, or the boss isn't in our list yet — in which case it shows the "keep ours"
// default plus a pick button per differing responder. Only conflict rows need a decision;
// SelectedKilledAt is what the Apply button writes for them (null = keep ours).
public sealed partial class BossTimerSyncRowViewModel : ObservableObject
{
    public string BossName { get; }
    public int? MonsterNumber { get; }
    public string? MatchName { get; }        // our BossStore name (null = untracked by us)
    // The name a responder sent (may be null for a number-only record) — the adoption
    // fallback when the identity isn't in our catalog by number.
    public string? SentName { get; }
    public bool Tracked => MatchName is not null;

    // Our effective timer for this boss: the held value at row creation, advanced when we
    // auto-adopt a sent timer we had none for. Drives the "keep ours" option shown on a
    // conflict row and the in-sync comparison.
    public DateTimeOffset? OursKilledAt { get; private set; }
    [ObservableProperty] private string _oursText;

    // A conflict row shows the picker; a resolved row (auto-adopted / already in sync)
    // shows ResolvedStatus instead. WasAutoMerged records that we wrote a timer we had
    // none for, so the parent can count adoptions separately from in-sync no-ops.
    [ObservableProperty] private bool _hasConflict;
    [ObservableProperty] private bool _wasAutoMerged;
    [ObservableProperty] private string _resolvedStatus = string.Empty;

    private readonly List<string> _inSyncWith = new();

    // "keep ours" is the safe default so nothing changes unless the user opts in.
    [ObservableProperty] private bool _keepOursSelected = true;

    public ObservableCollection<BossTimerSyncCellViewModel> Responders { get; } = new();

    // The killed-at that Apply will fold in for a conflict row, or null to leave ours.
    public DateTimeOffset? SelectedKilledAt { get; private set; }

    public BossTimerSyncRowViewModel(
        string bossName, int? monsterNumber, string? matchName, string? sentName,
        DateTimeOffset? oursKilledAt, string oursText)
    {
        BossName = bossName;
        MonsterNumber = monsterNumber;
        MatchName = matchName;
        SentName = sentName;
        OursKilledAt = oursKilledAt;
        _oursText = oursText;
        SelectedKilledAt = null;   // keep ours
    }

    // A differing offer (a real conflict against a timer we hold, or an untracked-boss
    // offer we must opt into) becomes a pickable option. A re-offer from the same
    // responder replaces their prior cell rather than stacking a duplicate.
    public void AddConflict(string responder, DateTimeOffset killedAt, string text)
    {
        for (int i = Responders.Count - 1; i >= 0; i--)
            if (string.Equals(Responders[i].Responder, responder, StringComparison.OrdinalIgnoreCase))
                Responders.RemoveAt(i);
        Responders.Add(new BossTimerSyncCellViewModel(responder, killedAt, text, Adopt));
        HasConflict = true;
        ResolvedStatus = string.Empty;
    }

    // We held no timer for this boss and adopted the offer outright — the store write
    // already happened; this just reflects it and advances our effective timer so a later
    // differing offer is measured against what we now hold.
    public void MarkAutoMerged(string responder, DateTimeOffset killedAt, string text)
    {
        OursKilledAt = killedAt;
        OursText = text;
        WasAutoMerged = true;
        if (!HasConflict) ResolvedStatus = $"Adopted {responder}'s timer — {text}";
    }

    // Their offer matched what we already hold — nothing to do.
    public void MarkInSync(string responder)
    {
        if (!_inSyncWith.Contains(responder)) _inSyncWith.Add(responder);
        if (!HasConflict && !WasAutoMerged)
            ResolvedStatus = $"Already in sync with {string.Join(", ", _inSyncWith)}";
    }

    [RelayCommand]
    private void KeepOurs()
    {
        SelectedKilledAt = null;
        KeepOursSelected = true;
        foreach (BossTimerSyncCellViewModel c in Responders) c.IsSelected = false;
    }

    private void Adopt(BossTimerSyncCellViewModel chosen)
    {
        SelectedKilledAt = chosen.KilledAt;
        KeepOursSelected = false;
        foreach (BossTimerSyncCellViewModel c in Responders) c.IsSelected = ReferenceEquals(c, chosen);
    }
}

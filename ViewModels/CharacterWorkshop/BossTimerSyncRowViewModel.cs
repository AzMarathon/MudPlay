using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One boss in the sync merge table. A row is either RESOLVED — its offered timer was
// merged in (we held none), synced (ours differed, and it is the answer of the one
// player asked) or already matched what we hold — in which case it shows a muted
// status line and no buttons; or a CONFLICT — a responder's timer differs from ours
// where several can answer, the boss isn't in our list yet, the offer answers no
// request of the user's, or we hold no timer and the answers of several are still
// being gathered or did not agree, or the one player's answer is held back while
// another telepath request waits to be shown sent — in which case it shows the "keep ours" default
// plus a pick button per responder listed. Only conflict rows need a decision;
// SelectedKilledAt is what the Apply button writes for them (null = keep ours).
public sealed partial class BossTimerSyncRowViewModel : ObservableObject
{
    // The boss as our list names it, or as the responder did while it is not on it.
    [ObservableProperty] private string _bossName;
    public int? MonsterNumber { get; }
    public string? MatchName { get; private set; }   // our BossStore name (null = untracked by us)
    // The name a responder sent (may be null for a number-only record) — the adoption
    // fallback when the identity isn't in our catalog by number.
    public string? SentName { get; }
    public bool Tracked => MatchName is not null;

    // Our effective timer for this boss: the held value at row creation, advanced when we
    // auto-adopt a sent timer we had none for. Drives the "keep ours" option shown on a
    // conflict row and the in-sync comparison.
    public DateTimeOffset? OursKilledAt { get; private set; }
    [ObservableProperty] private string _oursText;

    // The default pick's heading. With no timer of ours there is nothing to keep:
    // the default is to take none of the timers offered.
    public string KeepOursLabel => OursKilledAt is null ? "Take none" : "Keep ours";

    // A conflict row shows the picker; a resolved row (merged in / synced / already in
    // sync) shows ResolvedStatus instead. WasAutoMerged records that we wrote a timer
    // we had none for and WasSynced that one of ours was replaced, so the parent can
    // count each apart from in-sync no-ops.
    [ObservableProperty] private bool _hasConflict;
    [ObservableProperty] private bool _wasAutoMerged;
    [ObservableProperty] private bool _wasSynced;
    [ObservableProperty] private string _resolvedStatus = string.Empty;

    private readonly List<string> _inSyncWith = new();
    // What the row says once resolved, when a timer was merged in or synced.
    private string? _writtenStatus;

    // "keep ours" is the safe default so nothing changes unless the user opts in.
    [ObservableProperty] private bool _keepOursSelected = true;

    public ObservableCollection<BossTimerSyncCellViewModel> Responders { get; } = new();

    // The killed-at that Apply will fold in for a conflict row, or null to leave ours.
    public DateTimeOffset? SelectedKilledAt { get; private set; }

    public BossTimerSyncRowViewModel(
        string bossName, int? monsterNumber, string? matchName, string? sentName,
        DateTimeOffset? oursKilledAt, string oursText)
    {
        _bossName = bossName;
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
    // gathered: the offer is one of several answers being gathered for a boss we
    // hold no timer for (BossTimerSyncCellViewModel.Gathered).
    public void AddConflict(string responder, DateTimeOffset killedAt, string text, bool gathered = false)
    {
        for (int i = Responders.Count - 1; i >= 0; i--)
            if (string.Equals(Responders[i].Responder, responder, StringComparison.OrdinalIgnoreCase))
                Responders.RemoveAt(i);
        Responders.Add(new BossTimerSyncCellViewModel(responder, killedAt, text, Adopt, gathered));
        HasConflict = true;
        ResolvedStatus = string.Empty;
    }

    // The answers waiting to be judged together, in the order they came.
    public IReadOnlyList<BossTimerSyncCellViewModel> Gathered()
    {
        var gathered = new List<BossTimerSyncCellViewModel>();
        foreach (BossTimerSyncCellViewModel c in Responders)
            if (c.Gathered) gathered.Add(c);
        return gathered;
    }

    // A gathered answer taken off the row to be weighed again as it arrives
    // (BossTimerSyncViewModel, once the one player asked turns out to be the only
    // one). With no cell left the row is no longer a pick.
    public void Withdraw(BossTimerSyncCellViewModel cell)
    {
        if (!Responders.Remove(cell)) return;
        if (cell.IsSelected) KeepOurs();
        if (Responders.Count > 0) return;
        HasConflict = false;
        ResolvedStatus = _writtenStatus
            ?? (_inSyncWith.Count > 0 ? $"Already in sync with {string.Join(", ", _inSyncWith)}" : string.Empty);
    }

    // We held no timer for this boss and the offer was merged in — the store write
    // already happened; this just reflects it and advances our effective timer so a later
    // differing offer is measured against what we now hold, and shown beside whose it was.
    // responder is one name, or the several whose answers agreed.
    public void MarkAutoMerged(string responder, DateTimeOffset killedAt, string text)
    {
        OursKilledAt = killedAt;
        OnPropertyChanged(nameof(KeepOursLabel));
        OursText = $"{text} · merged in from {responder}";
        WasAutoMerged = true;
        _writtenStatus = $"Merged in from {responder} — {text}";
        if (!HasConflict) ResolvedStatus = _writtenStatus;
    }

    // We held another timer for this boss and it was replaced by the offer of the
    // one player asked. The store write already happened. What had matched the old
    // timer no longer does.
    public void MarkSynced(string responder, DateTimeOffset killedAt, string text)
    {
        OursKilledAt = killedAt;
        OnPropertyChanged(nameof(KeepOursLabel));
        OursText = $"{text} · synced from {responder}";
        WasSynced = true;
        _inSyncWith.Clear();
        _writtenStatus = $"Synced to {responder}'s timer — {text}";
        if (!HasConflict) ResolvedStatus = _writtenStatus;
    }

    // An offer that is the timer we now hold is no pick: its cell is taken off and
    // its responder is in sync with us. With no cell left the row is resolved.
    // (Our timer may have become theirs since the cell was listed: the gathered
    // answers agreed and were merged in, or a pick of one was applied.)
    public void DropCellsMatchingOurs(Func<DateTimeOffset, DateTimeOffset, bool> same)
    {
        if (OursKilledAt is not { } ours) return;
        bool droppedThePick = false;
        for (int i = 0; i < Responders.Count;)
        {
            BossTimerSyncCellViewModel c = Responders[i];
            if (!same(ours, c.KilledAt)) { i++; continue; }
            droppedThePick |= c.IsSelected;
            if (!_inSyncWith.Contains(c.Responder)) _inSyncWith.Add(c.Responder);
            Responders.RemoveAt(i);
        }
        if (droppedThePick) KeepOurs();
        if (Responders.Count > 0 || !HasConflict) return;
        HasConflict = false;
        ResolvedStatus = _writtenStatus ?? $"Already in sync with {string.Join(", ", _inSyncWith)}";
    }

    // The boss was not on our list when the row was built and is on it now, under
    // matchName: the row is weighed against our timer for it from here on. With no
    // timer yet it reads as any tracked boss we hold none for.
    public void Track(string matchName, string noTimerText)
    {
        MatchName = matchName;
        BossName = matchName;
        if (OursKilledAt is null) OursText = noTimerText;
    }

    // Our timer for this boss is no longer the one the row was showing (a kill of
    // our own, a pick just written, a timer that ran out). The row is weighed
    // against the new one from here on: the default pick is chosen again, since any
    // other was made against a timer we no longer hold, and what was said to match
    // the old one is forgotten.
    public void Rebase(DateTimeOffset? oursKilledAt, string oursText)
    {
        OursKilledAt = oursKilledAt;
        OnPropertyChanged(nameof(KeepOursLabel));
        OursText = oursText;
        _inSyncWith.Clear();
        _writtenStatus = null;
        if (!HasConflict && !WasAutoMerged && !WasSynced) ResolvedStatus = string.Empty;
        KeepOurs();
    }

    // Their offer matched what we already hold — nothing to do.
    public void MarkInSync(string responder)
    {
        if (!_inSyncWith.Contains(responder)) _inSyncWith.Add(responder);
        if (!HasConflict && !WasAutoMerged && !WasSynced)
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

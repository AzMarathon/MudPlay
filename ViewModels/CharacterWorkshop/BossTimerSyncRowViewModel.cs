using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One boss in the sync merge table: our own timer plus a pickable option per responder
// who sent a timer for this boss. Exactly one selection is live at a time — "keep ours"
// (the default) or a responder's. SelectedKilledAt is what Apply will write; null means
// "no change". MatchName is the BossStore key to MarkKilled against (null = a boss we
// don't track, shown for awareness but not applied in this version).
public sealed partial class BossTimerSyncRowViewModel : ObservableObject
{
    public string BossName { get; }
    public int? MonsterNumber { get; }
    public string? MatchName { get; }        // null = untracked by us
    public bool Tracked => MatchName is not null;

    public DateTimeOffset? OursKilledAt { get; }
    public string OursText { get; }

    // "keep ours" is the safe default so nothing changes unless the user opts in.
    [ObservableProperty] private bool _keepOursSelected = true;

    public ObservableCollection<BossTimerSyncCellViewModel> Responders { get; } = new();

    // The killed-at that Apply will fold in, or null to leave ours untouched.
    public DateTimeOffset? SelectedKilledAt { get; private set; }

    public BossTimerSyncRowViewModel(
        string bossName, int? monsterNumber, string? matchName,
        DateTimeOffset? oursKilledAt, string oursText)
    {
        BossName = bossName;
        MonsterNumber = monsterNumber;
        MatchName = matchName;
        OursKilledAt = oursKilledAt;
        OursText = oursText;
        SelectedKilledAt = null;   // keep ours
    }

    public BossTimerSyncCellViewModel AddResponder(
        string responder, DateTimeOffset killedAt, string text)
    {
        BossTimerSyncCellViewModel cell = new(responder, killedAt, text, Adopt);
        Responders.Add(cell);
        return cell;
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

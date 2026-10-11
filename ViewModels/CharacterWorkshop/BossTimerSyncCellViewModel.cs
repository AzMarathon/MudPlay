using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One responder's timer for a boss row in the sync merge table — a pickable option:
// the responder's name, their killed-at time (formatted), and an Adopt button. The
// row highlights whichever option is selected.
public sealed partial class BossTimerSyncCellViewModel : ObservableObject
{
    public string Responder { get; }
    public DateTimeOffset KilledAt { get; }
    public string Text { get; }

    // An answer to a request of the user's that several players can answer, for a
    // boss we held no timer for when it came: it waits with the others for the
    // replies to settle, and is merged in then if they all agree.
    public bool Gathered { get; }

    private readonly Action<BossTimerSyncCellViewModel> _onAdopt;

    [ObservableProperty] private bool _isSelected;

    public BossTimerSyncCellViewModel(
        string responder, DateTimeOffset killedAt, string text, Action<BossTimerSyncCellViewModel> onAdopt,
        bool gathered = false)
    {
        ArgumentNullException.ThrowIfNull(onAdopt);
        Responder = responder;
        KilledAt = killedAt;
        Text = text;
        Gathered = gathered;
        _onAdopt = onAdopt;
    }

    [RelayCommand]
    private void Adopt() => _onAdopt(this);
}

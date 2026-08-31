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

    private readonly Action<BossTimerSyncCellViewModel> _onAdopt;

    [ObservableProperty] private bool _isSelected;

    public BossTimerSyncCellViewModel(
        string responder, DateTimeOffset killedAt, string text, Action<BossTimerSyncCellViewModel> onAdopt)
    {
        ArgumentNullException.ThrowIfNull(onAdopt);
        Responder = responder;
        KilledAt = killedAt;
        Text = text;
        _onAdopt = onAdopt;
    }

    [RelayCommand]
    private void Adopt() => _onAdopt(this);
}

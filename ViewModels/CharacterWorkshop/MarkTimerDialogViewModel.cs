using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Modeless dialog for the Bosses tab "Mark" button — set (or back-date) a boss's
// kill time. Date + time default to now; the user can alter either. On OK the
// combined local instant is returned; Cancel / X returns null (no change).
public sealed partial class MarkTimerDialogViewModel : ObservableObject, IDialogViewModel<DateTimeOffset?>
{
    public event Action<DateTimeOffset?>? CloseRequested;

    public string BossName { get; }
    public string HeaderText => $"Set kill time for {BossName}";

    [ObservableProperty] private DateTimeOffset? _selectedDate;
    [ObservableProperty] private TimeSpan? _selectedTime;

    public MarkTimerDialogViewModel(string bossName, DateTimeOffset defaultLocal)
    {
        BossName = bossName;
        _selectedDate = defaultLocal;
        _selectedTime = defaultLocal.TimeOfDay;
    }

    [RelayCommand]
    private void Ok()
    {
        DateTimeOffset date = SelectedDate ?? DateTimeOffset.Now;
        TimeSpan time = SelectedTime ?? DateTimeOffset.Now.TimeOfDay;
        var combined = new DateTimeOffset(
            date.Year, date.Month, date.Day,
            time.Hours, time.Minutes, time.Seconds, date.Offset);
        CloseRequested?.Invoke(combined);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

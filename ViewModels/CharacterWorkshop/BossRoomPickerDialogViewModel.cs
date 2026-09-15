using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Room picker shown when a double-clicked boss has more than one room. Lists the
// boss's rooms nearest→furthest (the nearest pre-selected) with Run / Load /
// Cancel: Run starts the walk now, Load only arms the destination (the same
// arm-without-start a GOTO's Load does), Cancel walks nothing. The single-room
// case never opens this — the caller walks straight there.
public sealed partial class BossRoomPickerDialogViewModel
    : ObservableObject, IDialogViewModel<BossRoomPick?>
{
    public event Action<BossRoomPick?>? CloseRequested;

    public string Heading { get; }
    public IReadOnlyList<BossRoomOption> Rooms { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand))]
    private BossRoomOption? _selectedRoom;

    public BossRoomPickerDialogViewModel(string bossName, IReadOnlyList<BossRoomOption> rooms)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        Heading = $"{bossName} — pick a room";
        Rooms = rooms;
        // Pre-select the nearest (the list is already sorted nearest-first), so a
        // plain Run/Load acts on the closest room without an extra click.
        SelectedRoom = rooms.Count > 0 ? rooms[0] : null;
    }

    private bool HasSelection => SelectedRoom is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Run()
    {
        if (SelectedRoom is { } o) CloseRequested?.Invoke(new BossRoomPick(o.Key, StartNow: true));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Load()
    {
        if (SelectedRoom is { } o) CloseRequested?.Invoke(new BossRoomPick(o.Key, StartNow: false));
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);
}

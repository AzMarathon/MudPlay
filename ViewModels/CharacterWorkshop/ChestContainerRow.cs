using System;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels.CharacterWorkshop;

// A container listed in the Chest Offload window. Clicking it sends "open <name>"
// to the game. Real rows come from the player's inventory (the window then diffs
// to see the loot); "simulated" rows are seeded by the test button and roll the
// chest's loot table instead of waiting on a real drop.
public sealed class ChestContainerRow
{
    public string Name { get; }
    public string Display { get; }
    public bool Simulated { get; }
    public IRelayCommand OpenCommand { get; }

    public ChestContainerRow(string name, int count, Action open, bool simulated = false)
    {
        Name = name;
        Simulated = simulated;
        Display = simulated ? $"{name} (sim)" : count > 1 ? $"{count} × {name}" : name;
        OpenCommand = new RelayCommand(open);
    }
}

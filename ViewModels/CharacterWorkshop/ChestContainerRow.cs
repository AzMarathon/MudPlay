using System;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels.CharacterWorkshop;

// A container the player is holding, listed in the Chest Offload window. Clicking
// it sends "open <name>" to the game; the window then re-parses inventory to see
// what dropped.
public sealed class ChestContainerRow
{
    public string Name { get; }
    public string Display { get; }
    public IRelayCommand OpenCommand { get; }

    public ChestContainerRow(string name, int count, Action open)
    {
        Name = name;
        Display = count > 1 ? $"{count} × {name}" : name;
        OpenCommand = new RelayCommand(open);
    }
}

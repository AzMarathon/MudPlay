using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Inventory;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One Item Finder "More stats" row: keeps items whose stat is at or above Min
// (0 = off), like the fixed bonus rows above it. Data-driven so every bonus column
// the grid shows can be filtered on without a hand-written ticker per stat.
public sealed partial class ItemFinderStatFilter : ObservableObject
{
    private readonly Func<ItemFinderEntry, int> _value;

    public ItemFinderStatFilter(string label, Func<ItemFinderEntry, int> value)
    {
        Label = label;
        _value = value;
    }

    public string Label { get; }

    [ObservableProperty] private int _min;

    public bool Passes(ItemFinderEntry e) => Min <= 0 || _value(e) >= Min;
}

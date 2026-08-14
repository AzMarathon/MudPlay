using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Calculators;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One looted item in the Chest Offload window: how many the chest gave, an
// editable sell quantity (keep some, sell the rest), and what the selected
// quantity fetches at the current charm. Vendor sell-back is shop-independent
// (charm-only), so the per-item value is the same wherever it's sold.
public sealed partial class ChestOffloadItemRow : ObservableObject
{
    public string Name { get; }
    public int Gained { get; }
    public double BaseCopper { get; }

    private readonly Action? _onQtyChanged;
    private int _charm;
    private RealmType _realm;

    [ObservableProperty] private int _sellQty;
    [ObservableProperty] private string _lineValue = "—";
    public long LineCopper { get; private set; }

    // Drop the whole stack and take it off the sell list (wired by the window VM).
    public IRelayCommand DropCommand { get; }

    public ChestOffloadItemRow(string name, int gained, double baseCopper,
        Action? onQtyChanged, Action<ChestOffloadItemRow>? onDrop = null)
    {
        Name = name;
        Gained = gained;
        BaseCopper = baseCopper;
        _onQtyChanged = onQtyChanged;
        _sellQty = gained;   // default: sell all of what the chest gave
        DropCommand = new RelayCommand(() => onDrop?.Invoke(this));
    }

    public void Reprice(int charm, RealmType realm)
    {
        _charm = charm;
        _realm = realm;
        Recompute();
    }

    partial void OnSellQtyChanged(int value)
    {
        Recompute();
        _onQtyChanged?.Invoke();
    }

    private void Recompute()
    {
        double unit = ShopPriceCalculator.SellCopper(BaseCopper, _charm, _realm);
        LineCopper = (long)Math.Round(unit * Math.Max(0, SellQty));
        LineValue = LineCopper > 0 ? ShopPriceCalculator.FormatCopper(LineCopper) : "—";
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Calculators;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One looted item in the Chest Offload window: how many the chest gave, an
// editable sell quantity (keep some, sell the rest), and what the selected
// quantity fetches at the current charm. Vendor sell-back is shop-independent
// (charm-only), so the per-item value is the same wherever it's sold — which is
// why an item several shops buy can be freely moved between them via the popup.
public sealed partial class ChestOffloadItemRow : ObservableObject
{
    public string Name { get; }
    // Held count from the chest — reduced as confirmed sales/drops of this item land
    // (bound as the sell-qty ceiling and the "of N" label, so both track live).
    [ObservableProperty] private int _gained;
    public double BaseCopper { get; }
    public IReadOnlyCollection<int> CandidateShops { get; }

    private readonly Action<ChestOffloadItemRow>? _onQtyChanged;
    private readonly Func<ChestOffloadItemRow, IReadOnlyList<ShopChoiceRow>>? _buildChoices;
    private readonly Action<ChestOffloadItemRow, int>? _moveToShop;
    private int _charm;
    private RealmType _realm;

    // The shop group this row currently sits in (updated when the user moves it).
    public int CurrentShop { get; set; }
    // Only offer the "sell elsewhere" affordance when there's somewhere else to go.
    public bool CanChangeShop => CandidateShops.Count > 1;

    [ObservableProperty] private int _sellQty;
    [ObservableProperty] private string _lineValue = "—";
    public long LineCopper { get; private set; }

    // "Sell at a different shop" popup state.
    [ObservableProperty] private bool _shopMenuOpen;
    [ObservableProperty] private ShopChoiceRow? _selectedShopChoice;
    public ObservableCollection<ShopChoiceRow> ShopChoices { get; } = new();
    public bool HasShopChoices => ShopChoices.Count > 0;

    public IRelayCommand DropCommand { get; }
    public IRelayCommand SellCommand { get; }

    public ChestOffloadItemRow(string name, int gained, double baseCopper,
        IReadOnlyCollection<int> candidateShops, int currentShop,
        Action<ChestOffloadItemRow>? onQtyChanged, Action<ChestOffloadItemRow>? onDrop = null,
        Func<ChestOffloadItemRow, IReadOnlyList<ShopChoiceRow>>? buildChoices = null,
        Action<ChestOffloadItemRow, int>? moveToShop = null,
        Action<ChestOffloadItemRow>? onSell = null)
    {
        Name = name;
        _gained = gained;
        BaseCopper = baseCopper;
        CandidateShops = candidateShops;
        CurrentShop = currentShop;
        _onQtyChanged = onQtyChanged;
        _buildChoices = buildChoices;
        _moveToShop = moveToShop;
        _sellQty = gained;   // default: sell all of what the chest gave
        DropCommand = new RelayCommand(() => onDrop?.Invoke(this));
        SellCommand = new RelayCommand(() => onSell?.Invoke(this));
    }

    public void Reprice(int charm, RealmType realm)
    {
        _charm = charm;
        _realm = realm;
        Recompute();
    }

    [RelayCommand]
    private void OpenShopMenu()
    {
        ShopChoices.Clear();
        if (_buildChoices is not null)
            foreach (ShopChoiceRow choice in _buildChoices(this)) ShopChoices.Add(choice);
        OnPropertyChanged(nameof(HasShopChoices));
        SelectedShopChoice = null;
        ShopMenuOpen = true;
    }

    [RelayCommand]
    private void ConfirmShopChange()
    {
        // Close first, then re-parent — the row moves between ItemsControls.
        ShopChoiceRow? choice = SelectedShopChoice;
        ShopMenuOpen = false;
        if (choice is not null) _moveToShop?.Invoke(this, choice.Shop);
    }

    [RelayCommand]
    private void CancelShopChange() => ShopMenuOpen = false;

    partial void OnSellQtyChanged(int value)
    {
        Recompute();
        _onQtyChanged?.Invoke(this);
    }

    private void Recompute()
    {
        double unit = ShopPriceCalculator.SellCopper(BaseCopper, _charm, _realm);
        LineCopper = (long)Math.Round(unit * Math.Max(0, SellQty));
        LineValue = LineCopper > 0 ? ShopPriceCalculator.FormatCopper(LineCopper) : "—";
    }

    // What the Drop button drops: the leftover you're NOT selling (held − picked).
    public int DropQty => Math.Max(0, Gained - SellQty);

    // Apply a confirmed SELL of `count` of this item: the sold copies were the
    // picked-to-sell portion, so shed both the held count and the pick by `count`.
    // Returns true when nothing's left and the row should be removed.
    public bool ApplySold(int count)
    {
        Gained = Math.Max(0, Gained - count);
        SellQty = Math.Clamp(SellQty - count, 0, Gained);
        Recompute();
        return Gained <= 0;
    }

    // Apply a confirmed DROP of `count` of this item: the dropped copies were the
    // leftover you weren't selling, so shed the held count but keep the sell pick
    // (clamped to what remains). Returns true when nothing's left.
    public bool ApplyDropped(int count)
    {
        Gained = Math.Max(0, Gained - count);
        if (SellQty > Gained) SellQty = Gained;
        Recompute();
        return Gained <= 0;
    }
}

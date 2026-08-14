using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Map;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One shop's worth of looted items in the Chest Offload window. The header walks
// to the shop and shows the running total for what's selected here; the Sell
// button fires the sell commands for every selected item (the caller batches per
// realm). Items destined for the same shop are grouped so the trip is shortest.
public sealed partial class ChestOffloadShopGroup : ObservableObject
{
    public string ShopName { get; }
    public bool CanWalk { get; }
    public ObservableCollection<ChestOffloadItemRow> Items { get; } = new();

    [ObservableProperty] private string _total = "—";

    public IRelayCommand WalkCommand { get; }
    public IRelayCommand SellCommand { get; }
    public IRelayCommand DropAllCommand { get; }

    public ChestOffloadShopGroup(string shopName, RoomKey? room,
        Action<RoomKey> queueWalk, Action<ChestOffloadShopGroup> sell, Action<ChestOffloadShopGroup> dropAll)
    {
        ShopName = shopName;
        CanWalk = room is not null;
        WalkCommand = new RelayCommand(() => { if (room is { } key) queueWalk(key); }, () => room is not null);
        SellCommand = new RelayCommand(() => sell(this));
        DropAllCommand = new RelayCommand(() => dropAll(this));
    }

    // Re-price every item at the current charm, then re-total.
    public void Reprice(int charm, RealmType realm)
    {
        foreach (ChestOffloadItemRow item in Items) item.Reprice(charm, realm);
        Retotal();
    }

    public void Retotal()
    {
        long total = 0;
        foreach (ChestOffloadItemRow item in Items) total += item.LineCopper;
        Total = total > 0 ? ShopPriceCalculator.FormatCopper(total) : "—";
    }
}

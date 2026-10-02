using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Calculators;
using MudPlay.Game.Train;

namespace MudPlay.ViewModels.Settings;

// One shop-sold spell in the Settings → Auto-Trainer spell list: the level it
// unlocks at, the scroll that teaches it, where that's sold, and the Get? toggle
// deciding whether a train trip goes on to buy it. Flipping Wanted marks the parent
// section dirty via the supplied callback.
public sealed partial class ShopSpellRowViewModel : ObservableObject
{
    private readonly Action _onWantedChanged;

    // Spells.Name — the persistence key for the skipped set.
    public string Spell { get; }
    public int Level { get; }
    public string Scroll { get; }
    // "<shop> (map/room)" per selling room, comma-separated.
    public string SoldAt { get; }
    // The cheapest price among those shops, at the character's Charm.
    public string Price { get; }
    public bool IsLearned { get; }
    public string Learned => IsLearned ? "yes" : string.Empty;

    [ObservableProperty] private bool _wanted;

    public ShopSpellRowViewModel(ShopSpellOffer offer, bool learned, bool wanted, Action onWantedChanged)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(onWantedChanged);
        Spell = offer.SpellName;
        Level = offer.ReqLevel;
        IReadOnlyList<ShopSpellSource> sources = offer.Sources;
        Scroll = string.Join(" / ", sources.Select(s => s.ItemName).Distinct(StringComparer.OrdinalIgnoreCase));
        SoldAt = string.Join(", ", sources.Select(s => string.Create(CultureInfo.InvariantCulture,
            $"{s.ShopName} ({s.Room.Map}/{s.Room.Room})")));
        long cheapest = sources.Min(s => s.PriceCopper);
        Price = cheapest <= 0 ? "Free" : ShopPriceCalculator.FormatCopper(cheapest);
        IsLearned = learned;
        _wanted = wanted;
        _onWantedChanged = onWantedChanged;
    }

    partial void OnWantedChanged(bool value) => _onWantedChanged();
}

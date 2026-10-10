using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.ViewModels.Settings;

// One room spell in Settings → Periodic Damage Room Spells: what it does, what
// counters it, how many rooms carry it, and the Bars resting tick box.
public sealed partial class PeriodicDamageRoomSpellRowViewModel : ObservableObject
{
    // A room link is a map/room number; this many sit on one line of the room list.
    private const int LinksPerLine = 8;

    private readonly Action _changed;
    private IReadOnlyList<RoomAreaViewModel>? _areas;

    public PeriodicDamageRoomSpell Spell { get; }

    public int Number => Spell.Number;
    public string NumberText { get; }
    public string Name => Spell.Name;
    public string Damage { get; }
    public string How { get; }
    public string Counters { get; }
    public string RoomCount { get; }

    // What the box is with nothing stored for the spell.
    public bool BarsByDefault { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    private bool _barsResting;

    // Set away from the default: the only rows the profile stores.
    public bool IsChanged => BarsResting != BarsByDefault;

    public PeriodicDamageRoomSpellRowViewModel(
        PeriodicDamageRoomSpell spell, bool barsResting, Func<int, string?> itemName, Action changed)
    {
        ArgumentNullException.ThrowIfNull(spell);
        ArgumentNullException.ThrowIfNull(itemName);
        ArgumentNullException.ThrowIfNull(changed);
        Spell = spell;
        _changed = changed;
        NumberText = "#" + spell.Number.ToString(CultureInfo.InvariantCulture);
        Damage = RoomSpellDamageText.Damage(spell.Reading);
        How = RoomSpellDamageText.How(spell.Reading, itemName);
        Counters = spell.Counters.Length > 0 ? spell.Counters : "nothing in the game data";
        RoomCount = spell.Rooms.Count.ToString("N0", CultureInfo.InvariantCulture);
        BarsByDefault = RoomSpellDamageIndex.BarsRestingByDefault(spell.Reading.Kind);
        _barsResting = barsResting;
    }

    // The spell's rooms by map and room name, the largest area first. A spell can
    // sit on a couple of thousand rooms, so nothing is laid out until the row is
    // picked, and then one area's rooms at a time.
    public IReadOnlyList<RoomAreaViewModel> Areas => _areas ??= Spell.Rooms
        .GroupBy(static r => (r.Key.Map, r.DisplayName))
        .OrderByDescending(static g => g.Count())
        .ThenBy(static g => g.Key.Map)
        .ThenBy(static g => g.Key.DisplayName, StringComparer.OrdinalIgnoreCase)
        .Select(static g => new RoomAreaViewModel(g.Key.Map, g.Key.DisplayName, g.Select(static r => r.Key)
            .OrderBy(static k => k.Room).ToList(), LinksPerLine))
        .ToList();

    partial void OnBarsRestingChanged(bool value) => _changed();
}

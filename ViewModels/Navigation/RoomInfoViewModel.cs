using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.Navigation;

// Backs the Navigation window's ROOM INFO rail section. Left-clicking a room on
// the map populates the panel with clickable links to every game-data record
// attached to it — the room record itself, the monsters that lair / spawn / are
// placed there, the items placed on its floor (roomitem), the shop as a whole,
// and the cast-on-enter room spell. Each link opens the matching Game Data
// Browser record. This is the enumeration RoomDetailDialogViewModel does for its
// popup, restructured as an always-present inline panel populated live.
public sealed partial class RoomInfoViewModel : ObservableObject
{
    private readonly AppServices _services;

    // Identity of the room currently shown — the target of the title-link command.
    private RoomKey _key;

    public RoomInfoViewModel(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    // True once a room has been clicked — drives the "click a room" empty hint.
    [ObservableProperty] private bool _hasRoom;

    [ObservableProperty] private string _roomName = string.Empty;
    [ObservableProperty] private string _roomKeyLabel = string.Empty;

    // The shop as a whole (when the room hosts one) — one link to its Shops record.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShop))]
    private RoomDetailLink? _shopLink;
    public bool HasShop => ShopLink is not null;

    // The cast-on-enter room spell (when set).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoomSpell))]
    private RoomDetailLink? _roomSpellLink;
    public bool HasRoomSpell => RoomSpellLink is not null;

    // Monsters split by how the room hosts them — mirrors the map tooltip's
    // Placed / Assigned / Lair lines. Each renders under its own labelled group.
    public ObservableCollection<RoomDetailLink> PlacedMonsters { get; } = new();
    public bool HasPlaced => PlacedMonsters.Count > 0;

    public ObservableCollection<RoomDetailLink> AssignedMonsters { get; } = new();
    public bool HasAssigned => AssignedMonsters.Count > 0;

    public ObservableCollection<RoomDetailLink> LairMonsters { get; } = new();
    public bool HasLair => LairMonsters.Count > 0;

    // "Max Regen: N @ time" for the room's lair, shown beneath the Lair group
    // (mirrors the tooltip). Empty when the room has no lair.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLairRegen))]
    private string _lairRegen = string.Empty;
    public bool HasLairRegen => LairRegen.Length > 0;

    public ObservableCollection<RoomDetailLink> FloorItems { get; } = new();
    public bool HasFloorItems => FloorItems.Count > 0;

    // Populate every section for the clicked room. Called from
    // NavigationViewModel.OnRoomLeftClicked on any left-click.
    public void Show(RoomKey key)
    {
        _key = key;
        PlacedMonsters.Clear();
        AssignedMonsters.Clear();
        LairMonsters.Clear();
        LairRegen = string.Empty;
        FloorItems.Clear();
        ShopLink = null;
        RoomSpellLink = null;
        HasRoom = true;

        Room? room = _services.RoomGraph.GetRoom(key);
        if (room is null)
        {
            // No record for the key in the active set — the title still links to the
            // (possibly empty) Rooms browser row via OpenRoomRecord.
            RoomName = "???";
            RoomKeyLabel = key.ToString();
            RaiseSectionVisibility();
            return;
        }

        RoomName = room.DisplayName;
        RoomKeyLabel = $"Map {key.Map} · Room {key.Room}";

        // Monsters — split into Placed (the room's NPC fixture / a boss),
        // Assigned (roam / rare-random spawns), and Lair (consistent lair
        // spawners), each under its own labelled group so the panel mirrors the
        // map tooltip. A monster may appear in more than one group (a placed boss
        // that also roams) — the distinction is intentional, not a duplicate. The
        // group header carries the category, so the per-link note is dropped.
        RoomTooltipBuilder.RoomMonsters rm =
            RoomTooltipBuilder.ResolveRoomMonsters(room, _services.GameData, _services.MonsterSpawns);
        foreach (RoomTooltipBuilder.RoomMonsterRef m in rm.Placed)
            PlacedMonsters.Add(MakeMonsterLink(m.Id, m.Name, note: null));
        foreach (RoomTooltipBuilder.RoomMonsterRef m in rm.Assigned)
            AssignedMonsters.Add(MakeMonsterLink(m.Id, m.Name, note: null));
        foreach (RoomTooltipBuilder.RoomMonsterRef m in rm.Lair)
            LairMonsters.Add(MakeMonsterLink(m.Id, m.Name, note: null));
        LairRegen = RoomTooltipBuilder.FormatLairRegen(rm.LairMax, room.Delay);

        // Floor items — TBInfo `roomitem` placements.
        foreach (int itemId in _services.RoomFloorItems.FloorItemsOf(key))
        {
            int id = itemId;
            string name = _services.GameData.FindNameByNumber("Items", id) ?? $"#{id}";
            FloorItems.Add(new RoomDetailLink(
                $"{name}(#{id})", null, new RelayCommand(() => _services.OpenItemGameData(id))));
        }

        // Shop — one link that opens the interactive room-detail popup for this room (the
        // stock menu with buy/sell prices + charm picker), the same popup the Shops browser
        // tab double-click opens — not a Game Data Browser jump.
        if (room.Shop > 0)
        {
            int shop = room.Shop;
            string shopName = _services.GameData.FindNameByNumber("Shops", shop) ?? $"Shop #{shop}";
            RoomKey roomKey = key;
            ShopLink = new RoomDetailLink(
                $"{shopName}(#{shop})", null, new RelayCommand(() => RoomDetailPopup.Show(_services.Dialogs, roomKey)));
        }

        // Room spell — the cast-on-enter effect. Opens the spell record DIALOG (Message /
        // Game-Data tabs), like the monster link, not a Game Data Browser jump.
        if (room.Spell > 0)
        {
            int spell = room.Spell;
            string spellName = _services.GameData.FindNameByNumber("Spells", spell) ?? $"Spell #{spell}";
            RoomSpellLink = new RoomDetailLink(
                $"{spellName}(#{spell})", null, new AsyncRelayCommand(() => _services.OpenSpellRecordAsync(spell)));
        }

        RaiseSectionVisibility();
    }

    // The room title itself is the link to the room's game-data record.
    [RelayCommand]
    private void OpenRoomRecord() => _services.OpenRoomGameData(_key.Map, _key.Room);

    // The label carries the monster's record number — "chest(#69)" — mirroring the
    // room-detail popup so the panel doubles as a quick lookup key. Clicking opens the
    // monster record DIALOG (like the item record), not a Game Data Browser jump.
    private RoomDetailLink MakeMonsterLink(int id, string name, string? note)
        => new($"{name}(#{id})", note, new AsyncRelayCommand(() => _services.OpenMonsterRecordAsync(id)));

    // Has* flags track collection counts, not observable fields, so a re-populate
    // has to poke their bindings by hand. (HasLairRegen is auto-notified via the
    // LairRegen [ObservableProperty].)
    private void RaiseSectionVisibility()
    {
        OnPropertyChanged(nameof(HasPlaced));
        OnPropertyChanged(nameof(HasAssigned));
        OnPropertyChanged(nameof(HasLair));
        OnPropertyChanged(nameof(HasFloorItems));
    }
}

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
// the map (or a Game Data room chip, via SelectAndInspect) populates the panel
// with everything attached to it — the room name + light, the monsters that
// lair / spawn / are placed there, the obvious exits, the items on its floor
// (roomitem), the shop, and the cast-on-enter room spell. Monster / item / spell
// links open the matching Game Data record; exits re-root the map on the
// neighbour; the shop (and a shop room's title) open the shop stock popup. This
// is the enumeration RoomDetailDialogViewModel does for its popup, restructured
// as an always-present inline panel populated live.
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

    // Room-illumination summary shown below the map/room number ("Room Illu:
    // <value> - <phrase>", the room alone). Shown for every room — a fully-lit
    // room is the base value 0 ("Room Illu: 0 - You can see."), not "no illu";
    // empty only when no room record is shown.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRoomLight))]
    private string _roomLight = string.Empty;
    public bool HasRoomLight => RoomLight.Length > 0;

    // Player-adjusted illumination shown below Room Illu ("Your Illu: <value> -
    // <phrase>") — the room's light plus the player's carried illu (worn +illu
    // gear + readied light) and any configured light-spell illu. Empty when the
    // player carries no light (or no room is shown).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayerLight))]
    private string _playerLight = string.Empty;
    public bool HasPlayerLight => PlayerLight.Length > 0;

    // The shop as a whole (when the room hosts one) — one link to its stock popup.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShop))]
    [NotifyPropertyChangedFor(nameof(RoomTitleTip))]
    private RoomDetailLink? _shopLink;
    public bool HasShop => ShopLink is not null;

    // The room title's tooltip — a shop room's title jumps to its shop stock
    // popup, a plain room's to the Rooms game-data record (see OpenRoomRecord).
    public string RoomTitleTip => HasShop
        ? "Open this room's shop"
        : "Open this room's Game Data record";

    // Obvious exits — one clickable row per exit; clicking re-roots the map on
    // the neighbour (and refreshes this panel). Mirrors the map tooltip's order.
    public ObservableCollection<RoomDetailLink> Exits { get; } = new();
    public bool HasExits => Exits.Count > 0;

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
        RoomLight = string.Empty;
        PlayerLight = string.Empty;
        Exits.Clear();
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
        // With no carried light, Room Illu carries the phrase and there's no Your
        // Illu line. When the player has light (worn gear + readied light + any
        // configured light-spell illu), Your Illu appears with the effective value
        // and the phrase, and Room Illu drops its phrase (shows just its value).
        int playerIllu = _services.PlayerIllumination.Current + _services.ConfiguredLightSpellIllu();
        bool hasPlayerIllu = playerIllu > 0;
        RoomLight = RoomTooltipBuilder.BuildRoomLightSummary(room, includePhrase: !hasPlayerIllu);
        PlayerLight = hasPlayerIllu
            ? RoomTooltipBuilder.BuildPlayerLightSummary(room, playerIllu)
            : string.Empty;

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

        // Obvious exits — one clickable row per exit, same ordering + hint as the
        // map tooltip. Clicking re-roots the map on the neighbour and refreshes
        // this panel (NavigateToRoom → SelectAndInspect on the live nav window).
        foreach ((Direction dir, RoomExit exit) in RoomTooltipBuilder.OrderedExits(room))
        {
            RoomKey target = exit.Target;
            Room? dest = _services.RoomGraph.GetRoom(target);
            string destName = dest is not null ? dest.DisplayName : target.ToString();
            string hint = RoomTooltipBuilder.FormatExitHint(exit, _services.GameData);
            string label = $"{RoomTooltipBuilder.DirectionLabel(dir)} → {destName} ({target})";
            if (hint.Length > 0) label += $" · {hint}";
            Exits.Add(new RoomDetailLink(label, null, new RelayCommand(() => _services.NavigateToRoom(target))));
        }

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

    // The room title's click target. A shop room jumps to its shop stock popup
    // (item · max · regen · buy · sell + charm picker) — the useful view for a
    // merchant / bank / trainer — while a plain room opens the Rooms game-data
    // record. (The shop link below opens the same popup; the title is a second
    // path so a shop room's most prominent control lands on its stock.)
    [RelayCommand]
    private void OpenRoomRecord()
    {
        if (HasShop)
            RoomDetailPopup.Show(_services.Dialogs, _key);
        else
            _services.OpenRoomGameData(_key.Map, _key.Room);
    }

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
        OnPropertyChanged(nameof(HasExits));
        OnPropertyChanged(nameof(HasFloorItems));
    }
}

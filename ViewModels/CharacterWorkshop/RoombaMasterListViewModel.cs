using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Read-only master inventory across every room Roomba has ever scanned,
// opened from the GH Management tab's "Master List" button. One row per
// (item, room) sighting in GhItemLocationStore, cross-referenced against
// Items.json's Obtained From via the same ItemMdbViewBuilder the Item edit
// dialog uses — so the Market column reads exactly like that dialog's
// bought/sold list, minus any shop that sits inside a room the user has
// labeled as part of THIS gang house (that market isn't a useful outside
// reference point, and it's the definition of "gang house" this app already
// tracks — see GhRoomLabelStore).
//
// The market resolution is expensive per item, so it's built lazily per row
// (RoombaMasterListRowViewModel.Market) — the list opens instantly even on a
// large log. A filter box narrows the rows live; a double-click opens the
// item's record.
public sealed partial class RoombaMasterListViewModel : ObservableObject, IDisposable
{
    // The reference point the user asked for — MajorMUD's neutral "retail"
    // charm (GAME_MECHANICS.md: Charm 50 leaves BUY at retail and both SELL
    // branches land on half base). Fixed, not a live player stat, so the
    // list reads the same regardless of whose game session pulled it up.
    private const int ReferenceCharm = 50;

    private readonly GhItemLocationStore _locations;
    private readonly GhRoomLabelStore _labels;
    private readonly ItemNameStore _itemNames;
    private readonly GameDataCache _gameData;
    private readonly RoomGraphManager _roomGraph;

    // All rows, unfiltered; Rows is the filtered view bound to the grid.
    private readonly List<RoombaMasterListRowViewModel> _allRows = new();
    public ObservableCollection<RoombaMasterListRowViewModel> Rows { get; } = new();

    // Filters the rows live by item name, quantity, or the "seen in" text
    // (which carries the map/room, so "15/12" matches).
    [ObservableProperty] private string _filter = string.Empty;

    public RoombaMasterListViewModel(
        GhItemLocationStore locations, GhRoomLabelStore labels, ItemNameStore itemNames,
        GameDataCache gameData, RoomGraphManager roomGraph)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(itemNames);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(roomGraph);
        _locations = locations;
        _labels = labels;
        _itemNames = itemNames;
        _gameData = gameData;
        _roomGraph = roomGraph;

        _locations.Changed += Rebuild;
        Rebuild();
    }

    public void Dispose() => _locations.Changed -= Rebuild;

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void Rebuild()
    {
        _allRows.Clear();

        HashSet<RoomKey> ghRooms = new(_labels.Labels.Select(l => new RoomKey(l.Map, l.Room)));
        // One lazy market per distinct item, shared by every row for that item —
        // Obtained From doesn't vary by sighting, and the value is only computed
        // if/when a row for it is actually rendered.
        Dictionary<int, Lazy<string>> marketByItem = new();

        foreach (GhItemSighting sighting in _locations.Sightings
                     .OrderBy(s => s.ItemName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(s => s.Map).ThenBy(s => s.Room))
        {
            RoomKey seenIn = new(sighting.Map, sighting.Room);
            string? roomName = _roomGraph.GetRoom(seenIn)?.Name;
            string seenInText = roomName is { Length: > 0 } ? $"{roomName} ({seenIn})" : seenIn.ToString();

            int number = _itemNames.FindByName(sighting.ItemName) is int n ? n : -1;
            Lazy<string> market = number >= 0
                ? marketByItem.TryGetValue(number, out Lazy<string>? cached)
                    ? cached
                    : marketByItem[number] = new Lazy<string>(() => BuildMarketText(number, ghRooms))
                : UnresolvedMarket;

            _allRows.Add(new RoombaMasterListRowViewModel(
                sighting.ItemName, sighting.Quantity, seenInText, number, market));
        }

        ApplyFilter();
    }

    private static readonly Lazy<string> UnresolvedMarket = new(() => "(unresolved item)");

    private void ApplyFilter()
    {
        Rows.Clear();
        string f = Filter?.Trim() ?? string.Empty;
        foreach (RoombaMasterListRowViewModel r in _allRows)
        {
            if (f.Length == 0
                || r.ItemName.Contains(f, StringComparison.OrdinalIgnoreCase)
                || r.SeenIn.Contains(f, StringComparison.OrdinalIgnoreCase)
                || r.Quantity.ToString(CultureInfo.InvariantCulture).Contains(f))
                Rows.Add(r);
        }
    }

    // Double-click a row → open that item's record (the same ItemEditDialog the
    // Game Data Browser opens). No-op for an unresolved item.
    public async Task OpenItemRecordAsync(RoombaMasterListRowViewModel? row)
    {
        if (row is null || row.ItemNumber < 0) return;
        await ItemRecordOpener.OpenAsync(row.ItemNumber, row.ItemName);
    }

    // Reuses ItemMdbViewBuilder's Obtained-From shop resolution (same buy/sell
    // figures the Item edit dialog shows) at the fixed reference charm, then
    // drops any shop whose host room is one of this gang house's own labeled
    // rooms.
    private string BuildMarketText(int itemNumber, HashSet<RoomKey> ghRooms)
    {
        ItemMdbViewBuilder builder = new(_gameData, ReferenceCharm);
        ItemMdbView view = builder.Build(itemNumber.ToString(CultureInfo.InvariantCulture));

        List<ShopSaleRow> market = view.Shops
            .Where(s => !(s.CanOpen && ghRooms.Contains(new RoomKey(s.Map, s.Room))))
            .ToList();

        return market.Count == 0
            ? "(no outside market)"
            : string.Join("; ", market.Select(s => $"{s.Location}: {s.Price}"));
    }
}

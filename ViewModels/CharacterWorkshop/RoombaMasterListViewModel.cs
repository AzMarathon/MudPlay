using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
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
public sealed class RoombaMasterListViewModel : IDisposable
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

    public ObservableCollection<RoombaMasterListRowViewModel> Rows { get; } = new();

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

    private void Rebuild()
    {
        Rows.Clear();

        HashSet<RoomKey> ghRooms = new(_labels.Labels.Select(l => new RoomKey(l.Map, l.Room)));
        // One shop cross-reference per distinct item, reused across every room
        // that item was seen in — Obtained From doesn't vary by sighting.
        Dictionary<int, string> marketByItemNumber = new();

        foreach (GhItemSighting sighting in _locations.Sightings
                     .OrderBy(s => s.ItemName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(s => s.Map).ThenBy(s => s.Room))
        {
            RoomKey seenIn = new(sighting.Map, sighting.Room);
            string? roomName = _roomGraph.GetRoom(seenIn)?.Name;
            string seenInText = roomName is { Length: > 0 } ? $"{roomName} ({seenIn})" : seenIn.ToString();

            string market = _itemNames.FindByName(sighting.ItemName) is int number
                ? marketByItemNumber.TryGetValue(number, out string? cached)
                    ? cached
                    : marketByItemNumber[number] = BuildMarketText(number, ghRooms)
                : "(unresolved item)";

            Rows.Add(new RoombaMasterListRowViewModel(sighting.ItemName, sighting.Quantity, seenInText, market));
        }
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

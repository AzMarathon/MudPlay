using System.Linq;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Map;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.Navigation;

// Builds + opens the read-only "Details…" browse window for a route polyline,
// resolving every per-room link (monsters, hazard, item gate) + the room-click
// map highlight from AppServices. Shared by the Navigation window's CURRENT-NAV
// Details button and the route picker's Details button so both show the same
// window and rows.
public static class RouteDetailsLauncher
{
    // The RouteDetailRow list for a route's room-key polyline (source-first). Empty
    // when the polyline is trivial.
    public static IReadOnlyList<RouteDetailRow> BuildRows(AppServices services, IReadOnlyList<RoomKey>? polyline)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (polyline is not { Count: > 1 }) return Array.Empty<RouteDetailRow>();
        return CurrentRouteDetails.Build(
            services.RoomGraph, services.Bfs, services.Movement,
            polyline, services.ItemNames.GetName,
            key => MonsterLinks(services, key),
            services.HighlightWhereRoom,
            key => RoomHazard(services, key),
            id => ItemLink(services, id));
    }

    // Open the browse window for a polyline (modeless, fire-and-forget). Returns the
    // VM so a caller can toggle it closed (RequestClose) on a re-press.
    public static RouteDetailsDialogViewModel Open(
        AppServices services, string title, IReadOnlyList<RoomKey>? polyline)
    {
        ArgumentNullException.ThrowIfNull(services);
        var vm = new RouteDetailsDialogViewModel(title, BuildRows(services, polyline));
        _ = services.Dialogs.OpenWindowAsync<RouteDetailsDialogViewModel, bool?>(vm);
        return vm;
    }

    // A room's placed + lair monsters (deduped by id), each opening its record.
    private static IReadOnlyList<RoomDetailLink> MonsterLinks(AppServices services, RoomKey key)
    {
        Room? room = services.RoomGraph.GetRoom(key);
        if (room is null) return Array.Empty<RoomDetailLink>();
        RoomTooltipBuilder.RoomMonsters rm =
            RoomTooltipBuilder.ResolveRoomMonsters(room, services.GameData, services.MonsterSpawns);

        var links = new List<RoomDetailLink>(rm.Placed.Count + rm.Lair.Count);
        var seen = new HashSet<int>();
        foreach (RoomTooltipBuilder.RoomMonsterRef m in rm.Placed.Concat(rm.Lair))
        {
            if (!seen.Add(m.Id)) continue;
            links.Add(new RoomDetailLink($"{m.Name}(#{m.Id})", null,
                new AsyncRelayCommand(() => services.OpenMonsterRecordAsync(m.Id))));
        }
        return links;
    }

    // A room's protectable cast-on-enter hazard (RoomHazardIndex) — the harmful
    // spell + its counter items. Null for a room with no room-entry hazard (an
    // item-gated exit off it is folded in by CurrentRouteDetails.Build).
    private static RouteStepWarning? RoomHazard(AppServices services, RoomKey key)
    {
        Room? room = services.RoomGraph.GetRoom(key);
        if (room is null || room.Spell <= 0) return null;
        if (services.RoomHazards.HazardForSpell(room.Spell) is not { } hz) return null;

        string spellName = services.GameData.FindNameByNumber("Spells", room.Spell)
            ?? $"spell #{room.Spell}";
        var spellLink = new RoomDetailLink(spellName, null,
            new AsyncRelayCommand(() => services.OpenSpellRecordAsync(room.Spell)));

        var counters = new List<RoomDetailLink>(hz.ProtectingItems.Count);
        foreach (int itemId in hz.ProtectingItems)
            counters.Add(ItemLink(services, itemId));
        return new RouteStepWarning(spellLink, counters);
    }

    // An item id → a clickable link to its Game Data record (hazard counters +
    // item-gated-exit requirements alike).
    private static RoomDetailLink ItemLink(AppServices services, int itemId)
    {
        string itemName = services.ItemNames.GetName(itemId) ?? $"item #{itemId}";
        return new RoomDetailLink(itemName, null,
            new RelayCommand(() => services.OpenItemGameData(itemId)));
    }
}

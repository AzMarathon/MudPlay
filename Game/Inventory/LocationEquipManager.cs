using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Inventory;

// Wears a configured item while the character is inside a map area a Settings →
// Other location rule matches (by map/room number(s) and/or a room-name
// substring), and reverts the slot to the gear set's item on the way out.
//
// Driven purely by RoomTracker room transitions, so it fires the same whether
// the player, the walker, a loop, or Auto-Lair moved us — the movement runners
// honour it for free. The non-clobber half lives in EquipmentManager: an owned
// slot is skipped by gear-set applies until released here.
public sealed class LocationEquipManager
{
    // Shares the Equipment log category so a reader sees the swap alongside the
    // gear-set applies it coordinates with.
    public const string LogCategory = EquipmentManager.LogCategory;

    private readonly Func<OtherSettings> _readOther;
    private readonly EquipmentManager _equipment;
    private readonly LogService? _log;

    // Location items we currently own a slot for (case-insensitive). Added when a
    // rule's area is entered and the item is actually equipped; removed (and the
    // slot reverted) once no enabled rule keeps it active.
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);

    // The location items currently worn under an active rule — for the bug
    // report, so a "mask didn't go on / didn't come off" report shows what the
    // manager believes it owns.
    public IReadOnlyList<string> ActiveItemsSnapshot() => _active.ToList();

    public LocationEquipManager(
        Func<OtherSettings> readOther, EquipmentManager equipment, LogService? log = null)
    {
        _readOther = readOther ?? throw new ArgumentNullException(nameof(readOther));
        _equipment = equipment ?? throw new ArgumentNullException(nameof(equipment));
        _log = log;
    }

    // Re-evaluate the location rules against the room we've just entered. Wears
    // newly-matched items (when carried) and reverts items whose area we've left.
    // A null (unknown / Lost) room holds the current state rather than stripping
    // gear on a transient localization loss.
    public void OnRoomChanged(Room? newRoom)
    {
        if (newRoom is not { } room) return;

        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LocationEquipRule rule in _readOther().LocationEquipRules)
        {
            if (!rule.Enabled) continue;
            string item = rule.ItemName?.Trim() ?? "";
            if (item.Length == 0) continue;
            if (Matches(rule, room)) desired.Add(item);
        }

        // Release items no longer wanted (left the area / rule disabled / edited
        // out) — EquipmentManager reverts the slot to the current gear set's item.
        foreach (string item in _active.Where(a => !desired.Contains(a)).ToList())
        {
            _equipment.ClearSlotOverride(item);
            _active.Remove(item);
        }

        // Claim newly-wanted items. Re-tried on every step inside a same-named
        // area until the item is actually carried, so a mask picked up after
        // entering still goes on; SetSlotOverride is idempotent once owned.
        foreach (string item in desired)
        {
            if (_active.Contains(item)) continue;
            if (_equipment.SetSlotOverride(item)) _active.Add(item);
        }
    }

    // Drop all ownership on a profile swap — the new character's gear + rules are
    // unrelated. No revert commands (a swap generally follows a reconnect, where
    // the old character's worn state no longer exists to revert).
    public void OnProfileSwapped()
    {
        if (_active.Count > 0)
            _log?.Info(LogCategory, "location-equip: profile swap — dropping active location gear ownership");
        _active.Clear();
        _equipment.ForgetSlotOverrides();
    }

    // A rule matches when each of its non-empty criteria matches; when BOTH are
    // filled they combine per Match (And/Or). A rule with neither criterion never
    // matches (there's nothing to anchor it to an area).
    internal static bool Matches(LocationEquipRule rule, Room room)
    {
        bool hasRooms = ParseRoomKeys(rule.MapRoomNumbers, out HashSet<RoomKey> exact, out HashSet<int> bareRooms);
        string namePart = rule.RoomNameContains?.Trim() ?? "";
        bool hasName = namePart.Length > 0;
        if (!hasRooms && !hasName) return false;

        bool roomMatch = hasRooms && (exact.Contains(room.Key) || bareRooms.Contains(room.Key.Room));
        bool nameMatch = hasName
            && !string.IsNullOrEmpty(room.Name)
            && room.Name.Contains(namePart, StringComparison.OrdinalIgnoreCase);

        if (hasRooms && hasName)
            return rule.Match == LocationEquipMatchMode.And ? roomMatch && nameMatch : roomMatch || nameMatch;
        return hasRooms ? roomMatch : nameMatch;
    }

    // Parse "16/153, 154 12/9" into exact (map/room) keys and bare room numbers.
    // A "map/room" token is an exact key; a bare number matches that room in any
    // map. Tolerant of comma / whitespace / semicolon separators. Returns false
    // when nothing parseable was found.
    internal static bool ParseRoomKeys(string? text, out HashSet<RoomKey> exact, out HashSet<int> bareRooms)
    {
        exact = new HashSet<RoomKey>();
        bareRooms = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (string tok in text.Split(
                     new[] { ',', ' ', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (RoomKey.TryParseWire(tok, out RoomKey key)) exact.Add(key);
            else if (int.TryParse(tok, out int room) && room > 0) bareRooms.Add(room);
        }
        return exact.Count > 0 || bareRooms.Count > 0;
    }
}

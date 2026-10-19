using System.Collections.Generic;
using MudPlay.Game.Map;

namespace MudPlay.ViewModels.GameData.Tables;

// One bare `(Key: N)` lock side as the Doors tab shows it: where it is, its key, what
// the client takes it for, and where that comes from.
public sealed record DoorsRowInfo(
    RoomKey Room,
    string? RoomName,
    Direction Dir,
    int KeyItemId,
    string KeyName,
    BareLockBash Kind,
    bool? Seed,
    BareKeyLockBook.Tally? Record)
{
    // Every bare key lock on the map, by room then direction.
    public static IReadOnlyList<DoorsRowInfo> AllOn(IEnumerable<Room> rooms, BareKeyLockBook book, Func<int, string?> itemName)
    {
        ArgumentNullException.ThrowIfNull(rooms);
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(itemName);
        List<DoorsRowInfo> sides = new();
        foreach (Room room in rooms)
            foreach ((Direction dir, RoomExit exit) in room.Exits)
                if (exit.IsBareKeyLock)
                    sides.Add(new DoorsRowInfo(room.Key, room.Name, dir, exit.KeyItemId,
                        itemName(exit.KeyItemId) is { Length: > 0 } name ? name : $"item #{exit.KeyItemId}",
                        book.Lookup(room.Key, dir), book.SeedSays(room.Key, dir), book.RecordOf(room.Key, dir)));
        sides.Sort((a, b) => a.Room.Map != b.Room.Map ? a.Room.Map.CompareTo(b.Room.Map)
            : a.Room.Room != b.Room.Room ? a.Room.Room.CompareTo(b.Room.Room)
            : a.Dir.CompareTo(b.Dir));
        return sides;
    }

    public static string KindLabel(BareLockBash kind) => kind switch
    {
        BareLockBash.Bashable => "door or gate (bashes)",
        BareLockBash.NotBashable => "key exit (no bash)",
        _ => "not known",
    };

    public string KindText => KindLabel(Kind);

    // Where the kind comes from: the user's edit, the seed, or the game's answers on
    // this realm (which overturn the seed only on two agreeing answers).
    public string Source
    {
        get
        {
            if (Record?.UserKind is not null) return "set by you";
            bool? seedKind = Kind switch
            {
                BareLockBash.Bashable => true,
                BareLockBash.NotBashable => false,
                _ => null,
            };
            if (Seed is { } seed && seedKind == seed) return "seed";
            if (Record is { } t && t.Door + t.KeyExit > 0) return "learned";
            return "—";
        }
    }

    // "2 as a door, 0 as a key exit", or empty when the game hasn't answered here.
    public string Answers => Record is { } t && t.Door + t.KeyExit > 0
        ? $"{t.Door} as a door, {t.KeyExit} as a key exit" : string.Empty;

    public string LastAnswered => Record?.LastAnswered is { } at ? at.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
}

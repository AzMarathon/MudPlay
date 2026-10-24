using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MudPlay.Services;

// A starter GOTO that shipped pointing at the wrong room, and the room it should
// have pointed at. Listed per realm in the bundle's FavoriteCorrections.json,
// beside the Favorites.json it corrects, so the next one is a line of data.
//
// Fixing the bundle alone reaches only new installs: an existing set's
// Favorites.json and every character's own list already hold the wrong room, and
// the nav-seed ledger would offer the fixed one as a new favourite beside it.
// NavSeedBootstrapper applies these to the set (and its ledger), FavoritesStore to
// each character as it loads; each records the Id so a correction runs once.
//
// A favourite is corrected only when both its room and its label are the shipped
// ones: a room the user bookmarked under a label of their own is theirs.
internal sealed record FavoriteCorrection(string Label, int FromMap, int FromRoom, int ToMap, int ToRoom)
{
    public enum Outcome { None, Moved, Dropped }

    // One favourite as ApplyTo needs to see it, whatever form the list keeps it in.
    public readonly record struct Entry(int Map, int Room, string? Label, bool Starred);

    public string Id => $"{FromMap}/{FromRoom}|{Label}>{ToMap}/{ToRoom}";

    public bool IsShipped(int map, int room, string? label) =>
        map == FromMap && room == FromRoom && string.Equals(label, Label, StringComparison.Ordinal);

    public string Describe => $"'{Label}' {FromMap}/{FromRoom} → {ToMap}/{ToRoom}";

    public const string FileName = "FavoriteCorrections.json";

    // The corrections shipped in a realm's nav-seed folder; none when it has no file.
    // A file that won't parse throws (InvalidDataException), as a bad bundle should.
    public static IReadOnlyList<FavoriteCorrection> Load(string bundleDir) =>
        JsonStore.Load<List<FavoriteCorrection>>(Path.Combine(bundleDir, FileName)) ?? [];

    // Move the shipped favourite in list to the corrected room. When the list already
    // has a favourite there, that one is kept and the shipped one dropped, so the
    // place isn't listed twice; a star on the dropped one carries over. Generic over
    // the item so the set's file can stay JSON nodes, keeping fields this version
    // doesn't know, while a character's list is FavoriteRooms.
    public Outcome ApplyTo<T>(IList<T> list, Func<T, Entry> read, Action<T, int, int> move, Action<T> star)
        where T : class?
    {
        List<T> stale = list.Where(f => read(f) is var e && IsShipped(e.Map, e.Room, e.Label)).ToList();
        if (stale.Count == 0) return Outcome.None;
        T? target = list.FirstOrDefault(f => read(f) is var e && e.Map == ToMap && e.Room == ToRoom);
        Outcome outcome = target is null ? Outcome.Moved : Outcome.Dropped;
        bool starred = stale.Any(f => read(f).Starred);
        foreach (T f in stale)
        {
            if (target is null)
            {
                move(f, ToMap, ToRoom);
                target = f;
            }
            else list.Remove(f);
        }
        if (starred) star(target!);
        return outcome;
    }
}

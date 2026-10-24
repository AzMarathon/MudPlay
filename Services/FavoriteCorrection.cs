using System.Collections.Generic;
using System.IO;

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
    public string Id => $"{FromMap}/{FromRoom}|{Label}>{ToMap}/{ToRoom}";

    public bool IsShipped(int map, int room, string? label) =>
        map == FromMap && room == FromRoom && string.Equals(label, Label, StringComparison.Ordinal);

    public string Describe => $"'{Label}' {FromMap}/{FromRoom} → {ToMap}/{ToRoom}";

    public const string FileName = "FavoriteCorrections.json";

    // The corrections shipped in a realm's nav-seed folder; none when it has no file.
    // A file that won't parse throws (InvalidDataException), as a bad bundle should.
    public static IReadOnlyList<FavoriteCorrection> Load(string bundleDir) =>
        JsonStore.Load<List<FavoriteCorrection>>(Path.Combine(bundleDir, FileName)) ?? [];
}

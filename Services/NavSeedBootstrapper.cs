using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MudPlay.Services;

// One-shot bootstrap that seeds a freshly-imported game-data set with base
// navigation loops + GOTO favourites from the realm-matched bundle shipped under
// Defaults/nav-seed/{realm}/. Called from the MDB import path (before the set is
// switched active) so the subsequent set-switch loads the seeded files.
//
// Rules: additive (never overwrites an existing loop file or drops an existing
// favourite), realm-matched (stock vs paradigm via Info.json Legit), and once-only
// (a per-set .nav-seeded marker means re-imports and user deletions never re-add
// the seed). Best-effort — any failure logs and leaves the set unseeded rather
// than blocking the import.
public static class NavSeedBootstrapper
{
    public static void SeedIfNeeded(string setName, LogService? log = null)
    {
        if (string.IsNullOrWhiteSpace(setName)) return;
        string marker = AppPaths.NavSeedMarkerFile(setName);
        if (File.Exists(marker)) return;   // already seeded — deletions stay deleted

        string realm = GameDataRealm.Resolve(setName);
        string bundle = AppPaths.BundledNavSeedDir(realm);
        if (!Directory.Exists(bundle))
        {
            // Dev build or no bundle for this realm: skip WITHOUT marking, so a
            // later build that ships the bundle can still seed this set.
            log?.Log(LogSeverity.Info, "NavSeed",
                $"No seed bundle for realm '{realm}' at '{bundle}'; set '{setName}' left unseeded.");
            return;
        }

        try
        {
            int loops = CopyLoops(Path.Combine(bundle, "Loops"), AppPaths.GameDataSetLoopsFolder(setName));
            int favs = MergeFavourites(Path.Combine(bundle, "Favorites.json"), AppPaths.GameDataSetFavoritesFile(setName));
            File.WriteAllText(marker, $"realm={realm}\n");   // sentinel; contents informational only
            log?.Log(LogSeverity.Info, "NavSeed",
                $"Seeded set '{setName}' ({realm}): {loops} loop(s) + {favs} favourite(s).");
        }
        catch (Exception ex)
        {
            log?.Log(LogSeverity.Warn, "NavSeed",
                $"Failed to seed set '{setName}' from realm '{realm}': {ex.Message}");
        }
    }

    // Recursively copy *.loop from src into dst, preserving the sub-folder tree and
    // NEVER overwriting a file that already exists. Returns the count copied.
    private static int CopyLoops(string src, string dst)
    {
        if (!Directory.Exists(src)) return 0;
        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(src, "*.loop", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dst, Path.GetRelativePath(src, file));
            if (File.Exists(target)) continue;   // preserve any loop already there
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
            copied++;
        }
        return copied;
    }

    // Union the bundle's Favorites.json into the set's. On a fresh import the set
    // has none, so it's a plain copy; otherwise dedupe-merge the Favorites (by
    // map/room/label/folder) and FavoriteFolders (by name) so nothing existing is
    // lost. Returns the number of favourite rows added.
    private static int MergeFavourites(string src, string dst)
    {
        if (!File.Exists(src)) return 0;
        if (!File.Exists(dst))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst);
            return (JsonNode.Parse(File.ReadAllText(src))?["Favorites"] as JsonArray)?.Count ?? 0;
        }

        JsonObject seed = JsonNode.Parse(File.ReadAllText(src))?.AsObject() ?? new JsonObject();
        JsonObject cur  = JsonNode.Parse(File.ReadAllText(dst))?.AsObject() ?? new JsonObject();

        JsonArray curFavs = cur["Favorites"] as JsonArray ?? new JsonArray();
        var have = new HashSet<string>(curFavs.Select(FavKey));
        int added = 0;
        foreach (JsonNode? f in (seed["Favorites"] as JsonArray) ?? new JsonArray())
        {
            if (f is null || !have.Add(FavKey(f))) continue;
            curFavs.Add(f.DeepClone());
            added++;
        }
        cur["Favorites"] = curFavs;

        JsonArray curFolders = cur["FavoriteFolders"] as JsonArray ?? new JsonArray();
        var haveFolders = new HashSet<string>(curFolders.Select(x => x?.ToString() ?? ""));
        foreach (JsonNode? fld in (seed["FavoriteFolders"] as JsonArray) ?? new JsonArray())
        {
            string name = fld?.ToString() ?? "";
            if (name.Length > 0 && haveFolders.Add(name)) curFolders.Add(name);
        }
        cur["FavoriteFolders"] = curFolders;

        File.WriteAllText(dst, cur.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return added;
    }

    private static string FavKey(JsonNode? f) =>
        $"{f?["Map"]}/{f?["Room"]}|{f?["Label"]}|{f?["Folder"]}";
}

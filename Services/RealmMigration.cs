using System.IO;
using System.Text.Json;
using MudPlay.Models.Settings;

namespace MudPlay.Services;

// One-time move of a BBS from "the BBS is the realm" to named realms. A bbs.json
// with no Realms gets one realm named after the BBS, built from the realm fields
// the BBS used to carry, and the data the client collected on the board moves from
// the BBS folder into that realm's folder. Boss kill-times, which used to live with
// the game-data set, are copied into it. Realms added later start empty.
internal static class RealmMigration
{
    // Files that used to sit in BBS/{bbs}/ and now belong to the realm.
    private static readonly string[] RealmFiles =
    {
        "players.json", "room_blacklist.json", "leaderboard.json",
        "roomba.json", "roomba_items.json", "quests.json",
    };

    // Build the first realm for a BBS loaded without one. rawJson is its bbs.json
    // text (for the realm fields the BBS used to carry); defaultGameDataSet is the
    // Global fallback set, used to find boss timers when the BBS named no set.
    public static RealmProfile CreateFirstRealm(
        string bbsName, string? rawJson, string? defaultGameDataSet)
    {
        RealmProfile realm = FromLegacyFields(bbsName, rawJson);
        MoveBbsData(bbsName, realm, defaultGameDataSet);
        return realm;
    }

    private static RealmProfile FromLegacyFields(string bbsName, string? rawJson)
    {
        RealmProfile realm = new() { Name = bbsName };
        if (string.IsNullOrWhiteSpace(rawJson)) return realm;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            JsonElement root = doc.RootElement;
            if (Str(root, "ActiveGameDataSet") is { } set) realm.ActiveGameDataSet = set;
            if (Str(root, "GameEntryCommand") is { } entry) realm.GameEntryCommand = entry;
            if (Str(root, "GameExitCommand") is { } exit) realm.GameExitCommand = exit;
            if (Int(root, "PlayerDiesAtHp") is { } floor) realm.PlayerDiesAtHp = floor;
            if (Bool(root, "AutoRefineDeathFloor") is { } refine) realm.AutoRefineDeathFloor = refine;
            if (Str(root, "CleanupTimeOfDay") is { } tod) realm.CleanupTimeOfDay = tod;
            if (Str(root, "CleanupTimeZoneId") is { } tz) realm.CleanupTimeZoneId = tz;
            if (Str(root, "RunicCurrencyName") is { } runic) realm.RunicCurrencyName = runic;
        }
        catch (JsonException)
        {
            // A bbs.json that won't parse loaded as defaults anyway; the realm does too.
        }
        return realm;
    }

    private static void MoveBbsData(string bbsName, RealmProfile realm, string? defaultGameDataSet)
    {
        string bbsFolder = AppPaths.BbsFolder(bbsName);
        string realmFolder = AppPaths.RealmFolder(bbsName, realm.Name);
        Directory.CreateDirectory(realmFolder);

        foreach (string name in RealmFiles)
            MoveIfPresent(Path.Combine(bbsFolder, name), Path.Combine(realmFolder, name));
        // "Only for this BBS" game-data edits become "only for this realm".
        if (Directory.Exists(bbsFolder))
            foreach (string path in Directory.EnumerateFiles(bbsFolder, "*_overrides.*.json"))
                MoveIfPresent(path, Path.Combine(realmFolder, Path.GetFileName(path)));

        string? set = realm.ActiveGameDataSet ?? defaultGameDataSet;
        string timers = AppPaths.RealmBossTimersFile(realmFolder);
        if (!string.IsNullOrWhiteSpace(set) && !File.Exists(timers)
            && File.Exists(AppPaths.LegacySetBossTimersFile(set)))
            Tolerate(() => File.Copy(AppPaths.LegacySetBossTimersFile(set), timers));
    }

    private static void MoveIfPresent(string from, string to)
    {
        if (File.Exists(from) && !File.Exists(to)) Tolerate(() => File.Move(from, to));
    }

    // Several clients can load the same BBS at once and race to migrate it; the
    // one that loses finds the file already moved (or its target already there).
    private static void Tolerate(Action fileOp)
    {
        try { fileOp(); }
        catch (IOException)
        {
            // Another client moved it first — the data is where it belongs.
        }
    }

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
            ? i : null;

    private static bool? Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;
}

using System.IO;
using System.Text.Json;

namespace MudPlay.Services;

// Classifies a game-data set as "stock" or "paradigm" from its Info.json Legit
// field (2 = paradigm / GreaterMUD, anything else = stock), and names the board it
// was exported for. Works by set NAME off
// the folder on disk, so a set that isn't the active one can still be classified
// (e.g. right after import). The realm strings match the {stock,paradigm} naming
// used by the bundled seed files and folders.
//
// The two overlay-seed stores (MonsterOverlaySeedStore / ItemOverlaySeedStore)
// carry their own private copies of this; they predate this helper and can migrate
// to it in a later pass.
public static class GameDataRealm
{
    public static string Resolve(string setName)
    {
        int? legit = ReadInfo(setName, info =>
            info.TryGetProperty("Legit", out JsonElement value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int code) ? code : (int?)null);
        return legit == 2 ? "paradigm" : "stock";
    }

    // The board the set was exported for, from its Info "Custom" field ("Anomaly",
    // "Paradigm"; the 1.11p export says "Default"), or null when the set has none.
    public static string? BoardName(string? setName) =>
        ReadInfo(setName, info =>
            info.TryGetProperty("Custom", out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Replace("\0", "").Trim() is { Length: > 0 } name ? name : null
                : null);

    private static T? ReadInfo<T>(string? setName, Func<JsonElement, T?> read)
    {
        if (string.IsNullOrWhiteSpace(setName)) return default;
        string infoPath = Path.Combine(AppPaths.GameDataSetDir(setName), "Info.json");
        if (!File.Exists(infoPath)) return default;
        try
        {
            using FileStream fs = File.OpenRead(infoPath);
            using JsonDocument doc = JsonDocument.Parse(fs);
            // Info.json is a one-element array holding the export metadata.
            JsonElement root = doc.RootElement;
            JsonElement first = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0] : root;
            return read(first);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Malformed or unreadable Info.json — fall through to the safe default.
            return default;
        }
    }
}

using System.IO;
using System.Text.Json;

namespace MudPlay.Services;

// Classifies a game-data set as "stock" or "paradigm" from its Info.json Legit
// field (2 = paradigm / GreaterMUD, anything else = stock). Works by set NAME off
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
        if (string.IsNullOrWhiteSpace(setName)) return "stock";
        string infoPath = Path.Combine(AppPaths.GameDataSetDir(setName), "Info.json");
        if (!File.Exists(infoPath)) return "stock";
        try
        {
            using FileStream fs = File.OpenRead(infoPath);
            using JsonDocument doc = JsonDocument.Parse(fs);
            // Info.json is a one-element array holding the export metadata.
            JsonElement root = doc.RootElement;
            JsonElement first = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0] : root;
            if (first.TryGetProperty("Legit", out JsonElement legit) && legit.TryGetInt32(out int code))
                return code == 2 ? "paradigm" : "stock";
        }
        catch
        {
            // Malformed Info.json — fall through to the safe default.
        }
        return "stock";
    }
}

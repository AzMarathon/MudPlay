using System.Text.Json.Nodes;

namespace MudPlay.Services;

// Carries this client's changes onto a file another client has rewritten since this
// one read it. Given the file as this client last knew it (the baseline), this
// client's copy now (mine) and the file as it stands (theirs), the result is theirs
// with only what differs between baseline and mine applied. Objects merge a
// property at a time, to any depth; anything else (a value, an array) is taken
// whole from whichever side changed it, and from this client when both did.
//
// For the settings files several clients share (global.json, a BBS's bbs.json),
// where saving this client's whole copy undid whatever another had saved meanwhile.
public static class JsonThreeWayMerge
{
    public static JsonNode? Merge(JsonNode? baseline, JsonNode? mine, JsonNode? theirs)
    {
        if (JsonNode.DeepEquals(mine, baseline)) return theirs?.DeepClone();
        if (mine is not JsonObject mineObject || theirs is not JsonObject theirsObject)
            return mine?.DeepClone();

        JsonObject baseObject = baseline as JsonObject ?? new JsonObject();
        JsonObject merged = new();
        foreach ((string name, JsonNode? theirValue) in theirsObject)
        {
            bool mineHas = mineObject.TryGetPropertyValue(name, out JsonNode? mineValue);
            bool baseHad = baseObject.TryGetPropertyValue(name, out JsonNode? baseValue);
            if (!mineHas)
            {
                // Removed here, unless this client never had it: then it is theirs.
                if (!baseHad) merged[name] = theirValue?.DeepClone();
                continue;
            }
            merged[name] = Merge(baseValue, mineValue, theirValue);
        }
        foreach ((string name, JsonNode? mineValue) in mineObject)
        {
            if (theirsObject.ContainsKey(name)) continue;
            bool baseHad = baseObject.TryGetPropertyValue(name, out JsonNode? baseValue);
            // Theirs lacks it: added here, or changed here after they removed it.
            // Unchanged here since the baseline means they removed it, and it goes.
            if (!baseHad || !JsonNode.DeepEquals(mineValue, baseValue))
                merged[name] = mineValue?.DeepClone();
        }
        return merged;
    }
}

using System.Text.Json.Nodes;

namespace MudPlay.Services;

// Carries this client's changes onto a file another client has rewritten since this
// one read it. Given the file as this client last knew it (the baseline), this
// client's copy now (mine) and the file as it stands (theirs), the result is theirs
// with only what differs between baseline and mine applied. Objects merge a
// property at a time, to any depth; anything else (a value, an array) is taken
// whole from whichever side changed it, and from this client when both did.
//
// An array of records named in keyedArrays (property name → the field that names
// each record) merges a record at a time instead, each record as an object: a BBS's
// realms, where the Settings window holds a copy for as long as it is open and the
// running client writes a realm's counts meanwhile, and taking the window's array
// whole put the counts back as they were when it opened.
//
// For the settings files several clients share (global.json, a BBS's bbs.json),
// where saving this client's whole copy undid whatever another had saved meanwhile.
public static class JsonThreeWayMerge
{
    public static JsonNode? Merge(
        JsonNode? baseline, JsonNode? mine, JsonNode? theirs, IReadOnlyDictionary<string, string>? keyedArrays = null)
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
            merged[name] = keyedArrays is not null && keyedArrays.TryGetValue(name, out string? key)
                && MergeKeyed(baseValue, mineValue, theirValue, key, keyedArrays) is { } records
                    ? records
                    : Merge(baseValue, mineValue, theirValue, keyedArrays);
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

    // Null when any side isn't an array of records each with its own name, and the
    // array is then merged whole like any other.
    private static JsonArray? MergeKeyed(
        JsonNode? baseline, JsonNode? mine, JsonNode? theirs, string key, IReadOnlyDictionary<string, string> keyedArrays)
    {
        if (JsonNode.DeepEquals(mine, baseline)) return null;
        if (Records(mine, key) is not { } mineRecords || Records(theirs, key) is not { } theirRecords) return null;
        List<(string Name, JsonObject Record)>? baseRecords = baseline is null ? new() : Records(baseline, key);
        if (baseRecords is null) return null;

        Dictionary<string, JsonObject> baseByName = ByName(baseRecords), mineByName = ByName(mineRecords),
            theirByName = ByName(theirRecords);

        // The order is this client's when it added, removed, renamed or moved a
        // record (a realm's place says which is the board's first), and theirs
        // otherwise; records only one side added go after the rest.
        bool mineReshaped = !mineRecords.Select(r => r.Name)
            .SequenceEqual(baseRecords.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> order = mineReshaped
            ? mineRecords.Select(r => r.Name).Concat(theirRecords.Select(r => r.Name))
            : theirRecords.Select(r => r.Name).Concat(mineRecords.Select(r => r.Name));

        JsonArray merged = new();
        HashSet<string> done = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in order)
        {
            if (!done.Add(name)) continue;
            bool baseHad = baseByName.TryGetValue(name, out JsonObject? baseRecord);
            bool mineHas = mineByName.TryGetValue(name, out JsonObject? mineRecord);
            bool theyHave = theirByName.TryGetValue(name, out JsonObject? theirRecord);
            // A record removed on either side stays removed, though the other
            // changed it: a realm deleted in Settings takes its characters and data
            // with it, and a count written to it meanwhile must not bring it back.
            if (baseHad && (!mineHas || !theyHave)) continue;
            merged.Add(mineHas && theyHave
                ? Merge(baseRecord, mineRecord, theirRecord, keyedArrays)
                : (mineRecord ?? theirRecord)!.DeepClone());
        }
        return merged;
    }

    // The records of an array, each with its name, or null when it isn't an array
    // of objects with distinct string names.
    private static List<(string Name, JsonObject Record)>? Records(JsonNode? node, string key)
    {
        if (node is not JsonArray array) return null;
        List<(string, JsonObject)> records = new(array.Count);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? item in array)
        {
            if (item is not JsonObject record
                || !record.TryGetPropertyValue(key, out JsonNode? nameNode)
                || nameNode is not JsonValue value
                || !value.TryGetValue(out string? name)
                || !names.Add(name))
                return null;
            records.Add((name, record));
        }
        return records;
    }

    private static Dictionary<string, JsonObject> ByName(List<(string Name, JsonObject Record)> records) =>
        records.ToDictionary(r => r.Name, r => r.Record, StringComparer.OrdinalIgnoreCase);
}

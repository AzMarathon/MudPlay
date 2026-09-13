using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MudPlay.Services.Update;

// Pure parsing + version comparison for the updater. Turns the GitHub
// releases/latest JSON into an UpdateRelease, reads a SHA256SUMS.txt into a
// filename→hash map, and decides whether a tag names a newer build than what's
// running. No I/O — the service fetches the strings and hands them here.
public static class ReleaseParser
{
    // Parse the GitHub /releases/latest payload. Returns null on malformed JSON or a
    // missing tag. Assets with no name or download URL are skipped.
    public static UpdateRelease? ParseRelease(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? rawTag = GetString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(rawTag)) return null;
            if (!TryParseVersion(rawTag, out string version)) return null;

            string htmlUrl = GetString(root, "html_url") ?? string.Empty;
            string? notes = GetString(root, "body");

            var assets = new List<UpdateAsset>();
            if (root.TryGetProperty("assets", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement a in arr.EnumerateArray())
                {
                    string? name = GetString(a, "name");
                    string? url = GetString(a, "browser_download_url");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
                    long size = a.TryGetProperty("size", out JsonElement s) && s.ValueKind == JsonValueKind.Number
                        && s.TryGetInt64(out long n) ? n : 0;
                    assets.Add(new UpdateAsset(name!, url!, size));
                }
            }
            return new UpdateRelease(rawTag!, version, notes, htmlUrl, assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Parse a SHA256SUMS.txt (standard `sha256sum` output: "<hex>  <filename>", two
    // spaces, or "<hex> *<filename>" binary mode) into a filename→hash map. Unparsable
    // lines are skipped; last write wins on a duplicate filename.
    public static IReadOnlyDictionary<string, string> ParseSha256Sums(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return map;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0) continue;
            // hash then filename, separated by whitespace; the filename may carry a
            // leading '*' (binary-mode marker) and may itself contain spaces, so take
            // the first token as the hash and the rest (trimmed) as the name.
            int sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            string hash = line[..sp].Trim();
            string name = line[(sp + 1)..].Trim().TrimStart('*').Trim();
            if (hash.Length == 0 || name.Length == 0) continue;
            map[name] = hash.ToLowerInvariant();
        }
        return map;
    }

    // Strip a leading 'v'/'V' from a release tag and validate it parses as a numeric
    // version (e.g. "v3.78.0" → "3.78.0"). Returns false for a non-numeric tag.
    public static bool TryParseVersion(string? tag, out string version)
    {
        version = string.Empty;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        string t = tag.Trim();
        if (t.StartsWith("v", StringComparison.OrdinalIgnoreCase)) t = t[1..];
        if (!System.Version.TryParse(t, out Version? v) || v is null) return false;
        version = t;
        return true;
    }

    // True when latest is a strictly higher version than current. Both are the plain
    // numeric strings (no 'v'); an unparsable side makes this false (never offer an
    // update we can't reason about).
    public static bool IsNewer(string? current, string? latest)
    {
        if (!System.Version.TryParse(Normalize(current), out Version? c) || c is null) return false;
        if (!System.Version.TryParse(Normalize(latest), out Version? l) || l is null) return false;
        return l > c;
    }

    private static string? Normalize(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return v;
        string s = v.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        // AssemblyInformationalVersion can carry a +commit suffix; drop it.
        int plus = s.IndexOf('+');
        return plus >= 0 ? s[..plus] : s;
    }

    private static string? GetString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}

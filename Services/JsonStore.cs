using System.Text.Json;
using System.Text.Json.Serialization;

namespace MudPlay.Services;

// Shared System.Text.Json plumbing used by every settings/profile service so
// the on-disk JSON looks identical no matter who wrote it: indented for
// human edits, comment-tolerant on read, missing files return null instead
// of throwing.
internal static class JsonStore
{
    // Serializer options shared by all stores. Indented for readable diffs;
    // comments tolerated on load so users can annotate hand-edited files.
    // Enum values serialize as their member name (e.g. "Ignore",
    // "Poisoned, Confused") instead of the raw numeric backing — hand-edits
    // are far easier with names, and the converter still accepts numeric
    // values on read (allowIntegerValues defaults to true) so any legacy
    // file or generated payload keeps loading.
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    // Read and deserialize T from path. Returns null if the file does not
    // exist. Throws with the file path in the message if the JSON is
    // malformed — corrupt configuration is loud, not silent.
    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;

        string json = File.ReadAllText(path);
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Failed to parse {typeof(T).Name} from '{path}': {ex.Message}", ex);
        }
    }

    // Serialize value to JSON and write it to path, creating any missing
    // parent directories. Write is atomic via temp-file + rename so a crash
    // mid-write can't leave a half-written file on disk.
    public static void Save<T>(string path, T value) where T : class
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Atomic write: serialize to a UNIQUE sibling temp file, then rename over the
        // target so a reader never sees a half-written file. A per-write temp name
        // (pid + guid) keeps two writers racing on the SAME target from colliding on
        // one temp path — the case that crashed two client instances sharing a
        // realm-wide file (boss-timers.json): both wrote "…json.tmp", the first
        // File.Move renamed it away, and the second threw FileNotFoundException on the
        // now-gone temp. With distinct temps each rename just overwrites the target.
        string tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        string json = JsonSerializer.Serialize(value, Options);
        try
        {
            File.WriteAllText(tmp, json);
            MoveWithRetry(tmp, path);
        }
        catch
        {
            // Don't leave our temp behind on a failed write; ignore if it's already
            // gone. The original error still propagates to the caller.
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    // Rename src over dst, retrying briefly on the transient error a concurrent
    // replace of the same destination raises. POSIX rename() is atomic and never
    // collides, but Windows MoveFileEx locks the target for the swap, so two
    // near-simultaneous replaces (two client instances persisting a shared file)
    // can throw UnauthorizedAccessException / a sharing IOException. A handful of
    // short retries clears that sub-millisecond window; a persistent failure (disk
    // full, real permission denial) still surfaces on the final attempt.
    private static void MoveWithRetry(string src, string dst)
    {
        const int attempts = 20;
        for (int i = 1; ; i++)
        {
            try
            {
                File.Move(src, dst, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && i < attempts)
            {
                Thread.Sleep(Math.Min(i, 10) * 3);
            }
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using MudPlay.Game.Emotes;

namespace MudPlay.Services;

// One user-defined emote as persisted in the manifest. Exactly one of Emoji / Image
// is set: Emoji is a Unicode string, Image is a file name (not a path) under
// AppPaths.EmotesDir. Identity is Shortcode (the bare ":name:" token, no colons).
public sealed class UserEmote
{
    public string Shortcode { get; set; } = "";
    public string? Emoji { get; set; }
    public string? Image { get; set; }
    public string? DisplayName { get; set; }
}

// The user's personal, shareable emote set (Global tier): a JSON manifest plus copied
// image files under AppPaths.EmotesDir. Layers on top of the built-in catalog and
// publishes the merged scanner to EmoteRuntime so the conversation window renders both.
// Add/remove and import/export mutate the set, re-save, and republish + raise Changed.
public sealed partial class EmoteStore
{
    private readonly string _dir;
    private readonly string _manifestPath;
    private readonly LogService? _log;
    private List<UserEmote> _emotes = new();

    public EmoteStore(string? dir = null, LogService? log = null)
    {
        _dir = dir ?? AppPaths.EmotesDir;
        _manifestPath = Path.Combine(_dir, "emotes.json");
        _log = log;
        Directory.CreateDirectory(_dir);
        Load();
        Republish();
    }

    // Raised after the set changes (add / remove / import) so open conversation
    // windows re-render against the new catalog.
    public event System.Action? Changed;

    public IReadOnlyList<UserEmote> Emotes => _emotes;

    [GeneratedRegex(@"^[A-Za-z0-9_+\-]+$")]
    private static partial Regex ShortcodeShape();

    // Strip surrounding colons / whitespace and validate the shape (the same token
    // class the scanner matches). Returns null when the name can't be a shortcode.
    public static string? NormalizeShortcode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim().Trim(':').Trim();
        return s.Length > 0 && ShortcodeShape().IsMatch(s) ? s : null;
    }

    // Add / replace a Unicode-emoji user emote. Returns false when the shortcode is
    // malformed. An existing image emote of the same name is replaced (its file freed).
    public bool AddUnicode(string shortcode, string emoji, string? display = null)
    {
        if (NormalizeShortcode(shortcode) is not { } name || string.IsNullOrEmpty(emoji)) return false;
        RemoveInternal(name, deleteImage: true);
        _emotes.Add(new UserEmote { Shortcode = name, Emoji = emoji, DisplayName = display });
        Save();
        Republish();
        return true;
    }

    // Add / replace an image user emote by copying sourceImagePath into EmotesDir.
    // Returns false when the shortcode is malformed or the copy fails.
    public bool AddImage(string shortcode, string sourceImagePath, string? display = null)
    {
        if (NormalizeShortcode(shortcode) is not { } name) return false;
        if (string.IsNullOrWhiteSpace(sourceImagePath) || !File.Exists(sourceImagePath)) return false;

        string ext = Path.GetExtension(sourceImagePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        string file = name + ext.ToLowerInvariant();
        try
        {
            File.Copy(sourceImagePath, Path.Combine(_dir, file), overwrite: true);
        }
        catch (System.Exception ex)
        {
            _log?.Warn("Emotes", $"copy of '{sourceImagePath}' failed: {ex.Message}");
            return false;
        }

        RemoveInternal(name, deleteImage: false);   // replacing; the new file already overwrote
        _emotes.Add(new UserEmote { Shortcode = name, Image = file, DisplayName = display });
        ConversationImageInvalidate?.Invoke(Path.Combine(_dir, file));
        Save();
        Republish();
        return true;
    }

    public bool Remove(string shortcode)
    {
        if (NormalizeShortcode(shortcode) is not { } name) return false;
        if (!RemoveInternal(name, deleteImage: true)) return false;
        Save();
        Republish();
        return true;
    }

    // Write the user set as a single shareable package (a zip of the manifest + image
    // files) to destZipPath. Returns false on I/O failure.
    public bool Export(string destZipPath)
    {
        try
        {
            if (File.Exists(destZipPath)) File.Delete(destZipPath);
            using ZipArchive zip = ZipFile.Open(destZipPath, ZipArchiveMode.Create);
            zip.CreateEntryFromFile(_manifestPath, "emotes.json");
            foreach (string? img in _emotes.Select(e => e.Image).Where(i => !string.IsNullOrEmpty(i)).Distinct())
            {
                string path = Path.Combine(_dir, img!);
                if (File.Exists(path)) zip.CreateEntryFromFile(path, img!);
            }
            return true;
        }
        catch (System.Exception ex)
        {
            _log?.Warn("Emotes", $"export to '{destZipPath}' failed: {ex.Message}");
            return false;
        }
    }

    // Merge a package exported by Export: extract its images into EmotesDir and add its
    // manifest entries (an imported shortcode replaces a same-named user emote). Returns
    // the number of emotes imported, or -1 on failure.
    public int Import(string srcZipPath)
    {
        try
        {
            using ZipArchive zip = ZipFile.OpenRead(srcZipPath);
            ZipArchiveEntry? manifest = zip.GetEntry("emotes.json");
            if (manifest is null) return -1;

            List<UserEmote> incoming;
            using (Stream s = manifest.Open())
                incoming = System.Text.Json.JsonSerializer.Deserialize<List<UserEmote>>(s) ?? new();

            int added = 0;
            foreach (UserEmote e in incoming)
            {
                if (NormalizeShortcode(e.Shortcode) is not { } name) continue;

                if (!string.IsNullOrEmpty(e.Image) && zip.GetEntry(e.Image) is { } imgEntry)
                {
                    string dest = Path.Combine(_dir, e.Image);
                    imgEntry.ExtractToFile(dest, overwrite: true);
                    ConversationImageInvalidate?.Invoke(dest);
                    RemoveInternal(name, deleteImage: false);
                    _emotes.Add(new UserEmote { Shortcode = name, Image = e.Image, DisplayName = e.DisplayName });
                    added++;
                }
                else if (!string.IsNullOrEmpty(e.Emoji))
                {
                    RemoveInternal(name, deleteImage: true);
                    _emotes.Add(new UserEmote { Shortcode = name, Emoji = e.Emoji, DisplayName = e.DisplayName });
                    added++;
                }
            }
            Save();
            Republish();
            return added;
        }
        catch (System.Exception ex)
        {
            _log?.Warn("Emotes", $"import from '{srcZipPath}' failed: {ex.Message}");
            return -1;
        }
    }

    // Bridge to ConversationMessageInlines.EmoteImages.Invalidate — set by AppServices so
    // a replaced / re-imported image drops its cached bitmap. Left null in tests.
    public System.Action<string>? ConversationImageInvalidate { get; set; }

    private bool RemoveInternal(string name, bool deleteImage)
    {
        int idx = _emotes.FindIndex(e => string.Equals(e.Shortcode, name, System.StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;
        UserEmote e = _emotes[idx];
        _emotes.RemoveAt(idx);
        if (deleteImage && !string.IsNullOrEmpty(e.Image)
            && !_emotes.Any(o => string.Equals(o.Image, e.Image, System.StringComparison.OrdinalIgnoreCase)))
        {
            try { File.Delete(Path.Combine(_dir, e.Image)); } catch { /* best effort */ }
        }
        return true;
    }

    // Build the merged built-in + user catalog and publish its scanner.
    private void Republish()
    {
        EmoteCatalog catalog = EmoteCatalog.WithUser(_emotes.Select(ToEmote));
        EmoteRuntime.Publish(new EmoteScanner(catalog));
        Changed?.Invoke();
    }

    private Emote ToEmote(UserEmote e)
    {
        string display = string.IsNullOrWhiteSpace(e.DisplayName) ? e.Shortcode : e.DisplayName!;
        return !string.IsNullOrEmpty(e.Image)
            ? new Emote(e.Shortcode, EmoteKind.Image, Path.Combine(_dir, e.Image), display)
            : new Emote(e.Shortcode, EmoteKind.Unicode, e.Emoji ?? "", display);
    }

    private void Load()
    {
        try { _emotes = JsonStore.Load<List<UserEmote>>(_manifestPath) ?? new(); }
        catch (System.Exception ex)
        {
            _log?.Warn("Emotes", $"failed to load '{_manifestPath}': {ex.Message}");
            _emotes = new();
        }
    }

    private void Save()
    {
        try { JsonStore.Save(_manifestPath, _emotes); }
        catch (System.Exception ex) { _log?.Warn("Emotes", $"failed to save '{_manifestPath}': {ex.Message}"); }
    }
}

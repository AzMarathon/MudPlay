using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MudPlay.Services.Update;

// MudPlay.files: the list of every file a MudPlay release installs, one name per
// line, written beside the executable at publish time (MudPlay.csproj's
// WriteInstallManifest target) and installed with it. It is how the updater knows
// which files in the folder are MudPlay's own: people run MudPlay from Downloads, the
// Desktop or any folder shared with their own files, and an update may replace or
// remove only what a manifest names.
//
// Entries are bare file names beside the executable, never paths. Releases are a
// single-file build, so a flat list is all a release needs, and a name that can't
// climb, nest or reach another drive is one less thing the swap helpers have to
// guard. The character set is held to what a published file is ever called, so a
// name can't carry anything a cmd or bash line would read as syntax.
public static class InstallManifest
{
    public const string FileName = "MudPlay.files";

    private const string Header =
        "# MudPlay release manifest: every file this release installs beside the program, one per line.";

    // Why entry can't be a manifest line, or null when it can. Every entry must also
    // resolve, through PathGuard, to a file directly inside the folder it's used in.
    public static string? WhyNotEntry(string entry)
    {
        if (entry.Length == 0) return "is empty";
        if (entry[0] is '/' or '\\' || (entry.Length > 1 && entry[1] == ':')) return "is an absolute path";
        if (entry.Split('/', '\\').Any(seg => seg == "..")) return "climbs out of the folder with '..'";
        if (entry.Contains('/') || entry.Contains('\\')) return "names a file in a subfolder";
        if (entry.Length > 255) return "is too long for a file name";
        if (!entry.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+'))
            return "has characters no MudPlay file is named with";
        if (!PathGuard.IsPlainName(entry)) return "isn't a plain file name";
        return null;
    }

    // The entries of a manifest's text, in order, without duplicates (compared
    // ignoring case, as Windows and macOS do). Null with a reason when any line is
    // refused: a manifest that isn't wholly trustworthy is not used at all.
    public static IReadOnlyList<string>? Parse(string text, string folder, out string? error)
    {
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            string? why = WhyNotEntry(line)
                          ?? (PathGuard.Child(folder, line) is null ? "resolves outside the folder" : null);
            if (why is not null)
            {
                error = $"{FileName} line {i + 1}: {PathGuard.Show(line)} {why}";
                return null;
            }
            if (seen.Add(line)) entries.Add(line);
        }
        if (entries.Count == 0)
        {
            error = $"{FileName} lists no files";
            return null;
        }
        error = null;
        return entries;
    }

    // Read the manifest in folder. Missing: Entries null and Error null.
    public static ManifestRead Read(string folder)
    {
        string path = Path.Combine(folder, FileName);
        if (!File.Exists(path)) return new ManifestRead(false, null, null);
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ManifestRead(true, null, $"{FileName} can't be read: {ex.Message}");
        }
        IReadOnlyList<string>? entries = Parse(text, folder, out string? error);
        return new ManifestRead(true, entries, error);
    }

    // Write a manifest naming files (and itself) into folder.
    public static void Write(string folder, IEnumerable<string> files)
    {
        IEnumerable<string> names = files.Append(FileName).Distinct(StringComparer.OrdinalIgnoreCase);
        File.WriteAllLines(Path.Combine(folder, FileName), names.Prepend(Header));
    }
}

// A manifest as read from a folder: whether the file is there, its entries when it
// could be trusted, and why not when it couldn't.
public sealed record ManifestRead(bool Present, IReadOnlyList<string>? Entries, string? Error);

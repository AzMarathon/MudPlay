using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A package's manifest is another player's text. The image it names for an emote
// is unpacked into the import folder, and only ever there.
[Collection(EmoteRuntimeCollection.Name)]
public sealed class EmotePackageNameTests : IDisposable
{
    // The import folder sits several levels inside a jail, with folders beside it that
    // a path built on the manifest's names would reach: the jail's contents other than
    // the import folder must be the same after as before.
    private readonly string _jail = Path.Combine(Path.GetTempPath(), "mudplay-emotejail-" + Guid.NewGuid().ToString("N"));
    private string TempDir => Path.Combine(_jail, "a", "b", "c", "import");
    private string LibraryDir => Path.Combine(_jail, "library");
    private readonly LogService _log = new();

    public EmotePackageNameTests()
    {
        Directory.CreateDirectory(TempDir);
        Directory.CreateDirectory(LibraryDir);
        Directory.CreateDirectory(Path.Combine(_jail, "abs"));
        File.WriteAllText(Path.Combine(_jail, "canary.txt"), "canary");
        File.WriteAllText(Path.Combine(_jail, "a", "canary.txt"), "canary");
        File.WriteAllText(Path.Combine(_jail, "a", "b", "canary.txt"), "canary");
    }

    public void Dispose()
    {
        try { Directory.Delete(_jail, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private string Package(IEnumerable<(string Shortcode, string Image)> emotes, IEnumerable<(string Entry, string Content)> entries)
    {
        string path = Path.Combine(_jail, "pack-" + Guid.NewGuid().ToString("N") + ".mudpack");
        var manifest = new EmoteSet
        {
            Emotes = emotes.Select(e => new UserEmote { Shortcode = e.Shortcode, Image = e.Image }).ToList(),
        };
        using FileStream file = File.Create(path);
        using ZipArchive zip = new(file, ZipArchiveMode.Create);
        Add(zip, "emotes.json", JsonSerializer.Serialize(manifest));
        foreach ((string entry, string content) in entries) Add(zip, entry, content);
        return path;

        static void Add(ZipArchive zip, string name, string content)
        {
            using Stream s = zip.CreateEntry(name).Open();
            s.Write(Encoding.UTF8.GetBytes(content));
        }
    }

    // Everything in the jail outside the import folder, with what it holds.
    private SortedDictionary<string, string> Outside()
    {
        string temp = Path.GetFullPath(TempDir) + Path.DirectorySeparatorChar;
        var seen = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string entry in Directory.EnumerateFileSystemEntries(_jail, "*", SearchOption.AllDirectories))
        {
            string full = Path.GetFullPath(entry);
            if ((full + Path.DirectorySeparatorChar).StartsWith(temp, StringComparison.Ordinal)) continue;
            // The packages are the test's own and are not what is looked for.
            if (full.EndsWith(".mudpack", StringComparison.Ordinal)) continue;
            seen[full] = File.Exists(full) ? File.ReadAllText(full) : "(folder)";
        }
        return seen;
    }

    [Fact]
    public void AnImageNamedByAnythingButAFileName_IsUnpackedAsOneFileInTheImportFolder_AndNothingElseIsTouched()
    {
        string absolute = Path.Combine(_jail, "abs", "target.png");
        string package = Package(
            new[] { ("esc", "../../escape.png"), ("abs", absolute), ("nest", "sub/dir/x.png"), ("plain", "ok.png") },
            new[]
            {
                ("../../escape.png", "escape"), (absolute, "absolute"), ("sub/dir/x.png", "nested"), ("ok.png", "plain"),
            });
        var before = Outside();

        IReadOnlyList<EmoteDraft>? drafts = new EmoteStore(LibraryDir, _log).ReadPackageDrafts(package, TempDir);

        Assert.Equal(before, Outside());
        Assert.NotNull(drafts);
        Assert.Equal(4, drafts!.Count);
        string import = Path.GetFullPath(TempDir);
        Assert.All(drafts, d => Assert.Equal(import, Path.GetDirectoryName(Path.GetFullPath(d.ImageSourcePath!))));
        Assert.Equal(new[] { "escape.png", "ok.png", "target.png", "x.png" },
            Directory.EnumerateFileSystemEntries(TempDir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("escape", File.ReadAllText(Path.Combine(TempDir, "escape.png")));
        Assert.Equal("absolute", File.ReadAllText(Path.Combine(TempDir, "target.png")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(TempDir, "x.png")));
        Assert.Equal(new[] { "abs", "esc", "nest", "plain" }, drafts.Select(d => d.Shortcode).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void AnImageWithNoFileNameInItsPath_IsSkipped_AndTheRestAreStillRead()
    {
        string package = Package(
            new[] { ("dir", "sub/"), ("dots", ".."), ("plain", "ok.png") },
            new[] { ("sub/", ""), ("..", "dots"), ("ok.png", "plain") });

        IReadOnlyList<EmoteDraft>? drafts = new EmoteStore(LibraryDir, _log).ReadPackageDrafts(package, TempDir);

        Assert.NotNull(drafts);
        Assert.Equal("plain", Assert.Single(drafts!).Shortcode);
        Assert.Equal(new[] { "ok.png" }, Directory.EnumerateFileSystemEntries(TempDir).Select(Path.GetFileName));
    }

    [Fact]
    public void TwoImagesOfOneFileName_KeepTheFirst_AndSayTheSecondWasSkipped()
    {
        string package = Package(
            new[] { ("one", "a/pic.png"), ("two", "b/pic.png") },
            new[] { ("a/pic.png", "first"), ("b/pic.png", "second") });

        IReadOnlyList<EmoteDraft>? drafts = new EmoteStore(LibraryDir, _log).ReadPackageDrafts(package, TempDir);

        Assert.NotNull(drafts);
        Assert.Equal("one", Assert.Single(drafts!).Shortcode);
        Assert.Equal("first", File.ReadAllText(Path.Combine(TempDir, "pic.png")));
        Assert.Contains(_log.SnapshotProblems(), p => p.Source == "Emotes" && p.Message.Contains("another image"));
    }

    [Fact]
    public void TwoEmotesNamingOneImage_BothGetIt()
    {
        string package = Package(
            new[] { ("one", "pic.png"), ("two", "pic.png") },
            new[] { ("pic.png", "shared") });

        IReadOnlyList<EmoteDraft>? drafts = new EmoteStore(LibraryDir, _log).ReadPackageDrafts(package, TempDir);

        Assert.NotNull(drafts);
        Assert.Equal(new[] { "one", "two" }, drafts!.Select(d => d.Shortcode).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("shared", File.ReadAllText(Path.Combine(TempDir, "pic.png")));
        Assert.Empty(_log.SnapshotProblems());
    }

    [Fact]
    public void AnOrdinaryPackage_StillReadsAllItsImages()
    {
        string package = Package(
            new[] { ("one", "one.png"), ("two", "two.gif") },
            new[] { ("one.png", "1"), ("two.gif", "2"), ("extra.jpg", "3") });

        IReadOnlyList<EmoteDraft>? drafts = new EmoteStore(LibraryDir, _log).ReadPackageDrafts(package, TempDir);

        Assert.NotNull(drafts);
        Assert.Equal(3, drafts!.Count);
        Assert.Equal(new[] { "extra.jpg", "one.png", "two.gif" },
            Directory.EnumerateFiles(TempDir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("extra", Assert.Single(drafts, d => d.Shortcode == "").SuggestedShortcode);
        Assert.Empty(_log.SnapshotProblems());
    }
}

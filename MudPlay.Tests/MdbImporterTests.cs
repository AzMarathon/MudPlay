using System.IO;
using System.Linq;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// Surface tests for <see cref="MdbImporter"/> — covers folder
/// bookkeeping, filename sanitization, and the missing-file early
/// exit. The Jackcess read path runs against real .mdb / .accdb
/// fixtures and is exercised manually via the Game Data → Import
/// .mdb… flow.
/// </summary>
public sealed class MdbImporterTests
{
    [Fact]
    public void Constructor_CreatesGameDataRoot()
    {
        MdbImporter importer = new();
        Assert.True(Directory.Exists(importer.GameDataRoot),
            $"Expected GameDataRoot to exist after ctor: {importer.GameDataRoot}");
    }

    [Fact]
    public void GameDataRoot_PointsAtAppPathsGameDataRoot()
    {
        MdbImporter importer = new();
        Assert.Equal(AppPaths.GameDataRoot, importer.GameDataRoot);
    }

    [Fact]
    public void GetSubfolderPath_ComposesUnderGameDataRoot()
    {
        MdbImporter importer = new();
        string path = importer.GetSubfolderPath("Paradigm-PVE2");
        Assert.Equal(Path.Combine(importer.GameDataRoot, "Paradigm-PVE2"), path);
    }

    [Fact]
    public void GetGameDataFolders_ReturnsSortedSubdirectoryNames()
    {
        MdbImporter importer = new();
        string a = Path.Combine(importer.GameDataRoot, "zzz-test-set-a");
        string b = Path.Combine(importer.GameDataRoot, "aaa-test-set-b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        try
        {
            var folders = importer.GetGameDataFolders().ToList();
            Assert.Contains("aaa-test-set-b", folders);
            Assert.Contains("zzz-test-set-a", folders);

            // Sort is alphabetical / ordinal-ignore-case.
            int idxA = folders.IndexOf("aaa-test-set-b");
            int idxZ = folders.IndexOf("zzz-test-set-a");
            Assert.True(idxA < idxZ, "Expected alphabetical ordering");
        }
        finally
        {
            Directory.Delete(a, recursive: true);
            Directory.Delete(b, recursive: true);
        }
    }

    [Fact]
    public async Task ImportAsync_MissingFile_ReturnsFailureWithNotFoundMessage()
    {
        MdbImporter importer = new();
        string bogus = Path.Combine(Path.GetTempPath(), "definitely-not-a-real-file-12345.mdb");

        MdbImportResult result = await importer.ImportAsync(bogus);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, result.FolderName);
        Assert.Equal(0, result.TablesFound);
        Assert.Equal(0, result.TablesImported);
        Assert.Equal(0, result.TablesSkipped);
        Assert.Equal(0, result.RowsImported);
    }

    [Theory]
    [InlineData("Monsters",           "Monsters")]
    [InlineData("Spell Messages",     "Spell Messages")]
    [InlineData("Path/With/Slashes",  "Path_With_Slashes")]
    [InlineData("",                   "_unnamed")]
    [InlineData("   ",                "_unnamed")]
    public void MakeFilesystemSafe_StripsInvalidChars(string input, string expected)
    {
        // The colon edge-case differs between Linux and Windows
        // (Path.GetInvalidFileNameChars varies by platform), so we
        // assert the cross-platform-safe cases only.
        Assert.Equal(expected, MdbImporter.MakeFilesystemSafe(input));
    }

    // A zero-table import removes the freshly-created (empty) set folder so a broken
    // MDB never leaves an empty set to switch to.
    [Fact]
    public void RemoveDirectoryIfEmpty_DeletesAnEmptyFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-empty-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        MdbImporter.RemoveDirectoryIfEmpty(dir);

        Assert.False(Directory.Exists(dir));
    }

    // The post-write round-trip check that gates the per-table import retry: a
    // truncated / malformed file is rejected so the importer retries (and, if it
    // stays bad, reports the table skipped instead of shipping a crasher).
    [Fact]
    public void TableJsonParses_TrueForValid_FalseForMalformedOrMissing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-tjp-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            string good = Path.Combine(dir, "good.json");
            File.WriteAllText(good, "[{\"Name\":\"Goblin\"}]");
            Assert.True(MdbImporter.TableJsonParses(good));

            string bad = Path.Combine(dir, "bad.json");
            // A raw 0x1E mid-string — the exact corruption the crash report carried.
            File.WriteAllText(bad, "[{\"Name\":\"Go" + (char)0x1E + "blin\"}]");
            Assert.False(MdbImporter.TableJsonParses(bad));

            string truncated = Path.Combine(dir, "truncated.json");
            File.WriteAllText(truncated, "[{\"Name\":\"Gob");   // interrupted write
            Assert.False(MdbImporter.TableJsonParses(truncated));

            Assert.False(MdbImporter.TableJsonParses(Path.Combine(dir, "missing.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The safety guard: a failed re-import over an EXISTING populated set must never
    // delete that set's files.
    [Fact]
    public void RemoveDirectoryIfEmpty_KeepsAPopulatedFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mudplay-populated-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string keep = Path.Combine(dir, "Rooms.json");
        File.WriteAllText(keep, "[]");
        try
        {
            MdbImporter.RemoveDirectoryIfEmpty(dir);

            Assert.True(Directory.Exists(dir), "an existing populated set must survive a failed re-import");
            Assert.True(File.Exists(keep));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

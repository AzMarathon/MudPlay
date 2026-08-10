using System;
using System.IO;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// The one-time legacy "FujinTerm" → MudPlay data migration copies only files the
/// destination LACKS and never overwrites — so updating from a 2.x build backfills
/// a user's profiles / BBS folders / settings without clobbering anything the new
/// install already created. Pins that non-destructive contract.
/// </summary>
public sealed class AppPathsMigrationTests : IDisposable
{
    private readonly string _root;

    public AppPathsMigrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-migrate-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup */ }
    }

    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "dst");

    [Fact]
    public void CopyMissing_CopiesFilesAndNestedDirs_TheDestinationLacks()
    {
        Directory.CreateDirectory(Path.Combine(Src, "BBS", "Paradigm"));
        File.WriteAllText(Path.Combine(Src, "global.json"), "old-global");
        File.WriteAllText(Path.Combine(Src, "BBS", "Paradigm", "conn.json"), "bbs");

        int copied = AppPaths.CopyMissing(Src, Dst);

        Assert.Equal(2, copied);
        Assert.Equal("old-global", File.ReadAllText(Path.Combine(Dst, "global.json")));
        Assert.Equal("bbs", File.ReadAllText(Path.Combine(Dst, "BBS", "Paradigm", "conn.json")));
    }

    [Fact]
    public void CopyMissing_NeverOverwritesExistingFiles()
    {
        Directory.CreateDirectory(Src);
        Directory.CreateDirectory(Dst);
        File.WriteAllText(Path.Combine(Src, "global.json"), "old");
        File.WriteAllText(Path.Combine(Dst, "global.json"), "new");     // dst already has it
        File.WriteAllText(Path.Combine(Src, "profile.json"), "carry");   // dst lacks it

        int copied = AppPaths.CopyMissing(Src, Dst);

        Assert.Equal(1, copied);                                          // only the missing one
        Assert.Equal("new", File.ReadAllText(Path.Combine(Dst, "global.json")));   // untouched
        Assert.Equal("carry", File.ReadAllText(Path.Combine(Dst, "profile.json")));
    }
}

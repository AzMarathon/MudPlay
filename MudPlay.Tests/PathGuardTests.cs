using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The one rule for a name that becomes a folder's (PathGuard), and what stands on
// it: the paths AppPaths makes, the naming dialogs, and a delete that goes only for
// a folder inside the one it was meant for.
[Collection(ScratchRoot.NamesCollection)]
public sealed class PathGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mudplay-pathguard-" + Path.GetRandomFileName());

    public PathGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    // Names nothing may be given, whatever the platform.
    public static readonly string[] NeverPlain =
    {
        "", "   ", ".", "..", "../up", "a/b", "/abs", "a\\b", "\\abs", "C:x", "C:\\x", "name.", "name ",
        "tab\tbed", "nul\0l", "bell\u0007", "CON", "nul", "Aux", "COM1", "LPT9", "LPT1.txt", "con.profile",
    };

    public static readonly string[] Plain =
    {
        "Bob", "My Char", "data-v1.11p (room commands)", "a.b", "v1.11p", "New BBS 2", "Hills - Caves (L40 sim)",
        "über", "名前", "it's", "50% off", "a+b=c", "[tag] name", "COM10", "Console", "LPT", "nulls", ".hidden",
    };

    public static IEnumerable<object[]> NeverPlainRows() => NeverPlain.Select(n => new object[] { n });
    public static IEnumerable<object[]> PlainRows() => Plain.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(NeverPlainRows))]
    public void ANameThatIsNotOnePlainFileName_CannotBeGiven_AndTheReasonIsSaid(string name)
    {
        Assert.False(PathGuard.IsPlainName(name));
        Assert.False(string.IsNullOrWhiteSpace(PathGuard.WhyNotPlain(name)));
        Assert.Contains("BBS name", PathGuard.WhyNotPlain(name, "BBS name"));
    }

    [Theory]
    [MemberData(nameof(PlainRows))]
    public void AnOrdinaryNameCanBeGiven(string name)
    {
        Assert.True(PathGuard.IsPlainName(name), PathGuard.WhyNotPlain(name));
        Assert.NotNull(PathGuard.Child(_dir, name));
        Assert.True(PathGuard.Usable(_dir, name));
    }

    [Fact]
    public void APlainNameIsAlwaysAnEntryOfItsFolder()
    {
        // The strict rule is never looser than the containment under it.
        foreach (string name in Plain)
            Assert.Equal(Path.Combine(_dir, name), PathGuard.Child(_dir, name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../up")]
    [InlineData("a/b")]
    [InlineData("a/")]
    [InlineData("/abs")]
    [InlineData("nul\0l")]
    [InlineData(null)]
    public void WhatWouldNotBeAnEntryDirectlyInsideTheFolder_IsNoChildOfIt(string? name)
    {
        Assert.Null(PathGuard.Child(_dir, name));
        Assert.False(PathGuard.Usable(_dir, name));
    }

    [Fact]
    public void AnAbsolutePathIsNoChild_EvenOneThatPointsInsideTheFolder()
    {
        Assert.Null(PathGuard.Child(_dir, Path.Combine(_dir, "inside")));
        Assert.Null(PathGuard.Child(_dir, _dir));
    }

    [Fact]
    public void AnOddNameIsUsableOnlyWhereAFolderOfThatNameAlreadyIs()
    {
        // One path segment on this platform, and not a name to give anything.
        if (OperatingSystem.IsWindows()) return;
        foreach (string odd in new[] { "a\\b", "C:x", "name.", "name ", "tab\tbed", "CON", "LPT1.txt" })
        {
            Assert.NotNull(PathGuard.Child(_dir, odd));
            Assert.False(PathGuard.Usable(_dir, odd));

            Directory.CreateDirectory(Path.Combine(_dir, odd));
            Assert.True(PathGuard.Usable(_dir, odd));
            Assert.False(PathGuard.IsPlainName(odd));
        }
    }

    [Fact]
    public void InsideMeansStrictlyBelow()
    {
        string inner = Path.Combine(_dir, "inner");
        Assert.True(PathGuard.IsInside(inner, _dir));
        Assert.True(PathGuard.IsInside(Path.Combine(inner, "deeper"), _dir));
        Assert.True(PathGuard.IsInside(Path.Combine(_dir, "x", "..", "inner"), _dir));
        Assert.False(PathGuard.IsInside(_dir, _dir));
        Assert.False(PathGuard.IsInside(_dir + Path.DirectorySeparatorChar, _dir));
        Assert.False(PathGuard.IsInside(Path.Combine(_dir, ".."), _dir));
        Assert.False(PathGuard.IsInside(Path.Combine(inner, "..", ".."), _dir));
        // A sibling whose name the folder's begins.
        Assert.False(PathGuard.IsInside(_dir + "-other", _dir));
    }

    [Fact]
    public void ADeleteGoesOnlyForAFolderStrictlyBelowTheOneItWasMeantFor()
    {
        string within = Path.Combine(_dir, "within"), child = Path.Combine(within, "child");
        string beside = Path.Combine(_dir, "beside");
        foreach (string folder in new[] { child, beside }) Directory.CreateDirectory(folder);
        foreach (string canary in new[] { Path.Combine(_dir, "canary"), Path.Combine(within, "canary"), Path.Combine(beside, "canary") })
            File.WriteAllText(canary, "x");
        File.WriteAllText(Path.Combine(child, "file"), "x");

        Assert.False(PathGuard.DeleteTree(within, within, "test"));
        Assert.False(PathGuard.DeleteTree(Path.Combine(within, "."), within, "test"));
        Assert.False(PathGuard.DeleteTree(Path.Combine(within, ".."), within, "test"));
        Assert.False(PathGuard.DeleteTree(Path.Combine(within, "..", "beside"), within, "test"));
        Assert.False(PathGuard.DeleteTree(beside, within, "test"));
        Assert.True(File.Exists(Path.Combine(_dir, "canary")));
        Assert.True(File.Exists(Path.Combine(within, "canary")));
        Assert.True(File.Exists(Path.Combine(beside, "canary")));
        Assert.True(Directory.Exists(child));

        Assert.True(PathGuard.DeleteTree(child, within, "test"));
        Assert.False(Directory.Exists(child));
        Assert.True(File.Exists(Path.Combine(within, "canary")));
        // Gone already is no refusal.
        Assert.True(PathGuard.DeleteTree(child, within, "test"));
    }

    [Fact]
    public void ARefusalIsSaidOnce_NamingTheOperationAndTheName()
    {
        string name = "../" + Path.GetRandomFileName();
        var said = new List<string>();
        void Hear(string line) { lock (said) said.Add(line); }
        PathGuard.Refused += Hear;
        try
        {
            PathGuard.Refuse("Deleting a test", "test name", name);
            PathGuard.Refuse("Deleting a test", "test name", name);
        }
        finally { PathGuard.Refused -= Hear; }

        string line = Assert.Single(said, l => l.Contains(name));
        Assert.StartsWith("Deleting a test refused: ", line);
        Assert.Contains("can't be used as a test name", line);
        Assert.Contains("nothing on disk was changed", line);
        Assert.Equal("'bell\\u0007'", PathGuard.Show("bell\u0007"));
    }

    // ----- The paths AppPaths makes ---------

    // Every helper that makes a path from a name, with the folder its result has to
    // be in. The name under test goes in the place marked by the lambda's argument.
    public static readonly (string Id, Func<string, string> Path, Func<string> Within)[] Helpers =
    {
        ("GameDataSetDir", h => AppPaths.GameDataSetDir(h), () => AppPaths.GameDataRoot),
        ("GameDataSetLoopsFolder", h => AppPaths.GameDataSetLoopsFolder(h), () => AppPaths.GameDataRoot),
        ("GameDataSetFavoritesFile", h => AppPaths.GameDataSetFavoritesFile(h), () => AppPaths.GameDataRoot),
        ("MessagesFile", h => AppPaths.MessagesFile(h), () => AppPaths.GameDataRoot),
        ("MonsterMessagesFile", h => AppPaths.MonsterMessagesFile(h), () => AppPaths.GameDataRoot),
        ("FlavorPrefixesFile", h => AppPaths.FlavorPrefixesFile(h), () => AppPaths.GameDataRoot),
        ("QuestsFile", h => AppPaths.QuestsFile(h), () => AppPaths.GameDataRoot),
        ("LearnedArrivalRoomsFile", h => AppPaths.LearnedArrivalRoomsFile(h), () => AppPaths.GameDataRoot),
        ("NavSeedLedgerFile", h => AppPaths.NavSeedLedgerFile(h), () => AppPaths.GameDataRoot),
        ("BbsFolder", h => AppPaths.BbsFolder(h), () => AppPaths.BbsDir),
        ("BbsProfileFile", h => AppPaths.BbsProfileFile(h), () => AppPaths.BbsDir),
        ("BbsProfilesDir", h => AppPaths.BbsProfilesDir(h), () => AppPaths.BbsDir),
        ("BbsEmotesDir", h => AppPaths.BbsEmotesDir(h), () => AppPaths.BbsDir),
        ("LegacyBbsLairsFolder", h => AppPaths.LegacyBbsLairsFolder(h), () => AppPaths.BbsDir),
        ("RealmsDir", h => AppPaths.RealmsDir(h), () => AppPaths.BbsDir),
        ("RealmFolder(H, realm)", h => AppPaths.RealmFolder(h, "realm"), () => AppPaths.BbsDir),
        ("RealmFolder(bbs, H)", h => AppPaths.RealmFolder("board", h), () => AppPaths.RealmsDir("board")),
        ("ProfileFolder(H, name)", h => AppPaths.ProfileFolder(h, "name"), () => AppPaths.BbsDir),
        ("ProfileFolder(bbs, H)", h => AppPaths.ProfileFolder("board", h), () => AppPaths.BbsProfilesDir("board")),
        ("CharacterProfileFile(bbs, H)", h => AppPaths.CharacterProfileFile("board", h), () => AppPaths.BbsProfilesDir("board")),
        ("DeathLogsFolder(bbs, H)", h => AppPaths.DeathLogsFolder("board", h), () => AppPaths.BbsProfilesDir("board")),
        ("DeathLogFile(bbs, name, H)", h => AppPaths.DeathLogFile("board", "name", h), () => AppPaths.DeathLogsFolder("board", "name")),
        ("OverrideFile(Global, set H)", h => AppPaths.OverrideFile(SettingsTier.Global, null, "Monsters", h),
            () => Path.Combine(AppPaths.DataRoot, "Global")),
        ("OverrideFile(Character H)", h => AppPaths.OverrideFile(SettingsTier.Character, h, "Monsters", "set", "board"),
            () => AppPaths.BbsProfilesDir("board")),
        ("OverrideFile(Character on BBS H)", h => AppPaths.OverrideFile(SettingsTier.Character, "name", "Monsters", "set", h),
            () => AppPaths.BbsDir),
    };

    public static IEnumerable<object[]> HelperRows() =>
        from helper in Helpers
        // Blank is asked in the fact below: OverrideFile throws for a blank scope, as
        // it always has.
        from name in NeverPlain.Where(n => !string.IsNullOrWhiteSpace(n)).Append(Path.Combine(Path.GetTempPath(), "elsewhere"))
        select new object[] { helper.Id, name };

    [Theory]
    [MemberData(nameof(HelperRows))]
    public void NoPathMadeFromANameLeavesTheFolderItBelongsIn(string helperId, string name)
    {
        (string _, Func<string, string> path, Func<string> within) = Helpers.Single(h => h.Id == helperId);

        string made = path(name);

        Assert.True(PathGuard.IsInside(made, within()), $"{helperId}({PathGuard.Show(name)}) gave {made}, outside {within()}");
    }

    [Fact]
    public void ANameThatWouldLeadElsewhereGetsTheRefusedEntry_AndAnOrdinaryOneIsJoinedAsBefore()
    {
        Assert.Equal(Path.Combine(AppPaths.BbsDir, AppPaths.RefusedName), AppPaths.BbsFolder(".."));
        Assert.Equal(Path.Combine(AppPaths.GameDataRoot, AppPaths.RefusedName), AppPaths.GameDataSetDir("../BBS"));
        Assert.Equal(Path.Combine(AppPaths.BbsDir, "board", "profiles", AppPaths.RefusedName), AppPaths.ProfileFolder("board", "a/b"));
        Assert.Equal(Path.Combine(AppPaths.BbsDir, AppPaths.RefusedName), AppPaths.BbsFolder(""));
        Assert.True(PathGuard.IsPlainName(AppPaths.RefusedName));

        Assert.Equal(Path.Combine(AppPaths.BbsDir, "My Board"), AppPaths.BbsFolder("My Board"));
        Assert.Equal(Path.Combine(AppPaths.BbsDir, "My Board", "profiles", "Bob", "profile.json"),
            AppPaths.CharacterProfileFile("My Board", "Bob"));
        Assert.Equal(Path.Combine(AppPaths.BbsDir, "My Board", "Realms", "PvE"), AppPaths.RealmFolder("My Board", "PvE"));
        Assert.Equal(Path.Combine(AppPaths.GameDataRoot, "data-v1.11p (room commands)", "Loops"),
            AppPaths.GameDataSetLoopsFolder("data-v1.11p (room commands)"));
        Assert.Equal(Path.Combine(AppPaths.DataRoot, "Global", "monsters_overrides.data-v1.11p.json"),
            AppPaths.OverrideFile(SettingsTier.Global, null, "Monsters", "data-v1.11p"));
    }

    [Fact]
    public void AnOddNameAlreadyOnDiskStillGetsItsOwnPath()
    {
        // AppPaths does not know what a caller means to do, so it holds a name only
        // to being an entry of its folder: an older profile under an odd name is read.
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(Path.Combine(AppPaths.BbsDir, "Para: PvE"), AppPaths.BbsFolder("Para: PvE"));
        Assert.Equal(Path.Combine(AppPaths.BbsDir, "board", "profiles", "a\\b."), AppPaths.ProfileFolder("board", "a\\b."));
    }
}

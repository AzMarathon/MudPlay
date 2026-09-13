using System.Collections.Generic;
using MudPlay.Services.Update;
using Xunit;

namespace MudPlay.Tests;

// Pure updater logic: release-JSON parsing, checksum-file parsing, version
// comparison, platform asset-matching, and the swap-script structure. The
// network fetch + file swap themselves aren't unit-tested (they need a live
// release + a real install), but everything they feed on is pinned here.
public sealed class UpdateServiceTests
{
    // ----- ReleaseParser.ParseRelease -----------------------------------------

    [Fact]
    public void ParseRelease_ReadsTagAssetsAndNotes()
    {
        const string json = """
        {
          "tag_name": "v3.78.0",
          "html_url": "https://github.com/Tehshortbus/MudPlay/releases/tag/v3.78.0",
          "body": "Self-update feature.",
          "assets": [
            { "name": "MudPlay-v3.78.0-linux-x64.tar.gz", "browser_download_url": "https://example/linux", "size": 48234567 },
            { "name": "SHA256SUMS.txt", "browser_download_url": "https://example/sums", "size": 512 }
          ]
        }
        """;
        UpdateRelease? rel = ReleaseParser.ParseRelease(json);
        Assert.NotNull(rel);
        Assert.Equal("3.78.0", rel!.Version);
        Assert.Equal("v3.78.0", rel.RawTag);
        Assert.Equal("Self-update feature.", rel.Notes);
        Assert.Equal(2, rel.Assets.Count);
        Assert.Equal("MudPlay-v3.78.0-linux-x64.tar.gz", rel.Assets[0].Name);
        Assert.Equal(48234567, rel.Assets[0].Size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{ \"html_url\": \"x\" }")]        // no tag
    [InlineData("{ \"tag_name\": \"nightly\" }")]  // non-numeric tag
    public void ParseRelease_ReturnsNull_OnBadInput(string json)
        => Assert.Null(ReleaseParser.ParseRelease(json));

    [Fact]
    public void ParseRelease_SkipsAssetsMissingNameOrUrl()
    {
        const string json = """
        {
          "tag_name": "v1.2.3",
          "assets": [
            { "name": "good.tar.gz", "browser_download_url": "https://example/good", "size": 1 },
            { "browser_download_url": "https://example/noname" },
            { "name": "nourl.tar.gz" }
          ]
        }
        """;
        UpdateRelease? rel = ReleaseParser.ParseRelease(json);
        Assert.NotNull(rel);
        Assert.Single(rel!.Assets);
        Assert.Equal("good.tar.gz", rel.Assets[0].Name);
    }

    // ----- ReleaseParser.ParseSha256Sums --------------------------------------

    [Fact]
    public void ParseSha256Sums_HandlesTwoSpaceAndBinaryMarker()
    {
        // "<hash>  <name>" (sha256sum text mode) and "<hash> *<name>" (binary mode).
        string text =
            "ABCDEF0123456789  MudPlay-v3.78.0-linux-x64.tar.gz\n" +
            "0011223344556677 *MudPlay-v3.78.0-win-x64.zip\n" +
            "\n" +
            "garbage-with-no-space\n";
        IReadOnlyDictionary<string, string> map = ReleaseParser.ParseSha256Sums(text);
        Assert.Equal(2, map.Count);
        // Hash is lower-cased for a case-insensitive compare downstream.
        Assert.Equal("abcdef0123456789", map["MudPlay-v3.78.0-linux-x64.tar.gz"]);
        Assert.Equal("0011223344556677", map["MudPlay-v3.78.0-win-x64.zip"]);
    }

    [Fact]
    public void ParseSha256Sums_LastWriteWins_OnDuplicateName()
    {
        string text = "aaaa  dup.tar.gz\nbbbb  dup.tar.gz\n";
        Assert.Equal("bbbb", ReleaseParser.ParseSha256Sums(text)["dup.tar.gz"]);
    }

    // ----- ReleaseParser.TryParseVersion / IsNewer ----------------------------

    [Theory]
    [InlineData("v3.78.0", true, "3.78.0")]
    [InlineData("V1.0", true, "1.0")]
    [InlineData("3.2.1", true, "3.2.1")]
    [InlineData("nightly", false, "")]
    [InlineData("", false, "")]
    public void TryParseVersion_StripsVAndValidates(string tag, bool ok, string expected)
    {
        Assert.Equal(ok, ReleaseParser.TryParseVersion(tag, out string v));
        Assert.Equal(expected, v);
    }

    [Theory]
    [InlineData("3.78.0", "3.78.1", true)]
    [InlineData("3.78.0", "3.79.0", true)]
    [InlineData("3.78.0", "3.78.0", false)]  // equal → not newer
    [InlineData("3.78.1", "3.78.0", false)]  // older → not newer
    [InlineData("3.78.0", "v3.78.1", true)]  // leading v tolerated
    [InlineData("3.78.0+abc123", "3.78.1", true)]  // +commit suffix dropped
    [InlineData("3.78.0", "garbage", false)] // unparsable → never offer
    public void IsNewer_ComparesNumericVersions(string current, string latest, bool expected)
        => Assert.Equal(expected, ReleaseParser.IsNewer(current, latest));

    // ----- UpdatePlatform ------------------------------------------------------

    [Theory]
    [InlineData("win-x64", ".zip")]
    [InlineData("linux-x64", ".tar.gz")]
    [InlineData("osx-x64", ".tar.gz")]
    [InlineData("osx-arm64", ".tar.gz")]
    public void ArchiveExtension_WindowsIsZip_RestAreTarGz(string rid, string ext)
        => Assert.Equal(ext, UpdatePlatform.ArchiveExtension(rid));

    [Fact]
    public void MatchesCurrentPlatform_MatchesThisPlatformsAsset()
    {
        // AssetSuffix() is null only on an unsupported OS/arch (e.g. linux-arm64);
        // on any CI host we actually run on it resolves, so exercise the matcher.
        string? suffix = UpdatePlatform.AssetSuffix();
        if (suffix is null) return;   // unsupported host — nothing to assert

        Assert.True(UpdatePlatform.MatchesCurrentPlatform("MudPlay-v3.78.0" + suffix));
        // Case-insensitive on the whole name.
        Assert.True(UpdatePlatform.MatchesCurrentPlatform("mudplay-v3.78.0" + suffix));
        // Wrong prefix, wrong tail, and null are all rejected.
        Assert.False(UpdatePlatform.MatchesCurrentPlatform("SomethingElse" + suffix));
        Assert.False(UpdatePlatform.MatchesCurrentPlatform("MudPlay-v3.78.0-wrong-rid.bin"));
        Assert.False(UpdatePlatform.MatchesCurrentPlatform(null));
        Assert.False(UpdatePlatform.MatchesCurrentPlatform(""));
    }

    [Fact]
    public void AssetSuffix_MatchesCurrentRid_WhenSupported()
    {
        string? rid = UpdatePlatform.CurrentRid();
        if (rid is null)
        {
            Assert.Null(UpdatePlatform.AssetSuffix());
            return;
        }
        Assert.Equal($"-{rid}{UpdatePlatform.ArchiveExtension(rid)}", UpdatePlatform.AssetSuffix());
    }

    // ----- SwapScriptBuilder ---------------------------------------------------

    [Fact]
    public void BuildPosix_UsesPositionalArgs_BacksUpAndRelaunches()
    {
        string s = SwapScriptBuilder.BuildPosix();
        Assert.Contains("#!/usr/bin/env bash", s);
        // Every path arrives as a positional arg (never interpolated) so spaces are safe.
        Assert.Contains("PID=\"$1\"", s);
        Assert.Contains("NEW=\"$2\"", s);
        Assert.Contains("DST=\"$3\"", s);
        Assert.Contains("EXE=\"$4\"", s);
        Assert.Contains("STAGE=\"$5\"", s);
        // Waits for the app to exit, keeps a backup, and relaunches the exe.
        Assert.Contains("kill -0", s);
        Assert.Contains("mv \"$DST\" \"$BAK\"", s);
        Assert.Contains("mv \"$NEW\" \"$DST\"", s);
        Assert.Contains("\"$EXE\"", s);
        // On success it cleans up the staged download, the backup, and itself.
        Assert.Contains("rm -rf \"$STAGE\"", s);
        Assert.Contains("rm -rf \"$BAK\"", s);
        Assert.Contains("rm -f \"$0\"", s);
    }

    [Fact]
    public void BuildWindows_UsesPositionalArgs_MirrorsAndRelaunches()
    {
        string s = SwapScriptBuilder.BuildWindows();
        Assert.Contains("%~1", s);
        Assert.Contains("%~5", s);
        // Waits on the PID, mirrors install ↔ backup, treats robocopy >=8 as failure.
        Assert.Contains("tasklist", s);
        Assert.Contains("robocopy", s);
        Assert.Contains("errorlevel 8", s);
        Assert.Contains("start \"\" \"%EXE%\"", s);
        // On success it cleans up staging + backup and deletes itself.
        Assert.Contains("rmdir /s /q \"%STAGE%\"", s);
        Assert.Contains("del \"%~f0\"", s);
    }

    // ----- ChangelogExtractor --------------------------------------------------

    private const string SampleChangelog =
        "# Version history\n" +
        "\n" +
        "## 3.79.0\n" +
        "\n" +
        "- Auto-detect completed quests from flags\n" +
        "- Quest editor complete-value spinner\n" +
        "- bug reports addressed: foo-123, bar-456\n" +
        "\n" +
        "## 3.78.0\n" +
        "\n" +
        "- Self-update feature\n";

    [Fact]
    public void TopEntry_ReturnsMatchingVersionBullets_DropsHeadingAndBugLine()
    {
        string? notes = ChangelogExtractor.TopEntry(SampleChangelog, "3.79.0");
        Assert.Equal(
            "- Auto-detect completed quests from flags\n- Quest editor complete-value spinner",
            notes);
    }

    [Fact]
    public void TopEntry_NoVersion_FallsBackToTopEntry()
    {
        string? notes = ChangelogExtractor.TopEntry(SampleChangelog);
        Assert.StartsWith("- Auto-detect completed quests", notes);
        Assert.DoesNotContain("Self-update", notes);        // stops at the next ## heading
    }

    [Fact]
    public void TopEntry_PicksTheNamedOlderEntry_NotJustTheTop()
    {
        string? notes = ChangelogExtractor.TopEntry(SampleChangelog, "3.78.0");
        Assert.Equal("- Self-update feature", notes);
    }

    [Fact]
    public void TopEntry_ToleratesLeadingV_InVersion()
        => Assert.StartsWith("- Auto-detect", ChangelogExtractor.TopEntry(SampleChangelog, "v3.79.0"));

    [Fact]
    public void TopEntry_UnknownVersion_FallsBackToTopEntry()
        => Assert.StartsWith("- Auto-detect", ChangelogExtractor.TopEntry(SampleChangelog, "9.9.9"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# Version history\n\nno entries here\n")]   // no ## heading
    public void TopEntry_ReturnsNull_WhenNothingToShow(string md)
        => Assert.Null(ChangelogExtractor.TopEntry(md));
}

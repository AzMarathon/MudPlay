using System.Collections.Generic;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The --profile CLI parsing + token resolution: a comma list or a repeated flag
// yields one token per instance, and each token resolves to a saved profile by
// explicit "BBS/Name" or a bare name that's unique across BBSes.
public sealed class StartupOptionsTests
{
    [Fact]
    public void ParseProfileTokens_CommaList_SplitsAndTrims()
    {
        var tokens = StartupOptions.ParseProfileTokens(
            new[] { "--profile", "Playpen/Fujin, Playpen/Alt ,RetroBBS/Bob" });

        Assert.Equal(new[] { "Playpen/Fujin", "Playpen/Alt", "RetroBBS/Bob" }, tokens);
    }

    [Fact]
    public void ParseProfileTokens_RepeatedFlag_And_EqualsForm()
    {
        var tokens = StartupOptions.ParseProfileTokens(
            new[] { "--profile", "Fujin", "--profile=Bob" });

        Assert.Equal(new[] { "Fujin", "Bob" }, tokens);
    }

    [Fact]
    public void ParseProfileTokens_KeepsInternalSpaces_DropsEmpties()
    {
        var tokens = StartupOptions.ParseProfileTokens(
            new[] { "--profile", "My Char,, Other Char " });

        Assert.Equal(new[] { "My Char", "Other Char" }, tokens);
    }

    [Fact]
    public void ParseProfileTokens_NoFlag_ReturnsEmpty()
        => Assert.Empty(StartupOptions.ParseProfileTokens(new[] { "--other", "x" }));

    private static readonly IReadOnlyList<ProfileRef> Saved = new[]
    {
        new ProfileRef("Playpen", "Fujin"),
        new ProfileRef("Playpen", "Alt"),
        new ProfileRef("RetroBBS", "Fujin"),   // same char name on a second BBS
        new ProfileRef("RetroBBS", "Bob"),
    };

    [Fact]
    public void ResolveToken_Explicit_BbsSlashName_Matches()
    {
        ProfileRef? hit = StartupOptions.ResolveToken("RetroBBS/Bob", Saved, out string? err);

        Assert.Null(err);
        Assert.Equal(new ProfileRef("RetroBBS", "Bob"), hit);
    }

    [Fact]
    public void ResolveToken_Explicit_IsCaseInsensitive()
    {
        ProfileRef? hit = StartupOptions.ResolveToken("playpen/fujin", Saved, out _);
        Assert.Equal(new ProfileRef("Playpen", "Fujin"), hit);
    }

    [Fact]
    public void ResolveToken_Explicit_Miss_ReportsReason()
    {
        ProfileRef? hit = StartupOptions.ResolveToken("Playpen/Ghost", Saved, out string? err);

        Assert.Null(hit);
        Assert.Contains("no saved profile", err);
    }

    [Fact]
    public void ResolveToken_BareName_Unique_Matches()
    {
        ProfileRef? hit = StartupOptions.ResolveToken("Bob", Saved, out string? err);

        Assert.Null(err);
        Assert.Equal(new ProfileRef("RetroBBS", "Bob"), hit);
    }

    [Fact]
    public void ResolveToken_BareName_Ambiguous_FailsWithGuidance()
    {
        ProfileRef? hit = StartupOptions.ResolveToken("Fujin", Saved, out string? err);

        Assert.Null(hit);
        Assert.Contains("multiple BBSes", err);
        Assert.Contains("Playpen", err);
        Assert.Contains("RetroBBS", err);
    }

    [Fact]
    public void ResolveToken_BareName_NotFound_ReportsReason()
    {
        ProfileRef? hit = StartupOptions.ResolveToken("Nobody", Saved, out string? err);

        Assert.Null(hit);
        Assert.Contains("no saved profile named", err);
    }
}

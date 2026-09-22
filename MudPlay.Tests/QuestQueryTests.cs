using System.Collections.Generic;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Name/alias/number resolution and the @quest reply formatting (ordinals + wording).
public sealed class QuestQueryTests
{
    private static readonly (int, string)[] NoCandidates = System.Array.Empty<(int, string)>();

    // ----- QuestNameResolver ------------------------------------------

    [Fact]
    public void Resolve_FlagNumber_ReturnsIt()
        => Assert.Equal(126, QuestNameResolver.Resolve("126", NoCandidates));

    [Theory]
    [InlineData("good align", 126)]
    [InlineData("good alignment", 126)]
    [InlineData("Good", 126)]
    [InlineData("goodquest", 126)]
    [InlineData("neutral", 127)]
    [InlineData("evil quest", 128)]
    public void Resolve_CuratedAliases(string query, int expected)
        => Assert.Equal(expected, QuestNameResolver.Resolve(query, NoCandidates));

    [Fact]
    public void Resolve_ExactName_FromCandidates()
        => Assert.Equal(500, QuestNameResolver.Resolve("dragon slayer", new[] { (500, "Dragon Slayer") }));

    [Fact]
    public void Resolve_SubstringName_FromCandidates()
        => Assert.Equal(500, QuestNameResolver.Resolve("dragon", new[] { (500, "Dragon Slayer") }));

    [Fact]
    public void Resolve_NoMatch_ReturnsNull()
        => Assert.Null(QuestNameResolver.Resolve("nonsense quest", NoCandidates));

    [Fact]
    public void Resolve_Blank_ReturnsNull()
        => Assert.Null(QuestNameResolver.Resolve("   ", NoCandidates));

    [Fact]
    public void Label_AlignmentFlags_UseFriendlyLabel()
    {
        Assert.Equal("Good align", QuestNameResolver.Label(126, ""));
        Assert.Equal("Neutral align", QuestNameResolver.Label(127, null));
        Assert.Equal("Evil align", QuestNameResolver.Label(128, ""));
    }

    [Fact]
    public void Label_UserName_WinsOverAbilityName()
        => Assert.Equal("Dragon Slayer", QuestNameResolver.Label(500, "Dragon Slayer"));

    // ----- QuestQueryReport -------------------------------------------

    private static QuestProgress Done(int flag, int step) => new(flag, step) { Complete = true };
    private static QuestProgress Open(int flag, int step) => new(flag, step) { Complete = false };

    private static readonly int[] AlignBands = { 10, 20, 30, 40, 50 };

    [Fact]
    public void MarkedOrdinals_ConsecutiveBands_AreOneTwoThree()
    {
        List<QuestProgress> log = new() { Done(126, 10), Done(126, 20), Done(126, 30), Open(126, 40) };
        Assert.Equal(new[] { 1, 2, 3 }, QuestQueryReport.MarkedOrdinals(126, log, AlignBands));
    }

    [Fact]
    public void MarkedOrdinals_SparseBands_KeepTheirPosition()
    {
        // Steps 20 and 40 complete → the 2nd and 4th bands, not renumbered to 1, 2.
        List<QuestProgress> log = new() { Done(126, 20), Done(126, 40) };
        Assert.Equal(new[] { 2, 4 }, QuestQueryReport.MarkedOrdinals(126, log, AlignBands));
    }

    [Fact]
    public void FormatFlag_MatchesTheExampleWording()
    {
        List<QuestProgress> log = new() { Done(126, 10), Done(126, 20), Done(126, 30) };
        Assert.Equal("Good align 1, 2, 3 marked complete",
            QuestQueryReport.FormatFlag(126, "Good align", log, AlignBands));
    }

    [Fact]
    public void FormatFlag_NoneComplete()
    {
        List<QuestProgress> log = new() { Open(126, 10) };
        Assert.Equal("Good align: none marked complete",
            QuestQueryReport.FormatFlag(126, "Good align", log, AlignBands));
    }

    [Fact]
    public void FormatAll_Empty()
        => Assert.Equal("no quests marked complete",
            QuestQueryReport.FormatAll(new List<QuestProgress>(), new QuestStore()));

    [Fact]
    public void FormatAll_GroupsByFlag()
    {
        // From-start consecutive steps so the ordinals are seed-independent.
        List<QuestProgress> log = new()
        {
            Done(126, 10), Done(126, 20),
            Done(128, 10),
        };
        string all = QuestQueryReport.FormatAll(log, new QuestStore());
        Assert.Contains("Good align 1, 2", all);
        Assert.Contains("Evil align 1", all);
    }
}

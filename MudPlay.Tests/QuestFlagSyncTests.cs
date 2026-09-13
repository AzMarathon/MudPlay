using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Quests;
using Xunit;

namespace MudPlay.Tests;

// The pure pieces of the quest-flag completion sync: the two realm reply parsers and the
// one-way "which quests are newly complete" decision. The wire read + profile write aren't
// unit-tested (they need a live connection + profile), but everything they hinge on is.
public sealed class QuestFlagSyncTests
{
    // ----- QuestFlagProbe.ParseStockBulk -----

    [Fact]
    public void ParseStockBulk_ReadsAllPairs()
    {
        // A real stock dump line (as LineExtractor reassembles it from the soft-wrap).
        const string line = "User abilities: 126(17) 2(1) 129(3) 69(6) 125(3) 133(9) 187(1) 82(0)";
        Dictionary<int, int> map = QuestFlagProbe.ParseStockBulk(line).ToDictionary(p => p.Flag, p => p.Value);

        Assert.Equal(17, map[126]);
        Assert.Equal(1, map[2]);
        Assert.Equal(9, map[133]);
        Assert.Equal(0, map[82]);   // a zero value is still read (the decision layer ignores it)
        Assert.Equal(8, map.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("You are carrying: a sword (1)")]  // no "abilities" header → not the dump
    [InlineData("[HP=654/MA=201]: (Resting)")]
    public void ParseStockBulk_IgnoresNonDumpLines(string line)
        => Assert.Empty(QuestFlagProbe.ParseStockBulk(line));

    // ----- QuestFlagProbe.TryParseParadigmLine -----

    [Theory]
    [InlineData("GoodQuest(126)             8", 126, 8)]
    [InlineData("Smash(32)                  1", 32, 1)]
    [InlineData("AC(2)                      780", 2, 780)]
    [InlineData("SheDragonQuest(131)        0", 131, 0)]
    public void TryParseParadigmLine_ExtractsFlagAndValue(string line, int flag, int value)
    {
        Assert.True(QuestFlagProbe.TryParseParadigmLine(line, out int f, out int v));
        Assert.Equal(flag, f);
        Assert.Equal(value, v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[HP=324/MA=57]:abil 126")]        // the echoed command, not a reply
    [InlineData("You have removed griffon shield.")]
    public void TryParseParadigmLine_RejectsNonReplies(string line)
        => Assert.False(QuestFlagProbe.TryParseParadigmLine(line, out _, out _));

    // ----- QuestFlagCompletion.ResolveNewlyComplete -----

    private static QuestFlagCompletion.Target T(int flag, int step, int? complete)
        => new(flag, step, complete);

    [Fact]
    public void ResolveNewlyComplete_MarksAtOrAboveThreshold()
    {
        var targets = new[] { T(133, 0, 9), T(131, 0, 3) };
        var observed = new Dictionary<int, int> { [133] = 9, [131] = 2 };  // 131 below its 3
        IReadOnlyList<QuestFlagCompletion.QuestKey> newly =
            QuestFlagCompletion.ResolveNewlyComplete(targets, observed, new HashSet<QuestFlagCompletion.QuestKey>());

        Assert.Single(newly);
        Assert.Equal(133, newly[0].Flag);
    }

    [Fact]
    public void ResolveNewlyComplete_MarksEveryReachedAlignmentTier()
    {
        // Evil tiers complete at 2 / 3 / 11; a live flag value of 11 has passed all three.
        var targets = new[] { T(128, 10, 2), T(128, 20, 3), T(128, 30, 11), T(128, 40, 13) };
        var observed = new Dictionary<int, int> { [128] = 11 };
        var marked = QuestFlagCompletion.ResolveNewlyComplete(
                targets, observed, new HashSet<QuestFlagCompletion.QuestKey>())
            .Select(k => k.Step).OrderBy(s => s).ToArray();

        Assert.Equal(new[] { 10, 20, 30 }, marked);   // 40 (needs 13) stays incomplete
    }

    [Fact]
    public void ResolveNewlyComplete_NeverReMarksOrClears()
    {
        var targets = new[] { T(133, 0, 9), T(126, 0, 8) };
        var observed = new Dictionary<int, int> { [133] = 9, [126] = 8 };
        var already = new HashSet<QuestFlagCompletion.QuestKey> { new(133, 0) };

        var newly = QuestFlagCompletion.ResolveNewlyComplete(targets, observed, already);
        Assert.Single(newly);          // 133 already complete → skipped; only 126 is new
        Assert.Equal(126, newly[0].Flag);
    }

    [Fact]
    public void ResolveNewlyComplete_SkipsUndetectable()
    {
        // null complete value (couldn't derive) and a ≤0 one (Perfect Stealth) are never marked,
        // even when a flag value is present.
        var targets = new[] { T(50, 0, null), T(186, 0, 0) };
        var observed = new Dictionary<int, int> { [50] = 4, [186] = 0 };
        Assert.Empty(QuestFlagCompletion.ResolveNewlyComplete(
            targets, observed, new HashSet<QuestFlagCompletion.QuestKey>()));
    }

    [Fact]
    public void FlagsToQuery_OnlyIncompleteDetectableFlags()
    {
        var targets = new[]
        {
            T(133, 0, 9),   // incomplete detectable → query
            T(126, 0, 8),   // already complete → skip
            T(50, 0, null), // undetectable → skip
            T(131, 0, 3),   // incomplete detectable → query
        };
        var already = new HashSet<QuestFlagCompletion.QuestKey> { new(126, 0) };
        var flags = QuestFlagCompletion.FlagsToQuery(targets, already).OrderBy(f => f).ToArray();
        Assert.Equal(new[] { 131, 133 }, flags);
    }
}

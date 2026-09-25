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
    public void ResolveNewlyComplete_EvilFlag17_CompletesTiersOneThroughFour()
    {
        // The five Evil tiers complete at their LAST flag value — 2 / 3 / 11 / 13 / 31. A live
        // 128(17) sits partway through tier 5 (14–31), so tiers 1–4 read complete and tier 5
        // does not. Bands keyed by their crawl step (band ordinal here for clarity).
        var targets = new[]
        {
            T(128, 1, 2), T(128, 2, 3), T(128, 3, 11), T(128, 4, 13), T(128, 5, 31),
        };
        var observed = new Dictionary<int, int> { [128] = 17 };
        var marked = QuestFlagCompletion.ResolveNewlyComplete(
                targets, observed, new HashSet<QuestFlagCompletion.QuestKey>())
            .Select(k => k.Step).OrderBy(s => s).ToArray();

        Assert.Equal(new[] { 1, 2, 3, 4 }, marked);   // tier 5 (needs 31) stays incomplete
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

    // ----- in-progress flag values -------------------------------------

    [Fact]
    public void ResolveProgress_RecordsTheReadValueOnAnInProgressQuest()
    {
        // report paradigm-20260925-122911: DaoLordQuest(134) read 7 — the sunstone
        // wristband quest (reward at give-step 12) is mid-way, not complete, but the
        // read still says which steps are done.
        var got = QuestFlagCompletion.ResolveProgress(
            new[] { new QuestFlagCompletion.Band(134, 0, 0) },
            new Dictionary<int, int> { [134] = 7 });

        Assert.Equal(new[] { (new QuestFlagCompletion.QuestKey(134, 0), 7) }, got);
    }

    [Fact]
    public void ResolveProgress_SkipsZeroReadsAndUnreachedBands()
    {
        var got = QuestFlagCompletion.ResolveProgress(
            new[]
            {
                new QuestFlagCompletion.Band(50, 0, 0),       // read 0 — nothing done
                new QuestFlagCompletion.Band(126, 10, 1),     // band 1: reached
                new QuestFlagCompletion.Band(126, 20, 6),     // band 2 starts at 6: reached
                new QuestFlagCompletion.Band(126, 30, 11),    // band 3 starts at 11: not yet
                new QuestFlagCompletion.Band(999, 0, 0),      // flag not read
            },
            new Dictionary<int, int> { [50] = 0, [126] = 6 });

        Assert.Equal(new[] { 10, 20 }, got.Select(g => g.Key.Step));
        Assert.All(got, g => Assert.Equal(6, g.Value));
    }
}

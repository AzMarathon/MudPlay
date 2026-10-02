using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game.Combat;
using MudPlay.Models.Profile;
using Xunit;

namespace MudPlay.Tests;

// The Round Totals window's board: which rows it shows by the window's own options,
// and how it steadies the window's size from round to round.
public sealed class RoundTotalsBoardTests
{
    private static RoundSummary Round(int number, params CombatantDamage[] combatants) =>
        new(number, number, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, combatants, 0, 0, 0, 0, 0, 0);

    private static CombatantDamage Monster(string name, int dealt = 1, int taken = 1, int count = 1) =>
        new(name, dealt, taken, CombatantKind.Monster, count);

    private static readonly CombatantDamage You = new(DamageLineAttributor.Self, 45, 12, CombatantKind.Self);

    private static RoundSummary Crowd(int number, int monsters) =>
        Round(number, Enumerable.Range(1, monsters).Select(i => Monster($"rat {i}")).Prepend(You).ToArray());

    [Fact]
    public void NothingToShow_UntilTheFirstRound()
    {
        RoundTotalsBoard board = new(() => new RoundTotalsWindowSettings());
        Assert.False(board.HasRound);
        Assert.Empty(board.Rows);

        board.Publish(Crowd(1, 2));
        Assert.True(board.HasRound);
        Assert.Equal(1, board.Round);
        Assert.Equal(3, board.Rows.Count);
        Assert.Equal(3, board.RowSlots);
    }

    [Fact]
    public void ABiggerRound_GrowsAtOnce_AndASmallerOne_KeepsTheRoom()
    {
        RoundTotalsBoard board = new(() => new RoundTotalsWindowSettings());
        board.Publish(Crowd(1, 1));
        Assert.Equal(2, board.RowSlots);

        board.Publish(Crowd(2, 6));           // the room fills
        Assert.Equal(7, board.RowSlots);

        board.Publish(Crowd(3, 1));           // and empties again
        Assert.Equal(2, board.Rows.Count);
        Assert.Equal(7, board.RowSlots);      // the window holds its size
    }

    [Fact]
    public void TheRoomShrinks_OnlyAfterEveryRememberedRoundWasSmaller()
    {
        RoundTotalsBoard board = new(() => new RoundTotalsWindowSettings());
        board.Publish(Crowd(1, 6));
        for (int i = 0; i < RoundTotalsBoard.Memory - 1; i++)
        {
            board.Publish(Crowd(2 + i, 1));
            Assert.Equal(7, board.RowSlots);
        }
        board.Publish(Crowd(99, 1));          // the big round has now aged out
        Assert.Equal(2, board.RowSlots);
    }

    [Fact]
    public void TheNameColumn_IsSteadiedTheSameWay()
    {
        RoundTotalsBoard board = new(() => new RoundTotalsWindowSettings());
        board.Publish(Round(1, You, Monster("colossal midnight dragon")));
        int wide = board.NameWidth;
        Assert.Equal("colossal midnight dragon".Length, wide);

        board.Publish(Round(2, You, Monster("rat")));
        Assert.Equal(wide, board.NameWidth);
    }

    [Fact]
    public void TheWindowsOptions_PickTheRows_ApartFromAnythingElse()
    {
        RoundTotalsWindowSettings options = new();
        RoundTotalsBoard board = new(() => options);
        board.Publish(Round(1, You, new CombatantDamage("Bob", 30, 0, CombatantKind.Party), Monster("rat", count: 3)));
        Assert.Equal(new[] { "You", "Bob", "rat x3" }, board.Rows.Select(r => r.Name));

        int changes = 0;
        board.Changed += () => changes++;
        options.ShowMonsters = false;
        options.ShowParty = false;
        board.OptionsChanged();               // the same round, redrawn

        Assert.Equal(new[] { "You" }, board.Rows.Select(r => r.Name));
        Assert.Equal(1, board.RowSlots);      // sized for the new choice, not the old one
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Clear_ForgetsTheRoundAndTheSizes()
    {
        RoundTotalsBoard board = new(() => new RoundTotalsWindowSettings());
        board.Publish(Crowd(1, 5));
        board.Clear();
        Assert.False(board.HasRound);
        Assert.Empty(board.Rows);
        Assert.Equal(0, board.RowSlots);

        board.OptionsChanged();               // nothing to redraw
        Assert.False(board.HasRound);
    }

    [Fact]
    public void Rows_AndTheTerminalTable_AgreeOnContentAndOrder()
    {
        RoundSummary round = Round(4, Monster("orc", dealt: 9, taken: 60), You);
        CombatantKind[] everyone = { CombatantKind.Self, CombatantKind.Party, CombatantKind.Player, CombatantKind.Monster };

        IReadOnlyList<RoundTotalsRow> rows = RoundTotalsFormatter.Rows(round, everyone);
        Assert.Equal(new[] { "You", "orc" }, rows.Select(r => r.Name));

        IReadOnlyList<string> table = RoundTotalsFormatter.Table(round, everyone);
        int width = RoundTotalsFormatter.NameWidth(rows);
        Assert.Equal($"[ {RoundTotalsFormatter.HeaderColumns(width)} ]", table[1]);
        Assert.Equal($"[ {RoundTotalsFormatter.Columns("You", "45", "12", width)} ]", table[2]);
        Assert.Empty(RoundTotalsFormatter.Table(round, Array.Empty<CombatantKind>()));
    }
}

using System.Collections.Generic;
using MudPlay.Game.Inventory;
using Xunit;
using ChainMonster = MudPlay.Game.Inventory.BossDeathLoot.ChainMonster;

namespace MudPlay.Tests;

// Which record of a boss's chain a death was: the one whose drops Grab All goes for.
public sealed class BossDeathLootTests
{
    private static readonly ChainMonster[] Chisholm =
    {
        new(1297, "Lord Chisholm", 1_000),
        new(1298, "malformation", 700_000),
    };

    // A neutral record worth nothing whose death summons the hostile one of the
    // same name, which is the one that pays and drops.
    private static readonly ChainMonster[] Lallim =
    {
        new(607, "Lallim Whitemane", 0),
        new(611, "Lallim Whitemane", 200_000),
    };

    [Fact]
    public void NamedDeath_IsTheRecordOfThatName()
    {
        Assert.Equal(new[] { 1297 }, BossDeathLoot.RecordsThatDied(Chisholm, "Lord Chisholm", 1_000));
        Assert.Equal(new[] { 1298 }, BossDeathLoot.RecordsThatDied(Chisholm, "malformation", 700_000));
        Assert.Equal(new[] { 1298 }, BossDeathLoot.RecordsThatDied(Chisholm, "the  malformation", null));
    }

    // The boss gone from its room, or dead in the dark: the listed record.
    [Fact]
    public void UnnamedDeath_IsTheListedRecord()
        => Assert.Equal(new[] { 1297 }, BossDeathLoot.RecordsThatDied(Chisholm, null, 700_000));

    [Fact]
    public void SameNamedRecords_AreToldApartByTheExpPaid()
    {
        Assert.Equal(new[] { 611 }, BossDeathLoot.RecordsThatDied(Lallim, "Lallim Whitemane", 200_000));
        Assert.Equal(new[] { 611 }, BossDeathLoot.RecordsThatDied(Lallim, "Lallim Whitemane", 40_000));   // a party's share
        Assert.Equal(new[] { 607 }, BossDeathLoot.RecordsThatDied(Lallim, "Lallim Whitemane", null));
        Assert.Equal(new[] { 607 }, BossDeathLoot.RecordsThatDied(Lallim, "Lallim Whitemane", 0));
    }

    // Exp that fits neither, or both: every record of the name, so nothing is missed.
    [Fact]
    public void SameNamedRecords_ExpCantTell_AllOfThem()
    {
        Assert.Equal(new[] { 607, 611 }, BossDeathLoot.RecordsThatDied(Lallim, "Lallim Whitemane", 5));
        ChainMonster[] twins = { new(1, "Kai Master", 60_000), new(2, "Kai Master", 90_000) };
        Assert.Equal(new[] { 1, 2 }, BossDeathLoot.RecordsThatDied(twins, "Kai Master", 50_000));
    }

    // One record's name inside another's: the exact one, not the first that fits.
    [Fact]
    public void ANameInsideAnother_TakesTheExactRecord()
    {
        ChainMonster[] chain = { new(740, "master assassin", 50_000), new(745, "dying master assassin", 0) };
        Assert.Equal(new[] { 745 }, BossDeathLoot.RecordsThatDied(chain, "dying master assassin", null));
        Assert.Equal(new[] { 740 }, BossDeathLoot.RecordsThatDied(chain, "master assassin", 50_000));
        Assert.Equal(new[] { 745 }, BossDeathLoot.RecordsThatDied(chain, "a wounded dying master assassin", null));
    }

    [Fact]
    public void ANameNotInTheChain_FallsBackToTheListedRecord()
        => Assert.Equal(new[] { 1297 }, BossDeathLoot.RecordsThatDied(Chisholm, "shadow wolf", 100));

    [Fact]
    public void NoChain_NoRecords()
        => Assert.Empty(BossDeathLoot.RecordsThatDied(new List<ChainMonster>(), "anything", 1));
}

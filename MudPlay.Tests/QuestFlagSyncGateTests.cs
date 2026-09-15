using System;
using MudPlay.Game.Quests;
using Xunit;

namespace MudPlay.Tests;

public sealed class QuestFlagSyncGateTests
{
    [Fact]
    public void NeverSynced_DoesNotSkip()
    {
        Assert.False(QuestFlagSyncManager.AlreadyCheckedToday(null, new DateOnly(2026, 9, 15)));
    }

    [Fact]
    public void SyncedToday_Skips()
    {
        var today = new DateOnly(2026, 9, 15);
        Assert.True(QuestFlagSyncManager.AlreadyCheckedToday(today, today));
    }

    [Fact]
    public void SyncedYesterday_DoesNotSkip()
    {
        Assert.False(QuestFlagSyncManager.AlreadyCheckedToday(
            new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 15)));
    }
}

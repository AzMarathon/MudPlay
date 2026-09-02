using System;
using MudPlay.Game.Quests;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Guard behaviour of the shared completed-quest bonus resolver — it must NOT crawl
// the quest tree when there's nothing completed to attribute (so Monster Intel's
// per-capture read stays free in the common case). Full crawl resolution is
// game-data-driven and covered by the live Quest tab, not unit-tested here.
public sealed class CompletedQuestBonusesTests
{
    [Fact]
    public void EmptyWhenNoQuestLog()
        => Assert.Empty(CompletedQuestBonuses.Resolve(new GameDataCache(), classId: 1, questLog: null));

    [Fact]
    public void EmptyWhenNothingCompleted()
    {
        QuestProgress[] log =
        {
            new(57, 0) { Complete = false },
            new(128, 10) { Complete = false },
        };
        Assert.Empty(CompletedQuestBonuses.Resolve(new GameDataCache(), classId: 1, log));
    }

    [Fact]
    public void ResolveClassId_NullForBlankName()
        => Assert.Null(CompletedQuestBonuses.ResolveClassId(new GameDataCache(), ""));
}

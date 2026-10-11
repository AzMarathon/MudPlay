using System;
using MudPlay.Game.Map;
using MudPlay.Game.Simulation;
using Xunit;

namespace MudPlay.Tests;

// What a ranking listed again from those kept says of itself: when it was made and
// at what level, and that the character has changed level since when it has.
public sealed class AreaRankingTests
{
    // Local time, mid-June: no zone changes its clocks within two days of it.
    private static readonly DateTimeOffset Now = new(2026, 6, 18, 15, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 6, 18, 15, 0, 0)));

    private static AreaRanking Made(DateTimeOffset at, int level, int characterLevel) => new(
        Array.Empty<AreaRank>(), level, 1.0, 1.0, 3, new RoomKey(1, 1), "Stock", "summary", at, characterLevel);

    [Fact]
    public void MadeTodayAtTheCharactersLevel_SaysTheTimeAndTheLevel()
    {
        AreaRanking r = Made(Now.AddHours(-1).AddMinutes(-28), 42, 42);

        Assert.Equal("Made 13:32 at L42.", r.KeptNote(42, Now));
        Assert.True(r.IsCurrentAt(42));
    }

    [Fact]
    public void TheCharacterHasLevelledSince_SaysTheLevelNow()
    {
        AreaRanking r = Made(Now.AddMinutes(-28), 42, 42);

        Assert.Equal("Made 14:32 at L42; you are L43 now.", r.KeptNote(43, Now));
        Assert.False(r.IsCurrentAt(43));
    }

    [Fact]
    public void RankedAtAnotherLevelThanTheCharacters_IsMeasuredByTheCharactersOwn()
    {
        AreaRanking r = Made(Now.AddMinutes(-28), 50, 42);

        Assert.Equal("Made 14:32 at L50.", r.KeptNote(42, Now));
        Assert.True(r.IsCurrentAt(42));
        Assert.Equal("Made 14:32 at L50; you were L42 then and are L43 now.", r.KeptNote(43, Now));
        // Reaching the level that was ranked is still a change from when it was made.
        Assert.False(r.IsCurrentAt(50));
    }

    [Fact]
    public void TheLevelNowNotYetRead_SaysTheTimeOnly()
    {
        AreaRanking r = Made(Now.AddMinutes(-28), 42, 42);

        Assert.Equal("Made 14:32 at L42.", r.KeptNote(0, Now));
        Assert.True(r.IsCurrentAt(0));
    }

    [Fact]
    public void MadeOnAnEarlierDay_SaysTheDate()
    {
        AreaRanking r = Made(Now.AddDays(-2).AddMinutes(-28), 42, 42);

        Assert.Equal("Made 2026-06-16 14:32 at L42.", r.KeptNote(42, Now));
    }

    private static RankingCheck Changed(params string[] groups) => new(false, groups);

    [Fact]
    public void WhatHasChangedInTheCharacterIsNamedByItsGroups()
    {
        AreaRanking r = Made(Now.AddMinutes(-28), 42, 42);

        Assert.Equal("Made 14:32 at L42; your stats or gear have changed since.",
            r.KeptNote(42, Now, Changed(RankingCharacter.StatsOrGear)));
        Assert.Equal("Made 14:32 at L42; your stats or gear and settings have changed since.",
            r.KeptNote(42, Now, Changed(RankingCharacter.StatsOrGear, RankingCharacter.Settings)));
        Assert.Equal("Made 14:32 at L42; your spells, game-data edits and reach have changed since.",
            r.KeptNote(42, Now, Changed(RankingCharacter.Spells, RankingCharacter.GameDataEdits, RankingCharacter.Reach)));
        Assert.Equal("Made 14:32 at L42; your reach has changed since.",
            r.KeptNote(42, Now, Changed(RankingCharacter.Reach)));
        // With a change of level as well.
        Assert.Equal("Made 14:32 at L42; you are L43 now; your settings have changed since.",
            r.KeptNote(43, Now, Changed(RankingCharacter.Settings)));

        Assert.False(r.IsCurrentAt(42, Changed(RankingCharacter.Settings)));
        Assert.True(r.IsCurrentAt(42, Changed()));
    }

    [Fact]
    public void ACharacterThatCantBeReadYet_IsSaidNotToHaveBeenChecked_AndTheRankingIsNotCurrent()
    {
        AreaRanking r = Made(Now.AddDays(-2).AddMinutes(-28), 42, 42);

        Assert.Equal("Made 2026-06-16 14:32 at L42; not checked against the character yet.",
            r.KeptNote(0, Now, RankingCheck.NotChecked));
        Assert.False(r.IsCurrentAt(0, RankingCheck.NotChecked));
        Assert.False(r.IsCurrentAt(42, RankingCheck.NotChecked));
    }
}

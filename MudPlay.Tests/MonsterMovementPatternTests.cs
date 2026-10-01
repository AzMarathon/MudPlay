using System;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The monster appear / leave lines the Stock message table carries beyond the common
// "<verb> in from <dir>" and "<verb> out to <dir>" forms. A missed arrival keeps a
// monster off the roster until it swings; a missed departure leaves the engine
// swinging at one that has gone.
public sealed class MonsterMovementPatternTests
{
    private static readonly MessageRouter Router = BuildRouter();

    private static MessageRouter BuildRouter()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        return router;
    }

    private static bool Matches(string id, string text, out MatchResult match)
    {
        Assert.True(Router.TryGetPattern(id, out IMessagePattern pattern));
        return pattern.TryMatch(new LineExtractor.EmittedLine(
            text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false), out match);
    }

    [Theory]
    [InlineData("A giant black ooze enters from north.", "A giant black ooze", "north")]
    [InlineData("A crazed madman enters from the east.", "A crazed madman", "east")]
    [InlineData("A giant war dog enters the room from south.", "A giant war dog", "south")]
    public void Arrival_EntersFrom(string line, string name, string direction)
    {
        Assert.True(Matches(KnownPatterns.RoomEntryArrival, line, out MatchResult m));
        Assert.Equal(name, m.Groups[0]);
        Assert.Equal(direction, m.Groups[1]);
    }

    [Theory]
    [InlineData("A shade materializes in the room.")]
    [InlineData("The shade appears right behind you!")]
    [InlineData("The healer materializes from the shadows!")]
    [InlineData("A dragon flies down from above!")]
    [InlineData("An aged titan appears in flash of light!")]
    [InlineData("A barrow wight steps out of the shadows!")]
    [InlineData("A brine hag shuffles into the area.")]
    [InlineData("A skeleton arises from its place of rest.")]
    [InlineData("A sand worm crawls after you!")]
    [InlineData("A slimeworm crashes through the ground into the room!")]
    [InlineData("Commander Markus walks into the room.")]
    [InlineData("As the Champion of Blood falls, a tower of fire whirls about his body!")]
    public void SpawnArrival_StockAppearLines(string line)
        => Assert.True(Matches(KnownPatterns.RoomSpawnArrival, line, out _));

    [Theory]
    [InlineData("A shimmering portal appears in the wall.")]
    [InlineData("The orc creeps out of the room to north.")]
    [InlineData("A wall of fire appears in front of you!")]
    [InlineData("You walk into the room.")]
    [InlineData("Bob gossips: he just walked into the room.")]
    [InlineData("Bob says \"it ran into the room!\"")]
    [InlineData("Commander Markus walks into the room from the north.")]
    public void SpawnArrival_LeavesOtherLinesAlone(string line)
        => Assert.False(Matches(KnownPatterns.RoomSpawnArrival, line, out _));

    [Theory]
    [InlineData("The stone giant stomps off to the north.", "The stone giant", "north")]
    [InlineData("An alchemist walks off to east.", "An alchemist", "east")]
    [InlineData("The slimeworm drags itself off to the west.", "The slimeworm", "west")]
    [InlineData("The carrion beast oozes out of the room to south.", "The carrion beast", "south")]
    [InlineData("The sahuagin leaves to the northeast!", "The sahuagin", "northeast")]
    [InlineData("The brine hag exits to the down!", "The brine hag", "down")]
    [InlineData("The wild spider follows a web to the up.", "The wild spider", "up")]
    public void Departure_StockLeaveLines(string line, string name, string direction)
    {
        Assert.True(Matches(KnownPatterns.RoomEntryDeparture, line, out MatchResult m));
        Assert.Equal(name, m.Groups[0]);
        Assert.Equal(direction, m.Groups[1]);
    }

    // "off to" needs a real direction: this is chat, not a monster leaving.
    [Theory]
    [InlineData("Bob wanders off to the store.")]
    [InlineData("Bob leaves to eat.")]
    public void Departure_NeedsARealDirection(string line)
        => Assert.False(Matches(KnownPatterns.RoomEntryDeparture, line, out _));
}

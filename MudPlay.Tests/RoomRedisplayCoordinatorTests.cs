using System;
using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// RoomRedisplayCoordinator folds the two post-kill room re-render requests (the
// item engine's drop re-look and the cash engine's combat-clear re-display) into
// a single bare Enter, so the last kill renders the room once, not twice.
public sealed class RoomRedisplayCoordinatorTests
{
    [Fact]
    public void FirstRequest_Sends()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        RoomRedisplayCoordinator sut = new(() => now);

        Assert.True(sut.ShouldSend());
    }

    [Fact]
    public void SecondRequest_WithinWindow_Coalesces()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        RoomRedisplayCoordinator sut = new(() => now);

        Assert.True(sut.ShouldSend());       // AutoGet drop re-look sends the Enter
        now = now.AddMilliseconds(200);      // Cash combat-clear, same burst
        Assert.False(sut.ShouldSend());      // coalesced — no second render
    }

    [Fact]
    public void LaterRequest_PastWindow_SendsAgain()
    {
        DateTime now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        RoomRedisplayCoordinator sut = new(() => now);

        Assert.True(sut.ShouldSend());
        now = now.AddMilliseconds(800);      // a fresh kill past the 750ms window
        Assert.True(sut.ShouldSend());
    }
}

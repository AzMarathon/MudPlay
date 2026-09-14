using System;
using System.Text.Json;
using MudPlay.Models.Settings;
using MudPlay.Services;
using MudPlay.Services.Update;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// The two pure halves of the update notice: when the automatic re-check is due,
// and what the title-bar crawl renders. Both are timer-driven in the app, so
// pinning the math here is the only way to exercise them without waiting.
public sealed class UpdateNoticeTests
{
    // ----- UpdateSchedule ------------------------------------------------------

    [Theory]
    // Before the morning slot → this morning.
    [InlineData("2026-09-13 03:00", "2026-09-13 09:00")]
    [InlineData("2026-09-13 08:59", "2026-09-13 09:00")]
    // Between the slots → this evening.
    [InlineData("2026-09-13 09:01", "2026-09-13 21:00")]
    [InlineData("2026-09-13 20:59", "2026-09-13 21:00")]
    // After the last slot → tomorrow morning.
    [InlineData("2026-09-13 21:01", "2026-09-14 09:00")]
    [InlineData("2026-09-13 23:59", "2026-09-14 09:00")]
    public void NextSlotAfter_LandsOnMorningOrEvening(string now, string expected)
        => Assert.Equal(DateTime.Parse(expected), UpdateSchedule.NextSlotAfter(DateTime.Parse(now)));

    [Theory]
    [InlineData("2026-09-13 09:00")]
    [InlineData("2026-09-13 21:00")]
    public void NextSlotAfter_IsStrictlyAfter_SoAFiringSlotRollsForward(string atSlot)
    {
        // Called right when a slot fires, it must hand back the NEXT one — returning
        // the same instant would re-arm on a slot already in the past and the tick
        // would fire again on the very next poll.
        DateTime now = DateTime.Parse(atSlot);
        Assert.True(UpdateSchedule.NextSlotAfter(now) > now);
    }

    [Fact]
    public void NextSlotAfter_ChainsToExactlyTwoPerDay()
    {
        DateTime cursor = DateTime.Parse("2026-09-13 00:00");
        int fired = 0;
        while (cursor < DateTime.Parse("2026-09-20 00:00"))
        {
            cursor = UpdateSchedule.NextSlotAfter(cursor);
            if (cursor < DateTime.Parse("2026-09-20 00:00")) fired++;
        }
        Assert.Equal(14, fired);   // 7 days × 2
    }

    // ----- Settings key stability ----------------------------------------------

    [Fact]
    public void AutoCheckForUpdates_StillReadsTheOriginalOnDiskKey()
    {
        // The property was renamed when the check stopped being startup-only. The key
        // was NOT — anyone who turned the check off in 3.78.0+ has the old name in
        // their global.json, and a silent reset would quietly turn checking back on.
        const string legacy = """{ "CheckForUpdatesOnStartup": false }""";
        GlobalSettings? loaded = JsonSerializer.Deserialize<GlobalSettings>(legacy, JsonStore.Options);
        Assert.NotNull(loaded);
        Assert.False(loaded!.AutoCheckForUpdates);

        // And it writes the same key back, so an old build could still read it.
        string written = JsonSerializer.Serialize(new GlobalSettings { AutoCheckForUpdates = false }, JsonStore.Options);
        Assert.Contains("\"CheckForUpdatesOnStartup\": false", written);
        Assert.DoesNotContain("AutoCheckForUpdates", written);
    }

    // ----- UpdateTitleMarquee --------------------------------------------------

    [Fact]
    public void Frame_IsFixedWidth_SoTheTitleDoesntJitter()
    {
        int width = UpdateTitleMarquee.Frame(0).Length;
        for (int f = 0; f < UpdateTitleMarquee.Period * 2; f++)
            Assert.Equal(width, UpdateTitleMarquee.Frame(f).Length);
    }

    [Fact]
    public void Frame_SlidesRight_OneCharacterPerFrame()
    {
        // Sliding right means frame N+1 is frame N shifted over by one: drop the last
        // character of N and it must be the tail of N+1.
        for (int f = 0; f < UpdateTitleMarquee.Period; f++)
        {
            string a = UpdateTitleMarquee.Frame(f);
            string b = UpdateTitleMarquee.Frame(f + 1);
            Assert.Equal(a[..^1], b[1..]);
        }
    }

    [Fact]
    public void Frame_WrapsCleanlyAtThePeriod()
        => Assert.Equal(UpdateTitleMarquee.Frame(0), UpdateTitleMarquee.Frame(UpdateTitleMarquee.Period));

    [Fact]
    public void Frame_AlwaysShowsTheMessageAtLeastOnce()
    {
        // The whole point is that a glance at the title bar reads "UPDATE AVAILABLE".
        // A width that only ever showed a fragment would defeat it.
        for (int f = 0; f < UpdateTitleMarquee.Period; f++)
            Assert.Contains("UPDATE AVAILABLE", UpdateTitleMarquee.Frame(f));
    }

    [Fact]
    public void Frame_ToleratesNegativeAndHugeFrames()
    {
        // The caller keeps its counter bounded, but a title bar is the last thing
        // worth throwing over if that ever slips.
        Assert.Equal(UpdateTitleMarquee.Frame(0).Length, UpdateTitleMarquee.Frame(-1).Length);
        Assert.Equal(UpdateTitleMarquee.Frame(0).Length, UpdateTitleMarquee.Frame(int.MaxValue).Length);
    }
}

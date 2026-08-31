using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Pins the PST/MST-style short abbreviation lookup: DST-aware for a zone that
// observes it, stable for one that doesn't (Arizona), and a graceful fallback
// to the numeric UTC offset for a zone outside the table. TimeZoneInfo.FindSystemTimeZoneById
// resolves IANA ids on every platform .NET runs on (including Windows, via its
// built-in ICU conversion), so these are deterministic regardless of the
// machine actually running the test.
public sealed class TimeZoneAbbreviationTests
{
    [Fact]
    public void KnownZone_Winter_ReturnsStandardAbbreviation()
    {
        TimeZoneInfo denver = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
        DateTimeOffset winter = new(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours(-7));

        Assert.Equal("MST", TimeZoneAbbreviation.For(winter, denver));
    }

    [Fact]
    public void KnownZone_Summer_ReturnsDaylightAbbreviation()
    {
        TimeZoneInfo denver = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
        DateTimeOffset summer = new(2026, 7, 15, 12, 0, 0, TimeSpan.FromHours(-6));

        Assert.Equal("MDT", TimeZoneAbbreviation.For(summer, denver));
    }

    // Arizona never observes DST, so both a winter and a summer instant read
    // the same "MST" — this is the case that motivated a per-zone Daylight
    // entry instead of always deriving it from IsDaylightSavingTime.
    [Fact]
    public void ArizonaNeverObservesDaylightSaving_AlwaysStandard()
    {
        TimeZoneInfo phoenix = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
        DateTimeOffset winter = new(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours(-7));
        DateTimeOffset summer = new(2026, 7, 15, 12, 0, 0, TimeSpan.FromHours(-7));

        Assert.Equal("MST", TimeZoneAbbreviation.For(winter, phoenix));
        Assert.Equal("MST", TimeZoneAbbreviation.For(summer, phoenix));
    }

    // A zone outside the table (not a North American one this feature was
    // built for) falls back to the numeric offset rather than guessing at an
    // abbreviation or throwing.
    [Fact]
    public void UnknownZone_FallsBackToNumericOffset()
    {
        TimeZoneInfo tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        DateTimeOffset at = new(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours(9));

        Assert.Equal("+09:00", TimeZoneAbbreviation.For(at, tokyo));
    }
}

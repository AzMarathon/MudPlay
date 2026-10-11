using MudPlay.Game;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The account-detail scrub: the login name, login password and suicide password
// never survive into a rendered report, whichever section carried them.
public sealed class BugReportScrubTests
{
    private const string Login = "loginzed";
    private const string Password = "Pa55w0rd!x";
    private const string Suicide = "endgame77";

    private static BugReportScrub.Scrubber MakeScrubber(string? characterName = "Tester") =>
        BugReportScrub.Build(
        [
            new(BugReportScrub.LoginNameLabel, Login),
            new(BugReportScrub.LoginPasswordLabel, Password),
            new(BugReportScrub.SuicidePasswordLabel, Suicide),
        ],
        [characterName]);

    private static BugReportBuilder.BugReportCapture CaptureWith(BugReportScrub.Scrubber scrubber) => new(
        new DateTimeOffset(2026, 10, 10, 14, 0, 0, TimeSpan.Zero),
        RealmType.Stock,
        [
            new BugReportBuilder.Section("Scrollback",
                $"```\n12:00:01 Enter your User-ID: {Login}\n12:00:02 [HP=10/MA=5]:{Suicide}\n```"),
            new BugReportBuilder.Section("Wire — raw",
                $"```\n^[[0m{Password}^M\n^[[1;37m{Login.ToUpperInvariant()}^M\n```"),
            new BugReportBuilder.Section("Program log",
                $"```\n12:00:03.120  [Info]  Telnet: sent {Password}\n```"),
            new BugReportBuilder.Section("Commands sent",
                $"```\n12:00:04.000  typed  sent  {Suicide}\n```"),
            new BugReportBuilder.Section("Triggers",
                $"- login helper: pattern `User-ID` -> `{Login}`"),
            new BugReportBuilder.Section("Settings overrides",
                $"```json\n{{ \"Note\": \"{Password}\" }}\n```"),
        ],
        scrubber);

    [Fact]
    public void Render_MasksAllThreeInEverySection()
    {
        string md = BugReportBuilder.Render(CaptureWith(MakeScrubber()), $"my password is {Password}");

        Assert.DoesNotContain(Login, md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Suicide, md, StringComparison.OrdinalIgnoreCase);
        // Scrollback x2, wire x2, log, trail, trigger, settings, and the user's own text.
        Assert.Equal(9, CountOf(md, BugReportScrub.Mask));
        // What surrounds a masked value is untouched.
        Assert.Contains("12:00:01 Enter your User-ID: [redacted]", md);
        Assert.Contains("## Program log", md);
    }

    [Fact]
    public void RenderStateOnly_MasksToo()
    {
        string md = BugReportBuilder.RenderStateOnly(CaptureWith(MakeScrubber()));

        Assert.DoesNotContain(Login, md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Password, md);
        Assert.DoesNotContain(Suicide, md);
    }

    [Fact]
    public void Apply_MasksAValueSplitByAWrappedRow()
    {
        // The terminal wrapped the row mid-word; the next row carries its own time.
        string text = $"12:00:01 Enter your User-ID: {Login[..4]}\n12:00:01 {Login[4..]} ok\n12:00:02 next";

        string scrubbed = MakeScrubber().Apply(text, out int replaced);

        Assert.Equal(1, replaced);
        Assert.DoesNotContain(Login[..4], scrubbed);
        Assert.DoesNotContain(Login[4..], scrubbed);
        Assert.Contains("12:00:02 next", scrubbed);
    }

    [Fact]
    public void Apply_IsCaseInsensitive()
    {
        string scrubbed = MakeScrubber().Apply($"x {Password.ToUpperInvariant()} y {Suicide.ToUpperInvariant()} z");
        Assert.Equal($"x {BugReportScrub.Mask} y {BugReportScrub.Mask} z", scrubbed);
    }

    [Fact]
    public void LoginNameEqualToTheCharacterName_IsLeftAndSaidSo()
    {
        BugReportScrub.Scrubber scrubber = MakeScrubber(characterName: "Loginzed Surname");

        Assert.Equal($"{Login} telepaths: hi", scrubber.Apply($"{Login} telepaths: hi"));
        Assert.Contains(scrubber.Notes, n => n.Contains("same as the character's name"));
        // The passwords are still masked.
        Assert.Equal(BugReportScrub.Mask, scrubber.Apply(Password));
    }

    [Fact]
    public void ValueShorterThanThreeCharacters_IsLeftAndSaidSo()
    {
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build(
            [new(BugReportScrub.SuicidePasswordLabel, "ab"), new(BugReportScrub.LoginPasswordLabel, Password)],
            []);

        Assert.Equal("cabbage", scrubber.Apply("cabbage"));
        Assert.Contains(scrubber.Notes, n => n.Contains("suicide password") && n.Contains("shorter than 3"));
        Assert.DoesNotContain(scrubber.Notes, n => n.Contains("ab ") || n.EndsWith("ab", StringComparison.Ordinal));
    }

    [Fact]
    public void Contains_FindsAValueInADefinition()
    {
        BugReportScrub.Scrubber scrubber = MakeScrubber();
        Assert.True(scrubber.Contains($"say {Password.ToLowerInvariant()}"));
        Assert.False(scrubber.Contains("say hello"));
        Assert.False(scrubber.Contains(null));
    }

    [Fact]
    public void NoSecrets_ChangesNothing()
    {
        BugReportScrub.Scrubber scrubber = BugReportScrub.Build([new(BugReportScrub.LoginNameLabel, null)], []);
        Assert.False(scrubber.HasTerms);
        Assert.Equal("anything at all", scrubber.Apply("anything at all"));
    }

    [Fact]
    public void Scrubber_DoesNotPrintItsValues()
    {
        string printed = CaptureWith(MakeScrubber()).ToString();
        Assert.DoesNotContain(Login, printed);
        Assert.DoesNotContain(Password, printed);
        Assert.DoesNotContain(Suicide, printed);
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}

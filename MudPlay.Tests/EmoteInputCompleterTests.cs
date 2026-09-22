using MudPlay.Game.Emotes;
using Xunit;

namespace MudPlay.Tests;

// The ":" picker's token detection, suggestion ranking, and splice.
public sealed class EmoteInputCompleterTests
{
    [Fact]
    public void FindActiveToken_MidWord_ReturnsPartial()
    {
        var t = EmoteInputCompleter.FindActiveToken("hi :sma", 7);
        Assert.NotNull(t);
        Assert.Equal(3, t!.Value.ColonIndex);
        Assert.Equal("sma", t.Value.Partial);
    }

    [Fact]
    public void FindActiveToken_AtLineStart_Works()
    {
        var t = EmoteInputCompleter.FindActiveToken(":lol", 4);
        Assert.NotNull(t);
        Assert.Equal(0, t!.Value.ColonIndex);
        Assert.Equal("lol", t.Value.Partial);
    }

    [Fact]
    public void FindActiveToken_ClockTime_IsNotAToken()
        => Assert.Null(EmoteInputCompleter.FindActiveToken("raid 8:00", 9));

    [Fact]
    public void FindActiveToken_PastWhitespace_IsNull()
        => Assert.Null(EmoteInputCompleter.FindActiveToken("a :x y", 6));

    [Fact]
    public void FindActiveToken_NoColon_IsNull()
        => Assert.Null(EmoteInputCompleter.FindActiveToken("no colon here", 6));

    [Fact]
    public void Suggest_PrefixRanksBeforeSubstring()
    {
        string[] names = { "sadge", "sadge2", "copium", "monkas" };
        var s = EmoteInputCompleter.Suggest("sa", names);
        Assert.Equal("sadge", s[0]);
        Assert.Equal("sadge2", s[1]);
    }

    [Fact]
    public void Suggest_EmptyPartial_ListsAllCapped()
    {
        string[] names = { "a", "b", "c" };
        Assert.Equal(3, EmoteInputCompleter.Suggest("", names).Count);
        Assert.Equal(2, EmoteInputCompleter.Suggest("", names, max: 2).Count);
    }

    [Fact]
    public void Apply_SplicesShortcodeWithTrailingSpace()
    {
        var t = EmoteInputCompleter.FindActiveToken("hi :sma", 7)!.Value;
        var (text, caret) = EmoteInputCompleter.Apply("hi :sma", t, 7, "smile");
        Assert.Equal("hi :smile: ", text);
        Assert.Equal(text.Length, caret);
    }

    [Fact]
    public void Apply_KeepsTextAfterCaret()
    {
        // Caret in the middle: "hi :sma| there" → the trailing " there" is preserved.
        var t = EmoteInputCompleter.FindActiveToken("hi :sma there", 7)!.Value;
        var (text, _) = EmoteInputCompleter.Apply("hi :sma there", t, 7, "smile");
        Assert.Equal("hi :smile:  there", text);
    }
}

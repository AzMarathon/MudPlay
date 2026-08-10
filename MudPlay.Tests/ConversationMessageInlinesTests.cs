using System.Linq;
using MudPlay.Views;
using Xunit;

namespace MudPlay.Tests;

// ConversationMessageInlines.Segment — the pure URL splitter behind clickable web
// links in the Conversation window. Verifies http/https detection, trailing-
// punctuation trimming, multiple links, and plain-text passthrough.
public sealed class ConversationMessageInlinesTests
{
    [Fact]
    public void PlainText_IsOneNonLinkSegment()
    {
        var segs = ConversationMessageInlines.Segment("hello there, no links here");
        var one = Assert.Single(segs);
        Assert.False(one.IsLink);
        Assert.Equal("hello there, no links here", one.Text);
    }

    [Fact]
    public void EmptyOrNull_YieldsNoSegments()
    {
        Assert.Empty(ConversationMessageInlines.Segment(""));
        Assert.Empty(ConversationMessageInlines.Segment(null!));
    }

    [Fact]
    public void Url_IsSplitFromSurroundingText()
    {
        var segs = ConversationMessageInlines.Segment("visit https://example.com now");
        Assert.Equal(3, segs.Count);
        Assert.Equal(("visit ", false), (segs[0].Text, segs[0].IsLink));
        Assert.Equal(("https://example.com", true), (segs[1].Text, segs[1].IsLink));
        Assert.Equal((" now", false), (segs[2].Text, segs[2].IsLink));
    }

    [Theory]
    [InlineData("see https://x.org.", "https://x.org", ".")]
    [InlineData("(https://x.org)", "https://x.org", ")")]
    [InlineData("read https://x.org/path!", "https://x.org/path", "!")]
    public void TrailingPunctuation_IsPeeledOffTheLink(string input, string expectedLink, string expectedTail)
    {
        var segs = ConversationMessageInlines.Segment(input);
        var link = Assert.Single(segs.Where(s => s.IsLink));
        Assert.Equal(expectedLink, link.Text);
        Assert.EndsWith(expectedTail, segs[^1].Text);
        Assert.False(segs[^1].IsLink);
    }

    [Fact]
    public void MultipleUrls_EachBecomeLinks()
    {
        var segs = ConversationMessageInlines.Segment("http://a.com and https://b.org");
        var links = segs.Where(s => s.IsLink).Select(s => s.Text).ToArray();
        Assert.Equal(new[] { "http://a.com", "https://b.org" }, links);
    }

    [Fact]
    public void SchemeMatch_IsCaseInsensitive()
    {
        var segs = ConversationMessageInlines.Segment("HTTPS://Example.COM/Path");
        var link = Assert.Single(segs.Where(s => s.IsLink));
        Assert.Equal("HTTPS://Example.COM/Path", link.Text);
    }

    [Fact]
    public void BareWordWithoutScheme_IsNotLinked()
    {
        // "www.foo.com" without an http(s):// scheme stays plain text (conservative).
        var segs = ConversationMessageInlines.Segment("go to www.foo.com today");
        Assert.DoesNotContain(segs, s => s.IsLink);
    }
}

using System.Linq;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// HelpMarkup.ParseInline — the inline markdown tokenizer for the Help content
// renderer: **bold** / *italic* / `code` / [text](link) → styled runs.
public sealed class HelpMarkupTests
{
    [Fact]
    public void ParseInline_SplitsBoldFromSurroundingText()
    {
        var segs = HelpMarkup.ParseInline("A **bold** word");
        Assert.Equal(3, segs.Count);
        Assert.Equal(new HelpInline("A ", HelpInlineStyle.Normal), segs[0]);
        Assert.Equal(new HelpInline("bold", HelpInlineStyle.Bold), segs[1]);
        Assert.Equal(new HelpInline(" word", HelpInlineStyle.Normal), segs[2]);
    }

    [Fact]
    public void ParseInline_HandlesCodeAndItalic()
    {
        var segs = HelpMarkup.ParseInline("`code` and *em*");
        Assert.Equal("code", segs[0].Text);
        Assert.Equal(HelpInlineStyle.Code, segs[0].Style);
        Assert.Contains(segs, s => s.Style == HelpInlineStyle.Italic && s.Text == "em");
    }

    [Fact]
    public void ParseInline_FieldLabelWithInlineCode()
    {
        // The guide's per-setting field lines, e.g. "**Default:** `Spells first`".
        var segs = HelpMarkup.ParseInline("**Default:** `Spells first`");
        Assert.Equal(HelpInlineStyle.Bold, segs[0].Style);
        Assert.Equal("Default:", segs[0].Text);
        Assert.Contains(segs, s => s.Style == HelpInlineStyle.Code && s.Text == "Spells first");
    }

    [Fact]
    public void ParseInline_LinkRendersAsVisibleTextOnly()
    {
        var segs = HelpMarkup.ParseInline("see [Combat](#combat) tab");
        Assert.Equal("see Combat tab", string.Concat(segs.Select(s => s.Text)));
        Assert.DoesNotContain(segs, s => s.Text.Contains('#'));
    }

    [Fact]
    public void ParseInline_UnclosedMarkerStaysLiteral()
    {
        var segs = HelpMarkup.ParseInline("a * b");
        Assert.Equal("a * b", string.Concat(segs.Select(s => s.Text)));
        Assert.All(segs, s => Assert.Equal(HelpInlineStyle.Normal, s.Style));
    }
}

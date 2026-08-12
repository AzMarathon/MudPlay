using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// HelpBook.Parse — the markdown → TOC-tree parser behind the Help window:
// '# ' overview, '## ' sections, '### ' subsections, body = text between a
// heading and the next, standalone '---' rules dropped.
public sealed class HelpBookTests
{
    private const string Sample =
        "# Guide Title\n" +
        "Intro line one.\n" +
        "\n" +
        "---\n" +
        "\n" +
        "## General\n" +
        "General intro.\n" +
        "\n" +
        "### Font\n" +
        "Font body.\n" +
        "### Scale\n" +
        "Scale body.\n" +
        "\n" +
        "## Combat\n" +
        "Combat intro.\n" +
        "### Action order\n" +
        "Order body.\n";

    [Fact]
    public void Parse_NestsSectionsUnderTitleAndSubsectionsUnderSections()
    {
        var topics = HelpBook.Parse(Sample);

        Assert.Single(topics);                        // one '#' root holds it all
        HelpTopic root = topics[0];
        Assert.Equal("Guide Title", root.Title);
        Assert.Equal("Intro line one.", root.Body);   // '---' dropped, blanks trimmed
        Assert.Equal(2, root.Children.Count);         // General + Combat under the root

        HelpTopic general = root.Children[0];
        Assert.Equal("General", general.Title);
        Assert.Equal("General intro.", general.Body);
        Assert.Equal(2, general.Children.Count);
        Assert.Equal("Font", general.Children[0].Title);
        Assert.Equal("Font body.", general.Children[0].Body);
        Assert.Equal("Scale", general.Children[1].Title);
        Assert.Equal("Scale body.", general.Children[1].Body);

        HelpTopic combat = root.Children[1];
        Assert.Equal("Combat", combat.Title);
        Assert.Single(combat.Children);
        Assert.Equal("Action order", combat.Children[0].Title);
        Assert.Equal("Order body.", combat.Children[0].Body);
    }

    [Fact]
    public void Parse_PreservesInteriorBlanksAndBulletLines()
    {
        var topics = HelpBook.Parse(
            "## Sec\n" +
            "para one\n" +
            "\n" +
            "- item a\n" +
            "- item b\n");

        Assert.Single(topics);
        Assert.Equal("para one\n\n- item a\n- item b", topics[0].Body);
    }

    [Fact]
    public void Parse_Empty_ReturnsNoTopics()
    {
        Assert.Empty(HelpBook.Parse(""));
        Assert.Empty(HelpBook.Parse(null!));
    }
}

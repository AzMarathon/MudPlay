using System;
using MudPlay.Game;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Pins ActionEmoteClassifier — the green-line action/emote detector. Colour gate
// (all cells green = palette index 2/10), then the head shape: own "You <verb>…",
// or "<known player> <verb>…". Cases are real lines from a PlayPen/MajorMUD capture.
public sealed class ActionEmoteClassifierTests
{
    private static LineExtractor.EmittedLine Line(string text, int fgIndex)
    {
        CellAttributes attr = CellAttributes.Default.WithForeground(TerminalColor.Indexed(fgIndex));
        CellAttributes[] attrs = new CellAttributes[text.Length];
        for (int i = 0; i < text.Length; i++) attrs[i] = attr;
        return new LineExtractor.EmittedLine(text, attrs, DateTimeOffset.UnixEpoch, false);
    }

    private static LineExtractor.EmittedLine Green(string text) => Line(text, 2);

    // ----- IsAllGreen ---------------------------------------------------

    [Theory]
    [InlineData(2, true)]    // SGR 32 green — the action colour
    [InlineData(10, true)]   // bright green
    [InlineData(6, false)]   // cyan (room name hue)
    [InlineData(7, false)]   // default white
    public void IsAllGreen_ByForegroundIndex(int idx, bool expected)
        => Assert.Equal(expected, ActionEmoteClassifier.IsAllGreen(Line("You growl.", idx)));

    [Fact]
    public void IsAllGreen_MixedColours_False()
    {
        const string text = "You growl.";
        CellAttributes[] attrs = new CellAttributes[text.Length];
        for (int i = 0; i < text.Length; i++)
            attrs[i] = CellAttributes.Default.WithForeground(TerminalColor.Indexed(i == 0 ? 7 : 2));
        Assert.False(ActionEmoteClassifier.IsAllGreen(
            new LineExtractor.EmittedLine(text, attrs, DateTimeOffset.UnixEpoch, false)));
    }

    [Fact]
    public void IsAllGreen_NoAttributes_False()
        => Assert.False(ActionEmoteClassifier.IsAllGreen(
            new LineExtractor.EmittedLine("You growl.", Array.Empty<CellAttributes>(), DateTimeOffset.UnixEpoch, false)));

    // ----- Own POV ------------------------------------------------------

    [Theory]
    [InlineData("You hug Suijin close!")]
    [InlineData("You growl.")]
    [InlineData("You wave to Suijin!")]
    [InlineData("You look around looking for someone to tickle.")]   // `tickle`, no target
    [InlineData("You leap in the air!")]                             // `jump`
    [InlineData("You slap yourself!")]
    [InlineData("You grin evilly.")]                                 // `egrin`
    public void Classify_Own(string text)
        => Assert.Equal(ActionEmoteClassifier.Kind.Own,
            ActionEmoteClassifier.Classify(text, _ => false, out _));

    [Theory]
    [InlineData("You are carrying 14 gold crowns, 9 silver nobles.")]
    [InlineData("You have the following keys: obsidian key.")]
    [InlineData("You feel ferocious!")]
    [InlineData("You notice wooden chair here.")]
    [InlineData("You gain 100 experience.")]
    [InlineData("You invoke the way of the tiger.")]
    public void Classify_OwnNonAction_None(string text)
        => Assert.Equal(ActionEmoteClassifier.Kind.None,
            ActionEmoteClassifier.Classify(text, _ => false, out _));

    // ----- Others' POV --------------------------------------------------

    [Theory]
    [InlineData("Fujin hugs you close!")]
    [InlineData("Fujin grins slyly at you.")]
    [InlineData("Fujin growls ominously.")]                 // self-action seen by the room
    [InlineData("Fujin leaps in the air foolishly!")]
    public void Classify_Other_KnownPlayer(string text)
    {
        ActionEmoteClassifier.Kind k = ActionEmoteClassifier.Classify(text, n => n == "Fujin", out string? actor);
        Assert.Equal(ActionEmoteClassifier.Kind.Other, k);
        Assert.Equal("Fujin", actor);
    }

    [Fact]
    public void Classify_Other_UnknownActor_None()
        => Assert.Equal(ActionEmoteClassifier.Kind.None,
            ActionEmoteClassifier.Classify("Griswold ponders the meaning.", _ => false, out _));

    // Non-action lines rejected by the HEAD test. Only "Obvious exits:" is actually
    // all-green (and it fails here on the missing terminal '.'/'!'); the label:value
    // status lines fail the colour gate upstream (only the label cell is green), but
    // the head test rejects them anyway — belt-and-suspenders.
    [Theory]
    [InlineData("Obvious exits: north, east, closed gate west")]
    [InlineData("Wealth: 173100 copper farthings")]
    [InlineData("Encumbrance: 3919/7320 - Medium [53%]")]
    [InlineData("Name: Fujin WuzHere")]
    public void Classify_NonActionShape_None(string text)
        => Assert.Equal(ActionEmoteClassifier.Kind.None,
            ActionEmoteClassifier.Classify(text, _ => true, out _));

    // Movement / party / chat from a KNOWN player are still excluded.
    [Theory]
    [InlineData("Suijin walks into the room from the west.")]
    [InlineData("Fujin just left to the east.")]
    [InlineData("Suijin started to follow you.")]
    [InlineData("Raijin just moved to the back rank in your group.")]
    [InlineData("Fujin says something silly.")]
    public void Classify_KnownPlayerNonAction_None(string text)
        => Assert.Equal(ActionEmoteClassifier.Kind.None,
            ActionEmoteClassifier.Classify(text, _ => true, out _));
}

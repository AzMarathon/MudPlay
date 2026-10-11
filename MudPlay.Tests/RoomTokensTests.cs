using System.Text.RegularExpressions;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// RoomTokens.TryNext replaced the pattern (\d+)/(\d+) in every index built from a
// monster's Summoned By. It must find the same references in the same order, and
// refuse the same numbers, as that pattern followed by int.TryParse did.
public sealed class RoomTokensTests
{
    private static readonly Regex Pattern = new(@"(\d+)/(\d+)");

    private static List<(int Map, int Room)> ByPattern(string text)
    {
        List<(int, int)> found = new();
        foreach (Match m in Pattern.Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out int map)) map = 0;
            if (!int.TryParse(m.Groups[2].Value, out int room)) room = 0;
            found.Add((map, room));
        }
        return found;
    }

    private static List<(int Map, int Room)> ByScan(string text)
    {
        List<(int, int)> found = new();
        int at = 0;
        while (RoomTokens.TryNext(text, ref at, out int map, out int room)) found.Add((map, room));
        Assert.Equal(text.Length, at);
        return found;
    }

    [Theory]
    [InlineData("")]
    [InlineData("Room 1/1678")]
    [InlineData("Group(lair): 1/547, Group: 9/12, Room 14/1077")]
    [InlineData("oup: 1/547, Group: 102, Textblock #863")]
    [InlineData("1/2/3/4")]
    [InlineData("12/")]
    [InlineData("/12")]
    [InlineData("12/a34/5")]
    [InlineData("12 /3, 4/ 5, 6//7")]
    [InlineData("0/5, 5/0, 007/0008")]
    [InlineData("99999999999/1, 1/99999999999, 2147483647/2147483648")]
    [InlineData("٣/٤, 1/٥, ５/2, ²/3, 1٣/4")]
    [InlineData("Spell #491")]
    public void Finds_what_the_pattern_finds(string text)
    {
        Assert.Equal(ByPattern(text), ByScan(text));
    }

    [Fact]
    public void Finds_what_the_pattern_finds_in_arbitrary_text()
    {
        // The characters a Summoned By cell is made of, digits of two other scripts
        // and a superscript, which is a number but no digit.
        const string Alphabet = "0123456789//,,  :()Gro#٣５²";
        Random random = new(20261011);
        for (int i = 0; i < 20000; i++)
        {
            char[] chars = new char[random.Next(0, 24)];
            for (int c = 0; c < chars.Length; c++) chars[c] = Alphabet[random.Next(Alphabet.Length)];
            string text = new(chars);
            Assert.True(ByPattern(text).SequenceEqual(ByScan(text)), $"differs on \"{text}\"");
        }
    }

    [Fact]
    public void Resumes_after_the_reference_it_returned()
    {
        const string Text = "Group: 3/40, Room 5/6";
        int at = 0;
        Assert.True(RoomTokens.TryNext(Text, ref at, out int map, out int room));
        Assert.Equal((3, 40), (map, room));
        Assert.Equal(Text.IndexOf(','), at);
        Assert.True(RoomTokens.TryNext(Text, ref at, out map, out room));
        Assert.Equal((5, 6), (map, room));
        Assert.False(RoomTokens.TryNext(Text, ref at, out map, out room));
        Assert.Equal((0, 0), (map, room));
    }
}

using System.Text;
using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

// The words the break observer answers to are the game's own, from the Stock 1.11p
// command parser run over every prefix (GAME_MECHANICS "Command words and
// abbreviations"). These pin the table and what the observer forwards.
public sealed class OutboundBreakObserverTests
{
    private sealed class Seen
    {
        public List<string> Breaks { get; } = new();
        public List<(string Word, string? Target)> Attacks { get; } = new();
        public List<string?> AskedBy { get; } = new();
        public OutboundBreakObserver Observer { get; }

        public Seen()
            => Observer = new OutboundBreakObserver(
                isAttackSpell: c => c.Equals("aslt", StringComparison.OrdinalIgnoreCase),
                onBreak: (w, by) => { Breaks.Add(w); AskedBy.Add(by); },
                onAttack: (w, t, by) => { Attacks.Add((w, t)); AskedBy.Add(by); });

        public void Send(string line, string? askedBy = null)
            => Observer.ObserveOutbound(Encoding.Latin1.GetBytes(line + "\r"), askedBy);
    }

    [Theory]
    [InlineData("a")] [InlineData("at")] [InlineData("att")] [InlineData("atta")]
    [InlineData("attac")] [InlineData("attack")] [InlineData("au")]
    [InlineData("aa")] [InlineData("bas")] [InlineData("bash")]
    [InlineData("al")] [InlineData("all")] [InlineData("allo")] [InlineData("allou")] [InlineData("allout")]
    [InlineData("forc")] [InlineData("force")]
    [InlineData("bs")] [InlineData("backs")] [InlineData("backst")] [InlineData("backsta")] [InlineData("backstab")]
    [InlineData("sma")] [InlineData("smas")] [InlineData("smash")]
    [InlineData("pu")] [InlineData("pun")] [InlineData("punc")] [InlineData("punch")]
    [InlineData("pur")] [InlineData("purg")]
    [InlineData("kic")] [InlineData("kick")]
    [InlineData("ju")] [InlineData("jum")] [InlineData("jump")] [InlineData("jumpk")]
    [InlineData("jumpki")] [InlineData("jumpkic")] [InlineData("jumpkick")]
    [InlineData("ATTACK")]
    public void AttackWords_AreAttacks(string word)
        => Assert.True(AttackCommandWords.IsAttack(word));

    [Theory]
    [InlineData("")] [InlineData("b")] [InlineData("ba")] [InlineData("bac")] [InlineData("back")]
    [InlineData("sm")] [InlineData("k")] [InlineData("ki")] [InlineData("p")] [InlineData("j")]
    [InlineData("fo")] [InlineData("for")] [InlineData("kil")] [InlineData("kill")]
    [InlineData("purge")] [InlineData("attacks")] [InlineData("bashes")] [InlineData("ab")]
    [InlineData("br")] [InlineData("break")]
    public void OtherWords_AreNot(string word)
        => Assert.False(AttackCommandWords.IsAttack(word));

    [Theory]
    [InlineData("bre", true)] [InlineData("brea", true)] [InlineData("break", true)] [InlineData("Break", true)]
    [InlineData("b", false)] [InlineData("br", false)] [InlineData("breaks", false)] [InlineData("bri", false)]
    public void BreakIsTakenFromThreeLetters(string word, bool isBreak)
        => Assert.Equal(isBreak, AttackCommandWords.IsBreak(word));

    [Fact]
    public void Break_IsForwarded_WithTheWordAsSent()
    {
        Seen s = new();

        s.Send("bre");
        s.Send("break");

        Assert.Equal(new[] { "bre", "break" }, s.Breaks);
        Assert.Empty(s.Attacks);
    }

    [Fact]
    public void Attack_IsForwarded_WithWhatItWasAimedAt()
    {
        Seen s = new();

        s.Send("a giant rat");
        s.Send("kick");
        s.Send("aslt kobold thief");

        Assert.Equal(new (string, string?)[] { ("a", "giant rat"), ("kick", null), ("aslt", "kobold thief") },
            s.Attacks);
        Assert.Empty(s.Breaks);
    }

    // A line a party member's `@do` sent is forwarded with that member's name; the
    // user's own carries none.
    [Fact]
    public void WhoAskedForTheLine_IsForwarded()
    {
        Seen s = new();

        s.Send("break", askedBy: "Leader");
        s.Send("a giant rat", askedBy: "Leader");
        s.Send("break");

        Assert.Equal(new string?[] { "Leader", "Leader", null }, s.AskedBy);
    }

    // A direction after the bash command is the door on that exit, under any of the
    // command's names.
    [Theory]
    [InlineData("bash n")]
    [InlineData("bas north")]
    [InlineData("aa w")]
    [InlineData("allout se")]
    [InlineData("force up")]
    public void BashAtADirection_IsADoor_NotAnAttack(string line)
    {
        Seen s = new();

        s.Send(line);

        Assert.Empty(s.Attacks);
    }

    [Theory]
    [InlineData("br hello")]
    [InlineData("kill giant rat")]
    [InlineData("i")]
    [InlineData("mihe")]
    [InlineData("")]
    public void AnythingElse_IsForwardedToNeither(string line)
    {
        Seen s = new();

        s.Send(line);

        Assert.Empty(s.Breaks);
        Assert.Empty(s.Attacks);
    }

    // A text exit of the room we stand in is a move: it doesn't lift the hold, though
    // its first word spells jumpkick. A real exit from each data set
    // (OutboundAttackObserverTests.RoomWithJumpExit).
    [Theory]
    [InlineData(true, "jump moat")]
    [InlineData(false, "jump pool")]
    public void TextExitOfTheRoom_IsForwardedAsNoAttack(bool stock, string line)
    {
        MudPlay.Game.Map.Room room = OutboundAttackObserverTests.RoomWithJumpExit(stock);
        List<string> attacks = new();
        OutboundBreakObserver observer = new(
            isAttackSpell: _ => false,
            onBreak: (_, _) => { },
            onAttack: (word, _, _) => attacks.Add(word),
            isExitCommandHere: room.HasExitCommand);

        observer.ObserveOutbound(Encoding.Latin1.GetBytes(line + "\r"));
        Assert.Empty(attacks);

        observer.ObserveOutbound(Encoding.Latin1.GetBytes("jump giant rat\r"));
        Assert.Equal(new[] { "jump" }, attacks);
    }

    // A paste is no bare command.
    [Fact]
    public void LongPayload_IsIgnored()
    {
        Seen s = new();

        s.Send("break " + new string('x', 80));

        Assert.Empty(s.Breaks);
    }
}

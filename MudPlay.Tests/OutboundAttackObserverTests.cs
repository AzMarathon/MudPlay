using System;
using System.Collections.Generic;
using System.Text;
using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

// Coverage for OutboundAttackObserver — the wire sniffer that forwards a hand-typed
// physical attack verb so the combat engine can treat it as a user override. Every
// recognised verb (bare or with a target) fires; anything else stays silent.
public sealed class OutboundAttackObserverTests
{
    private static (OutboundAttackObserver obs, List<string> seen, List<string?> targets) New()
    {
        List<string> seen = new();
        List<string?> targets = new();
        OutboundAttackObserver obs = new((verb, target) => { seen.Add(verb); targets.Add(target); });
        return (obs, seen, targets);
    }

    private static void Send(OutboundAttackObserver obs, string command)
        => obs.ObserveOutbound(Encoding.Latin1.GetBytes(command + "\r"));

    [Fact]
    public void EveryAttackVerb_Fires_BareOrTargeted()
    {
        (OutboundAttackObserver obs, List<string> seen, _) = New();
        foreach (string cmd in new[]
                 { "a", "at", "att", "aa", "bash", "smash", "sma", "bs" })
            Send(obs, cmd);
        Send(obs, "a giant rat");   // first token is the verb, rest the target
        Send(obs, "  AA  Orc ");    // case-insensitive + trimmed

        Assert.Equal(new[]
        {
            "a", "at", "att", "aa", "bash", "smash", "sma", "bs", "a", "AA",
        }, seen);
    }

    [Fact]
    public void Target_Forwarded_NullForBareVerb()
    {
        (OutboundAttackObserver obs, _, List<string?> targets) = New();
        Send(obs, "a giant rat");   // targeted
        Send(obs, "aa");            // bare — self-resolving, no target
        Send(obs, "  bash  Orc ");  // trimmed remainder
        Assert.Equal(new string?[] { "giant rat", null, "Orc" }, targets);
    }

    [Fact]
    public void NonAttackCommand_StaysSilent()
    {
        (OutboundAttackObserver obs, List<string> seen, _) = New();
        Send(obs, "n");          // cardinal move
        Send(obs, "mmis rat");   // a cast, not a physical attack
        Send(obs, "look n");     // peek
        Send(obs, "get all");    // loot
        Send(obs, "auction sword");  // starts with 'a' but isn't the attack verb
        Assert.Empty(seen);
    }

    // The game's command table starts smash at `sma`; `sm` is no command, so the
    // line is spoken or refused and no attack was taken.
    [Fact]
    public void Sm_IsNotAnAttackVerb()
    {
        (OutboundAttackObserver obs, List<string> seen, _) = New();
        Send(obs, "sm");
        Send(obs, "sm giant rat");
        Assert.Empty(seen);
    }

    // A direction after `bash` is the door on that exit. The walker bashes doors on
    // its own, so reading one as a typed attack held the engine's attack for the round.
    [Fact]
    public void BashAtADirection_IsADoorNotAnAttack()
    {
        (OutboundAttackObserver obs, List<string> seen, List<string?> targets) = New();
        foreach (string door in new[] { "bash n", "bash sw", "BASH North", "bash  u ", "bash down" })
            Send(obs, door);
        Assert.Empty(seen);

        // `aa` is the same command as `bash`, door form included.
        foreach (string door in new[] { "aa n", "AA sw", "aa down" })
            Send(obs, door);
        Assert.Empty(seen);

        Send(obs, "bash nasty orc");   // starts with a direction letter, but is a monster
        Send(obs, "smash n");          // smash has no door form
        Assert.Equal(new[] { "bash", "smash" }, seen);
        Assert.Equal(new string?[] { "nasty orc", "n" }, targets);
    }

    // The override reads the same table as the user's break hold (user, 2026-10-10),
    // so it knows every spelling the game takes: backstab in full, the martial-arts
    // strikes, the longer leads of attack and smash.
    [Theory]
    [InlineData("backs")] [InlineData("backst")] [InlineData("backsta")] [InlineData("backstab")]
    [InlineData("pu")] [InlineData("pun")] [InlineData("punc")] [InlineData("punch")]
    [InlineData("kic")] [InlineData("kick")]
    [InlineData("ju")] [InlineData("jum")] [InlineData("jump")] [InlineData("jumpk")]
    [InlineData("jumpki")] [InlineData("jumpkic")] [InlineData("jumpkick")]
    [InlineData("atta")] [InlineData("attac")] [InlineData("attack")]
    [InlineData("smas")] [InlineData("bas")]
    public void WordsOfTheSharedTable_Fire(string verb)
    {
        (OutboundAttackObserver obs, List<string> seen, List<string?> targets) = New();
        Send(obs, verb + " giant rat");
        Assert.Equal(new[] { verb }, seen);
        Assert.Equal(new string?[] { "giant rat" }, targets);
    }

    // Short of the lead the game takes, or past the word, it is no attack.
    [Theory]
    [InlineData("back")] [InlineData("ki")] [InlineData("p")] [InlineData("j")]
    [InlineData("kicks")] [InlineData("punches")] [InlineData("kill")]
    public void WordsTheGameDoesNotTakeForAnAttack_StaySilent(string verb)
    {
        (OutboundAttackObserver obs, List<string> seen, _) = New();
        Send(obs, verb + " giant rat");
        Assert.Empty(seen);
    }

    // The bash command is a door under each of its names.
    [Theory]
    [InlineData("bas n")] [InlineData("allout se")] [InlineData("force up")]
    public void BashUnderItsOtherNames_AtADirection_IsADoor(string line)
    {
        (OutboundAttackObserver obs, List<string> seen, _) = New();
        Send(obs, line);
        Assert.Empty(seen);
    }

    [Fact]
    public void EmptyOrOversized_Ignored()
    {
        (OutboundAttackObserver obs, List<string> seen, _) = New();
        obs.ObserveOutbound(ReadOnlySpan<byte>.Empty);
        obs.ObserveOutbound(Encoding.Latin1.GetBytes("\r\n"));
        obs.ObserveOutbound(Encoding.Latin1.GetBytes(new string('x', 200)));
        Assert.Empty(seen);
    }
}

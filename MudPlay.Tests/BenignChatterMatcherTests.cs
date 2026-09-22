using System;
using MudPlay.Game;
using Xunit;

namespace MudPlay.Tests;

// BenignChatterMatcher recognizes non-spell wire chatter so the unrecognized-line watcher
// drops it from the review queue. The negative cases matter most: each shape is anchored
// so it can never swallow a genuine unknown spell / proc line (what the queue exists for).
public sealed class BenignChatterMatcherTests
{
    [Theory]
    // Player departures — every compass direction.
    [InlineData("Miserable just left to the east.")]
    [InlineData("Client just left to the south.")]
    [InlineData("Revolution just left to the northwest.")]
    [InlineData("MudPlay WuzHere just left to the southeast.")]
    // Disconnect — with and without the trailing period.
    [InlineData("Durnan just disconnected!!!")]
    [InlineData("Phrixas just disconnected!!!.")]
    // Follow, toll, empty say.
    [InlineData("You are following Client.")]
    [InlineData("You just paid 5 gold crowns in toll charges.")]
    [InlineData("You say \"\"")]
    // Also-here — period-terminated and the wrapped (no-period) first line.
    [InlineData("Also here: river ray, albino salamander, river ray.")]
    [InlineData("Also here: albino salamander, river ray, albino salamander, river")]
    // Suicide-password advisory block.
    [InlineData("To prevent accidental suicide or reroll, these commands")]
    [InlineData("have been password protected. You have not yet entered")]
    [InlineData("your suicide password, so please do so soon using the")]
    [InlineData("SET SUICIDE command.")]
    // Regen / illumination status labels.
    [InlineData("Regen Time:            3m 30s")]
    [InlineData("Room Illu:            0 (200)")]
    public void IsBenign_RecognizesKnownChatter(string line)
        => Assert.True(BenignChatterMatcher.IsBenign(line));

    [Theory]
    // Genuine unknown room-spell / monster / proc lines must NOT be suppressed.
    [InlineData("A dry twig snaps loudly behind you.")]
    [InlineData("An ominous wind blows through the trees.")]
    [InlineData("The goblin shaman chants a guttural incantation!")]
    [InlineData("The dark elf priest hurls a searing bolt at Suijin!")]
    // Near-misses of the benign shapes that are NOT the benign line.
    [InlineData("Balgor just left the Realm.")]               // routed elsewhere, not a directional departure
    [InlineData("You are following the trail of blood.")]     // lowercase target → not a player-follow notice
    [InlineData("The shade slips away to the east, unseen.")] // not the "just left to the <dir>" shape
    public void IsBenign_LeavesGenuineLinesAlone(string line)
        => Assert.False(BenignChatterMatcher.IsBenign(line));

    [Fact]
    public void GearSwap_KnownPlayer_IsRecognized()
    {
        Func<string, bool> known = n => n == "Client";
        Assert.True(BenignChatterMatcher.IsOtherPlayerGearSwap("Client wears runed cowl!", known));
        Assert.True(BenignChatterMatcher.IsOtherPlayerGearSwap("Client removes jeweled turban!", known));
    }

    [Fact]
    public void GearSwap_UnknownName_IsNotSuppressed()
    {
        // Roster gate: a same-shaped line whose subject isn't a known player stays in the
        // queue — it could be an uncatalogued monster / spell line.
        Func<string, bool> known = _ => false;
        Assert.False(BenignChatterMatcher.IsOtherPlayerGearSwap("The lich removes its own head!", known));
        Assert.False(BenignChatterMatcher.IsOtherPlayerGearSwap("Client wears runed cowl!", known));
    }
}

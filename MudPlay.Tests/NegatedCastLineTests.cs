using MudPlay.Game;
using MudPlay.Game.Spells;
using Xunit;

namespace MudPlay.Tests;

// The game's lines for a cast that did nothing to its target, as the caster, the
// target and the room are each told. Paradigm's wording is from report
// paradigm-20261011-165726 (holding breath, negated by a worn fish-helm) and the
// two of 2026-10-03 (a monster's necromantic beam); Stock's is the 1.11p engine's
// own format strings. The names are stand-ins.
public sealed class NegatedCastLineTests
{
    [Theory]
    // Paradigm, to the caster.
    [InlineData("You attempt to cast holding breath at Aldric, but it's negated!", "You", "Aldric", "holding breath")]
    [InlineData("You attempt to cast harm at thin nanati shadow, but it's negated!", "You", "thin nanati shadow", "harm")]
    // Paradigm, to the target.
    [InlineData("You negate Aldric's cast of holding breath!", "Aldric", "You", "holding breath")]
    [InlineData("You negate vengeful spirit's cast of necromantic beam!", "vengeful spirit", "You", "necromantic beam")]
    [InlineData("You negate Goru-Nezar's cast of ice storm!", "Goru-Nezar", "You", "ice storm")]
    // Paradigm, to the room. A room's spell is cast by each one in it, so the two
    // names can be the same.
    [InlineData("Aldric negates Aldric's cast of holding breath!", "Aldric", "Aldric", "holding breath")]
    [InlineData("Borin Ironfoot negates vengeful spirit's cast of necromantic beam!", "vengeful spirit", "Borin Ironfoot", "necromantic beam")]
    // Stock: "resisted", and no "cast of" when the caster is a player.
    [InlineData("You attempt to cast harm at orc shaman, but the spell is resisted.", "You", "orc shaman", "harm")]
    [InlineData("You resisted orc shaman's cast of harm.", "orc shaman", "You", "harm")]
    [InlineData("You resisted Aldric's holding breath.", "Aldric", "You", "holding breath")]
    [InlineData("Borin resisted orc shaman's cast of harm.", "orc shaman", "Borin", "harm")]
    [InlineData("Borin resisted Aldric's holding breath.", "Aldric", "Borin", "holding breath")]
    public void ANegatedCast_IsRead_WithWhoCastWhatAtWhom(string line, string caster, string target, string spell)
    {
        Assert.True(NegatedCastLine.TryRead(line, out NegatedCastLine.Cast cast));
        Assert.Equal(new NegatedCastLine.Cast(caster, target, spell), cast);
    }

    // The spell need not be one the set has: the shape is the game's own.
    [Fact]
    public void ASpellNobodyHasHeardOf_IsStillANegatedCast()
    {
        Assert.True(NegatedCastLine.TryRead("You negate Aldric's cast of zzyzx ward!", out NegatedCastLine.Cast cast));
        Assert.Equal("zzyzx ward", cast.Spell);
    }

    [Theory]
    // Quoted in chat, or with something after it: not the game's line.
    [InlineData("Aldric gossips: You negate Borin's cast of bless!")]
    [InlineData("Aldric says \"Borin negates Carl's cast of bless!\"")]
    [InlineData("From Aldric (telepath): You attempt to cast bless at Borin, but it's negated!")]
    [InlineData("You say \"You resisted orc shaman's cast of harm.\"")]
    [InlineData("You negate Aldric's cast of bless! Twice now.")]
    // The words in other lines.
    [InlineData("You cast negate magic on Aldric!")]
    [InlineData("The orc shaman attempts to cast harm on you, but you resist!")]
    [InlineData("You attempt to cast harm, but fail.")]
    [InlineData("You are freezing cold!")]
    [InlineData("")]
    public void AnythingElse_IsNot(string line)
        => Assert.False(NegatedCastLine.TryRead(line, out _));

    // Each names the spell, so none is a spell's own message: the unrecognized-line
    // watcher passes them over.
    [Theory]
    [InlineData("Aldric negates Aldric's cast of holding breath!")]
    [InlineData("You negate Aldric's cast of holding breath!")]
    [InlineData("You attempt to cast holding breath at Aldric, but it's negated!")]
    [InlineData("You resisted orc shaman's cast of harm.")]
    public void ANegatedCast_IsNotAnUnrecognizedLine(string line)
        => Assert.True(BenignChatterMatcher.IsBenign(line));

    // A resist is not a negate (user, 2026-10-17): only the caster's "it's negated!"
    // line is read as our cast negated.
    [Theory]
    [InlineData("You attempt to cast bless at Aldric, but it's negated!", true)]
    [InlineData("You attempt to cast bless at Aldric, but the spell is resisted.", false)]
    [InlineData("You attempt to cast negate magic at Aldric, but the spell is resisted.", false)]
    [InlineData("You negate Aldric's cast of bless!", false)]
    [InlineData("Borin negates Aldric's cast of bless!", false)]
    public void OwnNegate_OnlyTheCasterNegatedLine(string line, bool expected)
    {
        Assert.Equal(expected, NegatedCastLine.TryReadOwnNegate(line, out string target, out string spell));
        if (expected)
        {
            Assert.Equal("Aldric", target);
            Assert.Equal("bless", spell);
        }
    }
}

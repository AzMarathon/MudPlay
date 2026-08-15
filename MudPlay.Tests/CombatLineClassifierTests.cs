using MudPlay.Game.Combat;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Generic color+wording combat-line classification, verified against the exact
// wire lines from the orc-lieutenant / ankheg captures. Cyan (indexed 6) = miss or
// dodge; dark-red (indexed 1, non-bold) = armor block; bright-red (indexed 1 +
// bold) + "for N damage" = the player's own hit.
public sealed class CombatLineClassifierTests
{
    private static readonly TerminalColor Cyan     = TerminalColor.Indexed(6);
    private static readonly TerminalColor Red      = TerminalColor.Indexed(1);
    private static readonly TerminalColor White    = TerminalColor.Indexed(7);

    private static CombatLineKind Classify(string text, TerminalColor fg, bool bold = false, bool inWindow = true)
        => CombatLineClassifier.Classify(text, fg, bold, inWindow);

    [Fact]
    public void MonsterSwingMiss_Cyan_VsYou()
        => Assert.Equal(CombatLineKind.MonsterMissYou,
            Classify("The fierce orc lieutenant swings at you with their scimitar!", Cyan));

    [Fact]
    public void MonsterSwingMiss_Cyan_VsOther()
        => Assert.Equal(CombatLineKind.MonsterMissOther,
            Classify("The angry orc lieutenant swings at Fujin with their scimitar!", Cyan));

    [Fact]
    public void BlindedLunge_Cyan_VsYou_IsMiss()
        => Assert.Equal(CombatLineKind.MonsterMissYou,
            Classify("The large ankheg lunges at you!", Cyan));

    [Fact]
    public void ArmorGlance_DarkRed_VsYou()
        => Assert.Equal(CombatLineKind.ArmorBlockYou,
            Classify("The orc lieutenant cuts you, but the swing glances off!", Red, bold: false));

    [Fact]
    public void ArmorGlance_DarkRed_VsOther()
        => Assert.Equal(CombatLineKind.ArmorBlockOther,
            Classify("The orc lieutenant cuts Fujin, but the swing glances off!", Red, bold: false));

    [Fact]
    public void Dodge_Cyan_VsYou()
        => Assert.Equal(CombatLineKind.DodgeYou,
            Classify("The nasty dark monk spins and kicks at you, but you dodge!", Cyan));

    [Fact]
    public void PlayerMiss_Cyan_StartsWithYou()
        => Assert.Equal(CombatLineKind.PlayerMiss,
            Classify("You miss your throw at nasty orc lieutenant!", Cyan));

    [Fact]
    public void PlayerHit_BrightRed_HasDamage()
        => Assert.Equal(CombatLineKind.PlayerHit,
            Classify("You hurl your throwing hammer and strike fierce orc lieutenant for 67 damage!",
                     Red, bold: true));

    [Fact]
    public void MonsterHit_WithDamage_VsYou()
        => Assert.Equal(CombatLineKind.MonsterHitYou,
            Classify("The large ankheg spits acid on you for 32 damage!", Red, bold: false));

    [Fact]
    public void MonsterHit_WithDamage_VsOther()
        => Assert.Equal(CombatLineKind.MonsterHitOther,
            Classify("The dark monk punches Fujin in the head for 7 damage!", Red, bold: false));

    [Fact]
    public void OutsideCombatWindow_IsNone()
        => Assert.Equal(CombatLineKind.None,
            Classify("The fierce orc lieutenant swings at you with their scimitar!", Cyan, inWindow: false));

    [Fact]
    public void NonCombatWhiteLine_IsNone()
        => Assert.Equal(CombatLineKind.None,
            Classify("Obvious exits: north, south, west", White));

    [Fact]
    public void NoteMonsterDeath_Matched_TagsWithName()
    {
        using var c = new CombatLineClassifier(new MudPlay.Services.MessageRouter());
        c.NoteMonsterDeath("rotworm", inferred: false);
        Assert.Contains("[Monster Death: rotworm]", c.RenderLog());
    }

    [Fact]
    public void NoteMonsterDeath_Inferred_FlagsUnrecognized()
    {
        using var c = new CombatLineClassifier(new MudPlay.Services.MessageRouter());
        c.NoteMonsterDeath(null, inferred: true);
        Assert.Contains("message not recognized", c.RenderLog());
    }
}

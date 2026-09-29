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

    // A thorns/ShockShield reflect: a worn item strikes the attacker back. It's white
    // (default colour) and its victim is the MONSTER, so it must NOT read as a monster
    // hitting someone. The item wording varies ("armour spikes", "collar spikes", …),
    // so recognition is by colour + "for N damage" + a non-"you" target, not wording.
    [Fact]
    public void ShockShieldReflect_White_IsReflect()
        => Assert.Equal(CombatLineKind.Reflect,
            Classify("The armour spikes stab fierce orc captain for 5 damage!", White));

    [Fact]
    public void ShockShieldReflect_DefaultColour_IsReflect()
        => Assert.Equal(CombatLineKind.Reflect,
            Classify("The collar spikes stab tentacled mass for 6 damage!", TerminalColor.Default));

    // The reflect fix must not steal genuine incoming hits: a red "for N damage" line
    // targeting a party member is still a monster hit, not a reflect.
    [Fact]
    public void MonsterHitOther_Red_NotMisreadAsReflect()
        => Assert.Equal(CombatLineKind.MonsterHitOther,
            Classify("The armour spikes stab fierce orc captain for 5 damage!", Red, bold: false));

    [Fact]
    public void OutsideCombatWindow_IsNone()
        => Assert.Equal(CombatLineKind.None,
            Classify("The fierce orc lieutenant swings at you with their scimitar!", Cyan, inWindow: false));

    // Poison damage starts "You" like our own hit, but it's damage we took
    // (report paradigm-20260929-003750).
    [Fact]
    public void PoisonTick_IsDamageToYou_NotYourHit()
        => Assert.Equal(CombatLineKind.DamageYou,
            Classify("You are poisoned for 2 damage!", Red, bold: true));

    // The smash penalty — a smash's secondary effect — and the engine's "just …"
    // outcome wordings, read from the words since they carry no colour cue.
    [Theory]
    [InlineData("You are smashed to the ground!", CombatLineKind.SmashedYou)]
    [InlineData("You smashed Bob to the ground!", CombatLineKind.SmashedOther)]
    [InlineData("Bob is smashed to the ground defenseless!", CombatLineKind.SmashedOther)]
    [InlineData("The orc is smashed to the floor defenseless!", CombatLineKind.SmashedOther)]
    [InlineData("Bob's just glanced off of the orc's armour.", CombatLineKind.ArmorBlockOther)]
    [InlineData("Bob just dodged an attack from the orc.", CombatLineKind.DodgeOther)]
    [InlineData("The orc just missed an attack against Bob.", CombatLineKind.MonsterMissOther)]
    [InlineData("You take 12 damage from the flames!", CombatLineKind.DamageYou)]
    [InlineData("A bolt of lightning from the heavens strikes you for 12 points damage!", CombatLineKind.DamageYou)]
    [InlineData("You sing the song of blasting, causing 40 damage to your foes!", CombatLineKind.PlayerHit)]
    public void EngineOutcomeWordings(string line, CombatLineKind kind)
        => Assert.Equal(kind, Classify(line, White));

    [Fact]
    public void NonCombatWhiteLine_IsNone()
        => Assert.Equal(CombatLineKind.None,
            Classify("Obvious exits: north, south, west", White));

    [Fact]
    public void NoteMonsterDeath_MarksKillWithExp()
    {
        using var c = new CombatLineClassifier(new MudPlay.Services.MessageRouter());
        c.NoteMonsterDeath(950);
        Assert.Contains("[Monster Death: +950 exp]", c.RenderLog());
    }

    private static void Dispatch(MudPlay.Services.MessageRouter router, string text)
        => router.Dispatch(new LineExtractor.EmittedLine(
            text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));

    // The round ledger's reading lands on the same line's entry whichever of the two
    // LineDispatched handlers sees the line first.
    [Fact]
    public void LedgerTag_AfterTheClassifierSawTheLine_TagsThatEntry()
    {
        var router = new MudPlay.Services.MessageRouter();
        using var c = new CombatLineClassifier(router);
        Dispatch(router, "*Combat Engaged*");
        Dispatch(router, "Bob slashes goblin for 9 damage!");
        c.NoteLedger("Bob slashes goblin for 9 damage!", "Bob → goblin 9");

        ClassifiedLine e = c.SnapshotLog()[^1];
        Assert.Equal("Bob slashes goblin for 9 damage!", e.Text);
        Assert.Equal("Bob → goblin 9", e.Ledger);
        Assert.Contains("[Ledger: Bob → goblin 9]", c.RenderLog());
    }

    [Fact]
    public void LedgerTag_BeforeTheClassifierSawTheLine_WaitsForIt()
    {
        var router = new MudPlay.Services.MessageRouter();
        using var c = new CombatLineClassifier(router);
        Dispatch(router, "*Combat Engaged*");
        c.NoteLedger("Bob slashes goblin for 9 damage!", "Bob → goblin 9");
        Dispatch(router, "Bob slashes goblin for 9 damage!");

        Assert.Single(c.SnapshotLog(), e => e.Ledger == "Bob → goblin 9");
    }

    // A damage line outside our own combat window (a party member's fight) is still
    // kept once the ledger read it.
    [Fact]
    public void LedgerTag_OutsideTheCombatWindow_KeepsTheLine()
    {
        var router = new MudPlay.Services.MessageRouter();
        using var c = new CombatLineClassifier(router);
        Dispatch(router, "The goblin bites Bob for 2 damage!");
        c.NoteLedger("The goblin bites Bob for 2 damage!", "goblin → Bob 2");

        ClassifiedLine e = Assert.Single(c.SnapshotLog());
        Assert.Equal("goblin → Bob 2", e.Ledger);
    }
}

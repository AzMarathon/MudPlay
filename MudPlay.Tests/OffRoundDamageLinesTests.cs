using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Which damage lines the round clock leaves out. The records are the Paradigm seed's
// own wordings for the spells named; which of them a monster casts as its attack
// comes from the Monsters table (dark force, chaos storm and shadow breath here).
public sealed class OffRoundDamageLinesTests
{
    internal const int MagmaHeat = 526, MagmaExplosion = 944, ChaosStorm = 212;
    private const int DarkForce = 225, ShadowBreath = 1011, BurnHits = 589;

    // The rule as AppServices hands it to the round clock and the combat engines,
    // for a character standing in a room with roomSpell (0: none, or not placed).
    internal static Func<string, bool> ProbeIn(int roomSpell)
    {
        OffRoundDamageLines rule = Build();
        return line => rule.IsOffRound(line, roomSpell);
    }

    private static MessageRecord Record(int spell, string name, string caster, string target) => new(
        Id: $"T{spell}", Name: name, Flags: MessageFlags.None, RawFlagsHex: 0,
        CasterMessage: caster, TargetMessage: target, WitnessMessage: "{null}",
        AppliedMessage: "{null}", AppliedEndsWith: "{null}",
        Links: new[] { new GameDataLink("Spells", spell) });

    private static OffRoundDamageLines Build() => new(
        monsterAttackSpells: new[] { DarkForce, ChaosStorm, ShadowBreath },
        records: new[]
        {
            Record(MagmaHeat, "magma heat",
                "You are seared by the flames for {damage} damage!", "You are seared by the flames for {damage} damage!"),
            Record(MagmaExplosion, "magma explosion",
                "A magma explosion hits you for {damage} damage!", "A magma explosion hits you for {damage} damage!"),
            Record(ChaosStorm, "chaos storm",
                "A chaotic storm assaults your foe for {damage} damage!", "A chaotic storm assaults you for {damage} damage!"),
            Record(DarkForce, "dark force",
                "{spellname} is struck by a dark force for {damage} damage!", "You are struck by a dark force for {damage} damage!"),
            Record(ShadowBreath, "shadow breath", "{null}", "Your soul is drained for {damage} damage!"),
            Record(BurnHits, "burn hits",
                "{target} are burned for {damage} damage!", "You are burned for {damage} damage!"),
        });

    // Report paradigm-20261009-120757: the line that was read as a round.
    [Theory]
    [InlineData(MagmaHeat)]   // standing in a room that carries the spell
    [InlineData(0)]           // room not known: the wording still gives it away
    public void RoomHeat_IsOffTheRound(int roomSpell)
    {
        Assert.True(Build().IsOffRound("You are seared by the flames for 46 damage!", roomSpell));
    }

    // A monster's attack spell whose victim text names no dealer is the round all the
    // same. The wording alone used to leave these out: 9 of 366 monster attack spells
    // on Paradigm, one monster's whole attack among them.
    [Theory]
    [InlineData("You are struck by a dark force for 80 damage!", 0)]
    [InlineData("You are struck by a dark force for 80 damage!", MagmaHeat)]
    [InlineData("Your soul is drained for 31 damage!", 0)]
    public void MonsterAttackSpellWithNoDealerNamed_IsTheRound(string line, int roomSpell)
    {
        Assert.False(Build().IsOffRound(line, roomSpell));
    }

    // A room spell worded with a subject reads like anyone's hit. Only the room we
    // stand in tells it from one.
    [Fact]
    public void SubjectWordedRoomSpell_IsOffTheRound_OnlyInItsOwnRoom()
    {
        OffRoundDamageLines lines = Build();
        Assert.True(lines.IsOffRound("A magma explosion hits you for 12 damage!", MagmaExplosion));
        Assert.False(lines.IsOffRound("A magma explosion hits you for 12 damage!", 0));
        Assert.False(lines.IsOffRound("A magma explosion hits you for 12 damage!", MagmaHeat));
    }

    // Chaos storm is a room's spell in seven rooms and a monster's attack elsewhere.
    [Fact]
    public void SameTextFromARoomAndFromAMonster_GoesByTheRoom()
    {
        OffRoundDamageLines lines = Build();
        Assert.True(lines.IsOffRound("A chaotic storm assaults you for 30 damage!", ChaosStorm));
        Assert.False(lines.IsOffRound("A chaotic storm assaults you for 30 damage!", 0));
        Assert.False(lines.IsOffRound("A chaotic storm assaults you for 30 damage!", MagmaHeat));
    }

    // An on-hit effect isn't an attack slot's spell: it stays out, and the hit ahead
    // of it is the round.
    [Fact]
    public void OnHitEffect_StaysOffTheRound_AndRealHitsStayOn()
    {
        OffRoundDamageLines lines = Build();
        Assert.True(lines.IsOffRound("You are burned for 5 damage!", 0));
        Assert.False(lines.IsOffRound("The fire beetle bites you for 9 damage!", MagmaHeat));
        Assert.False(lines.IsOffRound("You punch giant hellhound for 51 damage!", MagmaHeat));
        Assert.False(lines.IsOffRound("Forged punches giant hellhound for 51 damage!", MagmaHeat));
    }

    // ----- on the round clock, wired as AppServices wires it -----------------

    private sealed class Clock : IDisposable
    {
        public DateTimeOffset Now = new(2026, 10, 9, 12, 7, 46, 431, TimeSpan.Zero);
        public MessageRouter Router { get; } = new();
        public TickEngine Tick { get; }
        public PlayerState State { get; } = new();
        public TickTimingLog Timing { get; }
        public int RoomSpell;
        public List<DateTimeOffset> Rounds { get; } = new();

        public Clock()
        {
            DefaultPatterns.Seed(Router);
            Tick = new TickEngine(Router, () => Now);
            Timing = new TickTimingLog(State, new RegenTracker(State, () => Now), () => Now);
            OffRoundDamageLines lines = Build();
            Tick.SetOffRoundDamageProbe(line => lines.IsOffRound(line, RoomSpell));
            Tick.CombatTickElapsed += () =>
            {
                Rounds.Add(Now);
                Timing.NoteRound(Tick.LastCombatTickWasDamageDriven);
            };
            Tick.DamageOffTheRound += Timing.NoteDamageOffTheRound;
        }

        public void Line(string text) => Router.Dispatch(
            new LineExtractor.EmittedLine(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false));

        public string[] DamageRows() => Timing.Render().Split('\n').Where(l => l.Contains("  damage ")).ToArray();

        public void Dispose()
        {
            Tick.Dispose();
            Timing.Dispose();
        }
    }

    [Fact]
    public void MonsterAttackSpellAsTheRoundsOnlyLine_MarksTheRound()
    {
        using Clock c = new() { RoomSpell = MagmaHeat };
        c.Line("You are struck by a dark force for 80 damage!");
        Assert.Single(c.Rounds);
        Assert.Empty(c.DamageRows());
    }

    [Fact]
    public void HitThenItsOnHitEffect_IsOneRound_AndNoDamageRow()
    {
        using Clock c = new();
        c.Line("The fire beetle bites you for 9 damage!");
        c.Now += TimeSpan.FromMilliseconds(3);
        c.Line("You are burned for 5 damage!");
        Assert.Single(c.Rounds);
        Assert.Empty(c.DamageRows());
    }

    [Fact]
    public void SubjectWordedRoomSpell_IsNoRoundInItsRoom_AndARoundFromAMonsterElsewhere()
    {
        using Clock c = new() { RoomSpell = ChaosStorm };
        c.Line("Forged punches giant hellhound for 51 damage!");
        c.Now += TimeSpan.FromSeconds(2);
        c.Line("A chaotic storm assaults you for 30 damage!");
        Assert.Single(c.Rounds);
        Assert.Contains("off the round  gap first  round+2.000s", Assert.Single(c.DamageRows()));

        c.RoomSpell = 0;
        c.Now += TimeSpan.FromSeconds(3.05);
        c.Line("A chaotic storm assaults you for 30 damage!");
        Assert.Equal(2, c.Rounds.Count);
    }

    [Fact]
    public void OnlyTheAttackTextsTheWordingRuleWouldTake_AreKept()
    {
        // Chaos storm's victim text names a subject, so the wording rule never took it.
        Assert.Equal(2, Build().MonsterAttackTextCount);
    }
}

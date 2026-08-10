using System.Collections.Generic;
using MudPlay.Game.Map;
using Xunit;

namespace MudPlay.Tests;

// Pins the death-summon wave model: an ordinary mob is a no-op, a real cascade
// folds the whole tree, and the engine's 20-monster room cap suppresses summons
// beyond a tier's capacity so a fan-out room isn't scored as if every summon spawned.
public sealed class DeathSummonCascadeTests
{
    private static CascadeResult Run(
        Dictionary<int, int> exp, Dictionary<int, IReadOnlyList<int>?> summons, int seed, int count)
        => DeathSummonCascade.Simulate(
            seed, count, id => exp.GetValueOrDefault(id), id => summons.GetValueOrDefault(id));

    [Fact]
    public void NonSummoningMonster_IsNoOp()
    {
        // No DeathSpell summons → the room is just its base spawn: exp = count × exp,
        // one kill each, a single clear wave.
        CascadeResult r = Run(new() { [1] = 500 }, new(), seed: 1, count: 3);

        Assert.Equal(1500, r.Exp, 3);
        Assert.Equal(3, r.Kills, 3);
        Assert.Equal(1, r.Waves);
    }

    [Fact]
    public void ZombiePenShape_FoldsWholeTree_WhenUnderCap()
    {
        // 3 stitched zombies (4000) → each summons waist + torso (4000) → waist→2 legs
        // (3500), torso→2 arms (3000) + head (3500). Peak tier is 15 < 20, so nothing
        // is capped: 85,500 exp over 24 kills across 3 waves.
        var exp = new Dictionary<int, int> { [1220] = 4000, [881] = 4000, [888] = 4000, [889] = 3500, [890] = 3000, [891] = 3500 };
        var summons = new Dictionary<int, IReadOnlyList<int>?>
        {
            [1220] = new[] { 881, 888 },
            [881] = new[] { 889, 889 },
            [888] = new[] { 890, 890, 891 },
        };
        CascadeResult r = Run(exp, summons, seed: 1220, count: 3);

        Assert.Equal(85_500, r.Exp, 3);
        Assert.Equal(24, r.Kills, 3);
        Assert.Equal(3, r.Waves);
    }

    [Fact]
    public void RoomCap_SuppressesSummonsBeyond20PerTier()
    {
        // 15 seeds each summon 2 → 30 wanted, but the room holds 20, so 10 are
        // suppressed (never spawn, never counted). The next tier likewise clamps
        // 40→20. Kills = 15 + 20 + 20 = 55, not the uncapped 15 + 30 + 60 = 105.
        var exp = new Dictionary<int, int> { [1] = 100, [2] = 10, [3] = 1 };
        var summons = new Dictionary<int, IReadOnlyList<int>?> { [1] = new[] { 2, 2 }, [2] = new[] { 3, 3 } };
        CascadeResult r = Run(exp, summons, seed: 1, count: 15);

        Assert.Equal(3, r.Waves);
        Assert.Equal(55, r.Kills, 3);
    }

    [Fact]
    public void RoomCap_FailsWholeSummonCasts_NotFractionally()
    {
        // A summon spell is atomic: it lands only if ALL its summons fit, else the
        // whole cast fails (permanently — no partial, no retry). Tier 2 overflows
        // with mixed cast sizes — the size-2 casts (id 2 → two 4s) are processed
        // first and all fit (16 of id 4), leaving room for only ONE size-4 cast
        // (id 3 → four 5s) → 4 of id 5. The rest fail whole. Exp reflects that whole-
        // cast outcome (16×10 + 4×1 = 164), NOT a proportional split (~80).
        var exp = new Dictionary<int, int> { [1] = 0, [2] = 0, [3] = 0, [4] = 10, [5] = 1 };
        var summons = new Dictionary<int, IReadOnlyList<int>?>
        {
            [1] = new[] { 2, 3 },
            [2] = new[] { 4, 4 },
            [3] = new[] { 5, 5, 5, 5 },
        };
        CascadeResult r = Run(exp, summons, seed: 1, count: 8);

        Assert.Equal(164, r.Exp, 3);
        Assert.Equal(44, r.Kills, 3);   // 8 + 16 + 20
    }

    [Fact]
    public void SeedCount_ClampedToCap()
    {
        // Even the base spawn can't exceed the room cap.
        CascadeResult r = Run(new() { [1] = 10 }, new(), seed: 1, count: 50);
        Assert.Equal(20, r.Kills, 3);
    }
}

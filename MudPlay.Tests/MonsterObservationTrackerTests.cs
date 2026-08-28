using MudPlay.Game.Combat;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class MonsterObservationTrackerTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public LogService Log { get; } = new();
        public FlavorPrefixStore Prefixes { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public MonsterObservationTracker Tracker { get; }
        public string? CurrentTarget { get; set; }
        public int ChangedCount { get; private set; }

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(
                Router, Monsters, Players, roomTracker: null, Log, gameData: null, Prefixes);
            Tracker = new MonsterObservationTracker(
                Router, Classifier, () => CurrentTarget, profile: null);
            Tracker.Changed += () => ChangedCount++;
        }

        public void AddMonster(int number, string name) =>
            Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}", Name: name,
                Links: new[] { new GameDataLink("Monsters", number) }));

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        public void Dispose()
        {
            Tracker.Dispose();
            Classifier.Dispose();
        }
    }

    [Fact]
    public void NoObservation_ForReturnsNull()
    {
        using Harness h = new();
        Assert.Null(h.Tracker.For(1));
        Assert.Empty(h.Tracker.Snapshot());
    }

    [Fact]
    public void Hit_AttributesByTargetName_RecordsDamageExtent()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");

        h.Feed("You slash giant rat for 8 damage!");

        MonsterObservation o = Assert.Single(h.Tracker.Snapshot());
        Assert.Equal(1, o.MonsterNumber);
        Assert.Equal(1, o.HitCount);
        Assert.Equal(8, o.HitDamageMin);
        Assert.Equal(8, o.HitDamageMax);
        Assert.Equal(8, o.HitDamageSum);
        Assert.Equal(8d, o.AvgHitDamage);
        Assert.True(h.ChangedCount > 0);
    }

    [Fact]
    public void MultipleHits_TrackMinMaxAndSum()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");

        h.Feed("You slash giant rat for 8 damage!");
        h.Feed("You slash giant rat for 15 damage!");
        h.Feed("You slash giant rat for 3 damage!");

        MonsterObservation o = h.Tracker.For(1)!;
        Assert.Equal(3, o.HitCount);
        Assert.Equal(3, o.HitDamageMin);
        Assert.Equal(15, o.HitDamageMax);
        Assert.Equal(26, o.HitDamageSum);
    }

    [Fact]
    public void OtherPlayersHit_IsIgnored()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");

        h.Feed("Raijin slashes giant rat for 9 damage!");

        Assert.Null(h.Tracker.For(1));
    }

    [Fact]
    public void HitAgainstUnresolvableName_IsNotRecorded()
    {
        // No AddMonster / Also-here — RoomEntityClassifier has nothing to
        // resolve "giant rat" against, so ResolveNumber finds no RoomEntity.
        using Harness h = new();
        h.Feed("You slash giant rat for 8 damage!");
        Assert.Empty(h.Tracker.Snapshot());
    }

    // UserMisses carries no target name on the wire, so a miss is attributed
    // to the live combat target instead — and only while engaged, mirroring
    // CombatSessionTracker's own false-positive guard against self-emotes
    // ending in "!".
    [Fact]
    public void Miss_AttributesToCurrentTarget_WhileEngaged()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        h.CurrentTarget = "giant rat";

        h.Feed("*Combat Engaged*");
        h.Feed("You swing at giant rat, but miss!");

        MonsterObservation o = h.Tracker.For(1)!;
        Assert.Equal(1, o.MissCount);
        Assert.Equal(0, o.HitCount);
    }

    [Fact]
    public void Miss_OutsideCombat_IsNotCounted()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        h.CurrentTarget = "giant rat";

        h.Feed("You feel much better!");

        Assert.Null(h.Tracker.For(1));
    }

    [Fact]
    public void Miss_WithNoCurrentTarget_IsNotCounted()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");

        h.Feed("*Combat Engaged*");
        h.Feed("You swing at giant rat, but miss!");

        Assert.Null(h.Tracker.For(1));
    }

    // A hit re-arms engagement too, same as CombatSessionTracker, so a miss
    // following a landed hit (with no explicit "*Combat Engaged*" line) still
    // counts.
    [Fact]
    public void HitThenMiss_MissStillCounted_WithoutExplicitEngagedLine()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        h.CurrentTarget = "giant rat";

        h.Feed("You slash giant rat for 8 damage!");
        h.Feed("You swing at giant rat, but miss!");

        MonsterObservation o = h.Tracker.For(1)!;
        Assert.Equal(1, o.HitCount);
        Assert.Equal(1, o.MissCount);
        Assert.Equal(2, o.SwingCount);
        Assert.Equal(50d, o.HitRatePercent);
    }

    [Fact]
    public void WeaponNoEffect_AttributesToCurrentTarget()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        h.CurrentTarget = "giant rat";

        h.Feed("Your weapon has no effect against this monster!");

        MonsterObservation o = h.Tracker.For(1)!;
        Assert.Equal(1, o.PhysicalNoEffectCount);
    }

    [Fact]
    public void FistsNoEffect_FoldsIntoSamePhysicalCounterAsWeapon()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        h.CurrentTarget = "giant rat";

        h.Feed("Your weapon has no effect against this monster!");
        h.Feed("Your fists have no effect against this monster!");

        MonsterObservation o = h.Tracker.For(1)!;
        Assert.Equal(2, o.PhysicalNoEffectCount);
    }

    [Fact]
    public void SpellNoEffect_AttributesByItsOwnTargetCapture()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        // Deliberately no CurrentTarget set — SpellNoEffect carries its own
        // target name on the wire, so it doesn't need the fallback.

        h.Feed("Your spell has no effect on giant rat.");

        MonsterObservation o = h.Tracker.For(1)!;
        Assert.Equal(1, o.SpellNoEffectCount);
    }

    [Fact]
    public void Clear_WipesAllObservations()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");
        h.Feed("You slash giant rat for 8 damage!");
        Assert.NotEmpty(h.Tracker.Snapshot());

        h.Tracker.Clear();

        Assert.Empty(h.Tracker.Snapshot());
    }

    [Fact]
    public void DistinctMonsters_TrackedSeparately()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "cave bear");
        h.Feed("Also here: giant rat, cave bear.");

        h.Feed("You slash giant rat for 8 damage!");
        h.Feed("You punch cave bear for 20 damage!");

        Assert.Equal(2, h.Tracker.Snapshot().Count);
        Assert.Equal(8, h.Tracker.For(1)!.HitDamageSum);
        Assert.Equal(20, h.Tracker.For(2)!.HitDamageSum);
    }
}

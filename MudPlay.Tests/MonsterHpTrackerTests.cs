using MudPlay.Game;
using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

// MonsterHpTracker: a running HP estimate per monster in the room — max HP less the
// damage the round ledger saw, plus regen, pulled into the wound band by each look.
public sealed class MonsterHpTrackerTests
{
    private sealed class Harness
    {
        public DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public bool Paradigm = true;
        public MonsterHpTracker Tracker { get; }

        // #1 orc: 100 HP, regen 10. #2 rat: 12 HP, regen 1.
        public Harness()
            => Tracker = new MonsterHpTracker(
                n => n switch { 1 => 100, 2 => 12, _ => null },
                n => n switch { 1 => 10, 2 => 1, _ => 0 },
                () => Paradigm, () => Now);

        public void Room(RoomObservationSource source, params (string Name, int Number)[] monsters)
            => Tracker.NoteRoomEntities(new RoomEntitiesObservation("",
                monsters.Select(m => new RoomEntity(m.Name, m.Name, EntityKind.Monster, m.Number)).ToArray(),
                Now, source));

        public void Hit(string name, int amount)
            => Tracker.NoteDamage(new DamageAttribution(DamageLineAttributor.Self, name, amount));
    }

    private static MonsterHpEstimate Band(string wound, int max = 100)
        => MonsterLookParser.EstimateHp(max, wound)!.Value;

    [Fact]
    public void Damage_LowersTheEstimate_UntouchedIsFull()
    {
        Harness h = new();
        h.Room(RoomObservationSource.AlsoHere, ("orc", 1));
        Assert.Equal(100, h.Tracker.Estimate("orc"));

        h.Hit("orc", 30);
        Assert.Equal(70, h.Tracker.Estimate("orc"));
    }

    [Theory]
    [InlineData(true, 30)]    // Paradigm: every 30 s
    [InlineData(false, 90)]   // Stock: every 90 s
    public void Regen_PaysPerRealmInterval(bool paradigm, int seconds)
    {
        Harness h = new() { Paradigm = paradigm };
        h.Room(RoomObservationSource.AlsoHere, ("orc", 1));
        h.Hit("orc", 30);

        h.Now = h.Now.AddSeconds(seconds - 1);
        Assert.Equal(70, h.Tracker.Estimate("orc"));
        h.Now = h.Now.AddSeconds(1);
        Assert.Equal(80, h.Tracker.Estimate("orc"));
        h.Now = h.Now.AddSeconds(seconds * 5);
        Assert.Equal(100, h.Tracker.Estimate("orc"));   // capped at max
    }

    [Fact]
    public void Look_PullsTheEstimateIntoTheBand()
    {
        Harness h = new();
        h.Room(RoomObservationSource.AlsoHere, ("orc", 1));
        h.Hit("orc", 10);   // ~90, but the look says heavily wounded (50-69)

        Assert.Equal(new MonsterHpRead(100, 69), h.Tracker.OnLook("orc", Band("heavily wounded")));
    }

    // The damage we saw says it should read lower than it does: a regen tick fired.
    [Fact]
    public void Look_BandHeldUp_CountsARegenTick()
    {
        Harness h = new();
        h.Room(RoomObservationSource.AlsoHere, ("orc", 1));
        h.Hit("orc", 55);   // ~45 → severely (30-49); a tick of 10 makes 55 → heavily

        Assert.Equal(new MonsterHpRead(100, 55), h.Tracker.OnLook("orc", Band("heavily wounded")));
    }

    // A look after a look that shows a better band: a regen tick fired between them,
    // and the regen clock is re-timed from then.
    [Fact]
    public void Look_BandRose_CountsATickAndRetimes()
    {
        Harness h = new();
        h.Room(RoomObservationSource.AlsoHere, ("orc", 1));
        h.Hit("orc", 51);                                         // ~49
        Assert.Equal(49, h.Tracker.OnLook("orc", Band("severely wounded"))!.Value.BestGuess);

        h.Now = h.Now.AddSeconds(10);                             // not a full interval
        Assert.Equal(59, h.Tracker.OnLook("orc", Band("heavily wounded"))!.Value.BestGuess);

        h.Now = h.Now.AddSeconds(29);                             // 29 s since the seen tick
        Assert.Equal(59, h.Tracker.Estimate("orc"));
        h.Now = h.Now.AddSeconds(1);                              // the next one lands on time
        Assert.Equal(69, h.Tracker.Estimate("orc"));
    }

    // Two monsters sharing a name: damage and looks go to the first in "Also here:",
    // and a death removes that first one.
    [Fact]
    public void SharedName_TheFirstTakesTheDamage_AndDiesFirst()
    {
        Harness h = new();
        h.Room(RoomObservationSource.AlsoHere, ("orc", 1), ("orc", 1));
        h.Hit("orc", 40);
        Assert.Equal(60, h.Tracker.Estimate("orc"));

        h.Room(RoomObservationSource.Death, ("orc", 1));
        Assert.Equal(100, h.Tracker.Estimate("orc"));   // the survivor is untouched
    }

    // Our room spell hits every monster, both of a shared name included.
    [Fact]
    public void AreaDamage_HitsEveryMonster()
    {
        Harness h = new();
        h.Room(RoomObservationSource.AlsoHere, ("orc", 1), ("orc", 1), ("rat", 2));
        h.Tracker.NoteAreaDamage(8);

        Assert.Equal(92, h.Tracker.Estimate("orc"));
        Assert.Equal(4, h.Tracker.Estimate("rat"));
        h.Room(RoomObservationSource.Death, ("orc", 1), ("rat", 2));
        Assert.Equal(92, h.Tracker.Estimate("orc"));   // the survivor was hit too
    }

    [Fact]
    public void RoomChange_ForgetsEverything()
    {
        Harness h = new();
        h.Room(RoomObservationSource.AlsoHere, ("orc", 1));
        h.Hit("orc", 40);
        h.Room(RoomObservationSource.RoomChange);
        h.Room(RoomObservationSource.AlsoHere, ("orc", 1));

        Assert.Equal(100, h.Tracker.Estimate("orc"));
    }

    [Fact]
    public void DamageToUs_IsIgnored()
    {
        Harness h = new();
        h.Room(RoomObservationSource.AlsoHere, ("orc", 1));
        h.Tracker.NoteDamage(new DamageAttribution("orc", DamageLineAttributor.Self, 20));
        Assert.Equal(100, h.Tracker.Estimate("orc"));
    }
}

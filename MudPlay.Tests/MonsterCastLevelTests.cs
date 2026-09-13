using System.IO;
using MudPlay.Game.Combat;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// MonsterCatalogEntry.CastLevelFor backs the witnessed-ailment chip duration
// (AppServices.ResolveAilmentDurationSeconds). The resolver used to compare a spell
// number against MonsterAttackSlot.Accuracy on EVERY slot, but Accuracy only carries a
// spell number on an AttType-2 slot — on a physical slot it's a to-hit value. 431
// physical slots in the shipped Paradigm data have an Accuracy equal to some ailment
// spell's number, so chips were timed off an unrelated attack's damage figure.
public sealed class MonsterCastLevelTests : IDisposable
{
    private readonly string _root;

    public MonsterCastLevelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-castlevel-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Values taken from real Paradigm rows so the collisions under test are the ones
    // that actually occur:
    //   #36 hellhound       — physical slot, AttAcc 60 collides with `fear` (#60),
    //                         AttMax 17 would be misread as a cast level.
    //   #40 Sheriff Lionheart — same collision with AttAcc 200 / `blind book`, and an
    //                         AttMax of 320, the worst observed distortion.
    //   #59 Thrag           — delivers `knockdown` (#318) as an on-hit PROC, which is a
    //                         physical attack and must never count as a cast.
    //   #900 dark elf priest — a genuine AttType-2 spell slot casting #60 at level 12.
    //   #901 shaman         — casts #60 between rounds at level 30.
    //   #902 idle caster    — an AttType-2 slot for #60 that can never fire (Att% 0).
    private const string Monsters = """
        [
          { "Number": 36,  "Name": "hellhound",
            "AttType-0": 1, "Att%-0": 75,  "AttAcc-0": 60,  "AttMax-0": 17 },
          { "Number": 40,  "Name": "Sheriff Lionheart",
            "AttType-1": 1, "Att%-1": 100, "AttAcc-1": 200, "AttMax-1": 320 },
          { "Number": 59,  "Name": "Thrag",
            "AttType-1": 1, "Att%-1": 100, "AttAcc-1": 50, "AttMax-1": 50,
            "AttHitSpell-1": 318 },
          { "Number": 900, "Name": "dark elf priest",
            "AttType-0": 2, "Att%-0": 60,  "AttAcc-0": 60,  "AttMax-0": 12 },
          { "Number": 901, "Name": "shaman",
            "MidSpell-0": 60, "MidSpell%-0": 25, "MidSpellLVL-0": 30 },
          { "Number": 902, "Name": "idle caster",
            "AttType-0": 2, "Att%-0": 0,   "AttAcc-0": 60,  "AttMax-0": 99 }
        ]
        """;

    private const string Spells = """
        [
          { "Number": 60,  "Name": "fear" },
          { "Number": 200, "Name": "blind book" },
          { "Number": 318, "Name": "knockdown" }
        ]
        """;

    private MonsterCatalog NewCatalog()
    {
        const string set = "set";
        Directory.CreateDirectory(Path.Combine(_root, set));
        File.WriteAllText(Path.Combine(_root, set, "Monsters.json"), Monsters);
        File.WriteAllText(Path.Combine(_root, set, "Spells.json"), Spells);
        GameDataCache cache = new(_root);
        cache.SwitchSet(set);
        return new MonsterCatalog(cache);
    }

    [Theory]
    // The reported bug: a physical slot whose to-hit value equals the spell number.
    [InlineData(36, 60)]    // hellhound  — AttAcc 60 == `fear`, AttMax 17
    [InlineData(40, 200)]   // Lionheart  — AttAcc 200 == `blind book`, AttMax 320
    public void PhysicalSlot_AccuracyMatchingASpellNumber_IsNotACast(int monster, int spell)
    {
        MonsterCatalogEntry entry = NewCatalog().Get(monster)!;
        Assert.Equal(0, entry.CastLevelFor(spell));
    }

    [Fact]
    public void OnHitProc_IsNotACast()
    {
        // A proc is a physical attack, not a cast — it carries no cast level and must
        // not drive spell duration.
        MonsterCatalogEntry thrag = NewCatalog().Get(59)!;
        Assert.Equal(0, thrag.CastLevelFor(318));
    }

    [Fact]
    public void SpellSlot_YieldsItsCastLevel()
    {
        // AttType 2: Accuracy is the spell number, MaxDamage the cast level.
        MonsterCatalogEntry priest = NewCatalog().Get(900)!;
        Assert.Equal(12, priest.CastLevelFor(60));
    }

    [Fact]
    public void BetweenRoundsSpell_YieldsItsCastLevel()
    {
        MonsterCatalogEntry shaman = NewCatalog().Get(901)!;
        Assert.Equal(30, shaman.CastLevelFor(60));
    }

    [Fact]
    public void SpellSlotThatCanNeverFire_IsIgnored()
    {
        MonsterCatalogEntry idle = NewCatalog().Get(902)!;
        Assert.Equal(0, idle.CastLevelFor(60));
    }

    [Fact]
    public void SpellTheMonsterDoesNotCast_IsZero()
    {
        MonsterCatalogEntry priest = NewCatalog().Get(900)!;
        Assert.Equal(0, priest.CastLevelFor(318));
        Assert.Equal(0, priest.CastLevelFor(0));
        Assert.Equal(0, priest.CastLevelFor(-1));
    }

    [Fact]
    public void RealCasterIsNotOutbidByAPhysicalCollision()
    {
        // The resolver takes the LONGEST level across in-room monsters, so the whole
        // point of the fix: Lionheart's 320-damage physical slot must not outrank the
        // dark elf priest's genuine level-12 cast of the same spell.
        MonsterCatalog catalog = NewCatalog();
        int best = 0;
        foreach (int id in new[] { 40, 36, 900 })
            if (catalog.Get(id)!.CastLevelFor(60) is var lvl && lvl > best) best = lvl;

        Assert.Equal(12, best);
    }
}

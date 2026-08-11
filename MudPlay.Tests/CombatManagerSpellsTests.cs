using System.Text;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

/// <summary>
/// PR 9.A (spell extension) — <see cref="CombatManager"/> combat-spell
/// round economy: the chooser-driven cast path that suppresses weapon
/// swings, the per-round heartbeat re-cast, and the opt-in guard that keeps
/// the weapon engine unchanged until <see cref="CombatManager.SetCombatSpellCaster"/>
/// is wired.
/// </summary>
public sealed class CombatManagerSpellsTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public MonsterMessageStore Monsters { get; } = new();
        public PlayerDatabase Players { get; } = new();
        public PartyState Party { get; } = new();
        public LogService Log { get; } = new();
        public RoomEntityClassifier Classifier { get; }
        public CombatManager Combat { get; }
        public CastCoordinator Cast { get; }
        public List<byte[]> Sent { get; } = new();
        public CombatSettings Settings { get; set; } = new()
        {
            NormalAttackCommand = "a",
            TargetOrder = TargetOrder.Normal,
        };

        public Dictionary<int, MonsterOverlay> Overlays { get; } = new();

        // Spell.Number → Short cast-code, feeding the per-monster override
        // resolver. An unmapped number resolves to null (unknown → fall back).
        public Dictionary<int, string> SpellShorts { get; } = new();

        public bool AutoCombatEnabled { get; set; } = true;
        public bool AutoNukeEnabled { get; set; } = true;
        public int Ma { get; set; } = 100;
        public int MaxMa { get; set; } = 100;
        public bool Sneaking { get; set; }
        public HashSet<int> SeeHidden { get; } = new();

        public Harness(bool wireCaster = true)
        {
            DefaultPatterns.Seed(Router);
            Classifier = new RoomEntityClassifier(Router, Monsters, Players, Log);
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(b => Sent.Add(b));
            Combat = new CombatManager(Router, Classifier, Monsters,
                resolveOverlay: n => Overlays.TryGetValue(n, out MonsterOverlay? o)
                                     ? o : new MonsterOverlay(),
                party: Party,
                readSettings: () => Settings,
                isEnabled: () => AutoCombatEnabled,
                readOwnGivenName: () => "MudPlay",
                post: a => a(),                          // synchronous in tests
                log: Log);
            Combat.SetWireSender(b => Sent.Add(b));
            Combat.SetBackstabHooks(() => Sneaking, n => SeeHidden.Contains(n));
            Combat.SetAutoNukeGate(() => AutoNukeEnabled);
            Combat.SetSpellShortResolver(
                n => SpellShorts.TryGetValue(n, out string? s) ? s : null);
            // Store the settle callback instead of running a real timer so a test
            // controls when the window elapses (FireSettle). Only arms on arrival
            // observations, so the existing Also-Here tests are unaffected.
            Combat.SetArrivalSettleScheduler((_, cb) => PendingSettle = cb);
            if (wireCaster)
                Combat.SetCombatSpellCaster(Cast, () => (Ma, MaxMa));
        }

        // The pending arrival-settle callback (see CombatManager). FireSettle runs
        // it, simulating the debounce window elapsing with no room re-display.
        public Action? PendingSettle { get; private set; }
        public void FireSettle() => PendingSettle?.Invoke();

        // Simulate a mid-room monster arrival ("A giant rat strides in …") — appends
        // to the classifier's Current with Source=Arrival, the path RoomEntryWatcher
        // drives in production.
        public void Arrive(string name)
            => Classifier.AppendArrivalEntity(
                Classifier.Classify(name),
                rawWireLine: $"A {name} strides in from the west.");

        public void SetOverlay(int monsterNumber, MonsterAttackPriority? priority = null,
                               MonsterRelationship? relationship = null)
            => Overlays[monsterNumber] = new MonsterOverlay
            {
                Priority = priority,
                Relationship = relationship,
            };

        public void AddMonster(int number, string name)
            => Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"M{number}",
                Name: name,
                HitYou: Array.Empty<string>(),
                HitOther: Array.Empty<string>(),
                DeathLine: new[] { $"The {name} dies." },
                ArmorBlockYou: Array.Empty<string>(),
                ArmorBlockOther: Array.Empty<string>(),
                DodgeYou: Array.Empty<string>(),
                DodgeOther: Array.Empty<string>(),
                MissYou: Array.Empty<string>(),
                MissOther: Array.Empty<string>(),
                FlavorPrefixes: Array.Empty<string>(),
                AllowNoPrefix: true,
                Links: new[] { new GameDataLink("Monsters", number) }));

        // A monster the classifier recognises by name (so it's EntityKind.Monster)
        // but which carries no Monsters-table link, so its number never resolves —
        // the real-world case where the server colours a variant hostile whose
        // flavored name isn't in game data. Mirrors ResolveMonsterNumber returning
        // null; the room-nuke still hits it, so it must count toward MinEnemies.
        public void AddUnnumberedMonster(string name)
            => Monsters.Messages.Add(new MonsterMessageRecord(
                Id: $"U-{name}",
                Name: name,
                HitYou: Array.Empty<string>(),
                HitOther: Array.Empty<string>(),
                DeathLine: new[] { $"The {name} dies." },
                ArmorBlockYou: Array.Empty<string>(),
                ArmorBlockOther: Array.Empty<string>(),
                DodgeYou: Array.Empty<string>(),
                DodgeOther: Array.Empty<string>(),
                MissYou: Array.Empty<string>(),
                MissOther: Array.Empty<string>(),
                FlavorPrefixes: Array.Empty<string>(),
                AllowNoPrefix: true,
                Links: Array.Empty<GameDataLink>()));

        public void Feed(string line)
        {
            LineExtractor.EmittedLine emitted = new(
                line, Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false);
            Router.Dispatch(emitted);
        }

        /// <summary>One combat round. Mirrors the AppServices tick-subscription
        /// order: the coordinator clears its cooldown first, then the combat
        /// heartbeat re-decides. (CastingDirector sits between them in production but
        /// isn't under test here.)</summary>
        public void Tick()
        {
            Cast.OnCombatTick();
            Combat.OnCombatTick();
        }

        public string LastSent => Sent.Count == 0
            ? string.Empty
            : Encoding.Latin1.GetString(Sent[^1]).TrimEnd('\r');

        public IEnumerable<string> AllSent =>
            Sent.Select(b => Encoding.Latin1.GetString(b).TrimEnd('\r'));

        public void Dispose()
        {
            Combat.Dispose();
            Cast.Dispose();
            Classifier.Dispose();
        }
    }

    // ----- opt-in guard ------------------------------------------------

    [Fact]
    public void CasterUnwired_MultiAttackConfigured_StillSwingsWeapon()
    {
        using Harness h = new(wireCaster: false);
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void CasterWired_NoSpellsConfigured_StillSwingsWeapon()
    {
        using Harness h = new();
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    // ----- spell suppresses the weapon swing ---------------------------

    [Fact]
    public void MultiAttackQualifies_CastsSpell_NoWeaponSwing()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        // A multi-attack (room-wide) spell is cast BARE — never "blast <mob>".
        Assert.Equal("blast", h.LastSent);
        Assert.DoesNotContain("a giant rat", h.AllSent);
        Assert.Equal("giant rat", h.Combat.CurrentTarget);
    }

    [Fact]
    public void MultiAttackBelowMinEnemies_FallsToWeapon()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 2 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("a giant rat", h.LastSent);
    }

    // Report paradigm-20260811-063728: a wrapped 5-mob "Also here:" where one
    // occupant's flavored name didn't resolve to a Monsters number was counted as
    // 4 by the heartbeat's engageable count, held below MinEnemies=5, so the room
    // spell never took over from the single-target cascade. The room-nuke hits
    // every monster regardless of whether we resolved its number, so an unknown-
    // number monster must count toward MinEnemies — matching the initial dispatch's
    // fail-open candidate build.
    [Fact]
    public void Heartbeat_CountsUnknownNumberMonster_TowardMinEnemies_RoomsBare()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 2 };
        h.AddMonster(1, "giant rat");        // resolvable
        h.AddUnnumberedMonster("dark stalker");   // Monster kind, number never resolves

        // One resolvable mob → below MinEnemies=2 → single-target attack spell.
        h.Feed("Also here: giant rat.");
        Assert.Equal("nuke giant rat", h.LastSent);

        // A second mob arrives whose number we can't resolve. The "already engaged"
        // guard blocks a re-dispatch here (giant rat still current), so the switch
        // is the heartbeat's job — and its count must include the unknown mob.
        h.Feed("Also here: giant rat, dark stalker.");
        h.Tick();

        // Count reached MinEnemies → room spell, cast BARE (never "blast <mob>").
        Assert.Equal("blast", h.LastSent);
    }

    // Simultaneous-arrival settle (report paradigm-20260811-063728 + a live report):
    // three monsters stride in on one wire flush, then the room re-displays. Engaging
    // the first arrival single-target used to strand the room below its multi-attack
    // threshold. The burst must HOLD until the room re-display drives one decision on
    // the whole group, so a qualifying room nukes on its first action.
    [Fact]
    public void SimultaneousArrivalBurst_RoomsOnRedisplay_NoSingleTargetFirst()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 3 };
        h.AddMonster(1, "giant rat");

        // Burst of arrivals — the first engage is held, nothing goes out yet.
        h.Arrive("giant rat");
        h.Arrive("giant rat");
        h.Arrive("giant rat");
        Assert.Equal(string.Empty, h.LastSent);

        // Authoritative room re-display of the whole group → rooms bare, first action.
        h.Feed("Also here: giant rat, giant rat, giant rat.");
        Assert.Equal("blast", h.LastSent);
        Assert.DoesNotContain("nuke giant rat", h.AllSent);
    }

    // The other side of the settle: a LONE spawn with no room re-display still engages,
    // just a beat later when the window elapses. Below the multi-attack threshold, so
    // it's the single-target cascade — the burst hold must not strand a solo arrival.
    [Fact]
    public void LoneArrival_EngagesSingleTarget_WhenSettleElapses()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "nuke", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 3 };
        h.AddMonster(1, "giant rat");

        h.Arrive("giant rat");
        Assert.Equal(string.Empty, h.LastSent);   // held during the window

        h.FireSettle();                            // window elapsed, no re-display
        Assert.Equal("nuke giant rat", h.LastSent);
    }

    // Post-kill re-engage race (reports 081053, 081654, 103708, 135433). Each realm
    // gives monsters custom death messages we can't map to the flavored target, so
    // the specific-death matcher misses and combat used to re-cast at the corpse on
    // the kill's *Combat Off* ("You don't see X here!"), then idle a round. The exp
    // gain that precedes a kill's Off — with no between-round cast to explain the
    // Off — marks it as our kill, so spell mode drops and no corpse re-cast goes out.
    [Fact]
    public void KillInferredFromExp_DropsSpellTarget_NoCorpseRecast()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("mmis giant rat", h.LastSent);   // engaged, spell mode
        int sentAfterEngage = h.Sent.Count;

        // Wire order of a kill: (custom death line we can't match) → exp → Off.
        h.Feed("You gain 100 experience.");
        h.Feed("*Combat Off*");

        // The corpse is not re-cast at — spell mode was dropped on the inferred kill.
        Assert.Equal(sentAfterEngage, h.Sent.Count);
    }

    // The other side of the gate: a mid-fight between-round cast's *Combat Off* (even
    // with an exp gain sitting nearby, e.g. party share-exp) is NOT a kill — the
    // resume must still re-announce the spell rather than dropping a live target.
    [Fact]
    public void BetweenRoundCastOff_WithNearbyExp_StillResumes_NotTreatedAsKill()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "mmis", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("mmis giant rat", h.LastSent);

        h.Feed("You gain 100 experience.");   // e.g. a partymate's kill
        h.Combat.NoteBetweenRoundCast();       // our own heal broke combat this round
        h.Feed("*Combat Off*");

        // Not a kill (a cast explains the Off) → the live target is re-announced.
        Assert.Equal("mmis giant rat", h.LastSent);
    }

    // ----- Auto-Nuke auto-engine gate ----------------------------------

    [Fact]
    public void AutoNukeOff_MultiAttackQualifies_FallsToWeapon()
    {
        using Harness h = new();
        h.AutoNukeEnabled = false;            // nukes disabled
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        // Multi-target nuke is suppressed; the single-target weapon swing runs.
        Assert.Equal("a giant rat", h.LastSent);
        Assert.DoesNotContain("blast", h.AllSent);
    }

    [Fact]
    public void AutoNukeOff_SingleTargetAttackSpell_StillFires()
    {
        using Harness h = new();
        h.AutoNukeEnabled = false;            // nukes disabled
        // A single-target attack spell is NOT a nuke — it stays available.
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "lightning", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("lightning giant rat", h.LastSent);
    }

    [Fact]
    public void AutoNukeOff_AreaDebuff_NotOffered()
    {
        using Harness h = new();
        h.AutoNukeEnabled = false;            // nukes (incl. debuffs) disabled
        h.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        // No combat spell configured beyond the debuff → weapon swing, and the
        // in-between debuff window stays empty.
        Assert.Equal("a giant rat", h.LastSent);
        Assert.Null(h.Combat.PickInBetweenDebuff());
    }

    // ----- per-monster spell overrides (Number → Short resolution) -----

    [Fact]
    public void AttackOverride_CastsOverrideSpell_NotConfiguredNormal()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.SpellShorts[42] = "fireball";
        h.Overlays[1] = new MonsterOverlay { OverrideAttackSpellId = 42, OverrideAttackCount = 3 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        // The Spell.Number override (42) resolves to its Short and replaces the
        // configured normal-attack cast-code for this monster.
        Assert.Equal("fireball giant rat", h.LastSent);
        Assert.DoesNotContain("harm giant rat", h.AllSent);
    }

    [Fact]
    public void AttackOverride_NullCount_FallsBackToConfiguredSlot()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.SpellShorts[42] = "fireball";
        // Spell set but no count (overlay documents null = 0) → not active.
        h.Overlays[1] = new MonsterOverlay { OverrideAttackSpellId = 42 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("harm giant rat", h.LastSent);
        Assert.DoesNotContain("fireball giant rat", h.AllSent);
    }

    [Fact]
    public void AttackOverride_UnknownNumber_FallsBackToConfiguredSlot()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        // Resolver has no entry for 99 → override can't resolve → configured slot.
        h.Overlays[1] = new MonsterOverlay { OverrideAttackSpellId = 99, OverrideAttackCount = 2 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("harm giant rat", h.LastSent);
    }

    [Fact]
    public void PreAttackOverride_OfferedAsInBetweenDebuff()
    {
        using Harness h = new();
        h.SpellShorts[7] = "curse";
        h.Overlays[1] = new MonsterOverlay { OverridePreAttackSpellId = 7, OverridePreAttackCount = 2 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");   // engages, sets the current target

        (string Spell, string? Target)? debuff = h.Combat.PickInBetweenDebuff();
        Assert.NotNull(debuff);
        Assert.Equal("curse", debuff!.Value.Spell);
        Assert.Equal("giant rat", debuff.Value.Target);
    }

    // ----- physical-first: weapon exhausted before spells -------------

    [Fact]
    public void PhysicalFirst_ExhaustsWeaponBeforeFallingToSpell()
    {
        // Physical-first with a caster: the weapon must be GENUINELY exhausted
        // before the spell cascade is reached. On the first alt no-effect the
        // engine force-retries the weapon (not the spell); only once THAT also
        // fails does it cast the attack spell.
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.PhysicalFirst;
        h.Settings.NormalWeapon = "sword";
        h.Settings.AlternateWeapon = "hammer";
        h.Settings.AlternateAttackCommand = "aa";
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "lightning", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");                             // swings weapon (physical-first)
        Assert.Equal("a giant rat", h.LastSent);

        h.Feed("Your weapon has no effect against this monster!");   // normal → swap to alt
        h.Feed("Your weapon has no effect against this monster!");   // alt 1st → force-retry the WEAPON
        Assert.Equal("aa giant rat", h.LastSent);
        Assert.DoesNotContain("lightning giant rat", h.AllSent);     // spell not reached yet

        h.Feed("Your weapon has no effect against this monster!");   // alt 2nd → weapon out → spell
        Assert.Equal("lightning giant rat", h.LastSent);
    }

    [Fact]
    public void ManaStuck_WeaponOut_MovesOnNow_ButStaysRetryableUntilManaRegens()
    {
        // Weapons can't hit and MA is below the spell's cast floor: the mob is
        // un-actionable THIS round (the walker moves on rather than stand getting
        // beaten waiting for a mana tick) — but NOT permanently. Once MA regens
        // above the floor it reads actionable again, so the cast chain is retried.
        using Harness h = new();
        h.Settings.NormalWeapon = "sword";
        h.Settings.AlternateWeapon = "hammer";
        h.Settings.AlternateAttackCommand = "aa";
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "lightning", MinManaPerCast = 50 };
        h.Ma = 10; h.MaxMa = 100;                                    // below the cast floor
        h.AddMonster(1, "giant rat");
        h.Feed("Also here: giant rat.");                            // spell can't fire → swings

        h.Feed("Your weapon has no effect against this monster!");   // normal → alt
        h.Feed("Your weapon has no effect against this monster!");   // alt 1st → retry
        h.Feed("Your weapon has no effect against this monster!");   // alt 2nd → weapon out

        Assert.False(h.Combat.CanEngageMonster(1));                  // can't act now → move on
        h.Ma = 100;                                                 // MA regenerates
        Assert.True(h.Combat.CanEngageMonster(1));                  // castable again → retry
    }

    // ----- announce once; the server auto-repeats -----------------------

    [Fact]
    public void Heartbeat_AnnouncesOnce_ServerAutoRepeats()
    {
        // CONFIRMED mechanic: an announced spell attack auto-repeats server-side each
        // round (like a weapon swing). So the client announces it ONCE and the
        // heartbeat sends NOTHING on later rounds while the decision is unchanged —
        // re-sending a spell the server is already repeating is the double/corpse-cast
        // bug this rework removes.
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");      // announce once
        h.Tick();                             // server repeats — no re-send
        h.Tick();

        Assert.Equal(1, h.AllSent.Count(s => s == "blast"));
        Assert.DoesNotContain("a giant rat", h.AllSent);
    }

    [Fact]
    public void Heartbeat_MaxCastsReached_SwitchesToWeapon()
    {
        // MaxCasts is the number of rounds to cast the spell; after that the client
        // re-announces the next cascade action (here the weapon, no attack spells
        // configured). The spell is announced ONCE and the heartbeat counts each
        // round toward the cap.
        using Harness h = new();
        h.Settings.MultiAttackSpell =
            new CombatSpellSlot { SpellName = "blast", MinEnemies = 1, MaxCastsPerRoom = 2 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");      // announce (round 1 of 2)
        h.Tick();                             // round 2 of 2 — still repeating, no send
        h.Tick();                             // cap reached → switch to weapon

        Assert.Equal(1, h.AllSent.Count(s => s == "blast"));   // announced once
        Assert.Equal("a giant rat", h.LastSent);                          // switched to weapon
        h.Tick();                             // weapon mode — heartbeat quiet
        Assert.Equal("a giant rat", h.LastSent);
    }

    // ----- stop on death; re-engage the survivor as a spell -------------

    [Fact]
    public void SpellKill_SendsNothingAtTheCorpse()
    {
        // The kill ends the engagement (the server stops its repeat; the client
        // clears spell mode). The heartbeat must not re-announce at the dead target —
        // the "You don't see X here!" corpse cast this rework fixes.
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");        // announce harm
        Assert.Equal("harm giant rat", h.LastSent);
        int sentAtKill = h.Sent.Count;

        h.Feed("The giant rat dies.");
        h.Tick();
        h.Tick();

        Assert.Equal(sentAtKill, h.Sent.Count);   // nothing sent after the kill
    }

    [Fact]
    public void AfterKill_NextMonster_ReEngagedWithSpell()
    {
        // A spell fighter that kills a mob re-engages the next one with the SPELL
        // (via the chooser), not a weapon swing.
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "orc");

        h.Feed("Also here: giant rat.");        // announce harm at the rat
        h.Feed("The giant rat dies.");          // kill
        h.Feed("Also here: orc.");              // next monster
        h.Tick();                               // re-announce at the survivor

        Assert.Equal("harm orc", h.LastSent);
        Assert.DoesNotContain("a orc", h.AllSent);
    }

    [Fact]
    public void MaxCasts_SingleTarget_ResetsPerTarget()
    {
        // A single-target attack spell's MaxCasts is PER TARGET: after it caps on one
        // mob, the next mob gets the spell again (not stuck on the weapon).
        using Harness h = new();
        h.Settings.NormalAttackSpell =
            new CombatSpellSlot { SpellName = "harm", MinEnemies = 1, MaxCastsPerRoom = 1 };
        h.AddMonster(1, "giant rat");
        h.AddMonster(2, "orc");

        h.Feed("Also here: giant rat.");        // announce harm at the rat (per-target cap 1)
        Assert.Equal("harm giant rat", h.LastSent);
        h.Feed("The giant rat dies.");          // kill → per-target counters reset
        h.Feed("Also here: orc.");
        h.Tick();

        Assert.Equal("harm orc", h.LastSent);   // spell again, not weapon
    }

    [Fact]
    public void Heartbeat_ManaDrained_FallsToWeapon()
    {
        using Harness h = new();
        h.Settings.SpellManaThresholdMode = ThresholdMode.Absolute;
        h.Settings.MultiAttackSpell =
            new CombatSpellSlot { SpellName = "blast", MinEnemies = 1, MinManaPerCast = 30 };
        h.AddMonster(1, "giant rat");

        h.Ma = 50;
        h.Feed("Also here: giant rat.");      // cast (50 >= 30)
        Assert.Equal("blast", h.LastSent);

        h.Ma = 20;                            // now below the gate
        h.Tick();                             // mana too low → weapon

        Assert.Equal("a giant rat", h.LastSent);
    }

    // ----- in-between debuff bridge ------------------------------------

    [Fact]
    public void AreaDebuff_OfferedAsInBetween_OncePerRoom_CombatActionAttacks()
    {
        using Harness h = new();
        h.Settings.AreaDebuffSpell = new CombatSpellSlot { SpellName = "curse", MinEnemies = 1 };
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        // The combat action is the attack spell; the debuff is an in-between
        // action that CastingDirector pulls from the engine (not under test
        // here — we drive the bridge directly).
        h.Feed("Also here: giant rat.");
        Assert.Equal("blast", h.LastSent);

        (string Spell, string? Target)? debuff = h.Combat.PickInBetweenDebuff();
        Assert.Equal("curse", debuff?.Spell);
        Assert.Null(debuff?.Target);   // area debuff is cast bare — never "curse <mob>"
        h.Combat.CommitInBetweenDebuff();

        Assert.Null(h.Combat.PickInBetweenDebuff());   // once per room

        h.Tick();                                       // combat action unchanged
        Assert.Equal("blast", h.LastSent);
    }

    // ----- backstab gate -----------------------------------------------

    [Fact]
    public void BackstabPending_SuppressesSpell_SendsBackstab()
    {
        using Harness h = new();
        h.Settings.DoBackstab = true;
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.Sneaking = true;
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");

        Assert.Equal("bs giant rat", h.LastSent);
        Assert.DoesNotContain("blast", h.AllSent);
    }

    // ----- room clear resets the chooser bookkeeping -------------------

    [Fact]
    public void RoomCleared_ResetsCastCap_NextRoomReCasts()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell =
            new CombatSpellSlot { SpellName = "blast", MinEnemies = 1, MaxCastsPerRoom = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");      // room 1 — cast (cap 1 reached)
        Assert.Equal("blast", h.LastSent);

        h.Feed("Also here: Bob.");            // room cleared → chooser reset
        h.Tick();                             // round passes (clears cast cooldown)
        h.Feed("Also here: giant rat.");      // room 2 — cap reset, casts again

        Assert.Equal(2, h.AllSent.Count(s => s == "blast"));
    }

    // ----- damage-immunity fallback (CS-c) -----------------------------

    [Fact]
    public void SpellNoEffect_CascadesPrimaryToAlternateToWeapon()
    {
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "firebolt" };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "icebolt" };
        h.AddMonster(1, "acid slime");

        h.Feed("Also here: acid slime.");                 // primary attack spell
        Assert.Equal("firebolt acid slime", h.LastSent);

        // A round passes (firebolt repeats server-side; its result comes back next
        // round). The immunity line then swaps to the alternate SPELL *on the same
        // line* — no extra round burned (report paradigm-20260809-162350). This is
        // the fix: previously the spell branch idled until the NEXT tick, so the
        // assert here needed a Tick() after the no-effect line.
        h.Tick();
        h.Feed("Your spell has no effect on acid slime."); // firebolt immune → instant icebolt
        Assert.Equal("icebolt acid slime", h.LastSent);

        // Next round, the alternate is also immune → the cascade reaches the weapon.
        h.Tick();
        h.Feed("Your spell has no effect on acid slime."); // icebolt immune → weapon
        Assert.Equal("a acid slime", h.LastSent);
    }

    [Fact]
    public void SpellNoEffect_SameRoundBurst_SwapsOnceNotStraightToWeapon()
    {
        // The attack casts several times per round, so an immune target draws a
        // burst of "no effect" lines. The first swaps primary→alternate; the rest
        // of the burst (same round, no tick) must be ignored — else the alternate
        // we just chose is itself mis-marked immune and the cascade skips straight
        // to the weapon, never actually trying the alternate spell.
        using Harness h = new();
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "firebolt" };
        h.Settings.AlternateAttackSpell = new CombatSpellSlot { SpellName = "icebolt" };
        h.AddMonster(1, "acid slime");

        h.Feed("Also here: acid slime.");
        h.Tick();
        h.Feed("Your spell has no effect on acid slime.");   // firebolt immune → icebolt
        Assert.Equal("icebolt acid slime", h.LastSent);

        h.Feed("Your spell has no effect on acid slime.");   // leftover burst line, same round
        Assert.Equal("icebolt acid slime", h.LastSent);      // still icebolt — NOT "a acid slime"
        Assert.DoesNotContain("a acid slime", h.AllSent);
    }

    [Fact]
    public void SpellNoEffect_MultiAttack_NotGated_KeepsCasting()
    {
        using Harness h = new();
        h.Settings.MultiAttackSpell = new CombatSpellSlot { SpellName = "blast", MinEnemies = 1 };
        h.AddMonster(1, "acid slime");

        h.Feed("Also here: acid slime.");                 // multi-attack room spell
        Assert.Equal("blast", h.LastSent);

        // One immune mob doesn't mean the room spell isn't damaging the
        // rest — multi-attack is never marked immune.
        h.Feed("Your spell has no effect on acid slime.");
        h.Tick();
        Assert.Equal("blast", h.LastSent);
        Assert.DoesNotContain("a acid slime", h.AllSent);
    }

    // ----- Alternating action orders: every-round command driving -------
    // The Alternate* orders can't lean on the server auto-repeat — the desired
    // action flips each round, so the engine re-issues a command every round. The
    // heartbeat drives the flip in BOTH the spell-phase and (critically) the
    // weapon-phase rounds, where the fixed-order heartbeat would return early.

    [Fact]
    public void AlternateSpellPhysical_OpensOnSpell_ThenFlipsEachRound()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.AlternateSpellPhysical;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        // Engage — round 0 = spell phase.
        h.Feed("Also here: giant rat.");
        Assert.Equal("harm giant rat", h.LastSent);

        // Round 1 — physical (the weapon-phase flip the fixed heartbeat can't do).
        h.Tick();
        Assert.Equal("a giant rat", h.LastSent);

        // Round 2 — back to the spell.
        h.Tick();
        Assert.Equal("harm giant rat", h.LastSent);

        // Round 3 — physical again.
        h.Tick();
        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void AlternatePhysicalSpell_OpensOnSwing_ThenFlipsEachRound()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.AlternatePhysicalSpell;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        // Engage — round 0 = physical phase.
        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);

        // Round 1 — spell.
        h.Tick();
        Assert.Equal("harm giant rat", h.LastSent);

        // Round 2 — physical again.
        h.Tick();
        Assert.Equal("a giant rat", h.LastSent);
    }

    [Fact]
    public void AlternateSpellPhysical_SpellPhaseUnaffordable_SwingsThatRound()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.AlternateSpellPhysical;
        h.Settings.SpellManaThresholdMode = ThresholdMode.Absolute;
        h.Settings.NormalAttackSpell =
            new CombatSpellSlot { SpellName = "harm", MinEnemies = 1, MinManaPerCast = 30 };
        h.Ma = 10;                                   // below the spell's reserve
        h.AddMonster(1, "giant rat");

        // Engage on a spell phase, but mana is too low — fall back to the swing.
        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);
    }

    // ----- Round-cycle action order: configurable-length phases ---------
    // Unlike the fixed Alternate* orders above, a phase can span many rounds.
    // Continuing rounds within a phase must NOT resend — physical leans on the
    // server's own auto-repeat, and spell leans on the existing heartbeat's
    // same-decision dedup. Only a genuine phase boundary forces a fresh command,
    // and only the physical→spell edge needs an explicit push (the spell→physical
    // edge already falls out of the ordinary heartbeat re-deciding every tick).

    [Fact]
    public void CustomRoundCycle_PhysicalThenSpellsTillDeath_SwitchesOnceNoRepeats()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.CustomRoundCycle;
        h.Settings.CycleRoundsPhysical = 2;
        h.Settings.CycleRoundsSpell = 0;   // "spells till death" — never switches back
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        // Round 0 (engage) — physical phase.
        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);
        int afterEngage = h.Sent.Count;

        // Round 1 — still physical (1 < 2 rounds configured). No resend: the
        // server's own auto-repeat is carrying the swing.
        h.Tick();
        Assert.Equal(afterEngage, h.Sent.Count);

        // Round 2 — phase boundary: forced switch to the spell (nothing else
        // would ever interrupt an otherwise-passive physical auto-repeat).
        h.Tick();
        Assert.Equal("harm giant rat", h.LastSent);
        int afterSwitch = h.Sent.Count;

        // Rounds 3–4 — spells till death: stay on the cast, no re-announce
        // (re-sending a spell the server is already repeating is the
        // double-cast / corpse-cast bug the ordinary heartbeat dedup prevents).
        h.Tick();
        Assert.Equal(afterSwitch, h.Sent.Count);
        h.Tick();
        Assert.Equal(afterSwitch, h.Sent.Count);
    }

    [Fact]
    public void CustomRoundCycle_OneOneMatchesFixedAlternation()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.CustomRoundCycle;
        h.Settings.CycleRoundsPhysical = 1;
        h.Settings.CycleRoundsSpell = 1;
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        h.Feed("Also here: giant rat.");
        Assert.Equal("a giant rat", h.LastSent);       // round 0 — physical (default open)

        h.Tick();
        Assert.Equal("harm giant rat", h.LastSent);    // round 1 — spell

        h.Tick();
        Assert.Equal("a giant rat", h.LastSent);       // round 2 — physical again

        h.Tick();
        Assert.Equal("harm giant rat", h.LastSent);    // round 3 — spell again
    }

    [Fact]
    public void CustomRoundCycle_StartOnSpell_ThenPhysicalTillDeath()
    {
        using Harness h = new();
        h.Settings.ActionOrder = CombatActionOrder.CustomRoundCycle;
        h.Settings.CycleStartOnSpell = true;
        h.Settings.CycleRoundsSpell = 1;
        h.Settings.CycleRoundsPhysical = 0;   // physical forever once reached
        h.Settings.NormalAttackSpell = new CombatSpellSlot { SpellName = "harm", MinEnemies = 1 };
        h.AddMonster(1, "giant rat");

        // Round 0 (engage) — opens on the spell phase.
        h.Feed("Also here: giant rat.");
        Assert.Equal("harm giant rat", h.LastSent);

        // Round 1 — phase boundary: switches to physical.
        h.Tick();
        Assert.Equal("a giant rat", h.LastSent);
        int afterSwitch = h.Sent.Count;

        // Rounds 2–3 — physical till death: no resend.
        h.Tick();
        Assert.Equal(afterSwitch, h.Sent.Count);
        h.Tick();
        Assert.Equal(afterSwitch, h.Sent.Count);
    }
}

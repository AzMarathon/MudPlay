using MudPlay.Game.Calculators;
using MudPlay.Game.Combat;
using MudPlay.Game.Health;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;

namespace MudPlay.Game.Simulation;

// Plays a character around a loop in simulated time and measures what Session
// Stats measures live. Every decision the client makes goes through the client's
// own code — CombatSpellChooser picks the round's attack (with CombatSpellGates'
// per-monster overrides and blocks), SelfHealPicker the between-round heal — and
// every outcome comes from the combat math Monster Intel already uses (hit chance,
// spell damage after resists, cast chance, monster energy). Rolls are seeded, so a
// run is repeatable; several seeds give the spread.
//
// Time runs in quarter-second steps. Combat resolves on the global 5 s round, one
// between-round cast per round (GAME_MECHANICS "Combat round (5s) and the
// between-round cast cycle"); regen ticks on the realm's cadence (RealmRegenProfile).
// Reaching 0 HP ends the run as a death.
public static class LoopSimulator
{
    public static LoopSimSummary RunMany(
        SimCharacter character, IReadOnlyList<SimRoom> lap, SimWorld world,
        double secondsPerStep, double hours, int runs, CancellationToken cancel = default)
    {
        var results = new List<LoopSimRun>(Math.Max(0, runs));
        for (int seed = 1; seed <= runs; seed++)
        {
            cancel.ThrowIfCancellationRequested();
            results.Add(Run(character, lap, world, secondsPerStep, hours, seed, cancel));
        }
        return new LoopSimSummary(results);
    }

    public static LoopSimRun Run(
        SimCharacter character, IReadOnlyList<SimRoom> lap, SimWorld world,
        double secondsPerStep, double hours, int seed, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(lap);
        ArgumentNullException.ThrowIfNull(world);
        return new Session(character, lap, world, secondsPerStep, seed).Play(hours * 3600.0, cancel);
    }

    private enum Posture { Standing, Resting, Meditating }

    private sealed class Mob
    {
        public required MonsterCatalogEntry Entry { get; init; }
        public required string Key { get; init; }
        public required int LairSlot { get; init; }          // -1 = the room's placed NPC
        public required bool Hostile { get; init; }          // opens on us unprovoked
        public required bool Fightable { get; init; }        // the client engages it and can hurt it
        public int Hp { get; set; }
        public double Energy { get; set; }
        public bool Engaged { get; set; }
        public bool Alive => Hp > 0;
        public bool AttacksUs => Alive && (Hostile || Engaged);
    }

    private sealed class RoomState(int slots)
    {
        public double[] SlotReadyAt { get; } = new double[slots];
        public Mob?[] SlotMob { get; } = new Mob?[slots];
        public List<Mob> Mobs { get; } = new();
    }

    private sealed class Session
    {
        private const double Step = 0.25;
        private const int RoundSteps = 20;        // 5 s combat round
        // The engine's monster-create pass, which refills the lair a player stands
        // in, runs every 5 s by default (GAME_MECHANICS "Lair respawn timers").
        private const int SpawnPassSteps = 20;
        private const int CancelCheckSteps = 4000;   // every 1000 simulated seconds
        private const double MaxSwingsPerSlotPass = 20;

        private readonly SimCharacter _ch;
        private readonly IReadOnlyList<SimRoom> _lap;
        private readonly SimWorld _world;
        private readonly double _secondsPerStep;
        private readonly Random _rng;
        private readonly CombatSpellChooser _chooser = new();
        private readonly Dictionary<Map.RoomKey, RoomState> _rooms = new();
        private readonly Dictionary<string, int> _casts = new(StringComparer.OrdinalIgnoreCase);
        private readonly int _standingSteps, _restingSteps, _meditatingSteps;
        private readonly int _spawnPassPhase;

        private long _step;
        private double _hp, _ma;
        private Posture _posture = Posture.Standing;
        private long _postureSince;
        private bool _hpGate, _maGate;
        private int _pos;
        private double? _moveDoneAt;
        private Mob? _target;
        private double _swingCarry;
        private int _mobSerial;
        private long _exp;
        private int _kills, _laps;
        private double _moving, _attacking, _resting, _meditating, _waiting;
        private int _lowHp = 100, _lowMa = 100;
        private double? _diedAt;

        public Session(SimCharacter ch, IReadOnlyList<SimRoom> lap, SimWorld world, double secondsPerStep, int seed)
        {
            _ch = ch;
            _lap = lap;
            _world = world;
            _secondsPerStep = Math.Max(Step, secondsPerStep);
            _rng = new Random(seed);
            _hp = ch.MaxHp;
            _ma = ch.MaxMana;
            _standingSteps = StepsOf(ch.Regen.Cadence.StandingInterval);
            _restingSteps = StepsOf(ch.Regen.Cadence.RestingInterval);
            _meditatingSteps = StepsOf(ch.Regen.Cadence.MeditatingInterval);
            // The spawn pass runs on the server's own clock, unrelated to the round.
            _spawnPassPhase = _rng.Next(SpawnPassSteps);
        }

        private double Now => _step * Step;

        private static int StepsOf(TimeSpan t) => Math.Max(1, (int)Math.Round(t.TotalSeconds / Step));

        public LoopSimRun Play(double durationSeconds, CancellationToken cancel)
        {
            if (_lap.Count > 0) Enter(0);
            while (_lap.Count > 0 && Now < durationSeconds && _diedAt is null)
            {
                if (_step % CancelCheckSteps == 0) cancel.ThrowIfCancellationRequested();
                if (_moveDoneAt is { } done && Now >= done) Arrive();
                if (_moveDoneAt is null && _step % SpawnPassSteps == _spawnPassPhase) Spawn(Here(), entering: false);
                if (_step % RoundSteps == 0) Round();
                if (_diedAt is not null) break;
                Regen();
                Decide();
                Account();
                _step++;
            }
            return new LoopSimRun(durationSeconds, Now, _exp, _kills, _laps, _moving, _attacking, _resting, _meditating, _waiting,
                _lowHp, _lowMa, _diedAt, new Dictionary<string, int>(_casts, StringComparer.OrdinalIgnoreCase));
        }

        // ----- Rooms and spawns ---------

        private RoomState Here() => State(_lap[_pos]);

        private RoomState State(SimRoom room)
        {
            if (!_rooms.TryGetValue(room.Key, out RoomState? state))
                _rooms[room.Key] = state = new RoomState(room.HasLair ? room.LairMax : 0);
            return state;
        }

        private void Enter(int pos)
        {
            _pos = pos;
            SimRoom room = _lap[pos];
            RoomState state = State(room);
            // A placed fixture is back the moment you re-enter (GAME_MECHANICS
            // "NPC-placed monsters").
            if (room.NpcMonster > 0 && !state.Mobs.Any(m => m.LairSlot < 0 && m.Alive))
                AddMob(state, room.NpcMonster, lairSlot: -1);
            Spawn(state, entering: true);
        }

        // Fill every lair slot whose clock has run out. Walking in refills at once;
        // standing in the room refills only when the spawn pass comes round, so a
        // lair comes back slower under a character who waits in it. A lair whose
        // timer couldn't be resolved (RespawnSeconds 0) refills on entry only — a
        // zero clock checked while standing would respawn each kill on the spot.
        private void Spawn(RoomState state, bool entering)
        {
            SimRoom room = _lap[_pos];
            if (!room.HasLair || (!entering && room.RespawnSeconds <= 0)) return;
            for (int slot = 0; slot < state.SlotMob.Length; slot++)
            {
                if (state.SlotMob[slot] is not null || state.SlotReadyAt[slot] > Now) continue;
                int id = room.LairMonsters[_rng.Next(room.LairMonsters.Count)];
                state.SlotMob[slot] = AddMob(state, id, slot);
            }
        }

        private Mob? AddMob(RoomState state, int number, int lairSlot)
        {
            if (_world.Monster(number) is not { } entry) return null;
            // Lawful evil spares Outlaw-or-worse, as StockAggroCalculator.Acquire rules.
            bool hostile = entry.Align is 1 or 2 or 5
                || (entry.Align == 6 && _ch.AlignmentValue < StockAggroCalculator.OutlawValue);
            bool engageable = MonsterEngagement.IsEngageable(_ch.Overlay(number));
            var mob = new Mob
            {
                Entry = entry,
                Key = $"{entry.Name}#{++_mobSerial}",
                LairSlot = lairSlot,
                Hostile = hostile,
                Fightable = (engageable || hostile) && CanHurt(entry),
                Hp = Math.Max(1, entry.Hp),
            };
            state.Mobs.Add(mob);
            return mob;
        }

        // Whether anything in the configured cascade can damage it — the weapon clears
        // its Magical level, or the Normal / Alternate attack spell isn't blocked by
        // game data. The client walks past a monster it provably can't hurt.
        private bool CanHurt(MonsterCatalogEntry entry)
        {
            if (_ch.Melee.HasWeapon && _ch.WeaponHitMagic >= entry.Magical) return true;
            string? normal = NullIfBlank(_ch.Combat.NormalAttackSpell.SpellName);
            string? alt = NullIfBlank(_ch.Combat.AlternateAttackSpell.SpellName);
            MonsterOverlay overlay = _ch.Overlay(entry.Number);
            normal = _ch.SpellShortByNumber(overlay.OverrideAttackSpellId ?? 0) ?? normal;
            alt = _ch.SpellShortByNumber(overlay.OverrideAltAttackSpellId ?? 0) ?? alt;
            bool Blocked(string? code, CombatSpellAction action) =>
                code is null || !_ch.Spells.ContainsKey(code)
                || (Gates(entry.Number, null, normal, alt) is { } b && b.Contains(action));
            return !Blocked(normal, CombatSpellAction.NormalAttackSpell)
                || !Blocked(alt, CombatSpellAction.AlternateAttackSpell);
        }

        private IReadOnlySet<CombatSpellAction>? Gates(int number, string? single, string? normal, string? alt)
        {
            HashSet<CombatSpellAction>? all = null;
            foreach (IReadOnlySet<CombatSpellAction>? set in new[]
            {
                CombatSpellGates.LevelBlocked(_world.MonsterMagic, _world.SpellReqLevel, number, single, normal, alt),
                CombatSpellGates.ResistBlocked(_world.MonsterResist, _world.SpellAttackType, number, normal, alt),
                CombatSpellGates.TargetTypeBlocked(_world.SpellTargetType, _world.MonsterLife, number, normal, alt),
            })
                if (set is not null) (all ??= new HashSet<CombatSpellAction>()).UnionWith(set);
            return all;
        }

        private bool Fighting => Here().Mobs.Any(m => m.Alive && m.Fightable);

        // ----- The round ---------

        private void Round()
        {
            bool combat = _moveDoneAt is null && Fighting;
            if (combat)
            {
                _posture = Posture.Standing;
                PlayerAttack();
            }
            if (_moveDoneAt is null) MonstersAttack();
            Track();
            if (_hp <= 0) { _diedAt = Now; return; }
            BetweenRoundCast(combat && Fighting);
            Here().Mobs.RemoveAll(m => !m.Alive);
        }

        private void PlayerAttack()
        {
            RoomState room = Here();
            List<Mob> fightable = room.Mobs.Where(m => m.Alive && m.Fightable).ToList();
            if (_target is null || !_target.Alive || !room.Mobs.Contains(_target))
            {
                _target = fightable[0];
                _chooser.ResetForNewTarget();
            }
            Mob target = _target;
            target.Engaged = true;

            CombatSpellDecision decision = _chooser.Choose(_ch.Combat, Context(target, fightable));
            bool area = decision.Action is CombatSpellAction.MultiAttack or CombatSpellAction.MultiAttack2;
            if (decision.Spell is { } code && _ch.Spells.TryGetValue(code, out SimSpell? spell))
            {
                if (CastAttack(spell, target, area ? fightable : null))
                {
                    _chooser.MarkCast(decision, target.Key, fightable.Select(m => m.Key).ToList());
                    Count(spell.Short);
                }
            }
            else
            {
                Swing(target);
                Count(_ch.Combat.NormalAttackCommand);
            }

            if (!room.Mobs.Any(m => m.Alive && m.Fightable)) _chooser.ResetForRosterClear();
        }

        private CombatSpellContext Context(Mob target, List<Mob> fightable)
        {
            int ma = (int)_ma;
            int number = target.Entry.Number;
            MonsterOverlay o = _ch.Overlay(number);
            ThresholdMode mode = _ch.Combat.SpellManaThresholdMode;
            (string? atk, int? atkCap) = CombatSpellGates.ResolveOverride(o.OverrideAttackSpellId, o.OverrideAttackCount,
                o.OverrideAttackMinMana, ma, _ch.MaxMana, mode, _ch.SpellShortByNumber);
            (string? alt, int? altCap) = CombatSpellGates.ResolveOverride(o.OverrideAltAttackSpellId, o.OverrideAltAttackCount,
                o.OverrideAltAttackMinMana, ma, _ch.MaxMana, mode, _ch.SpellShortByNumber);
            (string? pre, int? preCap) = CombatSpellGates.ResolveOverride(o.OverridePreAttackSpellId, o.OverridePreAttackCount,
                o.OverridePreAttackMinMana, ma, _ch.MaxMana, mode, _ch.SpellShortByNumber);
            string? normalEff = atk ?? NullIfBlank(_ch.Combat.NormalAttackSpell.SpellName);
            string? altEff = alt ?? NullIfBlank(_ch.Combat.AlternateAttackSpell.SpellName);
            string? singleEff = pre ?? NullIfBlank(_ch.Combat.SingleTargetDebuffSpell.SpellName);
            bool hpBelowDrain = _ch.Combat.DrainHpTrigger > 0
                && _hp <= Math.Round(_ch.MaxHp * _ch.Combat.DrainHpTrigger / 100.0);

            return new CombatSpellContext(
                EnemyCount: fightable.Count,
                TargetRawName: target.Key,
                Mana: ma,
                MaxMana: _ch.MaxMana,
                BackstabPending: false,
                SpellsAvailable: ma > 0,
                LevelBlockedActions: CombatSpellGates.LevelBlocked(_world.MonsterMagic, _world.SpellReqLevel, number, singleEff, normalEff, altEff),
                ResistBlockedActions: CombatSpellGates.ResistBlocked(_world.MonsterResist, _world.SpellAttackType, number, normalEff, altEff),
                TargetTypeBlockedActions: CombatSpellGates.TargetTypeBlocked(_world.SpellTargetType, _world.MonsterLife, number, normalEff, altEff),
                TargetDontBackstab: o.DontBackstab ?? false,
                OverrideAttackSpell: atk,
                OverrideAttackMaxCasts: atkCap,
                OverridePreAttackSpell: pre,
                OverridePreAttackMaxCasts: preCap,
                OverrideAltAttackSpell: alt,
                OverrideAltAttackMaxCasts: altCap,
                WeaponIneffective: !_ch.Melee.HasWeapon || _ch.WeaponHitMagic < target.Entry.Magical,
                HpBelowDrainTrigger: hpBelowDrain,
                DrainTargetEligible: _world.MonsterLife?.CanDrain(number) ?? true,
                RoomMobKeys: fightable.Select(m => m.Key).ToList(),
                ManaCostOf: c => _ch.Spells.TryGetValue(c, out SimSpell? s) ? s.ManaPerCast : null);
        }

        // One round of an attack spell: up to FiresPerRound fires, each paying its
        // mana whether it lands or not, the round's damage range split across them.
        // A single-target spell stops firing once its target dies. False when not one
        // fire could be paid for.
        private bool CastAttack(SimSpell spell, Mob target, List<Mob>? room)
        {
            int fires = Math.Max(1, spell.FiresPerRound);
            bool fired = false;
            for (int f = 0; f < fires; f++)
            {
                if (_ma < spell.ManaPerCast) break;
                _ma -= spell.ManaPerCast;
                fired = true;
                if (!Roll(spell.CastChance ?? 100)) continue;
                if (room is null)
                {
                    SpellHit(target, spell, fires);
                    if (!target.Alive) break;
                }
                else
                {
                    foreach (Mob m in room.Where(m => m.Alive).ToList()) SpellHit(m, spell, fires);
                    if (!room.Any(m => m.Alive)) break;
                }
            }
            return fired;
        }

        private void SpellHit(Mob mob, SimSpell spell, int fires)
        {
            MonsterCatalogEntry e = mob.Entry;
            mob.Engaged = true;
            if (Roll(SpellDamageCalculator.FullResistChance(spell.TypeOfResists, e.SpellMagicResist, e.AntiMagic))) return;
            long lo = spell.MinDamagePerRound / fires, hi = spell.MaxDamagePerRound / fires;
            long raw = lo >= hi ? hi : lo + (long)(_rng.NextDouble() * (hi - lo + 1));
            int code = MonsterResistIndex.ElementalResistCode(spell.AttType);
            int resist = code >= 0 && e.ElementalResists.TryGetValue(code, out int pct) ? pct : 0;
            long dmg = SpellDamageCalculator.AfterTargetResists(raw, spell.UsesMagicResist, e.SpellMagicResist, e.AntiMagic, resist, _ch.Realm);
            Damage(mob, dmg);
        }

        // The weapon round: swings carry their fractional remainder into the next
        // round (energy rolls over — GAME_MECHANICS "Player physical swings per round").
        private void Swing(Mob target)
        {
            PlayerMatchupProfile melee = _ch.Melee;
            if (!melee.HasWeapon || _ch.WeaponHitMagic < target.Entry.Magical) return;
            _swingCarry += melee.SwingsPerRound;
            int swings = (int)_swingCarry;
            _swingCarry -= swings;
            int hit = CombatCalculator.CalculateHitChance(melee.NormalAccuracy, target.Entry.ArmourClass,
                target.Entry.Dodge, realmType: _ch.Realm).OverallHitPercent;
            int dr = target.Entry.DamageResist * melee.MonsterDrMultiplier;
            for (int i = 0; i < swings && target.Alive; i++)
            {
                if (!Roll(hit)) continue;
                int dmg = Roll(melee.CritChancePercent) ? melee.AvgCritDamage : melee.AvgWeaponDamage;
                Damage(target, Math.Max(0, dmg - dr));
            }
        }

        private void Damage(Mob mob, long amount)
        {
            if (!mob.Alive) return;
            mob.Hp = (int)Math.Min(mob.Entry.Hp, mob.Hp - amount);   // a negative amount heals, never past max
            if (mob.Alive) return;

            _exp += mob.Entry.EffectiveExp;
            _kills++;
            RoomState room = Here();
            if (mob.LairSlot >= 0)
            {
                room.SlotMob[mob.LairSlot] = null;
                room.SlotReadyAt[mob.LairSlot] = Now + _lap[_pos].RespawnSeconds;
            }
            if (ReferenceEquals(mob, _target))
            {
                _target = null;
                _chooser.ResetForNewTarget();
            }
        }

        // Each monster fighting us spends its round's energy (plus last round's
        // leftover) on attacks picked by their use-weight, then rolls its
        // between-rounds spells (GAME_MECHANICS "Monster swings per round: energy
        // budget and rollover").
        private void MonstersAttack()
        {
            PlayerDefenseProfile def = _ch.Defense;
            foreach (Mob mob in Here().Mobs.Where(m => m.AttacksUs))
            {
                MonsterCatalogEntry e = mob.Entry;
                var slots = e.Attacks.Where(a => a.Percent > 0 && a.Type is 1 or 2).ToList();
                mob.Energy += e.Energy > 0 ? e.Energy : 1000;
                for (int n = 0; n < MaxSwingsPerSlotPass && slots.Count > 0; n++)
                {
                    MonsterAttackSlot a = PickWeighted(slots);
                    int cost = a.Energy > 0 ? a.Energy : (e.Energy > 0 ? e.Energy : 1000);
                    if (mob.Energy < cost) break;
                    mob.Energy -= cost;
                    if (a.Type == 1)
                    {
                        int hit = MonsterMatchupCalculatorSpells.AttackHitPercent(a.Accuracy, e.Align, def.Ac, def.Dodge,
                            def.ProtEvil, def.ProtGood, _ch.Realm, def.Shadow, def.VileWard, def.Evil, def.ArmourType);
                        if (Roll(hit)) _hp -= Math.Max(0, Between(a.MinDamage, a.MaxDamage) - _ch.Melee.DamageResist);
                    }
                    else if (a.SpellDmgMax > 0 && Roll(a.MinDamage))
                    {
                        // A spell slot's MinDamage field holds its cast-success percent.
                        _hp -= Between(a.SpellDmgMin, a.SpellDmgMax);
                    }
                }
                foreach (MonsterMidSpellSlot m in e.MidSpells)
                    if (m.DmgMax > 0 && Roll(m.Percent)) _hp -= Between(m.DmgMin, m.DmgMax);
            }
        }

        private MonsterAttackSlot PickWeighted(List<MonsterAttackSlot> slots)
        {
            double total = slots.Sum(Weight);
            double r = _rng.NextDouble() * total;
            foreach (MonsterAttackSlot s in slots)
            {
                r -= Weight(s);
                if (r < 0) return s;
            }
            return slots[^1];

            static double Weight(MonsterAttackSlot s) => s.TruePercent > 0 ? s.TruePercent : s.Percent;
        }

        // The round's one between-round cast: the self-heal tiers in the Spells-tab
        // priority order, the first due and affordable one firing. A cast stands a
        // resting character up (GAME_MECHANICS "Casting a spell interrupts resting /
        // meditating").
        private void BetweenRoundCast(bool inCombat)
        {
            SpellsSettings spells = _ch.SpellSlots;
            HealthSettings health = _ch.Health;
            var inputs = new SelfHealInputs((int)_hp, _ch.MaxHp, (int)_ma, _ch.MaxMana, inCombat,
                Resting: _posture == Posture.Resting,
                HealHpTrigger: (mode, value) => RestThresholds.ResolveValue(mode, value, _ch.DefaultMaxHp, _ch.MaxHp, _ch.MaxHp),
                Affordable: code => !_ch.Spells.TryGetValue(code, out SimSpell? s) || _ma >= s.ManaPerCast,
                HpRegenRecastDue: _ => false);

            (int Priority, int Order, Func<string?> Pick)[] tiers =
            {
                (spells.PriorityEmergencyHeal, 0, () => SelfHealPicker.Emergency(spells, health, inputs)),
                (spells.PriorityMinorSelfHeal, 1, () => SelfHealPicker.Minor(spells, health, inputs)),
                (spells.PriorityMajorSelfHeal, 2, () => SelfHealPicker.Major(spells, health, inputs)),
            };
            foreach (var tier in tiers.OrderBy(t => t.Priority).ThenBy(t => t.Order))
            {
                if (tier.Pick() is not { } code || !_ch.Spells.TryGetValue(code, out SimSpell? heal)) continue;
                if (_ma < heal.ManaPerCast) continue;
                _ma -= heal.ManaPerCast;
                Count(heal.Short);
                if (_posture != Posture.Standing) _posture = Posture.Standing;
                if (Roll(heal.CastChance ?? 100))
                    _hp = Math.Min(_ch.MaxHp, _hp + Between(heal.MinHeal, heal.MaxHeal));
                return;
            }
        }

        // ----- Between rounds: regen, rest, move ---------

        private void Regen()
        {
            SimRegen r = _ch.Regen;
            long since = _step - _postureSince;
            bool resting = _posture == Posture.Resting;
            if (_step > 0 && _step % _standingSteps == 0)
            {
                if (!resting || !r.RestReplacesStanding) _hp += r.HpStanding;
                _ma += r.MaStanding;
            }
            if (resting && since > 0 && since % _restingSteps == 0)
            {
                long tick = since / _restingSteps;
                _hp += tick % Math.Max(1, r.RestFullEvery) == 0 ? r.HpResting : r.HpResting * r.RestReducedShare;
            }
            if (_posture == Posture.Meditating && since > 0 && since % _meditatingSteps == 0)
                _ma += r.MaMeditating;
            _hp = Math.Min(_hp, _ch.MaxHp);
            _ma = Math.Min(_ma, _ch.MaxMana);
        }

        // Out of combat: rest while a pool sits under its rest trigger until it's
        // back to its rest target (HealthManager's gates), else walk on.
        private void Decide()
        {
            if (_moveDoneAt is not null || Fighting) return;
            HealthSettings h = _ch.Health;
            (int hpLow, int hpFull) = RestThresholds.Resolve(h.HpThresholdMode, h.RestIfBelowHp, h.RestMaxHp,
                _ch.DefaultMaxHp, _ch.MaxHp, _ch.MaxHp);
            (int maLow, int maFull) = RestThresholds.Resolve(h.MaThresholdMode, h.RestIfBelowMa, h.RestMaxMa,
                _ch.DefaultMaxMana, _ch.MaxMana, _ch.MaxMana);
            _hpGate = _hpGate ? _hp < hpFull : _hp < hpLow;
            _maGate = _ch.MaxMana > 0 && (_maGate ? _ma < maFull : _ma < maLow);

            if (_hpGate || _maGate)
            {
                Posture want = RestPosture(h);
                if (_posture != want)
                {
                    _posture = want;
                    _postureSince = _step;
                }
                return;
            }
            _posture = Posture.Standing;
            _moveDoneAt = Now + _secondsPerStep;
        }

        // HealthManager.ChooseRestCommand: meditate for mana alone (or first, when
        // MeditateBeforeResting), rest otherwise.
        private Posture RestPosture(HealthSettings h)
        {
            if (!h.UseMeditateAbility || _ch.MaxMana <= 0) return Posture.Resting;
            if (_maGate && !_hpGate) return Posture.Meditating;
            if (_hpGate && _maGate && h.MeditateBeforeResting) return Posture.Meditating;
            return Posture.Resting;
        }

        private void Arrive()
        {
            _moveDoneAt = null;
            int next = (_pos + 1) % _lap.Count;
            if (next == 0) _laps++;
            _target = null;
            _chooser.ResetForNewRoom();
            Enter(next);
        }

        private void Account()
        {
            if (_moveDoneAt is not null) _moving += Step;
            else if (Fighting) _attacking += Step;
            else if (_posture == Posture.Meditating) _meditating += Step;
            else if (_posture == Posture.Resting) _resting += Step;
            else _waiting += Step;
        }

        private void Track()
        {
            if (_ch.MaxHp > 0) _lowHp = Math.Min(_lowHp, (int)Math.Floor(Math.Max(0, _hp) * 100 / _ch.MaxHp));
            if (_ch.MaxMana > 0) _lowMa = Math.Min(_lowMa, (int)Math.Floor(Math.Max(0, _ma) * 100 / _ch.MaxMana));
        }

        private void Count(string code) => _casts[code] = _casts.GetValueOrDefault(code) + 1;

        private bool Roll(int percent) => percent >= 100 || (percent > 0 && _rng.Next(100) < percent);

        private int Between(long min, long max) =>
            (int)(min >= max ? max : min + (long)(_rng.NextDouble() * (max - min + 1)));

        private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
    }
}

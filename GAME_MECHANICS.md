# Game Mechanics Reference — MajorMUD / MegaMUD

How the game engine actually behaves and what messages it emits. This is the trusted record
so a session doesn't re-guess engine behavior — **read it before reasoning about the game, and
append to it when the user confirms something new.** Per CLAUDE.md: never invent a mechanic; if
it isn't here and you're unsure, ask.

**Confidence tags**
- **[CONFIRMED]** — the user confirmed it directly.
- **[OBSERVED]** — grounded in a real bug-report capture, the imported game data, or the client's
  own parsers / message handling; strongly evidenced but not explicitly user-confirmed.
- **[NEEDS CONFIRMATION]** — the code currently relies on it, but it's unverified; ask before
  extending anything that depends on it.
- **[CONFLICT — ask the user]** — two recorded statements disagree; both are kept until the user
  settles it.
- **Unrated** — recorded without a confidence tag; treat it as unverified and check the code or ask
  before building on it.
- **Client policy** — a MudPlay design decision, not game behaviour; recorded here because it sits
  next to the mechanic it acts on.

A status line with no `Realm:` part means the realm wasn't recorded — don't assume `both`.

**Entry format.** Each `###` topic sits in the one chapter its subject belongs to, and reads:
1. a status line — `*Status: CONFIRMED <date> (<source>; report \`<id>\`) · Realm: both | Paradigm | Stock | differs*`;
2. the rules as bullets, each leading with the rule in **bold**, exact game text in backticks;
3. a **Client use:** list when the client relies on the rule (the class / method, and the report that drove it).

**Adding an entry.** Find the chapter by subject (the contents below) and extend the existing
topic if there is one — search for the command, message text, or ability name first so a fact is
never recorded twice. Start a new `###` topic only for a genuinely new subject. When a realm
differs, say which in the status line or on the bullet. When new information contradicts an entry,
rewrite it to the current truth and keep the old claim as a one-line superseded note. The full
editing rules (what belongs here, chapter scopes, cross-references, renames) are in CLAUDE.md's
*GAME_MECHANICS.md — the engine reference* section.

## Contents
1. [Timing & rounds](#timing--rounds)
2. [Wire, prompt & command output](#wire-prompt--command-output)
3. [Talk & chat channels](#talk--chat-channels)
4. [Character stats & progression](#character-stats--progression)
5. [Armour, defence & to-hit](#armour-defence--to-hit)
6. [Health, resting & recovery](#health-resting--recovery)
7. [Combat](#combat)
8. [Spells, buffs & conditions](#spells-buffs--conditions)
9. [Monsters, lairs & spawns](#monsters-lairs--spawns)
10. [Movement & navigation](#movement--navigation)
11. [Items, inventory & equipment](#items-inventory--equipment)
12. [Money, banks & shops](#money-banks--shops)
13. [Party](#party)
14. [Quests](#quests)
15. [Death & corpse recovery](#death--corpse-recovery)
16. [Sysop commands](#sysop-commands)

---

## Timing & rounds

How game time is sliced: the 5-second combat round / global tick, the 3-second spell round, and
how many swings or spell fires a player or monster gets inside one round.

### Combat round (5s) and the between-round cast cycle
*Status: CONFIRMED (user); between-round-cycle rule CONFIRMED 2026-09-16 (user; report `paradigm-20260916-074131`)*

- **A combat round is 5 seconds** — precisely ~5.04s; the client shows it as a 5-second round.
  This is the cadence of combat lines. *([CONFIRMED])*
- **A between-round spell can be cast once per combat round.** *([CONFIRMED])* See *Spells, buffs &
  conditions → One between-round spell per combat round*.
- **The between-round cycle runs on this SAME 5s tick WHETHER OR NOT you are in combat.**
  *([CONFIRMED] 2026-09-16, user)* The one-0-energy-cast-per-round cap always applies; a second
  cast within the same 5s window draws `You have already cast a spell this round!`.
- **Client use:**
  - Slot tracking must NOT be gated on the client's `InCombat` flag, which flickers false during
    the between-kill *Combat Off* gap. A fresh arrival's pre-attack debuff fired in that flicker
    re-entered a spent round, and its rejection latched the block that then delayed the coupled
    attack a whole round (report `paradigm-20260916-074131`).
  - The slot is tracked time-scoped to the 5s window in `CastingDirector`, freed on the round tick
    or after the window lapses.

### Spell round (3s) and durations
*Status: CONFIRMED (user); wall-clock length OBSERVED (report `paradigm-20260816-222917`; observed on Paradigm)*

- **A spell round is 3 seconds.** *([CONFIRMED])* Buff/debuff durations on a player, item
  durations, and spell durations (e.g. a teleport / boat-transit spell) are all counted in
  **spell rounds** — a duration of `N` rounds lasts `N × 3` seconds. A debuff falls off on the
  same 3s cadence.
- **A spell record's `Dur` field is spell rounds:** real seconds = `Dur × 3`.
- **Boat-voyage length** is the sum of the transit spells' `Dur` along the disembark chain, × 3s.
- **The real spell round runs slightly LONG (~3.04s).** *([OBSERVED], report
  `paradigm-20260816-222917`)* Just as the combat round is ~5.04s not 5.0, a 50-round buff
  (`prev`, protection from evil) was observed lasting **~151-152s**, i.e. ~**3.04s/round**, not the
  nominal 150s. A "recast within N s" slot is measured against the buff's **real** remaining
  seconds, so it must time off ~3.04s or it recasts ~1-2s early.
- **If a specific duration's unit is ever ambiguous, ask the user** — they can give the correct
  value rather than us guessing.
- **Client use:**
  - Spell-data displays keep the nominal 3s; the live recast clock + Buff Watchdog use
    `SpellCalculator.SpellRoundSecondsWallClock` (3.04).

### Global combat tick and exp accrual
*Status: CONFIRMED 2026-08-02 (user)*

- **Combat fires on a fixed 5-second global tick** (720 ticks/hour), whether or not you're doing
  anything. If you aren't engaged with a monster, the tick is a **no-op** on the engine side — you
  send no combat round out, so nothing can die and no exp is awarded that tick.
- **Engaged = a monster is in the room AND you sent an attack command at it.** Only then does a
  tick land a round.
- **A tick in a room with no (live) monster yields nothing** — no damage out, no exp in.
- **Kill timing is counted in ticks from engagement.** A mob you kill "in 1 round" dies on the
  **next** combat tick after you engaged it; a 2-round mob dies after 2 ticks pass; etc. This is
  why the estimator's "rounds to kill a mob" accepts **decimals** — if some mobs die in 1 round and
  some in 3, the average is fractional.
- **Movement rides the downtime between ticks.** A hop from one lair to the next that completes
  within the ~5s gap re-engages you before the next tick, so it drops **no** combat round — travel
  is effectively free when it fits. Travel only costs exp when a stretch is long enough that a tick
  fires while you're standing in a monster-less room mid-transit.
- **Free rooms per hop ≈ `floor(5s ÷ step-seconds)`.** Concretely (user): at **1.0–1.2s per step
  you can cross 4 empty (non-lair) rooms** between lairs without dropping a round; **above 1.2s it
  drops to 3**. Use the step time you actually **observe**, not the configured one *([CONFIRMED]
  2026-09-26, user)*: lag stretches it — a Paradigm character pinned at the 1.0s movement floor
  typically sees room displays land every 1.1–1.2s, which is why the figure is 4, not the 5 a
  literal 1.0s would give.
- **Step time differs by realm** *([CONFIRMED] 2026-09-26, user)*. **Stock** averages roughly
  **0.6–0.7s per room**; **Paradigm** is bounded by its movement-speed formula (see *Movement &
  navigation → Per-hop movement speed*), 1.0s at best, plus lag.

### Exp/hour ceiling and loop geometry
*Status: CONFIRMED 2026-08-02 (user); single-target-ceiling rule CONFIRMED 2026-08-14 (user)*

- **A perfectly streamlined lair loop keeps a live mob engaged on all 720 ticks**, so the ceiling
  is `720 × avg-exp-per-kill` (matches the "720 kills/hour" cave-worm cap).
- **Travel overlaps tick downtime rather than adding to combat time.** Charging travel as
  wall-clock time *added to* combat (as a naive lap model does) understates a tight loop, because
  in reality that travel overlaps the downtime and doesn't consume ticks.
- **720/hr is only the *single-target* ceiling, and few loops reach it.** *([CONFIRMED], user
  2026-08-14)* It's the max a melee / single-target-spell player can kill (one mob per 5s tick);
  **rooming (AoE) clears a whole room per pass, so it runs ABOVE 720/hr.**
- **Whether a loop approaches its ceiling depends entirely on geometry.** In a line (out-and-back
  / A→B→A) loop you re-cross just-cleared lairs on the return — an empty room you simply walk
  through (no pause, no fight). With a 30s respawn and a sub-30s return you find them still down,
  so that return walk is dead time that wastes combat ticks. So the middle lairs of a line are hit
  less than a naive "each lair once per lap" count, and the end lairs less than the middle.
- **Client use:**
  - A faithful estimate must **replay the actual room order** with per-mob respawn clocks (see
    *Monsters, lairs & spawns → Lair respawn timers*), not assume a uniform per-lap fire rate —
    that's what the Exp/Hr estimator now does.

### Combat spells: engaged once, auto-repeat per round
*Status: CONFIRMED 2026-08-22 (user)*

- **A combat spell is engaged ONCE and auto-repeats.** You type `disr zombie` a single time; the
  engine then re-fires it **every round on its own** — you do NOT re-issue it per round — until the
  monster dies or an **in-between-round action breaks combat**. The repeat stops on `break`,
  moving, the room clearing, the target dying (single-target) or any non-swing action (cast /
  equip) with its `*Combat Off*` (see *Combat → Non-swing actions break combat (casting,
  equipping)*). See also *Combat → Spell attacks auto-repeat; re-announcing a single-target spell is harmless*.
- **Fires per round = `floor(1000 / EnergyCost)`** — the 1000-energy round budget over the spell's
  cost. A **500-cost** spell (`disr`, `mmis`, `lbol`) fires **up to twice** a round, **333** → 3×,
  **250** → 4×.
- **That is the maximum.** It fires **fewer** when the monster **dies on an earlier shot** or combat
  order ends the round first. Concretely for `disr` (500): a **30-HP** mob dies on the first shot →
  **1 fire total**; a **700-HP** mob → ~3–4 rounds showing **2 fires each round** (absent other
  damage).
- **The client cannot suppress a mid-round repeat** (server/energy-driven), so the earliest a spell
  swap (e.g. a `MaxCastsPerRoom` cap) can take effect is the **next round** — the cap-switch is a
  client override sent after counting.
- **Client use:**
  - Cap-counting: **one round** of a 500-cost cap spell is **up to two** `You cast X …` result
    lines, and a per-encounter cap must treat that round's fires as **ONE unit**, not two casts.
  - Deriving the expected per-round fire count from `floor(1000/EnergyCost)` is a more robust
    "same round" signal than a fixed wall-clock grouping window.

### Player physical swings per round: cap and energy formula
*Status: CONFIRMED 2026-08-22 (user); cap surplus + energy formula CONFIRMED 2026-08-31 (user, vs MMUD-Explorer + live game) · Realm: both*

- **Physical swings per round come from a swing calculation, hard-capped at 5 on Stock, 6 on
  Paradigm.** *([CONFIRMED] 2026-08-22, user)*
- **The uncapped raw figure can exceed the cap, and the surplus isn't discarded.** The cap limits
  the per-round *integer* swings; the surplus over the cap feeds the Quick-and-Deadly bonus.
  *([CONFIRMED] 2026-08-31, user — Paradigm `stat all` shows 7.143 attacks "capped to 6, and the
  extra then applies towards the QND bonus".)*
- **Swing energy uses `level × CombatLVL`, NOT `level × (CombatLVL + 2)`.** The per-swing energy
  divisor is `((level × combatLvl) + 45) × (agi + 150) / 6`, where `combatLvl` is the class's raw
  **CombatLVL** field. MMUD-Explorer expresses the same value via `GetClassCombat = CombatLVL − 2`
  (modMMudDatabase.bas) fed into a `(nCombat + 2)` form — the −2 and +2 cancel, so the net is
  `level × CombatLVL`.
- **Accuracy is the exception:** it uses the raw CombatLVL directly (MMUD-Explorer re-adds the +2:
  `nCombatLevel = GetClassCombat + 2`).
- **Evidence** *([CONFIRMED] 2026-08-31 vs MMUD-Explorer + live game)*: a L28 Paladin (CombatLVL 6)
  with throwing hammers (speed 1100) at 57% encum reads normal 7.143 / bash 3.572 in `stat all`,
  matching `level × 6`; `level × 8` inflated it to 9 / 4.5.
- **Leftover player energy rolls over** the same way as a monster's (see *Monster swings per round: energy budget and rollover*): `CombatCalculator`
  uses `remaining = (remaining % energy) + 1000`.

### Monster swings per round: energy budget and rollover
*Status: CONFIRMED 2026-09-03 (user); attacks/round readout CONFIRMED 2026-08-22 (user)*

- **A monster works like a player: it has a per-round energy budget** (the Monsters-table
  **`Energy`** field, typically ~1000; 1000 on Stock) and each swing of an attack costs that
  attack's **`AttEnergy`**. It spends its whole budget each round, landing
  `floor(available ÷ AttEnergy)` swings.
- **Monster attacks/round = `energy ÷ per-attack energy`.** The `Energy` budget divided by a slot's
  `AttEnergy-N` gives that slot's attacks/round — the same `Max N x/round` the Game-Data monster
  readout shows; the monster-side analog of the player's `floor(1000/EnergyCost)`.
- **Leftover energy ROLLS OVER:** round 1 starts at `Energy`, and every round after is
  `(leftover) + Energy`. So over a long window the mean swing rate is the **FRACTIONAL**
  `Energy ÷ AttEnergy`, not the single-round integer floor (e.g. 1000/300 = 3.33/round, not 3).
  This is the same model the player swing calc already uses (`CombatCalculator`: `remaining =
  (remaining % energy) + 1000`).
- **No realm swing cap for monsters** — unlike the player's 5 (Stock) / 6 (Paradigm) ceiling, a
  monster is limited only by energy ÷ attack cost.

### Slowness (ability 68) attack-speed penalty
*Status: CONFIRMED 2026-09-02 (user + syntax53/MMUD-Explorer `modMMudFunc.bas`)*

- **Slowness (ability 68) multiplies attack speed/energy by ×1.5** (`Fix(speed × 3 / 2)` —
  MMUD-Explorer `AdjustSpeedForSlowness` + the per-attack `bAbil68Slow` overrides), yielding ~a
  third fewer swings.
- **Player path:** MMUD-Explorer applies it to weapon speed.
- **Monster path:** applied to a MONSTER (a slowness debuff landed on it), it raises the monster's
  effective per-attack energy the same ×1.5, thinning its attacks/round.

---

## Wire, prompt & command output

What the game prints on the wire, including the prompt/statline, the command rate limiter, the output formats of informational commands (`spells`, `health`; the `bank` output is in *Money, banks & shops → Bank commands: balance / withdraw / deposit*), social/interaction lines, the realm exit sequence, the catalogue of lines the client parses, and the MegaMUD `messages.md` source format.

### Statline & prompt shape
*Status: Unrated*

- **The prompt/statline is user-defined.** MajorMUD's `set statline` lets a player format the prompt however they like. The default is bracketed HP/mana (`[HP=%h/MA=%m]: %r`, or `KAI`, or HP-only), and that is what the vast majority run. A custom statline can be any shape (`set statline full custom <template>`).
- **Template static text is exact; dynamic parts are `%`-wildcards** (`%h`, `%m`, `%r`, …).
- **The configured statline is what actually prints on the wire.** MudPlay is the source of truth for the live statline: it sends `set statline` on logon and re-sends it on any parser mismatch.
- **Echo detection keys off the player's configured prompt, not a hardcoded `[HP=..]:`.** The same template compiles into the exact matcher used to read HP/mana AND to find the echoed command after the prompt, so the move-echo gate works whatever statline the player sets.
- **If an echo can't be read, the tracker falls back to timing rather than freezing.**

**Client use:**
- StatlineReconciler sends `set statline` on logon and re-sends it on a parser mismatch.
- StatlinePromptRegexBuilder compiles the template into the prompt matcher, which reads HP/mana and locates the echoed command.

### Command rate limit (typing/sending too fast)
*Status: CONFIRMED 2026-08-25 (user); bulk `get`/`drop` extension OBSERVED 2026-09-02 (capture `stock-20260902-224515`); prompt-per-rate-line OBSERVED 2026-09-03 · Realm: both (wording differs per realm)*

- **The game throttles how fast a client may send commands** (typed input or telepaths). Exceeding the limit means the offending command **is dropped and never processed**. The wording of the notice is **realm-specific**.
- **Stock realms are stricter and give a two-tier signal.** Approaching the limit, the game nudges with **`Why don't you slow down for a few seconds?`**. Pushing past it drops the command with **`You are typing too quickly - command ignored`**.
- **Paradigm realms give no early warning.** They accept moderately paced rapid input without complaint and only object to a **burst of more than ~10 lines at once**, with the red line **`Too many messages sent - please wait for a few moments before
  trying again`**. Any lines beyond the burst allowance are dropped.
- **Bulk sends must be paced, and a rate-limit line means the last send was lost.** For example, `@roomba sync` can be ~20 telepaths. Pace them out so a burst never forms, and treat the "command ignored" / "too many messages" lines as a signal that the last send was lost and should be re-sent.
- **Bulk `get`/`drop` is limited just like telepaths** *(2026-09-02, observed; capture `stock-20260902-224515`)*. A Roomba sort sent a whole room's batch at once (26 gets) and tripped the stock limiter. **Every** command in the batch was dropped, and so was the movement command that followed it. That left the tracker Pending on a move the server never processed, which took the sweep down with it.
- **A flood also costs the *next* command, not just the flooded batch.** This collateral damage is the part to remember.
- **Every rate-limit line the game emits carries its own prompt** *(2026-09-03, observed)*. So the prompt alone is NOT sufficient as a rate signal. Gating purely on prompts speeds the client up in exactly the condition that should slow it down: the nudge arrives with a prompt, the prompt releases another command, and that command earns another nudge. This was observed as bursts of five nudges inside 200ms, repeating on every back-off.
- **Pace on a time floor, with the prompt as a gate on top of it, never as the sole trigger.**
- **This is not the outbound-write interleaving bug.** That was a client-side concurrency defect in `TelnetClient`, not a game rate limit.

**Client use:**
- Every telepath passes `TelepathPacer`'s 100 ms floor; bulk reply bursts (e.g. `@roomba sync`) go ~800 ms apart via `PacedReplySender`. See *Talk & chat channels → Telepath throttle and per-telepath acknowledgement*.
- Roomba releases `get`/`drop` at most one per wire prompt AND no faster than an 800 ms floor (`GhSweepManager.MinCommandInterval`). The game's own prompt acts as the meter, so no rate has to be guessed. Because the prompt alone is not sufficient, it is used as a gate on top of a time floor.

### Message catalogue (lines the client parses)
*Status: Unrated; the Thorns/ShockShield row is CONFIRMED 2026-08-15 (user)*

| Event | Line |
|---|---|
| Weapon equip / swap (one line) | `You are now holding <X>.` |
| Armor wear, empty slot (names no slot) | `You are now wearing <X>.` |
| Armor swap into an occupied slot (two lines) | `You have removed <old>.` then `You are now wearing <new>.` |
| Remove | `You have removed <X>.` |
| Already worn | `You do not have <X> left unequipped.` |
| Sneak armed (ACK) | `Attempting to sneak...` |
| Sneak soft-fail | `Attempting to sneak...You don't think you're sneaking.` |
| Sneak confirmed (room entry) | `Sneaking...` |
| Sneak lost (loud) | `You make a sound as you enter the room!` |
| Sneak blocked (hard) | `You may not sneak right now!` |
| Weapon ineffective | `Your weapon has no effect against this monster!` |
| Fists ineffective | `Your fists have no effect against this monster!` |
| Spell can't affect target (e.g. living-only vs NonLiving) | `Your spell has no effect on <monster>.` |
| Local player death (lives readout, slow / normal; see *Death & corpse recovery → Death lines & the miracle-save*) | `You now have N lives remaining.` |
| Local player death (DoT / no named killer; see *Death & corpse recovery → Death lines & the miracle-save*) | `You have been killed!` |
| Miracle-save lives readout (a death, still has lives; see *Death & corpse recovery → Death lines & the miracle-save*) | `You have N lives left.` |
| Local player slain (attacker named; see *Death & corpse recovery → Death lines & the miracle-save*) | `You have been slain by <killer>.` |
| Party member / other player killed in room (see *Death & corpse recovery → Death lines & the miracle-save*) | `<Name> has died.` |
| Character drops (0 HP, party/room-side; self sees own name) | `<Name> drops to the ground!` |
| Being dragged while dropped (dragged char's view, per move) | `<Leader> is dragging you around.` |
| Action attempted while dropped (rejection) | `You may not do that while you are mortally wounded!` |
| Coin pickup (no trailing period; see *Money, banks & shops → Coin wire wording*) | `You picked up N <coin>` (e.g. `6 silver nobles`) |
| Coin drop | `You dropped N <coin>.` |
| Coin stash / hide | `You hid N <coin>.` |
| Bank deposit (manual or auto; multi-currency, may wrap) | `You deposit 1 platinum piece, 93 gold crowns, ... copper farthings.` |
| Corpse loot drop (bare keyword) | `N <keyword> drop to the ground.` |
| Room cash survey | `You notice ... N <coin> ... here.` |
| Move refused — no exit | `There is no exit in that direction!` |
| Move refused — blocked way | `You can't go that way.` / `You can't move (in) that direction.` (the forms the client's refusal detector matches; an earlier note wrote `You can't move that way.`, never seen in a capture) |
| Move refused — shut door / gate (the client matches case-insensitively, `.` or `!`) | `The door is closed.` / `The door is Closed!` (captured forms); gate form `The gate is closed!` |
| Room too dark to see (starves name + exits + Also-here) | `The room is very dark - you can't see anything.` |
| Room considerably darker (same starving) | `The room is pitch black...` |
| Guard interposes for a guarded monster (no trailing period, no prefix) | `<guard> moves to protect <protected>` |
| Incoming mob attack — miss (dark cyan; reveals a mob in a dark room) | `The <monster> <verb> at you` |
| Incoming mob attack — hit (dark cyan; reveals a mob in a dark room) | `The <monster> <verb> you for N damage!` |
| Thorns / ShockShield reflect (**white** line, follows the **red** hit that triggered it, inside a *Combat Engaged*…*Combat Off* window) | `The <item-wording> stab <attacker> for N damage!` — see *Armour, defence & to-hit → Thorns / ShockShield reflect damage* |
| Monster leaves the room (e.g. dragged out by a fleeing player) | `<name> walks out of the room to <dir>.` **or** `<name> exits the room to <dir>.` — both confirmed; the "exits" form (no leading article) was the paradigm drag-out capture |
| Attacked a target not in the room (with talk slow on; with it off the words are spoken — see *Combat → Attacking a monster that isn't in the room*; also a no-op `break` on Paradigm) | `Your command had no effect.` |
| Toll exit unaffordable | `You do not have enough to cover the toll of N gold crowns.` |
| Train success — stock (carries the attained level; see *Character stats & progression → Trainers: level band, class restriction, and `train stats`*) | `You hand over <cost> and you receive training to attain level N.` |
| Train success — Paradigm/ParaMud (**level-less**; see *Character stats & progression → Trainers: level band, class restriction, and `train stats`*) | `You hand over <cost> to train to the next level!` — a successful train with **no level number**; mutually exclusive with the stock line above, so auto-train infers the new level as current+1 |
| Server PvP announcement (**Paradigm-only**) | `Server PvP Message: <body>` — realm-wide server broadcast for PvP events; the kill form is `Server PvP Message: <killer> just killed <victim>!`, but other PvP bodies share the same `Server PvP Message: ` prefix. Not emitted on stock realms |

### The `spells` / `sp` command output
*Status: CONFIRMED 2026-08-13 (user capture) · Realm: Paradigm*

- **`sp` is the accepted abbreviation of `spells`** and produces the identical listing of the character's obtained spells.
- **The format (mana classes) is an intro line, a padding-aligned column header, then one row per spell,** terminated by the prompt:

  ```
  You have the following spells:
  Level Mana Short Spell Name
     1    1  harm   harm
     1    2  mihe   minor healing
     2    4  bles   bless
     ...
  ```

- **The header's inter-column padding varies by class and realm, so match it whitespace-normalised,** not against a fixed single-space string. Kai classes render "Level Kai  Short …" with an extra space, and a realm's mana header can be padded differently again.
- **Each row is `Level Mana Short <Spell Name…>`.** The obtained set keys on the full Name, not the Short cast-code.
- **`You have no spells.` is the authoritative empty list.**
- **A parse that opens on the header but reads zero rows is a format miss, not an empty book.** It must not clear the obtained set.

**Client use:**
- SpellListParser. Driven by the report "sp didn't update spellbook".

### The `health` command output
*Status: CONFIRMED 2026-09-03 (user captures) · Realm: both*

- **`health` works on both realms** (stock + Paradigm). It prints a single compact line with the character's HP and power pool, each as current/max with a percent.
- **It is the cheap way to re-anchor the authoritative max HP/mana.** It scrolls far less than the full `stat` screen, which helps e.g. after a gear swap drifts the high-water mark.
- **The pool field's label depends on class, not realm.** It is `Mana` for a mana class and `Kai` for a Kai class (mystic), and the pool field is **absent entirely** for a class that has only HP:

  ```
  Health:    593/593  [100%]  Mana:  619/619  [100%]  (mana class → Mana)
  Health:      91/91  [100%]  Kai:  6/10  [60%]       (mystic → Kai)
  Health:    137/137  [100%]                          (HP-only class → no pool field)
  ```

- **The line is anchored at line start with `Health:`, and HP and the pool are both `cur/max` followed by `[pct%]`.** Inter-column padding varies, so match whitespace-tolerantly.
- **The two `Health:` labels collide.** Here `Health:` is the **HP pool**. The `stat` screen labels the HP pool `Hits:` and has its own separate `Health:` field, which is the **Health core stat** (a bare number like `Health: 71`, alongside Strength / Agility / etc.). So the health-command line is parsed on its own outbound gate (`health` observed) and never through the stat-screen field scan.
- **HP/MA regen (`HP Regen` / `MA Regen`) only appears in Paradigm's `stat all`, which the client does NOT parse.** Regen is computed from stats in the Player Workshop instead.

**Client use:**
- StatParser.TryHealthCommandLine, gated on an observed outbound `health`.

### Another player looking at you
*Status: CONFIRMED 2026-07-20 (user)*

- **When another player `look`s at us, the wire prints `<name> is looking at you.`** (`name` is a single first-name token).
- **The wording is user-supplied, not data-derived.** It is a live interaction line and is not present in any imported MDB table.

**Client use:**
- The reactive-look-back feature (Settings → Talk) keys on this exact phrase. If a realm's wording differs, it's a one-line regex tweak (`KnownPatterns.PlayerLooksAtYou`, regex registered in `DefaultPatterns`).

### BBS actions / emotes (the `action list` socials)
*Status: CONFIRMED 2026-08-14 (user + live capture)*

- **MajorMUD / MajorBBS boards ship a customizable action list** (MUD socials / emotes), shown by typing `action list`. It is a bare **space-separated list of verbs** (e.g. `hug kiss
  wave grin bow bleed nod laugh … smile … tickle`) that wraps across lines, with no header.
- **Using an action always produces a full GREEN line**: ANSI palette **index 2** (`SGR 0;32`), the same green the board paints the whole `Obvious exits:` line.
- **All-green is necessary but not sufficient**, because that exits line is all-green too. The board greens only the *label* of `Wealth:` / `Encumbrance:` / stat rows, not their values, so those lines aren't all-green. `You are carrying …` isn't green at all. Both fail the colour gate before any text test.
- **Own POV begins `You <verb…>`.** It is either non-targeted (`You growl.`; `tickle` with no target gives `You look around looking for someone to tickle.`) or aimed at a player (`You hug Suijin close!`, `You wave to Suijin!`).
- **The output wording does NOT follow the command verb**: `jump` → `You leap in the air!`, `egrin` → `You grin evilly.`. So there is no verb→output map to key on; colour plus head shape is the signal.
- **Target POV (aimed at you) is `<Player> <verb…> [at] you…`** (`Fujin hugs you close!`, `Fujin grins slyly at you.`).
- **3rd-party POV (you witness it) is `<Player> <verb…> [<other>]`.** A self-action reads the same to everyone in the room (`Fujin growls ominously.`).
- **Targeting varies per action.** Some are usable only at players, some also at monsters, some are self-only.
- **Actions are room-local.** You only see others' actions when they share your room, so the actor in an others'-POV line is always a **player in your room**.
- **`Your command had no effect.` follows an action used with no or an invalid target** (or one not usable there). That line is **not** green.

**Client use:**
- The Conversation window captures these as a **Social** channel entry, grouped under the room-local "say" filter (the say chip stays white, the message text renders green).
- Detection is all-green colour plus head shape. Own lines are `You <lowercase verb>…`, excluding the known green status prefixes (`You are/have/notice/feel/…`). Others' lines are `<known room player>
  <lowercase verb>…` minus enter/exit/follow/logon/chat lines (see `ActionEmoteClassifier`).

### Realm exit / logoff sequence
*Status: CONFIRMED 2026-09-05 (user + report `stock-20260904-230111`)*

- **You exit the realm from inside the game with the exit command** (on the user's board, a bare `x`).
- **`x` → "You will exit after a period of silent meditation." → a few seconds later "Your character has been saved."** At that point the character is safely out of the game. The trailing "leave comments in E-mail to Sysop" text is board-customised.
- **No Y/N confirm prompt fires on the exit path.**
- **Where you land after that depends on the board.** Some boards drop straight to MajorMUD's own entry menu (`[E] . Enter the Realm`). Others nest the realm under extra door/games menus, so a second `x` is needed to walk back out. Example: the door post-game screen with a `[MAJORMUD]:` prompt, then the BBS games menu `[M]...MajorMUD! …` with a `Fujin, your selection or ? for help:` prompt. The entry-menu row does NOT appear on the nested boards.
- **"Your character has been saved." is the board-agnostic "we're out of the realm" signal**, not the entry menu.

**Client use:**
- The cleanup-logoff orchestrator keys its carrier-drop on that line (`KnownPatterns.RealmExitSaved`). The entry-menu row and a wait-timeout remain secondary fallbacks.

### MegaMUD `messages.md` format
*Status: CONFIRMED 2026-08-17 (user + decode of both stock/paramud files)*

- **MegaMUD's "Messages/Responses" catalogue ships as a plain-text `messages.md`,** one per game-type folder: `…(Stock)/Default/messages.md`, `…(Paramud)/Default/MESSAGES.md`. It is the ORIGINAL source that our Messages seed (and Triggers seed) were derived from.
- **Every record is rigidly 3 lines, `\r\n`-terminated.** This was validated on 612 stock / 840 paramud records, with zero misalignment.
- **Line 1 is `name : FLAGS(4-hex) : ACTION(decimal) : RESPONSE`.**
  - `name` is the message name. **A spell's message is named exactly after the spell, and an item's after the item.** Paramud names item procs after the item (e.g. `acid slasher`), while stock used a generic effect name (e.g. `acid hits`). This name is how a message is linked to its spell/item.
  - `FLAGS` holds the Effects checkboxes as an **additive hex bitfield**, with the same bit layout as our `MessageFlags`: `0001` Blinded, `0002` Confused, `0004` Poisoned, `0008` Losing-HP, `0010` Movement-prevented, `0020` Attack-prevented, `0040` Diseased, `0080` HP-regen, `0100` Find-in-conversations*, `0200` Mana-regen, `0400` Find-in-text*, `0800` reserved*, `1000` Ends-combat, `2000` Last-action-failed, `4000` Use-when-chasing*, `8000` Disabled. (`*` = the find-mode/chasing bits the importer strips.) Validated end-to-end against message text.
  - `ACTION` is the Action radio, a top-down index 0–6: 0 Ignore, 1 Check-who's-in-room, 2 Wait-until-wears-off, 3 Rest-full-HP, 4 Rest-full-Mana, 5 Don't-rest-run, 6 Hangup. **Read but NOT emitted**: the client's own engines/settings handle every one of these, so a MudPlay message is recognition only and carries no action.
  - `RESPONSE` is the literal text that MegaMUD would send when it sees the line echoed from the server. **Read but NOT emitted**: in MudPlay, a player response to a seen line is a *Trigger*, not a message.
- **Line 2 is the Message line** (the pattern matched on the wire).
- **Line 3 is the Ends-with line (wear-off).** It is **blank when the effect has no wear-off**.
- **Tokens are `{dmg}` (numeric), `{target}`, `{source}`,** which `CasterMessageMatcher` already understands, plus `{1}` = a user-defined **wildcard** handled by the Triggers matcher.
- **The .md is the source for THREE of our tables, not just Messages:**
  - A record whose name matches a Spell goes to the Messages seed with `Spells#N`.
  - One that matches an Item goes to the Messages seed with `Items#N`.
  - One that carries an ailment flag / applied+wear-off goes to the Messages seed (ConditionTracker detector).
  - **Everything else (pattern + response/action) goes to the Triggers seed.** Examples: `1 life left` → say warning + hangup; the separate `X end` records → response `stat` to refresh status.
- **Decoding to our record:** if Ends-with is present OR there is an ailment flag, it becomes `AppliedMessage` (+`AppliedEndsWith`); otherwise it becomes `CasterMessage`.

---

## Talk & chat channels

How the game's talk modes and chat channels behave: say forms, the gang channel's speak verb, how the server echoes our own messages, and how telepaths are throttled and acknowledged.

### Talk modes are per-realm configuration
*Status: CONFIRMED*

- **Talk modes (say / talk-fast / slow) differ per realm.** That is game configuration, not a client bug.
- **The keyboard period is a say-precursor and stays unbindable.**

### Directed say vs undirected say
*Status: CONFIRMED 2026-08-27 (user)*

- **A directed say is `><name> <message>`** (`>` verb + name, no precursor). It says the message TO one person in the room, so in a crowded room they know it's aimed at them.
- **The undirected say is the say-precursor `.<message>`**, which is room-wide. It is distinct from the directed say.

**Client use:**
- The client answers a say-channel `@`-command with a directed say at the sender (`RemoteCommandManager.SendReply`).

### Self-echo on public / broadcast channels
*Status: CONFIRMED 2026-08-24 (user)*

- **On a public / broadcast channel (gangpath, gossip, auction, broadcast) the server echoes our own message back tagged with our character name**, e.g. `Raijin gangpaths: <msg>`. It does **NOT** use `You`.
- **Only the directed/room channels use the `You` form:** say → `You say "…"`, telepath echo → `--- Telepath sent to X ---` / `--- Telepath Sent to <Name> ---`. The client reads either casing (`ConversationTelepathOut`, registered in `DefaultPatterns`, is case-insensitive, as is `TelepathPacer`'s acknowledgement match). See *Talk & chat channels → Telepath throttle and per-telepath acknowledgement*.

**Client use:**
- In `RemoteCommandManager`, the null-/`You`-speaker self-echo guard can't catch a public-channel self-echo, so it must compare the speaker against our own name (`SelfNameProvider` → `PartyManager.LocalCharacterName`, given-name form). Otherwise our own gangpath'd `@`-command (e.g. `@timer sync`) is read back as an inbound command and bounces a denial at the whole gang.

### Gang channel speak verb (`bg`)
*Status: CONFIRMED 2026-08-04 (user)*

- **The gang-channel speak verb is `bg`** (broadcast-gang), with `gb` and the `broadg…`/`broadgang` long forms as equivalents.
- **`gang` is NOT a speak command** — sending `gang <msg>` does not reach the gang.

**Client use:**
- Anything we emit on the gangpath channel (remote `@`-command replies, level-up announces, party `bg @heal`) must use `bg`.
- The alias-collision table in `AliasEngine` already reserves `bg`/`gb`/`broadg…` as the gangpath forms.

### Telepath throttle and per-telepath acknowledgement
*Status: CONFIRMED 2026-09-24 (user; report `stock-20260924-011014`)*

- **Telepaths are throttled, and each one is acknowledged in send order.** Every telepath gets exactly one reply line, in the order they went out:
  - `--- Telepath Sent to <Name> ---` — delivered.
  - `--- Telepath Not Sent ---` — it didn't reach its target because of throttling.
- **Read the acks positionally.** Three telepaths fired in a burst answered by one "Sent to" and then two "Not Sent" means the first arrived and the 2nd and 3rd were dropped and must be resent.
- **Pacing telepaths (and party/game-entry chatter) about 100 ms apart avoids the throttle.**
- **The ack may be glued after the prompt:** `[HP=32/MA=27]:--- Telepath Sent to Raijin ---`.
- **The ack names the full player name even when the telepath used an abbreviation** (`/raij`).
- **Seen in report `stock-20260924-011014`**, where a party-join probe burst lost `@level` + `@version`.

---

## Character stats & progression

How a character earns and spends character points (CP), how exp needed per level is computed, where and how training works, and what each base stat contributes to derived combat/utility stats.

### Where CP comes from (CP gain per level)
*Status: CONFIRMED (user rule, verified against a live level-10 build)*

- **Level 1 grants the race's BaseCP pool** — **100** for the standard races (the `BaseCP` field on the race record; Kang = 100). This is character-creation seed, not a training gain.
- **Training to each level 2+ grants a step that rises every decade:**
  - Levels **2–10** → **10** CP each
  - Levels **11–20** → **15** each
  - Levels **21–30** → **20** each
  - Levels **31–40** → **25** each … (+5 CP per decade thereafter)
- **The step rises at the first level of each new decade (11, 21, 31…)**, so a decade *top* pays the lower rate: **level 10 grants 10** (not 15) and **level 20 grants 15** (not 20).
- **Per-level training gain formula (level ≥ 2):** `((level - 1) / 10) * 5 + 10` (integer division).
- **Total CP on reaching a level, spending none:** `BaseCP + Σ gains(2..level)`.
- **A level-10 character who spent nothing has 190 CP** (100 base + 9×10). Confirmed by a level-10 Kang who trained straight to 190 spent.

### What CP costs to spend
*Status: CONFIRMED · Realm: differs*

- **Raising a stat one point costs** `((currentStat - raceMin) / 10) + 1` CP — it escalates by 1 for every full 10 the stat sits above its race minimum.
- **ParaMUD/Paradigm is uncapped; Stock caps the per-point cost at 10.**
- **The in-game Point Cost Chart states the cumulative form:** +10 above base = 10 CP, +20 = 30, +30 = 60, +40 = 100, +50 = 150 CP (each is the running sum of the per-point costs above).
- **Worked example** — Kang (mSTR 55, mAGL 30, mHEA 50) at level 10 with 190 CP: STR 55→99 = 120 CP, AGI 30→60 = 60, HEA 50→60 = 10 → exactly 190. The **next** STR point (99→100) costs `(99-55)/10 + 1 = 5` CP, which 190 can't afford, so **99 is the real ceiling** at that level — a plan must not offer 100.

### Class/race exp modifier — the `ExpTable` field carries a −100 baseline
*Status: CONFIRMED 2026-09-23 (user + GreaterMUD Explorer screenshots, Paradigm 1.9.1) · Realm: Paradigm* *([NEEDS CONFIRMATION] the client applies the +100 baseline on Stock too — does Stock's table include it?)*

- **The class exp modifier a player reads (and MMUD / GreaterMUD Explorer shows) is `ExpTable + 100`**, not the raw MDB field: the stored `ExpTable` is the class's delta ABOVE the 100% baseline. E.g. Warrior `320` → **420%**, Thief `230` → **330%**, Paladin `490` → **590%** — a uniform +100 across every class.
- **The race exp modifier is added raw (no +100):** the game's exp-chart percentage is **`(classExpTable + 100) + raceExpTable`** (`ExperienceTableCalculator.CalcExpChart`), so the 100% baseline is counted once, on the class term.

**Client use:**
- The Game-Data **Classes** tab renders `ExpTable` as the modifier (`+100`, with a `%`) to match this; the raw field still drives search/sort.
- The **Races** tab still shows the raw `ExpTable` (its raw additive delta) — the Explorer Races-view display convention isn't yet confirmed.

### ParaMUD exp-needed curve
*Status: CONFIRMED 2026-09-14 (user) · Realm: Paradigm*

- **The realm runs ParaMUD 1.9.1 and will never run an older version again**, so the client targets the 1.9 curve only — there is no legacy table to preserve or version-gate.
- **Levels up to 26 use the per-level modifier table; 27-33 stay flat at 115%.**
- **Per-level growth tapers from level 34:** the 115% multiplier drops by one point every five levels, floored at 108%. Counting starts at 34, so the first drop lands at 39 and the floor is reached at 69 (115% for 34-38, 114% for 39-43, ... 108% from 69 up).
- **The pre-1.9 curve** (flat 115% through level 55, then 109%, then 108%) **overstates** exp needed for every level past 35 — up to roughly 22% around level 60 — which fed auto-train, level-up announcements, the Level Projection columns and TNL.
- **Stock has its own progression and is unaffected.**

**Client use:**
- Implemented in `ExperienceTableCalculator.CalcExpNeeded_ParaMud`.

### Trainers: level band, class restriction, and `train stats`
*Status: CONFIRMED 2026-07-10 (verified against the 1.11p Shops table) — level band; CONFIRMED 2026-09-22 (user) — `train stats` not level-gated*

- **Trainers carry a level band.** A training room is `Shops.ShopType == 8`; its `MinLVL` / `MaxLVL` fields are the **level range it can train** and `ClassRest` the single class it serves (a `Classes` row, `0` = any class).
- **The range is one contiguous band per shop** — the schema has no way to express a gap, so a trainer never splits into multiple bands.
- **The `MinLVL` / `MaxLVL` band gates `train` (level up) ONLY — NOT `train stats` (CP allocation).** Applying stat points works at **any** trainer that isn't class-restricted against you (a universal Training Room `ClassRest == 0`, or your own class's trainer), regardless of the trainer's level band. The `train` level-up path stays band-gated.
- **The train success line differs by realm** (also listed in *Wire, prompt & command output → Message catalogue (lines the client parses)*):
  - Stock carries the attained level: `You hand over <cost> and you receive training to attain level N.`
  - Paradigm/ParaMud is **level-less**: `You hand over <cost> to train to the next level!` — mutually exclusive with the stock line, so auto-train infers the new level as current+1.
- **A trainer can also stock items** (the Bard Training Room sells songsheets, the Thief Training Room lockpicks) — same 20-slot stock table as a merchant — so a training room is a trainer *and* a merchant at once, not either/or.

**Client use:**
- The auto-trainer's CP-only reconcile selects a trainer by **class only** (`TrainerCatalog.SelectNearestForStats`), never the level-band `SelectNearest` — a band-filtered pick would walk you across the map (or abort) to allocate stats you could apply right where you stand.

### The `train stats` screen (Char. Creation box)
*Status: OBSERVED (Paradigm capture) for the per-realm rendering; per-fact CONFIRMED tags inline*

- **The screen renders differently per realm.**
  - **Paradigm** draws a full-screen, cursor-positioned box titled **"Char. Creation"** with a side **"Point Cost Chart"** panel (the banner reads `P A R A D I G M`, not `MAJOR MUD`). Because it's cursor-positioned, the marker row never completes as a scrolled line until the screen tears down — so a marker-gated "menu entered" detector never fires mid-session on Paradigm; the realm-independent signal is the outbound `train stats` command itself.
  - **Stock** realms render the same screen inline (scrolling text), so the marker row is emitted normally.
- **Initial character creation reaches this box with NO outbound `train stats`** *([CONFIRMED] 2026-08-02, capture `paradigm-20260802-164301`)* — the menu walks class → race → alignment → training on its own. So on Paradigm neither signal is available during creation (no command to arm on, and the cursor-drawn marker row never emits inline), which leaves the client's arrow keys bound to command-history recall — up/down cycle the just-typed given/family names instead of moving between stat fields.
  - The only reliable entry signal is **scanning the live screen** for the box (the `Point Cost Chart` panel beside the `Char. Creation` / `Character Creation` title, both on the top row).
  - The banner text is literally abbreviated **`Char. Creation`** on Paradigm, not the stock `Character Creation`.
- **The stat box's first field is the "Family Name" (surname / last name)** *([CONFIRMED] — user report)*. The cursor starts there, and a plain Enter advances past it (the auto-trainer replays a bare Enter and never edits it). So any automated `<text>\r` that fires while the user is parked on the form types into that field: a stray party `par\r` poll overwrites the character's last name with "par".
- **The cursor-positioned box LINGERS on screen after the user exits back to the realm** *([CONFIRMED] 2026-08-05, captures `paradigm-20260805-095320/095546/095653`)*. It isn't cleared when the in-game prompt returns — it only leaves once enough new output scrolls it off. So the "box is on screen" signal stays true well after the session is over; the authoritative "user is back in the realm" signal is the **in-game prompt returning**, not the box vanishing.

**Client use:**
- Every wall-clock / on-demand automated wire send must gate on the realm-independent "screen owns the keyboard" signal (the outbound `train stats` command), not the marker — which on Paradigm never confirms.
- A screen-scan detector must arm on the box's *rising edge* only — a level-triggered re-arm flaps held→resume→held off the stale box every feed and holds the movement engine (the "walker stalls after training, moves one room per manual `rm`" bug).

### Base-stat → derived-stat contributions (overview)
*Status: Unrated (each derived-stat topic in this chapter carries its own tag)*

- **What each of the six base stats buys**, so a CP plan can target real breakpoints. These are the gear-free stat-and-level portion (item bonuses stack on top in-game).
- **Integer division truncates toward zero**, matching the engine.

**Client use:**
- Surfaced as the CP Allocation column tooltips and the Level Projection derived-stat columns; all values come from `StatEffects`, which composes the same `CombatCalculator` / `CharacterCalculator` the combat engine uses (no hand-copied numbers).

### Normal-attack accuracy — stat contribution
*Status: CONFIRMED (directly verified against syntax53/MMUD-Explorer `modMMudFunc.bas` `CalculateAccuracy` lines 2185–2225, where `bGreaterMUD`=Paradigm; matches `CombatCalculator.CalcAccuracy`) · Realm: both*

- **Realm-split, and further split by ATTACK TYPE on Paradigm.** The `bGreaterMUD` (Paradigm) branch even tags the STR/AGL contributions `*bash`/`*smash` in the tool's own breakdown.
- **Stock (all attacks):** `(STR-50)/3 + (AGL-50)/6` — STR ~3/pt, AGL ~6/pt. INT and CHM do **not** feed accuracy at all, for any attack type.
- **Paradigm normal attack:** `(AGL-50)/3 + (INT-50)/6 + (CHM-50)/10` — AGL ~3/pt, INT ~6/pt, CHM ~10/pt. STR does **not** feed a normal Paradigm attack.
- **Paradigm bash / smash:** `(STR-50)/3 + (AGL-50)/6` — STR ~3/pt, AGL ~6/pt (INT/CHM drop out). So STR reaches accuracy on Paradigm ONLY through bash/smash.

**Client use:**
- `StatEffects.BashAccuracyFromStats` computes the bash/smash form; the CP tooltip labels each accuracy line with the attacks it applies to.

### Dodge (raw value, pre vs-accuracy conversion)
*Status: CONFIRMED (`CombatCalculator.CalcDodge`)*

- **Formula:** `level/5 + (CHM-50)/5 + (AGL-50)/3` (+ gear, + an encumbrance bonus under 33% load). So AGL ~3/pt, CHM ~5/pt.

### Stealth base
*Status: CONFIRMED (MMUD-Explorer `CalculateStealth` (modMMudFunc.bas ~4620); stock also in `dll-stats-map.md` `0x5fa`) · Realm: both*

- **Formula:** `stat terms + stealthLvl + 20`, where `stealthLvl = level<16 ? level*2 : level+15`.
- **Per-point ratios are INT ~8/pt, AGL ~4/pt, CHM ~6/pt on BOTH realms, but the realms round differently** — Stock TRUNCATES each stat term (`Fix(AGL/4)+Fix(INT/8)+Fix(CHM/6)`) while Paradigm (`bGreaterMUD`) sums the stat contributions as a float and rounds ONCE (`Round(AGL/4 + INT/8 + CHM/6)`), so the two can differ by a point or two.
- **This corrects an earlier note that claimed the PNG showed stealth "identical" on Paradigm** — the PNG's granularity couldn't see the rounding difference; MMUD-Explorer's derivation makes it explicit.

**Client use:**
- `CalcStealthBase` is realm-split.

### Crit rating (base)
*Status: CONFIRMED Stock (via `dll-stats-map.md` (`0x710`)); **AGL term NOT verified for Paradigm** — stock formula used for both, flagged for confirmation*

- **Formula:** `clamp(level/10 + (INT-50)/10 + (AGL-50)/20 + (CHM-50)/30, 1, 75)`. So INT ~10/pt, AGL ~20/pt, CHM ~30/pt.

### Melee damage bonus (STR onto the weapon's own range)
*Status: CONFIRMED (GreaterMUD)*

- **Floored at 0:** min `(STR-100)/10`, max `(STR-50)/10`, never negative. So ~+1 min per 10 STR above 100, ~+1 max per 10 above 50.

### Max encumbrance (carry weight)
*Status: CONFIRMED Stock (via `dll-stats-map.md` (`0xb2`)); **NOT verified for Paradigm** — stock formula used for both, flagged for confirmation*

- **Formula:** `STR*48`, plus `STR*36 - 3600` once STR > 100 (steeper past 100). So +48/pt (more above 100).

### Magic resistance
*Status: CONFIRMED Stock (via `dll-stats-map.md`); **NOT verified for Paradigm** — stock formula used for both, flagged for confirmation*

- **Formula:** `(INT + 3*WIL)/4`. WIL is the heaviest term (~0.75/pt vs INT ~0.25/pt).

### Health (HEA) — max HP and HP regen
*Status: Unrated*

- **HEA feeds max HP and HP regen, both level-scaled** (see *Health, resting & recovery → Max-HP sources*).
- **Max-HP marginal per HEA point is `(1/2 + level/16)`** (the `HEA/2` + `(HEA-50)*level/16` terms of `CalcMaxHp`), so it rises nearly every point and steepens with level.
- **HP regen idle is `(level+20)*HEA/divisor`** (750 stock / 500 Para), tripled while resting.

**Client use:**
- The CP tooltip shows the current-value max-HP marginal and the next HEA that ticks regen up.

### Mana regen — one stat per class; max mana is not stat-driven
*Status: CONFIRMED (user + `CharacterCalculator.CalcManaRegen`)*

- **Matches the full passive-tick model in *Health, resting & recovery → Mana regeneration & the ManaRgn breakpoints*** (the 30 s tick, the `ManaRgn%` modifier and its breakpoints, realm forms); the stat-side view follows.

- **Mana regen scales off ONE stat per class:** magery type 1 = INT (Mage), 2 = WIL (Priest), 3 = (INT+WIL)/2 (Druid), 4 = CHM (Bard), 5 = fixed Kai rate (Mystic).
- **Core formula:** `((level+20)*stat*(mageryLevel+2))/1650`.
- **Maximum mana is NOT stat-driven** — it's `mageryLevel*level*2 + 6` — so a caster raises mana *regen* by training its casting stat, never max mana.

**Client use:**
- The CP tooltip lists mana regen only under the character's actual casting stat(s).

### Spellcasting skill (spellLvl, 0x604)
*Status: CONFIRMED Stock (RE'd DLL `dll-stats-map.md`, verified asm 0x41aae0); Paradigm unverified*

- **Formula:** `Level*2 + manaStat + mageryLevel*5 + spellcastingAbility(70)` — 70 is the +spellcasting ability code (gear/innate).
- **The blended `manaStat` differs from the mana-regen stat:** type 1 = (3*Int+Wil)/6, 2 = (3*Wil+Int)/6, 3 = (Int+Wil)/3, 4 = (3*Chm+Wil)/6. So for a Priest each WIL point is ~+0.5 spellcasting (+1 per 2).
- **Non-casters / Mystics have no standard spellcasting skill.**

**Client use:**
- `CharacterCalculator.CalcSpellcasting`; the CP tooltip shows it under the casting stat(s) with the exact next breakpoint.

### Utility skills — Perception + the thief four
*Status: CONFIRMED Stock (RE'd DLL `calculate_secondary_stats` @ `0x41a424`, offsets `0x5f8` / `0x5fe` / `0x606` / `0x60a` / `0x60c`); Paradigm unverified*

- **All five are pure integer divisions of stats plus a shared level term**, so points that don't complete a division buy nothing:

| Skill | Formula | Per-point |
|---|---|---|
| Perception (`0x5f8`) | `(INT*5 + WIL*2 + CHM)/8` | INT ~1.6/pt, WIL ~0.6, CHM ~0.3 — **no level term** |
| Thievery (`0x5fe`) | `(AGL + INT + CHM + lvlTerm*24)/6` | AGL / INT / CHM ~6/pt each |
| Traps (`0x606`) | `(INT + AGL + CHM*2 + lvlTerm*28)/7` | CHM ~4/pt (weighted **double**), INT / AGL ~7/pt |
| Picklocks (`0x60a`) | `((AGL + INT + lvlTerm*10)*2)/7` | AGL / INT ~4/pt (÷3.5 effective) |
| Tracking (`0x60c`) | `(INT*2 + WIL + CHM + lvlTerm*40)/8` | INT ~4/pt, WIL / CHM ~8/pt |

- **`lvlTerm = level<16 ? level : 15 + (level-15)/2` — the level slope halves at 16** for all four thief skills (Stealth halves at the same level via its own `stealthLvl`). So they grow fast to 16 and half as fast after; past the knee the CP case for INT / AGL / CHM on these is what carries them.
- **Perception is the only one every class carries;** the other four exist only for a class or race that was granted the skill (ability codes 39 Thievery, 40/41 Traps, 37/180 Picklocks, 38 Tracking, plus the custom/ParaMUD `1001`–`1004` `Grant*` variants).
- **The `+skill` gear abilities stack on top of these bases.**

**Client use:**
- `CharacterCalculator.CalcPerception` / `…Thievery` / `…Traps` / `…Picklocks` / `…Tracking`.

### Formulas deliberately NOT adopted
*Status: 2026-09-13, user decision*

- **A second reverse-engineering write-up of `wccmmud.dll` v1.11p** corroborated stealth, magic resist, crit, max HP, mana, carry capacity and the five skills above exactly, but disagreed on three points. All three were reviewed and **left as shipped** — don't re-open them without a live capture:
  - **Min melee damage.** That write-up reads `((STR-100)/10)*2` (citing `ADD EAX,EAX` @ `0x42AD4D`); we ship `(STR-100)/10`, matching the community chart and GreaterMUD.
  - **Dodge.** It gives `(AGL-50)/3 + (CHM-50)/5` with no level term; we keep `level/5` as well.
  - **Spellcasting.** It reads a trailing `+= mageryLevel` (so `mageryLevel*6`); we keep `mageryLevel*5`.

### Realm differences in stat derivation & Paradigm-verification summary
*Status: CONFIRMED 2026-09-10 (inspected syntax53/MMUD-Explorer `modMMudFunc.bas`) for the realm-difference note*

- **MMUD-Explorer reads, not derives, most derived stats:** it reads crit / encumbrance / magic-resist / spellcasting / mana-regen / HP straight from the pasted character (`tCharStats.nCrit`, `.nEncumMax`, `.nMagicRes`, `.nSpellcasting`, …) — it does NOT derive them from primary stats, so it provides no independent Paradigm derivation to compare against. Most of the stat→derived formulas here are the RE'd stock ones.
- **Known realm differences live in how these get *applied* in combat** (MMUD-Explorer's `bGreaterMUD` branches: accuracy weighting, dodge-vs-accuracy curve, spell-damage multiplier, resist application) and in the three stat derivations that already realm-split in code — **normal-attack accuracy** (Stock STR+AGL vs Paradigm AGL+INT+CHM), **stealth rounding** (Stock truncates each term, Paradigm rounds once — see *Stealth base*, `CalcStealthBase`) and **HP-regen divisor** (750 stock / 500 Paradigm). Those three the CP tooltip already reflects per realm; the rest use the stock derivation for both, flagged for confirmation.
- **Realm-verified:** accuracy (both realms, incl. the MMUD-Explorer Paradigm branch), dodge, stealth (MMUD-Explorer-verified — realms differ by a rounding step, not identical), and melee damage.
- **Unverified on Paradigm:** **crit's AGL term, encumbrance, and magic resistance use the stock formula for Paradigm as well and are unverified there** — treat as close-but-unconfirmed until a Paradigm source or capture pins them.

---

## Armour, defence & to-hit

How Armour Class and damage resistance are stored and displayed, which sources add to a character's defence, and the realm-dependent limits on a monster's chance to hit (to-hit floor, dodge caps), plus the Thorns/ShockShield damage a worn item reflects back at an attacker.

### Armour Class (AC) — stored at 10×, displayed to the tenth, floored for combat
*Status: CONFIRMED 2026-09-07 (user + source: syntax53/MMUD-Explorer); CONFIRMED 2026-09-04 (user)*

- **Item AC is stored at ten times the AC it grants.** Item `ArmourClass` raw 101 → +10.1 AC; `ArmourClass 615` = +61.5 AC. Same convention as `DR`. A character's summed AC therefore carries tenths.
- **AC is a fractional value on screen — 10.1 is a valid, correct AC.** MMUD-Explorer displays every item and the character total as **`raw / 10` to one decimal** (`Round(sum, 1)` for the character stat). Show the summed worn+buff AC to the tenth, not floored.
- **The to-hit formula uses a WHOLE-number AC, not the fraction.** MMUD-Explorer's monster-attack sim holds `m_nUserAC As Long` and is fed `Round(displayedAC)`. The incoming-hit chance is `Round(1 − (AC²/100) / (acc²/140), 2) × 100` = `100 − AC²/(acc²/140)` (our `CombatCalculator` port matches this shape).
- **The real game FLOORS (truncates) the fractional AC — it does not round.** MMUD-Explorer *rounds* the fractional AC to a whole number, but the real game floors it. User-confirmed in-game: gear +61.5 → the game uses **61**; rounding to 62 over-stated hit odds by 1. The integer AC the game uses for to-hit — the value the `stat` screen reports, and what Character Info's "Projected AC" reflects — is the **floor** of the fractional total, **not** a round-half-up. A projected **61.5** is an in-game AC of **61**; rounding the half up over-states AC by 1.
- **Display precise, combat whole.** Display the tenths, but the estimate consumes `floor(AC)`. The label may read `10.1` while the Hits-You-% is computed on `10`; that's faithful to the game, not a bug.
- **Buff/shadow/prot AC are integers,** so they don't affect the fractional part.

**Client use:**
- The client floors the AC into the hit-chance math.
- Monster Intel's Hits-You-% sim was seeding 62 (round-half-up); `IncomingHitEstimator` truncates now.

### `DR` (ability code 7) magnitude — stored at 10×
*Status: CONFIRMED 2026-08-30 (user)*

- **The `DR` ability's stored value is ten times the damage-resistance actually gained.** Raw **10 → +1.0** DR, raw **22 → +2.2**, raw **15 → +1.5**.
- **Display it as `raw / 10` to the tenth, never the raw store value.** A spell/effect showing "DR +10" really grants +1.0.

**Client use:**
- Applied in `SpellEffectFormatter` (the effect line) and the spell Game Data view.
- Worn-DR on gear via the equipment stat path is a separate display not yet audited against this.

### Armour Class contributions — shadow, Prot-Evil, Prot-Good, VileWard
*Status: CONFIRMED 2026-07-18 (user); realm-exclusivity CONFIRMED 2026-09-07 (user + syntax53/MMUD-Explorer + Paradigm-1.9.1 item data)*

Sources that feed a character's effective AC beyond the item/race/class/quest `+AC` (ability code 2 / blur 10) totals:

- **Shadow property (ability code 9) — a flat +10 AC that stacks only once,** no matter how many sources carry it. Ten shadow items still grant a single +10, not +100. Note: the client's `AbilityNames`/stat map currently labels code 9 "Shadow Resist" and accumulates its raw `AbilVal`; the *AC effect* is the flat +10-once, computed separately from that raw sum.
- **Prot-Evil / PREV (ability code 24) — 1 AC per point, but ONLY versus evil monsters** (the majority of monsters). Because it's conditional, it is surfaced as its own "+N vs evil" line rather than folded into a flat AC total.
- **Prot-Good / PRGD (ability code 25, granted by spell #108 "protection from good") — 1 AC per point, but ONLY versus GOOD monsters, and STOCK realms only** (see the ProtGood vs VileWard realm-exclusivity bullet in this topic). The mirror of Prot-Evil.
- **VileWard (ability code 1113) — a Paradigm-only AC bonus vs evil monsters whose magnitude scales with the wearer's own evil.** Confirmed formula (MMUD-Explorer `CalculateAttackDefense`): when the target is evil, `wearer-evil ≤ Seedy → 0`, `≤ Criminal → halved`, then **`÷10`** and added to secondary defense.
- **ProtGood vs VileWard are realm-EXCLUSIVE.** The ability *numbering* is shared across realms (25 = ProtGood, 1113 = VileWard in both — `bGreaterMUD` changes behavior, not numbers), but **Stock uses ProtGood and Paradigm uses VileWard**. Paradigm **dropped ProtGood**: its server does not honor ability 25, and its gear carries none (all moved to VileWard 1113). A stray ability-25 value on Paradigm must never add to defense.

**Client use:**
- VileWard is applied exactly per the formula above (`CombatCalculator.AdjustVileWard`), gated to ParaMud.
- The client counts **ProtGood only on Stock** and **VileWard only on Paradigm**.
- Monster Intel shows the Prot Good what-if field on Stock and the Vile Ward field (+ evil-tier picker) on Paradigm.

### Blur AC (ability code 10) — encumbrance-scaled, NOT flat
*Status: CONFIRMED 2026-08-08 (user)*

- **Blur AC is fundamentally different from flat worn AC (code 2).** Ability code **10**, the item field shown as "AC Blur" — its effective value **scales inversely with carried encumbrance**.
- **Full value at 0% load, 0 at 100% (heavy), linear in between.** A "AC Blur 12" item gives 12 AC when unburdened and nothing when maxed out — it linearly interpolates between.

**Client use:**
- Blur is surfaced **as its own "AC Blur" line/column**, never merged into the flat "Armour Class" figure (the Item Finder, trial-set readout, and Equipment Manager all split it out).
- Internally the aggregate `PlusAC` still carries the nominal blur value for the combat/projected formulas — the split is a **display** distinction.
- The finder shows the nominal (max) value, not an encumbrance-adjusted one, since it's a planning aid without a fixed load assumption.

### To-hit floor — the minimum chance a monster can ever land, by realm and armour type
*Status: CONFIRMED 2026-09-06 (MMUD-Explorer `modMMudFunc.bas` `CalculateAttackDefense`) · Realm: differs*

- **A physical hit chance never reaches 0%.** No matter how high a defender's AC/Dodge climbs, an attacker's chance to land a physical hit is **clamped to a floor**. The floor is realm-dependent, and on ParaMUD it also depends on the **defender's class armour type**.
- **Stock: 8%.** Flat, regardless of armour type.
- **ParaMUD: 2% normally, dropping to 1% when the defender's class `ArmourType` is 1..6** — the light-armour tiers **Silk (1), Ninja (2), Leather (3–6)**. Heavier classes — **Chainmail (7), Scalemail (8), Platemail (9)** — and **Natural (0)** stay at the 2% floor.
- **`ArmourType` is a per-class field (Classes table),** so it's the *character's* class armour tier that lowers the floor, not the gear currently worn. Value→name map (LookupEnums): 0 = Natural, 1 = Silk, 2 = Ninja, 3–6 = Leather, 7 = Chainmail, 8 = Scalemail, 9 = Platemail.
- **Sibling dodge caps (also in `CombatCalculator`):** Stock hard-caps dodge at **95%**; ParaMUD applies a **soft cap at 55%** (diminishing returns above it) then a **hard cap at 98%**.

**Client use:**
- Mirrors `CombatCalculator.GetHitMin`: ParaMUD base `PARAMUD_HIT_MIN = 2`, minus 1 when `ArmourType` is in 1..6; Stock `STOCK_HIT_MIN = 8`.
- This is why Monster Intel's Hits-You-% column can read **1%** for a light-armour ParaMUD class and its filter dropdown grows a leading `≤1%` band there — the estimator threads the class `ArmourType` into the hit-chance calc so the shown number matches the engine's real minimum (PR #503, v3.52.7).

### Thorns / ShockShield reflect damage
*Status: CONFIRMED 2026-08-15 (user)*

- **Wire line:** `The <item-wording> stab <attacker> for N damage!` — a **white** line that follows the **red** hit that triggered it, inside a *Combat Engaged*…*Combat Off* window.
- **A worn item with the ShockShield property strikes the attacker BACK** when the wearer is hit **physically**. The property's value is the max reflect damage (e.g. 5); the reflect does up to that much.
- **It fires after an armor-block glance (0 damage to us) OR a real hit.**
- **The item wording varies** (`armour spikes`, `collar spikes`, …), so the line can't be keyed on wording.
- **The monster is the victim**, so the damage is OURS (or a party member's), not a monster hit.

**Client use:**
- Recognized by COLOUR (white/default, vs the red of a real incoming hit) + `for N damage` + a non-`you` target — NOT by wording.
- Classified `Reflect`, not a monster hit.

---

## Health, resting & recovery

How HP works from full health down through dropping and death, how monster health reads via `look`, what does and doesn't let you rest, and how mana regenerates (including rerolling mana-regen roll spells).

### Max-HP sources
*Status: CONFIRMED*

- **Class sets the base health rolls; race adjusts them; level scales within those bounds.** The same class differs by race, and max HP climbs with level between the race/class-determined floor and ceiling.
- **The Health stat scales HP regen and also feeds max HP.** Higher Health means faster natural recovery (a regen rate on a scale), and it adds to max HP too — `CharacterCalculator.CalcMaxHp` uses `health/2 + (health-50)*level/16` (see *Character stats & progression → Health (HEA) — max HP and HP regen*).

### Positive HP — fully functional
*Status: CONFIRMED*

- **At any positive HP (1 … max) the character keeps every normal action.** HP level alone imposes no restriction.
- **Ailments are a separate axis.** Status afflictions can still block actions (e.g. a *held* status stops movement server-side), but that is independent of HP.

### 0 HP — dropped / bleeding out
*Status: CONFIRMED*

- **Hitting 0 HP drops the character.** They can **no longer move on their own**, and can **no longer fight or cast spells**. A dropped character is out of the action entirely, not merely immobile.
- **A drop means bleeding out.** Left unreversed, HP keeps trending toward the BBS death threshold (see *Death & corpse recovery → Death threshold & consequences*).
- **Dropping removes you from the party** — see *Party → Dropping (0 HP) or instant death removes you from the party* and *Party → Dropped ally rescue*.
- **The game rejects every action command while dropped / mortally wounded** — except `sys …` commands (see *Sysop commands → Sysop power gating*). Movement, casting, aiding and telepaths all bounce with `You may not do that while you are mortally wounded!`, `Your command had no effect.`, or (for remote / telepath commands) `{command invalid or not allowed}`.
- **Two reversals bring a dropped character back into the positive:**
  - **another player** issues `aid <name>` on them, or
  - a **healing spell** lifts their HP above 0.
- **Any player can `drag <name>` a dropped character** *([CONFIRMED] 2026-09-26, user)*. The dropped character then **follows wherever the dragging player moves** — their only way out of the room until aided or healed. In a party the client leaves it to the leader (see *Party → Dropped ally rescue*).
- **A dropped character can still hang up.** Dropping blocks in-realm *actions* (move / fight / cast), but the **carrier drop / main-menu exit** (the Game-Exit command, e.g. `=x` / `;o`; see *Wire, prompt & command output → Realm exit / logoff sequence*) **still goes through at 0 HP or below** *([NEEDS CONFIRMATION] is it the BBS-level =x that works at 0 HP?)*. So the emergency-hangup escape stays available all the way through the bleeding-out window.
- **HP percentage goes negative while bleeding out.** HP% is a plain `hp / maxHp` ratio with no clamp at zero, so a dropped character reads a **negative percentage**. Where `par` still lists the member it shows it as such (e.g. a member driven to −12/200 HP reads a negative HP%) — but **by realm** (2026-09-26, user): on **Stock** a dropped member is removed from the party at once, so `par` never shows them; on **Paradigm** `par` is believed to show the negative HP% *([NEEDS CONFIRMATION])* — see *Party → Dropping (0 HP) or instant death removes you from the party*. A percentage-based threshold is therefore a continuous scale from 100 % down through 0 % into the negatives, exactly like an absolute-HP scale.
- **Client use:**
  - The client's low-HP auto-hangup fires down to (but not past) the BBS death floor, giving a dropped-but-not-yet-dead character a last chance to disconnect before dying. Its "hang up if below" trigger can be set anywhere on the HP% scale, including negative.
  - Client engines that keep firing commands while dropped accomplish nothing but noise — a dropped / mortally-wounded local player must suppress engine command output until healed / aided.

### Poison prevents resting
*Status: CONFIRMED 2026-08-17 (user; report `paradigm-20260817-092945`)*

- **While poisoned you cannot rest.** A `rest` (or meditate) issued while poisoned does **not** put you into the `(Resting)` state — poison refuses / breaks it — so the position never becomes Resting and the resting recovery doesn't happen; you only get the slow standing regen.
- **Client use:**
  - An optimistic "resting" latch armed on the send (`HealthManager._restInFlight`) never confirms while poisoned, and the interruption latch can't clear it (it needs a confirmed Resting first). So the auto-rest engine must **re-attempt the rest once poison clears** (the poison falling edge drops the stale latch) — otherwise it sits standing below the rest floor forever, which is what report 092945 hit.

### Casting a spell interrupts resting / meditating
*Status: CONFIRMED 2026-08-20 (user)*

- **Casting a spell on yourself (a self-bless, etc.) while resting or meditating breaks the rest/meditate state.** Position drops back to Standing, the same as taking a hit or moving. Reported as "meditate not re-engaging automatically after blessing while resting."
- **Client use:**
  - `HealthManager`'s confirm/interrupt latch (`_restInFlight` / `_restConfirmedByPrompt`) only recognized `PlayerPosition.Resting`, never `PlayerPosition.Meditating`. So a `meditate` send's confirmation step never fired, the interruption step's guard never tripped either, and the latch stuck `true` forever after a meditate got interrupted in place (no room move to fall back on and clear it via `NoteRoomChanged`).
  - Fixed by treating Resting and Meditating as the same "in a resting-family position" state for the confirm/interrupt check. `rest` was never affected, since its position always matched.

### ShadowRest
*Status: CONFIRMED (user) · Realm: Paradigm (not present in stock)*

- **Some Paradigm classes have a ShadowRest class ability; it is not a stock MajorMUD mechanic.** In the imported game data it is **class-ability code 1103** on the Classes table (`AbilityNames` maps `1103 → "ShadowRest"`); a class row carrying that code in any `Abil-N` slot has the ability.
- **While hidden or sneaking, the character can `rest` (or meditate) and stay stealthed while resting in the room.** Monsters in the room **do not attack** the resting stealthed character. Normally a hostile in the room means you can't safely rest; ShadowRest lets a stealthed character rest right there without being engaged.
- **Some ShadowRest classes gain an HP-regen bonus while resting this way** (e.g. thief gets extra regen). The bonus is server-side.
- **No special messages mark the state.** The only observable sequence is a successful hide/sneak followed by `rest` — there is no "you shadow-rest" line.
- **Ideally used solo.** Resting while hidden un-targets you from party single-target heals/buffs (the same reason auto-hide is party-suppressed), so ShadowRest resting is a solo behavior.
- **Client use:**
  - The client's `RegenTracker` measures the actual regen rate off the stat line, so it needs no separate model of the bonus magnitude.
  - The client can't detect ShadowRest from the stream; it gates on the **class ability (code 1103) + the user setting** instead.

### Mana regeneration & the ManaRgn breakpoints
*Status: CONFIRMED 2026-08-08 (against the engine's own reference formula) · Realm: both (stock and Paradigm / GreaterMUD forms below)*

- **Passive (non-resting, non-meditating) mana regen ticks every 30 s (6 rounds)** and adds a whole-MP amount computed by one integer formula:

```
S (base stat) by magery type:  mage = INT ·  priest = WIL ·  druid = (INT + WIL) / 2 ·  bard = CHM ·  mystic = fixed 1
base = trunc( (level + 20) · S · (mageryLevel + 2) / 1650 )
tick = trunc( (ManaRgn% + 100) · base / 100 )        [stock]
tick = base + trunc( ManaRgn% · base / 100 )          [Paradigm / GreaterMUD — functionally equivalent for ManaRgn% ≥ 0; for a negative total they differ by 1, e.g. R=−31, b=5 → 3 on stock, 4 on Paradigm]
```

- **`mageryLevel` is the class's magery tier (`Classes.MageryLVL`), a constant per class — NOT the character level.** It also drives max mana: `MaxMana = (mageryLevel · level · 2) + 6`. (A stock ability code 145 = "ManaRgn".)
- **`ManaRgn%` is the sum of every code-145 source.** That is gear/quest `addability 145 N` bonuses (`+N ManaRgn`) **plus** a cast mana-regen roll spell's rolled magnitude. It is a **percent modifier on the tick, not flat mana**.
- **Breakpoints are emergent, not a table.** Because `tick` is truncated, it steps up by 1 MP only when `ManaRgn%` (or level / stat) crosses the integer threshold `(N·100/base − 100)`. Between thresholds, extra ManaRgn% does nothing.
- **Roll spells (nature tap / mana flux) carry a code-145 slot with stored value 0.** The magnitude is rolled per cast from the level-scaled range `Min/Max = base + trunc(inc/incLVLs · level)` (the same `SpellCalculator.AffectMagnitude` scaling every affect uses). So the worst roll = Min, best = Max, and the rolled value adds straight into `ManaRgn%`. This is why a reroll only helps when the range can cross a truncation breakpoint at the current level — otherwise it just burns mana.
- **Unverified / modelled (not from the engine reference, don't hard-depend):**
  - the roll's *distribution* across `[Min,Max]` is treated as linear for "where on the range" purposes but is not proven uniform;
  - the meditate path (10 s tick, ManaRgn% excluded) is a reverse-engineered model;
  - whether the live engine caps summed ManaRgn% is unknown.
- **Client use:**
  - `CharacterCalculator.CalcManaRegen` implements the formula; the Level Projection grid already relies on it.
  - The mana-regen breakpoint calculator uses only the CONFIRMED passive formula above.

### Rerolling a mana-regen roll spell
*Status: per-fact tags below (CONFIRMED 2026-08-28 user + screenshot; 2026-08-30 report `paradigm-20260830-110918`; 2026-09-09 user) · Realm: both (reading the roll differs by realm)*

- **Only roll spells are reroll candidates** *([CONFIRMED] 2026-08-28, user + screenshot)*. That means nature tap / mana flux / `prfl`, and kin — code-145 with stored value 0. A bad roll drags `ManaRgn%` down, so re-cast to chase a better one.
- **`CHSU` (chaos surge) is NEVER rerolled.** It's a *constant* mana heal-over-time (the mana analogue of HP regen), not a rolled `ManaRgn%` modifier; maintain it like a normal buff. A fixed (non-zero) code-145 spell isn't rerolled either. (`RegenSpellClassifier` already splits roll / fixed / HoT.)
- **Mana-regen roll spells are self-only casts** *([CONFIRMED] 2026-09-09, user)*. Mana flux and its kin **cannot be cast on others**; the caster always receives the roll whenever the slot fires.
- **The rolled value can be negative** *([CONFIRMED] 2026-08-30, report `paradigm-20260830-110918` — mana flux rolled `spells: -31`)*. So a "reroll below 0" gate is a normal setting: it chases any non-negative roll.
- **A roll spell confirms via the shared `ManaRegenerating` condition, not a spell-specific applied message** *([CONFIRMED] 2026-08-30, report `paradigm-20260830-110918`)*. So the applied-line path can't map the landing back to the specific spell (#406).
- **Reading the roll — Paradigm:** send **`abil 145`**. The output is three lines for `ManaRegen(145)`:

  ```
  granted: N      (innate)
  worn:    N      (gear/quest +ManaRgn bonuses)
  spells:  N      (what the mana-regen spell ROLLED — this is the value we gate on)
  ```

  The **`spells:` value** is the rolled magnitude (e.g. a priest's `prfl` rolling 32). Reroll if `spells:` < the min gate.
- **Reading the roll — Stock:** there is **no `abil 145`**. Instead, monitor the actual **mana tick**: passive regen lands one tick every ~30 s, so watch the MP jump to measure the realised per-tick amount. Because the tick formula is known, compute the possible tick range at the current level — worst = tick with the Min roll, best = tick with the Max roll (using worn `ManaRgn%`) — and surface both so the user sets a min-tick threshold on a **0–100 % scale** between worst and best. After a cast, wait for the next tick (~30 s), and if the observed tick is below threshold, recast (up to max); otherwise let it ride.
- **Client use:**
  - **Config per roll-spell slot:** a **max rerolls per cycle** and a **minimum gate**. Cast → read the roll → if below the gate, re-queue and repeat until at/above the gate or max attempts hit; then stop and wait for the spell's normal recast-within window before trying the cycle again.
  - **Self-only matching:** the reroll engine must NOT gate on a "cast on self" flag (a user can leave a slot's default whole-party flag on; the spell still only lands on the caster). `ManaRegenRerollSlot` therefore matches any configured roll-spell slot regardless of its target flags.
  - **Trigger timing:** the reroll cycle keys off the **cast** (we know what we cast), not the applied-line confirm: after the cast reaches the wire, send `abil 145` and read the fresh roll. Keying it on the confirm meant it never fired at all.
  - **Running out of mana mid-cycle PAUSES, it does not surrender** *(2026-09-01, report `paradigm-20260901-114223`: the reroller quit at 3/20 when a recast would breach the mana floor, stranding the spell at a bad roll)*. Each recast costs mana, so if the next one would drop under the buff mana floor the cycle SUSPENDS with its reroll counter intact and resumes the next attempt once meditation lifts mana back over the floor — so it spends its full reroll budget across rest instead of abandoning a bad roll at the floor.
  - **Built — Paradigm:** `ManaRegenReroller` sends `abil 145`, parses the `spells:` slice, rerolls below the per-slot threshold up to its cap.
  - **Built — Stock:** the Stock tick-monitor path in `ManaRegenReroller` judges the roll from the observed passive tick.

### Looking at a monster — coarse wound bands
*Status: CONFIRMED*

- **`look <monster>` reveals a wound band, never a number.** The game only ever states the condition as one of **eight coarse wound bands**.
- **Monster look vs player look.** A player look prints a bracketed `[ Name ]` header and ends `He is unwounded.`; a **monster** look has **no header** — the monster's name is the first response line, prose follows, and the **last line** is `(It|He|She) appears to be <wound>.`. The `appears to be` phrasing is monster-exclusive (players read `is unwounded`), so it never false-matches a player look. The server echoes the typed command as its own content line (`look ca`) ahead of the name.
- **Each band is a fixed percentage window of the monster's max HP** (from game data), so `max HP × band` gives an absolute HP range. Validated live: a **70-HP cave worm** reading **heavily wounded** was **35–48 HP** (actual 38). Bands, percentage of max HP, lower bound inclusive:

  | Descriptor | % of max HP | 70-HP cave worm |
  |---|---|---|
  | unwounded | = 100 (full) | 70 |
  | slightly wounded | [85, 100) | 60–69 |
  | moderately wounded | [70, 85) | 49–59 |
  | heavily wounded | [50, 70) | 35–48 |
  | severely wounded | [30, 50) | 21–34 |
  | critically wounded | [20, 30) | 14–20 |
  | very critically wounded | (0, 20) | 1–13 |
  | mortally wounded | ≤ 0 (dead/dying) | ≤0 |

- **Band → HP range.** For a band `[lo, hi)`: `Low = ceil(lo·M/100)`, `High = ceil(hi·M/100) − 1` — exactly the integer HP values that read as that band.
- **Why the range is worth having.** Against a **high-HP boss with fast regen / self-heal**, the per-round scroll outpaces any attempt to tally HP by counting damage lines, so the wound band is the only reliable read of where the boss's "HP gate" sits.
- **Client use:**
  - Implemented in `MonsterLookParser` → status-bar `Target: min-max`.
  - Name→HP resolution goes through `RoomEntityClassifier.ResolveLookedMonsterNumber`, which prefers the monster variant actually in the room so shared names / adjective prefixes resolve to the right HP.

---

## Combat

How a fight runs on the wire: announcing and repeating attacks, what breaks combat, the attack-spell cascade, weapons and backstab, how monsters decide whom to attack, and how kills are recognised and attributed. Also covers the client's per-monster auto-combat overrides.

### Attack announce lines and round commits
*Status: CONFIRMED (user); room-spell and poised lines CONFIRMED 2026-09-21 (user)*

- **Any normal attack against an NPC is announced publicly.** The attacker sees `*Combat Engaged*`; everyone else in the room sees `<player> moves to attack <target>.` (the client's `moves to attack` regex requires the trailing period).
- **A backstab round is silent.** It emits no `moves to attack` line to other players, so the surprise opener doesn't tip off onlookers. The client therefore can't confirm its own backstab landed from a `moves to attack` echo (there won't be one); it uses the `surprise` swing line instead (see *Backstab*).
- **A round action is announced by ONE of two lines, melee OR spell.** A party member has "gone" for the round when the room shows *either* `<player> moves to attack <target>.` (melee/ranged) *or* `<player> moves to cast <spell name> upon <target>.` (a combat spell). Both count as that member's announce; a caster's turn produces the second form only.
- *([CONFIRMED] 2026-09-21, user)* **A ROOM spell announces as `<player> moves to attack everyone in the room.`** — the melee "moves to attack" form with the wildcard target `everyone in the room`, NOT the single-target `moves to cast … upon …` form. Rooming is always a spell cast; physical attacks can't AoE. It matches the `moves to attack` regex with `target = "everyone in the room"` — a room-wide commit rather than a specific mob.
- **Paradigm-only: `<player> is poised to assault the room!`** *([CONFIRMED] 2026-09-21, user)* is shown when we ENTER a room where someone is ALREADY room-spelling: they've committed to rooming and the combat round hasn't fired yet. **Stock does not emit this line.** It is the "already-active roomer" counterpart to the fresh `moves to attack everyone in the room` commit.

**Client use:**
- Attack-last coordination (waiting until every other member has committed before our own `*Combat Engaged*` lands) must treat the melee and spell announce lines as equivalent per-member signals — keying only on `moves to attack` misses every spellcaster in the party.
- A `moves to attack everyone in the room` line counts as that member's round commit against the whole room, our target included. Never follow "everyone in the room" as a literal monster (there is no such mob).
- `<player> is poised to assault the room!` is also treated as a room commit for attack-last (we cast our room spell after the poised member, so ours lands last).

### Player attack order and "Attack last"
*Status: Unrated*

- **Player attack order = announce order, FIFO.** Players deal their damage in the order they engaged/announced their attacks — first to announce fires first. Party rank does NOT change this order.
- **Backstab is pre-emptive.** A successful backstab always resolves **first**, ahead of the normal order.
- **"Attack last" (client setting) therefore does two independent things:**
  - Resolves your action at the **end** of the round's order — the **mana/energy save**. In a party that *usually but not always* one-rounds a mob, a spellcaster set to attack last casts last, so if the mob is already dead its spell has no target and **never fires — energy/mana saved**; it only spends when the mob survives to the caster's slot.
  - **Raises** your monster-targeting odds (a positive modifier) (Paradigm: score modifier; Stock: lock re-point — see *Monster target selection — who it swings at once fighting*) — useful for a tank drawing aggro, a cost for a squishy caster.

### Spell attacks auto-repeat; re-announcing a single-target spell is harmless
*Status: CONFIRMED 2026-08-05 (user); stop conditions and re-announce rule CONFIRMED 2026-09-21 (user) · Realm: both*

- **A spell attack command AUTO-REPEATS server-side every round, exactly like a weapon swing — on ALL realms.** You announce the spell once (type its cast-code + target); the next combat tick it fires, and it keeps firing each round while the target is present and mana suffices, with NO re-announcing. A spell attack is functionally identical to a physical attack — announce once, the server repeats it; the client adds its own gates (`MinManaPerCast`, `MaxCasts`, `MinEnemies`). See also *Timing & rounds → Combat spells: engaged once, auto-repeat per round*.
- **The auto-repeat stops on** *(2026-09-21, user)*: `break`; moving rooms; the room clearing; the target dying (single-target); or any non-swing action (a cast or an equip) with its `*Combat Off*` (see *Non-swing actions break combat (casting, equipping)*).
- **Re-announcing a SINGLE-TARGET spell the server is already repeating is HARMLESS — it costs no mana and does NOT double-fire** *(2026-09-21, user)*. Mana is spent **each time the spell fires**, not when it is announced *([CONFIRMED] 2026-09-26, user)*: a 500-energy spell that fires twice in a round costs its mana ×2; if the target dies before the second fire, that cast never goes out and its mana isn't spent. The announce itself is free. Re-typing the cast-code (e.g. `fbal`) just re-postures the same auto-repeat: the server still fires it once that round (or, if mana is short that round, postures without firing and tries again next round).
- **This corrects an earlier note** that claimed re-announcing "double-fires / wastes mana" — it does not, for a single-target spell.
- **Room spells are the exception — do NOT re-send one while it is channeling** *([CONFIRMED] 2026-09-23, report `paradigm-20260923-103938`)*: re-sending the same room spell breaks the running channel and starts a fresh one, wasting the round (see *Room-attack spells: cast bare, persistent channel*). This newer rule supersedes the 2026-09-21 "harmless" note for room spells.
- **The hazard for a single-target spell is targeting, not mana.** Re-announcing a **single-target** spell at a mob that just died would re-aim at the corpse. A **room spell is cast bare** — no target — so it has no corpse hazard, but it still must not be re-sent while channeling.

**Client use:**
- The client CAN safely re-announce a single-target combat spell for ordering purposes — this is how **attack-last** works for a caster: re-announcing our attack after the party's commits re-posts our action so it lands last, with no extra mana cost. A room spell must not be re-sent while it is channeling. The single-target re-announce guards on the target still being our live current target.
- The client re-announces when the best action must change, AND for attack-last ordering:
  - **Target dies** → announce at the next target.
  - **`MaxCasts` rounds elapsed** → switch to the next cascade action. Scope: **per-target** for the single-target slots (normal / alternate attack spell, single-target debuff); **per-room** for the two AoE slots (multi-attack, area debuff).
  - **Attack-last re-fire** → re-announce our current action after a party member's round commit, so ours lands last (a harmless re-posture for a single-target spell; never re-send a channeling room spell).

### Non-swing actions break combat (casting, equipping)
*Status: CONFIRMED (user); equip rule CONFIRMED 2026-08-24 (user) · Realm: both (equip rule)*

- **Casting a spell mid-fight emits `*Combat Off*`; the re-attack lands the same round.** The server emits `*Combat Off*` because a cast is a distinct action that interrupts the sustained weapon swing (see *Spells, buffs & conditions → Between-round cast slot vs the combat attack*). If the target is **still alive** after the cast, the desired behaviour is to **re-attack immediately** (as soon as the `*Combat Off*` lands), not wait for the next combat-round tick or a manual room re-parse. Confirmed by the user casting a Kai power (`swan`) on a live target: without a prompt re-attack the client idled a full round.
- **This applies to a hand-typed cast just as much as an engine-issued between-round cast.** A spell is cast by typing its cast-code (`Spells.Short`) directly (see *Spells, buffs & conditions → Casting syntax — bare cast code, optional target name*) (`swan`, `swan rat`), with no `c` verb precursor, so the client recognises a manual cast by that cast-code on the wire.
- **Equipping (`eq` / `wear` / `wield`) breaks combat on both Stock and Paradigm** *([CONFIRMED] 2026-08-24, user)*. It's a non-swing action, so it interrupts the sustained weapon attack and emits `*Combat Off*`, exactly like a between-round cast.
- **Getting items from the ground (`get`) and recovering your corpse (`recover corpse`) do NOT break combat** — you can grab your pile mid-fight without dropping the round.
- **This asymmetry is what makes in-combat death recovery safe to interleave:** grab everything freely, but the re-equip burst must be paced across rounds (a few pieces per round, re-attacking after each) so it doesn't stall the fight.

**Client use:**
- The engine re-attacks on the equip's `*Combat Off*` via the same signal a cast arms (`CombatManager.NoteBetweenRoundCast`).

### `break` — stopping an announced attack
*Status: CONFIRMED 2026-09-14 (user; report `stock-20260914-003246`) · Realm: both (no-effect reply differs by realm)*

- **`break` stops your announced attack.** Its practical value is **before you move**: leaving a room with an attack still announced raises the chance the monster **chases you**, so breaking first lowers that chance. This is what the client's *Break combat before running* (`CombatSettings.BreakBeforeFleeing`) is for.
- **The trigger is "did we send an attack command", physical OR spell** — either kind counts. If an attack went out and a movement command is about to follow, send `break` first.
- **With no attack sent and no `*Combat Engaged*` seen, a `break` is a wasted command** — don't send one speculatively.
- **A `break` with no effect differs by realm** *([CONFIRMED] 2026-09-14, user)*:
  - **Stock** — emits **nothing at all**. Silently ignored.
  - **Paradigm** — emits **`Your command had no effect.`**
- **Why that matters:** on Paradigm the wasted break is not merely wasted — it puts a parsed line on the wire. `KnownPatterns.CommandNoEffect` is live, and `DarkRoomCombatWatcher` reads that line as "our attack target isn't in the room" and **retracts the target** from the classifier. That handler is gated on `RoomTracker.IsInDarkRoom` and a non-empty `CurrentTarget`, so the collision needs Paradigm + a dark room + a stale target — but it is the reason a speculative `break` is worse than a no-op there. Send one only when an attack actually went out.

**Client use:**
- `PlayerState.InCombat` only flips once `*Combat Engaged*` has been parsed, so it is *not* sufficient on its own — a toggle can land between our attack going out and that reply arriving (report `stock-20260914-003246`). `CombatStateTracker` therefore reads InCombat **OR** the engine's live target, and the engine keeps two: `CombatManager.CurrentTarget` (weapon mode) and `CastingSpellTarget` (spell mode). Only one is live at a time, so both must be consulted.

### Attack-prevented states (stun, petrify, bind)
*Status: CONFIRMED 2026-09-03 (user); corrected 2026-09-17 (user)*

- **Some status effects — a stun, petrification/petrify, a leg/body bind — leave the character unable to issue any command that acts, not just an attack.** While the state is up the server refuses it (echoing the ailment's name, e.g. "You are stunned!"); when the wear-off message lands the block clears and acting resumes.
- **The block covers physical attacks, spell attacks, AND non-attack casts** (self heals / cures / buffs) alike — it is not attack-only. A message tagged with the **AttackPrevented** effect flag means "hold every cast and every attack" (weapon swing, attack spell, offensive debuff, AND a between-round self-heal/buff/cure) until the paired wear-off fires.
- **Correction history:** originally recorded as attack-only; corrected 2026-09-17 after a session transcript showed a between-round Minor Heal cast sent into a stun window come back denied with no mana spent and no heal confirmation.

**Client use:**
- Every combat attack chokepoint is gated on `ConditionTracker.IsAttackPrevented` (see `CombatManager.AttacksBlocked`), AND CastingDirector's between-round heal/cure/buff/debuff loop on the same flag (`CastingDirector.AttacksPrevented`). While true, both send nothing and re-attempt as soon as the wear-off fires.

### Room-attack spells: cast bare, persistent channel
*Status: CONFIRMED 2026-08-11 (user + fix PR #271); channel CONFIRMED 2026-09-23 (user — report `paradigm-20260923-103938`)*

- **Room-wide spells are cast BARE — no target** *([CONFIRMED] 2026-08-11, user + fix PR #271)*. The two AoE slots (multi-attack e.g. `blad`/dancing blades, area debuff e.g. `stnk`/stinking cloud) hit the whole room and must be cast as the bare cast-code (`blad`, `stnk`) — NEVER `blad <mob>`. The server treats the targeted form of a room spell as an unknown command ("Combat Off / Combat Engaged" flip-flop, no cast). Single-target attack/debuff spells DO take the mob name; only these two slots omit it.
- **A room / multi-target attack spell is a persistent channel, not a one-shot** *([CONFIRMED] 2026-09-23)*. E.g. `hsto` hellstorm — cast bare, no target. Once cast while engaged, it auto-fires at **every** monster in the room **each combat round** — the survivors of any kill **and** monsters that **roam in afterward** — until you stop meeting the conditions to cast it. It is the same server-side auto-repeat a single-target attack spell / weapon swing gets: announce **once**, the server owns the repeat.
- **You do NOT recast it to include new arrivals** — the running channel already hits them next round.
- **The game has no engine-side collision guard against recasting the same room attack.** If the client re-sends the same room spell while it is already channeling, the engine **breaks the current one and starts a fresh one** — visible as a `*Combat Off*` immediately followed by a `*Combat Engaged*`. That wastes the round and interrupts the AoE. (Contrast: re-sending a **between-round** spell — Energy 0 — shows only the `*Combat Off*` half.)
- **When the channel ends:** you keep casting the room spell until a cast condition fails — the room's live enemy count drops **below `MinEnemies`** (→ switch to a single-target action), you hit **MaxCastsPerRoom**, or mana falls **below the AoE slot's per-cast floor**. At that point re-evaluate the rest of the spell/combat chain normally (single-target attack spell → weapon, etc.).
- **A fresh room / multi-target attack spell cast into an empty room still fires and emits a "nothing to hit here" style message** — *([NEEDS CONFIRMATION] exact wording unknown — no test character yet)*.

**Client use:**
- `CombatManager._roomChannelSpell` records the active room-attack spell for the engagement. It **survives a kill** (unlike `_castingSpellTarget`/`_announcedSpellCode`, which a kill clears to re-pick a target) and clears only on a genuine end-of-fight (`ClearAttackSpellCascadeState`), a physical move (`NotePreMove`), or a switch to a non-room action.
- The post-kill re-pick in `OnEntitiesObserved` re-anchors the round to a surviving mob **without recasting** when the chooser — peeked against the fresh roster — would still pick that same room spell; the per-round heartbeat then keeps driving it (tally, no send).

### Single-target attack-spell cascade and `MaxCasts`
*Status: CONFIRMED 2026-08-05 (user); authoritative cascade rules CONFIRMED 2026-08-12 (user — report `paradigm-20260812-200128`, ~6th on this issue)*

- **`MaxCasts` counts combat ROUNDS, not individual casts** *(2026-08-05, user)*. It is the maximum number of rounds the client will spend casting this spell at a target — one round counts as one regardless of how many times the spell fires that round (e.g. a spell that casts twice per round still spends a single round).
- **The spell keeps casting only while mana also stays at or above `MinManaPerCast`.** Whichever limit is hit first (rounds spent ≥ `MaxCasts`, **or** mana below the reserve) ends the spell for that target and drops to the next cascade action.
- **Once dropped, stay dropped for that target.** A mana-regen tick that lifts mana back above the reserve must NOT flip the client back to the spell mid-fight; it commits to the weapon until the monster dies (or the room clears). This is a per-target latch, mirroring the observed-immunity latch.
- **Cascade order when the current spell is unaffordable** (`MinManaPerCast` vs live mana) **or the target is immune** → switch down the cascade: alternate attack spell (if configured) → physical main weapon → physical alternate weapon (if configured) → can't hit.
- **The authoritative rules** *(2026-08-12, report `paradigm-20260812-200128`)*, stated for ActionOrder = *Spells first* with a Normal + Alternate single-target attack spell configured (e.g. Normal `lbol` MaxCasts=1 / min-mana 75, Alt `mmis` unlimited / min-mana 0):
  - **One combat spell per combat round at one target.** A spell may fire multiple times in a round but that's still "one spell." You can NEVER announce two *different* attack spells the same round.
  - **A kill always wins over any cascade switch.** When the chosen attack spell kills the target, drop that target and re-pick — do NOT fire the OTHER attack spell (normal↔alternate) at the just-dead target. A cast at a corpse ("You don't see X here!") is a wasted round. This holds symmetrically: normal-kills-then-alt-at-corpse AND alt-kills-then-normal-at-corpse are both the bug.
  - **When the Alternate is considered (single-target only):** only against a still-ALIVE target the Normal can't handle — (a) we don't meet the Normal's cast conditions (mana below its floor, or its MaxCasts rounds elapsed while the target lives), or (b) the Normal came back "no effect" / immune (e.g. priest `harm` vs an acid slime).
  - **Every fresh target re-evaluates the Normal first.** The cascade state (which spell is current, the per-target cast count, the mana-drop latch) resets per target — a new mob always reconsiders the Normal, re-checking mana (regen ticks in / casts drain it round to round). It must NOT carry the previous target's cascade position, or a fresh mob opens on the alternate.
  - **MaxCasts=1 = one FULL round** (the spell actually fired), then switch ONLY if the target is still alive — never "announce once then immediately switch the same round before it fires."
  - **Mana fallback under Spells-first:** below the Normal's min-mana → consider the Alt; if mana is at or above the Alt's min-mana use the Alt, else fall back to physical. Once dropped off the Normal for mana, the per-target mana-drop latch applies (see the "Once dropped, stay dropped" rule in this topic).

### Weapons: "no effect" lines and the magical-weapon requirement
*Status: "no effect" lines OBSERVED; magical-weapon and hit-magic rules CONFIRMED*

- **`Your weapon has no effect against this monster!`** *([OBSERVED])* — the current weapon can't hurt this monster; the client swaps to the configured alternate weapon.
- **`Your fists have no effect against this monster!`** *([OBSERVED])* — you're swinging bare-handed (no weapon in hand, or it left your hand).
- **A magical creature needs a magical weapon (or a spell) to be damaged** *([CONFIRMED])*. Physical un-hittability is deterministic from game data: a weapon can damage a monster iff the weapon's magical "hit" level is at least the monster's magical-defense level (`ItemMagic.HitMagic(weapon) >= MonsterMagic.MagicalLevel(monster)`; a monster whose `MagicalLevel <= 0` is hittable by any weapon).
- **When the deterministic check can't decide** (weapon unknown to the tables), the `Your weapon has no effect` line is the reactive backstop — the client records the species as un-hittable by that weapon.
- **Spells are not bound by this physical gate** — an attack spell can damage a magical creature that no configured weapon can touch. So when the whole weapon path is exhausted (normal weapon can't hit, and either no alternate is configured or the alternate also can't hit), the *Physical first* action order falls back to the attack-spell cascade for that target rather than swinging uselessly.
- **Hit-magic (the "magical" to-hit level) only matters on weapons** *([CONFIRMED])*. It's the weapon's magical hit level compared above against a monster's magical defense; nothing else consults it. If a non-weapon item (armour / jewellery) carries a hit-magic ability value it's inert — the game ignores it.

**Client use:**
- UI that surfaces the hit-magic stat (e.g. the Item Finder) shows it on weapon rows only and blanks it everywhere else.

### Attack-command "no effect" fallback
*Status: Client policy (report `paradigm-20260809-131642`)*

- **Attack-command "no effect" fallback.** A spell driven through the global **attack-command** slot (`NormalAttackCommand = "harm"`, not the attack-*spell* rung) draws the same `Your spell has no effect on <monster>.` immunity line, but reaches the wire as a plain command, so the spell-slot cascade can't see it. The client falls the COMMAND path back the same way it does a physical weapon's "no effect": it marks the species failed vs the normal command (the next pick prefers `AlternateAttackCommand`) and re-sends the alternate command this round; with no distinct alternate it concedes the species and re-picks. (Report `paradigm-20260809-131642` — priest `harm` command vs an acid slime never dropped to `attack`.)

### Martial-arts strikes are class-innate
*Status: CONFIRMED · Realm: both (observed across every stock + Paradigm set)*

- **Martial-arts strikes (Punch / Kick / Jumpkick) are class-innate abilities, not a function of the trained Martial Arts skill.** A class grants a strike by listing its ability id in an `Abil-0..9` slot: **Punch = 29, Kick = 30, Jumpkick = 35**.
- **Mystic carries all three at value 1** across every observed stock + Paradigm set; no other class carries any.
- **The Martial Arts *skill* stat can be raised by items/races without unlocking the strikes.**

**Client use:**
- The Character Info combat panel gates each strike row on the class ability — not on `MartialArts > 0`.

### Backstab
*Status: mixed — per-fact tags inline*

- **Backstab command: `bs <target>`** *([OBSERVED])*.
- **A monster in the room with the see-hidden ability reveals the sneaker to the whole room** *([OBSERVED])*, so the opening move falls back to a normal attack rather than `bs`.
- **Backstab only lands on the opening round** *([CONFIRMED])* — the very first action taken in a freshly-approached room while sneaking or hidden. Once ANY combat action has fired here (a `bs`, a spell, or a normal swing), the surprise is spent and a later `bs` can no longer connect. So after the opener the client must fall back to the configured normal attack priority; re-issuing `bs` on a re-engage (a cast interrupt's re-attack, a target re-pick) wastes the round.
- **Success line** *([CONFIRMED])*: a landed backstab is a **single** swing containing the word **`surprise`** — e.g. `You surprise punch large wild dog for 36 damage!`. A surprise line making it through **proves the sneak did not fail** — the opener connected.
- **Backstab is silent to onlookers and resolves first** — see *Attack announce lines and round commits* and *Player attack order and "Attack last"*.
- **The opener is always `bs`, never `pu`** *([CONFIRMED])*. The stock realm has been observed to still run the surprise round even when the opener was a normal `pu` on the mystic — but that leeway is **realm-specific** and must not be relied on; other game types may require the literal `bs` opener to trigger the surprise at all. So whenever backstab is enabled and the character is armed (a successful sneak, or hidden with a monster in the room), the opening command is **always** `bs <target>` — the client must never substitute `pu` and hope the surprise still fires.
- **Only the opener needs to be `bs`, and follow-on attacks must stay quiet** *([OBSERVED, mechanism unconfirmed])*. In one live capture the opener `bs large wild dog` was followed by two client-sent `pu large wild dog` during the `*Combat Off*` / `*Combat Engaged*` interrupt bounce, and `You surprise punch ... for 36 damage!` still landed. **Do not read this as "the engine continues the backstab through follow-on attacks"** — the likelier explanation is timing: the `pu` commands simply hadn't registered server-side before the `bs` surprise round resolved. So a well-timed follow-on `pu` *could* have sabotaged the surprise. Practical rule: send `bs` as the opener, then stay quiet — don't spam follow-on attack commands that might register and clobber the surprise (let the server's auto-repeat carry the fight). Never send a second `bs`.
- **Failure signals — the reliable single-line tell** *([CONFIRMED])*. The surprise round is a **single** swing, so the **first** of the player's own combat-result lines after the `bs` settles the outcome: it either **carries `surprise`** (landed) or **lacks it** (failed). A failure surfaces either as a **whiff** (`You swing at <target>!` — no "for N damage", renders dark-cyan) or as a **folded normal round** (`You punch <target> for N damage!` with no "surprise"). Detection is **text-only** — the `surprise` token, not the color.
- **`You cannot backstab with this weapon.`** *([CONFIRMED])* — you tried to `bs` while sneaking with a weapon that isn't backstab-capable. No weapon-type flag in the game data exposes this ahead of time; it is only knowable reactively from this line.

**Client use:**
- The client tracks the spent opener per room; it is re-armed on the next sneak-approach or a fresh in-place hide (see *Movement & navigation → Hiding — sneak vs hide, the hide state machine, and search reveals*).
- The client enforces the quiet-follow-on rule by suppressing all Attack-Order re-fire while a `bs` is pending resolution.
- The client keys off the first-line failure tell and, when *Run if BS fails* is on, flees on a detected failure (routed through the normal break-before-flee escape path).

### Monster spell-attack damage — single cast, monster-owned energy
*Status: CONFIRMED 2026-09-04 (user)*

- **A monster's spell attack (`AttType-N == 2`) stores the spell number in its `AttAcc-N` field and the cast level in `AttMax-N`.** The linked **Spells** record holds the scaling formula (Min/MaxBase + per-level slope).
- **The monster's own `AttEnergy-N`** (shown as "N energy" in the attack row) is its per-round attack-budget cost for that cast — a *monster-owned* value.
- **The spell record's own `EnergyCost` is a player-cadence field** (how many times a *player* fires it per round); it does **not** apply to a monster's cast.
- **A monster casts the spell once when the attack lands**, so its per-cast damage is the **single cast** value: the formula's Min/Max scaled to the monster's cast level with **no per-round energy multiplier**. User anchor: spits acid #325 at level 11 = **12–40**, exactly its MinBase..MaxBase — a naïve `MaxDamage` would double a 500-energy spell like lightning bolt and 6× a 166-energy one.

**Client use:**
- `SpellCalculator.SingleCastMin/MaxDamage` computes this (the player getters keep the multiplier).
- `MonsterCatalog` resolves each spell-attack / mid-spell slot's damage range at build time (`MonsterAttackSlot.SpellDmg*`, `MonsterMidSpellSlot.Dmg*`), shown in Monster Intel's Attacks panel.

### Monster on-hit procs (`AttHitSpell-N`) are physical attacks, not casts
*Status: CONFIRMED 2026-09-12 (user)*

- **An `AttHitSpell-N` proc rides a physical attack slot.** It is **not** a spell cast, so it has no cast level of its own — there is no per-slot level field for it (`AttMax-N` is the physical attack's max damage).
- **The Monsters table has no monster-level column at all** (only `CharmLVL` and the per-mid-spell `MidSpellLVL-N`).
- **A proc must not feed anything that needs a cast level.**
- **Recognizing the message and timing a duration are different questions.** The proc's spell record still *has* messages, so it remains a legitimate candidate for attributing an unrecognized line.

**Client use:**
- Witnessed-ailment chip durations (`AppServices.ResolveAilmentDurationSeconds` → `MonsterCatalogEntry.CastLevelFor`) count only real spell slots (`AttType-N == 2`) and between-rounds spells, and skip `AttHitSpell` entirely.
- `RoomSpellAttributor` may still use the proc's spell record to attribute an unrecognized line.

### Guarded monsters redirect attacks
*Status: CONFIRMED 2026-07-14 (user + wire capture; report `paradigm-20260714-115526`)*

- **Some monsters are guarded by others in the same room** (e.g. a *brigand chief* guarded by *brigands*). A guarded monster **cannot be hit directly** while any guard is present: each attack aimed at it is **redirected to a guard**, announced by `<guard> moves to protect <protected>` — **no trailing period, no prompt prefix**, and both names are ordinary (multi-word) monster names.
- **The redirect repeats — one guard interposes per attack — until all guards are dead**, after which the protected monster is directly attackable. Confirmed on the wire in report `paradigm-20260714-115526`: three protect lines as each attack was shielded, then guard deaths, then the chief became hittable.

**Client use:**
- When the protect line names our current priority, the engine keeps that priority "blocked" and, as **each guard falls**, re-issues an attack **by the priority's literal name** (`aa <priority>`) to test whether the guard wall is down yet. This is reactive (line-driven), not read off the game-data "guarded by" field.
- The block clears when the priority itself dies, on room change, or on a target-not-here / no-effect reply.
- Without this, killing the last guard emits a *Combat Off* with the chief alive but unengaged, and auto-combat stalls until the user manually attacks (`aa b`) — the reported symptom.

### Monster `Align` values and your alignment-title ladder
*Status: `Align` values CONFIRMED (matches `LookupEnums.MonAlignmentNames`); numeric values CONFIRMED by user 2026-08-27 (capture `paradigm-20260827-144553`) · Realm: both*

- **Monster `Align` values** (the Monsters-table `Align` column, int 0–6): `0` Good · `1` Evil · `2` Chaotic Evil · `3` Neutral · `4` Lawful Good · `5` Neutral Evil · `6` Lawful Evil.
- **Your alignment-title ladder** (good → evil, from the who column): Saint → Good → Neutral → **Seedy → Outlaw → Criminal → Villain → Fiend**. The last five (Seedy and worse) are the **"Evil bucket."**
- **Numeric alignment values** — the underlying alignment number per band, most-good (negative) → most-evil (positive): `Saint -201 · Good -100 · Neutral 0 · Seedy 40 · Outlaw 80 · Criminal 120 · Villain 180 · Fiend 300`.
- **The ladder is identical on stock and Paradigm** — the only difference is Paradigm shows your exact number where stock shows just the band title.
- **"Lawful" is NOT its own band.** It's a user-set flag that forbids the character from ever committing evil acts, and it's treated as **Good** (-100) for all alignment math.
- **The finer evil titles (Villain / Fiend) matter mainly for item-equip gating** on items in that range — a separate system from the exit gate.

**Client use:**
- `AlignmentBucket` collapses the ladder to Good / Neutral / Evil for item filtering; the criminal / guard layer needs the finer title.

### Monster aggression — who opens on you unprovoked
*Status: CONFIRMED (Layer 1 alignment auto-aggro; Layer 2 criminal/guard behaviour; guard identification)*

- **A monster is hostile (attacks without being engaged first) as a function of the monster's `Align` and your character's alignment title.** Two independent layers stack.
- **Layer 1 — alignment auto-aggro (every monster, straight from `Align`):**
  - `Align` **1 / 2 / 5** (Evil / Chaotic Evil / Neutral Evil) — **opens on everyone**, every title.
  - `Align` **0 / 3** (Good / Neutral) — **never** aggros anyone.
  - `Align` **6** (Lawful Evil) — "honor among the wicked": aggros **Lawful / Good / Neutral** titles *([NEEDS CONFIRMATION] there is no Lawful title band — does this cover Saint too?)*, but **spares the Evil bucket** (Seedy and worse).
  - `Align` **4** (Lawful Good) — **never aggros by alignment**; the only Align-4 aggro is the guard subset via Layer 2.
  - So the only alignment-driven aggro that depends on *your* title is Align-6 (spares Seedy+); 1/2/5 are unconditional, 0/3/4 never bite on alignment alone.
- **Layer 2 — criminal / guard system** (the guard subset of Align 4; runtime reputation, NOT in the monster table). Keyed on **your title**, enforced by **guard** NPCs plus special actors:

| Your title | Guards | Extra actors |
|---|---|---|
| Lawful / Good / Neutral *([NEEDS CONFIRMATION] there is no Lawful title band — does this cover Saint too?)* | ignore | — |
| Seedy | ignore | bad deeds done *to* you are ignored (you lose guard protection, but guards don't aggro) |
| Outlaw | **attack on sight**, but spare your life | — |
| Criminal | **slay on sight** | — |
| Villain | **slay** | bounty hunters also attack |
| Fiend | **slay** | bounty hunters + archons / gods smite you with lightning |

- **Identifying a guard from imported data.** The game's monster-`Type` field distinguishes an ordinary NPC from a law-enforcing *guard*, but that distinction is **not exported into the MDB we import** — the imported `Type` only carries Solo / Leader / Follower / Stationary (0–3), never the guard value. So guard-ness can't be read off the type.
  - **The reliable proxy:** a monster that **casts spell 583 (`jail`)** is a guard, and it attacks us when our title is **Outlaw or worse**. Detection = the monster references spell `583` in any of its castable-spell fields (`AttHitSpell-*`, `MidSpell-*`, `DeathSpell`, `CreateSpell`).
  - In the shipped set that flags the guardsmen (#13/#14/#905/#538), Sheriff Lionheart (#40), and elite guardsman (#757).
  - This is a **partial** list — other mobs aggro the evil-titled without casting `jail` (e.g. Templar is a guard yet has no `jail`); those get added here as they're recognised.
- **A monster that opens on you unprovoked is an enemy, not a neutral** — e.g. storm giants. The client models those as the `Enemy` relationship (see *Neutral monsters and kill-on-sight*).

**Client use:**
- **Hostile-in-room test.** For each monster in the room, read its `Align`: hostile if `Align ∈ {1,2,5}` (always), or `Align == 6` and our title is Lawful / Good / Neutral, or the monster is a **guard** (casts `jail` 583 — the guard proxy in this topic) **and** our title is Outlaw-or-worse. Our own title comes from the stat screen / who line (`AlignmentTracker` / `PlayerStats`).

### Neutral monsters and kill-on-sight
*Status: CONFIRMED 2026-08-15 (user)*

- **A neutral monster never attacks you on sight / never attacks first.** "Neutral" here means any monster that doesn't open on you (see *Monster aggression — who opens on you unprovoked*), not only `Align` 3. It attacks **only if you've attacked it** and it's still alive — and once provoked it **keeps attacking every round until it dies**, even if you `break` and sit there.
- **So a room of un-engaged neutrals is safe to rest in.** The moment you engage one, *that* one is hitting you back (no resting mid-fight), but the others stay passive until you turn on them.
- **A monster that *does* open on you unprovoked is an enemy, not a neutral** — e.g. storm giants; see *Monster aggression — who opens on you unprovoked*.

**Client use:**
- The per-monster overlay `Relationship` (`Enemy` / `Neutral` / `Friend` / `Flee` / `Hangup`) drives auto-combat. `Enemy` = engage on sight; `Neutral` = leave alone.
- The **KillOnSight** flag (Monster edit dialog, shown only for `Neutral`) makes auto-combat **engage a neutral like an enemy** — but because neutrals never open on you, the *other* un-engaged neutrals still don't block resting, so between kills the engine rests/meditates (only when below the rest trigger) before turning on the next one.
- **Hand-attacking a passive neutral.** If *you* hand-attack one (a manual swing or combat cast), it turns hostile per the mechanic above, so the client marks that instance user-engaged and the auto-combat engine **takes over finishing it** — treating it like an enemy until it dies (and holding the walker in the room) instead of stopping the moment you engaged it. It's keyed per-instance by name and pruned once the mob is gone, so it never leaks onto a freshly-arrived same-named passive neutral; the *other* un-engaged neutrals stay passive and rest-safe. `Enemy` monsters are unchanged.

### Monster target selection — who it swings at once fighting
*Status: Stock CONFIRMED (stock DLL source, user-provided); Paradigm CONFIRMED (user writeup, Paradigm only); realm split CONFIRMED 2026-09-26 (user) · Realm: differs — see bullets*

- **Distinct from *whether* a monster opens on you:** once a monster is in a fight it picks **one target per beat**, and the two realms use different engines.
- **Stock** *([CONFIRMED] — stock DLL source, user-provided)*. A stock monster attacks a single **locked target** at a time (not everyone it has aggroed). Each beat:
  1. **Locked target present** → hit it again (the lock clears if that player left / died).
  2. **No lock → spread pick** among the aggroed players in room / terminal order: each rolls `genrdn(0,100) < 50 − 5 × (hits they're already taking this beat)`; **first to pass** is hit; if none pass, the **last eligible** player is hit (fallback). The mob drifts toward whoever *isn't* already piled on — a player taking ≥10 hits this beat hits a 0% threshold and is skipped by fresh rolls.
  3. **After swinging it rolls `genrdn(1,100) < Follow%`** (Monsters `Follow%`): pass → **lock** onto the just-hit player; fail on an **aggressive** align (∉ {0,3,4}) → **clear** the lock and re-spread next beat; fail on a **passive** align ({0,3,4}) → **keep** the lock.
  - **The mirror roll when a *player* hits the mob re-points the lock to that attacker** — the "attack last" behaviour. Follow% is the stickiness dial.
  - **Special types** (engine-internal type, not the imported `Type` column)**:** summoned (type `0x25`) never manage a lock this way; a type-5 monster only acquires a lock when currently untargeted.
  - **Carve-out:** an evil NPC won't spread onto a fellow-evil player (`EvilPoints > 39`).
- **Paradigm** *([CONFIRMED] — user writeup, Paradigm only)*. Paradigm rewrote target selection into a **weighted lottery** with no locked-target mechanic. Each player scores from a base **150**: `+ (10 − Charm/5)` (higher Charm lowers the score), `+` party position (frontrank 60 / midrank 30 / backrank 0; **solo = frontrank 60**), `+` recent aggro (last hitter **+30 × players-in-fight**, everyone else **−5 × players-in-fight**), **floored at 50**. The monster rolls a weighted lottery over the summed scores — bigger score = bigger slice, never a guarantee, never impossible.
- **Charm and party position have no effect on stock target selection** — they are Paradigm-only.
- **Targeting depends on the realm** *([CONFIRMED] 2026-09-26, user)* — the two models above never share a formula, and the Monster Aggro calculator shows each one.
- **Paradigm, in plain terms:** a hostile NPC picks its target by **chance**; hidden **modifiers** make a party member more or less likely to be chosen — never guaranteed, and the modifier values aren't visible in game:
  - **Party rank** — **frontrank** raises the odds of being targeted, **backrank** lowers them (midrank between).
  - **Attacking last** raises the odds; **attacking first** lowers them.
  - These stack: a **frontrank member attacking last** is the most likely target; a **backrank member attacking first** the least — but it's still a weighted roll.
- **On Stock, rank doesn't matter**; "attacking last" works through the lock re-point and Follow% stickiness above, not a score.

**Client use:**
- Surfaced in the **Monster Aggro** calculator (Workshop → Calculators), which shows the loaded set's model: `StockAggroCalculator` (acquisition → spread pick → Follow% stickiness) or `ParadigmAggroCalculator` (weighted lottery).

### Per-monster overlay automation (client policy)
*Status: CONFIRMED 2026-07-10 (user design); count-field reading is a client interpretation, [NEEDS CONFIRMATION]*

Client-side automation policy for the Game Data → Monster overlay flags — not engine behaviour, but how the client's auto-combat interprets the per-monster overrides.

- **DontBackstab — a flagged monster is never the backstab opener.** On the opening (armed) round the target picker **prefers the highest-priority non-flagged** actionable monster to backstab. A flagged monster is only chosen when **every** actionable monster in the room is flagged, in which case the room is **still cleared** — the opener just falls through to a normal attack instead of `bs` (never skip the room over the flag).
- **Override Attack / Override pre-attack — a per-monster attack that substitutes for the global Combat-tab choice for that species only.** The **Override Attack** field takes EITHER:
  - a **`Spells.Number`** (resolved to the `Spells.Short` cast-code) — routed through the *Normal Attack Spell* rung with its mana floor + per-room cast cap; OR
  - a **raw command verb** ("attack", "bash") sent **verbatim** as the attack verb, forced over the whole spell/weapon flow, with **no** rung gating (the server auto-repeats it like any attack command).
- **The editor disambiguates on save.** A positive integer is the spell id; other text is checked against the active set's known spell **cast-codes** — a match (e.g. "turn") resolves to that spell's Number and takes the **gated spell rung** (a user who types the code they'd cast in-game gets mana/cap gating, not a raw command). Only text matching no spell stays a raw command — this is what lets `attack` persist; an earlier int-only parse silently dropped it.
- **The pre-attack override stays spell-only** and occupies the *Single-Target Debuff* rung.
- **Gate bypass.** When an override is set the client **bypasses the effectiveness gates** (observed "no effect" immunity, SpellImmu level-block, and ≥100% elemental resist) — the rationale is that a user who hand-picks the attack for a specific monster has done the due diligence that it works. So a forced command is **never** second-guessed by the *Attack-command "no effect" fallback*. The **physical constraints still apply** for the spell-id form: the rung's mana floor, the once-per-target guard (pre-attack), and the override's own per-room cast cap.
- **Count = per-room cast cap (spell-id form only).** The override's configured count is the cap; the overlay documents **null = 0**, so a spell set with **no positive count is treated as inactive** and the client falls back to the global slot (likewise if the number doesn't resolve to a known cast-code). The command form carries no count — it's active whenever the text is non-blank. This "null/zero count ⇒ fall back to global" reading is the client's interpretation of the ambiguous count field — **[NEEDS CONFIRMATION]** if override behaviour is ever questioned.
- **0-mana stand-down.** At literal 0 mana nothing that costs MA can land — the server silently **no-ops** a cast or a mana-costing command with **no error line** to react to (an earlier build kept re-sending a forced `turn` every round while the player stood there getting hit until a regen tick). So at 0 mana the client marks spells unavailable (the chooser collapses to Backstab/Physical) and, for a forced command that resolves to a spell cast-code, falls back to the physical weapon; a genuinely free verb (bash/kick) still fires since it costs no MA. Re-evaluated each round, so it resumes the instant mana ticks back up.

### Combat round output order — damage lines precede the prompt, so HP lags a round
*Status: CONFIRMED 2026-09-05 (user + report `paradigm-20260904-214056`)*

- **A combat round's server output arrives as a burst: damage / hit / miss lines first, then the round's prompt** (`[HP=.../MA=...]`) — and **only the prompt carries the post-round HP/MA**. So between "the hit landed" and "the prompt parsed," the client's `PlayerState.Hp` still holds the *previous* round's value.

**Client use:**
- **Client encoding (load-bearing).** The between-round cast heartbeat, `TickEngine.CombatTickElapsed`, is fired *by the damage lines themselves* (`RecordCombatTick`, debounced to the round's first combat line) as well as by the 5 s timer fallback. So a damage-line-driven tick runs `CastingDirector.OnCombatTick` → `Evaluate` **during the burst, before the round's prompt refreshes HP** — the between-round decision sees stale HP.
- **The incident** (report `paradigm-20260904-214056`): a round chunked the player 254 → 117, but the tick fired the between-round decision while HP still read 254, so it spent the round's one slot on a due **armour buff** (looked safe at "254") instead of a heal; the player died two rounds later. The program log's `Buffing fired ... hp=254/257` is the *stale* read — matching the pre-burst prompt — while the wire shows the buff landing at `[HP=117/MA=286]`.
- **Fix (v3.50.10).** `TickEngine` now flags whether the tick in flight is damage-line-driven (`LastCombatTickWasDamageDriven`); on such a tick `CastingDirector` **holds the non-heal survival categories (cure / buff / debuff)** so the round's slot isn't spent on unconfirmed HP. Heals stay eligible — they're safe on the stale read (a stale-high read simply doesn't fire) and the prompt's own reactive `Evaluate` (fired when HP changes) then drives the real heal on fresh HP. The timer-fallback tick and out-of-combat heartbeat are HP-fresh, so they're unaffected.

### Kill detection and monster-kill message order
*Status: CONFIRMED 2026-07-23 (bug-report captures); exp-line and AoE rules CONFIRMED 2026-08-15 (user); fight-over rule CONFIRMED 2026-09-08 (user)*

- **A kill prints in a fixed order:** the monster's **death line** (e.g. `The toad croaks in agony, and collapses wetly.`) → `You gain N experience.` → `*Combat Off*`, all in the same server flush.
- **`*Combat Off*` is not a reliable death signal on its own.** Non-sustaining attacks (thrown weapons, KAI pummel, a party member's throws) emit an off/engaged bounce **every strike**, so a `*Combat Off*` fires many times per fight with no death.
- **The "exp + Combat Off within a window" fallback death is a *weak* heuristic** — only trust it for monsters whose specific death line isn't in the data (historical — per-monster death lines were retired in v3.16.0; the exp line followed by `*Combat Off*` is the kill signal). Because a specific death line always precedes its exp, an exp that lands right after a specific death belongs to that already-attributed kill and must not also arm the fallback. Otherwise, with identical-exp mobs dying every few seconds (a swarm), the prior kill's exp stays inside the window and the next fight's non-death `*Combat Off*` fires a phantom fallback death on it, a beat before the current mob actually dies.
- **The exp line is the earliest reliable per-kill signal during combat** *([CONFIRMED] 2026-08-15, user)*. Every kill grants exp, and the exp line lands **before** the kill's `*Combat Off*`. So while engaged with a target we've attacked, a `You gain N experience.` line means that target just died — recognize the kill on the **exp line**, not the later `*Combat Off*`. Waiting for the Off let the round's **alternate** attack corpse-cast: `lbol` kills → `mmis <corpse>` → "You don't see X here!" (report `paradigm-20260814-230258`). This is generic — the exp line is identical for every monster.
- **AoE clears the whole room as a burst of exp lines** *([CONFIRMED] 2026-08-15, user — "20 targets dead in 1 spell")*. One room spell prints a `<flavor>` + `You gain N experience.` **pair per monster it kills**, then a **single** `*Combat Off*` at the end. So exp-line count = kill count.
- **"The fight is over" = `*Combat Off*` AND an empty hostile roster** *([CONFIRMED] 2026-09-08, user)*. On stock, `*Combat Off*` is the message that marks combat ending *([NEEDS CONFIRMATION] same on Paradigm?)* — but as above it also fires on every cast and once per strike for non-sustaining attacks, so on its own it says nothing about whether anything is still alive. The usable pair is that line **plus** a room re-display showing no engageable monster left.
- **Death messages are arbitrary per-monster flavor** — no shared keyword (a scan of 1035 seed death lines: `…a tortured squeak`, `…to the ground`, `…without a sound`, `…a thousand pieces`, `…an agonized bellow`, `…in a heap`, most with no death word) and no distinctive colour (they render default/white). So a monster death **cannot be recognized by wording or colour generically** — the exp line is the generic signal, and our own targeting (`CombatManager.CurrentTarget`) names the mob.

**Client use:**
- `CombatStateTracker` combines `*Combat Off*` with the room observation: it clears the Combat gate only on that observation, and its idle-stall watchdog sends a bare CR to force a re-display when a final kill produced none.
- Anything asking "may I leave this room now?" — the Auto-Lair engage phase is the first — must read the gate, never the raw line.
- The per-monster `DeathLine` data was **retired** (v3.16.0): every kill is recognized from exp + `*Combat Off*`, and the dead slot is refreshed by the forced room re-display. No per-monster death message is maintained anywhere.

### Attributing a kill to a specific monster
*Status: CONFIRMED 2026-08-04 (user)*

- **Monster numbers are never observable in-game** — the client only ever sees monster *names* on the wire.
- **The reliable way to attribute a death to a specific monster** (e.g. "was that the boss?") is the monster **name we were engaged with** (`CombatManager.CurrentTarget`, read live at the death) **plus** the death event.
- **This works for the common fallback death too** — the fallback `exp + *Combat Off*` carries no identity of its own, but it is by definition the death of whatever we were fighting, so the engaged name attributes it.
- **Never key kill-attribution on a monster number the player can't have seen.**

**Client use:**
- This is what boss-timer kill detection uses.

### Attacking a monster that isn't in the room
*Status: CONFIRMED 2026-09-24 (user; report `stock-20260924-013525`) · Realm: Stock (Paradigm NEEDS CONFIRMATION)*

- **An attack at a monster that isn't in the room answers per the "talk slow" setting.**
  - With talk slow **off**, the unrecognised command is spoken: `a kobold thief` → `You say "a kobold thief"`.
  - With talk slow **on** it's `Your command had no effect.` — whether or not anyone else is in the room.
- **Both mean the target is gone** — typically a monster a party member killed, whose death gives us no exp line and so is never seen (report `stock-20260924-013525`).

---

## Spells, buffs & conditions

How spells fire, land, get resisted and interact with monster types, how buffs clobber and time out,
and how the engine applies and cures conditions (fear, poison, disease, blind, hold).

### Spell energy, firing rate and duration
*Status: CONFIRMED 2026-09-03 (user + Spells-table trace) · Realm: both (Paradigm 1.9.1 + Stock)*

- **`EnergyCost` sets a spell's per-round firing rate.** `EnergyCost` 1–1000 fires
  `floor(1000 ÷ EnergyCost)` times per round (mmis/lbol at 500 → 2×). `EnergyCost` 0 is cast between
  rounds. (Full firing-rate rule: *Timing & rounds → Combat spells: engaged once, auto-repeat per
  round*.)
- **Energy does NOT track lasting effect — `Dur` does.** A monster's damage breath can be `EnergyCost` 0
  yet purely instant, and a per-round bolt can still *poison*. The reliable tell is the spell's
  **duration**.
- **`Dur > 0` (or `DurInc > 0`) ⟺ a lasting effect ⟺ it has an applied + wear-off pair.** This covers
  **every** buff (bless `Dur`=40, prot-evil `Dur`=50) *and* every lasting debuff (poison `Dur`=5000, black
  curse `Dur`=40, a `Dur`=5 confuse breath, hold/blind).
- **`Dur` == 0 ⟺ an instant spell ⟺ no applied/wear-off.** Instant spells: attack/damage `Abil` 1/17,
  heal 18, cure 20/81/84/122, dispel 73, life-drain 8, summon 12.
- **Verified across both realms: no `Dur`==0 spell carries a poison/confuse/hold/paralyze/blind
  ability.**
- **Client use:**
  - The Messages seeds pre-mark `{null}` on both slots (applied + wear-off) for every `Dur`==0 record that
    has no condition flag and no already-authored effect line.

### Spell cast-success chance and the `Diff` column
*Status: CONFIRMED 2026-08-30 (source: syntax53/MMUD-Explorer `GetSpellCastChance`) · Realm: both*

- **A spell's chance to LAND (not fizzle) is a flat, level-independent function of the caster's
  `Spellcasting` stat and the spell's `Diff` (difficulty) column:**

  ```
  success% = clamp(Spellcasting + Diff, 0, cap)
  ```

  **Which wire line a failed Spellcasting+Diff roll prints isn't pinned down** *([NEEDS CONFIRMATION]
  — user, 2026-09-26: only the recorded lines are known)*. The recorded cast-failure line is
  `You attempt to cast <spell>, but fail.` (see *"You attempt to cast <spell>, but fail." — cast but
  missed*); no separate fizzle line has been recorded. The other ways a cast comes to nothing have
  their own lines or tells: target-type immunity (see *"Your spell has no effect" — immunity spends no
  round*) and elemental resist (see *Elemental resistance — flat, deterministic, pre-emptable*).

- **`Diff` is the Spells-table `Diff` column, normally ≤ 0.** A harder spell is more negative, so it
  lowers the chance (`ethereal shield` = −5). It's added directly to Spellcasting.
- **cap = 100 for a Kai caster (`Magery` type 5), else 98 on stock.** MMUD-Explorer's GreaterMUD
  branch caps at 100, and **GreaterMUD is Paradigm** *([CONFIRMED] 2026-09-26, user — "Paradigm" is
  the name the game goes by to players)*, so Paradigm's non-Kai cap is 100. An earlier note here said
  Paradigm "is a MajorMUD variant, not GreaterMUD" and shares the stock 98 — that was wrong.
  - **Client use:** `SpellCastChance.Cap` gives 98 on stock, 100 on Paradigm or for a Kai caster.
- **Short-circuits:** `Diff ≥ 200` marks an always-succeeds utility spell → **100%**. A `Spellcasting`
  of **0** means the character isn't a caster (or the stat line isn't parsed yet) → **no stated chance**
  (the client shows "—", never a bogus 100%).
- **Level plays no part** — the caster's level scales a spell's damage/duration, not its landing chance.
- **Client use:**
  - Modeled in `SpellCastChance`; surfaced as the Spell Book "Difficulty" column.

### "You attempt to cast <spell>, but fail." — cast but missed
*Status: CONFIRMED 2026-08-05 (user)*

- **The line means the spell DID cast — mana was spent — but it missed the target (a hit-roll
  failure).** It is NOT out-of-mana. Whether it is also the outcome of a failed Spellcasting+Diff
  roll isn't known — no separate fizzle line has been recorded *([NEEDS CONFIRMATION])*; see *Spell
  cast-success chance and the `Diff` column*.
- **An attack spell drains mana every round it repeats, whether it lands or misses.** A "but fail" round
  is a spent round (mana down, zero damage), not a free retry.
- **Client use:**
  - The client must not treat this line as an out-of-mana / interrupt signal.

### Between-round cast slot vs the combat attack
*Status: CONFIRMED 2026-08-12 + 2026-08-25 + 2026-09-01 + 2026-09-18 (user; reports `paradigm-20260825-103417`, `paradigm-20260901-123720` / `-140747`, `paradigm-20260918-190830`)*

- **A configured debuff fires BEFORE the attack on engage, and the attack lands the SAME round — the
  between-round debuff and the combat attack are independent slots.** *(2026-08-12 + 2026-08-25)* On
  entering a room, if a debuff slot is due (area debuff at ≥ `MinEnemies`, or a single-target /
  per-monster pre-attack debuff), the debuff is cast first and the combat attack goes out immediately
  behind it, both in the same combat round.
- **A 0-energy between-round cast and the combat action do not compete for one slot.** Sending the debuff
  "has nothing to do with" the combat spell, so pairing them does **not** draw the
  `You have already cast a spell this round!` rejection that a second *between-round* cast would (see
  *One between-round spell per combat round*).
- **The debuff still obeys the Spells + Ailments spell-type priority.** A higher-priority in-between
  survival cast (heal / cure / buff) that's due wins the in-between slot first, and the debuff waits for
  the next in-between pass. Only when nothing higher-priority is queued does the debuff pre-empt the
  attack.
- **Report `paradigm-20260825-103417` (2026-08-25):** an AoE debuff went out but the multi-attack spell
  followed a full round later — the attack had been deferred to the debuff's `*Combat Off*`; corrected to
  fire same-round.
- **A between-round debuff DOES collide with ANOTHER between-round cast that same round** *(2026-09-01,
  reports `paradigm-20260901-123720` / `-140747`)*. The one-between-round-cast slot is shared, so if an
  auto-buff (e.g. a mana-flux recast) or the user's OWN manual cast already spent the round's cast, the
  debuff draws `You have already cast a spell this round!`.
  - The debuff is sent optimistically (the client marks the mob debuffed on wire-write, before the server
    answers), so the rejection has to un-mark it.
  - The combat ATTACK that round self-corrects on its own owed-and-retry path; only the debuff mark needed
    the rollback.
- **The independent-slots rule covers ALL between-round casts, not just debuffs** *(2026-09-18, user)*.
  Survival casts (heal / cure / buff) share the same independence from the combat attack. A due
  between-round cast must fire on its own slot the round it's queued — it must NOT sit out a round "so
  the attack can catch up," because the attack never competed for that slot in the first place (it
  auto-repeats server-side and re-announces on the cast's `*Combat Off*`).
- **The only real per-round limit is the single shared between-round cast slot itself.**
- **Client use:**
  - On the `You have already cast a spell this round!` rejection, the client recognizes it and re-fires
    the debuff next round instead of leaving the mob falsely marked debuffed (and the monster
    un-debuffed).
  - The engine previously coupled survival casts to the attack: after a survival cast it withheld the
    *whole* between-round slot for a round (an "attack-owed alternation"), so a top-priority heal — even
    an emergency heal — could sit queued and unfired for a full ~5s round (report
    `paradigm-20260918-190830`). That coupling was removed.

### Why an attack spell fails to damage — three independent mechanics
*Status: CONFIRMED (worked examples use the 1.11p data set)*

- **Three independent mechanics decide whether an attack spell damages a monster — do not conflate
  them:**
  1. **SpellImmu +N — level immunity** (this topic).
  2. **Spell targeting restriction (e.g. living-only)** — see *Spell targeting: monster type tags* and
     *"Your spell has no effect" — immunity spends no round*.
  3. **Damage-type resistance** — see *Elemental resistance — flat, deterministic, pre-emptable*,
     *Magic Resist (M.R.) and `TypeOfResists`* and *Poison (`AttType 6`) — binary immunity*.
- **`SpellImmu +N` blocks any spell whose base learnable level (the Spells table `ReqLevel`) is below
  N; such a spell deals no damage.** A spell learnable at level ≥ N still lands.
- **Example:** monster **#184** has `SpellImmu +10`, so every spell learnable at level 9 or lower can't
  hurt it — only spells learnable at 10+ work.
- **Client use:**
  - Level immunity is deterministic from game data, so the engine **pre-empts** it: `LevelBlockedFor` /
    `AttackSpellCanLand` skip a level-blocked spell before casting, and fold it into whether the monster
    is engageable at all.

### Spell targeting: monster type tags
*Status: CONFIRMED (monster-side flags verified against 1.11p) · charm-level rule NEEDS CONFIRMATION*

- **A spell's eligibility against a monster is a match between a spell-side targeting tag and a
  monster-side type flag.** A spell with no targeting tag affects every monster; a tagged spell only
  affects monsters carrying the matching flag (or, for `living-only`, *lacking* the NonLiving flag).
- **These are hard eligibility gates, independent of resistance and level immunity.** A targeting
  mismatch is **not** a resistance and **not** a level gate — it's a hard eligibility mismatch between a
  spell attribute and a monster attribute.
- **Example:** the priest **harm** spell carries `AffectsLivingOnly` (ability code 108), so a monster
  flagged **NonLiving** (code 109) takes no damage from it — this is the
  `Your spell has no effect on <monster>.` case (e.g. `harm` on an acid slime). A spell with **no**
  targeting tag hits everything: `magic missile` carries no such tag, so it damages living, nonliving,
  **and** undead alike.

**Monster-side type flags** *([CONFIRMED] — verified against 1.11p)*
- **NonLiving — the `NonLiving` ability (code 109).** Its **absence** means the monster is living; there
  is no separate "living" flag.
- **Undead — a dedicated `Undead` column on the Monsters row, separate from NonLiving.** It is a
  **byte-boolean: 0 = not undead, any non-zero = undead.** The MDB stores the Boolean `True` as `-1`, so
  across 1.11p the column holds `0` (986 rows), `1` (107 rows), **and `255`** (8 rows — `-1` as a byte);
  all non-zero values mean undead. **Test `Undead != 0`, never `== 1`.**
- **Animal — the `Animal` ability (code 78).** Gates the animal-charm spells below.
- **These are independent axes: a monster can be NonLiving without being Undead.** Worked examples:

  | Monster | NonLiving (109) | Undead (col) | Animal (78) | Net |
  |---|---|---|---|---|
  | thug (#10) | — | 0 | — | living |
  | lashworm (#2) | — | 0 | ✓ | living animal |
  | acid slime (#5) | ✓ | 0 | — | nonliving, **not** undead |
  | skeleton (#11) | ✓ | 1 | — | nonliving **and** undead |

**Spell-side targeting tags** *([CONFIRMED])*
- **`AffectsLivingOnly` (code 108)** — only affects monsters **without** the NonLiving flag (e.g.
  `harm`, `enslave`).
- **`AffectsUndeadOnly` (code 23)** — only affects monsters with `Undead != 0`.
- **`AffectsAnimalsOnly` (code 80)** — only affects monsters with the Animal flag (e.g. `charm animal`).
- **No tag** — affects all monster types (e.g. `magic missile`).

**Charm / enslave family** *([CONFIRMED] except where noted)*
- **All charm-type control spells share the same base ability, `Enslave` (code 6); they differ only by
  their targeting tag.** `enslave` (#55) is `Enslave` + `AffectsLivingOnly` (any living target);
  `charm animal` (#92) is `Enslave` + `AffectsAnimalsOnly` (needs the Animal flag); `song of charming`
  (#49, bard) is `Enslave` + `AffectsLivingOnly`.
- **[NEEDS CONFIRMATION] A "charm level" is believed to cap what these can affect** (possibly the
  caster's minimum level for the spell to take). This could **not** be verified: the reference client
  only *displays* these tags — it does not model charm success, and no "charm level" column exists on
  the Spells row (only `ReqLevel` / `MageryLVL` / `Cap`, which are learn/scaling params). Ask before
  building on a charm-level rule.

- **Client use:**
  - Reactive backstop, off the `no effect` line: `OnSpellNoEffect` marks the species + spell immune
    for the rest of the room and gates that spell down the attack cascade (primary → alternate →
    weapon).
  - Since 2026-09-22 target-class mismatches are pre-empted from game data — see *"Your spell has no
    effect" — immunity spends no round*.

### "Your spell has no effect" — immunity spends no round
*Status: CONFIRMED 2026-08-09 (user; report `paradigm-20260809-162350`); updated 2026-09-22 (report `paradigm-20260922-082559`)*

- **`Your spell has no effect on <monster>.` spends NO round — it's free.** A no-effect cast (the
  living-only / immunity mismatch) doesn't consume the combat round, so you can cast a *different* spell
  that same round.
- **The swap is one cascade step per round**, because the alternate's own no-effect can't arrive until it
  has cast next round.
- **UPDATE (2026-09-22): target-class immunity IS pre-emptable from game data.** The earlier claim here
  that "the living-only immunity isn't pre-emptable from data" was WRONG — a spell's target-class
  restriction *is* in game data (Spells `Abil` codes 23 AffectsUndeadOnly / 80 AffectsAnimalsOnly / 108
  AffectsLivingOnly), as is the monster's life-class (NonLiving ability 109, Undead column, Animal
  ability 78 — see *Spell targeting: monster type tags*).
- **Client use:**
  - The client swaps the attack cascade's primary → alternate attack spell **immediately** on the
    no-effect line, the same round, rather than idling until the next ~5s tick (report
    `paradigm-20260809-162350`: `harm`→`hamm` was losing a round because only the weapon fallback swung
    immediately while the alternate *spell* waited a tick).
  - Since 2026-09-22 the client **proactively skips** an attack spell whose target-class the monster's
    type excludes (`turn`-undead vs a non-undead mob, `harm` vs a nonliving construct) —
    `SpellTargetTypeIndex` + `MonsterLifeIndex.CanAffect`, gated in `CombatSpellChooser` beside the
    level / resist blocks (report `paradigm-20260922-082559`).
  - Only the reactive backstop remains for spells with no target-class tag; the reactive
    one-step-per-round path still applies to any immunity game data can't prove.

### Drain / life-steal spells (mage)
*Status: CONFIRMED 2026-08-14 (user)*

- **Some mage spells are life-drain spells — the damage they deal also heals the caster for a portion of
  it.** Examples: `vamp`, `dtch`, and (high-level evil mage) `nebo`. Availability is gated by the
  class's **magery level**, like any spell.
- **They are a combat action, NOT a between-round cast.** They take the place of the round's attack and
  auto-repeat server-side like any attack spell (see *Combat → Spell attacks auto-repeat; re-announcing
  is harmless*).
- **Tactically they're used like a heal** — only worth casting when you actually want the HP back, so a
  drain-capable mage treats them as an emergency heal that also does damage.
- **They cannot affect NonLiving or Undead targets.** Draining one produces the same
  `Your spell has no effect on <monster>.` line as any living-only spell (there's no life to drain). So a
  drain's valid target is **living (no NonLiving ability 109) AND not undead (Undead column == 0)** — the
  union of the living-only gate and an undead exclusion.
- **Client use:**
  - Against an ineligible target the drain is skipped and the client falls back to the normal attack
    cascade.
  - The Combat tab's **Drain spell** slot casts as the round's action when HP is at/under its configured
    %-trigger (and mana ≥ its floor, casts remain, and the target is drain-eligible), reverting to the
    normal attack pick once HP recovers or mana drops below the floor.
  - By default the drain **yields to the room AoE** (multi-attack) whenever that would fire — rooming
    enough enemies to trigger the AoE is usually the safer play — and a **"Drains override AoE"** option
    lets it pre-empt the AoE too when the loop calls for it.

### Elemental resistance — flat, deterministic, pre-emptable
*Status: CONFIRMED (worked examples use the 1.11p data set)*

- **A spell's damage type is its Spells-table `AttType` column** (the same values `LookupEnums` labels
  for the Browser). How resistance applies depends on which type it is — **do not treat all three
  flavors alike** (elemental, Magic Resist, poison), because only the first supports a pre-emptive skip.
- **The five elemental `AttType`s map one-to-one onto a monster `Resist-<type>` ability:**

  | `AttType` | Element | Monster resist ability (code) |
  |---|---|---|
  | 0 | Cold | `Resist-Cold` (3) |
  | 1 | Fire | `Resist-Fire` (5) |
  | 2 | Stone | `Resist-Stone` (65) |
  | 3 | Lightning | `Resist-Lightning` (66) |
  | 5 | Water | `Resist-Water` (147) |

- **`Resist-<type> +N` is a flat N% reduction of that element.** Example: #184 (adolescent red dragon)
  has `Resist-Fire +50`, so fire spells deal **half** damage. At **100%** the element does **0 damage**;
  **above 100%** the damage goes **negative** and the spell **heals** the monster instead of harming it.
- **The value is signed; a negative `Resist-<type>` is a vulnerability** — that element deals **extra**
  damage (e.g. `Resist-Fire -50` → +50% fire damage). Across 1.11p the column runs roughly
  **-200 … +300**. Full curve: negative = bonus damage → `0` = normal → `100` = zero damage → `>100` =
  healing *(re-confirmed 2026-09-26, user)*.
- **A ≥100% elemental resist is the ONLY resistance the engine can safely pre-empt** — skip the spell
  before casting when the target resists its element ≥100%. A negative (or 1–99%) resist must still
  **fire** the spell: it's a damage bonus or a partial cut, never a reason to skip.
- **There is no dedicated message.** Every spell's verbose hit text differs, so the only runtime tell is
  the **damage number** in that spell's own hit line — **0 or negative is the resist signal.**
- **Not modeled today:** a resisted 0 / heal cast produces no `no effect` line, so when game data
  doesn't show the resist, the runtime 0 / negative hit line isn't acted on — the engine can keep
  re-casting a spell that heals the monster.
- **Client use:**
  - Elemental ≥100% resist is pre-empted via `MonsterResistIndex`; `CombatSpellChooser` explicitly
    resist-blocks *elemental* spells only (see also *Magic Resist (M.R.) and `TypeOfResists`*).

### Magic Resist (M.R.) and `TypeOfResists`
*Status: CONFIRMED; damage-code gating CONFIRMED 2026-08-30 (user + syntax53/MMUD-Explorer `CalculateResistDamage` / `CalculateSpellCast`)*

- **Magic Resist (M.R., code 36) is probabilistic, NOT pre-emptable.** `AttType 4` "Normal" spells (mage
  `magic missile`, priest `harm`) are **not** elemental, so the elemental Select-Case explicitly
  **skips** them (it skips `AttType 4` Normal and `AttType 6` Poison). Their only damage-type mitigation
  is the monster's `M.R.` ability, **not** a `Resist-<type>`.
- **M.R. never nulls a spell deterministically from its value alone.** It works through **two
  independent effects, each separately gated** (the equations are the reference client's own combat
  math).
- **Partial damage reduction — gated by the spell's damage ability code.** Applies to code **17**
  `Damage(-MR)` (the "(−MR)" means M.R. **is** subtracted); code **1** `Damage` takes **no** M.R. cut.
  *([CONFIRMED] 2026-08-30 — **this note previously had the two codes reversed**.)*
  - `baseline M.R. is 50` (the no-change point). For M.R. ≥ 50 the reduction is `(M.R. − 50) / 200`,
    climbing to a hard **cap of 50%** at M.R. 150 and stopping.
  - The target's own AntiMagic raises the cap to **75%**, via `M.R. / 200`.
  - Below M.R. 50 the term goes negative — low M.R. *amplifies* damage taken.
  - So even an enormous M.R. only ever **halves** (or, under AntiMagic, three-quarters) the damage —
    never 0.
- **Full-resist chance — gated by the spell's `TypeOfResists`, independent of the damage code.** A
  separate per-cast roll can negate the spell entirely, with probability `M.R. / 2` percent (M.R. 100 →
  50% chance, capped at 98% for M.R. ≥ 196) — a *chance*, never a certainty short of the cap.
- **Net: 100 M.R. never means 0 damage** (the partial cut caps at 50%, or 75% under AntiMagic), so M.R.
  must **never** feed a ≥100%→skip guard. In every case a high-M.R. monster still takes Normal-spell
  damage.
- **Worked examples — both example spells carry code 17, so both take the partial cut:**
  - `magic missile` (code 17 + `TypeOfResists 0`) takes the partial cut but **no** full-resist roll —
    still the most reliable nuker (never fully *negated*), just softened against a high-M.R. target.
  - `harm` (code 17 + `TypeOfResists 2`) takes the partial cut **and** can be fully-resist-rolled.
  - A code-**1** Normal spell would take **neither** M.R. effect (though the full-resist roll, being
    `TypeOfResists`-gated, could still fire).
- **`TypeOfResists` is the full-resist eligibility flag.** The Spells-table `TypeOfResists` column
  (values 0/1/2) gates whether the full-resist roll can fire, independent of the damage type:
  **0 = never** (no full-resist roll — the spell always lands its post-reduction damage), **1 = only
  when the target has AntiMagic**, **2 = always eligible**.
  - Elemental attack spells are typically `TypeOfResists 0` (fireball / frost jet / lightning bolt / acid
    jet all 0), so their only mitigation is the deterministic elemental cut — which is exactly why a
    ≥100% elemental resist is safely pre-emptable.
  - Among Normal spells, `magic missile` is `TypeOfResists 0` (never rolled-resisted) while `harm` is
    `TypeOfResists 2`.
- **Client use:**
  - The Game Data spell view's interactive damage calculator (`SpellDamageCalculator`) implements the
    reduction: the per-cast range comes from Min/MaxBase scaling, then the code-17 **magic-resist partial
    cut** (fraction `(MR−50)/200`, cap 50%; AntiMagic `MR/200` cap 75%; below MR 50 it amplifies), then
    the **elemental flat-% cut** on any elemental spell. The probabilistic full-resist chance is shown
    separately, never folded into the range.
  - **The combat engine never estimates M.R.-reduced damage** — it only pre-empts spells on
    deterministic signals (elemental ≥100% resist via `MonsterResistIndex`, `SpellImmu`, and the
    `Magical` weapon-hit gate) — M.R. is never a resist-block (see *Elemental resistance — flat,
    deterministic, pre-emptable*).
  - So correcting the code-1↔17 gating changed **no combat decision** — the old reversed note was never
    implemented in engine code; it drove only the (now-fixed) doc and this display calculator.

### Poison (`AttType 6`) — binary immunity
*Status: CONFIRMED*

- **Poison is not resistible.** It has **no** resist value and **no** `Resist-Poison` code — a target is
  either affected or immune, never "partially resisted."
- **Immunity is sourced from race / items, not a resist stat:**
  - The **Kang** race is poison-immune.
  - The **golden headdress** item grants poison immunity.
  - **Swamp boots** / **snakeskin boots** negate certain room-cast "swamp poison" effects — snakeskin also
    grants immunity to certain poisons, varying by game-data set.

### Attack-spell mana efficiency
*Status: Unrated (client formula as used by Monster Intel)*

- **Damage per mana:** `damage-per-mana = effective-damage-per-round ÷ mana-per-round`.
- **Rounds to kill:** `rounds-to-kill = ceil(monster HP ÷ effective-damage-per-round)`.
- **Mana to kill:** `mana-to-kill = rounds-to-kill × mana-per-round`.
- **Both the per-round damage and per-round mana carry the same energy multiplier**, so the ratio reads
  as damage output per point of mana.
- **Client use:**
  - Monster Intel's ranked attack spells: `MonsterMatchupCalculatorSpells.RankAttackSpells` (takes a
    `monsterHp`, sorts eligible spells most-efficient first).

### Heal spells that can't be cast on self
*Status: CONFIRMED 2026-09-22 (user)*

- **A few heal spells are, by their game data, castable only on OTHERS — a bare self-cast is rejected by
  the engine.** **`anno` / annointed hands (#744)** is the notable example (one of very few such
  spells).
- **Self still counts toward the AoE member gate** — an area heal legitimately lands on everyone
  including the caster.
- **Care point:** don't use `anno` in an example that implies a self-cast; it's specifically one of the
  spells that can't.
- **Client use:**
  - The party-heal picker (`CastingDirector.PickPartyHeal`) **never single-targets self** with a
    party-settings heal, regardless of which spell sits in the slot: it can't know per-spell whether a
    self-cast is legal, and the caster's own dips are the Spells + Ailments self-heal slots' job anyway.

### Item-cast spells — how a `CastsSp` fires (on-use vs combat proc)
*Status: CONFIRMED 2026-07-18 (user); message-record rules CONFIRMED (user + item panels)*

- **An Items row's `CastsSp` ability (code 43, `AbilVal` = the cast `Spells.Number`) does NOT always
  mean "you command-cast this spell."** An item delivers a spell via an `Abil 43` (CastsSp) slot, and
  how the cast fires depends on the ability that **immediately precedes** the `43` slot in the item's
  `Abil-0..19` list.
- **`%Spell` (code 114, `Abil 114`) before `CastsSp` → an automatic per-swing combat proc.** The
  `%Spell` `AbilVal` is the proc chance; it fires only while the item is equipped and you send a
  physical attack — on a hit the weapon adds the cast spell as **extra damage lines during combat**
  (e.g. *hellblade*: `%Spell 25` → `sunsword`; nexus spear's energy-hits #431 at 25%/swing, darkwood
  staff #849 at 10%/swing). This is the "casts a spell during combat rounds" case. Not
  player-triggered.
- **`CastOnKill%` (code 1114, `1114`) before `CastsSp` → an on-kill proc.** Fires **only when the
  wearer lands a monster kill** (`AbilVal` = the per-kill chance), not per swing. Legitimately appears
  on **worn** gear, not just weapons (e.g. the *fukumen* / *shinobi mask* / *oni mask*, all worn masks
  → `invigorate` / `adrenaline rush` / `nimble`). Not player-triggered.
- **A single item can carry both** — one `%Spell→CastsSp` proc **and** a separate
  `CastOnKill%→CastsSp` proc (e.g. *Pulsar*: `%Spell 45 → blue ray` + `CastOnKill% 90 → energy barrier`).
  Two independent automatic procs.
- **Bare `CastsSp` (no `%Spell` / `CastOnKill%` modifier before it) → a command-activated "on use"
  cast.** The player deliberately activates the readied item — `use <item>` — to cast the spell (e.g.
  a wand/staff; *jeweled longsword* → `weapon major valour`; weapon major bless #114 blesses your
  weapon; the nexus spear's spear-slam #72). **These are the item spell sources the Spell Book lists**
  alongside learnable spells. They are player-visible casts, so their message records
  (caster/target/witness, plus applied/wear-off for a lasting effect) are worth keeping and filling.
- **`CastsSp` on a one-time consumable** (potion, food — `ItemType` Drink/Food, worn Nowhere) →
  technically activates on use (quaff/eat), but it's **single-use**, so it is **not** a repeatable cast
  source and is **excluded** from the Spell Book. (The equippable-slot gate already filters these — a
  cast source must ready into a real equipment slot.)
- **The on-use / proc MESSAGE lives on the cast spell's record (Spells#N)**, shared by every item that
  casts the same spell — never on the item.
- **A weapon-proc spell that only deals damage (no lasting effect — `Dur==0`, which excludes every
  ailment) is not worth a message record** and is dropped from the seeds; the generic colour combat
  recognizer still tallies its damage.
- **A proc that applies an ailment (poison/blind/hold/disease — `Dur>0`) keeps its record and its
  condition flag; an on-use cast always keeps its record.**
- **Client use:**
  - `KnownSpellCatalog.GetClassCastItems` (the Spell Book's item-cast list) must skip any `CastsSp` slot
    preceded by a `%Spell` / `CastOnKill%` modifier — otherwise proc weapons and on-kill gear masquerade
    as command-cast spell sources.
  - See `ItemCastSpells`, `Game/GameData/`.

### `RemovesSpell` buff clobbering — literal lists, per-realm timing, shared wear-off
*Status: CONFIRMED 2026-09-07 (user + game data); literal-list rule CONFIRMED 2026-09-10 (user; report `paradigm-20260910-012303`; verified on Paradigm); realm timing CONFIRMED 2026-09-10 + 2026-09-26 (user) · Realm: differs — Paradigm re-checks every tick (~3s), Stock only at cast*

- **A spell that carries a RemovesSpell ability (Abil 122 → a target spell number) strips that target
  buff off you the instant it lands.**
- **When the removal is checked depends on the realm** *([CONFIRMED] 2026-09-10 + 2026-09-26, user)*
  — this is why #540's suppression is Paradigm-only:
  - **Paradigm checks engine-side every tick (~3s)** and strips any buff a spell on you removes — an
    ACTIVE remover keeps stripping its removes on that ongoing tick, not just at its own cast. So a
    one-way-removed buff CANNOT coexist with an active remover regardless of cast order: an active
    greater bless re-strips chant every few seconds. Layering is impossible there: a one-way loser can
    never hold while its remover is up.
  - **Stock checks only at the moment of cast** — a buff's RemovesSpell fires ONLY when that spell is
    cast (one-time; no ongoing re-check). A buff and its remover **can** be layered by casting in the
    right order — the remover first, then the buff it removes. So a ONE-WAY pair cast in non-colliding
    order holds **both, durably**: cast **greater bless first, then chant** and both stay (gbls's strip
    already fired with no chant present; chant doesn't list gbls). Reverse order (chant then gbls)
    leaves only gbls. (Also subject to the stock 10-affect cap — see *Stock "10 spelling" affect cap*.)
  - **On Stock, clobbering is a one-time effect at cast, not a standing suppression.** Casting a buff
    that the other removes, **after** it, simply re-applies — only the remover's cast strips. (Not so
    on Paradigm, which re-checks every tick.)
  - **Mutual pairs (bless ↔ greater bless — each lists the other) are last-cast-wins in both realms.**
- **For a mutual pair, whichever was cast last is on you; one-way pairs follow the per-realm rule**;
  the buffs the winning cast removes are gone.
- **A buff strips exactly the spells its own `RemovesSpell` (Abil-122) list names — nothing more.** There
  is NO "family exclusivity slot": the fact that bless removes both chant and greater bless, and that
  bless ↔ greater bless remove each other, does NOT make chant remove greater bless.
- **The direction is per the realm's data, so check it, don't assume** — on Paradigm 1.9.1 **bless
  removes chant** (confirmed 2026-09-07, user + game data), and chant (#23) also removes bless, so the
  pair is mutual there. On Stock v1.11p the removal is one-way: bless removes chant, but chant (#23 /
  #825) removes only blight and curse.
- **The bless family, concretely (Paradigm 1.9.1 data — chant lists bless there):**
  - **greater bless #146** removes: greater curse #61, bless #14, curse #15, **chant #23**, divine
    favour #62, ashwood wand #668.
  - **chant #23** removes: blight #75, curse #15, bless #14 — **NOT greater bless.**
  - So it's **asymmetric**: casting **greater bless strips an active chant** (gbls lists chant
    directly), but casting **chant leaves an active greater bless alone** (chant's list omits it).
  - An earlier build inferred chant→greater-bless transitively via a "family slot" — that was WRONG and
    was reverted; the user verified in-game that chant does not strip greater bless.
- **Two `chant` records share cast code `chan`.** **#23** is the learnable one (`Learnable`, all
  classes, the one a player casts and the one gbls/curse/blight reference); **#825** is a non-learnable
  room-cast duplicate (`Casted By = Room …`). The clobber math keys off #23.
- **A wear-off line does fire when the buff is stripped — but only as the shared family text, so it
  cannot be attributed to the right spell by text alone.** Bless and chant **share the same message
  records** (both the cast/applied line "You feel lucky!" *and* the wear-off "The effects of bless wear
  off!"), so a chant-being-stripped shows bless's wear-off text; there is no distinct wear-off for the
  stripped buff. A client keying timers off those shared lines mis-attributes — the cast confirm
  refreshes the wrong buff, and the wear-off clears the wrong one.
- **The reliable signal is what we actually sent** (the distinct "You cast <spell> on …" / the pending
  self-buff short).
- **Client use:**
  - Paradigm: `BuffConflictAnalyzer.OneDirectionalLosers` flags a one-way loser, which is dropped rather
    than cast into a remover that re-strips it every tick. Mutual pairs stay last-cast-wins. This is
    exactly why **#540's "suppress the loser" is correct on Paradigm** (don't waste rounds maintaining
    chant under a maintained gbls) and **correctly does nothing on stock** (where cast-order lets you
    hold both).
  - Stock: `BuffConflictAnalyzer.OneDirectionalRemoverCodes` + `BuffPriorityOrder.OrderRemoversFirst`
    cast the remover ahead of its loser so both stay up. The stock counterpart — **cast the remover
    before the loser so both stay up** — is BUILT: the Buff Watchdog re-orders its maintenance casts
    (remover before removed) on stock only, keeping the loser maintained instead of dropping it
    (`BuffPriorityOrder.OrderRemoversFirst` + `AppServices.CollisionOrderConstraints`, the stock branch
    of the same one-way removes graph #540 suppresses on Paradigm). The clobber-clear re-applies the
    loser after each remover recast.
  - The Buff Watchdog keys the timer off what was sent and, on a wear-off right after a clobbering
    cast, leaves both timers alone and **infers** the clobber from RemovesSpell + cast order
    (`Until − TotalSec` = each buff's cast instant), rendering the clobbered bar as **"conflict"** rather
    than a bogus countdown.
  - **Applied-latch gotcha** *(report `paradigm-20260910-012303`)*: `ConditionTracker` dedups a repeated
    applied line (each spell's "You feel …" latches once until its wear-off). A clobber clears the
    victim's *timer* but the only wear-off the game sends for it is the shared family text, which the
    tracker ignores, so the victim's applied-latch survived — and a later **re-cast** of that victim was
    deduped, never re-confirmed, and so never drove its own clobber-clear (a re-cast greater bless never
    dropped an active chant). Fixed by `ConditionTracker.ReleaseApplied`, called from the clobber-clear
    so the victim's latch is dropped too.

### Stock "10 spelling" affect cap
*Status: CONFIRMED 2026-09-10 (user) · Realm: Stock*

- **Paradigm has unlimited buff/affect slots; stock caps active affects at 10** (buffs + debuffs
  combined).
- **Casting/receiving an 11th pushes an existing affect off** — the exact eviction rule is not yet known.
- **Client use:**
  - Not modelled in the client yet.

### Buff timers across a disconnect
*Status: CONFIRMED 2026-08-28 (user); earlier model 2026-08-16 (user); current behaviour after report `paradigm-20260908-205555`*

- **Current client behaviour** (`CastingDirector.PauseBuffTimers` / `ResumeBuffTimers`, after report
  `paradigm-20260908-205555`): any disconnect freezes the Buff Watchdog display only.
- **On the first in-game prompt after reconnect, both self and party timers ride their real (absolute)
  expiry** — no shift by the offline gap, no wholesale wipe of self timers — and only the timers that
  lapsed while offline are dropped and recast.
- **A fresh character (ProfileLoaded) is the only path that clears everything.**
- The superseded models were recorded as the client's reconnect handling evolved; neither describes
  the current code exactly.
- **Superseded (2026-08-28): when WE (the caster) disconnect, only OUR OWN buffs are in doubt.** The other party
  members stayed **online**, so their buffs kept **counting down in real time** the whole time we were
  gone — their absolute expiry doesn't move.
  - On reconnect: clear our self-buff timers (re-establish fresh), and **leave party-member timers at
    their real (absolute) expiry** — they now read the correctly reduced remaining (any that lapsed while
    we were away recast).
  - Do **not** shift party timers forward by the offline gap (the old "freeze + preserve remaining"
    model over-counted them).
- **Superseded (2026-08-16): session vs drop for buff timers.** **Any** disconnect — manual, hangup, or an unexpected
  drop — **freezes** the buff timers (the buffs persist server-side through link-death).
  - The Buff Watchdog display freezes at the drop instant too (its 1s heartbeat is a wall clock that
    keeps ticking offline).
  - The first in-game prompt after reconnect **resumes** them shifted forward by the offline gap (same
    remaining), instead of clearing and recasting from full.
  - Clearing (no buffs assumed) happens only on a **fresh character** (ProfileLoaded — a same-character
    reconnect does not reload the profile, so its paused timers survive) or when the offline gap exceeds
    the longest armed buff's full duration (they're surely gone by then).

### Fear
*Status: CONFIRMED 2026-09-10 (user — MegaMUD backscroll capture, terror beast in the Black Wasteland, spell ID 430) · Realm: both; `stat` countdown suffix Paradigm-only*

- **Fear is a monster-cast debuff, not a room property.** It **is** announced and **is** a trackable
  timed condition, contrary to an earlier (wrong) note that it was messageless.
- **Onset:** the attack line `…'s unearthly shriek strikes terror into your heart!` then
  **`You are afraid!`**.
- **Active:** shows in the `stat` effect list as a timed buff/debuff line, **`You are afraid!`**.
  - The trailing **`(27s)` remaining-time suffix is Paradigm-only** (seconds left as of when `stat`
    fired) — stock prints the effect line with no countdown.
  - This realm split applies to the *whole* stat effect list, not just fear (the same capture shows
    `safe from evil! (75s)`, `halo … (156s)`, etc.).
- **End:** **`The effects of fear wear off!`**.
- **While feared, the game shoves you between cardinal-CONNECTED adjacent rooms** (never across a
  `go`/text-exit) and re-renders each new room — but those forced moves carry **no command echo and no
  per-move line** (bare `[HP=..]:` prompts, just changing exits).
  - So the *direction* of each fear-move is unknown from the wire, but the *feared state itself is
    known* (onset → wear-off window).
- **Fear is rare**, so this is an edge case, not the common path.
- **Client use:**
  - A nav client can therefore recognise it's being fear-moved and stop treating the echo-less
    redisplays as re-looks — dropping to localisation instead of holding/guessing — rather than being
    "wire-indistinguishable."

### Ailment identification — which spells cause disease / poison / blind / hold
*Status: CONFIRMED 2026-09-03 (user + AbilityNames.cs) · Realm: both (each realm seed flagged from its own MDB)*

- **Identification differs by ailment because of how the engine models each.** Used to mark which
  spells inflict a curable condition (for the Messages seed's Effect flags).
- **Disease has NO engine effect code** — it isn't an enumerated ability. So a *cure disease* spell
  can't target "the disease effect"; it instead lists the specific spells it removes via ability
  **122 (RemovesSpell)**, whose `AbilVal` is each removed Spell.Number.
  - Disease-causing spells are therefore found by unioning the RemovesSpell targets of *cure disease*
    and *cure major disease* (the only two cures that enumerate).
  - *cure major disease* is Paradigm-only.
- **Poison / blind / hold-person ARE enumerated engine effects**, so their cures clear the effect
  wholesale and the causing spells are found by the spell's own **inflict ability code**: Poison =
  **19**, BlindUser = **107**, HoldPerson = **74**, Paralyze = **75** (74+75 → "Movement prevented").
  - **In the data, paralyze spells use 74, and nothing uses 75** *([OBSERVED] 2026-09-26, game data —
    v1.11p and Paradigm 1.9.1 identical)*: hold person #66 / #395, paralyze #125 / #251, paralysis
    #939 and earthquake hold #927 all carry **74 (HoldPerson)**. The *paralyzed* condition spells
    (#327, and Paradigm's #5110) carry **71 (Confusion) at +100** instead. Code 75 exists in the
    ability list but no spell in either data set uses it.
  - CurePoison is code 20; a cure poison spell also lists 794/798 explicitly via RemovesSpell as special
    cases.
  - Ability 19 is the lingering-poison DoT. `poison bolt`'s own record has no 19, but it EndCasts poison
    bite (19), so it does poison — see *Condition Effects flags derive from the linked spell's ability
    codes* (data-verified on both v1.11p and Paradigm 1.9.1: spell #35 → EndCast (151) → #1366). This
    supersedes the 2026-09-03 claim that instant poison *damage* spells like `poison bolt` use a
    different code and are not "poisoned"-condition.
- **The ailment→flag map:** Diseased, Poisoned, Blinded, MovementPrevented.
- **Diseased has no ability code, so its flags are set by hand:** the seed's Diseased flags were
  hand-authored from the *cure disease* / *cure major disease* RemovesSpell union, and the runtime reads them from the
  seed (`AppServices.DiseaseApplySpellNumbers` reads the Diseased flag off the Messages seed records).
- **Client use:**
  - Both realm seeds are flagged from their own realm MDB (Euphoria-Stock-ish for stock, Paradigm-1.9.1
    for paradigm).

### Cure-spell identification — which spells REMOVE each ailment
*Status: CONFIRMED 2026-09-13 (user + real stock/paradigm Spells data) · Realm: both*

- **The inverse of ailment identification: recognition must come from game data, not the local cure
  config.** A party-mate cures with their own class's spells (a Priest's cure poison), which the local
  character may never learn.
- **The codes (`CureSpellIndex`):**
  - **poison** — `CurePoison` (**20**), or `DispellMagic` (**73**) whose `AbilVal` is the Poison apply
    code (19).
  - **hold / paralysis** (MovementPrevented) — `Freedom` (**81**), or `DispellMagic` (73) targeting
    HoldPerson (74) / Paralyze (75).
  - **blind** — `DispellMagic` (73) targeting BlindingLight (53) / BlindUser (107).
  - **disease** — `RemovesSpell` (**122**) whose `AbilVal` is a disease-applying Spell.Number (the
    disease-apply set = spells whose Messages record carries the Diseased flag — same source as ailment
    identification).
- **The `DispellMagic` / `RemovesSpell` indirection is what separates cure from apply.** A DIRECT
  `BlindUser(107)` code is the blind APPLY (spell "blind"), while `DispellMagic(73)=107` is the blind
  CURE ("cure blindness").
- **Combined heal+cures register as poison cures for free.** Curing wind `curi` and merciful grace `mgra`
  carry `CurePoison(20)` beside `Heal(18)`.
- **Client use:**
  - Used to clear a party member's ailment chip when **any** member casts a cure.

### Casting syntax — bare cast code, optional target name
*Status: CONFIRMED 2026-08-29 (user)*

- **No `c`/`cast` prefix is needed — the bare 4-letter cast code IS the command.**
- **A bare code casts per the spell's own scope**, with no target token: a self buff lands on yourself, a **whole-party** buff on the whole party (`unfa` → unholy fanaticism on the party, `chan`, etc.), a room spell on the room.
- **A code plus a name is a single-target cast** (`gbls fuj`, `bles alice`). The name is a **prefix / shorthand** the server resolves against the players (and NPCs, for offensive spells) **in the room**: `gbls fuj` casts greater bless on `Fujin` if that uniquely matches.
- **An ambiguous prefix bonks** with a "do you mean …" list and casts NOTHING.
- **Client use:**
  - This is exactly how the engine casts — bare code for self / whole-party, `<code> <given-name prefix>` (e.g. `gbls fuj`) for a single member.

### Combat vs in-between spells — round energy cost is the divider
*Status: CONFIRMED 2026-08-14 (user; capture `paradigm-20260814-210613`)*

- **A spell's round energy cost (`Spells.EnergyCost`) is the clean divider between a combat/attack spell and an in-between/utility spell.**
  - **Combat spell — `EnergyCost` between 1 and 1000.** It IS the round's combat action (spends the round's energy), so it competes with the weapon swing. Examples in Paradigm 1.9.1: `mmis` 500, `lbol` 500, `vamp` 1000, `fbal` 1000.
  - **In-between spell — `EnergyCost` 0.** A heal / buff / cure that rides the shared in-between window and does NOT spend the round's combat action. Examples: `mend`, `armr`, `mshi`, `bles`, `cure` — all 0.
- **`AttType` does NOT distinguish them** — both attack and utility spells carry an AttType (e.g. mmis and mend are both AttType 4). Energy cost is the reliable signal.
- **A cast-code (`Spells.Short`) is AMBIGUOUS — it maps to several spells**, the player's plus monster variants, each with its own `EnergyCost` *(capture `paradigm-20260814-210613`)*. In Paradigm 1.9.1 `vamp` is the player's **vampiric touch (1000, combat)** AND monster **vampiric hits / bite / rosebush (0)**. The player casts their own version, so **a code is a combat spell if ANY spell with it is** (energy 1–1000). Classify a shared cast-code by "any entry combat", not the last one.
- **Client use:**
  - Classifies a **manually-typed cast** during combat: a hand-cast combat spell is the user taking the round's attack (a user override — the engine must not re-send its auto attack that round), while a hand-cast in-between spell keeps the engine's resume-after-cast behaviour (it heals/buffs, then the engine resumes attacking).
  - A last-writer-wins lookup that picked a 0-energy monster duplicate misfiled a hand-cast `vamp` as in-between and the engine re-announced its attack over it — hence the "any entry combat" rule.

### One between-round spell per combat round
*Status: CONFIRMED 2026-08-16 (user + report `paradigm-20260816-101702`); same-round attack rule CONFIRMED 2026-09-16 (user; report `paradigm-20260916-033047`)*

- **You may cast only ONE 0-energy "between-round" spell per combat round — total, across heals, buffs, debuffs, and use-item buffs.** These are the `EnergyCost = 0` spells (mageshield / holy armour, cures, regen HoTs, etc.); they ride *between* the round's main action, so one is free each round on top of your attack.
- **The between-round cycle runs on the same 5s tick whether or not you are in combat** — see *Timing & rounds → Combat round (5s) and the between-round cast cycle*.
- **A between-round cast and the combat attack are independent slots** — see *Between-round cast slot vs the combat attack*.
- **A second between-round spell the same round is rejected with `You have already cast a spell this round!`, and the spell you just sent does NOT fire** — success or failure of the first doesn't matter, the round's single between-round slot is spent.
- **This line never appears for combat spells** (lbol / mmis / deathtouch / fireball are 500–1000 energy — the round's main action, not between-round), so it is purely the between-round coordinator's signal.
- **A between-round buff never delays the SAME round's real attack — a fresh engage must fire instantly right behind one** *(CONFIRMED, user 2026-09-16)*. Casting a 0-energy buff (`prfl`, `vlwa`, …) and then immediately attacking a monster that just arrived is legitimate the same round; there is no server-side wait.
- **Client use:**
  - The between-round coordinator (`CastingDirector`) casts at most one between-round spell per round, gated on a latch cleared by the **combat round tick** — `TickEngine.CombatTickElapsed` (`NotifyRoundComplete`), the 5s combat heartbeat refreshed by damage lines.
  - It must NOT clear on **`*Combat Off*`**: that fires per *kill*, so in a multi-mob room it lands several times a round and would re-open the slot mid-round (the recast storm's door). The bug that produced the "4 mageshields in a short span" storm was the coordinator's own one-per-round cooldown being cleared on every per-hit tick, letting it send several between-round spells a round.
  - On the rejection the just-sent spell didn't fire, so its optimistic recast timer is dropped (it re-attempts next round) and the round's slot is latched spent (`CastingDirector.OnCastFailed`).
  - `CombatManager`'s fresh-engage dispatch used to go through `CastCoordinator`'s `MinRecastInterval` — a pure client-side burst guard against `CastingDirector` re-firing its OWN casts within one frame, never meant to model this server rule — so a buff landing the instant a monster walked in deferred the attack to the next tick, handing the newcomer a free round (report `paradigm-20260916-033047`). The fresh-engage `DispatchRoundAction` call now passes `bypassRecastInterval: true`, matching the already-correct debuff-then-attack path.

### Per-perspective spell messages
*Status: CONFIRMED 2026-08-29 (user — game data)*

- **Every spell has per-perspective messages in the Messages table** (`MessageRecord`):
  - **CasterMessage** — the line YOU see when YOU cast it.
  - **TargetMessage** — the line YOU see when it lands on YOU (i.e. someone cast it on you).
  - **WitnessMessage** — the line YOU see when someone casts on someone else.
  - **AppliedMessage** — the buff-applied condition line.
- **A successful cast emits the perspective-appropriate line to each observer**, so a buff landing on the party can be recognised from ANY seat — our own cast (Caster), a buff cast ON us (Target), or a buff cast on a party member (Witness).
- **Client use:**
  - Today the buff-timer engine reads only CasterMessage (our casts) + AppliedMessage (self conditions); it does NOT yet read Target / Witness, so a buff another party member casts isn't tracked in the Buff Watchdog.
  - Our own single-target manual cast IS tracked: `CastingDirector.NoteManualBuffCast` arms a pending confirm for a targeted hand cast (`gbls fuj`) and resolves the member off the success line.

### Learning a spell from a teaching item
*Status: CONFIRMED 2026-08-15 (user + wire capture)*

- **A teaching item is used with `read <code>` — the SAME 4-letter cast-code the spell is otherwise cast by.** A teaching item is a spellbook / tome carrying the `LearnSp` ability, code **42**, whose value is the `Spells.Number` it teaches.
- **On success the game confirms with the full spell name**, even though the command speaks the short code:

  ```
  :read agon
  You add agony to your spellbook!
  ```

  The command uses `agon`; the confirmation names `agony`.
- **This wording is distinct from the classic learn-scroll line** ("You read <scroll> and learn the spell <name>.").
- **Client use:**
  - The client recognises both (`KnownPatterns.LearnSpell` + `LearnSpellFromItem`) and marks the spell obtained (`SpellbookState.MarkObtainedByName`, keyed on the name), so the learned-spell set updates the instant a spell is learned mid-session rather than waiting for the next `spells` poll.

### Self-buff recast tracking — keyed on the 4-letter cast code
*Status: CONFIRMED 2026-08-16 (user + report `paradigm-20260816-101702`)*

- **A self-buff's active/recast state is keyed to its own 4-letter cast code**, resolved from game data: the success line (`Spells` → *user definitions* → CasterMessage / AppliedMessage) starts the duration timer, and the buff's OWN wear-off (`AppliedEndsWith`) clears it.
- **Distinct buffs that merely share an applied / onset line must NOT cross-clear.** Unlike confusion (one shared state, many sources — a group clear is correct there), the five shields that all emit **`You feel protected!`** (mageshield #132, ethereal shield #4, holy/unholy armour #148/#149, heros tabard #859) are *separate* effects with their *own* distinct wear-offs.
- **A hand-typed buff is confirmed by the CAST CODE, not the shared success text.** You type the 4-letter code (`bles`) → the client arms/refreshes that buff's timer anchored on the code, exactly as an engine cast does. The following success line just confirms it landed; identity comes from the code, never the ambiguous applied message.
- **Client use:**
  - A wear-off fires `ConditionTracker.ConditionEnded` only for the record whose own end-text matched — a sibling sharing the applied line is dropped from the active set (keeping flags honest) but does not fire the event, so it can't clear a different buff we actually cast.
  - Hand-typed casts arm the timer via `CastingDirector.NoteManualBuffCast`, fed by the `OutboundCastObserver`.

### The `stat` screen's buff readout is never a fresh cast
*Status: CONFIRMED 2026-08-16 (user + report `paradigm-20260816-232454`) · Realm: Paradigm*

- **On Paradigm, `stat` lists each active effect as `You feel <effect>! (<remaining>s)`** (e.g. `You feel lucky! (411s)`, `You feel safe from evil! (12s)`).
- **Ignore it for buff tracking — the effect text is shared across many records.** One `You feel lucky!` line matched **11** catalogue records (bless + chant + several weapons/items), so a readout can neither identify **which** buff is up nor legitimately "apply" one.
- **A genuine fresh-cast effect line has no parenthetical.**
- **Client use:**
  - Treating the readout as a cast falsely marked buffs active on **login** (the post-entry `stat` refresh), and because the tracker only fires on a not-active→active transition, that stale "active" state then **suppressed the confirm on the real manual cast** (a repeat applied line is no transition).
  - The client keys off the trailing **`(<remaining>s)`** parenthetical to skip these readouts entirely (`ConditionTracker`). *([NEEDS CONFIRMATION] Stock's stat list has no countdown suffix — does a Stock stat readout falsely arm buffs?)* (The Stock no-suffix form is in *Fear*.)

### Party buff slot scope — whole-party vs single-target
*Status: user 2026-08-17 / 2026-08-28, corrected 2026-09-06 (CONFIRMED, user; report `paradigm-20260906-150624`); master-enable note 2026-09-09 (report `paradigm-20260909-220212`) · Realm: both (scope classification confirmed against stock + Paradigm data)*

- **A whole-party cast still lands on a solo caster** *(CONFIRMED, user 2026-09-06)*. MajorMUD treats a lone character as a party of one, so a whole-party cast/use still lands on yourself while solo — it isn't refused or wasted.
  - Superseded: this used to say party-buff slots (then a separate list, since folded into the one unified `CharacterProfile.PartyBuffs` list, see `Models/Profile/BuffSettings.cs`) never fire solo. That's wrong for a **whole-party** scope specifically.
  - A **single-target** slot genuinely still needs an actual party member to aim at, so that branch is unaffected.
- **Scope classification** (confirmed against stock + Paradigm data), gated first on **`EnergyCost == 0`** (a buff, not an attack):
  - **`Spells.Targets` = 2 (Self or User) → a single-target beneficial buff** cast on ONE other member (`frenzy`, `divine favour`, `blood ritual`, `regeneration`). Never targets self (self uses the self-slots).
  - **`Spells.Targets` = 10 / 13 (Divided / Full Party Area) → a whole-party buff**, one cast with no target that blankets the party (`chant`, `mass frenzy`, `unholy fanaticism`, `rejuvenating field`). Lands on self too.
  - **Scope 0/1 (self-only), 4/8/9/12 (enemy), 7 (item) are NOT party buffs.**
- **Single-target targeting is by selected member (given name), not class** — a slot blesses "all members" or a checklist of specific players, and only fires for a name that is a current `par` party member (never casts at someone uninvited). Targeting is by `par` membership, not room presence (`AppServices.IsGivenNameInRoom` is NOT a party-buff cast gate); a `You do not see <name> here!` reply backs that member off — see *Party → Targeted casts on a hiding member*.
- **Supersession: a spell that carries RemovesSpell (Abil 122) removes the named spell** (the Spell Book renders it "Removes <spell>"). When a configured **whole-party** buff removes a configured self-buff (e.g. **chant removes bless** — a Paradigm-only example), in a party we stop self-casting the removed one and let the party buff cover us — the Buff Watchdog shows that self-buff "covered by <party buff>". Only whole-party covers count (a single-target party buff can't cover self). (Paradigm 1.9.1 data has both directions — chant #23 removes bless and bless removes chant; see *`RemovesSpell` buff clobbering — literal lists, per-realm timing, shared wear-off*.) **Layer when possible, cover only when not** *([CONFIRMED] 2026-09-26, user)*: if the self-buff can be layered with the party buff (Stock, one-way remover — cast the party buff first, then the self-buff), layer them; only when layering is impossible (Paradigm's per-tick removal, or a mutual pair) cast the party buff and stop self-casting the one it removes. **Client use:** `AppServices.SelfBuffCoverage` covers only a mutual pair on Stock (a one-way remover layers, ordered by `CollisionOrderConstraints`); on Paradigm it covers every removed self-buff.
- **Client use:**
  - `CastingDirector.PickUnifiedBuff` falls back to the self-bless timing gates for a `WholePartyOn` slot when `!PartyState.IsInParty`, instead of holding it forever behind "must be in a party" (report `paradigm-20260906-150624`: a whole-party item-cast buff, `platinum sceptre`, never fired outside a party).
  - **`WholePartyOn` remains the master enable**: the per-slot `CastSolo` option only extends an enabled slot to solo play and must not bypass an unchecked Party box. (2026-09-09, report `paradigm-20260909-220212`: unchecked whole-party rows kept casting solo through their default `CastSolo=true`, draining mana while the rest of the UI reported them off.)

### Debuff slot spells — energy and targeting
*Status: CONFIRMED 2026-08-17 (user + game-data trace) · Realm: Paradigm 1.9.1*

- **The Settings → Combat debuff slots (single-target debuff + AoE debuff) hold between-round spells, not combat attacks.** Two hard rules distinguish a valid debuff from a misconfiguration.
- **0 energy = between-round: a debuff slot spell must have `EnergyCost == 0`.** That's what separates a debuff from a combat attack spell: attacks cost energy (Paradigm `lbol`/`mmis` = 500, `fbal`/`dtch` = 1000), between-round spells cost 0 (`blin`/`frai`/`stnk`/`corr`-flesh = 0).
  - Energy — not targeting — is the discriminator: `blin` (debuff, 0) and `lbol` (attack, 500) share the SAME `Targets` scope (8).
  - A non-zero-energy spell in a debuff slot is an attack spell mis-slotted.
- **Targeting must fit the slot** (`Spells.Targets` scope):
  - **Single-target debuff** → a single-enemy scope: **Monster (4)** or **Monster or User (8)** (e.g. `blin`/`frai`/`corr`-flesh are 8).
  - **AoE debuff** → an area/room scope: **Divided Area not-self (3)**, **Divided Area incl-self (5)**, **Divided Attack Area (9)**, **Full Area (11)**, **Full Attack Area (12)** (e.g. `stnk` is 12; `fbal` shares scope 12 but is an attack (EnergyCost 1000), so not slot-eligible).
  - The **party-area** scopes (Divided Party Area 10 / Full Party Area 13) are buffs/heals aimed at the party, **never** enemy debuffs.
  - So a targeted spell can't be slotted as an AoE, nor an AoE as single-target (e.g. `stnk`, Targets 12, belongs in the AoE slot, not single-target).
- **Gating:** the **single-target** debuff is gated by **Auto-Combat** (it's a pre-attack debuff, part of the attack rotation); the **AoE** debuff is gated by **Auto-Nuke**.
- **Client use:**
  - The client rejects a mis-slotted debuff before it casts and warns once in the program log.

### Stat debuffs on a monster subtract and may go negative
*Status: CONFIRMED 2026-09-02 (user)*

- **Stat debuffs on a monster SUBTRACT their listed magnitude and may go NEGATIVE.** An enemy debuff that lists e.g. "AC -20" / "Accuracy -10" lowers the monster's AC / accuracy by that much, and the result is **not** floored at 0.
- **Below-zero values matter:** a monster pushed below zero accuracy can't land a hit; below-zero AC makes it trivially hit (your to-hit benefits a lot).
- **Same ability codes as the equip/buff resolvers:** AC 2/10, DR 7 [stored ×10], Dodge 34, Accuracy 22/105/106. Debuff values are stored signed ("-20"), so the fold takes the magnitude.
- **Client use:**
  - Modelled only in Monster Intel's "Apply Debuffs" WHAT-IF (`MonsterDebuffCalculator` + the `MonsterMatchupCalculator` monster-swing model) — the app does NOT apply debuff stat effects during live combat; this is a preview.

### Condition Effects flags derive from the linked spell's ability codes
*Status: CONFIRMED 2026-09-04 (user + game-data) · Realm: both (Paradigm 1.9.1 / Stock 1.11.p)*

- **A Messages record's Effects flags (Blinded / Confused / Poisoned / MovementPrevented) come from the linked Spell's `Abil-N` ability codes.** Everything that inflicts a condition (monster cast, item proc, player spell) routes through a Spell record, so the spell's codes are the authoritative source. Ability code → flag:

| Abil code | name | Effects flag | example spell |
|---|---|---|---|
| **19** | Poison | **Poisoned** | savagely bites #81 (direct); poison bolt #35 → **EndCast** → poison bite #1366 (chain) |
| **107** | BlindUser | **Blinded** | blind book #200, blind #77, sand blind #539 |
| **74** | HoldPerson | **MovementPrevented** | hold person #66 — "can't move, can still act" (Movement only, NOT Attack) |
| **71** | Confusion | **Confused** | rose book confuse #199, convulsions #951, beholder death #1111 |

- **Poison follows the `EndCast` (151) cast-chain** — a damage spell (poison bolt) does its damage then EndCasts the actual DoT (poison bite), which carries the `19`. Only EndCast is followed for condition-inflict; `GiveTempSpell` (160) GRANTS a castable spell to the caster (its code confuses only when the player later casts it), so it is NOT followed.
- **`Confusion` (71) is shared with sleep / stun / paralyze** — those are code-71 too (paralyze = `71` +100 = fumble 100% = full incapacitation). They carry NO distinct hold code; their "can't move OR attack" is a manual Movement+Attack-prevented classification. So Confused is added from code 71 **only when the record isn't already Movement/Attack-prevented**, keeping the hand-authored full-holds as hold. In the data the *paralyzed* condition spells (#327, Paradigm #5110) are these code-71 +100 records, while the paralyze / hold-person **spells** carry 74 (HoldPerson) — see *Ailment identification — which spells cause disease / poison / blind / hold*.
- **`LastActionFailed` is for spell/item-use FAILURES, not confusion** — a record that ends up Confused must not also carry it (stripped in the re-derivation).
- **Mute (`76`, prevents casting) has no Effects flag and is out of scope** (rare, PVP-only on a learned spell).
- **Diseased is monster/trap-inflicted with no spell code** — left hand-authored. The seed's Diseased flags are set by hand from the *cure disease* / *cure major disease* RemovesSpell union, and the runtime (`AppServices.DiseaseApplySpellNumbers`) reads them from the seed — see *Ailment identification — which spells cause disease / poison / blind / hold*.
- **Client use:**
  - The flags are recomputed **additively** (add what the codes prove, never strip a hand-authored flag except the LastActionFailed-on-confuse cleanup).
  - `SelfAilmentChipResponder` / `PartyAilmentTracker` / `ConditionTracker` all read these flags, so a missing flag silently breaks ailment + confuse-fumble recognition (the reason rose book #199 was invisible).

### Confusion is a single state — generic onset and wear-off lines
*Status: CONFIRMED 2026-07-14 (user); single-state clear CONFIRMED 2026-07-28 (report `paradigm-20260728-173036`); five message forms CONFIRMED 2026-09-02 (user)*

- **`The effects of confusion wear off!` is a shared, generic wear-off** reused by many different confusion sources — a lot of confusion spells and monster effects emit the same line. The onset `You are confused!` is likewise generic. So from the wire alone the client cannot tell *which* of those confusions is on the character: most confusion sources share the generic onset, and a single wear-off line ends it.
- **Most confusion sources share the generic onset; some (convulsions `You are in convulsions!`, form of the monkey) have their own onset and wear-off** — see *Convulsions — custom fumble line, repeated move fumbles, seed onset fix* and *Form of the monkey — self-buff that confuses the caster*.
- **A few effects append their own specific wear-off** (e.g. `The effect of hypnotic hands wears off.`) rather than the generic line, but they still share the generic `You are confused!` onset. They are therefore not independently distinguishable from text alone.
- **Records that share an applied line are aliases of one effect and must be cleared as a group** — when any of them wears off, all of them end. Keying each record's clear solely to its own end text strands the flag whenever a sibling with a specific wear-off never sees its matching line. *(2026-07-14)*
- **Any confusion wear-off clears confusion entirely** *(CONFIRMED 2026-07-28, report `paradigm-20260728-173036`)*. More than one record can hold `Confused` at once: a specific source (a monster confuse spell with its own wear-off line, matched by a user-defined message entry incl. caster/target/witness cases) plus the generic `fumble` record, which also carries `Confused` so a confuse whose *set*-line was missed is still recognised. When ANY real confusion wears off you are no longer confused, so **every** latched `Confused` source clears — not just the record whose wear-off fired. Clearing only the wearing-off record (or its applied-line aliases) strands the flag: a death-dog shriek that wore off left the co-latched `fumble` still holding `Confused`, keeping the nav ConfusionGate stuck.
- **A confuse spell surfaces up to FIVE distinct message forms** *(CONFIRMED 2026-09-02, user)*. From the target's point of view: (1) a caster→you cast line, (2) a third-party witness line, (3) an **applied** onset, (4) a **wear-off**, and (5) a per-action **fumble**. The applied/wear-off pair sets and clears the `Confused` state; the fumble line (carrying `LastActionFailed`) fires on each swallowed command and has no wear-off pairing of its own — it clears with the effect via the single-state confusion clear. (The catalogue's `fumble` RECORD does list an end text, but that is just the generic confusion wear-off — see *Confusion fumbles — actions fail and must be re-sent*.)
- **Client use:**
  - `ConditionTracker`'s applied-line alias group-clear implements the shared-applied-line rule.
  - `ConditionTracker` clears every active `Confused` record when a `Confused`-carrying record's wear-off matches.
  - The parked game-data remodel plans to fold the fumble wordings into a shared table, like monster prefixes, plus an "is a confuse spell" checkbox on the message record — so a confuse record then needs only the standard spell lines, not a hand-built fumble entry.

### Confusion fumbles — actions fail and must be re-sent
*Status: CONFIRMED 2026-07-14 (user; report `paradigm-20260714-093614`); any-action + move revert CONFIRMED 2026-09-01 (user + report `paradigm-20260901-080223`); re-send of every client command CONFIRMED 2026-09-09 (user + report `paradigm-20260908-211659`)*

- **Confusion does not block attacking (or acting) outright — each action you send can fumble.** The game consumes the command and it does **not execute** — surfaced as `You fumble in confusion!` (self) / `<name> fumbles about dazedly!` (others). The catalogue's `fumble` record carries the `LastActionFailed` flag (and `Confused`), and the record's end text is the generic confusion wear-off `The effects of confusion wear off` — the fumble line itself has no wear-off pairing.
- **A fumbled action is lost; to actually perform it you must re-send the same action.** Confusion can fumble several actions in a row; how many depends on the severity of the confusion.
- **A fumble can prevent ANY action, not just combat, and the fumble line can be customized per confuse source** *(2026-09-01)*. Most confusion sources surface the generic `You fumble in confusion!`; `convulsions` customizes it (see *Convulsions — custom fumble line, repeated move fumbles, seed onset fix*). Either way the just-sent command is consumed and never executes.
- **The fumble line always appears as the direct reply to the command it swallowed**, never as unprompted ambient text *(2026-09-01)*.
- **A fumbled move has to REVERT its pending step or the tracker strands** *(2026-09-01)* — the unreverted move got wrongly matched against later unrelated text and stranded a tier-3 recovery backtrack indefinitely (no timeout watched its landing).
- **Implication for auto-combat:** an attack command (`aa` / `a`) that fumbles is consumed without hitting, so the engine must **re-issue its last attack** when it sees a fumble rather than assume the swing landed. Otherwise the monster goes unattacked until the user manually re-sends — the reported symptom of "monsters in room but not attacking unless I manually send attack commands" (report `paradigm-20260714-093614`).
- **The re-send covers EVERY client-sent command, not just a weapon swing — but only the CLIENT's own commands, never what the user typed** *(2026-09-09)*. A fumble eats whatever command was just sent; to perform it you re-send the same command. The client does this generically for anything IT sent — weapon swing, attack spell (immediately, not deferred to the next round tick), item use, door bash, and so on — because a user fighting confused shouldn't have to hand-repeat each eaten action. A command the **user typed** is never auto-repeated (re-sending a manual command is the user's call).
- **Client use:**
  - `MovementRefusalDetector` recognizes the fumble lines as movement refusals, reverting the pending move immediately. The lines aren't hardcoded — they come from each Confused MessageRecord's `ConfuseFumbleLine`, checked via `ConditionTracker.IsConfuseFumbleLine`.
  - Every fumble fires `ConditionTracker.ActionFailed` (on a `LastActionFailed` record OR a `ConfuseFumbleLine` match). The handler calls `CombatManager.OnActionFailed` first (re-sends a weapon swing WITH its engage-verification bookkeeping, returns whether it did), and on false falls through to `EngineSendGate.ReplayLastClientCommand`, which re-sends the last command that passed through the engine send gate.
  - User-typed input bypasses that gate (it flows straight to `SendUserInput`), so it's never in the replay buffer.
  - A bare **movement** step is skipped by the replay (the fumbled-move revert already recovers it, and a second send would double-step).

### Convulsions — custom fumble line, repeated move fumbles, seed onset fix
*Status: CONFIRMED 2026-09-01 (user + report `paradigm-20260901-080223`); CONFIRMED 2026-09-02 (report `paradigm-20260902-113201`); CONFIRMED 2026-09-05 (report `paradigm-20260905-183956`); third fumble wording NEEDS CONFIRMATION 2026-09-02*

- **`convulsions` customizes the fumble line to `You convulse violently!` (with its own onset `You are in convulsions!`)** *(CONFIRMED 2026-09-01)*. Its wear-off (`AppliedEndsWith`) is `Your body returns to normal.`.
- **Convulsions can fumble several consecutive moves in a row, well inside a handful of seconds** *(CONFIRMED 2026-09-02, report `paradigm-20260902-113201`)*. The per-move revert is correct, but `LoopRunner`'s bounded recovery budget (3 attempts) was shared between genuine desyncs and these fumbles — three convulsion bonks on the same room burned the whole budget in under 10 seconds and permanently failed the loop, leaving the character standing there Confused with nothing left running.
- **The correct onset is `You are in convulsions!`** *(CONFIRMED 2026-09-05, report `paradigm-20260905-183956`)* — confirmed live in both this report and `paradigm-20260901-080223`. The shipped `convulsions` message record's `AppliedMessage` was wired to the wrong line, so the 2026-09-02 recovery-budget exemption never actually engaged for a real convulsions episode:
  - Both bundled seeds (`Messages.paradigm.seed.json` and `Messages.stock.seed.json`) had `AppliedMessage: "You look around stupidly and do nothing!"` on the merged `convulsions` record — that's one of the fumble wordings (correctly still listed in `ConfuseFumbleLine`), not the condition's own onset line.
  - Since that fumble text never actually appears as ambient onset text in a session, the record's `AppliedMessage` never matched, `ConditionTracker` never added it to `_active`, and `IsConfused` stayed false for the whole convulsions duration — so `LoopRunner.EnterRecovery` charged every fumble-caused block against `MaxRecoverAttempts` same as a real desync, burning the budget in under 20 seconds and permanently failing the loop.
  - The onset had previously been found and hand-corrected by the user in a per-set `messages.json` override that predates the realm-flavored split — a one-time legacy-messages migration (since removed; historically `DataMigration.RetireLegacyMessagesOnce`) retired that override to `.bak` during the split since the shipped seed never carried the same correction, silently reintroducing the bug.
- **`convulsions` may have a THIRD fumble wording — `You look around stupidly and do nothing!`** *([NEEDS CONFIRMATION] 2026-09-02, cross-referenced from a messages.md export, not a live bug report)*, alongside the generic fumble and its own `You convulse violently!`, flagged `LastActionFailed` in the source data. Not yet confirmed against a live session; treat as provisional until it's actually observed. The 2026-09-05 onset-fix note calls it "one of the fumble wordings" only because the client lists it in `ConfuseFumbleLine` — it still hasn't been seen live.
- **Client use:**
  - `MovementRefusalDetector` recognizes BOTH `You fumble in confusion!` and `You convulse violently!` as movement refusals, reverting the pending move immediately; it also recognizes `You look around stupidly and do nothing!` (same revert mechanic), so if that wording does turn out to be real, a move fumbled this way won't strand the tracker. These lines are no longer hardcoded in the detector — they come from each Confused MessageRecord's `ConfuseFumbleLine` via `ConditionTracker.IsConfuseFumbleLine`.
  - `LoopRunner.EnterRecovery` reads `ConditionTracker.IsConfused` (wired via `SetConfusedCheck`) and doesn't charge an attempt against `MaxRecoverAttempts` while it's true — the reroute/resend still happens every time, it just isn't bounded by the same budget a real mapping problem is.
  - Both bundled seeds' `convulsions` record now has `AppliedMessage: "You are in convulsions!"`; `AppliedEndsWith` (`Your body returns to normal.`) was already correct. No code change — `ConditionTracker` and `LoopRunner` already behaved exactly as designed once fed the right onset text.

### Form of the monkey — self-buff that confuses the caster
*Status: CONFIRMED 2026-09-02 (user)*

- **`form of the monkey` inherently confuses the caster for the buff's whole duration.** While the self-buff is up (onset `The spirit of the monkey inhabits your body!`), each action has a chance to fumble as `You are distracted!`.
- **There is no separate wear-off for the confusion** — it ends when the **form itself** wears off (`The spirit of the monkey has left your body!`). **Client encoding:** the `form of the monkey` message record carries `Confused` (set on the form's onset, cleared on the form's wear-off, which triggers the single-state confusion clear); the separate `You are distracted!` record carries `LastActionFailed` (+ `Confused` as the missed-onset fallback) to drive the per-action re-send.

### Knockdown — a movement-preventing hold
*Status: CONFIRMED (report `paradigm-20260714-002413`)*

- **A knockdown is a movement-preventing (held) status.** The hit lands as `You are knocked off your feet, and land with a heavy thump!` (third-person `{s} is knocked flat!`).
- **While down, the standing status is `You are flat on your back!`** — which is also what the server prints as the **move refusal** when you try to walk while knocked down (a bonk, no room redisplay).
- **It clears with `You get back on your feet.`**.
- **Client use:**
  - `MovementRefusalDetector` matches the `You are flat on your back!` move refusal.
  - The applied/clear pair maps to the `MovementPrevented` flag, so the local hold (`SelfHeldResponder` → `HeldGate`) holds our own loop for the duration exactly as a confused leader's does. A held leader or solo character has no leader to send `.@held` to, so the local `HeldGate` alone pauses the loop.

---

## Monsters, lairs & spawns

How monsters come into a room (lair groups, `NPC` placements, bosses, death-summons, room-spell and command summons), how often they respawn, what they are worth, and how their arrival and departure lines look.

### Lair respawn timers

*Status: CONFIRMED 2026-08-02 (user)*

Two distinct spawn mechanisms exist (lair mobs here, NPC-placed mobs in *NPC-placed monsters*), and they respawn on completely different rules. This matters directly for exp/hr estimation of a loop (how fast a lair refills vs how fast you can lap it).

- **Respawn time `T`.** A room's `Lair` group spawns N monsters with a respawn time `T`. **Lair timers are calculated differently from other timers** *([CONFIRMED] 2026-09-26, user)*: `T` comes from the room's `Delay` field, in set 30-second steps — `(Delay − 1)` minutes + 30 s — and the map's lair timer shows it correctly; that is the value to use for how long a lair takes to respawn. When a room has no `Delay`, `LairTimerStore` falls back to the lair's `AvgDelay` (stock exports it in **minutes**, Paradigm/GreaterMUD differs), else the slowest member's `RegenTime`. (A monster's own `RegenTime` works the same way for every monster — hours, as *Boss monsters* records — but it isn't the lair clock.)
- **The clock is per monster slot, keyed to each kill.** Each monster slot in a lair carries its own independent respawn clock of length `T`, started at the moment *that* monster was last killed — not a shared lair clock, and separate from every other lair, even lairs with the same monster type and size. In a 3-mob, 60 s lair: a mob killed at t=0 is killable again at t=60; one killed at t=10 is back at t=70. They come back **staggered**. So a dense multi-lair loop desynchronises: with enough slots there's almost always one ready, and a single-target loop runs pinned at the 720/hr tick cap (the *single-target* ceiling — rooming runs above it; see *Timing & rounds → Exp/hour ceiling and loop geometry*) rather than clearing everything then idling for a synchronised repop.
- **Being early does not make it spawn.** To re-kill a mob you must be in the room at or after `(its last kill + T)`. Arriving earlier, it simply isn't there yet.
- **Loop consequence.** A lap that returns to a lair with period `P ≥ T` finds it fully respawned (full mobs that lap); `P < T` laps into a partial/empty room. So a lair's sustainable exp rate is capped at `mobs × exp ÷ T` regardless of how fast you loop — the "respawn-limited" regime — while a loop long enough that `P ≥ T` is "travel/kill-limited." The real rate per lair ≈ `min(the two)`.

### NPC-placed monsters

*Status: CONFIRMED 2026-08-02 (user) · Realm: Stock (verified examples)*

- **NPC-placed mobs regenerate on entry — effectively no respawn cap.** A monster placed via the room's **`NPC`** field (a fixture, distinct from a `Lair` group) with `RegenTime` 0-ish **regenerates the moment you (re-)enter the room after killing it** — no timer to wait out. These are the classic "rooming" targets (kill as fast as you can fight; bounded by kill speed, not respawn).
- **Verified stock examples:** slime beast `1/1765` (`NPC=57`, `RegenTime 0`, 250 xp); cave worm `1/866` (`NPC=8`, `RegenTime 0`, 100 xp); barmaid `1/311` (`NPC=248`, `RegenTime 1`, **0 xp** — an evil-points target, not exp). Her regen timer follows the same mechanic as every other monster's, but she is also the room's placed `NPC`, so kill her, walk out and back in, and she is there again at once *([CONFIRMED] 2026-09-26, user)*. With `GameLimit 5` she is **not** a boss (see *Boss monsters*), so her placement respawns instantly like any other fixture. **Client use:** `BossCatalog.IsBoss` is `GameLimit == 1`, shared by `RouteExpResolver`, so she counts as an instant fixture.
- **A room can carry both an NPC fixture and a `Lair` group** (cave-worm room `1/866` has `NPC=8` plus a lair), so a room's yield is the sum of its NPC target(s) + its lair contribution.
- **In a loop, an instant mob still yields only once per lap** (bounded by lap time); only a stay-in-room **rooming** setup kills it every round.
- **Exception — bosses:** a placed monster that qualifies as a boss is *not* instant; see *Boss monsters*.

### `Summoned By` spawn tokens

*Status: CONFIRMED 2026-08-19 (data cross-ref)*

A monster's `Summoned By` field lists the rooms it appears in, each token tagged by *how* it spawns there — three room-reference token kinds. Verified by the room side: a `Group(lair)` token always points at a room **with** a `Lair` tag, a `Group:` token at one **without**.

- **`Room m/r`** — the room's `NPC` fixture: a **placed** boss / unique (the mechanic in *NPC-placed monsters*). Nav tooltip labels these **`Placed:`**.
- **`Group: m/r`** (no `(lair)`) — an **assigned** roam / rare-random spawn; the room carries no `Lair` tag for it. Tooltip label **`Assigned:`**.
- **`Group(lair): m/r`** — a **lair** spawn; the room's `Lair` tag lists the same monster. Tooltip label **`Lair:`** (sourced from the room's own `Lair` tag, which also carries the `(Max N)` simultaneous cap).
- **One monster can carry more than one kind for the same room** — a placed boss that also has a `Group:` roam token, e.g. Aiken `1/398` has both `Room 1/398` and `Group: 1/398` — so the three tooltip lines may legitimately repeat a name.
- **Client use:**
  - The nav tooltip / Room Info panel split these into Placed / Assigned / Lair.
  - `MonsterSpawnIndex` parses the token kinds, while the combat resolver keeps a permissive union of all of them.

### Boss monsters

*Status: CONFIRMED 2026-08-03 (user + reports `paradigm-20260803-035136`, `-094657`)*

- **A boss is a game-limited singleton: `GameLimit` exactly 1** (only one exists in the game at a time) *([CONFIRMED] 2026-09-26, user — `GameLimit` = 1, not ≥; a long `RegenTime` does not by itself make a boss. An earlier rule here also counted any monster with `RegenTime` ≥ 1 hour.)* — and this holds whether it's a **lair** member OR a **placed** monster (the room's `NPC` field).
- **A boss can be killed only as often as its regen timer.**
  - The crowned spider (`Number 929`, lair, `GameLimit 1`, `RegenTime 15`) is killable **once per 15 hours** and can spawn in **any** room of a multi-room lair.
  - The animated juggernaut (`Number 1211`, placed in `17/7055`, `GameLimit 1`, `RegenTime 3`, 1,300,000 exp) is killable **once per 3 hours**.
- **A placed boss is NOT an instant fixture.** Normal `NPC`-placed monsters regenerate on entry (instant, every pass), but a *boss* placed monster is still gated by its regen, so it's amortised exactly like a lair boss, not grabbed every lap.
- **A boss's `RegenTime` is in HOURS** (the Game-Data browser renders it "15 hour") — the same unit as every monster's `RegenTime` (see *Lair respawn timers*); a lair's `AvgDelay` is a separate field.
- **Exp/hr estimation of a boss:** pull it OUT of its lair's per-mob average and add its amortised contribution **`boss exp ÷ regen-hours`, counted once** for the whole loop (a single time no matter how many rooms it can appear in) — `1,200,000 ÷ 15 = 80,000/hr`, not `1.2M` per lap in every room. The regular (non-boss) lair mobs still fire per-room on the room delay.

### Monster exp multiplier

*Status: CONFIRMED 2026-08-03 (user + reports `paradigm-20260803-035136`, `-094657`)*

- **True monster exp is `EXP × ExpMulti`.** The Monsters table stores a base `EXP` and a separate `ExpMulti` (the boss/exp multiplier); crowned spider `EXP 60000 × ExpMulti 20 = 1,200,000`.
- **Read them together.** The Game-Data browser already shows "60,000 (×20)"; `EXP` alone under-reports a multiplied monster.

### Cleanup-only boss respawns

*Status: CONFIRMED 2026-08-04 (user)*

- **Some bosses reset only at nightly server cleanup.** A subset of bosses (in the boss-timer seed: Lord Feyr, Iceforge, Huge Sandstone Sphinx, Mammoth Stone Scorpion, Bogwood Box) do **not** respawn on a kill-based countdown. They reset **only at the BBS's nightly server cleanup**, a fixed daily wall-clock time (per board — e.g. **2100 Pacific**).
- **Once killed they stay dead until the next cleanup instant**, then return.
- **Client use:**
  - The client models this as a DEAD / ALIVE state keyed to the per-BBS cleanup time, not a duration timer.

### Death-summon cascades

*Status: CONFIRMED 2026-08-03 (user + game-data trace, Paradigm 1.9.1) · Realm: Paradigm*

Some monsters spawn **more monsters when they die**, and those can summon in turn — the Zombie Pen (`17/2601`) is the canonical case, built for AoE ("rooming") crowds.

- **The mechanic is `DeathSpell` → summon slots.** A monster's **`DeathSpell`** field names a spell (`0` = none). In the Spells table that spell has ten ability slots `Abil-0..Abil-9` with values in `AbilVal-0..AbilVal-9`; a slot whose **`Abil` value is `12`** ("summon monster") summons the monster **`Number`** in the matching `AbilVal`.
- **Only `Abil == 12` slots summon.** A spell row carries unrelated payloads in its other `AbilVal` slots, so filtering on `Abil == 12` is required.
- **It recurses, and the data terminates it.** Each summoned monster has its own `DeathSpell`, so the chain continues until a tier whose members have `DeathSpell 0`. Real chains are shallow (≤3 tiers); follow the data, don't assume a fixed tier count.
- **A room holds at most 20 monsters at once, and a summon cast that would exceed it fails whole.** The engine caps a room at **20** living monsters. A dying monster's `DeathSpell` fires as **one atomic cast**: it lands only if *all* its summons fit under the cap — otherwise the whole cast **fails to cast, is never retried, and is permanently lost** (its summons do NOT appear on a later round when space frees up).
  - So a wave fills with whole casts until the next won't fit, then drops the rest: e.g. 15 monsters each summoning 2 want 30, but only 20 spawn (10 whole casts) — the other 5 casts fail outright.
  - A fan-out room is therefore worth far less than the raw tree would suggest. (The Zombie Pen peaks at 15 with `Max 3` zombies, so its cap never bites; a higher `Max` or a wider fan-out would.)
- **Worked example — stitched zombie (`Number 1220`, `EXP 4000`, `DeathSpell 1032`):**
  - Spell `1032` (Abil-0/1 = 12) → **severed waist `881`** + **severed torso `888`**.
  - Waist `881` (`DeathSpell 1034`) → 2× **severed leg `889`** (3500 each).
  - Torso `888` (`DeathSpell 1036`) → 2× **severed arm `890`** (3000) + **severed head `891`** (3500).
  - Legs/arms/head have `DeathSpell 0` → terminal. **One stitched zombie = 8 monsters, 28,500 exp** (not the 4,000 its own `EXP` shows). Note each part summons via a **different** spell.
- **Exp/hr estimation of a summoner:** simulate the room tier by tier under the 20-cap and count the monsters that actually spawn.
  - Its effective yield is the **whole (capped) tree's exp** (base + every descendant — `28,500`, not `4,000`, for one stitched zombie).
  - The summons also cost combat time: **single-target** fights every monster the room becomes (kill count = tree size, `8` per zombie), while **AoE/rooming** clears one tier per pass (waves = tree depth, `3`).
  - So a death-summon room yields far more than its face value, but the extra kill/wave time — and the cap on huge fan-outs — keep it below the naive exp-ratio multiple.
  - Bosses are left on their base exp (their death-summon, if any, is not folded — a rare edge, and boss exp is already a flat amortised approximation).

### Room-spell monster summons

*Status: CONFIRMED 2026-08-06 (user + game-data trace, Paradigm 1.9.1); cadence CONFIRMED 2026-09-08 (user); estimator model = user design, revised 2026-09-08 · Realm: both (cadence differs)*

Distinct from a monster's death-summon: a **room itself** can summon monsters via its entry spell. `Rooms.Spell` names a `Spells` row cast **on room entry and re-cast on a recurring tick** while you're in the room. These are the monster-spawning rooms that made the Exp/Hr estimator under-report: the exp comes from summons the route resolver never counted.

- **Re-roll cadence differs by realm** *([CONFIRMED] 2026-09-08, user)*.
  - **Paradigm** re-casts the room spell **every combat tick (~5s)** while you're present (the user timed it at **~5.3s**, firing just after entry) plus the entry cast.
  - **Stock** re-rolls on a slower **"medium tick" of 6 seconds** (per the `wccmmud.dll` disassembly) plus on room change — so a Stock summoning room yields fewer rolls over the same fight than a Paradigm one.
  - The estimator encodes the 6s value as `StockMediumTickSeconds`.
- **The summon lives in a TextBlock, not an `Abil 12` slot.** The room spell carries a **`TextBlock` ability (`Abil == 148`)** whose `AbilVal` is a **TBInfo `Number`**. The TBInfo `Action` string is the roll table — so a room-summon is *not* found by scanning `Abil == 12` (that's the death-summon path).
- **`nomonsters:` gate.** A leading `nomonsters:` condition means the spell **only fires when the room holds no monsters** — so it can't stack summons: kill what's there, and the next tick may summon one.
- **d100 roll table with cumulative bands.** Each `Action` line is `<cumulativeThreshold>:<act>[:<act>…]`; a line's probability is `(threshold − prevThreshold) / topThreshold` (top is usually 100). A line whose actions include **`summon <monsterNumber>`** summons that monster (from the Monsters table, normal exp). Other actions (`addevil`, `message N`) are misses.
- **Worked example — "crypt summon 2" (`Rooms.Spell 5248`, e.g. room `13/3573`):** `Abil-0 = 148`, `AbilVal-0 = 3411`.
  - TBInfo `3411`: `nomonsters:random 3412` → gate + redirect to the roll table `3412`.
  - TBInfo `3412`: `60:addevil 0` (1–60, nothing) · `85:message 4064` (61–85, message) · `90:…:summon 2111` (86–90, **cairn wraith** 13000) · `95:…:summon 2119` (91–95, **ogre skeleton** 12000) · `100:…:summon 2122` (96–100, **zombie warrior** 12000).
  - → **15% summon chance**, **1,850 expected exp per roll** (`0.05 × (13000+12000+12000)`).
- **Estimator model** *(user design, revised 2026-09-08)*: credit each summoning room `ExpPerRoll × fires` per visit, where `ExpPerRoll = Σ band% × monster exp` and `fires` follows the cadence + gate:
  - **Ungated spell** — rolls regardless of occupancy: `fires = 1 (entry/room-change) + roundsInRoom`, where `roundsInRoom` = combat rounds spent fighting base mobs here ÷ the realm's room-spell tick (Paradigm = combat round, Stock = medium tick).
  - **Gated spell (`nomonsters:`)** — only summons while the room is empty, so a pass-through visit credits **1** fire when the room was empty on arrival and **0** when you arrive to a full lair; combat length adds no fires.
  - Summon mobs are never *killed* by the estimator (a room-attached spell never kills NPCs, and no feedback is modeled) — `RoundsPerMob` (the clear-rate knob) stays realm-agnostic and the user sets it directly.
- **Client use:**
  - Lives in `LoopExpSimulator.SummonFires`.

### Summoned monster key drop

*Status: Unrated · Realm: Paradigm 1.9.1 (worked example)*

- **A conjured monster drops loot exactly like a lair-spawned one.** Being conjured rather than lair-spawned changes nothing about the drop: the item lands loose on the ground, isn't announced on the death line, and has to be re-surveyed (`look`) before anything can see it, then `get`-ed by name — exactly the rule in *Items, inventory & equipment → Monster drops land on the ground* (the worked chain is also in *Movement & navigation → Route gate items — crossing vs acquiring, required vs optional, reliable vs unreliable*).
- **Worked example (Paradigm 1.9.1):** room 8/461 "Black Steel Gate" carries `CMD 863` = `touch statue:summon 347` / `move statue:summon 347`; monster 347 "obsidian statue" is `GameLimit 1`, `Summoned By: Textblock #863`, and carries `DropItem-0: 806` (gate key) at `DropItem%-0: 100`. That key opens 8/461's south exit (`Key: 806 [or 101 picklocks]`).
- **This is the shape that makes a key worth routing for** — the spawn is on demand and the drop is certain, so the whole chain is deterministic, unlike a lair key such as the black star key (item 172, dropped at 1–10% by lair-spawned cultists) which can never be relied on mid-route.
- **Re-typing the summon keyword is *not* a known way to force a second monster** while one is already up.

### Monster movement lines

*Status: CONFIRMED 2026-09-09 (user) for yellow-indexed arrival names; CONFIRMED 2026-09-24 (user + contributor capture, PR #690) for generic movement lines*

**Arrival lines carry a yellow-indexed name** *([CONFIRMED] 2026-09-09, user)*

- **Arrival wording is per-monster and NOT exported.** A monster entering the room prints an arrival line; it can be the structured `"<name> <verb> into the room from <dir>."` form OR an arbitrary custom line with no such marker (e.g. `"A muckworm darts out of the mud!"`).
- **Constant across every monster: the name is tagged the yellow ANSI colour index** (standard 3 / bright 11) — sometimes just the name, sometimes the whole line.
  - Yellow is not strictly monster-only: the player sneak-notice line (`You notice <name> sneaking in from the <dir>.`) may also be painted yellow (see *Movement & navigation → Observing another player's failed sneak into your room*).
- **This is an index, not a rendered colour.** The user confirmed the arrival name's yellow index is **not remapped or stripped by the client palette** — a custom palette only changes how index 3 *looks*, not that the cell carries index 3. So the index is the one palette-stable signal.
- **Do NOT record a colour→line-type table as game truth.** Other lines' rendered colours (the `You notice` floor line, the `Also here:` roster, prompts, room descriptions) **vary by the user's palette** and are not reliable identifiers. Only the yellow *index* on an arrival name is dependable; a plain room description that happens to name a monster is default-colour (no yellow index), so it isn't an arrival.

**Generic movement lines for a monster with no sentence of its own** *([CONFIRMED] 2026-09-24, user + contributor capture, PR #690)*

- **Arrival, compass direction:** `"<mob> moves into the from the <dir>."` — the server really drops the word **"room"** on this one form (e.g. `bugbear captain moves into the from the northeast.`), while the vertical siblings are complete: `"… moves into the room from above."` / `"… from below."`.
- **Arrival, directionless:** `"<mob> just arrived from <dir|nowhere>."`.
- **Departure:** `"<mob> just left to the <dir>."`, and for the vertical pair the direction is an adverb with no "to the": `"… just left upwards."` / `"… just left downwards."` (players leave with the same wording — `Fujin just left upwards.` is in our own captures).

- **Client use:**
  - `RoomEntryWatcher.OnLineScan` recognizes the unstructured custom spawns the two regex patterns (`RoomEntryArrival` and `RoomSpawnArrival`, in `DefaultPatterns.cs`) miss — on a line no structured pattern claims, a run of fully-yellow-indexed words that resolves to a known monster (not already in the room) is appended as an arrival, tripping the combat gate a round before the spawn's first swing. Keys on the palette index (3/11), never the RGB.
  - `RoomEntryArrival` / `RoomEntryDeparture` match the generic lines; the "just arrived" / "just left" branches require a real direction word so chat of the same shape ("I just left downtown.") stays out.
  - Missing the compass arrival was permanent in a **dark** room, which never re-displays an `Also here:` to correct the roster, so auto-combat never engaged the monster.

---

## Movement & navigation

How moves, bonks, dark/blind rooms, light, stealth, doors, gates, teleports, ferries and room-entry hazards behave on the wire and in the game data, and how the client's tracker, walker and router rely on each rule.

### Movement on the wire — command echo, failure lines, and what actually moves you
*Status: CONFIRMED 2026-09-10 (user wire captures `stock-20260910-21{1620,1747,1931,2008}` + `paradigm-20260910-21{2717,2938}`) · Realm: both*

- **In character mode the server echoes the typed command on the prompt line before its result**, e.g. `[HP=91/KAI=5]:e` then the new room display. This holds on stock and Paradigm (`[HP=../MA=..]`/`[HP=../KAI=..]`).
- **The echo's placement varies** — inline (`]:w`), on its own line (`w`), or doubled.
- **A display can arrive well after the move that caused it** — combat / spell / event lines and even other commands' echoes can interleave between a move's echo and its landing room display.
- **Every room display has an identifiable cause on the wire:**
  - **Move succeeded** — the move's echo, then a NEW room display. This is the only case that should advance position.
  - **Move failed, stayed put** — the move's echo, then a failure line, no new room: `There is no exit in that direction!` (wall), `The gate is closed!`, `The door is closed.` / `The door is Closed!`, or `You are typing too quickly - command ignored` (rate-limiter drop — common in fast command bursts; the command did NOT execute). These mean the move never happened.
  - **`look` / `look <dir>`** — a `look` echo, then a room display that is the *same* room or an *adjacent* room (peek). Never a move. A shut door/gate blocks the peek entirely (see *`look <dir>` — peeking an adjacent room*: the server answers *"The door is closed in that direction!"* and renders no room). (Directional peeks are already suppressed via the outbound `NoteLookSent` path.)
  - **Spontaneous re-display** — NO command echo. An NPC self-moving (`X moves into the room from the <dir>` / `just left to the <dir>` — see *Monsters, lairs & spawns → Monster movement lines* for the exact captured forms) or a regen/KAI tick re-renders the CURRENT room. This is what fools a naive tracker into a phantom move. (A refused move never redisplays the room — see *Refused ("bonked") moves*.)
- **Moves with NO command echo (forced moves) — the complete set:**
  - **Party follow / drag** — a follower moves because the leader did and sends no bytes; announced by `-- Following your Party leader <dir> --` (see *Party → Following the leader: follow / drag movement*).
    - A hidden/foliage text-exit drag moves the follower with **no** `-- Following …` line and no direction (see *Party → Following the leader: follow / drag movement*).
    - A dropped (0 HP) member being dragged sees `<Leader> is dragging you around.` on each of the dragger's moves (see *Party → Dropped ally rescue* and *Wire, prompt & command output → Message catalogue (lines the client parses)*).
  - **Fear** — the game shoves a feared character between cardinal-connected rooms with no echo and no per-move line (see *Spells, buffs & conditions → Fear*).
- **Moves that are *not* in that set:**
  - **`go`/teleport is a commanded, echoed text-exit move** (`go vortex`, `go man`, `go path`); it can land you in a distant unrelated room (`You step into the swirling vortex, and find yourself... elsewhere.`).
  - **There is no random "flee"** — "flee" is a user-sent directional run-away (commanded + echoed).
  - **No engine recall** — sys-goto resolves to a known destination room (goto table), and Paradigm teleport tokens are player-only (not engine-consumed; `rm` fixes position on Paradigm).

### Refused ("bonked") moves
*Status: CONFIRMED*

- **A refused move always prints an explicit line and never redisplays the room.** When a move command can't be honoured — no exit that way, a shut door, an impairment — the game emits a one-line refusal *instead of* a room display. The wording varies by the reason for the bonk, e.g.:
  - `There is no exit in that direction!`
  - `You can't go that way.` / `You can't move (in) that direction.` (the forms the client's refusal detector matches; an earlier note wrote `You can't move that way.`, never seen in a capture)
  - `The door is closed.` / `The door is Closed!` / `The gate is closed!` (the client matches these case-insensitively, ending in `.` or `!`)
  - impairment forms (paralyzed / confused / stunned / dazed / too encumbered / can't see well enough to move).
- **The player's on-screen room does not re-print on a refusal** — this is the authoritative signal the client keys on.
- **Corollary: a room redisplay that still matches the room you moved from is never the result of a refused move.** While a move is pending, seeing the source room again can only be a **passive re-look** — a combat-clear, a monster/player arrival or departure notice, a bare re-glance — carrying no position signal.
- **A genuine self-loop exit is a real move, not a passive redisplay:** a genuine self-loop exit that lands back in the same room is a real move with a real room display; it resolves as a normal predicted-neighbour match because the exit's target *is* the source, so it is not confused with a passive redisplay.

**Client use:**
- `MovementRefusalDetector` matches the refusal lines and calls `RoomTracker.NoteMoveBlocked` (which drops the pending move and re-confirms at the source).
- The tracker ignores a source-room redisplay while a move is pending and keeps waiting for the move's real outcome (a different room), rather than inferring a refusal from the redisplay alone.

### Per-hop movement speed
*Status: CONFIRMED 2026-07-22 (user) · Realm: both (realm-specific)*

- **Per-hop movement speed is realm-specific, and the two realms differ enough that no single fixed movement timer can be right for both.**
- **Paradigm paces each hop by a deterministic server formula (no lag term):**
  `hop_ms = max(1000, 1100 + enc² · 2000 − quickness · 10)`, where `enc` is the encumbrance fraction (0–1 of max carry) and `quickness` is total quickness.
  - There is a hard **1.0s cap — the fastest any hop can be.**
  - Time rises quadratically with encumbrance and falls linearly with quickness: a light, high-quickness build sits pinned at the 1.0s floor (quickness 100 stays capped until ~67% enc), while a heavy or low-quickness build ranges up toward ~3.1s/hop.
  - The server will not process a hop faster than this, so back-to-back move commands are throttled to it rather than executing instantly.
  - Cross-checked against the falls-below-cap points: quickness 15 → 16% enc, 100 → 67%, 200 → 97%.
- **Stock has no such floor.** Empirical captures (8 sessions, 199 moves) show a true-speed floor around **0.25s** unencumbered, medians ~0.6–0.7s at light/medium loads rising to ~1.65s when Heavy (≈67%+ enc), with wide lag-driven variance per hop. A comparable character therefore moves roughly **2–4× faster per hop on stock** than the Paradigm 1.0s cap.
- **Design consequence — the dark-room settle window (and any fixed inter-move timer) is realm-coupled.** At 1.0s it ≈ the Paradigm server cadence, so on Paradigm it costs almost nothing on an empty room; on stock the same 1.0s nearly doubles the natural ~0.6s hop — a heavy tax.
- **Overshoot (stepping before a dark pursuer reveals) is a fast-mover problem:** it only bites a character near the Paradigm cap or a quick stock character; a slow/heavy mover has ample reveal margin. This argues for making dark-room room-clear detection **event-driven** (step once the room is confirmed clear via the attack→"no effect" + combat-line-silence signals) rather than a single global duration.

### `rm` — authoritative position (Paradigm only)
*Status: CONFIRMED (capture 2026-07-12; reliability: user 2026-09-02, report paradigm-20260902-223159) · Realm: Paradigm*

- **On a ParaMud (Paradigm) realm, typing `rm` returns a fixed three-line block**, each label left-justified with the value padded to a column:
  ```
  Location:      1,1729
  Regen Time:      2m 30s
  Room Illu:      -100 (-100)
  ```
  - `Location: <map>,<room>` is the authoritative (map, room) — no guessing needed.
  - `Regen Time:` is a duration; `Room Illu:` an illumination pair `<n> (<n>)`.
  - The prompt returns immediately after.
- **`rm` does NOT exist on stock realms** — stock keeps relying on the heuristic position tracker.
- **`rm` is correct for followers too** — it reports the *player's own* position, so there is no leader/follower divergence.
- **`rm` is reliable — the ONLY way it fails to answer is a confusion fumble eating the command** *(CONFIRMED, user 2026-09-02)*. There's no game-side throttle or refusal; a `rm` that returns no `Location:` line means the send was consumed by a confusion fumble (same mechanic as a fumbled move / eaten `@wait`), not that the game declined.

**Client use:**
- The client keys on the `Location:` line to re-anchor `RoomTracker` via `SetLocated`; if that (map,room) isn't in the imported graph, `SetLocated` logs a warning and refuses rather than writing a stale anchor.
- On Paradigm the recovery gate treats an *unanswered* forced `rm` as "confused, try again" — re-asking until the confusion self-clears and a `Location:` lands — rather than a genuine failure.
- This is why the heuristic reverse-walk / "Lost" dialog should essentially never be reached on Paradigm: at every give-up boundary (before the backtrack, and again before Lost) the gate spends a forced `rm` first, and only a `rm` that *answers* with a room the loaded map set doesn't contain — not a fumble-eaten one — is a real dead end (report paradigm-20260902-223159, `EngineRecoveryGate.HandleResyncFailure`).

### Nav-recovery authoritative locate (`rm` / `sys st`)
*Status: Unrated · Realm: `rm` on Paradigm, `sys st` on stock with the sysop power*

- **The sysop `sys st` position locate mirrors the Paradigm `rm` re-anchor at every point `rm` fires** —
  first mismatch, engine stall, the tier-3 give-up ladder, the terminal pre-Lost shot, the no-engine
  drift gap, `@where`, and a blocked loop/replan.
- **On Paradigm `rm` wins each site (realm-gated); on a stock realm with the power `sys st` fills in.**
- **Maze-solve stays `rm`-only** — the solver drives its own relocalization.
- **Client use:**
  - Both share the gate's `NoteAuthoritativePosition` / `OnAuthoritativeResyncFailed` consumers.

### Dark rooms — no name, no exits, traversal inferred from no bonk
*Status: CONFIRMED*

- **A room too dark to see in replaces the *entire* room display** (name, `Obvious exits:`, `Also here:`) with a single line — `The room is very dark - you can't see anything.`, or in a considerably darker room `The room is pitch black...` (elided here; the full line is `The room is pitch black - you can't see anything`).
- **Every dark room emits the same line, so the line itself carries no position signal.**
- **Traversal is deducible from the absence of a bonk:** combined with the bonk rule (see *Refused ("bonked") moves*), once we send a move into the dark, **no bonk line means the move succeeded** — we advanced into the room the sent direction leads to.
- **Only the *very dark* / *pitch black* forms starve the display this way** — a normally-lit room always prints its name + exits.

**Client use:**
- The tracker keeps position by projecting the sent direction onto the current room's graph edge (`RoomTracker.NoteDarkRoomEntered`): when the pending move resolves to a known neighbour it advances there; when the edge is unmapped it holds the last position (stays Pending) rather than guessing.

### Moving while blind
*Status: CONFIRMED (capture 2026-07-11)*

- **A move made while *blinded* succeeds but starves the room display, printing only `You are blind.`** — same shape as the dark-room case, but it's the player who can't see, not the room. A blinded player who sends a move gets no name, no `Obvious exits:`, no description — just the single line `You are blind.` (period) — yet the move **traverses** (party followers are dragged in the sent direction).
- **Three lines mention blindness — distinguish them:**
  - the **onset** `You are blind!` (exclamation, applies the Blinded flag);
  - the **move-succeeded** `You are blind.` (period, starves the display);
  - the **refusal** `You can't see well enough to move.` (a bonk — the move did *not* happen, caught as an impairment refusal).
- **Verified from a live capture:** `:s` → `You are blind.` → `Suijin walks into the room from the north.` (the party followed south) with no room render; the map had frozen at the source room until this path landed.

**Client use:**
- Only the period form drives dead-reckoning: `RoomTracker.NoteBlindMove` advances along the pending move's mapped edge just like `NoteDarkRoomEntered`, but leaves `IsInDarkRoom` untouched (carried light can't cure blindness, and the dark-room attack-line combat path must not switch on).

### Light sources
*Status: CONFIRMED (verbs; bare-CR redisplay user 2026-08-11; visibility user 2026-07-24; burnout line capture 2026-07-11)*

- **`use <item>` readies a light (torch, lantern); `rem <item>` removes it.** Lights follow the same trade-places rule as `eq` — `use`-ing a new light swaps out the current one (if usable).
- **`use <light>` only grants the ability to see — it does NOT re-display the room** *(CONFIRMED, user 2026-08-11)*. After lighting in a dark room you must send a **bare carriage return** to redisplay:
  - if the light now lets you see, the full room prints (name / exits / `Also here:`, revealing any monsters that were standing there unseen);
  - if it's still too dark, the "can't see" dark message prints again.
- **The bare CR is the ONLY way to discover a *standing* (non-attacking) monster in a just-lit room** — the dark display never listed it, and `DarkRoomCombatWatcher` only catches a monster that *swings* (its attack line).
- **A readied light is visible to other players** *(CONFIRMED, user 2026-07-24)* — an onlooker sees the lit source the way they see worn gear, so it counts as "shown," not hidden.
- **A readied light burning out prints exactly `Your <item> flickers and goes out.`** *(CONFIRMED, capture 2026-07-11)* (e.g. `Your torch flickers and goes out.`) — one line, period-terminated, no name/exits.
  - It is the *only* signal the light is gone: the inventory `i` dump still lists the item as readied until the next dump lands, so the display lies about a light that no longer exists in the meantime.

**Client use:**
- Auto-light must send the bare CR after readying the light or it walks past passive mobs (the "lights a torch but doesn't re-check the room" report).
- The `@inv` remote report (carried items an onlooker can't see) deliberately excludes the readied light, reporting only the pack + key ring.
- The auto-light path latches on the burnout line (`AutoLightProvisioner.OnReadiedLightExpired`, pattern `^Your .+ flickers and goes out\.$`) to discount the stale readied value and re-ready a carried spare once the room's "can't see" line confirms it went dark. Anchored full-line so a mob-flavour "flickers" elsewhere can't false-trigger.

### Monsters in dark rooms
*Status: CONFIRMED (attack-line engage; name dedupe 2026-07-21, user; late populate 2026-07-22, user)*

- **A monster in a dark room is invisible to the room display but still attacks — engage it by the name in its attack line.** With no `Also here:` line (the dark room prints only the "can't see" line, see *Dark rooms — no name, no exits, traversal inferred from no bonk*), the only evidence a hostile shares the room is its incoming attack line, rendered in dark cyan: a miss `The <monster> <verb> at you` or a hit `The <monster> <verb> you for N damage!`.
  - The `<monster>` token is the monster's real name, so `a <monster>` (e.g. `a cave bear`) attacks it exactly as if it had been listed under `Also here:`.
  - Attacking a monster that **isn't** in the room draws `Your command had no effect.` (with talk slow on — see *Combat → Attacking a monster that isn't in the room*) — the signal that the target is gone (retract it and stop swinging).
- **In a dark room, a distinct display name is a distinct enemy — dedupe reveals by name, never by monster record** *(2026-07-21, user)*.
  - A mob can swing more than once per round and the dark room re-emits its attack line every round, so four `The cave lizard swings at you` lines are **not** four lizards — the client collapses same-name reveals to one entity.
  - But `cave lizard` and `small cave lizard` (e.g. we're on one, a partner announces the other) are **two separate enemies**, even if they share a Monsters-table record — so the dedupe keys on the **name string**, not the resolved monster number; collapsing by record would drop a live mob.
  - Two genuinely same-named mobs can't be told apart in the dark, so they read as one until the first dies — the survivor's next swing re-reveals it and combat re-engages. Over-count is thus impossible and under-count self-heals.
- **A dark room's monsters can populate *after* entry, not only at the moment you arrive** *(2026-07-22, user)*. A room that reads empty on arrival (no attack line yet) can still have a hostile reveal itself a beat later via its first swing.
  - Empirically a reveal often lands at 0ms (synchronous with entry — over a 1-hour hunt all 334 populated rooms revealed at 0ms), but that is area/condition-dependent, **not** a guarantee: the delayed-populate case is real.

**Client use:**
- The client injects the attack-revealed monster into the room's entity list so auto-combat engages it (`DarkRoomCombatWatcher`).
- The dark-room settle window (`DarkRoomSettleGate`, ~1.0s per dark advance) must hold before the walker moves on — it's the only late-reveal guard for a room that looked empty at entry. It is a justified safety buffer, not dead time: do **not** trim or short-circuit it on the strength of an all-synchronous sample. (See *Per-hop movement speed* for its realm-coupled cost.)

### `look <dir>` — peeking an adjacent room
*Status: CONFIRMED (closed-barrier case 2026-09-08, user + report `stock-20260908-205441`)*

- **`look <dir>` peeks the adjacent room with a full room display, but the player never moves.** Looking into an exit (`look north`, `l e`, `peer …`) renders the neighbouring room exactly like walking in would — its title, its `You notice … here.` item/cash survey, and its `Also here:` monster/player list — yet the player stays put.
- **It is a *preview*, not an entry**, so any room-entry automation keyed on the room display (auto-get items, cash pickup, combat engage) must be suppressed for it — otherwise the client fires `get`/attacks at a room it isn't standing in (the reported bug).
- **The player's *own* room is unaffected:** walking in for real re-renders the room outside the window and the automation runs normally.
- **A closed door/gate blocks the peek** *([CONFIRMED] 2026-09-08, user + report `stock-20260908-205441`)*. `look <dir>` at an exit whose barrier is **shut** renders no room — the server answers *"The door is closed in that direction!"* instead.
  - The obvious-exits line flags it ahead of time (`closed door north` / `closed gate north`), so a peek that must read the neighbour has to **open the barrier first, then look**.

**Client use:**
- The client arms a short suppression window on sending the look (`RoomTracker.NoteLookSent`); the display consumers that run *before* the `Obvious exits:` line poll `IsPeekSuppressed()` to skip the peeked room, and the window is consumed when `NoteRoomObserved` fires on the exits line.
- The Warped Asylum look-sweep opens the barrier then looks (its rooms gate siblings behind bashable doors); before #346 a shut door on a peek direction failed the whole maze solve out.

### Room display parsing — the room title is positional, not just bright cyan
*Status: CONFIRMED (user; report `paradigm-20260829-154032`)*

- **Bright cyan is not uniquely the room title — an engine-side palette can remap spell/ability text to it.** A player running a palette that shifts spell/ability lines from dark blue to bright cyan makes every `<player> invokes the way of the monkey!`-style broadcast render in the **same bright cyan as room names**, deterministically (not an occasional fluke).
- **Such a line can arrive in the arrival burst immediately *before* the real title**, so colour can't tell an ability line from the title under that palette.
- **The room display is one contiguous block** — title → `Also here:` → `Obvious exits:` — so async player broadcasts land before it, never between the title and the exits line. The title is therefore the **last** bright-cyan line before `Obvious exits:`.

**Client use:**
- The room-name detector anchors on **position**: `RoomDisplayParser` keeps the nearest bright-cyan line to the exits, not the first in the block.

### Sneaking — commands, equip order, and the sneak state machine
*Status: commands OBSERVED (the client issues these) · equip-before-sneak CONFIRMED · sneak lines OBSERVED (parsed by the client)*

- **Commands** *([OBSERVED])*: `sn` — attempt to sneak. (Hiding is `hid` — see *Hiding — sneak vs hide, the hide state machine, and search reveals*.)
- **Equip before sneak** *([CONFIRMED])*: equipping / removing gear breaks sneak, so any gear change for an approach must be sent **before** the `sn`, never after. The correct approach order is **equip → sneak → move**.
- **Sneak state machine** *(lines all [OBSERVED])*:
  - `Attempting to sneak...` (alone, no suffix) — the server ACK: the sneak took and you're armed to move. A move made now carries the sneak into the next room.
  - `Attempting to sneak...You don't think you're sneaking.` — soft rejection; the attempt didn't take. Resend `sn`.
  - `Sneaking...` — emitted on each room entry while sneak holds; post-move confirmation you arrived unseen.
  - `You make a sound as you enter the room!` — loud loss of sneak.
  - `You may not sneak right now!` — hard block; no auto-retry.
- **Sneak breaks *silently* when you move into a room that doesn't re-emit `Sneaking...`** *([OBSERVED])* — no failure line, the stealth is just gone.
- **Any NPC in the room prevents a sneak from taking** *([OBSERVED])* — an `sn` is wasted while a monster shares the room.

**Client use:**
- The backstab loadout is applied in the walker's pre-move step, ahead of the `sn`, rather than raced at room-clear (because of the equip-before-sneak rule).

### Observing another player's failed sneak into your room
*Status: CONFIRMED 2026-07-12 (user)*

- **`You notice <name> sneaking in from the <dir>.`** — you *perceived* another player entering your room while sneaking (their sneak failed against you).
- **This line is always a player, never a monster** — monsters do not sneak-enter with this wording.
- **Wire colour is not a reliable kind hint here** — the realm may paint the line the monster hue (yellow).

**Client use:**
- `SneakArrivalNotice` captures the bare `<name>` and `RoomEntryWatcher` classifies it Player unconditionally.
- The generic `RoomEntryArrival` pattern carries a `(?!You notice )` guard so it doesn't also grab `"You notice <name>"` as a null-numbered Monster — which previously held the combat gate open and froze the loop.

### Hiding — sneak vs hide, the hide state machine, and search reveals
*Status: CONFIRMED (hide lines from paired two-character POV captures)*

- **Sneaking and hidden are distinct stealth states and either one enables a backstab.**
  - *Sneaking* lets you **move** silently and open on a target you approach, but does **not** remove your name from the room's `Also here:` line — others still see you listed there.
  - *Hidden* removes your name from `Also here:` (you're invisible in the room) but you **cannot move** while hidden.
  - A **player** has to `search` the room to reveal (unhide) you; **monsters do not search rooms**, so a monster that walks into a room you're hidden in never reveals you — it just becomes a backstab target.
  - A monster's passive **see-hidden** ability is a separate thing: it reveals a stealthed character to the whole room on sight, defeating the opener (see *Combat → Backstab*).
- **Command** *([OBSERVED] — the client issues it)*: `hid` — attempt to hide.
- **The bare verb hides the player; with an object it stashes the object** *([CONFIRMED] 2026-09-26, user)*.
  A plain `hide` (or its shorthand `hid`) is the player hiding themselves; `hide <N> <item>` stashes the
  item in the room, where it can't be seen again until someone actively searches for it (see *Items,
  inventory & equipment → Hiding items in a room (stashing)* and *Money, banks & shops → Hiding coin in a
  room (stashing)*).
- **Hide state machine** *(lines all [CONFIRMED] — from paired two-character POV captures)*:
  - `Attempting to hide...` (alone, no suffix) — the attempt fired and the server ran a hide check, but the outcome is **NOT reported to you**. This line is **ambiguous**: it means "a check happened," not "you are hidden." You cannot tell success from failure off this line alone.
  - `Attempting to hide...You don't think you are hidden.` — explicit hide **FAILURE**. This is the only self-observable failure signal.
- **Hide SUCCESS is not self-observable.** There is no self-side "you are now hidden" confirmation. The only 100%-reliable confirmation is **external**: another player displaying the room and finding you **absent** from the `Also here:` line (or their `search` failing to turn you up). From your own output stream, the best you can know is "an attempt fired" (`Attempting to hide...`) or "it failed" (`...You don't think you are hidden.`) — never a positive success.
- **Reveal (search) mechanic:**
  - A player runs `search` / `sea`. On a hit they see `You see <name> hiding in the shadows.` and the hidden character is revealed (returned to `Also here:`); on a miss they see `Your search revealed nothing.`.
  - The hidden character sees `<name> is searching the area.` while someone searches — i.e. you get a warning that a reveal attempt is in progress.
- **Do not hide while in a party** *([CONFIRMED])*. A hidden member is removed from `Also here:`, and a player who isn't listed there **cannot be single-target-targeted** by other players — including party heals and buffs — until revealed. Only room-wide spells (relevant in PvP) and possibly party-wide spells still reach a hidden member. (See *Party → Targeted casts on a hiding member*.)

**Client use:**
- The engine arms the opening `bs` off **either** stealth state (sneaking OR hidden — the backstab gate reads `StealthManager.IsStealthed`).
- Because hide success is **not** self-observable, the hidden side is handled **optimistically**: a bare `Attempting to hide...` latches `Hidden = true` on faith, and the backstab surprise-round resolver confirms or denies it after the opener swings (a real hide lands the `surprise`; a false one whiffs and, with `RunIfBackstabFails`, flees).
- The one ground-truth signal, `...You don't think you are hidden.`, drops the optimistic state.
- A move breaks hide (you can't move while hidden).
- A fresh in-place hide re-arms the surprise round, so a hidden character that kills one monster and re-hides can backstab the next one that wanders in.
- **Auto-hide is suppressed while in a party** — a hidden member falls off the Also-here line and can't be single-target-healed/buffed until revealed.

### Casting breaks both Sneak and Hide
*Status: CONFIRMED 2026-09-09 (user)*

- **Casting a spell — self buff/heal/cure, party buff, anything — breaks Sneak and Hide alike.**
- **This is an accepted cost, not a reason to withhold the cast.**

**Client use:**
- The buff-maintenance automation casts a due buff/heal/cure regardless of stealth state rather than silently sitting on it to preserve Sneak or Hide (`CastingDirector` does not gate casts on `StealthManager.IsStealthed`).
- Only the backstab opener still reads combined stealth state, since either Sneaking or Hidden opens it.

### Locked doors — picking, opening and bashing
*Status: CONFIRMED (picklock wording: stock, capture 2026-07-30, report stock-20260730-182812; bash HP drain: user direction) · Realm: Stock (picklock wording)*

- **A successful `pick <dir>` prints `You successfully unlocked the door.`** — **past tense**, and the *same* line the use-key unlock emits (the two are distinguished only by which command was in flight, not by wording).
- **A pick failure is `Your skill fails you this time.`**
- **Unlocking does not open the door** — a separate `open <dir>` is required, whose success prints **`You open the door.`** (not "The door is now open.").
- **Bashing a door drains the basher's HP.** Each `bash <dir>` swing at a door costs HP (a bashable door opens after some number of swings, gated by RNG, not a single hit), so sustained bashing whittles the character down.
- **Picking does not drain HP.**

**Client use:**
- `DoorOpenManager` keys on all three pick/open lines; matching only present-tense "unlock(s)" or "is now open" stranded the walker at a picked door (report stock-20260730-182812).
- `DoorOpenManager` bashes a *bashable* door (per `DoorPolicy`) **uncapped** — no fixed attempt limit — but interleaves rest: once HP falls to the Health-tab **rest-if-below** trigger it pauses bashing so `HealthManager` can rest to **rest-max**, then resumes. (Confirmed by user direction; replaced the old fixed `MaxBashAttempts` cap.)
- Picking keeps its `MaxPickAttempts` retry cap.

### Hidden exits — `sea <dir>` reveal wording
*Status: CONFIRMED (capture 2026-07-14, report 121106); works-while-blind: Unrated*

- **Revealing a hidden exit is `sea <dir>`; the reply wording is axis-dependent:**
  - **success** — cardinals `You found an exit to the <dir>!`; up/down `You found an exit upwards!` / `You found an exit downwards!` (no "to the", `<dir>wards` suffix). *`upwards` confirmed on the wire; `downwards` confirmed from an earlier capture.*
  - **failure** — cardinals `You notice nothing different to the <dir>.`; up/down `You notice nothing different above you.` / `You notice nothing different below you.` (no "to the", no direction word). *Both vertical forms confirmed (`above you` on the wire, `below you` by the user).*
- **A "bonked" `sea` is distinct from a bonked *move*:** the `sea` reply above is not a move refusal.
- **Searching for a hidden exit works while blind** *(Unrated)*. `search` / `sea <dir>` reveals a hidden exit regardless of blindness. Blindness suppresses only the descriptive room lines — room name, room description (if enabled), the `Also here:` roster, and the `Obvious exits:` line — it does NOT block the search itself or its outcome.
  - The reveal carries its own confirmation line (`You found an exit to the <dir>!` and its vertical forms) that fires whether or not you can see the room, so an engine driving a walk can rely on that confirmation to know the hidden exit opened, even mid-blindness.

**Client use:**
- The client keys on both to drive the reveal retry loop (`HiddenExitRevealManager`): a failure line triggers another `sea` up to the attempt cap, a success line resolves the reveal so the walker sends the move.
- Because the up/down failure form drops "to the" entirely, a failure regex that only matched the cardinal `to the <dir>` shape never registered an up/down miss — so up/down searches never retried cleanly and stalled (the reported symptom).
- The auto-walker's "is this exit already revealed?" pre-check must not skip the `sea <dir>` just because the character is blind — the search still works and is required to unveil the exit.
- It also must not trust a stale observed-exits set from a room it only dead-reckoned into — see RoomTracker.SetRoom clearing ObservedExitDirections; a stale set made the walker skip the required search and ram a wall.

### Exit traps — search and disarm
*Status: CONFIRMED (capture 2026-07-15, reports 132150 and 131801); `disarm trap <longdir>` acceptance NOT wire-confirmed*

- **A trap search reports the found trap with the LONG-form direction word.** Searching a trapped exit is `sea <dir>`; on a hit the game replies `You found a trap to the <dir>!` where `<dir>` is spelled out long — `You found a trap to the southeast!` (confirmed on the wire, alongside the outbound `sea southeast` that produced it).
- **Whether `disarm trap <longdir>` (e.g. `disarm trap southeast`) is accepted the same way `sea <longdir>` is has NOT been directly wire-confirmed** (the reported capture stalled before the disarm went out); it is the walker's existing send shape and is flagged for live verification.
- **Trap-disarm capability can be inferred from the character's race and class via game data — the parsed Traps stat is not the only signal** *(report 131801)*.
  - Race and class are chosen at character creation (the player selects them) and are shown on the train-stats screen, so they are known even for a brand-new character that has never run `stat`.
  - A class or race grants the Traps skill when its game-data record carries a trap-skill ability code — code 40 (FindTraps, the single "Traps" skill governing both find and disarm in stock data), 41 (DisarmTraps), or 1002 (GrantTraps) for custom / ParaMUD sets.
  - The Traps *value* itself still comes only from the `stat` screen's `Traps:` row — the single-line `exp` output (`Exp: N Level: M Exp needed for next level: ...`) reports only progression and never carries it.

**Client use:**
- The client keys on the trap-found line to drive `TrapDisarmManager`'s search→disarm loop.
- **Direction matching must normalise both sides.** The @trap remote handler enqueues the short form it parsed, but the walker enqueues the long-form direction word — and the game's reply is long-form. Comparing a short-normalised observed direction against an un-normalised stored one (long-form from the walker) never matched, so a successful search stalled in *Searching* and the disarm never fired (the reported bug). Both the observed and the stored/enqueued direction are now normalised to the short form before compare.
- **The disarm send stays long-form** — kept long-form by analogy with `sea` (`sea southeast` is wire-confirmed, matching the confirmed search); unverified for `disarm` itself. The walker keeps sending the long form it enqueued rather than re-shortening it.
- Trap-skill grant check: `AbilityNames.HasTrapAbility` — the same grant the party-delegation capability check already reads for other players.
- When the Traps value hasn't been captured yet (a freshly loaded profile with no `stat` this session, or a new character), the client falls back to the race/class game-data grant to decide capability: the walker self-disarms if the selected class or race grants Traps, rather than deciding on a defaulted-zero value and waltzing through. A positive parsed Traps value remains the primary signal.

### Winch gates
*Status: CONFIRMED 2026-08-27 (user — report `paradigm-20260827-113513` + wire capture); drawbridge CONFIRMED 2026-09-06 (user — report `paradigm-20260906-202008`) · Realm: Paradigm*

- **Some gates are opened by a winch in the room** (a `MultiActionHidden` exit whose prerequisite is `pull winch`), e.g. the Entrance Hall fortress gate west (`iron gates … a heavy wooden winch`).
- **The winch is operated by any of `pull` / `turn` / `move` / `push` winch** (aliases for the same TBInfo action), yielding exactly one of two lines:
  - **success:** `You heave mightily on the winch, and it begins to turn!`
  - **failure:** `You heave mightily on the winch, but it does not budge.`
- **"Does not budge" is a strength roll, not randomness.** The TBInfo action is `testskill strength 20 …` — a strength check on each pull. **Higher strength = more likely to pass**; a roll can still miss. So failure is a **retry** (keep pulling), not a give-up. (All races start ≥20 strength, so no character is hard-blocked — it just takes more pulls at low strength.)
- **After it turns, the gate opens on a short delay with no "the gate opens" line.** The action carries `adddelay 3` ≈ 3s; the gate only reads `open gate <dir>` in a room re-display (a bare `l` / look refreshes it). Moving before the gate is actually open bonks `The gate is closed!` (which the movement-refusal detector reverts).
- **Same-room vs cross-room.** Most winches sit in the same room as the gate they open. But a winch can `remoteaction` a gate in a **different** room (in Paradigm 1.9.1, the winch in `12/2118` opens the gate off `12/2122`). Then the gate exit is a cross-room remote-action detour, not a same-room exit.
- **Paradigm 1.9.1 winches (3):** `12/2099` (Entrance Hall, same-room), `12/2123` (Narrow Precipice, same-room), and `12/2118`→gate off `12/2122` (cross-room).
- **`12/2123` (Narrow Precipice) is a drawbridge, not a door/gate, and breaks the "no open line" assumption above** *([CONFIRMED] 2026-09-06, user — report `paradigm-20260906-202008`)*. Its exit is permanently phrased `lowered drawbridge <dir>` in the exits list — the wording never flips to `open`/`closed`, so the room-redisplay poll can never see it as open. It DOES broadcast its own explicit line on success, though: `The wooden drawbridge lowers with a heavy thud!`

**Client use:**
- **Same-room** → `WinchManager` (walker + loop) sends the pull, retries on "does not budge", and on "begins to turn" polls a look until the gate reads open, THEN moves — never fires the move blindly.
- **Cross-room** → the `RemoteActionPathExpander` detour's `pull winch` step is flagged `IsWinchPull` and routed through `WinchManager` pull-only (retry until it turns, no gate poll — the walk back to the gate room covers the open delay).
- `WinchManager` treats the drawbridge line as authoritative and skips the poll for it (whichever order it arrives relative to "begins to turn").

### Exit alignment gates
*Status: CONFIRMED 2026-08-27 (user — mechanic for report `paradigm-20260827-144553`) · Realm: Paradigm*

- **A room exit can carry an `(Alignment: <low> to <high>)` restriction** (e.g. `(Alignment: Neutral to Fiend)`) — rare (only ~14 exits in the Paradigm dataset).
- **The exit admits a character iff their numeric alignment falls inclusively within [value(low), value(high)]**; outside that band the game refuses the move with **"Your current alignment prevents you from entering this exit."**
  - So `Neutral to Fiend` = `[0, 300]`, which admits Neutral..Fiend and **blocks Good (-100) / Saint (-201)** (the "evil entrance" a Good character can't use).
- **Enforcement is whole-party** — the party is stopped if ANY member's alignment is excluded, so routing must consider every member's alignment (looked up from the PlayerDatabase, populated by `who` / look).

**Client use:**
- When a member's alignment is **unknown**, the router does NOT detour around the gate; it walks **up to** the gate and **halts** there for the user to decide (rather than guessing or risking a bonk).

### `borrow skiff` — a free text-exit ferry
*Status: CONFIRMED (capture 2026-07-11)*

- **A water crossing keyed `borrow skiff` is a *free* text-exit ferry, not an item gate.** At a shore room the exit is a Text exit whose command is `borrow skiff`; sending it prints `You climb into one of the skiffs, and row to <place>.` and lands the player in the far room (e.g. Silvermere at 1/2335).
- **It costs nothing** — the capture crossed with `Gold: 0` — so it's a *borrow*, distinct from the buy-a-raft `(Item: N)` carry gates the route picker weighs elsewhere.

**Client use:**
- The client crosses it like any other text exit (`RoomTracker` Confirmed → Pending on the sent command; the walker resolves the Text exit's deterministic target), so `borrow skiff` must be treated as a plain traversal command, never a purchase or a carried-item requirement.

### CMD-driven room teleports split the party
*Status: CONFIRMED (arrival ordering: capture 2026-07-10); general CMD-vs-exit rule NEEDS CONFIRMATION*

- **A CMD-driven room teleport moves only the character who types it — every member must fire it themselves.** Some rooms carry a command-triggered teleport in the room's `CMD` → TBInfo action chain rather than as a directional exit — e.g. Slum Street (`1/1182`) has TBInfo `#4087`: `ring chime:message …:teleport 65 1:message …` / `use chime:…` (a `ring chime` / `use chime` verb that teleports the caster).
- **This is not a `Text` ("go path") exit** where the leader traverses and followers are dragged along: a CMD teleport **breaks the party apart** (the teleport removes the mover from the group). So a leader taking a party through one must:
  1. relay the verb to the whole party first — `.@party ring chime` (the leading `.` sends the `@party` relay as a say) — so every member's client fires it and teleports, then
  2. fire the verb itself (`ring chime`), and
  3. because the teleport disbanded the party, **re-invite every member and wait for them to rejoin** before continuing the route.
- **Arrival ordering** *([CONFIRMED], capture 2026-07-10)*: the leader teleports *first* (`You ring the chime…` → `You find yourself…elsewhere.`), then each relayed follower materialises a beat later, one per line: **`%name% appears in a blinding flash of light!`** (the generic teleport/recall arrival line for another player entering your room — no direction).
  - A re-invite fired the instant the leader crosses races **ahead** of the members' arrival and the server answers **`You don't see %name% here!`** — the invite is silently lost and that member is left out of the reformed party.
  - The re-invite for each member must therefore wait until that member is observed in the room (their `appears in a blinding flash of light!` line, or an `Also here:` listing if they landed ahead of the leader).
  - A member whose invite lands after arrival rejoins cleanly (`You have invited %name% to follow you.` → `%name% started to follow you.`).
- **Believed general rule** *([NEEDS CONFIRMATION] — user's inference, not yet verified across all cases)*: a teleport driven by a room **`CMD`** (TBInfo chain) splits the party and needs each member to execute it (→ `.@party` relay + re-invite/wait), whereas a teleport/traversal that is **exit-driven** (a `Text` exit like `go path`) needs **only the leader** to execute it and is party-safe (followers follow normally). Confirm before extending the split/re-invite behaviour to teleport shapes other than the `ring chime` CMD case above.

### Greet teleports — an NPC transports a player who asks
*Status: CONFIRMED (report 2026-08-13); class gate + skill roll from Paradigm map 1 data (issue #455); per-member fare CONFIRMED 2026-09-24 (user, greet-teleport fare gating)*

- **Some placed NPCs carry, inside their `Monsters.GreetTXT` chain, an askable keyword whose directive block ends in a `teleport <room> <map>`** — asking the NPC that keyword (`ask <noun> <keyword>`, noun = last word of the monster's name, same convention as the guard-door ask commands) ports the asker to that room.
- **Example — the Floating Citadel's Grey Lord** (`#251`, GreetTXT 366) stands in the Great Hall (`1/160`), a sealed pocket whose only cardinal exit is S; **`ask lord teleport`** ports the character to **Town Square (`1/224`)** — the pocket's only egress toward the rest of the realm.
  - The same greet also exposes quest keywords (`code` / `word`, TBInfo block 371) that reach the same room but sit behind `evilaligned` / `goodaligned` / `checkability` gates; the plain **`teleport`** keyword (block 370) is **ungated** and deterministic.
- **Party behaviour — each person asks and each person pays** *([CONFIRMED] 2026-09-24, user — greet-teleport fare gating)*. Each party member using the transport word must have the fare: a party crosses an NPC ask-transport by every member asking it themselves (the leader relays the keyword), and each asker is charged the full fare — so the whole party needs it on hand. (Like a CMD teleport, it moves only the asker.)
- **Class gate** *(issue #455, from Paradigm map 1 data)*: a **`class N`** gate (N = `Classes.Number`) restricts the transport to one class — the barmaid (`#248`, `1/391`) carries `class 12:testskill …:teleport 391 1`, a **bard-only** ask-transport. This is surfaced as the edge's `ClassGate` so `MovementFilter.IsClassGateBlocked` keeps it routable for that class and drops it for every other, rather than letting non-bards route through a transport they can't use.
- **Skill roll** *(issue #455)*: a **`testskill <skill> <amount> <failTB>`** in the same block is a per-use **skill roll** (like the winch's `testskill strength`) that can fail even for the right class. A failed roll leaves the asker put — sometimes with no fresh room render at all, so no tracker transition comes.

**Client use:**
- The navigation engine models only the **ungated** greet teleport as a routable exit (GreetTeleportResolver → `RoomGraphManager.BuildGreetTeleportEdges`, a `Direction.Teleport` edge whose command is `ask <noun> <keyword>`); gated keywords are skipped because the client can't verify the gate and a failed transport would strand the walker.
- Three directive kinds on a greet chain are NOT treated as "unverifiable, skip": `class N`, `testskill`, and `price` (`GreetTeleportResolver` parses it and sums repeats — see *Money, banks & shops → Repeated `price` directives add up*).
  - The client does NOT model the skill-roll odds; instead the walker treats a greet teleport as a **verify-and-retry** step: after asking, it checks it actually landed in the destination and, if not, **re-asks until it arrives.** The class gate guarantees only a class that CAN eventually pass the roll ever reaches the step, so the retry converges.
- Ordinary CMD teleports — chime / boat / item-cast — stay fire-once; only the `ask`-transport edge, `RawHint` `"greet teleport"`, gets the retry watchdog.
- **Fares:** `GreetTeleportResolver` sums the line's `price` directives into the transport's `FareCopper`; the graph stamps it on the synthesised Teleport edge (`RoomExit.FareCopper`) and `MovementFilter` gates it like a toll — on the poorest party member's `@wealth` (or your own wallet solo).

### Boat travel — the sea captain's `secure passage to <place>`
*Status: CONFIRMED (user 2026-07-20) + OBSERVED from Paradigm-1.9.1 data · Realm: Paradigm*

- **A sea-captain dock `CMD` ferries a party across water via `secure passage to <place>`.** This is a *specific, more elaborate application* of the CMD-teleport-splits-party mechanic (see *CMD-driven room teleports split the party*): a dock room's `CMD` points at a TBInfo block listing one `secure passage to <place>` verb per reachable port.
  - Typing the verb at the dock CMD-teleports **only the caster** onto a ship, so it splits the party exactly like `ring chime` — the leader `.@party`-relays the verb, every member fires their own copy, and the party **re-forms at the destination port** (re-invite/wait, same arrival-ordering rules).
  - The NPC behind it is the **old sea captain (#2344)** standing in the dock room.
- **Per-member gating — the captain rejects an individual, not the party.** Each `secure passage` line carries an independent **minlevel** *and* a **fare** (price). Every member is gated **individually** on both: a member below the minlevel, or without the fare on hand, is refused *at the captain* and **left behind on the dock** while the rest sail. So the pre-flight check is per-member, mirroring the toll gate: route the party to a port only when the **poorest / lowest** member clears both bars.
  - **Fare is in copper** (the base coin unit), so it folds into the same `@wealth` pre-flight gate as tolls — a member needs `price` copper-value on hand. (Contrast: a `(Toll: N)` exit is `N` **gold** = `N*100` copper; a boat `price` is already copper.)
  - **`checkability <flag> <rank>`** (optional, present only on talkiran below) is a **quest-flag / rank** gate the client does **not** read *for boat routing* — attunement (e.g. MerchantCaptain rank 3) is the user's responsibility. If a member routes to that port un-attuned the captain rejects *them*; the client never boards and **fails out** (see the **Transit + fail-out** bullet in this topic). (The client *does* read live quest-flag values elsewhere — the login quest-completion sync, see *Quests → Quest-flag values (`abil` / `sys god abil`)* — but never uses them to decide boat passage; attunement stays the user's call.)
- **TBInfo Action format** (verified verbatim off `data-Paradigm-1.9.1`, TBInfo #4986 / #5012, each reached from the dock room's `CMD`). One newline-separated line per port; colon-separated directives:

  ```
  secure passage to <place>:[checkability <flag> <rank>:]minlevel <L> <failMsgId>:price <copperFare> <failMsgId>:random <tbId>:text <msgId>
  ```

  - `random <tbId>` points at a **weighted-random TBInfo table** (a `Textblock(rndm)`), whose lines read `<roll>:cast <boardSpell>:cast <tripSpell>`.
  - Both `cast`s fire in order: **`boardSpell` (5415 "board ship")** random-teleports the caster into a ship room (`Abil 140` val 0 over `MinBase..MaxBase` = rooms **14/715–723**, `Abil 141` = map 14); **`tripSpell`** starts the buff-locked voyage.
- **Arrival-room resolution is a spell EndCast chain** (uses the `140/141/151` mechanics — `140/141` documented in *Cast-on-walk exits and random teleports*, the `151` EndCast chain in *Room-spell hazard shape 1 — direct damage, negated by an item's `NegateSpell-N`*): follow `tripSpell` down its **`Abil 151` (EndCast → follow-on spell)** chain — `trip 1 → trip 2 → trip 3 → disembark to <port>` — to the terminal **`disembark`** spell, whose **fixed `Abil 140` room + `Abil 141` map** is the arrival `RoomKey`.
  - Each trip leg carries `Abil 29` (buff-lock) so the player can't act mid-voyage; the final `<port> landing` spell is a cosmetic message.
  - Worked chain (albion): random `#4987` → `cast 5415`+`cast 5416` → `5416 → 5418 → 5419 → 5417 "disembark to kingsport"` (`Abil 140`=702, `Abil 141`=14) → arrival **14/702**.
- **Verified Paradigm-1.9.1 dock table** (discovered data-driven, *not* to be hardcoded — scan every room's `CMD` TBInfo for `secure passage to` lines and resolve as above):

  | Dock room | Port | Verb | Minlevel | Fare (copper) | checkability | Arrival room |
  |---|---|---|---|---|---|---|
  | 14/759 "Blackwater Harbor, Wharf" | albion | `secure passage to albion` | 50 | 2,000,000 | — | 14/702 (Kingsport Harbor) |
  | 14/759 | terra fuego | `secure passage to terra fuego` | 50 | 2,000,000 | — | 14/1812 (Shoreline, Sandy Beach) |
  | 14/759 | terra ista | `secure passage to terra ista` | 60 | 5,000,000 | — | 14/1813 (Cliffside Beach) |
  | 14/759 | talkiran | `secure passage to talkiran` | 65 | 6,000,000 | `211 3` (MerchantCaptain rk 3) | 14/15000 (Tal'kiran Shore) |
  | 14/702 "Kingsport Harbor, Wharf" | port blackwater | `secure passage to port blackwater` | 1 | 2,000,000 | — | 14/759 (Blackwater Harbor) |

- **Transit + fail-out.** After the verb: board → random ship room (14/715–723) → buff-locked trip legs → disembark teleport to the arrival room. A member the captain rejected (minlevel / fare / attunement) **never boards**.

**Client use:**
- The client **suppresses engines** during the voyage and waits for the arrival room to render.
- A rejected member is detected as *no teleport into a ship room within a short window of firing the verb*, which is the fail signal.

### Jail `bribe guard` — cell-hop helper with an escalating toll
*Status: CONFIRMED 2026-07-30 (user) · Realm: Paradigm*

- **In the jail rooms (Paradigm `1/541–545`, `14/1326–1333`) the `bribe guard` command casts a jail-teleport** — it moves the player between cells so a jailed player can reach their gear when they can't bash / picklock the cell door or lack the jail key.
- **The TBInfo `Action` lists six escalating `price` tiers** — `100 / 1000 / 10000 / 100000 / 1000000 / 10000000` copper (1 Gold → 10 Runic).
- **The guard charges the largest tier the player can currently afford**, capped at 10 Runic per bribe: carry 11 Runic → charged 10 Runic; carry 8 Runic → charged 1 Runic (the next tier, 10 Runic, is unaffordable). The escalating cost *is* the catch.
  - **Why it isn't a sum** *([CONFIRMED] 2026-09-26, user)*: the trigger reads its effects **top-down** — if you can afford a `price` it moves on to the next one, until it reaches one you can't afford, and then fires the previous (last affordable) tier. Contrast the identical repeats on an NPC transport line, which add up (*Money, banks & shops → Repeated `price` directives add up*). The client models the two shapes separately: `GreetTeleportResolver` sums, the room-CMD requirement reader takes the highest affordable tier.
- **This tiered multi-`price` shape is unique to bribe guard among room-`CMD` commands**; ordinary paid services (`roll dice`, `buy <spell>`, `summon <x>`, `secure passage`) carry a single `price <copper> [failTextblockId]`.

**Client use:**
- Only the ceiling is meaningful to surface, so the room tooltip renders it as "bribe guard — costs up to 10 Runic (takes the most you can afford)".

### Room-entry hazards — what counts as a movement hazard
*Status: CONFIRMED 2026-08-25 (user)*

- **A room's entry spell (`Rooms.Spell`) is a hazard only when its unprotected effect actually damages the player, kills the player, or forces/prevents their movement** (a teleport/transfer out). For the navigation router's purposes a hazard is something to route around, gate behind a counter item, or warn on.
- **Not hazards:** a monster summon on entry, an alignment shift (`addevil`/`addgood`), a flavor `message`, or quest-item placement — you can still walk in freely, so the router must treat the room as ordinary.
- **This holds even when the room-entry spell carries a `failitem` "counter".** E.g. Blackwood Forest (`Spell 1040`) only summons a monster + shifts alignment + prints a message, so despite its `failitem 185` ("manhole"), it is **not** a movement hazard.
- **Real hazards** damage (a `Damage`/`EndCast` ability, or a TextBlock `cast` of a damaging spell), gate on a survival buff (`checkspell`/`failspell` — the desert heat, the drown chain), or `teleport`/`transfer` you out (the sea/ice/pit rooms).

**Client use:**
- Encoded in `RoomHazardIndex` — see the harm gate in `BuildHazard`.

### Protective-item gates — exit-gated vs room-spell-gated
*Status: CONFIRMED (encoding fully decoded off the stock v1.11p data set)*

- **Some rooms harm you on entry unless you carry (or wear, or drink) a protective item — either exit-gated or room-spell-gated.** There are TWO gate locations (exit vs room-spell) and, within room-spells, THREE distinct protection encodings (*Room-spell hazard shape 1 — direct damage, negated by an item's `NegateSpell-N`*, *Room-spell hazard shape 2 — TextBlock action guarded by `failitem <itemNum>`*, *Room-spell hazard shape 3 — buff check (`checkspell` / `failspell`): the desert waterskin*).
- **A. Exit-gated** — the exit string itself carries the modifier; already parsed:
  - `Item: (item#)` → `RoomExit.KeyItemId` + `RoomExitHint.Item`. Traversal needs the item in the pack. Examples: `6/79 → 6/80` needs *rope and grapple* (item 191); `6/1549 → 6/1550` needs *climbing harness* (item 930).
  - `Level: X to Y` → `MinLevel`/`MaxLevel`. Example: `12/2369 → 12/2371` needs level 50+.
  - A level-restricted *action* can also be `CMD`-driven (TBInfo), not a Spells hazard — e.g. `17/2854` (`CMD:4328`), a min-level gate expressed as an action chain, not `Room.Spell`.
- **B. Room-spell-gated** — the room carries a cast-on-enter spell (`Room.Spell` = a record number into the Spells table). 3981 rooms carry an entry `Spell` but only ~82 distinct spell numbers are used, and most are benign (light/ambiance/message). The hazardous ones use one of three shapes: (1) direct damage negated by `NegateSpell`, (2) TextBlock guarded by `failitem`, (3) TextBlock guarded by a buff check (`checkspell` / `failspell`).
- **Routing takeaway:** a room is *safe to route through* if, for its `Room.Spell` hazard, the player satisfies the protection — holds a `failitem` item, wears/holds an item that `NegateSpell`s the damage/timer spell (or its EndCast follow-on), or carries the buff-source item for a `checkspell` gate. Otherwise the node is hazardous: avoid it, or offer acquire/ask, same as an item-gated exit.
- **Detecting a hazard therefore needs:**
  - (i) read `Room.Spell`;
  - (ii) walk its `Abil/AbilVal` for a direct `1`(Damage) or `151`(EndCast) chain, and for `148`(TextBlock) parse the TBInfo `Action` for `failitem` / `checkspell` before a `cast`;
  - (iii) resolve protective items via Items `NegateSpell-N`, `failitem` item ids, and `checkspell` buff-source `CastsSp` items.

### Room-spell hazard shape 1 — direct damage, negated by an item's `NegateSpell-N`
*Status: CONFIRMED (lava/volcano via game data, Paradigm 1.9.1; Misty Bog by user, report `paradigm-20260829-203409`) · Realm: Paradigm*

- **The entry spell has `Abil 1` (Damage) directly, and may chain a death-timer via `Abil 151` (EndCast → follow-on spell).** Protection is a held/worn item whose `NegateSpell-0..9` list (an Items.json field) contains the spell number.
- **Worked example — the underwater/frozen passage:**
  - `6/1139` `Spell:511` "freezing water" = `Abil-0 1`(Damage) + `Abil-2 151`(EndCast→**512** "holding breath", `Dur 25`). Spell 512 in turn `151`(EndCast→**513**) — that is the **death timer**: hold-breath runs 25 ticks, then 513 drowns you.
  - `8/647` Black Moat `Spell:453` "black water" = `Abil-0 1`(Damage), constant chip each entry.
  - Protection: **gnomish fish-helm (item 929)**, `NegateSpell = [512, 513, 514, 453]`. Worn, it negates the drown chain (512/513/514) and the black-water damage (453). You still take the minor direct 511 chip but never drown.
  - **Timer cancel on exit:** the `6/1139` up-exit is `(Cast: pre-516, post-0)` → spell **516** `151`(EndCast→**515** "stop drowning"), and 515 `153`(KillSpell) **512** & **513** — leaving the water cancels the drown timer. (`Cast: pre-N` = cast spell N *before* moving through the exit.)
- **The lava / volcano biome is this same shape** *([CONFIRMED via game data, Paradigm 1.9.1])*.
  - `Spell:526` "magma heat" (`Abil-0 1` Damage, a leaf spell — no EndCast chain) covers ~1000 rooms (Lava Tube / "salamander tubes", Magma/Molten River, Jagged Obsidian Field, Volcano Magma Tunnels, etc.); `Spell:218` "temple of fire fire" covers the Temple-of-Fire / Volcano-heart rooms.
  - Both are negated by **either** the **magma amulet (item 487)** or the **phoenix feather (item 1000)** — each has `NegateSpell = [526, 218]`, and they're the only two items that do.
- **Misty Bog swamp damage** *([CONFIRMED by user, report `paradigm-20260829-203409`])* — `Spell:485` (the bog zone's entry spell) is negated by **either** the **swamp boots (item 925)** or the **trollskin boots (item 1232)** — both share `NegateSpell = [485, 5682]`, an any-of group exactly like the lava amulet/feather pair.
  - A player who equips a *different* group member they already own (swamp boots) is just as protected as one wearing the sourced item.

**Client use:**
- `RoomHazardIndex` already indexes the lava counters (one any-of group {487, 1000}), so the router treats lava like any protectable hazard: avoid unless the player carries a counter. By severity, lava sits with the river as *survivable* damage, while the desert is *grave* (see *Hazard severity — survivable damage vs grave*).
- The route picker/walker only ever resolve to *sourcing* one representative item from a multi-item group (whichever the acquisition pipeline can actually reach — here, trollskin via a shop). The client's path-item tracking accepts **any** member of the any-of group, so equipping a different group member counts as protected (fixed for report `paradigm-20260829-203409`, when tracking was still pinned to the one item it originally chose).

### Room-spell hazard shape 2 — TextBlock action guarded by `failitem <itemNum>`
*Status: CONFIRMED (ice cavern by user 2026-08-10, reports `paradigm-20260810-201953` / `-202239`) · Realm: both (Silver River `failitem` guards differ)*

- **The entry spell has `Abil 148` (TextBlock → a `TBInfo.Number`); the TBInfo `Action` is a colon-separated command chain.** A leading run of `failitem N` tokens before a harmful `cast <spell>` means **"if you HOLD any listed item N, abort the chain (safe); if you hold none, fall through to the damage cast."**
- **Worked example — Silver River:** `Spell:753` → `Abil-0 148`(TextBlock **2750**). TBInfo 2750 `Action` (stock): `failitem 690:failitem 691:failitem 1181:message 2096:cast 754`. Items 690 *log raft* / 691 *wooden skiff* / 1181 *silverbark canoe* are the boats; holding any one aborts before `cast 754`. *(DATA-VERIFIED: Paradigm's TB 2750 adds a fourth guard, `failitem 3609`; stock doesn't.)*
- **Not every `failitem` is a hazard:** `failitem` is used 139× across TBInfo — many are quest "don't re-give" guards like `failitem 622:giveitem 622`; only the ones ending in a harmful `cast` are hazards.
- **Ice cavern (Frozen Cavern) up/down slide** *([CONFIRMED by user 2026-08-10, reports `paradigm-20260810-201953` / `-202239`])* — the descent rooms (`10/276`–`10/295`) cast `Spell:1144` "ice cavern level 1" / `Spell:1145` "level 2", which check for a **rope & grapple** (item **191**): hold it and the slide is safe, lack it and you're **teleported down and take heavy damage**.
  - In the data the entry spell's TextBlock is **9407** (`failitem 191:failitem 930:random 9408`; `9408` does the `teleport … cast 1142` damage). The room-hazard machinery already handles this `failitem` shape — but see the encoding gotcha next.
- **DATA ENCODING GOTCHA — a TextBlock spell's TB number is NOT always in `AbilVal`.** Most room-entry hazard spells put the `TBInfo.Number` in the `Abil 148` slot's `AbilVal` (e.g. desert 683 → 2653, river 753 → 2750). But a **large class** (~40 spells: the ice cavern 1144/1145, blackwood 1040, graveyard 1126, bone dock 1152, fungus 1205, the highlands/farms 5500-series, caverns 5788/5789, …) leave `AbilVal-0 = 0` and stash the TB number in the spell's **`MinBase`/`MaxBase`** instead (ice cavern 1144 → MinBase `9407`).

**Client use:**
- `RoomHazardIndex.WalkSpellChain` now falls back to `MinBase`/`MaxBase` when the Abil-148 `AbilVal` is 0 — before that fix every one of these hazards was invisible to the router, so no protection was ever offered (the ice-cavern route picker never surfaced).

### Room-spell hazard shape 3 — buff check (`checkspell` / `failspell`): the desert waterskin
*Status: CONFIRMED (user; `failspell` 2026-07-28, report `paradigm-20260728-201619`; sunstone possession 2026-08-27, report `paradigm-20260827-112011`) · Realm: differs (Paradigm `failspell`, stock `checkspell`)*

- **A TextBlock action guarded by a buff check — `checkspell` OR `failspell`** (a buff check, not an item check).
  - `failspell S T` *([CONFIRMED by user 2026-07-28])*: "if buff S is **not** active, the damage fires."
  - `checkspell S T` **also branches to T when buff S is ABSENT** — T is the damage branch, not a safe one
    *([OBSERVED] 2026-09-26 — deduced from the data plus confirmed rules; Stock not yet seen live)*. Stock's
    T for the desert (TB 2654) is `failitem 1180:cast 712:random 2655` / `checkitem 1180:random 2655`: it
    casts the thirst damage (712) unless you hold the sunstone. Since the waterskin buff is confirmed to stop
    that damage, 2654 can only be the no-buff branch. (An earlier note here defined `checkspell` as "if buff
    S is active, branch to T (safe)" — wrong.)
  - Either way the survival model is identical — the room punishes you unless buff S is up. The client's
    `RoomHazardIndex` already reads both directives' T as the buff-absent branch.
- **Worked example — Scorching Desert** `12/853` `Spell:683` → TextBlock **2653**: `failspell 711 2654:random 2655` on **Paradigm 1.9.1** — **`failspell`, not `checkspell`** — but `checkspell 711 2654:random 2655` on **stock v1.11p** *(DATA-VERIFIED — the directive differs by realm)*. The client's hazard parser first handled only `checkspell` and so walked the Paradigm desert unprotected (report `paradigm-20260728-201619`).
  - Map 12 also has a `failspell 711` variant on `Spell:684`.
  - Buff 711 "waterskin" (`Dur 600`) is conferred by **using** the *waterskin* (item 283, `Abil 43` CastsSp→711, 3 uses).
- **The desert spell does two things — damage AND a random teleport — and the two protections are NOT equivalent** *([CONFIRMED by user 2026-07-28, report `paradigm-20260728-201619`])*.
  - The waterskin buff (711) only stops the **damage** portion; it does not stop the random teleport.
  - The **sunstone wristband** (item 1180) prevents the **entire** interaction (damage + teleport), so **if you have the sunstone you don't need a waterskin at all.**
  - **The sunstone grants desert immunity by POSSESSION** *([CONFIRMED by user 2026-08-27, report `paradigm-20260827-112011`])* — it can be worn, but the player only needs to *have* it (carried or worn), matching the `failitem` "if you HOLD the item" mechanic.
  - In the data the wristband is a `failitem 1180` guard sitting one-to-two `random` hops below the `failspell` (e.g. `2653 → random 2655 → random 2700` and `2658 → random 2660`), guarding the sandstorm/sinkhole casts (713/714/743) — which are the only desert damage that fires above `maxlevel 19`, i.e. what actually hits a high-level character. (Its `NegateSpell` covers only 713; the reliable signal is the `failitem`, not the negator.)
- **Protection is duration-based, not carry-based** *([CONFIRMED by user])*. `use waterskin` applies buff 711, which lasts its listed `Dur` in spell rounds like any cast buff — `use` applies it engine-side exactly as casting `bless` would *([CONFIRMED] 2026-09-26, user)*: 600 × 3s = **30 min** (see *Timing & rounds → Spell round (3s) and durations*; an earlier note said "600 = 10 min game-time", which was wrong). You are protected only **while the buff is up** — carrying the item alone does nothing. If you're still in a desert/hazard room that needs the buff when it **expires**, you must `use waterskin` **again** to re-apply it.
- **Each `use` consumes one charge; a fresh waterskin carries 3 charges** *([CONFIRMED by user])* (the item's `Uses` field). When a waterskin is spent, you need another one — so players typically carry **2–3 waterskins** into the desert. Provisioning must therefore stock enough total charges to cover the expected time in the hazard stretch, not just "one waterskin."
- **Routing model:** carry the source item(s), `use` on entering the first hazard room to raise the buff, and **re-`use` whenever the buff lapses while still inside a hazard room**, consuming a charge each time; when charges run out mid-stretch and no spare waterskin remains, halt rather than walking a room unprotected.
- **There is NO wear-off message for the waterskin buff** *([CONFIRMED by user])*. So routine refresh cannot be reactive — the client **must TIME it** (predictively re-`use` a margin before the buff's `Dur` would expire; this is the PRIMARY refresh). The lapse prompt below is only a **reactive backstop**: when the timer's estimate is off and the buff drops early, the room re-emits the prompt and the client fires **exactly ONE** `use waterskin` to re-raise — not a client-side wear-off reaction (there's no such line), but a correction to a mistimed timer.
- **Lapse / sandstorm spells are derivable from the checkspell chain.** `checkspell 711 2654` — the token's second int (2654) is the buff-ABSENT target TB; that block's `cast` is the lapse-damage spell. In the desert that's **spell 712 "desert damage"** (its CasterMessage is the thirst prompt); **spell 713 "desert sandstorm"** is the separate random-chance teleport, not a lapse signal.
  - Stock's TB 2658 → 2659 has the same shape. On **Paradigm**, TB 2654 and 2659 don't exist, so the thirst damage and sandstorm don't come from that branch there; holding the sunstone (not wearing it) still prevents **both** the thirst damage and the desert teleport *([CONFIRMED] 2026-09-26, user)* — see the possession bullet above.
- **Trigger + confirmation messages** *([CONFIRMED by user])* — all in the Messages game-data table, so match by record number, not hardcoded realm text. **These lines are plain text with no `{s}` placeholder**, so they're matched by literal case-insensitive substring, not the caster-message regex:
  - Desert lapse prompt — drink now (Spells#712): `You suffer in the desert heat... you need water, soon!` The game's own signal that the buff has lapsed while still in the hazard. Fire ONE `use waterskin` on this line.
  - Self re-`use` success (Spells#711): `You take a swig of water from your waterskin.` Confirms a charge burned and buff 711 re-applied. A `use waterskin` that draws no such line before the NEXT lapse prompt means charges/waterskins are exhausted → halt, don't walk on unprotected.
  - Witnessing a party member drink: `<name> takes a swig of water from a waterskin.` How the leader observes a follower successfully re-buffing (each member reacts to their own desert prompt).

**Client use:**
- `RoomHazardIndex.ScanTextBlock` parses both `checkspell` and `failspell` into the same buff-counter, guarded on an item actually casting the buff (so a `failspell` on a buff no carried item raises is ignored).
- A `failitem` guard found by chasing a buff-gate's `random`-linked failure branch is folded into the SAME requirement group as the buff source, so having **either** the waterskin **or** the sunstone clears the desert for routing. This mirrors how the river raft/canoe `failitem` guards (top-level, not nested) let a boat-carrier route the river.
- The `BuffCounter` also carries the immunity guards, so the buff-refresh provisioner **skips the `use waterskin` entirely** when a sunstone is held (carried or worn) rather than spending a pointless charge (report `paradigm-20260827-112011`).
- The client resolves the lapse prompt via the Messages record linked to Spells#712, so it tracks the active set rather than hardcoded realm text.

### Hazard severity — survivable damage vs grave
*Status: CONFIRMED 2026-09-02 (user + game-data trace, Paradigm 1.9.1) · Realm: Paradigm*

Among protectable hazards, a further split governs whether the navigator may offer to **cross it unprotected** (walk in and eat the effect on the user's say-so):

- **Survivable damage** — the unprotected outcome is only a `Damage` hit: a direct `Damage` ability, OR a TextBlock `cast` of a spell whose own chain is `Damage`-only.
  - Example: the **Silver River** (`Rooms.Spell 753`) → TB `failitem 690/691/1181/3609` (any raft; the `3609` guard is Paradigm-only — stock's TB 2750 has `690/691/1181`) `: cast 754`, where spell **754 "battered"** is a plain `Damage` spell. You just take the hit and keep walking, so "cross unprotected — take the damage" is a legitimate choice (survivable = can't displace you or start a death timer; it can still kill a weak character — see *Teleport vs walking route choice*).
  - Lava / magma heat, `Spell 526`, is the same shape — a direct `Damage` ability.
- **Grave** — the chain reaches an `EndCast` death-timer, a `teleport`/`transfer` (forced relocation), or a `checkspell`/`failspell` buff-gate (drown / suffocation / desert-heat-death). These can END or DISPLACE you, not just hurt you, so the counter is the only safe way past — the navigator must **never** offer to cross them unprotected.
  - Conservatively, ANY `EndCast` or buff-gate is treated as grave.

**Client use:**
- Encoded as `RoomHazard.IsSurvivableDamage`; classifier `RoomHazardIndex.IsSurvivableHazardDamage`.

### Door and gate barriers in the room display
*Status: CONFIRMED 2026-07-14 (capture, report 091244)*

- **A lever-raised gate renders in the live room display as a *gate*, not a *door*.** At `1/1331` the
  `Obvious exits:` line reads `closed gate north, south, east, west`; after `pull lever` ("you hear the
  loud nearby rumbling of a gate") it becomes `open gate north, …`.
- **The barrier noun on the wire is "gate", and the open/closed prefix carries its live state exactly
  like a door's.** Treat "gate" and "door" as the same door-type barrier class for display parsing.
- **Client use:**
  - `RoomDisplayParser.ParseExits` strips an `<open|closed> <door|gate>` prefix off each exit token,
    feeding the open ones into `OpenDoorDirections` so the walker skips the door-open FSM on an
    already-raised gate.

### Multi-action exits and levers
*Status: CONFIRMED (timed window, fire-and-forget); CONFIRMED 2026-08-17 (user; relative order, nesting); CONFIRMED 2026-07-28 (capture, report 180730 — supersedes report 195552; redundant levers) · per-fact tags inline*

- **[CONFIRMED] A `(Hidden, Needs N Actions, {any|specific} order)` exit unlocks by issuing the listed
  command(s) from the named room + exit direction.** The action room can differ from the room the exit
  lives in (the "cross-room" case): e.g. pull a lever in room A to open an exit in room B.
- **[CONFIRMED] Once opened, the exit stays open for a timed window of roughly 3–5 minutes that is NOT
  encoded anywhere in the game data.** Long enough to walk from the action room to the exit room and
  cross without racing a re-lock.
- **[CONFIRMED] Specific-order across rooms is tolerant of walk time.** For "Needs 2 Actions, specific
  order," performing action #1 (in its room) stays satisfied through the same ~3–5 min timer; you then
  walk to action #2's room and perform it, which opens the target exit, and *that* exit then stays open
  another ~3–5 min. No tight contiguous-run requirement.
- **[CONFIRMED] Confirmation is unmatchable — each action is fire-and-forget.** Each unlock action
  **does** produce a visible server response, but the wording is **different per action** and those
  TextBlocks are **not shipped in the game data**, so the client cannot await a known confirmation
  string. Send the command, don't wait for a specific reply, then proceed to the next step / the
  cardinal.
- **[CONFIRMED, user 2026-08-17] A "specific order" gate tracks ONLY its own sequence levers, in
  RELATIVE order.** For `Needs N Actions, specific order`, the gate only requires that ITS OWN actions
  fire in relative order (#1 before #2, #2 before #3, …). Pulling a lever for a *different* exit (e.g.
  the entrance lever of an alcove you must open to reach the next sequence lever) between two of the
  gate's ordered steps does **not** reset the sequence. So the client may interleave: open alcove *i*,
  pull sequence-lever *i*, repeat — the sequence stays valid.
- **[CONFIRMED, user 2026-08-17] Nested action gates can chain, and persistence covers the compound
  walk.** This lets the walker solve *nested* lever vaults (a lever whose room is itself behind another
  action-gated exit). An opened action-gated exit stays passable for the same minutes-long window, and
  opening several nested gates and re-crossing them all lands well inside it — so a multi-gate compound
  detour is safe.
  - **Grounding example (data-Paradigm-1.9.1): the 6/861 tomb vault → 6/924.** Descent
    `6/861 →D→ 6/922 →D→ 6/923 →D→ 6/924`; `6/861` D needs 4 ordered levers, in alcoves 6/919 / 6/921 /
    6/920 / 6/918. Each alcove is behind its own `Needs 1 Actions` entrance gate whose lever sits in a
    freely-walked Ancestral Tomb room (6/889 / 6/917 / 6/903 / 6/875) — genuinely one level of nesting.
- **[CONFIRMED, capture 2026-07-28 report 180730 — SUPERSEDES the earlier report-195552 "both needed"
  claim] Two guardroom levers with identical commands on one gate are REDUNDANT alternatives — one pull
  raises it.** Some `Door` exits are raised not by a same-room verb but by a `pull lever` performed in
  one or more *other* rooms — the game data annotates the door exit with an
  `Action[#N] [on the {dir} exit of room M/R]: pull lever` cell per lever room. At the Newhaven castle
  inner gate `1/1331 N`, the two guardrooms `1/1345` and `1/1339` each carry a lever, and
  **both cells carry the identical command list and a bare `Action` (StepNumber 1)** with **no `Needs N`
  modifier** on the door.
  - Scrollback proves one suffices: the exits line went `closed gate north` → `open gate north` after
    the *first* `pull lever` and stayed open through the redundant second pull. So same-command,
    same-StepNumber levers on one exit are interchangeable — pulling either raises it. (An earlier run
    misread a failure as "both must be pulled"; this direct capture retracts that.)
  - A **genuine** multi-step gate is one whose data declares an explicit `Needs N Actions` modifier or
    distinct `Action#1`/`Action#2` StepNumbers — those pull every step.
  - A same-room lever variant also exists (`1/1375 S`, the courtyard, whose lever is on this room's own
    W slot — one action, no remote detour).
- **Client use:**
  - Walker takeaway: walk-to-action-room → send the command(s) in `StepNumber` order → walk-back to the
    exit's room → send the cardinal. The generous open window makes normal walk distances safe; do not
    gate on a data-supplied timer (there isn't one) or on parsing a confirmation line.
  - `RemoteActionPathExpander` opens nested gates recursively (open the inner door, then cross), bounded
    by a nesting-depth cap + a lever-cycle guard; past those it clean-fails. This is fully generic off
    the exit graph — no per-area code (the Asylum + Pyramid remain the only bespoke area solvers).
  - A lever `Door`/`KeyLocked` exit carrying action cells is promoted to `MultiActionHidden` at
    graph-build; the required-action count is the number of DISTINCT StepNumbers (same-StepNumber levers
    count as one), and the path expander pulls one cheapest alternative per StepNumber — so a redundant
    pair pulls once, a declared multi-step gate pulls all.

### Room-command reveal exits
*Status: CONFIRMED 2026-08-18 (user + game-data trace) · Realm: Paradigm (1.9.1)*

- **Context:** one of two exit shapes beyond ordinary cardinals / doors / CMD-teleports that the walker
  can route through, both on the route to the Necromancer (9/1431, phoenix-feather quest). (The other is
  the item-use teleport — see *Item-use teleports*.)
- **A room-command reveal is a lever whose opener is the room CMD.** A hidden exit
  (`(Hidden/Needs N Actions)`) whose unlock isn't an exit `Action` cell but a `remoteaction` in the
  **room's CMD chain**.
- **Canonical: 9/1012 "Crumbling Ruin, Entrance" CMD 1422 =
  `clear rubble:testskill strength 0 …: remoteaction 1012 … 0`** (synonyms `move`/`push` ×
  `rubble`/`mound`/`rock`; the trailing `0` = the N exit). You type `clear rubble`, the N passage to
  9/1013 opens.
- **The reveal is one-directional.** Only the closed side (9/1012→N) needs it; the reverse
  (9/1013→S→9/1012) is a normal always-open exit.
- **The opened exit stays open a few minutes.**

### `testskill` obstacle checks
*Status: CONFIRMED 2026-08-18 (user + game-data trace) · Realm: Paradigm (1.9.1)*

- **`testskill <stat> <difficulty> <failTextblock>` is a stat check with a random roll.** Found in a
  room-CMD / greet / spell action chain: the game rolls a die in a range tied to the difficulty and
  compares it to the character's `<stat>`.
  - Roll **under** your stat → the check **passes** and the chain's follow-on action fires (a
    `remoteaction` reveal, a `teleport`, etc.).
  - Roll over → the **fail action** runs (jump to `<failTextblock>`), which often lands you somewhere
    unintended.
  - Example that DOES roll: the Slums slaver-rooftop `jump` exits carry `testskill agility` and can drop
    you into the wrong room on a miss.
- **Difficulty `0` is the special case: it never actually checks anything — the action always fires.**
  So `clear rubble:testskill strength 0 …:remoteaction …` behaves like a plain lever/reveal, not a
  gamble.
- **Client use:**
  - The walker treats a difficulty-0 `testskill` reveal as a deterministic action (send the keyword, then
    take the exit — no fail-handling).
  - A non-zero `testskill` exit is NOT auto-traversed as a sure thing.

### Room-wide search during and after combat
*Status: CONFIRMED 2026-07-27 (user; report `paradigm-20260727-185836`); start-room search from report `paradigm-20260909-055045`*

- **A room-wide `search` (`sea`) is blocked only while you're *actively engaged* in combat.** Sent
  mid-fight — right after the attack-announcement lines — it's lost; the game won't process a whole-room
  search until the room is clear of hostiles.
- **Out of combat it's a quick command→reply** (just the ~150 ms network latency, no server-side delay),
  and the reveal doesn't always surface *everything* hidden (that's fine). *([NEEDS CONFIRMATION] does
  this apply to hidden items/players only, with stashed coin always surfacing?)*
- **An empty search answers with `Your search revealed nothing.`**; a reveal surfaces the
  `You notice … here.` survey.
- (Targeted `sea <dir>` hidden-exit reveals are a separate path — see *Hidden exits — `sea <dir>` reveal
  wording*.)
- **Client use:**
  - Auto-search holds the `sea` past the fight and fires it **once** the room clears, then keeps the
    walker held briefly so the revealed `You notice … here.` survey lands and the get engines collect it
    **before** the loop sets up sneaking and steps on. One search per room; empty rooms (no fight)
    search on entry as before.
  - **The post-search hold is released *reactively*:** the walker is let go the instant
    `Your search revealed nothing.` arrives rather than sitting out the whole settle window — so a room
    with nothing hidden costs only the command→reply round-trip, not a fixed per-room wait. The settle
    window survives only as a short fallback for the case where the reveal *does* surface loot (the
    `You notice …` survey), which the get engines then need a beat to collect.
  - **The room a walk / loop / auto-lair STARTS from is searched too** (report paradigm-20260909-055045):
    auto-search normally arms on room *entry*, but the room you're standing in when movement begins was
    entered earlier — before auto-search was armed, or at login — so it never got that entry search.
    Movement start arms + searches it before the walker steps out, deduped against the last room actually
    searched so a loop's later legs (each starting from a room already searched on arrival) don't
    re-search.

### Toll exits
*Status: CONFIRMED*

- **Toll exits gate on total wealth, not a specific coin.** A room exit tagged `(Toll: N)` in the map
  data requires the crosser to carry a **wealth value of `N × 100`** (copper farthings — the same
  consolidated `Wealth:` figure), held **on them** (carried coin, not banked).
- **The refusal line reads `You do not have enough to cover the toll of N gold crowns.`** — but "N gold
  crowns" is just how the message phrases the copper-value bar (`N` gold = `N × 100` copper), NOT a
  demand for that coin specifically: any mix of denominations totalling `N × 100` copper-value passes.
  So affordability is `TotalCopperValue >= TollGold * 100`.
- **The check is per-crosser.** Every party member needs their own `N × 100` on hand, and a member who
  can't cover it is refused at the gate and left behind while the rest pass.

### Gating a party's route through toll and level exits
*Status: CONFIRMED*

- **A leader routing the party must confirm every member can cross before taking a toll / level
  route**, because a toll is per-crosser.
- **Toll:** poll the party with **`@wealth`** (each member's client replies with their wealth, same
  round-trip shape as `@health` / `@level`).
  - If **all** members reply AND each can cover the toll (`wealth >= TollGold * 100`), the route may use
    the toll room; if **any** member can't cover it (or doesn't reply), **avoid that toll room for this
    passing**.
  - Wealth changes constantly (loot / spend), so it's polled fresh at planning time rather than cached.
- **Level:** use the member level already **stored in game data** (each player's recorded level); only
  when it's suspected stale, **re-poll in the room with `@level`**. A member outside the exit's
  `(Level: MIN to MAX)` window means the party routes around that exit.
  - Every client answers `@level` — formats vary by client, parsed leniently (the parser also tolerates
    the `{ }` wrap another MudPlay client adds to its reply).
  - **Until a member answers, their `who` title's level band is used with the LOW end as the
    conservative floor** (they could be as low as the band's minimum, so a `MinLevel` gate clears only
    if even that floor clears it).
  - An exact reading counts as **fresh only for the current local day** — a member could have levelled
    since.
- **Client use:**
  - Toll half is implemented: `MovementFilter.IsTollGateBlocked` + `PartyWealthProbe` /
    `PartyWealthTracker`. Wealth is **demand-polled**: `MinWealth` fires the `@wealth` probe only while BFS is
    evaluating a toll exit, and a follower with no fresh reading gates the toll, so the first plan
    routes around it while the probe warms up.
  - Level half is implemented: `MovementFilter.IsExitBlocked` + `PartyLevelProbe` /
    `PartyLevelTracker`. **Always-on** — routing a following party around a gate it can't clear is never
    wanted OFF (the alternative strands a member), so there's no opt-in toggle; the only gate is "am I
    leading a party".
  - An exact level reading comes from an `@level` reply and is stamped with the time it was learned
    (`PlayerObservation.LevelAt`); `PartyLevelTracker.WarmStaleLevels` re-fires `@level` for any unknown
    member, or one whose reading isn't from today, when a walk's planned route actually crosses a level
    gate.
  - That freshness poll is route-scoped and debounced the same way the `@wealth` toll poll is: fired from
    `MovementFilter.WarmForRoute` only when the levels-permitted shortest route genuinely uses a level
    gate. The ordinary keep-warm refresh is the once-a-day party probe (see
    *Party → Party intel probe: `@level` / `@version` on partying*), not a roster-change poll.

### Room-level entry blocks — an early-engine mechanism with no MDB representation
*Status: CONFIRMED 2026-09-14 (user + other devs + data check) · Realm: Stock (Paradigm differs)*

- **Rare — the first room of this kind encountered, and probably a leftover of early MajorMUD engine
  design.** Do not expect the pattern to be common; treat it as a known one-off until another turns up.
- **The block is a property of the room being ENTERED, not of any exit.** `1/2150` ("Newhaven, Arena")
  carries an engine-side level range of **1 to 3**. The engine does not test it while traversing an
  exit — it tests when you attempt to **enter the room**, so every way in is blocked by the same rule.
- **Neither the room's range nor the block appears anywhere in the MDB.** This is not an exporter
  dropping a field: the MDB has **no field for a room-level gate at all**. The mechanism predates the
  table format.
- **Do not confuse it with an ordinary exit-level gate**, which is a real and fully-supported MDB
  feature. Both exist in the same room on stock `1.11p`:

  | from `1/2146` | destination | in the MDB |
  |---|---|---|
  | `W` | `1/2190` Newhaven, Healer | `(Level: 1 to 3)` — a genuine **exit** gate |
  | `D` | `1/2150` Newhaven, Arena | nothing — the **room** block, unrepresentable |

  The two produce a similar player-facing effect and are unrelated in origin.
- **`1/2150` has three inbound exits on stock** — `1/2146 D`, `1/2152 S` (Dungeon, Entrance) and
  `1/2155 U` (internal to the Arena) — and the room block applies to all of them, which is why no single
  exit modifier could express it.
- **Paradigm's dats edited this exit** so the gate does surface there as an exit modifier
  (`D -> 1/2150 (Level: 0 to 5)`, a different window from stock's room range).
- **There is no room/exit overlay tier** — overlays cover Items and Monsters only — so this cannot be
  corrected with a per-tier game-data override.
- **General lesson: absence of a gate in the data is not proof the game has no gate.** Treat a refusal
  on an exit the graph believes is open as possible evidence of a room-level block.
- **Client use:**
  - `RoomGraphManager.ApplyStockArenaEntryBlock` stamps `MinLevel 1 / MaxLevel 3` on the stock
    `1/2146 D → 1/2150` exit at graph-build (Stock only; skipped if the exit already carries a gate), so
    the router, route picker and room tooltip read it like the west exit.
  - `MovementFilter.IsLevelGateBlocked` short-circuits on `if (!exit.HasLevelGate) return false;`, so any
    inbound left ungated — the `1/2152 S` inbound is still ungated in the graph — lets an over-level
    character be routed into the Arena and refused by the server. The refusal is handled (it reads as a
    failed move, not a stall), but nothing stops the router re-planning the same way through an ungated
    inbound.

### Guardian-monster doors opened by `ask`
*Status: CONFIRMED (game data v1.11p map 9, MMUD Explorer cross-check)*

- **A placed "guardian" monster whose greet dialogue raises a door on its own room is opened by
  `ask <monster-noun> <topic>` — the spoken password lifts the gate.** Some pick/bash-proof `Door` exits
  aren't operated by a room verb or a remote lever at all; the barrier is a stationed monster who lifts
  it when the player asks the right keyword.
- **Confirmed case: the grove shadow guard.** Room `9/1423` carries `Lair='(Max 2): 503,[…]'` placing
  shadow guard **#503** (`GreetTXT 1433`), and the door `9/1423 W → 9/1425` (Morukai's chamber) has an
  impassable stat requirement so it can't be picked or bashed.
  - The greet decodes as: block `1433` lists topics `morukai / orfeo / passage / phoenix / prophecy`,
    each pointing at `1435` (empty, `LinkTo 1436`); block `1436` is
    `checkability 133 4 : remoteaction 1423 66 0 3 : message 1841`.
  - So asking any of those five keywords fires `remoteaction 1423 … 3` — **direction index 3 = W** —
    operating this room's own west exit.
- **The spoken command is `ask <noun> <topic>` where `<noun>` is the last word of the monster's name**
  ("shadow guard" → `guard`), e.g. `ask guard morukai`.
- **The five topics are alternatives** that all open the same door — the walker sends only one.
- **The open is quest-gated and the gate is untrackable by the client.** `checkability 133 4` gates the
  lift on ability **133 = PhoenixQuest**; the client doesn't pre-read the flag for routing (see
  *Quests → Quest-flag values (`abil` / `sys god abil`)*), so the crossing is **reactive**: promote the door to routable, issue the `ask`, attempt the move, and react to
  whether it actually opened (halt/replan if not). Do **not** try to pre-check the gate.
- **Client use:**
  - Identical promotion to the lever-door case (*Multi-action exits and levers*): a `Door`/`KeyLocked`
    exit fronted by a greeting monster whose `remoteaction` targets *its own room* and names *that exit*
    is promoted to `MultiActionHidden` at graph-build, folding the resolved `ask` command into the same
    `byExit` action table the `Action#N` lever cells populate (`GuardDoorCommandResolver` +
    `RoomGraphManager.InjectGuardDoorActions`).
  - The crossing then reuses `SpecialExitDispatch`'s ask-then-move path.
  - Monster ids come from the room's `Lair` group (and its single placed `Npc`); only monsters carrying a
    `GreetTXT` are considered.

### Keyword command forms (`ask <noun> <keyword>` vs verbatim room CMD)
*Status: `ask` noun rule — as for guardian doors; command-form split CONFIRMED 2026-07-23 (user); keyword model CONFIRMED 2026-09-26 (user)*

- **The NPC's full name always works; so, usually, does its first word** *([CONFIRMED] 2026-09-26,
  user)*. `ask archmage valduin crystal` is safe, and `ask archmage crystal` usually works too; some
  monsters also accept odd shorthands. The 2026-07-23 note's `ask gnome orb` is the first-word form of `Gnome Commander`.
  - An older rule here said the parser takes ONE target token, so a multi-word name must reduce to its
    **last** word (`ask commander orb`, **not** `ask gnome commander orb`). That is superseded — the
    full name works — and the last word alone is **not** confirmed to work.
  - **Client use:** the guardian-door, greet-teleport and path-item give commands send the full name
    with a leading article dropped (`GuardDoorCommandResolver.AskTarget`, formerly the last-word
    `LastWord`); the quest planner also sends the full name.
- **`ask` is the command for talking to an NPC through its keywords: `ask <npc name> <keyword>`** *([CONFIRMED] 2026-09-26, user)*. It runs whatever actions sit under that keyword.
  - Some keywords carry **checks with pass/fail criteria** and do different things depending on the result.
  - An NPC's keywords are listed in game data: the monster's `GreetTXT` textblock holds `keyword:textblock` lines. For example, Paradigm 1.9.1 gnome commander #332 has `GreetTXT` 809, which lists `dark-elf` / `plots` / `slaves` / `orb` / `passage`. `orb` → 814 → 815 `giveitem 807`, the item he drops, so `orb` is the keyword that hands over the orb. (The full dark-elf front-door chain is in *Route gate items — crossing vs acquiring, required vs optional, reliable vs unreliable*; detecting the hand-over is in *Items, inventory & equipment → NPC keyword hand-over detection*.)
- **The same `ask <noun> <keyword>` noun rule backs the path-item give router.** A giver NPC that hands
  over a path-gate item via a deterministic `giveitem` (an `ask <keyword>` dialogue award) is addressed
  by the identical single-word target.
- **[CONFIRMED, user 2026-07-23] Keyword command form depends on the TBInfo trigger's root:** an
  **NPC-attached** keyword is issued as `ask <npc name> <keyword>` (e.g. `ask gnome orb`); a **room CMD**
  keyword is typed **verbatim** (e.g. `rub orb`, `touch statue`).
- **Keyword strings are fixed in the TBInfo `Action`/keyword data, and the client can read them.**
- **Client use:**
  - The give router reuses `GuardDoorCommandResolver.LastWord` for the single-word target; only the
    picker's human-readable "(ask …)" promise keeps the full name.

### Cast-on-walk exits and random teleports
*Status: CONFIRMED (game data v1.11p map 9)*

- **A `(Cast: pre-N, post-M)` exit fires a spell as part of the walk — pre-N before the move, post-M
  after.** The exit stays a plain cardinal move (its cell modifier carries the two spell numbers; `0`
  means no cast on that side).
- **When the post-cast spell is a *random* teleport, the exit's landing is non-deterministic.** The
  spell's game-data record classifies its landing:
  - ability code **140 = TeleportRoom** — value `0` → a *random* room drawn from the spell's
    `MinBase..MaxBase` base range; value `>0` → a single *fixed* room.
  - **141 = TeleportMap** — the destination map.
  - A room-teleport with value 0 spanning more than one base room (and inside the defensive
    `MaxRandomRange` ceiling of 64) is the random case; a fixed room, a single-room range, or a
    non-teleport spell is deterministic.
- **The confirmed real case is the Warped Asylum** (map 9, rooms 1183–1290 reached from the Rhudaur
  side): those rooms carry a mix of plain cardinals and cast exits whose post-cast spell (596 / 597)
  random-teleports the caster into roughly the `[1183,1206]` band.
- **The random-teleport exit is NOT routable.** Its non-determinism, not a gate, is what rules it out;
  the walker can only be routed through the area's *plain* exits and its single-destination teleports —
  a random landing can't be planned.
- **But the random-teleport exit still LAYS OUT on the map.** Its nominal target is a real adjacent
  room, and the Warped Asylum's cast grid is fully reciprocal — laying those exits out normally renders
  the whole connected area, where portalling them away would strand ~90% of the rooms (from room 1259,
  ~10 rooms are reachable by plain exits vs ~108 by cast).
- **A one-way cast "pocket" is not overdrawn onto its housing map.** A cast area can be a *sink* —
  entered by a single cast-on-walk exit with no walk-back, so it lives on but topologically apart from
  the surrounding map. The Warped Asylum is one: its 108 rooms are reachable only via one cast mouth
  (`1182 W → 1183`, `(Cast: pre-0, post-596)`) and have zero exits back out (stock data; Paradigm adds
  the `9/1259` pull-lever back to `9/1180`, which the client neutralises at graph-build — see
  *Random-teleport maze (the Warped Asylum)*). Laying the pocket out from
  a housing-map origin poured all 108 rooms into the housing map's coordinate grid, drawing them on top
  of it (the Rhudaur overlay).
- **Pocket entrance is a *topology* discriminator, orthogonal to `CastTeleportRandom` (a
  *predictability* one).** A cast exit `a→b` is a **pocket entrance** iff `b` cannot reach `a` by *any*
  directed route. The asylum mouth is both, an internal reciprocal cast exit is neither, and a fixed
  one-way cast-teleport into a sink would be a pocket entrance without being random.
- **Client use:**
  - BFS pathing (`FindPath` / `ComputeDistancesFrom`) skips any exit flagged `CastTeleportRandom`, even
    when exit gates are being ignored.
  - The map marks each cast-on-walk exit with a short perpendicular "wall" glyph in the Spell colour,
    drawn between the two rooms, so the player sees the whole area with its spell-gated exits visually
    flagged rather than a sparse fragment. `RoomExit.CastsOnWalk` drives the render mark;
    `CastTeleportRandom`, set by the spell-catalog classification pass at graph build, drives only the
    router prune.
  - `RoomExit.CastPocketEntrance` is set by a graph-build reachability pass. The planar mapper stops
    expanding through a pocket entrance, so from outside the pocket shows only as a spell-wall stub at
    its mouth — but a walker standing *inside* still lays the whole area out, because the pocket's
    internal cast exits are reciprocal (they have return paths) and so are never flagged as entrances.

### Quest-gated gateway portals
*Status: CONFIRMED (game data Paradigm 1.9.1 map 9) · Realm: both (byte-identical across stock and Paradigm data)*

- **A quest-gated portal keyword can teleport to a fixed room for flagged characters but a *random*
  room for everyone else — so it is routed as a last-resort "gateway", never a plain shortcut.**
- **Worked case: room `9/1291` ("Ancient Darkwood Tree, Portal")** has `CMD 1462`, whose TBInfo fires the
  same keyword three ways on ability **133 = PhoenixQuest**: `go portal:checkability 133 5:cast 620` /
  `go portal:testability 133 4:cast 621` / `go portal:failability 133:cast 621` (and identical
  `enter portal` lines).
  - Spell **620** "lower portal (invited)" is `TeleportRoom 1424` → a **fixed** hop to `9/1424` (the
    character has talked to Morukai and is quest-flagged).
  - Spell **621** "lower portal (uninvited)" has `TeleportRoom 0`, `MinBase 1292 / MaxBase 1327` → a
    **random** dump into the Caves of Chaos (`9/1292–1327`) for anyone not flagged.
- **The only observable difference is *where you land*; the quest ability is untrackable by the
  client.** Byte-identical across the stock and Paradigm data sets, so the rule is realm-generic.
- **Client use:**
  - When a cast-teleport keyword's branches disagree — a fixed branch alongside a random (or a
    different-room fixed) sibling — the landing is non-deterministic, so it is minted as a **gateway**
    `Direction.Teleport` edge (flagged `GatewayTeleport`, nominal target = the fixed branch's landing
    `9/1424`) rather than a plain shortcut (`RoomGraphManager.TryFirstRoutableTeleport` classifies each
    keyword; a keyword with *every* branch a fixed hop to the *same* room stays a plain edge).
  - BFS routes in two passes (`BfsMapper.FindPath`): a deterministic pass that ignores gateways, then —
    only if that finds nothing — a fallback pass that may cross one.
  - So from *inside* the cluster the gateway is never used (BFS prefers the deterministic narrow-stair
    climb `9/1413 → U … → 9/1422 → N → 9/1423 →` guard door `W → 9/1425`), which is what stops an
    unflagged character looping down through the random portal; but from the *overworld* tree base
    (`7/1360`), where the portal is the only way up, the fallback pass crosses it.
  - The walker re-plans from wherever the cast actually drops it (flagged → `9/1424` and continues;
    unflagged → a random caves room → re-plan into the cardinal stair climb to Morukai).
  - A pure `IsRandomTeleport` cast with no fixed branch to anchor a nominal target stays fully
    non-routable (`CastTeleportRandom`, skipped in both passes).

### Item-use teleports
*Status: CONFIRMED 2026-08-18 (user + game-data trace) · Realm: Paradigm (1.9.1)*

- **An item-use teleport is an item whose USE transports you** — the second special exit shape on the
  route to the Necromancer (9/1431, phoenix-feather quest). Distinct from CMD / greet / room-spell
  teleports: the teleport lives entirely on the **item**, via the chain item `Abil 43 (CastsSp)` → spell
  → spell `Abil 148 (TextBlock)` → TBInfo whose Action has a literal `teleport <room> <map>`.
- **Canonical: potion of levitation** (item 992 → spell 607 → TBInfo 1421
  `roomitem 993 …: teleport 1009 9`) — used in **3/1** it transports you to **9/1009**.
- **It is gated two ways:**
  - you must **carry** the item (it's a single-use consumable, spent on use);
  - the TBInfo's `roomitem <fixture>` gate means it only fires in the room that holds that fixture (item
    993 "waterfall" lives in 3/1, which is why it "must be used there").
- **The return trip is a normal exit** (9/1009→D→3/1), so the potion is never needed to come back.
- **This is likely the ONLY item-only-anchored teleport in the data.** The nightblack portal
  (item 1419) is not one *([OBSERVED] 2026-09-26, game data)*: it's a room fixture (`Encum 9999`,
  abilities `Remove@Maint` / `Visible@Maint`, no teleport ability of its own) placed in many rooms,
  and each host room's `CMD` textblock does the teleport — `go portal` / `enter portal` /
  `go nightblack portal` with `roomitem 1419`, a per-portal `minlevel` (40 / 50 / 60) and a fixed
  `teleport` destination. You never carry it.
- **Partied: every member needs their OWN potion**, and the crossing is a party-relay — the leader must
  tell the party to use theirs *before* using its own.
- **Client use:**
  - The client anchors its graph edge on the fixture item's own room (its `Obtained From`), since there's
    no exit / CMD / greet to hang it on.
  - The item-use teleport is an ordinary `Teleport`-hint edge, so the standard teleport party-relay
    already handles it: a leader with followers sends `.@party use potion of levitation`, THEN uses its
    own `use potion of levitation`. Every teleport relay takes this `.@party <keyword>` form: the leading
    `.` sends the line as a say, so the followers in the room all hear and act on it (a say-relay — no
    per-player grant needed). Item gives, by contrast, go out as a bare `@party give …`.
  - The one thing the client can't verify is whether each follower actually carries a potion — a member
    without one is left behind (we don't track follower inventory).

### Teleport vs walking route choice
*Status: CONFIRMED 2026-07-23 (user)*

- **A teleport shortcut is usually far shorter than the equivalent walking route but can drop the
  character somewhere lethal.** An item/CMD-cast teleport exit (`RoomExitHint.Teleport`, promoted from
  an `(Item: N)` exit on a `CMD > 0` room) is a plain one-hop edge to BFS, so a walk-to will silently
  route through it as the shortest path — dangerous, because a teleport can land you in a **damaging
  plane** (negative power plane, black wasteland) or across **water with no boat** (Balthazar's teleport
  dropping you at the silver river, which is near-certain death without a boat).
- **Whether it's lethal depends on the character.** A high-level priest can out-heal the silver-river
  damage spell and cross freely, so the same teleport that kills one character is a fine shortcut for
  another. The client cannot judge this, so it must NOT silently take it — exactly like the
  acquire-item-vs-take-the-long-way choice, it presents the fork to the user.
- **Client use:**
  - On a user-initiated walk-to, if the shortest route takes a teleport AND a teleport-free walking route
    also exists AND the teleport saves ≥ 2 rooms, pop the route picker ("walk it, don't teleport" vs
    "take the teleport").
  - If there's no walking alternative the walker just takes the teleport (no fork to offer); if the
    shortest route already walks the whole way there is nothing to weigh.
  - `BfsMapper.FindPath(refuseTeleports: true)` backs the "walk it" side by refusing both
    `RoomExitHint.Teleport` and gateway-portal exits.
  - Automated walks (loops, death recovery, deposits, party comeback, trainer routing) never prompt —
    they keep the default teleport-allowed shortest route.

### Route gate items — crossing vs acquiring, required vs optional, reliable vs unreliable
*Status: CONFIRMED 2026-07-23 (user; dark-elf front door); OBSERVED (Paradigm 1.9.1 game data, cross-referenced; landmark IDs); CONFIRMED 2026-08-18 (user + game-data trace; quest items never auto-obtained); CONFIRMED 2026-09-13 (user + report `paradigm-20260913-100733`; required vs optional) · Realm: Paradigm (1.9.1) · per-fact tags inline*

- **[CONFIRMED, user 2026-07-23] A walled city can have a "front door" that is a
  keyword→item→summon→kill→key chain, entirely separate from any teleport "backdoor" the map data also
  holds.** The dark-elf city (Paradigm 1.9.1) is the worked example the client got wrong: it has two
  ways in, and the walk-to diagnostic named the wrong one. The **front door** is a multi-step gauntlet
  the player performs by hand — the map graph only encodes its final locked-door hop:
  1. Walk to the **gnome commander** (NPC in `8/459`) and `ask gnome orb` — he hands over the
     **bloodstone orb (item 807)**.
  2. Carry the orb to `8/398` and `rub orb` — consumes the orb and opens the **south exit to `8/403`**
     toward the gate. This step's gate IS surfaced on the room exit ("Needs 1 action: rub bloodstone
     orb / hold bloodstone orb / rub orb"), so once the orb is in hand the walker already knows how to
     cross it.
  3. At the **Black Steel Gate (`8/461`)**, `touch statue` — summons the **obsidian statue
     (monster 347)**; kill it and it drops the **gate key (item 806)**. This summon command is
     NOT surfaced anywhere on the map.
  4. The gate key opens the **town gate `8/461 → 8/462`** into the city — the exit shows
     `(Key: gate key)` but not how to obtain the key.

  The **backdoor** is the **nightblack portal** network — portal fixtures you walk into with
  `go portal` (see *Item-use teleports*), each gated behind a high **minimum character level**.
- *[OBSERVED — Paradigm 1.9.1 game data, cross-referenced]* **Front-door landmark IDs:** gnome commander
  in room `8/459`; bloodstone **orb item 807** (given by monster #332 via keyword `orb`, Textblock
  #809→#814→#815 `giveitem 807`); the `rub orb` consume-gate at `8/398`; the **obsidian statue monster
  347** summoned at the Black Steel Gate (`8/461`, Textblock #863 `touch statue:summon 347`, Called From
  Room 8/461), which drops **gate key item 806**; the gate hop `8/461 → 8/462` carries
  `Key: 806 or 101 picklocks`. The backdoor is **nightblack-portal item 1419**, whose teleport exit into
  map 8 is gated `minlevel 40` — e.g. the portal in `8/992` (Negative Power Plane, TB 9131) lands in
  `8/558` at level 40+.
- **The map surfaces how to *cross* an item/key gate, but NOT how to *acquire* the gating item (the
  crux of auto-traversal).** The consumption command and required item ride on the exit (`8/398` south
  names `rub orb` + bloodstone orb; `8/461` south names `Key: gate key`). Acquisition provenance lives
  only in the **TBInfo** game-data table: `ask gnome orb` → `giveitem 807`, and `touch statue` →
  `summon 347` (kill → drop 806) are invisible to the walker until it reads TBInfo. So the walker can
  cross a gate it holds the item for, but is blind to how to obtain that item.
- **[CONFIRMED 2026-08-18] Quest items on special-exit routes are never auto-obtained.** The potion (see
  *Item-use teleports*), plus the titanium fork / magical quartz rod that gate the ruin's barrier exits,
  are quest-locked, so a route missing one fails with a named "go obtain the &lt;item&gt;" message
  rather than trying to fetch it.
- **[CONFIRMED 2026-09-13] A route's item gates come in two kinds the route picker must not conflate:**
  - **Required gate** — the destination is *unreachable* without crossing it (no route avoids it).
    Example on the way to the dark elf city interior (8/1699): the **bloodstone orb** (item 807) — every
    route needs it **below level 40**. At level 40+ the portal from `8/992` lands in `8/558`, and from
    there 8/1699 is reachable through ordinary doors without the orb or the gate key *([OBSERVED]
    2026-09-26, reachability over the Paradigm 1.9.1 room data; exit gates other than the orb and key —
    doors, hidden exits — not modelled)*.
  - **Optional shortcut** — an item merely unlocks a *shorter* route; a longer route reaches the
    destination without it. Example: the **amber talisman** (item 815) opens a rooftop shortcut through
    the slums; a longer walk (through the city gate) avoids it entirely. An optional shortcut item is
    **never "required"** and is never auto-fetched.
- **[CONFIRMED 2026-09-13] Optionality is a topology fact** (does a route avoiding the gate exist?),
  independent of what the crosser currently carries.
- **[CONFIRMED 2026-09-13] Reliable vs unreliable sourcing** (orthogonal to required/optional):
  - **gate key** (item 806) — *guaranteed* obtain from a statue at the dark elf city entrance.
  - **bloodstone orb** (item 807) — *guaranteed* obtain from the gnome commander.
  - **amber talisman** (item 815) — *not* guaranteed: looted from the slaver leader on the slums rooftops
    (who may be dead), or from hidden player-made stashes. So even though it shortens the trip,
    detouring to fetch it can fail — the client presents it as the player's own call (the slaver-leader
    detour may or may not net out ahead, depending on what the player is doing), never an auto-obtain.
- **Client use:**
  - **Why the front door mattered for pathing:** the backdoor portal is the *shorter* graph route, so a
    blocked walk-to that re-probed by ignoring **all** gates surfaced it and blamed "a level
    requirement" — a door the under-level character was never going to take. The real obstacle is the
    front door's **acquirable** gate (fetch the gate key / carry the orb). Fix: the failure diagnostic
    now re-probes with only the *acquirable* gates (item / ticket / key-door / hazard) suspended first —
    level / toll / class stay active — so it describes the route the crosser would actually walk and
    names the key/item to fetch, and only falls back to the ignore-all probe when even that finds
    nothing.
  - Optionality is decided by re-running reachability with that one gate kept closed
    (`MovementFilter.SuspendAcquirableGatesExcept`); reachable ⇒ optional, disconnected ⇒ required.
  - For a sole route (no gate-free way there), `RouteChoicePlanner` commits the route that avoids every
    optional shortcut (so the walk takes the reliable way and any gate item the crosser already holds
    surfaces as "— you have it"), reports only genuinely-required unheld items, and offers the shortcut
    separately (`RouteChoice.ShortcutItems` / `ShortcutStepCount`) with the rooms it would save.

### Random-teleport maze (the Warped Asylum)
*Status: CONFIRMED 2026-07-17 (user design); solvable-room fast path CONFIRMED 2026-08-16 (user) · Realm: both (Paradigm lever handling differs)*

- **A "random-teleport maze" is a pocket of same-named rooms behind a one-way cast mouth whose interior
  random-teleports you on every step.** Normal position tracking collapses to Lost because every room
  shares a name and plain-exit fingerprint, so the walker can't source a route. The Warped Asylum is the
  canonical one.
- **The pocket is detected structurally, with no hardcoded room numbers:** it's the set of rooms trapped
  behind a cast-pocket entrance (`RoomExit.CastPocketEntrance`, defined in *Cast-on-walk exits and
  random teleports*) whose interior holds at least one random-teleport exit
  (`RoomExit.CastTeleportRandom`).
- **Because the asylum's entrance exit is *both* a pocket mouth and a random teleport,
  `BfsMapper.FindPath(outside, mazeRoom)` is always null** — the clean signal the walker uses to hand the
  destination to the maze solver instead of failing.
- **Relocalization is by a "1x2 signature", not the room name.** A random teleport only ever drops you
  into a *corridor* room, and within a pocket every corridor room's signature is unique.
  - The signature is the room's own obvious-exits mask **plus, for each of those exits, the neighbour
    room's obvious-exits mask** — the neighbour read live via **`look <dir>`, a passive peek that renders
    the neighbour's exits without moving or firing the teleport**.
  - Dead-end cells (reached only deterministically by walking, so never a teleport landing) have
    non-unique signatures and are deliberately omitted from the lookup rather than risk a mis-ID.
- **Solving:** relocalize from the signature → if a plain BFS route to the goal now exists, hand the final
  walk back to the walker; if the goal sits in a plain-disconnected component (reachable only by
  re-teleporting), **reshuffle** — walk a `CastTeleportRandom` exit to re-teleport and retry.
- **Runs on every realm; the relocalization method differs by realm.** Stock relocalizes by the 1x2
  look-sweep. Paradigm relocalizes with `rm` alone and never looks: `rm` returns the room number, which is
  distinct even though every asylum room shares one name, so it pinpoints the exact landing — every
  landing and every plain step re-locates by `rm`, and a dropped reply is re-sent rather than falling
  back to a look.
- **[CONFIRMED, user 2026-08-16] Once relocalized into a "solvable" room, the plain route to the goal is
  unhindered — drive it with no per-step re-location.**
  - The initial teleport-landing relocalization (the 1x2 look-sweep on stock, `rm` on Paradigm) still
    runs — that's how the solver confirms which room it landed in.
  - The moment that relocalization yields a room from which a plain BFS route to the goal exists
    (`FindPath(here, goal)` non-empty — the "solvable room" test), that route is a **deterministic,
    teleport-free corridor with no cast end-casts on it**, so the solver paces the moves straight out
    with **no per-step verification**: no `look` sweep on stock, no `rm` on Paradigm. The per-step
    re-location only existed to catch surprise teleports / blocked doors that this route is confirmed not
    to have, so it was pure spam.
  - The concrete asylum instance: **map 9, rooms 1200 / 1199 / 1197 / 1198** are the solvable rooms —
    from any of them the walk to the **old man (NPC #499, phoenix-feather quest start)** is unhindered.
    The trigger stays structural (any maze's plain-route rooms), with these four the confirmed real case.
- **[CONFIRMED, user design 2026-07-17] Paradigm asylum pull-lever = pocket dimension.** Only the
  Paradigm 1.9.1 data (not stock v1.11p) gives room `9/1259` a `pull lever` CMD teleport back to the
  entry area `9/1180`. That one escape edge would otherwise defeat the one-way pocket test (reachability
  walks the lever back out; the pocket-collection BFS balloons through it into the overworld), so the
  asylum would never be flagged/indexed as a maze on Paradigm. The lever is still a real in-game exit the
  player can pull manually — the client just doesn't route through it.
- **Client use:**
  - `look <dir>` responses reach the solver because `RoomDisplayParser.RoomParsed` fires before the
    tracker's look-suppression drops the peek.
  - Implemented in `TeleportMazeIndex` (detection + signatures) and `TeleportMazeSolver` (the state
    machine).
  - The Paradigm lever's routable edge is **not synthesised** (`RoomGraphManager.ParadigmAsylumLeverRoom`),
    making the asylum act as the same one-way pocket it already is on stock.

### Passive grid re-localiser (command-free)
*Status: Unrated*

- **Fills the same-name-grid + no-engine gap the two command-driven paths can't reach.** The
  authoritative locate (*Nav-recovery authoritative locate (`rm` / `sys st`)*) and the engine's forward localiser (tier 2 of its Lost recovery) both require something to send / an
  engine to drive. When neither is available — no `rm`/`sys st` power, no engine attached (automation
  stopped, walking a same-name grid by hand, or a party drag) — the passive re-localiser runs.
- **It sends nothing to the wire** (pure client-side inference) and **refuses to guess** — an ambiguous
  set that never narrows to one stays Lost.
- **Client use:**
  - `RoomTracker` runs the same `FootprintMatcher` SLAM narrowing passively off the moves it already sees
    (`NoteMoveSent` / `NoteFollowMove`) and the room displays, re-anchoring (`SetRoom`) the instant the
    walked sequence fits exactly one graph room.
  - It stands down whenever an engine is attached (gated on `EngineRecoveryGate.HasAttachedEngine`).

### Great Pyramid puzzle climb
*Status: CONFIRMED 2026-07-29 (user + capture `follower log of going up the pyramid.log` + hand-drawn map + game-data trace); undead-priest holds CONFIRMED 2026-07-30 (user + game-data trace) · Realm: Paradigm (1.9.1); stock noted where it differs*

- **Geography.** Starts at `Scorched Cavern, Firepit` = `12/1239` (its `up` casts spell 685 = timer).
  Great Pyramid = `12/1800–2085`, contiguous, 6 floors: F1 `1800–1920`, F2 `1921–2001`, F3 `2002–2051`,
  F4 `2052–2076`, F5 `2077–2084`, top `2085` (→ Tomb via `2085 U→2250`). Every room displays only as
  `Great Pyramid`, so room **number** is the sole identity.
- **Solver scope.** The solver only delivers the party to `12/2085` and stops there. The `e` sphinx at
  2085, the Tomb, Pharaoh Rastep, and the Dao Lord portions are all player-handled.
- **Sphinx ascensions are in game data — on the monster, not the room.** Each floor's ascension exit is
  `Hidden/Needs 1 Action` with no command on the *exit*; the action is delivered by the **stone-sphinx
  monster's `GreetTXT` textblock** as a keyword → `remoteaction <top-room>`:
  - `fire` (mon #548 @ 1920 → 1921), `sun` (#549 @ 2001 → 2002), `stars` (#550 @ 2051 → 2052),
    `e`/`letter e`/`the letter e` (#552 @ 2085 → 2250).
  - The F4-entry sphinx (#551 @ 2052) is **`riddle`-only** — the footpath hint, no ascension word.
  - `ask sphinx riddle` returns the clue on any sphinx.
  - Success broadcast: `With a loud grinding noise, a concealed passage opens in the ceiling!` → `u`. (The
    hand-drawn "time" is the riddle mnemonic; the accepted keyword is `e`.)
- **Fall/scatter.** The pyramid room-spells `691`/`692`/`700` (`cleanup`/`cleanup 2`/`cleanup 3`) each
  carry `Abil 115 = 66` with `MinBase/MaxBase = 1239/1278` — ability 115 reads Min/Max as a **random room
  range**, so a fail scatters you to a **random room in `12/1239–1278`** (the Scorched Cavern firepit
  cluster). A secondary path, `dao scatter` (742, cast only from `12/2251`
  `Elemental Plane of Earth`), drops to the single desert room `12/335` `Scorching Desert, Pyramid`. **Detection:** landing in a
  `Scorched Cavern` room (`12/1239–1278`) or `12/335` mid-climb = failed → halt+report.
- **F1 — timed, blind-fast.** Entry: `You have a strange feeling that time is running out!`; finish
  within ~5 min of the first firepit `up` or scatter. Lateral gates open with `push block` (encoded
  `push block, push square block, move block`; broadcast
  `<leader> pushes the stone block, and it slides into the wall.`). Never stop on F1.
- **F1 timing budget.** F1 ≈ **126 moves + 6 actions** (5 push-blocks + `ask sphinx fire`), ~250
  ms/action, under 5 min. **Stock:** a `Heavy` (>66%) leader = guaranteed timeout. **Paradigm:** the
  estimate `126·per-move + 6·250 ms` goes over 5 min at roughly >80% enc with no quickness.
- **F2 — chaos, blind-fast.** Pitch-black (`The room is pitch black - you can't see anything`), wall
  darts/blades (poison), room spells whose damage **scales the longer you dwell** → keep everyone healed,
  don't stop for blind/poison/confuse. Undead priests may `hold person` a member; moving on leaves a held
  member behind (party cohesion is human-managed here in v1).
- **F3 — door-maze, paced.** Doors cycle on spell 700 → TB 2528/2529 (weighted
  `remoteaction … 0 0 2/1` = open/close); timer broadcast `Doors on this level creak and thump!`,
  per-door `The door to <dir> just opened.`, exits carry state (`open/closed door <dir>`). **Per-door:**
  `(Door [1000 picklocks/strength])` = unbashable → **wait** for the timer; lesser door on-path = **bash
  `<dir>`**.
  - **Spell 700 is both** *([OBSERVED] 2026-09-26, game data — v1.11p and Paradigm 1.9.1 identical)*:
    "cleanup 3" carries `ScatterItems` (ability 157, like cleanup #691 / cleanup 2 #692 — hence its
    place under **Fall/scatter**) **and** TextBlock 2528, whose `random 2529` fan-out is the door
    `remoteaction` cycle.
  - Golden lion key drops from the neutral `floating key` monster (**#598**).
  - **No-drop bug** — a bugged kill drops nothing → exit E, re-enter W to respawn.
  - Key door: `unlock` → `The key breaks and crumbles apart.` → `open` → move.
- **F4 — footpath, forward-only.** Spell "fourteen" by walking the correct arches (`ask sphinx riddle` @
  2052 gives the clue). Each arch casts **701 (pass)** / **702 (fail)**; **702 → TB 2640→2641** =
  weighted teleport down; a **backtrack also falls** (backtracking = unsolved). Pass internally gates on
  ability 134 = 9 (Dao/Sunstone flag) — climbers already hold it.
- **F5 — standard, paced.** `go shaft`/`go pit` (room CMD textblocks, e.g. 1800/2524, 1857/2521) escape
  **down** to the firepit.
- **[CONFIRMED 2026-07-30] Undead-priest holds.** The pyramid undead priest is monster **#770**; it casts
  `MidSpell-0 = 66` — **spell #66 `hold person`, the SAME spell ID the player casts** (25% at level 20;
  also casts #77). (Hold person details: see *Spells, buffs & conditions → Ailment identification — which
  spells cause disease / poison / blind / hold*.)
  - Wording: witness cast `<caster> casts hold person on <member>!`; the held target's own lines are
    `Your legs are paralyzed!` → `You can move again!` (private — a *witness* never sees a member's
    wear-off).
  - Cure = **freedom (#70)** / **cure paralysis (#160)**, seen as `<caster> casts freedom on <member>!` /
    `<caster> casts cure paralysis on <member>!`.
  - F5 has no priests.
- **Correction to earlier notes:** spell **698 = "crushing blocks" (damage), NOT a teleport**; F4 fail =
  spell **702**; scatter range = `12/1239–1278` (room-spells 691/692/700), desert secondary = spell
  **742** → `12/335`. Earlier "begins to stiffen up!" wording was spell **#327 "paralyzed"** (a
  beholder-type), NOT the undead priest's hold person #66.
- **Client use:**
  - Not walker-BFS-routable — the graph builder doesn't synthesise monster-greet `remoteaction` edges —
    so the climb runs a **canned per-floor script**; game-data room numbers position / detect floor /
    read door state.
  - Scatter detection (**Fall/scatter**) halts the climb and reports.
  - **Pre-flight timer gate:** **Stock:** `Heavy` (>66%) leader → refuse. **Paradigm:** estimate
    `126·per-move + 6·250 ms` via `MovementSpeedCalculator` (live enc% + quickness, floored at the 1 s
    cap); over 5 min → refuse (crosses ~>80% enc, no quickness). Drives **leader/solo only**.
  - F3 key: the `floating key` monster's (#598) default client relationship was `Flee` in the Paradigm
    overlay (stock was already `Enemy`); set to **`Enemy`** so party auto-combat clears it for the key
    (the solver needs no kill logic). The client tracks who grabs it (`<name> picks up golden lion key`)
    and forces a bare `@party give golden lion key to <leader>` (no leading `.` — see *Item-use
    teleports*) at the key-door unless the leader grabbed it.
    The no-drop respawn (exit E, re-enter W) is not yet automated.
  - F4: runs the footpath strictly forward (never back up); paces slower than the other floors for
    reaction time. The client doesn't check/encode the ability-134 pass gate.
  - **Hold handling per floor:** F1 (timed) and F2 (deadly-to-linger) keep moving through a hold; **F3/F4
    wait it out** — pause until a freedom/cure cast frees the member (multiple can be held) or a wear-off
    cap (~hold person Dur 4) elapses. Combat and a **held leader** ride the shared `MovementCoordinator`
    `Combat`/`Held` gates (the solver waits on those on the paced floors); F1/F2 never gate.

---

## Items, inventory & equipment

How items are acquired, counted, picked up, dropped and stored in rooms. Also covers equip/remove verbs, slot rules, wear restrictions, item charges, buff-item swaps, chests and special items like gang-house emblems and NPC hand-overs.

### Acquisition verbs
*Status: CONFIRMED*

- **[CONFIRMED] The acquisition verbs are `buy` / `get` / `search`+`get`** (plus NPC `ask` gives and chest opens). There is no "hunt" verb, so don't describe path-item sourcing as "hunting."

### Monster drops land on the ground
*Status: CONFIRMED 2026-07-16 (user)*

- **Monster drops land loose on the ground as the item.** When a monster we kill drops one of its `DropItem-N` items, the item appears on the floor as a normal ground item. There is no corpse-container split to loot.
- **A plain `get <item>` collects it**, exactly like any other ground item.
- **The drop isn't announced on the kill line.** To see and auto-collect it, the room must be re-surveyed: a bare `look` re-renders the `You notice … here.` list that the auto-get engine already parses.

### Ground stack counts in the room survey
*Status: CONFIRMED 2026-07-20 (user)*

- **A ground stack of 2+ identical items shows a leading count; a lone item shows the article form (no count).** The room survey renders a pile as `You notice 5 piece of amber here.`, and the count is the true number on the floor.
- **With only one of the item, the survey omits the count and uses the article**: `You notice a piece of amber here.` (never `1 piece of amber`). There is no bulk-get verb, so each `get <name>` grabs a single unit — true on Stock; Paradigm also accepts the counted `get <N> <item>` (see *Item batching: Paradigm counted commands vs Stock one-per-command*).

**Client use:**
- The auto-collect engine issues **one `get` per counted unit** on both realms (the Stock-safe form; it does not route through `CountedCommand.Emit`): the survey count for a stack, exactly one for the article/lone form.
- Count parsing mirrors `ItemNameStore.Normalize`, which strips the same leading count/article token when matching the item name.

### Item batching: Paradigm counted commands vs Stock one-per-command
*Status: CONFIRMED (Paradigm-specific counted form; report `paradigm-20260812-201631`) · Realm: Paradigm counted, Stock one-per-command*

- **Paradigm lets a single action name a count**: `buy <N> <item>`, `sell <N> <item>`, `get <N> <item>`, `drop <N> <item>`, `give <N> <item>`, `hide <N> <item>`.
- **`<item>` can be anything in the Items table** of a game-data import: keys (`drop 3 black star key`), equipment, weapons, usable items and loot.
- **The confirmation echoes the count with the SINGULAR item name**, e.g. `You hid 35 orc-head.` (not `orc-heads`).
- **The other verbs' bare-count confirmations are not all captured yet.** They have the same shape (`You took/dropped <N> <item>.`, buy/sell), but not every one has been captured from a live session.
- **[CONFIRMED] Stock: one item per action — `sell dagger` ×10 to sell ten; no quantity argument.** This holds for every verb and every kind of item: `buy <item>` and `sell <item>` each transact exactly one unit.
- **Gets follow the same split** (user, 2026-09-26): the ground-stack entry's "there is no bulk-get verb, so each `get <name>` grabs a single unit" (2026-07-20) holds on Stock; Paradigm accepts `get <N> <item>` — the capacity-refusal screenshot (see *`get` failure responses*) shows `get 20 torch`.

**Client use:**
- The client emits both forms (Paradigm counted, Stock one-per-command) through `CountedCommand.Emit`.
- **Inventory tracking must apply the count.** A counted confirmation removes or adds N copies, not one. The carried-list and running-weight adjustments must strip the leading count and apply it N times, or the encumbrance estimate drifts.
- Report `paradigm-20260812-201631`: 35 stashed orc-heads left the estimate ~1050 too heavy. The cash "skip if Heavy" gate then wrongly skipped a collect while the character was actually Medium.

### Pickup / drop confirmation lines and item vs coin disambiguation
*Status: CONFIRMED*

- **Item vs coin is told apart by verb + shape.**
  - An **item** get is `You took <item>.` and an item drop is `You dropped <item>.`
  - The drop/hide verbs are **shared** with coins. A colour-adjective item (`You dropped a silver key.`) is told apart from coin only by the trailing **coin noun** (`nobles`/`farthings`/…) and a numeric count.
  - `You picked up …` is coin-exclusive; items never use it.
- **The pickup lines are the authoritative "the get landed" signal**: `You took <item>.` per item (one line per collected item), and `You picked up <N> <coin>` per coin get (see *Money, banks & shops → Coin wire wording*).
- **A `You picked up 0 <coin>` is a FAILURE, not a success.** (This entry originally wrote it with a trailing period, `You picked up 0 <coin>.`, which disagrees with *Money, banks & shops → Coin wire wording*.) The character is at its carry limit and took nothing; the coins stay on the ground. For gate purposes the get has still *resolved*, so it stops the walker waiting on it, but nothing was collected.
- **Item-get failures have their own wordings**, recorded in *`get` failure responses* (CONFIRMED 2026-08-21 from screenshots; superseded: earlier recorded as not yet captured). A get that yields neither a `You took` line nor a recognised failure is released by the settle timeout rather than by a confirmation.

**Client use:**
- The client keys movement-resume (the acquisition gate) and collection dedup on the pickup lines.

### Coin-adjective binding on get/drop
*Status: CONFIRMED*

- **A get/drop target given as the bare denomination adjective binds to any like-named item, not the coins.** `drop 1 silver` can resolve to a *silver ring* instead of a silver noble; the game picks whichever object matches first.
- **The full two-word coin noun forces the currency match**: `silver noble`, `gold crown`, `copper farthing`, `platinum piece`, runic `<word> coin`.

**Client use:**
- The client sends the full coin noun on every outgoing get/drop.

### `get` failure responses
*Status: CONFIRMED 2026-08-21 (user — screenshots); braces CORRECTED 2026-09-03 (live capture)*

A `get <item>` that can't succeed replies with one of these shapes:

- **`You don't see <echo> here.`**: the item isn't on the floor (gone: decayed, or another player took it).
  - `<echo>` is whatever text followed `get`, echoed back verbatim, so it can be a bare word rather than the item's full name.
  - Examples: `get rod` → `You don't see rod here.`; `get warhorn` → `You don't see warhorn here.`
- **`Syntax: GET {Amount} {Currency}`**: the game misparsed the item name as a **currency** get.
  - Observed for some multi-word names, e.g. `get silk cape`, and for gem/stone names like `piece of amber`.
  - No item name is echoed, and retrying the same name can't help.
- **Note the BRACES** *([CORRECTED] 2026-09-03, live capture)*.
  - This line was first recorded here with square brackets, and the client's matcher implemented that faithfully. It therefore matched nothing on the live realm for as long as it existed.
  - The failure was invisible and expensive. An unmatched refusal is not stranded, so the item is retried every lap forever (`get piece of amber` sent 42 times in one capture) and can pin a sweep ping-ponging between the rooms holding such items.
  - Match **either** form. The DROP counterpart uses braces too.
- **`You cannot carry that much!`**: a **capacity refusal**. The item is on the floor and gettable, but taking it would exceed the carry limit.
  - Unlike the two shapes above, the item is NOT gone. It's a transient block that clears once weight is shed.
  - No item name is echoed.
  - Confirmed by screenshot: `get 20 torch` succeeds twice, then a third `get 20 tor` while overloaded → `You cannot carry that much!`.

**Client use (Roomba Mode):**
- **The first two shapes mean retrying is futile**: the item is gone or un-gettable by that name, so drop it from the sort queue. Correlate to the outstanding get by the echoed word, falling back to the sole pending get for a truncated/absent echo.
- **The capacity refusal keeps the item queued.** It means our tracked working-weight has drifted low (we thought it fit and it didn't).
  - Re-verify inventory once (`i`) to resync the baseline, then re-plan.
  - Deliver to free room and retry, or strand the item if it's too heavy for the whole working budget.
- **Do not name-match a failure line's word to decide anything.** Match by "we just sent a `get` and got a failure back," since the echo can be truncated or absent.

### `drop`: targeting, refusals and worn items
*Status: CONFIRMED 2026-09-02 (user); worn-item drop CONFIRMED 2026-09-26 (user)*

- **`Syntax: DROP {Amount} {Currency}`** is the **drop** counterpart of the get syntax refusal (confirmed 2026-09-02). Note the **braces**.
  - The get form uses braces too. Corrected 2026-09-03: braces, not brackets (see *`get` failure responses*).
  - Same shape otherwise: no item name is echoed. It means the game didn't recognise the name as something you're holding, usually because you aren't.
- **`You may not drop that item!` is a *different* refusal**: the item is held but undroppable.
- **Drop arguments are PARTIAL-MATCHED against what you hold** *([CONFIRMED] 2026-09-02, user)*. This is the dangerous one.
  - Observed: bloodstones were auto-discarded, and a later `drop bloodstone` bound to a **`bloodstone orb`** still in the pack. The game answered `You may not drop that item!` because the orb is undroppable.
  - **Had the collision landed on something droppable, the wrong item would have been dropped with no complaint at all.**
  - So a drop for an item you may no longer hold is never safe to send blind. Either confirm you hold it, or be ready to treat any refusal as "verify against a real `i` before doing anything else".
- **A worn item drops with a plain `drop <item>`** *([CONFIRMED] 2026-09-26, user)*: no `rem` first. The game takes it off and drops it in one command.

**Client use:**
- Roomba treats any drop refusal as "verify against a real `i` before doing anything else". It also drops its belief in a carried item the moment anything else is seen dropping it.
- `@drop-all full` / Drop Everything drops worn gear with the same `drop` it uses for the pack (InventoryActionHandler).

### Hiding items in a room (stashing)
*Status: CONFIRMED 2026-09-26 (user) · Realm: both; the counted form is Paradigm-only*

- **`hide <item>` stashes an item in the room; a bare `hide` hides the player instead.** A stashed item
  can't be seen again until someone actively searches the room for it. `hid <item>` is the shorthand.
- **Worn gear hides directly**, like `drop`: `hide <item>` on a worn piece takes it off and stashes it
  in one command, no `rem` first.
- **Counts follow the batching rule** (see *Item batching: Paradigm counted commands vs Stock
  one-per-command*): Paradigm takes `hide <N> <item>` in one command; Stock needs one `hide` per copy.
- Coin stashes: see *Money, banks & shops → Hiding coin in a room (stashing)*.

**Client use:**
- `@hide-all [full|coins|keys]` and the Hide All / Hide Everything / Hide Coins / Hide Keys actions
  (`InventoryActionHandler.HideAll`) run the Drop All sweeps with `hide`, always naming the item or coin
  — never a bare `hide`, which would hide the character instead.

### Room item capacity: drop refusal
*Status: CONFIRMED 2026-09-02 (user, live capture); per-object stacking 2026-09-03 (user; mechanism NEEDS CONFIRMATION); realm CONFIRMED 2026-09-26 (user) · Realm: Stock — Paradigm rooms have no item cap*

On Stock, a room holds a limited number of items (Paradigm rooms have no item cap; the capture below didn't record its realm). The same cap drives the Stock deathpile spill-over (see *Death & corpse recovery → Deathpile — where the items go*). A `drop` into a room already at that limit is refused:

```
[HP=642/MA=265]:drop pend
There is no room to drop amethyst pendant here.
```

- **The reply carries the item's FULL canonical name**, not the word typed. The command above abbreviated it to `pend`, and the refusal still named `amethyst pendant`. Unlike the `get` failures, where the echo can be truncated and name-matching is explicitly unsafe, a drop refusal CAN be correlated to the outstanding drop by name.
- **It is per-drop, not per-batch.** A batch of N drops into a full room produces N refusals, one per command, and none of them confirm.
- **The exact capacity is unknown, and the client never needs it.** "Full" is only ever learned by being refused.
- **Capacity is per OBJECT, not per item, so a stacking drop may still fit a "full" room** *(2026-09-03, user; [NEEDS CONFIRMATION] mechanism)*.
  - The sysop dump counts floor *objects*, and an id can appear more than once. Two black star keys dropped singly read `172(0) 172(0)` (two objects), while two diamonds read `902(1)` (one object of two).
  - So stacking is item-dependent. An item that stacks onto a pile already on the floor consumes no new slot, which means a room that refuses one item can still accept another that stacks with its existing contents.
- **What isn't known: which items stack.**
  - There may be a column in the Items table for it. This is unchecked; don't assume a plausibly-named column means this without confirming.
  - A stack may itself have a size limit.
  - **The experiment:** in a room that has just refused a drop, try dropping an item that matches something already on its floor. Success means stacking bypasses the object cap. A second refusal means it doesn't, or that pile is itself full.

**Client use (Roomba Mode):**
- **A refusal marks that room full for the rest of the sweep.**
  - Every pending move bound there, carried or not yet collected, is re-resolved onto the next room labeled for the same category, then the catch-all.
  - Anything with nowhere left is recorded and dropped from the queue rather than retried.
- **The same mark makes the room a *preferred pickup source***, since the foreign items sitting in it are the only ones whose removal frees its capacity.
- **The mark is per-sweep**: a full room is only full until someone loots it.
- **The sweep is correct but conservative.** It treats a refusal as "this room is full for everything" and re-targets the whole batch, so it gives up on stackable items that would have fitted.
- **The cheap empirical fix is deliberately NOT implemented** while the mechanism is unverified. That fix would retry an item whose name already appears in that room's survey, once, before re-targeting it.

### Equip / remove verbs
*Status: CONFIRMED*

- **`eq <item>` is the universal equip verb.** It works for **every** slot (armor *and* weapons).
- **`wear <item>`** is an alternative for **armor** items only.
- **`wield <item>`** is an alternative for **weapons** only.
- **`rem <item>` is the universal remove verb.** It removes any equipped item (armor, weapon, or light).
- **`hold` is not used** by this game.

### Trade-places on equip
*Status: CONFIRMED*

- **Equipping into an occupied slot trades places.** If a slot is occupied and you `eq` (or `use`, for a light) another item from your inventory, the new item takes the slot and the old item returns to inventory.
- **This only works if the new item is actually usable.** Class/level/slot constraints apply, as does a two-hander vs an occupied off-hand, etc. If the new item isn't usable, the swap fails and nothing changes.
- **So a single-slot swap needs no explicit `rem` first.**

### Named-item uniqueness and paired slots (finger / wrist)
*Status: CONFIRMED 2026-09-03 (user in-game tests; report `paradigm-20260903-111522`) · Realm: both (eviction slot differs per realm)*

- **Only one of each *named* item can be worn at a time.** Two identically-named pieces (e.g. two *silver bracelets*) can't both be equipped; the second is refused. Distinct names are fine: a *silver bracelet* and an *ivory bracelet* equip together.
- **The finger and wrist families each hold two physical pieces** (Finger1/Finger2, Wrist1/Wrist2), so long as the two are distinct names. Every other slot holds one.
- **`i`-list order IS physical slot order, and it's reliable** *([CONFIRMED] 2026-09-03, user in-game tests, both realms)*. The first-listed paired piece is slot 1 and the second is slot 2, on both Stock and Paradigm.
- **`eq` target-swaps ONE physical slot in place** *([CONFIRMED] 2026-09-03, user)*. On Paradigm `wear` is **not** equivalent for paired items — it appends to slot 2 and shuffles (see **Client use** in this topic). On Stock `wear` and `eq` behave the same for paired slots *([CONFIRMED] 2026-09-26, user)*.
  - Equipping a new paired piece into a FULL family evicts one slot's occupant, and the new piece takes that slot.
  - Equipping into a family with a free slot fills the empty slot.
- **Which slot is evicted is realm-specific:**
  - **Paradigm evicts SLOT 1** (the first-listed). Worn `[adamantite, white gold]` + `eq silver` → removes adamantite → `[silver, white gold]`.
  - **Stock evicts SLOT 2** (the second-listed). Worn `[amethyst, silver]` + `eq copper` → removes silver → `[amethyst, copper]`.
- **Consequences for a set swap (per realm):**
  - A set pick bound for the **evicted** slot rides the eq, with no `rem`: it displaces the odd-out sitting there back to the pack.
  - A pick bound for the **other** slot needs its odd-out `rem`med first, or the eq displaces the member the set keeps back to the pack.
  - To swap BOTH members: `eq <new A>` (evicts the odd in the evicted slot), `rem <other odd>`, `eq <new B>` (originally recorded as `eq 3` … `eq 4`). That is **one** `rem`, not two.

**Client use:**
- `EquipmentManager.ComposePairedSlotCommands` takes a realm flag (Paradigm ⇒ evict slot 1) and reasons off the reliable `i` order.
- Paired items are equipped with **`eq`**. On Paradigm, `wear` appended the new ring to slot 2 and shuffled the survivor into slot 1. That scrambled the order across a swap cycle and forced a needless `rem` (report `paradigm-20260903-111522`).

### Equip / swap result lines
*Status: CONFIRMED (weapon and armor swap lines); OBSERVED (already-worn refusal)*

- **[CONFIRMED] A weapon equip / swap prints a single line**: `You are now holding <new>.`
  - Swapping into an occupied weapon hand emits **no** removal line for the old weapon; the displaced weapon returns to the pack silently.
- **[CONFIRMED] An armor swap into an occupied slot prints two separate lines**, in order: `You have removed <old>.` then `You are now wearing <new>.`
  - This is where armor differs from a weapon swap: armor names the displaced piece with an explicit removal line, and a weapon does not.
  - The two lines arrive back-to-back but are distinct, and the client matches each on its own.
- **[OBSERVED] Re-equipping an item that's already worn** draws `You do not have <X> left unequipped.`

### Two-handed weapons and the off-hand block
*Status: OBSERVED (general block); CONFIRMED 2026-08-19 (user report `paradigm-20260819-234712`)*

- **[OBSERVED] A two-handed weapon needs both hands free.** The game rejects the wield while an off-hand is occupied: the weapon isn't "usable" until the off-hand is gone. So the off-hand must be `rem`'d first.
- **[CONFIRMED 2026-08-19, user report paradigm-20260819-234712] The block isn't limited to items the `i` listing prints as `(Off-Hand)`.**
  - An item whose MDB `Worn` code is Off-Hand (12) can still print under the generic `(Worn)` bucket in the game's own `i` text (e.g. a *red skull*, a worn charm/skull item).
  - Such an item still mechanically fills the off-hand and blocks a 2H wield exactly the same way: `You may not ready a 2-handed weapon with your <item> worn!`, naming the blocking item. The client's own `EquippedItems.Slot` label is taken verbatim from the game's `i` text, so it can disagree with what actually blocks a 2H equip.
- **The item's declared MDB `Worn` code is the authoritative signal, not its display bucket.**

### Worn state: no forced unequip, persists across login (EP-zap exception)
*Status: CONFIRMED*

- **No effect in the game force-unequips gear** (no disarm / removal effects). Worn state changes *only* from commands the player or the client issues.
- **Worn gear persists across logins.** You log back in wearing whatever you had on. There's no re-equip-on-connect step to do, because the loadout is already correct.
- **The one exception is the rare cleanup EP-zap.** When an evil character's alignment drops below an item's Evil-Point threshold, the game force-removes it. Re-equipping then fails with `You may not use that weapon.` (weapon) / `You may not wear that item!` (armor).

**Client use:**
- The client must not fire a speculative `eq` before the first `i` dump lands. The desired gear is already worn, so a blind equip only draws the already-on refusal (or the EP-zap refusal).

### Item wear restrictions (ability-code flags)
*Status: CONFIRMED*

- **Wear-restrictions aren't dedicated columns.** They're MajorMUD **ability-code flags** in an item's `Abil-N` slots; the `AbilVal-N` is presence noise.
- **Alignment codes:** `97` Good-only, `98` Evil-only, `110` not-Good, `111` not-Evil, `112` Neutral-only, `113` not-Neutral.
- **Level codes:** `135` min-level, `136` max-level.
- **Class is a separate `ClassRest-0..9` allow-list** of class Numbers.

**Client use:**
- `ItemEquipFilter.CanEquip` evaluates all of these against the live character.
- The Equipment Manager blocks a slot whose item fails the check, and also blocks it on the EP-zap refusal.

### Item charges (`Uses` / `UseCount`)
*Status: Unrated*

- **A positive value is the item's real charge count.** Charges are consumed to zero, then the item is gone.
- **`<= 0` means unlimited.**
  - MajorMUD stores **`-1`** for a truly unlimited item (the common case, e.g. *shimmering greatsword*, *jeweled longsword*), and occasionally **`0`**. **Both are unlimited.**
  - This matches MMUD Explorer's own normalisation `If uses <= 0 Then uses = -1`.

**Client use:**
- Only unlimited items are safe to feed a buff-recast loop.
- The Spell Book renders `<= 0` as the word "Unlimited" (never a raw "-1 uses").

### Equip → use → restore swap for a readied buff item
*Status: CONFIRMED 2026-08-06 (user); 2H-weapon + off-hand-buff exception CONFIRMED 2026-08-26 (user)*

- **To command-cast from an equippable item you must have it equipped.** Consumables — potions, waterskins — are `use`d straight from inventory and never need equipping *([CONFIRMED] 2026-09-26, user)*. So the buff engine equips the cast item, `use`s it, then puts back whatever it displaced.
- **A buff item can live in ANY equip slot, not just weapon / off-hand.** A warhorn is off-hand, a charged amulet is neck, etc.
- **Restore is slot-specific.** `eq <item>` puts the item into **its own** slot and displaces only what was there.
- **1H weapon buff:** displaces the **weapon hand**, so restore the weapon.
  - If you're on a 2H weapon, a 1H buff swaps cleanly: `eq buff`, `use`, `eq <2H weapon>`. There's no off-hand step, because the off-hand was empty under the two-hander.
- **Off-hand buff** (warhorn, a held item): displaces the **off-hand**, so restore the off-hand shield.
  - **The weapon is never touched** *when it's one-handed*.
  - Restoring the weapon instead was the bug: it strands the buff in the off-hand and never puts the shield back.
- **Exception: off-hand buff while wielding a 2H weapon** *([CONFIRMED] 2026-08-26, user)*.
  - A two-hander fills **both** hands, so `eq <off-hand buff>` is **rejected outright** until the two-hander comes off. This mirrors the 2H-buff case below.
  - Order: `rem <2H weapon>` → `eq <buff>` → `use` → `rem <buff>` (frees the off-hand) → `eq <2H weapon>`.
  - The buff must be removed before re-wielding, because the game likewise blocks the 2H wield while the off-hand is occupied.
  - Without this order the sequence just loops on a rejected `eq <buff>`.
- **Worn buff** (amulet/ring/etc.): displaces **that worn slot**, so restore that slot's item.
- **2H weapon buff while holding a 1H weapon + off-hand:** the buff needs **both** hands.
  - Order: `rem <off-hand>` → `eq <2H buff>` → `use` → `eq <1H weapon>` (this displaces the two-hander back to the pack and frees the off-hand) → `eq <off-hand>`.
- **Whatever slot was empty simply isn't restored** (nothing to put back).

### Chests and chest loot tables
*Status: CONFIRMED (chest behaviour); CONFIRMED — verified against the 1.11p / Paradigm / Euphoria data, 2026-07-10 (loot-table chain)*

- **Some monsters drop a `chest`.** `open chest` **dumps a set of random items straight into inventory** that the player does not get to choose.
- **A chest's loot table is data-driven through a three-hop chain.**
  - A container is `Items.ItemType == 8`.
  - Its `open` behaviour is an ability pair `Abil == 43` (CastsSp) whose `AbilVal` is a **Spells** row.
  - That spell carries `Abil == 148` (TextBlock) whose `AbilVal` is a **TBInfo** row.
- **The top TBInfo entry's `Action` is a single colon-separated directive line.** It contains:
  - `message N` (flavour, ignore)
  - `giveitem I` (a **guaranteed** drop)
  - `random T` tokens
- **Each `random T` token is one independent draw** from weighted table `T`. The token is **repeated once per draw**: oak chest = `random 898` ×3 + `random 874` ×3 = six draws.
- **A weighted table's lines are `threshold:directives`**, with **cumulative** thresholds.
  - Per-bracket chance = `thisThreshold − prevThreshold`. Tables normally end at 100.
  - The selected bracket runs its own directives: `giveitem I`, a nested `random M` (a sub-draw, possibly repeated within the bracket), or `message`/`failitem`/`price` (no item).
  - **`failitem` yields nothing** (a dud).
- **Per-item drop chance is *at-least-once across all draws***: `1 − ∏(1 − p_draw)`.
- **The item count a single open yields is fixed by the number of draws.** A bracket that only messages or fails contributes 0, so min ≤ draws ≤ max.
- **Chest coins can't be derived from the data.** Chests **do** drop coins in-game, but the loot tables in the imported data carry **no `givecoins` token in any installed set**. The coin amount isn't encoded.

**Client use:**
- AutoDiscard exists to clean up after `open chest`: it drops the unwanted dumped items down to the keep band.
- The loot readout shows items only, because chest coin amounts aren't in the data.

### NPC keyword hand-over detection
*Status: CONFIRMED 2026-09-11 (user, reports `paradigm-20260911-103025`, `-103315`)*

- **An NPC keyword hand-over cannot be detected from its message. Re-read the pack instead.**
- **The giver's line is flavor text in a TextBlock that is not shipped with the MDB**, so it can't be matched against game data.
  - Example: asking the gnome commander for the orb prints *"The gnome commander gives you the heavy bloodstone orb."*, while the item's record name is plainly `bloodstone orb`.
- **Other keyword-gives may print no line containing any part of the item's name at all**, so there is no wording to latch onto even in principle.
- **The reliable test is to send the ask and then immediately `i`.** The inventory listing names the item **by its item-record name**, which game-data lookups match exactly.
- **Treat a hand-over message as unparseable decoration** — never as the signal that a give succeeded.

### Gang houses and guard emblems
*Status: CONFIRMED 2026-08-16 (user)*

- **Gang house (GH) guards are conditionally hostile, gated on an item**, not on the alignment/title system.
- **A gang house's guards do not attack a character carrying that house's matching emblem item.** The emblem's name matches the house's color/name: a gold house's guards stand down for a **Gold Emblem**, a silver house's for a **Silver Emblem**.
- **Losing or dropping the required emblem while inside makes the guards attack.**
- **This is separate from the `jail`-casting guard/title system** in *Combat → Monster aggression — who opens on you unprovoked*. It is not tied to the player's alignment title at all, only to possession of the correct emblem for the specific house.
- **No item-level flag or `ItemType` value in the imported MDB data currently distinguishes an emblem** from any other item. The only known signal is the item name pattern (`"<Color> Emblem"`).

**Client use (Roomba Mode / any GH automation):**
- An emblem item must never be treated as disposable clutter by automation that picks up and relocates items inside a gang house. Moving it away from the player, even temporarily mid-sweep, risks the guards turning hostile.
- Until a more precise data field is identified, automation should treat any item name matching `"<Color> Emblem"` as categorically excluded from being picked up/moved.
- Automation should never disturb items the player was already carrying before it started. This is a stronger, simpler guarantee that also protects a worn emblem without needing to recognize it specifically.

---

## Money, banks & shops

How coin is named, valued, dropped, collected, hidden and banked, and how shops price, stock and report buys and sells, including textblock `price` charges.

### Currency denominations & value ladder
*Status: CONFIRMED*

- **Five denominations, each with its own full coin name:** **copper farthings**, **silver nobles**,
  **gold crowns**, **platinum pieces**, **runic coins**.
- **The runic coin noun can be renamed per BBS.** A realm may call its top denomination something
  else; the other four are stable across the target realms.
- **Value ladder (in copper):** 1 silver = 10, 1 gold = 100, 1 platinum = 10 000, 1 runic = 1 000 000.
- **Wealth is consolidated in copper farthings** (the game's `Wealth:` line).

### Coin wire wording
*Status: CONFIRMED*

- **The keyword is the denomination-defining first word** (`copper`/`silver`/`gold`/`platinum`/`runic`);
  the second word is the flavour coin noun (`farthings`/`nobles`/`crowns`/`pieces`/`coins`). The client
  keys policy/value on the keyword. Some lines carry only the keyword, others the full pair — don't
  assume one form.
- **Get / drop commands the client sends name the coin in full** (`get 6 silver noble`,
  `drop 1 silver noble`) — the full-noun rule is in *Items, inventory & equipment → Coin-adjective
  binding on get/drop*. Internally the client still keys policy/tally/parsing on the bare first word.
- **Kill drops name the bare keyword:** `6 silver drop to the ground.`
- **Pickup confirmation names the full coin and carries NO trailing period:**
  `You picked up 6 silver nobles` (singular `You picked up 1 silver noble`).
  *([NEEDS CONFIRMATION] the singular form and the trailing period aren't pinned down, and may differ
  between Paradigm and Stock — user, 2026-09-26. *Items, inventory & equipment → Pickup / drop confirmation
  lines and item vs coin disambiguation* and
  *Death & corpse recovery → Corpse recovery (`recover corpse`)* write `You picked up <N> <coin>.` with a period, and the code (`InventoryManager`) documents `You picked up a gold crown.`; the client's matcher accepts both forms, with or without a period.)*
- **Drop / stash confirmations name the full coin with a trailing period:**
  `You dropped 5 gold crowns.` / `You hid 219 copper farthings.`
- **Bank deposit confirmation names the full multi-currency amount** as one comma-separated list with a
  trailing period: `You deposit 1 platinum piece, 93 gold crowns, 4 silver nobles,
  12 copper farthings.` Emitted for **both** a manual `dep` and the client's auto-deposit `dep`, so it's
  the authoritative both-paths signal. Withdrawals use the same `You withdrew …` / `you withdrew …` prefix but name a single **copper** amount (see *Bank commands: balance / withdraw / deposit*).
- **Room survey lists the full coin:** `You notice 56 silver nobles, 198 copper farthings here.`

**Client use:**
- A long `You deposit …` list wraps at the ~78-col margin, so the client re-merges a non-`.`-terminated
  `You deposit …` row with the next physical row before parsing.

### Ground cash is a running room total
*Status: CONFIRMED 2026-08-03 (user)*

- **`You notice N <coin> here.` is the running room TOTAL, not a per-kill delta.** A kill drops coin
  with a `N <coin> drop to the ground.` line (the delta), and it merges into the room's single ground
  pile. A later room re-display's `You notice N <coin> here.` reports the *whole* pile — every coin
  dropped by kills plus anything present on entry.
  - Example: walk in on 5 silver / 20 copper, kill a mob dropping 1 silver / 5 copper, re-display
    shows 6 silver / 25 copper.
- **The kill-drop lines and the room-total line describe the same coins** — summing both
  double-counts.
- **Same-denomination piles merge**, so if someone else grabs from the pile first you can only get what
  the current display shows.

**Client use:**
- With collect-after-combat, re-`look` once the room clears and collect off the fresh `You notice`
  total — one authoritative pass — never replay the mid-fight drop deltas (they double-count against
  the total, re-queue on every re-render, and go stale). Implemented in `CashManager`.

### Re-surveying ground cash with `look`
*Status: CONFIRMED*

- **A bare `look` (or `l`) with no target re-displays the current room**, including the
  `You notice N <coin> here.` ground-cash survey line, so the exact remaining ground cash can be
  re-surveyed on demand.
- **A `look <direction>`/`l <dir>` peek does not survey your room.** It renders the *adjacent* room's
  display instead and is peek-suppressed; only the target-less form surveys the room you stand in.

**Client use:**
- This is the recovery path for stale deferred counts after a witnessed third-party pickup: re-survey
  with `look` and collect the freshly-observed amount.

### Another player picking up ground cash
*Status: CONFIRMED*

- **A third-party grab emits a non-specific, count-less line:** `<Name> picks up some <coin-plural>`
  (e.g. `Tristian picks up some gold crowns`) — "some", the full coin plural, and **no trailing count
  and no trailing period**.
- **It does not say how much they took**, and it **may take part or all** of the pile.

**Client use:**
- A witnessed third-party pickup of a denomination we've deferred makes our stored exact per-pile count
  stale and unrecoverable — the remaining amount can't be derived from the line (recover with a bare
  `look` — *Re-surveying ground cash with `look`*).

### Hiding coin in a room (stashing)
*Status: CONFIRMED 2026-08-29 (user; report `paradigm-20260829-212158`); CONFIRMED 2026-09-14 (user)*

- **`hide <N> <coin>` is a stash, not a vault.** `hide <item>` is the full command and `hid <item>` its
  shorthand *([CONFIRMED] 2026-09-26, user)*. Hiding an object (or coin) is a different act from
  hiding the character (see *Movement & navigation → Hiding — sneak vs hide, the hide state machine,
  and search reveals*).
- **Hidden coin persists, but it is not yours.** It stays in the room rather than decaying, but **any
  player who searches that room finds it and can take it**. A stash balance is therefore a *belief*,
  never a fact: plan against it, but confirm it on arrival before spending it.
- **Stashed (hidden) coin is only re-surfaced by a `search`.** A hidden pile does **not** show on plain
  room entry or a re-`look` — only a `search` / `sea` re-reveals it, re-rendered through the same
  `You notice N <coin> here.` line as visible coin.
- **Retrieval is `sea` then `get`, and it does not miss.** A search **reliably** surfaces coin you hid —
  no skill check, unlike the stealth-reveal search which can fail. Once surfaced, `get` takes it. There
  is no separate un-hide verb.
- **Retrieval is capped by carry weight, not by the pile.** `get` takes as much as **available
  encumbrance** allows, so a large stash can need several trips or may be partly unrecoverable while
  loaded.
- **Coin weight is 1 unit per 3 coins regardless of denomination**, which is why a withdraw/retrieval in
  large denominations is far cheaper to carry than the same value in copper.
- **A stash room holds two kinds of coin:** **visible** coin (present on entry, or dropped by a kill via
  `N <coin> drop to the ground.`), which is fine to collect, and **search-revealed** coin (the pile we
  just stashed), which must **not** be re-grabbed.

**Client use:**
- In a stash room the client `hide`s excess coin.
- Auto-collect is suppressed in a stash room **only while an auto-search reveal is in flight** — coin
  shown on plain entry or a kill drop still collects, in the stash room and in the room after it.
  Implemented as `AutoSearchManager.IsRevealInFlight` gating the stash-room collect guard.
- Reading "am I in a stash room" alone is wrong: a room's entry survey is parsed before the room is
  confirmed, so it mis-attributes the *next* room's coin to the stash room just left — report
  `paradigm-20260829-212158`.

### Bank commands: balance / withdraw / deposit
*Status: CONFIRMED 2026-07-19 (user, capture); `bank` output CONFIRMED 2026-09-07 (user + screenshots) · Realm: both (the `bank` header differs — Stock appends ` (#N)`)*

**`bank` — the balance readout:**
- **`bank` is a self-only, global account query.** It lists the character's balance at **every bank they have ever deposited at**, from any room. It is NOT a room action like `dep`/`with`, which do require standing at the bank.
- **A bank the character has never used stays hidden; one used and then fully withdrawn shows with a zero balance.**
- **It never shows party members' banks.** Bank balances can only be seen for yourself. Party on-hand cash is visible separately via `@wealth`, which reports **carried** coin only, never deposits.
- **Output is one two-line block per bank, repeated** — a `Your balance at …` header, then
  `On deposit: <N> copper farthings [<G> gold crowns]`:

  ```
  Your balance at Bank of Godfrey is:                              (Paradigm — bank name only)
  On deposit: 19578816 copper farthings [195,788.16 gold crowns]
  Your balance at Bank of Godfrey (#8) is:                         (Stock — appends the shop number)
  On deposit: 4512 copper farthings [45.12 gold crowns]
  ```

- **The header is `Your balance at <name> is:`, where `<name>` is the bank's shop name.** Only Stock
  appends a ` (#N)` shop-number suffix (the Stock form is `Your balance at <Bank name> (#<n>) is:`);
  Paradigm omits it. Drop the suffix so the name matches the shop-name key.
- **The authoritative figure on the deposit line is the copper farthings count** — parse
  `(\d+) copper farthings` for the banked total in copper, allowing thousands-commas in the number. The
  bracketed gold-crowns value is only a gloss.
- **Because the name is the shop name, a parsed balance maps back to its room(s) via the bank-shop catalogue (ShopType 7).** So a route that needs more money than the purse holds can point at the nearest bank where the deposit actually sits.

**`with` / `dep` — at the bank:**
- **`with <amount>` withdraws, where `<amount>` is in copper farthings.** Coins arrive in the **largest
  denominations** (`with 2000` → 20 gold crowns). Success line: `You withdrew <amount> copper
  farthings.` (echoes the requested copper amount). The line names the **copper** amount *([CONFIRMED]
  2026-09-26, user)*, not a denomination list (an older note said withdrawals mirror the multi-currency
  deposit list).
- **Over-withdraw silently fails.** Requesting **more than the banked balance** produces **no output at
  all** — no error line. So verify a withdraw by watching for the `You withdrew …` success line (its
  absence within the reply window = failure), and/or read `bank` first and never request more than the
  balance.
- **`dep <amount>` deposits (amount in copper).** The confirmation names the actual carried
  denominations (`You deposit 5 platinum pieces, 29 gold crowns, 7 silver nobles.`) — see *Coin wire
  wording*.

**Client use:**
- BankBalanceProbe.

### Shop prices — buy & sell
*Status: CONFIRMED (extracted from the MMUD Explorer data viewer) · Realm: both (SELL formula differs Stock vs Paradigm)*

- **Price inputs:** an item's cost is derived from its MDB `Price` + `Currency`, the shop's `Markup%`,
  and the buyer's Charm.
- **Charm 50 is the neutral "retail" point** (no discount, no surcharge). A Charm of 0 in the data means
  "unknown," so the client prices unknown Charm at 50.
- **Base value → copper.** `copper = Price × {Copper:1, Silver:10, Gold:100, Platinum:10000,
  Runic:1000000}` (Currency codes 0–4). All the math below is in copper; the display then reduces to
  the friendliest denomination that keeps the value ≥ 10 (or copper when < 100).
- **BUY (per shop; identical formula in both realms).** Markup first, then charm:
  `buy = baseCopper + Fix(baseCopper × Markup%/100)`; if Charm > 0,
  `buy = (1 − ((Fix(Charm/5) − 10)/100)) × buy`. (`Fix` truncates toward zero.) Charm above 50
  discounts, below 50 marks up, exactly 50 is retail (Charm 60 → ×0.98, Charm 40 → ×1.02;
  `ShopPriceCalculator.CharmBuyMod`). Buy takes the item's **stock price** with a
  **charm-based markup or discount**.
- **SELL ignores markup → same at every shop for a given charm.** Sell nets money by **shop + character
  charm**.
  - **Stock:** `sell = Fix((Fix(Charm/2) + 25) × baseCopper / 100)`.
  - **Paradigm/GreaterMUD:** `sell = (baseCopper/2) × (1 + Fix((Charm − 50)/5)/100)`.
- **Charm no-op.** Charm 0 or exactly 50 leaves BUY at retail; the two SELL branches both land on
  ~half base at Charm 50.

**Client use:**
- The MMUD Explorer data viewer wraps charm-scaled totals above 4,294,967,295 copper (a legacy 32-bit overflow
  bug); the client deliberately does **not** replicate that wrap.

### Shop stock & restock data
*Status: CONFIRMED (verified against the 1.11p Shops table); `Time-N` units CONFIRMED 2026-07-10 (the MMUD Explorer data viewer's Shops tab rendering)*

- **Each shop carries a fixed list of items it can stock** (the Shops table's Item-0..19 slots). Every
  stocked item has one of two replenishment behaviours:
  - **Restocking** — regenerates on its own, a **percentage chance over a time period**, so it trickles
    back into stock without player involvement.
  - **No-stock** — never spawns on its own; the shop only has one to sell **if a player sold one to that
    shop**. Player sells are what seed a no-stock item.
- **Each of the twenty stock slots is five fields, not one:** `Item-N` (item id), `Max-N` (the shop's
  stock **cap** for that item), `Time-N` (restock **period**), `Amount-N` (units replenished per period),
  `%-N` (restock **chance** per period). So the restock rate is fully data-driven.
- **In the shipped set `%-N` splits cleanly:** **100** = always restocks (344 slots), **0** = never
  self-restocks → the **no-stock** items that only exist in stock when a player sold one to the shop
  (330 slots), everything between = a probabilistic trickle (e.g. 35 / 25 / 5).
- **`ShopType` 10 is the ordinary buy/sell merchant** (7 = bank, 8 = trainer). `Markup%` is the buy
  markup fed to *Shop prices — buy & sell*.
- **`Time-N` is in minutes.** The MMUD Explorer data viewer renders each slot's restock in a **Regen** column as
  `<%-N>% for <Amount-N> per <Time-N humanised>` — humanising the minutes into `10m`, `2h` (120),
  `4h` (240), `12h` (720), etc.
- **A `%-N = 0` slot renders as `no regen` regardless of its `Max-N`** (the cap still shows in its own
  column, but nothing spawns on its own).
- **The MMUD Explorer data viewer's stock table columns are `# | Name | Max | Regen | Cost`**, Cost being the buy price
  at the chosen Charm with `Markup%` applied.

**Client use:**
- **Data-model gap for the loot feature.** `ShopStockIndex` today reads only `Item-N` (item → shops
  that *can* carry it — the candidate list). AutoBuy/AutoSell that reason about real availability need
  `Max/Time/Amount/%-N` read too; but since live stock count isn't knowable from static data, the
  engines should treat the index as "shops capable of stocking X" and confirm off the **live buy/sell
  result** (a `%-N = 0` item may simply be out until someone sells one).

### `list` — live shop stock readout
*Status: CONFIRMED 2026-07-10 (in-game capture)*

- **In a shop, `list` prints a three-column table of the live stock**, so real availability *is*
  readable at runtime — parse `list`; don't predict from the static `%-N` restock data:

```
The following items are for sale here:

Item                    Quantity        Price
-----------------------------------------------
torch                   250             Free
lantern                 40              4 gold crowns
rope and grapple        56              10 gold crowns
iron ration             430             10 silver nobles
crowbar                 35              6 gold crowns (You can't use)
glass jug               5               2 gold crowns
```

- **Item** = the name to feed `buy <item>`. **Quantity** = current stock count. **Price** = formatted
  currency (or `Free`).
- **A trailing `(You can't use)` suffix marks an item the character can't use** — shown when the
  character's class / stats bar the item from being *used*. This suffix is **informational only**.

**Client use:**
- The `(You can't use)` suffix does **not** gate auto-buy. If the user flagged the item AutoBuy, buy it
  regardless; the player may want it for a mule, a party member, resale, or a quest. User intent (the
  AutoBuy flag) always wins over the usability hint.

### Buy / sell result lines
*Status: CONFIRMED 2026-07-10; ParaMUD free-item line CONFIRMED 2026-08-02 (capture `paradigm-20260802-164843`)*

| Event | Line |
|---|---|
| Buy OK | `You just bought <item> for <amount> <currency>.` |
| Buy — free item (stock) | `You just bought <item> for nothing.` |
| Buy — free item (ParaMUD) | `You just bought <item> for 0 copper farthings.` *([CONFIRMED] 2026-08-02, capture `paradigm-20260802-164843`)* |
| Buy — can't afford | `You cannot afford <item>.` |
| Sell OK | `You sold <item> for <amount> <currency>.` |
| Sell — worthless | `You sold <item> for 0 copper farthings.` |
| Sell — shop refuses | `You cannot sell <item> here.` |

### Auto-buy / auto-discard band semantics
*Status: **Client policy** — CONFIRMED 2026-07-10 (user design)*

- **Auto-discard with no Min/Max band set → discard *all*** of that item (drop every copy).
- **Auto-buy with no band → buy as many as affordable.**
- **Ticking Auto-buy on in the item-edit dialog defaults `MaxToGet` to 10** (the user changes it from
  there). So a freshly-flagged auto-buy item is bounded at 10 by default, never unbounded-by-accident.

### Repeated `price` directives add up
*Status: CONFIRMED 2026-09-24 (user) · Realm: both*

- **Every `price <amount> <msg>` directive on a textblock line charges its amount**, so a line that
  lists the same `price` several times charges the **sum**, not the amount once. (The NPC side of these
  fares is in *Movement & navigation → Greet teleports — an NPC transports a player who asks*.)
  An escalating tier ladder is different: the jail `bribe guard` line is read top-down and charges only
  the last tier you can afford (*Movement & navigation → Jail `bribe guard` — cell-hop helper with an
  escalating toll*).
- **The coin is named by the directive's trailing letter** (R runic / P platinum / G gold / S silver),
  else copper.
- **Example — Seher'Sahham (monster #715, 16/2666), `ask Seher'Sahham activate`:** TB #2778 is
  `price 100000 2446` ×10, then `message 2447`, then `teleport 637 16` — a **1 runic** fare
  (10 × 100,000 copper) to Damp Cavern, Wellspring (16/637). The same data ships in stock and Paradigm.
- **Decoders that collapse repeats show a real multiplier.** The greet tree's
  `Cost: 100,000 copper (x10)`, as the MMUD Explorer data viewer shows it, is a repeat count that is a real multiplier.

---

## Party

How MajorMUD parties form, move, lose and regain members, and how party clients (MudPlay and MegaMUD) signal each other: `par` output, follow/drag movement, drop / death / training effects, ailment and pause signalling, and the telepath probes and requests members exchange.

### Party size bounds
*Status: CONFIRMED*

- **A party holds at least 2 and at most 6 members.** Party size: minimum 2, maximum 6.

### Co-location and the `Also here:` line
*Status: CONFIRMED 2026-08-28 (user; report `stock-20260828-124347` + scrollback)*

- **Party members are always co-located: a party is a single room.** If a name shows as an active (non-invited) `par` row, they are in your room, full stop. So the correct "is this member reachable?" gate is *party membership* (`par`), **not** the room's `Also here:` line. *(2026-08-28, user)*
- **A party member may be missing from `Also here:` for two reasons, and neither means they left the room.** *(2026-08-28, user — report `stock-20260828-124347` + scrollback)*
  1. **The leader you're FOLLOWING is never listed in `Also here:`.** The game prints `You are following <leader>.` as a separate status line instead, and `Also here:` shows only the *other* occupants. The reverse also holds: when **you** lead, your followers **do** appear in your `Also here:` (you follow no one).
  2. **A member who is HIDING is removed from `Also here:`** (see *Movement & navigation → Hiding — sneak vs hide, the hide state machine, and search reveals*). They're still in the room and in `par`.

### `par` output block
*Status: CONFIRMED*

- **`par` output must never be read as a room name.** The party-list command replies with a fixed block whose **first line is `You are following <leader>.`** (the follower's follow status), then `The following people are in your travel party:`, then one indented roster row per member (`<name>  (<class>)  [K/M: N%] [H: N%]  - Frontrank/Backrank`).
- **The block routinely lands just before a dragged room, in room-title colour.** A follower's party tracking polls `par` constantly, so this block often lands in the room-display buffer just before a dragged room. `You are following <leader>.` renders in the **same bright cyan** the room title uses, so the colour-anchored room-name detector will grab it unless the `par` lines and the drag line are treated as block boundaries.
- **`par` lists live membership only** (see *Dropped ally rescue* and *Member death shows as an invited slot in `par`* for what a dropped or dead member looks like).
- **Client use:**
  - `RoomDisplayParser.PartyChatterBoundaryPattern` treats the `par` lines and the drag line as block boundaries. The room the follower lands in is displayed immediately after ` -- Following your Party leader <dir> --`, so that drag line is the natural boundary.

### `par` row: secondary-resource bracket
*Status: CONFIRMED*

- **A `par` row omits its secondary-resource bracket entirely when that resource is exactly 0 points.** The bracket is mana `[M:N%]` for casters, kai `[K:N%]` for Mystics / monks, and the rule holds for mana and kai alike.
- **It's a 0-*points* rule, not a 0-*percent* one.** A caster with a few points left still prints `[M: 0%]` (bracket present).
- **The row keeps its `[H:N%]` bracket**, so a drained member is a member row missing its secondary field, not a dropped member.
- **Client use:**
  - A bracket-less row must still parse (or reconciliation drops the member).
  - An absent bracket on a known-caster row (`BaselineMp > 0`) means 0, not "unchanged."

### Following the leader: follow / drag movement
*Status: CONFIRMED (live follower capture, Darkwood Forest)*

- **A party follower is dragged one room per leader step, announced by ` -- Following your Party leader <dir> --`.** Movement is leader-driven: when the party leader walks, the game moves every follower one room the same way and prints this line immediately *before* the follower's new room display. The follower sends no bytes and issues no movement command of its own (`PartyFollowerMovementGate` holds its engines), so this line (`-- Following your Party leader <dir> --`) is the **only** signal that a dragged follower moved.
- **The direction is the long-form word the game prints** (`north`, `northeast`, `up`, …). Verified from a live follower capture walking Darkwood Forest (northeast / east / southeast / southwest / south drags).
- **Hidden/foliage exits drag the follower with no direction.** Some Darkwood Forest exits are text-only: the leader prints `<leader> shoves aside the foliage, and disappears among the trees.` and the follower is pulled through with `You push through the dense foliage, and walk onto a small path.` — **no** ` -- Following your Party leader <dir> --` line and **no** cardinal direction. The follower's room changes but there is nothing to feed `NoteFollowMove`, so the tracker sees the new room as a mismatch and must recover via replay/candidate resolution rather than a predicted step.
- **Client use:**
  - The client keys on the follow line to stay located; without it the tracker keeps its old anchor, reads every new room as a mismatch and falls to Lost within a few rooms.
  - The follow line is handled via `FollowMoveObserver` → `RoomTracker.NoteFollowMove`. `NoteFollowMove` wraps `RoomTracker.NoteMoveSent`'s prediction core but flags the move as a follow-drag, so its near-instant arrival isn't taken for a passive re-look.

### Losing the leader disbands the party
*Status: CONFIRMED*

- **Losing the leader disbands the whole party, whether the leader disconnects or dies.** There is no grace-window auto-invite for a lost leader. On the leader's own death, the party is gone by the time they respawn in the graveyard.

### Dropping (0 HP) or instant death removes you from the party
*Status: CONFIRMED; suicide / instant-death detail CONFIRMED 2026-08-25 (user)*

- **Dropping (hitting 0 HP) doesn't just immobilize you. It removes you from the party game-side.** After a miracle-save death the `par` check reads `You are not in a party at the present time.` even though the client still believed it was partied and following the leader. *([CONFIRMED])*
- **A dropped character still tracks the leader's room only because the leader `drag`s them.** Following is an artifact of the drag, not live party membership. *([CONFIRMED])*
- **`suicide` / instant death also removes you, with no drop stage.** *([CONFIRMED 2026-08-25, user])* A `suicide` (or any instant death that costs a life) skips the mortally-wounded / 0-HP drop entirely. It goes straight to `After a LONG thought, you take your own life.` → `You now have N lives remaining.` → respawn.
  - Party removal still happens: a **follower** sees `You are no longer following <leader>.` and is out of the party, and a **leader**'s party **disbands**.
  - There's no `<name> drops to the ground!` line, since the character never passed through the mortally-wounded state.

### Dropped ally rescue
*Status: mixed (per-bullet tags below; bullets without a tag are Unrated)*

- **The drop line is seen party-side and by the dropped character.** When a character drops, everyone in the room (the party included) sees `<name> drops to the ground!`. The dropped character sees it with their **own** name (observed: `Raijin drops to the ground!`). That line is the party-side signal that a member has gone down.
- **The drag prints to the dragged character on every move.** Once someone starts it, the drag prints `<leader> is dragging you around.` to the dragged character on each of the dragger's moves (observed: `MudPlay is dragging you around.`).
- **Drag is manual, never automatic.** **Any player** can `drag <name>` a dropped character *([CONFIRMED] 2026-09-26, user)*; in a party it's normally the leader who does it after seeing the drop line. Nothing drags them on its own. Dragging only relocates the still-mortally-wounded body. It does **not** revive them or restore party membership.
- **A dropped ally is revived with `aid` and/or a heal.** A dropped ally sits at 0 HP or below and can't act for themselves. They must be brought back by **`aid <name>`** and/or a **heal** that lifts their HP above 0. So a party leader watching `<member> drops to the ground!` should **aid and heal that member** (drag is a separate, optional relocation choice, not the rescue).
- **A dropped ally leaves `par` — immediately on Stock** *([CONFIRMED] 2026-09-26, user)*. On **Stock** a dropped member is removed from the party at once and no longer appears in the `par` roster (`par` lists live membership only). On **Paradigm** `par` is believed to keep showing them with a negative HP% *([NEEDS CONFIRMATION])*; they can't be moved except by `drag`, and what happens to their party standing once dragged and moved on Paradigm isn't known. Their vitals therefore stop refreshing from `par`, so tracking a dropped, then partially-recovered ally's HP needs an out-of-band poll.
- **An `@health` telepath polls a member's vitals.** *([CONFIRMED])* Sending an ally a telepath `@health` makes their client's @health responder reply with their current HP / MA. This is an out-of-band way to read a member's health when `par` won't show it (e.g. after they've dropped off the roster).
- **A name-targeted heal still lands on a dropped ally who's been aided.** *([CONFIRMED])* Even though an aided-but-still-dropped ally isn't in `par` anymore, a heal cast **at them by name** still reaches them. A party healer can keep topping them up until they fully recover / rejoin.
- **Recovering to positive HP does NOT auto-rejoin the party. A re-invite is required.** *([CONFIRMED])* The drop removed the character from the party game-side, so bringing them back above 0 HP (via `aid` + heal) restores their ability to act but **not** their membership.
  - The **party leader must `invite <name>` again** to pull them back into the group. Until then, the recovered character is solo even though they're standing right there.
  - This holds both ways. When the **local** character recovers from a self-drop, the client must NOT resurrect the wiped roster; it waits for a real follow / `par` signal (which only arrives after the leader's re-invite). When a **leader** revives a dropped member, the rescue sequence is `aid` + heal **then** `invite <name>`.
- **Client use:**
  - Client reaction (party healer, self is a member with party heals): treat a member's drop as a **wait condition** and pause farming / movement to stay with them. Once they've been **aided** back above 0, keep **healing them by name** despite their absence from `par`, and poll their HP periodically via an `@health` telepath until they recover. Then (if leading) **re-invite** them.
  - Implemented in `AllyDroppedHandler`. It:
    - asserts `MovementCoordinator.AllyDownGate`
    - sends `aid <name>`
    - exposes the aided ally to `CastingDirector`'s downed-ally heal category
    - polls `@health`
    - releases on a full-HP reply / rejoin / rescue timeout
    - re-invites when leading
  - Its own recent-leader memory recognises a dropped leader that a leader-disconnect already wiped from the roster.

### Member death shows as an invited slot in `par`
*Status: CONFIRMED*

- **A dead non-leader member leaves the active party but shows in the leader's `par` as an invited (pending) slot.** That slot is **indistinguishable from a genuine pending invite**.
- **A member death is recognized from the room line `<Name> has died.`, not from `par`.** The line is emitted where they're killed. The leader keys roster cleanup off that named member by **uninviting** them; there's no automatic removal.
- **Client use:**
  - Never infer a death by diffing `par` alone. A died-and-now-invited name looks identical to a recruit we're still waiting on; only the death line disambiguates.

### Training drops you from the party
*Status: CONFIRMED (report `stock-20260801-002423`)*

- **Training (`train` / `train stats`) is a realm excursion. It briefly drops you out of and back into the realm**, emitting `<Name> just left the Realm.` then `<Name> just entered the Realm.` to everyone in the room.
- **The party effect depends on who trained.**
  - A **follower's** train drops only that follower. This is the same as a disconnect: they are removed server-side and need a fresh leader invite to rejoin. They do **not** auto-rejoin on return.
  - The **leader's** train disbands the whole party (see *Losing the leader disbands the party*). The leader sees `You are not in a party at the present time.` on return.
- **Self perspective (the character doing the training): entering the train-stats screen breaks up OUR OWN party server-side.** Our client must reset its own `PartyState` on train-stats entry to match:
  - a **leader** clears its whole roster (party disbanded)
  - a **follower** clears the "following `<leader>`" state (no longer following)
- **Client use:**
  - Route `<Name> just left the Realm.` through the same member-drop correlation as a disconnect. A trained follower is then stamped into the reconnect grace window and auto-re-invited on their `just entered the Realm.`. Members who train at staggered times each get re-invited as they individually re-enter within the window.
  - Skipping the `PartyState` reset leaves a stale "following" state that makes the client **reject the leader's fresh re-invite**. Both the `@join` handler and the invite auto-accept no-op on "already following `<leader>`", so the follower never rejoins (report `stock-20260801-002423`).

### The level-11 train is a class-gated quest
*Status: CONFIRMED 2026-09-23 (user); how it works CONFIRMED 2026-09-26 (user)*

- **The 10→11 train is a quest, not a normal trainer visit.** It needs a **key from the mummy NPC** on the
  **third level** of the crypt below the **Silvermere graveyard**. The key opens a hall whose exits are each
  **class-gated**; the trainers that perform the 10→11 train — the **(class) spirits** — wait at the end,
  at the bottom of the crypt.
- **So only same-class characters can make the trip together**; a mixed party splits at the hall. That is
  why the step is only easily auto-trainable running solo.
- **Client use:**
  - Party auto-train stops members at level 10 by default (Auto-Trainer → *Leave the level 11 train to a solo trip*).

### Targeted casts on a hiding member
*Status: CONFIRMED 2026-08-28 (user + screenshot)*

- **The only time a targeted cast on a party member misses is when that member is HIDING.** The server answers **`You do not see <name> here!`** (and the member isn't in `Also here:`).
- **Client use:**
  - Don't pre-gate single-target party casts on `Also here:`; that wrongly skips the followed leader and any hidden member. Attempt the cast based on party membership.
  - If `You do not see <name> here!` comes back, back that member off until you **move** or they **reappear in `Also here:`** (they unhid). Don't re-fire the cast, and repeat the failure, every round.

### Resting observed in the party
*Status: mixed (rest line CONFIRMED; rest-to-use-the-wait is **Client policy**, user directive 2026-07-11)*

- **A party member sitting down to rest is announced to everyone else in the room as `<name> stops to rest.`** *([CONFIRMED])* `<name>` is the given name. The actor's own view uses a different verb form (`You stop to rest.`), so the third-person line never matches the resting player's own row.
- **The meditate-observation line is not yet confirmed.** Do not guess it.
- **Rest-to-use-the-wait.** *(**Client policy**, user directive, 2026-07-11)*
  - When the party **leader** is `@wait`-held and **not poisoned**, the leader rests (or meditates) to use the forced downtime, until the wait clears.
  - A **follower** that sees the leader rest/meditate rests/meditates too, **unless the follower is poisoned** (poison ticks break rest and waste the downtime).
  - Poison blocks resting altogether — see *Health, resting & recovery → Poison prevents resting* (CONFIRMED 2026-08-17), which supersedes this 2026-07-11 note's claim that only these two downtime-rest paths gate on poison.
- **Client use:**
  - The `<name> stops to rest.` line flips `PartyMember.Resting` the instant it's seen, ahead of the 5-second `par` poll, so a follower can mirror the leader's rest immediately.

### Party ailment signaling and cross-client sync (MegaMUD parity)
*Status: CONFIRMED 2026-09-12 (user)*

This covers how a client learns which ailments afflict itself and its party members, and how it warns the party. It is modelled to interoperate with MegaMUD, so MudPlay and MegaMUD clients can party together and each engine reacts identically.

- **Self (most reliable): a client knows its OWN ailments from the apply and wear-off spell messages.** This is authoritative for self; the statline carries no ailment flag.
- **Poison is NOT announced.** In a party it is read from each member's **`P` flag on the `par` party screen**; solo, from the poison apply message.
  - A member's poison chip is set when `P` is present in the par row and cleared when it drops.
  - `par` carries no other ailment letter.
- **Blind / Diseased / Confused / Held are announced verbosely on SAY as a bare token**: `@blind`, `@diseased`, `@confused`, `@held`.
  - **Wire form:** the sender types the token `.`-prefixed (`.@blind`, `.@held`, …; the leading `.` sends it on say), and observers see the bare token (`@blind`). The same holds for `@panic` (sent as `.@panic`).
  - There is **no `on`/`off` argument**; MegaMUD sends the bare token on apply only.
  - An observer sets the named member's chip on the bare token.
  - Confusion has no realm cure, so its announce only lights the chip. It is kept because ignore-confusion behavior applies to party members too.
  - *Historical:* MudPlay formerly sent a paired `.@X on` / `.@X off` toggle (it lacked wear-off detection at the time). MegaMUD uses bare tokens; the clear comes from the signals below.
- **No say signal is sent on wear-off.** A member's chip clears on **whichever of these is observed first**:
  - a **witnessed cure** on that member (our cast, or one seen cast on them in the room)
  - the **spell's duration timing out**
  - the **par `P` flag dropping** (poison)
  - a **`@status`** reply of "no ailments"
- **The only clear signal the afflicted member emits is `@ok`**, a telepath to the leader to release the wait.
- **Ailment duration is deterministic.** When a monster's ailment spell *lands* (a "resist" means it never landed, so there's no chip at all), the duration is a specific number the spell record computes from **that monster's cast level**.
  - The same spell on different monsters differs only because their cast levels differ.
  - So an observer that witnessed the apply computes the exact duration (`Dur` rounds × spell-round seconds, at the caster's cast level) and auto-clears the chip when it elapses, with no fudge beyond clock jitter.
- **Client use:**
  - MudPlay reads its own ailments with `ConditionTracker`, matched against the Messages table.

### `@wait` / `@ok` party pause
*Status: CONFIRMED 2026-09-12 (user); pause-flag detail CONFIRMED*

- **An afflicted follower telepaths `@wait` to the leader and `@ok` when it clears (follower → leader, TELEPATH).**
  - A follower afflicted by poison / blind / confused / diseased / **held** telepaths **`@wait`** to the leader, unless that ailment's `Ignore<X>` is set.
  - It telepaths **`@ok`** when its last non-ignored ailment clears.
- **`@wait` / `@ok` is a leader-directed pause flag, not a momentary signal.** The leader stays paused until **either** the same member telepaths `@ok`, **or** the leader's own wait timer expires.
  - The timer is the "If leading, wait only (s)" cap (`PartySettings.IfLeadingWaitTotalSec`).
  - On expiry the leader gives up and resumes, so a dropped / AFK member can't strand the party forever.
- **The leader-side "ignore @wait when leading" opt-out drops inbound `@wait` before it ever pauses.**
- **Held follows the same `@wait` / `@ok` flow and cannot be suppressed.** A held member can't move, so the party waits for them. Held has no `Ignore` gate, so it is never suppressible.
  - **Both signals pause the leader.** A held member telepaths `@wait`/`@ok` *in addition to* announcing its `.@held` on say: the say lights the member's chip, and the `@wait` pauses the leader. The inbound `@held` say also routes through the same pause (`PartyEssentialHandlers.NotePause`), and that member's `@ok` on cure releases it.
- **All of this is party-only.** Solo (no party / no leader / you ARE the leader), nothing is telepathed. Self recognition and clearing run entirely off the apply/wear-off spell messages.

### `@panic` leader warning
*Status: CONFIRMED 2026-09-12 (user)*

- **`@panic` is a party-wipe warning (leader → party, SAY).** A party **leader** whose HP crosses its **"hang if below"** floor says the bare token **`@panic`** (sent as `.@panic`). It then hangs up (or `break` + `sys goto <wimpy>` if opted in).
- **Two checkboxes gate it:** **"use @panic while leading"** (send) and **"ignore @panics"** (receive).
- **A member not ignoring `@panic` reacts the same way**, hanging up or doing break + sys-goto-wimpy, per its own settings.

### `@comeback` (follower → leader)
*Status: CONFIRMED 2026-07-31 (user; report `stock-20260731-082602`)*

- **`@comeback` is a follower→leader telepath.** The follower asks the leader to come back and re-grab/re-invite it.
- **It is messaging only.** It must **never** drive a walk on the follower's own client.
- **A follower sends it in exactly two left-behind cases:**
  1. after a disconnect+reconnect where it either doesn't see the leader in the room or the leader left before re-inviting
  2. when the follower is stunned / hit by a movement-preventing affliction and the leader didn't wait for it to clear before leaving the room
- **Client use:**
  - The client's `ComebackRequester` is correctly telepath-only.
  - A follower must not auto-navigate itself back to a stale self-selected walk-to target.
  - What happens to movement after a death is in *Death & corpse recovery → Death wipes all effects*.

### `@join` while already following
*Status: CONFIRMED 2026-07-10 (capture)*

- **A follower client that receives `@join` while it is already following someone denies it.** It answers the telepath with **`I'm following someone; denied.`**, so `@join` is not idempotent against an existing follow.
- **This shows up downstream when a reform re-invite was lost.** The leader's `@join` nag then telepaths a member who never dropped their follow state, and the join is refused.
- **Client use:**
  - Landing the re-invite at the right time (post-arrival) avoids the nag path entirely.

### Party intel probe: `@level` / `@version` on partying
*Status: CONFIRMED 2026-08-08 (user design)*

- **When we start partying with a player, the client telepaths them intel probes.** This is `PartyProbeManager`. It works whether or not we lead, and is gated by Settings → Party "probe stats on partying" (default on).
- **`@health` fires on every join.** This is `PartyPoller`, for the live party-window HP/MA vitals (unchanged).
- **`@level` + `@version` fire only the first time we party with that player on a given local day.**
  - Gated by `PlayerObservation.LastPartiedUtc`, the same once-per-calendar-day way `GreetManager` rate-limits auto-greets.
  - The `@level` reply is recorded by `PartyLevelProbe` (the sole `@level` recorder).
  - The `@version` reply is recorded onto the player record (`PlayerObservation.Version` / `VersionAt`).
- **`@version` reply shape: the answering client returns its name + version, brace-wrapped**, e.g. `{MudPlay 2.37.0}` or `{MegaMud 1.03u}`. It is recorded verbatim.
  - The reply is correlated to the member we just probed within a short window.
  - It must be a brace-wrapped, letter-led payload carrying a digit. This rejects denial / chat lines and the `@level`/`@health` replies that share the window.
- **A recorded exact level supersedes the title-derived band, UNLESS the band's floor has risen above the exact reading** (`TitleRange.Min > exact`). This is exact-vs-title-band reconciliation (`PartyLevelEstimate`).
  - A floor above the exact reading means the player has clearly trained since we last asked: their `who` title moved up to a band starting above our recorded level.
  - In that case the **title band wins** until we re-learn an exact level at or above the band's floor.
  - A lower or overlapping band never overrides a valid exact; only a band whose floor has passed it does.
  - Example: recorded level 9, title band now 10-14 → the band wins, so the member reads 10-14, not a stale 9, until a fresh `@level` lands ≥ 10.

---

## Quests

How MajorMUD quests are structured in the game data (kill steps, NPC dialogue steps) and how a character's quest progress is stored in, and read back from, ability flags.

### Quest-flag values (`abil` / `sys god abil`)
*Status: CONFIRMED 2026-09-13 (user captures + mudinfo.net) · Realm: both*

- **Quest progress lives in ability flags** — the same `giveability <flag> <value>` targets `QuestCrawler` keys quests on. The value a flag currently holds is readable live, differently per realm.
- **Paradigm reads one flag at a time (built-in, ungated):** `abil <flag>` → one line **`<Name>(<flag>)   <value>`**, e.g. `GoodQuest(126)             8`. The flag number is in the reply, so replies need no order-correlation. It also answers non-quest abilities (`AC(2)  780`).
- **Stock dumps every flag at once (behind sys-god access):** `sys god <name> abil` → one wrapped line dumping **every** ability as `flag(value)` pairs, no names: `User abilities: 126(17) 2(1) 129(3) 133(9) …`. One command, full dump.
- **"Complete" is a per-quest value, not a universal one.** Each quest finishes when its flag reaches a specific value that depends on the quest line — Phoenix `133(9)`, Dao Lord `134(12)`, Red Dragon `131(3)`, High Druid `129(2)` (`129(1)` = reward granted but "go back for the exp bonus", `(2)` = fully done).
- **The complete value is the terminal reachable flag value**, and `QuestCrawler.CompleteValue` derives it: the highest absolute `giveability` value for a single-part quest / a ladder's last band, and, for an earlier band, the highest give value that falls inside the band's give-step range.
- **Alignment quests (126 Good / 127 Neutral / 128 Evil) are five value-tiers on one flag** — a running counter, not a boolean. Each tier completes at its own value; Evil's tiers cap at **128(2) / 128(3) / 128(11) / 128(13) / 128(31)** (the last is "as far as you can currently go"). A live value of 11 means tiers 1–3 are done and tier 4 (needs 13) isn't. Per-band `observed >= that band's complete value` marks it.
- **Some quests have no auto-detectable complete** (`CompleteValue` = null; left to the manual box / the editor override):
  - **MageBane / Witchunter (flag 50)** has no "finished" flag at all — it climbs 1→4 by `addability` and just stops.
  - **Perfect Stealth (186(0))** completes at value **0**, indistinguishable from "not started" via `abil`.
- **Alignment "check" helper flags are NOT quests** — GoodCheck / NeutralCheck / EvilCheck (216 / 217 / 218 on Paradigm). They're sub-markers granted only inside an alignment quest chain:
  - the grant is gated on being at a specific progress value of a canonical alignment flag (`checkability 126 7 : … : giveability 216 1`);
  - it hands the 2nd-alignment turn-in item (severed head 684 for Good, etc.);
  - it's `failability`-gated at the alignment pledge, then reverts to 0.
  - So they never indicate a completed quest, and `QuestCrawler` drops them (`DiscoverAlignmentHelperFlags`: a granted flag — other than 126/127/128 themselves — whose every grant chain checks/tests a canonical alignment flag). The alignment quests' own completion rides flag 126/127/128, not these. Verified: the rule catches exactly {216,217,218} on `data-Paradigm-1.9.1` and nothing on the stock sets.
- **Sync scope is level-eligible quests only.** The login/manual sync only reads flags for quests the character can complete at its current level (the same eligible + level-met + incomplete set the availability announce uses) — on Paradigm that bounds the per-flag `abil` burst; on both realms it keeps marking to quests the character could really do.
- **Client use:**
  - The login quest-completion sync (`QuestFlagSyncManager`, opt-in via `GeneralSettings.AutoSyncQuestFlagsOnLogin`) reads these values before the availability announce and marks `QuestProgress.Complete` for any quest whose flag has reached its effective complete value (the per-quest `QuestDefinition.CompleteValueOverride` if set, else the crawl's). Strictly one-way — never clears.
  - The `@quest <name|flag>` remote reply marks the same way from its live read (every crawled band on a read flag, not just level-eligible ones — the flag value proves it).
  - `@quest update` runs the sync's read-and-mark on demand (no daily gate, no opt-in).

### Quest kill steps & monster placement
*Status: CONFIRMED 2026-07-16 (user)*

- **A command-less quest step sourced from a monster's textblock is a "kill this monster" step.** The flag advances because the monster's **death spell** (or a room **`nomonster`** spell that fires when the room is cleared) grants the quest progress — you receive the flag by killing the monster, not by typing a command. So a crawled step whose Called-From is a `Monster #N` chain and carries no player command narrates `kill <monster> (<drop>)`.
- **A monster a quest requires you to kill is placed in a specific room** — the room's **NPC field** (`Rooms.json` `NPC` = the monster number) names it. That placement is the authoritative room a guide walks you to for the kill (e.g. queen ant #485 is placed at 9/717 via `room.NPC == 485`). The placement mechanic itself is in *Monsters, lairs & spawns → NPC-placed monsters*.
- **When the kill target is summoned rather than statically placed**, its Monsters `Summoned By` record resolves the room: either a room token directly (`Room 9/717` / `Group(lair): 1/531`), or a `Spell #N` that another NPC casts to summon it. In the spell case the summoner is the monster whose **`CreateSpell` == N**, and that summoner's own placement stands in as the target's room (e.g. *hydra head* is summoned by *hydra*'s CreateSpell, so you fight it where the hydra waits). The token kinds are in *Monsters, lairs & spawns → `Summoned By` spawn tokens*.
- **Client use:**
  - `RoomSearchService.QuestKillRooms` resolves the kill room this way.

### Quest dialogue steps — NPC keyword dispatch
*Status: CONFIRMED 2026-07-16 (user)*

- **A quest advances through NPC dialogue, not standalone room commands.** The player types `ask <npc> <keyword>` (the command form is in *Movement & navigation → Keyword command forms (`ask <noun> <keyword>` vs verbatim room CMD)*); the NPC's **root dispatch textblock** maps that keyword to a child textblock (`Action` is a `\n`-separated list of `keyword:textblock` chains, e.g. `crystal:7018`); the child block runs its own `Action` and eventually `giveability <flag> <step>` to grant progress.
- **Called-From links each block to its parent.** The child's **Called From** names its parent (`Textblock #N`), and the root dispatch block's Called From is the **`Monster #N`** — the NPC itself.
- **A crawled step gated behind an NPC is recovered by walking its Called-From chain up to that `Monster #N`**, then reading which dispatch keyword branches into the child that leads to the step. That yields the exact `ask <npc> <keyword>` the player must type (e.g. Mandos quest: `ask archmage valduin crystal` — the full name always works, and the first word usually does too (`ask archmage crystal`), *([CONFIRMED] 2026-09-26, user)*; the quest planner (`QuestStepGraph`) sends the full name, `ask kale mandos free`). The step is then re-anchored on the NPC so the guide links the NPC's placement room (same map used for kill steps).
- **Auto-shown blocks aren't askable.** Dispatch keywords `message`, `text`, and `greeting` are shown automatically on interaction (or as flavor), not typed — a step reached only through one of those has no `ask` command and isn't drafted as one.
- **Client use:**
  - `QuestStepGraph.ResolveAsk` performs the Called-From walk and keyword resolution.

---

## Death & corpse recovery

What happens when a character dies — the death threshold, lives, effect wipe, the death-line forms — and how the dropped items and coins (the deathpile / corpse) are recovered.

### Death threshold & consequences
*Status: CONFIRMED*

- **Each BBS sets its own negative-HP death threshold**; not every BBS advertises the number. When HP **reaches or passes** it (at, or more negative than, the threshold), the character **dies**:
  - loses a **life**,
  - **all non-loyal items are lost from the player** (loyal items stay on the player); *where* the dropped items land is realm-type dependent — see *Deathpile — where the items go*,
  - the character is **teleported to the graveyard room** appropriate to the **map** they died on.
- **Graveyard rooms are per-map**; two known graveyards are **`1/2189`** (map 1, room 2189) and **`16/542`** (map 16, room 542).

**The death readout and overkill:**
- **There is no "overkill" message.** The HP figure visible at death is just the value HP was driven to by the killing event.
- **A single large hit can drive HP far below the true floor.** The blow overshoots the threshold with no clamp or announcement, so an overkill death's HP reading **over-negatives** (understates) the real floor.
- **A slow death is an accurate measurement.** Bleeding out, HP crosses the floor one tick at a time and lands right at the floor, so that reading measures the true threshold.
- **One death message, both cases.** An overkill blow and a slow bleed-out print the same death line, so the line by itself cannot tell a slow death from an overkill. The death lines don't depend on how you die *([CONFIRMED] 2026-09-26, user)*, and *Death lines & the miracle-save* holds every line on record — including `You have been slain by <killer>.` and `You have been killed!` (an earlier note had a bleed-out print `slain by` its **last attacker**). The only runtime signal that separates them is the **HP trajectory** into death: a gradual, small-step descent through the bleeding-out band (slow, accurate) versus a single large HP drop that blows past the floor (overkill, discard).
- **An overkill can mask the reached HP entirely.** A killing blow that jumps well past the floor may emit **no sub-floor HP prompt at all** — the client sees the pre-death HP and then the death, and the intermediate value the blow drove HP to is never printed. (Observed: at HP `-241` a `9`-point hit simply killed the character; no `-250` prompt appeared.) So a single terminal reading can never be trusted as a floor measurement.
- **Live-survival evidence is the reliable complement.** While HP ticks down through the negatives and the character is confirmed **still alive** (a *later* in-band prompt proves the previous one was survived), each survived reading is a valid lower bound — the floor sits **below** it. The estimate ratchets down progressively as HP rolls further negative and simply **stops at the death message**. The terminal/masked reading is structurally excluded because it is never followed by another in-band prompt.
- **Client use:**
  - The stored death threshold is only a starting estimate — the client seeds it at `-25`, a guess. Refine it from **slow deaths and survived negative-HP readings; never from an overkill death reading** (`DeathFloorTracer.RecordDeath` / `NoteHp`); an overkill reading is unreliable and must not push the estimate more negative.
  - The client's floor auto-refinement must classify off the observed HP steps, not the message — and, per `DeathFloorTracer`'s stated assumption, only while the killing blow isn't a huge hit that leaps right past the floor.

### Death wipes all effects
*Status: CONFIRMED 2026-08-09, 2026-08-28 (user; report `paradigm-20260809-114444`) · Realm: both*

- **Death wipes every ailment, status effect, buff, and debuff off the character** — poison, disease, blindness, confusion, held/knockdown, and every positive buff alike. This holds on **both stock and Paradigm** (realm-independent); the character respawns at the graveyard with a clean effect slate.
- **A poison ticking at the moment of death clears with it** — the death sequence carries `The effects of the poison wear off!` right alongside `You have been killed!`. So after a death the character is at full HP with **no lingering effects of any kind**; any client-side effect / buff tracking must be flushed on death.
- **It is the death event doing it**, distinct from a buff-strip room (which dispels on entry).
- **Party timers: only the dead character's buffs are gone** *([CONFIRMED] 2026-08-28, user)*. Consequence for a buff-maintaining automation:
  - on **our own** death, clear the timers for **our self-buffs** (they're gone) but **keep** the timers we hold on party members (they didn't die — still buffed);
  - on a **party member's** death (the `<Name> has died.` line), clear the timers we hold **on that member** (their buffs are gone).
- **Client use:**
  - `ConditionTracker` is an observation log driven by the server's applied / wear-off lines, and death teleports you out **without emitting those wear-off lines** — so a condition latched at the moment of death (most dangerously *MovementPrevented*, whose stale flag keeps `MovementCoordinator.HeldGate` asserted and strands the walker "Paused by: Held") never auto-clears.
  - Because the game clears *everything* on death, the client mirrors it with a full `ConditionTracker.ClearAll("death")` on `RoomTracker.PlayerDeathObserved` — no per-flag scoping — matching the mechanic exactly (report `paradigm-20260809-114444`, fixed v2.39.1).
  - After **any** death, every movement engine is cleanly stopped and every retained destination cleared, the same as hitting the Nav Stop button. There is no lingering halt, so a manual or remote nav action afterward runs freely, and nothing re-drives us into the room we died in (report `stock-20260731-082602`).

### Death lines & the miracle-save
*Status: CONFIRMED*

- **A miracle-save is a death, not a rescue.** When a character who still has lives dies, the engine prints a three-line miracle sequence in place of the plain slain line:
  ```
  You have been killed!
  But, due to a miracle, you have been saved.
  You have N lives left.
  ```
  Despite the "saved" wording this **IS a death** — a life is spent (N is the post-death count), non-loyal items drop, HP resets to full, and the character is teleported to the graveyard / temple room, exactly like any other death. The "miracle" text is **flavor that comes with having lives**, not a rescue that avoids the death.
- **At 0 lives the engine force-exits the character** from the game (permadeath) rather than print the miracle line.
- **Two different lives readouts:** the miracle path prints `You have N lives left.` — a **different line** from the slow / normal-death `You now have N lives remaining.`. A death-capture that keys only off the "remaining" form misses every miracle-save death.
- **The reliable death marker across all forms** is the `You have been killed!` line (DoT / no-named-killer deaths) alongside `You have been slain by <killer>.` (attacker-named deaths) — capture off those, not off the lives readout.

### Deathpile — where the items go
*Status: CONFIRMED 2026-08-24 (user); Stock spill mechanics CONFIRMED 2026-09-26 (user) · Realm: differs — Stock spills loose, Paradigm uses a corpse*

- **Death-pile source differs by realm** *([CONFIRMED 2026-08-24, user])*:
  - on **Stock**, death drops **all your items loose on the ground** — they appear in the room's `You notice … here.` survey and, if the floor is already crowded, **spills on into connected rooms** (see the spill-over bullet); you recover each with `get <item>` (confirmed by `You took <item>.`);
  - on **Paradigm**, your items are held **inside a corpse** (`corpse of <given-name>` in the survey) — the deathpile is a `corpse` object, recovered with one `recover corpse <given-name>` command, **NOT a per-item `get`** *([CONFIRMED] 2026-08-03, user + captures)* (confirmed by `You have recovered the corpse of <name>.`; procedure in *Corpse recovery (`recover corpse`)*);
  - the combat-break rule of *Combat → Non-swing actions break combat (casting, equipping)* (which re-times only the wear/eq burst) is realm-agnostic, so it applies to both.
- **Stock spill-over, in detail** *([CONFIRMED] 2026-09-26, user)*:
  - Dying on Stock spills the items you carried into **the death room**.
  - If that room fills before all your items are down, the rest spill into **one connected room** through a cardinal or diagonal exit or up/down (N/S/E/W/NE/NW/SE/SW/U/D only). Once that room fills too, another exit is picked and the same happens.
  - This repeats **up to roughly 5 rooms away**. Which exit is picked first is not known.
  - It only happens on Stock, because **Paradigm rooms have no item cap**.

### Corpse recovery (`recover corpse`)
*Status: CONFIRMED 2026-08-03 (user + captures) · Realm: Paradigm (Stock has no corpse — see *Deathpile — where the items go*)*

- **Non-loyal items and coins go into a corpse of <player>** on the death-room floor; loyal items stay on the player.
- **The room's floor survey names it by the player's GIVEN name only**, no article: `You notice corpse of Ermias here.` (character "Ermias Asghedom" → the corpse reads "Ermias").
- **Recover it with `recover corpse <given-name>`** (e.g. `recover corpse ermias`). Bare `recover corpse` also works but **gets confused when several corpses share the room**, so always name it.
- **Recovering your OWN corpse never needs a password**, even with corpse passwords set; the password system only gates *other* players looting your corpse.
- **One command pulls the WHOLE pile back at once** — coins and items together. The output is: `You begin to pick through the corpse of <name>...`, then `You picked up N <denom>.` per coin denomination and `You took <item>.` per item, ending with the green completion line **`You have recovered the corpse of <name>.`** — the single reliable "pile recovered" marker.
- **Auto-recovery procedure:** on entering the death room, read the `You notice … here.` survey; if it holds `corpse of <ourGivenName>`, send one `recover corpse <name>` and finalise on `You have recovered the corpse of <name>.`; if the corpse is NOT in the survey, the pile is gone (looted / decayed) — mark it Missing and send nothing (never per-item `get`, which just spams `You don't see <item> here.`).

### Coins in the deathpile
*Status: Unrated*

- **Coins on hand drop into the deathpile too**, alongside the non-loyal items — recoverable from the deathpile / corpse like the rest of the drop (per *Deathpile — where the items go*).
- **Five denominations** (largest first): `runic coin`, `platinum piece`, `gold crown`, `silver noble`, `copper farthing` — values per *Money, banks & shops → Currency denominations & value ladder*.
- **The deathpile display lists each denomination by its own count** (e.g. `100 gold crowns` + `1 platinum piece`), **not** re-bucketed into a consolidated wealth total.

---

## Sysop commands

The `sys …` sysop command family — `SYSOP STATUS` room dumps, `sys map`, `sys god`, `sys goto` — how each is gated, what it prints, and how the client uses it.

### Sysop power gating
*Status: CONFIRMED 2026-09-04, 2026-09-05 (user)*

- **Sysop commands require sysop privileges on the BBS.** On an account without them the command is refused.
- **Each `sys …` command is a SEPARATELY-gated permission with its own level** *([CONFIRMED] 2026-09-05, user)* — a board can grant one and deny another. In particular, **many stock boards grant `sys map` but NOT `sys status`**, so for those players `sys map` is the *only* game-side "where am I?" tool.
- **`sys goto` is its own separately-gated power too** *([CONFIRMED] 2026-09-08, user)* — distinct from `sys status` / `sys map` / `sys god`.
- **`sys` commands are NOT gated by the mortally-wounded (HP ≤ 0) state** *([CONFIRMED] 2026-09-08, user)*. Ordinary action commands are refused while mortally wounded ("You may not do that while you are mortally wounded!"). Sysop powers bypass that entirely — `sys goto` (and the other `sys` commands) can be sent and are honoured at **any** HP, bleeding-out included.
- **Client use:**
  - The client's three sysop powers (status / god lives / goto) are independent per-BBS checkboxes, because each `sys` command is gated separately. The client does not use `sys map` (see *`sys map` — ASCII area map*).
  - The client holds its EngineSendGate at HP ≤ 0 (PlayerDroppedGate) because ordinary commands are refused while mortally wounded. So a `sys goto` must go out on a sender that pierces the mortally-wounded hold (the raw un-wrapped wire, like the emergency hangup uses), NOT the gate-wrapped engine sender that drops sends at HP ≤ 0. Consequence: the "sys goto wimpy instead of hanging" escape fires at any HP in its window, including deep in the bleeding-out band.

### `SYSOP STATUS` — forms and arguments
*Status: CONFIRMED 2026-09-02 (user + official help text + live capture)*

Forms, from the game's own help:

- **`SYSOP STATUS`** (abbreviates to `sys st`) — debug dump for the room you are standing in. Intended for diagnosing monsters that stop or never stop regenerating.
- **`SYSOP STATUS <user>`** — status of a named user, if they are currently playing.
- **`SYSOP STATUS ROOM <room> <map>`** — the same dump **for any room on any map**. Argument order is **room first, then map** *([CONFIRMED] 2026-09-02, live capture)*:
  - `sys st room 224 1` → `Room 224  Map: 1` ✔
  - `sys st room 1 224` → `Room error` (read as room 1 on map 224, which doesn't exist)
  - `sys st room 1/224` → `Room 1  Map: 1` — the `map/room` form is **not** understood; it takes the leading integer as the room and defaults the map to 1. Silently wrong rather than rejected, so never send that shape.
  - `sys st 224 1` (no `room` keyword) → `Cannot find user 224` — falls through to the user-lookup form.
- **`SYS LIST USERS`** — lists users and the room each is in.
- **`MAP`** — generated map of the current area. The help warns it is recursive and has caused stack overflows; treat as unsafe to automate.
- **[NEEDS CONFIRMATION]** **Denied wording is unknown** — the text emitted when the command is **denied** is believed to be a generic "Command not recognized". Nothing depends on it: the client gates on the user's own sysop-powers flag and falls back to a timeout, not a string match.

### `SYSOP STATUS` — room dump format
*Status: CONFIRMED 2026-09-02 (user + live capture); per-fact tags inline*

Captured dump (gang-house room, verbatim including the 80-column wrap):

```
Room 2187  Map: 1
This room as Area: Max: 0  Current: 0
Min: 0 Max: 0 Group: Lair by Number: 0
Room Max: 5  Current: 0  Last Killed: 00:00:00 Delay: 0
No controlling room.
Patrollable
Ganghouse
Monsters: None
Items: 521(0) 743(0) 882(0) 690(0) 464(0) 1484(0) 37(0) 890(0) 1443(0) 891(0) 47
0(0) 466(0) 1461(0) 899(0) 420(0) 465(0)
Hidden items: 1845(0) 14(0) 894(0) 223(0) 879(0) 870(0) 897(1) 876(1) 402(0) 430
(0) 264(0) 905(0) 419(0) 422(0) 896(0)
```

- **`Room N  Map: M` is the room's true identity** — the same `map/room` pair the client keys rooms on. This is authoritative location, which is why the parser that reads it is armed only by an outbound sysop status (a forged line would otherwise relocate the player).
- **Item entries are `id(value)`** where the id is the MDB `Items.Number`. Every id in the captured dump resolves to a real item in the `realm2` set.
- **[CONFIRMED, live capture 2026-09-02]** **An entry is one object on the floor; the parenthesised value is that object's stack size minus one** — `(0)` is a single item, `(1)` a stack of two. **An id can repeat in one list.** Dropping two black star keys one at a time gives `172(0) 172(0)` (two objects of one each); dropping two diamonds gives `902(1)` (one object of two). The room's true count of an item is therefore the **sum** of (value + 1) over every entry with that id, never a single-entry lookup. The room display aggregates either shape identically ("You notice 2 black star key" / "You notice 2 diamond"), so it can't be used to tell the two apart.
- **[CONFIRMED, live capture 2026-09-02]** **Player-dropped items DO appear** in `Items:`, immediately. An empty room prints `Items: None` and `Hidden items: None` rather than omitting the lines.
- **[CONFIRMED, user]** **Non-gettable items DO appear** in these lists. The MDB `Items` table carries a `Gettable` column (0 = cannot be picked up; 453 of 2047 rows in `realm2`), so fixtures are filtered from data rather than by a refused `get`.
- **[NEEDS CONFIRMATION, user's read]** **Container contents and carried items do not appear** — items inside a **container** in the room, and items **held by a monster or player**, are not listed.
- **[CONFIRMED, live capture 2026-09-02]** **`Monsters:` values are NOT `Monsters.Number`.** The line carries space-separated bare numbers (`Monsters: 4510 8407`); 4510 / 8407 / 2951 exist in no Monsters row of the `realm2` set, though the same dumps' `Specific Monster: 784-Mayor Godfrey [1/1]` line does carry a real catalogue number. What the `Monsters:` values identify is **unknown** (spawn instances, most likely). **Do not treat them as monster ids.** For "is this specific monster here", read `Specific Monster:`.
- **[CONFIRMED, live capture 2026-09-02]** **Further conditional lines the dump can print:** `Controlling Room: <room>` (vs `No controlling room.`), `Current Area: Max: N Current: N`, `Specific Monster: <number>-<name> [n/m]Last Killed: hh:mm:ss (RG: n)`, and `Placed items: <id> <id>` — bare ids, no parens, listing which of `Items:` are the room's **static placements**. Subtracting `Placed items` from `Items` isolates player drops.
- **[CONFIRMED, live capture 2026-09-02]** **The header often lands on the prompt row** — the server frequently prints the block's first line onto the row the prompt already occupies: `[HP=639/MA=268]:Room 3551  Map: 1`. Any parser that treats a prompt as the block terminator must ignore prompts seen **before** the room header, or it drops the entire dump.
- **The lists wrap at the terminal margin mid-token** — `47` + `0(0)` is item `470`, `430` + `(0)` splits an id from its value. Rejoin the block before tokenizing.
- **The dump carries no `Obvious exits:` line**, so it is not mistakable for a room display.
- **[CONFIRMED, live capture 2026-09-02, report stock-20260902-203631]** **A full `sys st` dump is heavy** — ~8–9 lines (`Room N  Map: M`, `This room as Area:`, `Min/Max/Group/Lair`, `Room Max/Current/Last Killed/Delay`, controlling-room, `Monsters:`, `Items:`, `Hidden items:`) — far heavier than Paradigm's three-line `rm` reply, and the item/hidden lists grow with room contents.
- **Client use:**
  - The client **throttles** repeated locates to keep the dump from flooding the screen: the give-up boundary fires unthrottled, but the eager tier-2 / loop-one-shot / no-engine / `@where` mirror sites reuse the 15s locate throttle (report stock-20260902-203631).
  - Only `Room N  Map: M` is needed for position; the rest is future roomba content-sync fodder.

### `sys map` — ASCII area map
*Status: CONFIRMED 2026-09-05 (user + live captures)*

- **`sys map` draws a text ASCII area map centred on the player, with no map/room numbers** (unlike `sys st`).
- **Symbol legend:** **`X`** = your current room, **`*`** = a plain room (neither shop nor lair), **`S`** = shop, **`L`** = lair.
- **Links:** **`-`** (E↔W), **`|`** (N↔S), **`/`** (NE↔SW diagonal exit), **`\`** (NW↔SE diagonal exit) — i.e. the eight compass directions N/S/E/W/NE/SE/NW/SW.
- **Up/down aren't rendered on the flat grid** — a room whose only exit is **vertical (up/down)** draws as a lone `X`.
- **Only remaining unknown:** whether letters other than S/L can appear (surfaces during parsing).
- **Client use:**
  - The client does not use `sys map` — its output can't be matched to the room graph reliably (there are no numbers, only the drawn shape). The Sysop map checkbox was removed.

### `sys god <name> add life`
*Status: CONFIRMED 2026-09-04 (user)*

- **`sys god <name> add life` adds one life to the named character.** Requires real sysop/god access — the command is refused otherwise.
- **Client use:**
  - The client's **Sysop god lives** power auto-sends `sys god <own-name> add life` the moment it observes the character's own death, to recover the life just spent (one send per death).

### `sys goto <location>` — teleport to a named location
*Status: CONFIRMED 2026-09-08 (user)*

- **The keyword is sent verbatim; the game resolves the name.** `sys goto newhaven` sends exactly that — the client never sends the map/room.
- **Hostiles merely PRESENT in the room do NOT block it.** You can `sys goto` out of a room full of hostile monsters. **Only ACTIVE combat blocks** — i.e. once an attack has been announced against a target. When actively engaged, you must send **`break`** first to stop combat, *then* `sys goto`.
- **No confirmation, no messages on success** — a successful `sys goto` produces **only a statline redisplay**, no room display, no "you teleport" line. To learn the room you landed in you must send a **bare Enter** to force the room display.
- **Works at any HP** — see *Sysop power gating*: `sys goto` is honoured while mortally wounded.
- **[NEEDS CONFIRMATION]** **Denied / break-then-goto wording is not pinned down** — the exact wording of a *denied* `sys goto` (no power, or the game rejecting an unknown keyword) and of the `break`-then-goto success path. Nothing depends on it: the client gates on its own per-BBS power flag + the location table, and the landing resync is name-match-or-timeout, not a string match on any reply.
- **Client use:**
  - Modeled as a third per-BBS **Sysop goto** checkbox backed by an editable location table (keyword → map/room + optional min-level), stored per-character-per-BBS.
  - The stored map/room is only for the client's own landing resync + a human-readable "resolves to" preview; the optional min-level is a client-side courtesy gate (the game enforces its own).
  - When actively engaged, the client refuses with a notice and auto-sends `break` so a re-run works once combat stops.
  - After a goto the client sends the bare Enter itself and arms a landing-name resync: the next room display matching the stored location's name commits the position (`RoomTracker.SetLocated`).

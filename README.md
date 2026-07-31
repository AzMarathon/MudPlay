# FujinTerm

<!-- current-version:start -->
> **Version 2.12.0**
> - Game Data → Items "Dropped By" now lists each monster's drop rate, e.g. "Prismatic Dragon(10%)"
> - Route picker shows an approximate ETA per route (steps + lair-fight time), matching the live walk status
> - Map room tooltips surface locked-door pick/bash requirements (e.g. "Door: 50 picklocks/strength")
> - Map room tooltips surface the Dwarven Mines "mine ore" gather commands
> - Map room tooltips surface paid room-command costs (gambling, healer/summon buys, passage fares, the jail bribe-guard)
> - Navigation lair highlight gains a combined heat+count mode, and the chosen mode is now saved per character
> - Lower native memory growth on long sessions: the terminal/backscroll renderer caches the bold typeface instead of reallocating it per run per frame, and on Linux/glibc malloc arenas are capped at startup to hold down the native RSS floor
> - Auto-combat no longer stalls on a monster it can't hurt: with both weapons ineffective and no attack spell castable, it moves to the next hostile or room instead of standing there getting beaten — a mana shortage is retried once MA regenerates, a true dead-end logs "cannot attack <monster>"
> - Physical-first combat now fully exhausts the weapon (forcing the alternate swap) before falling back to spells
> - Fixed a stale teleport route-preview lingering after a re-route, and a mid-walk replan dropping the "walk it, no teleport" choice
> - On "your weapon has no effect", auto-combat now force-swaps to the alternate weapon (or falls back to a spell) and retries instead of stalling
> - Killing a summon-on-death monster now rechecks the room before the walker steps on, so a fresh summon isn't dragged into the next room
> - Fixed WalkTo failing to route out of some rooms (e.g. ganghouse 15/945) whose CMD was misread as a teleport
> - Auto-collect no longer fires doomed coin `get`s at 100% encumbrance — the hard weight cap now always applies, not only when a "skip if makes …" flag is set
> - Auto-deposit no longer wedges: a bank reroute that returns without dropping wealth below the threshold now re-arms instead of looping forever
> - Equipment manager no longer auto-applies a gear set while the Auto-All kill-switch is engaged (manual "Apply Now" / "Equip All" / @equip still work)
> - No-mana classes (warriors/ninjas) no longer break combat and run when you type `exp` — the Health tab's mana/kai settings now stay inert for a character with no mana
> - Killing the last monster no longer triggers a flee: if the killing blow drops HP into flee territory but empties the room, the client stays and rests instead of running from nothing
> - Auto-search now holds the walker in a cleared room long enough to search it in place, instead of a zero-dwell loop stepping out before the search fires
> - Auto-sneak now re-establishes sneak before leaving a room it just cleared of hostiles — the fight spends your sneak, and the client no longer treats the stale state as still-sneaking
> - Old Mother Woodard (monster #545) now defaults to Friend on stock realms (she was missing from the stock overlay seed, so auto-combat treated her as an enemy); Paradigm already had her as Friend
> - Picklock doors now open and the walk continues: the live "You successfully unlocked the door." / "You open the door." wording is now recognized, so a picked door no longer strands the walker waiting to send `open`
> - Resting now starts the instant a room clears of hostiles instead of stalling several seconds — when a fresh monster interrupts a rest, the out-of-combat transition no longer sees a stale "hostile present" and re-sends `rest` immediately
> - Combat no longer walks off mid-fight: on realms whose melee prints no damage number or gets armour-deflected, the mob's per-round swings now register as combat activity, so the idle-stall watchdog stops mistaking an active fight for a stuck-gate empty room and abandoning it
> - Auto-collect no longer grabs cash on room entry before combat starts: with "collect after combat" on, a room whose "Also here" hostiles reveal a beat after the floor-cash line now holds the collect until the room is cleared, instead of firing a `get` into a fight
> - Party reconnect: when a member re-enters the realm and you haven't moved, the client now sends a carriage return to re-observe the room and auto-invites them if they're standing there — only falling back to the `@where` round-trip if they re-entered elsewhere
> - After death, the client sends a carriage return to re-observe the graveyard if the respawn room hasn't shown up on its own, so your position is re-established promptly instead of sitting "lost" until you move
>
> See the [version history](CHANGELOG.md) for the full changelog.
<!-- current-version:end -->

A modern Telnet terminal client for **MajorMUD** and other BBS door games, built in C# / .NET 10 with [Avalonia](https://avaloniaui.net/). It renders a faithful CP437 cell grid with full VT100/ANSI parsing, and layers a MegaMUD-style automation suite (combat, party, navigation, healing, and more) on top — all in modeless, dockable windows so the terminal stays live while you configure anything.

Linux is the primary platform; Windows and macOS are supported through Avalonia.

## Features

- **Faithful terminal** — Telnet (RFC 854/855 with NAWS + TERM-TYPE), an explicit VT100/ANSI escape-sequence parser, and a CP437 cell grid rendered by a custom Avalonia control. No host TTY dependency.
- **Combat automation** — attack rotations, target ordering, backstab handling, area/debuff spells, and per-room monster gating.
- **Party play** — party tracking, remote `@`-commands over chat channels, leader-aware wait/invite logic, and coordinated healing/blessing.
- **Navigation** — a room-graph map with go-to routing, repeatable movement loops, Auto-Lair hunting, and trap handling.
- **Healing & spells** — HP/mana thresholds, rest management, cures, buffs, and mana-regen roll-spell rerolling.
- **Character Workshop** — a unified hub for stats, equipment sets with auto-equip triggers, CP allocation plans, and quest tracking.
- **Scripting** — macros, pattern triggers, and scheduled/lifecycle events.
- **Game data** — import MajorMUD `.MDB` databases to JSON, then browse and override records (monsters, items, spells, rooms, shops, and more).
- **Layered settings** — a 4-tier hierarchy (installed defaults → all characters → per-BBS → per-character) where each tier stores only its deltas.
- **Quality of life** — session statistics, scrollback + a searchable backscroll window, a conversation/chat pane, a configurable toolbar and statline, and a built-in bug reporter (see below).

## Getting started

### Requirements

- The [.NET 10 SDK](https://dotnet.microsoft.com/) (the exact version is pinned in `global.json`).

### Build & run

```bash
git clone https://github.com/Tehshortbus/FujinTerm.git
cd FujinTerm
dotnet build      # compile check
dotnet run        # launch
```

If local state ever gets weird, `dotnet clean` and rebuild.

### First connection

1. Launch the app and create a character profile (auth + which BBS to connect to).
2. Set the BBS host/port and connect.
3. For the full automation suite, open **Game Data** and import a MajorMUD `.MDB` database — this populates the monster/item/spell/room tables the engines read from. The terminal itself works without it.

### Where your data lives

Everything is stored under a single `Data/` root, resolved per platform:

- **Linux** — `~/.local/share/FujinTerm/Data/`
- **Windows** — `%AppData%\FujinTerm\Data\`
- **macOS** — `~/Library/Application Support/FujinTerm/Data/`

Profiles, per-BBS settings, global settings, imported game data, and logs each live in their own subfolder. Settings files store only deltas from the tier beneath them, so they stay small and easy to back up.

## Reporting a bug

FujinTerm has a **built-in bug reporter** that snapshots the client's state at the moment of the problem — far more useful than describing it from memory. Please use it when filing an issue:

1. **Capture** — click the **Bug Report** button in the menu bar (or right-click the terminal → **Bug report…**). Type a short description of what went wrong and confirm.
2. FujinTerm writes a Markdown report to your **Desktop**, named `<realm>-<timestamp>.md`. It contains your player/inventory state, movement-engine status, relevant settings, the program log, and recent scrollback — with time-sensitive data frozen at click time.
3. **File the issue** — open a new issue at **https://github.com/Tehshortbus/FujinTerm/issues/new**, describe the problem, and **attach the generated `.md` file**.

The more of that capture you include, the faster a fix lands. Review the file before attaching if you'd like to redact anything.

## Contributing

- The build is **zero-warning** (`TreatWarningsAsErrors` + `EnforceCodeStyleInBuild`) and XAML bindings are compile-checked — a clean `dotnet build` is the baseline.
- `dotnet test` runs the xUnit suite (parsers, structural invariants, and critical decision logic).
- Coding conventions, architecture rules, and the per-change Definition of Done live in [`CLAUDE.md`](CLAUDE.md).

## License

MIT — see [`LICENSE`](LICENSE).

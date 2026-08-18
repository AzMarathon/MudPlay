# MudPlay

<!-- current-version:start -->
> **Version 3.21.7**
> - Recovery: auto-rest no longer gets stuck off after a fight ends in an empty room — a post-combat "wait for the room to re-confirm" hold could sit forever when the room stays empty and you don't move, leaving you below your rest threshold yet never resting
> - Combat: a room/AoE attack spell now drops to single-target the same round the room thins below its minimum-enemy count, even when a lone survivor keeps the fight going (previously it kept re-casting the AoE at the last mob until a `*Combat Off*` or an Enter press)
> - Navigation: Auto-Search no longer stalls the walker with a per-room pause when nothing is set to collect what it reveals (Auto-Get Items / Auto-Get Cash off, no path-item hunt), so travelling with Auto-Search on is markedly faster
> - Combat: a capped attack spell no longer fires an extra round against a lone monster — the engine's cap-switch to the alternate was landing one round late when a solo fight's first round arrived quickly, so e.g. LBOL set to cast **1** fired twice before switching to MMIS
> - Combat: a hand-cast attack/drain spell that draws "no effect" (e.g. probing an immune elemental with `dtch`) no longer marks the engine's **own** last auto-cast spell immune — a manual probe used to wrongly drop the auto-attack cascade to melee instead of trying the next attack spell
> - Combat: a hand-cast enemy debuff (a monster-targeting spell such as `vuln`) no longer arms a phantom self-buff recast timer or shows up as a bogus self-buff in the Buff Watchdog
> - Game Data: an item's "bought / sold" list now shows **every** room a shop operates from (a shop that runs from several rooms previously surfaced only its first)
>
> See the [version history](CHANGELOG.md) for the full changelog.
<!-- current-version:end -->

A modern Telnet terminal client for **MajorMUD** and other BBS door games, built in C# / .NET 10 with [Avalonia](https://avaloniaui.net/). It renders a faithful CP437 cell grid with full VT100/ANSI parsing, and layers a MegaMUD-style automation suite (combat, party, navigation, healing, and more) on top — all in modeless, dockable windows so the terminal stays live while you configure anything.

Linux is the primary platform; Windows and macOS are supported through Avalonia.

## Features

- **Faithful terminal** — Telnet (RFC 854/855 with NAWS + TERM-TYPE), an explicit VT100/ANSI escape-sequence parser, and a CP437 cell grid rendered by a custom Avalonia control that scales crisply to fill the window. No host TTY dependency.
- **Combat automation** — attack/spell primary and alternate settings, target ordering/priority, backstab handling, area/single target debuff spells with an immunity-aware fallback cascade, and per-monster attack/priority overrides.
- **Party play** — party tracking, coordinated healing/blessing, leader-aware wait/invite logic, and remote `@`-commands over chat channels: @health, @level, @version, @comeback, @share and more.
- **Navigation** — a room-graph map with go-to routing via saved goto locations, search for destination or right click menu on map, looping, new Auto-Lair mode, trap handling, stash rooms, storable favorite loops, auto-lairs and goto's in right click menu. auto-mode toggles and fully configurable keybinds and toolbar. Map overlays!
- **Healing & spells** — HP/mana thresholds, rest management, cures, buffs, and mana-regen roll-spell rerolling.
- **Character Workshop** — a unified hub for character management and development. live stats, equipment sets with auto-equip triggers, an **Item Finder** with trial gearsets for what-if stat/encumbrance comparisons, CP allocation plans, quest tracking, boss timer tracking, various calculators.
- **automation tools** — macros, aliases, triggers, and events.
- **Game data** — import MajorMUD `.MDB` databases, all engines read from game data and you can then browse many significant aspects of game data in the Game Data Browser.
- **Quality of life** — session statistics, timestamped full ansi scrollback + search filter, a conversation/chat pane, type-through so keystrokes keep reaching the terminal while other windows are open unless a textblock is focused on another window and a ***built-in bug reporter (USE THIS WHEN REPORTING ISSUES IT WILL SHOW ME A LOT MORE THAN YOU CAN DESCRIBE OR SHOW VIA PICTURES)***.

## Getting started

### Requirements

- The [.NET 10 SDK](https://dotnet.microsoft.com/) (the exact version is pinned in `global.json`).

### Build & run

```bash
git clone https://github.com/Tehshortbus/MudPlay.git
cd MudPlay
dotnet build      # compile check
dotnet run        # launch
```

If local state ever gets weird, `dotnet clean` and rebuild.

### First connection

1. Launch the app and create a character profile (auth + which BBS to connect to).
2. Set the BBS host/port and connect.
3. For the full automation suite, open **Game Data** and import a MajorMUD `.MDB` database — this populates the monster/item/spell/room tables the engines read from. The terminal itself works without it.

### Where your data lives

Everything is stored under a single app-data folder, resolved per platform:

- **Linux** — `~/.local/share/MudPlay/`
- **Windows** — `%AppData%\MudPlay\`
- **macOS** — `~/Library/Application Support/MudPlay/`

Profiles, per-BBS settings, global settings, imported game data, and logs each live in their own subfolder. Settings files store only deltas from the tier beneath them, so they stay small and easy to back up. (Updating from an older build automatically lifts your data out of the previous nested `Data/` subfolder on first launch.)

## Reporting a bug

MudPlay has a **built-in bug reporter** that snapshots the client's state at the moment of the problem — far more useful than describing it from memory. Please use it when filing an issue:

1. **Capture** — click the **Bug Report** button in the menu bar (or right-click the terminal → **Bug report…**). Type a short description of what went wrong and confirm.
2. MudPlay writes a Markdown report to your **Desktop**, named `<realm>-<timestamp>.md`. It contains your player/inventory state, movement-engine status, relevant settings, the program log, and recent scrollback — with time-sensitive data frozen at click time.
3. **File the issue** — open a new issue at **https://github.com/Tehshortbus/MudPlay/issues/new**, describe the problem, and **attach the generated `.md` file**.

The bug report includes almost all of the info needed to isolate the problem but a good description helps me target it faster. You can review the bug report before submitting if you wish but please leave as much context in the report as possible. The bug report does include all your settings, your character name, stats, inventory, client info, the program log and ~750 lines of backscroll.  ***It DOES NOT include your BBS login name or password or your login menu navigation settings.***

## Contributing

- The build is **zero-warning** (`TreatWarningsAsErrors` + `EnforceCodeStyleInBuild`) and XAML bindings are compile-checked — a clean `dotnet build` is the baseline.
- `dotnet test` runs the xUnit suite (parsers, structural invariants, and critical decision logic).
- Coding conventions, architecture rules, and the per-change Definition of Done live in [`CLAUDE.md`](CLAUDE.md).

## License

MudPlay is licensed under the **MIT License** — see [`LICENSE`](LICENSE).

It bundles third-party components under their own licenses. The full text of each is viewable in-app under **Help → About**:

| Component | License |
|---|---|
| [Avalonia](https://avaloniaui.net/) | MIT |
| [JetDatabaseReader](https://github.com/diegoripera/JetDatabaseReader) | MIT |
| [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) font | SIL Open Font License 1.1 |
| [IBM Plex Sans](https://github.com/IBM/plex) font | SIL Open Font License 1.1 |
| [Px437 / Mx437 (Oldschool PC Fonts)](https://int10h.org/oldschool-pc-fonts/) | CC BY-SA 4.0 |

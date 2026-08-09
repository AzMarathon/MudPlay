# FujinTerm

<!-- current-version:start -->
> **Version 2.38.0**
> - Party stats probe: the first time you party with a player each day, the client asks them `@level` and `@version` and records their exact level + client version onto their player record (Game Data → Players); `@health` still fires on every join for live vitals. New Party setting (on by default)
> - `@version` replies (e.g. `{FujinTerm 2.38.0}`, `{MegaMud 1.03u}`) are captured per player; the player edit dialog shows Version + Last partied
> - Level display reconciles exact vs. title: a recorded exact wins, unless the player's title band has climbed above it (they trained since we last asked) — then the title range shows until we re-learn an exact at or above the band's floor
> - Route level-gating treats a recorded level as fresh for the current day (was 24h) and re-learns it via the daily probe or any manual `@level`; the old leader-only roster-change poll is retired
> - Peer `@level` replies from other FujinTerm clients (brace-wrapped) now record correctly
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

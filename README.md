# MudPlay

<!-- current-version:start -->
> **Version 3.2.0**
> - New Help → About window: program name + version, a clickable link to the repo, a tab per bundled license (MudPlay, Avalonia, JetDatabaseReader, and the bundled fonts), and a thank-you to the MajorMUD community and its tools (MegaMUD, Nightmare Redux, MajorMUD Explorer)
> - New animated mud-throw startup splash on the terminal (toggle in Settings → General); the title/byline stay either way, and it never touches the backscroll
> - Updating from a 2.x build now migrates your existing data (profiles, BBS, settings, game data) from the old FujinTerm folder into MudPlay — fixes profiles appearing missing after the 3.0 rename
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

Everything is stored under a single `Data/` root, resolved per platform:

- **Linux** — `~/.local/share/MudPlay/Data/`
- **Windows** — `%AppData%\MudPlay\Data\`
- **macOS** — `~/Library/Application Support/MudPlay/Data/`

Profiles, per-BBS settings, global settings, imported game data, and logs each live in their own subfolder. Settings files store only deltas from the tier beneath them, so they stay small and easy to back up.

## Reporting a bug

MudPlay has a **built-in bug reporter** that snapshots the client's state at the moment of the problem — far more useful than describing it from memory. Please use it when filing an issue:

1. **Capture** — click the **Bug Report** button in the menu bar (or right-click the terminal → **Bug report…**). Type a short description of what went wrong and confirm.
2. MudPlay writes a Markdown report to your **Desktop**, named `<realm>-<timestamp>.md`. It contains your player/inventory state, movement-engine status, relevant settings, the program log, and recent scrollback — with time-sensitive data frozen at click time.
3. **File the issue** — open a new issue at **https://github.com/Tehshortbus/MudPlay/issues/new**, describe the problem, and **attach the generated `.md` file**.

The more of that capture you include, the faster a fix lands. Review the file before attaching if you'd like to redact anything but absolutely no login information can or will be shared, as your username and password are Sha-256 encrypted when stored on disk.

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
| Px437 / Mx437 (Oldschool PC Fonts) | CC BY-SA 4.0 |

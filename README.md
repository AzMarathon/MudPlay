# MudPlay

<!-- current-version:start -->
> **Version 3.104.0**
> - Triggers: numbered wildcards `{1}`, `{2}`… now work in Literal patterns and responses (were silently treated as literal braces); any letters/digits/underscores is a valid wildcard name
> - Triggers: new **Wildcards** button on the Triggers table opens a live viewer of the captured wildcards and their current values (trigger-only store, kept separate from alias/macro placeholders)
> - `@status` / `@path` now report the **specific reason** the navigator is paused — "resting (low HP) en route to X", "meditating (low mana) on loop 'X'", "held", "party asked to wait", or a manual "paused" — instead of always saying "walking"
> - Combat switches to the **room (multi-target) attack** the moment a mob arrives mid-fight and the count crosses your MinEnemies, rather than staying single-target until the next damage tick
> - Combat: a between-round area-debuff drawing "already cast this round" no longer strands the round's combat attack — the independent attack slot fires regardless
> - Combat: the room attack spell is no longer recast while it's already channeling (a kill or a roamer kept re-sending it, which the game breaks-and-restarts) — it persists until the count drops below MinEnemies, MaxCastsPerRoom is hit, or mana falls under the AoE floor
> - Combat: re-attacks promptly after a second party heal — two heals a beat apart used to leave the second's re-attack paced out for a full round (a lost round + the mob's exp)
>
> See the [version history](CHANGELOG.md) for the full changelog.
<!-- current-version:end -->

A modern Telnet terminal client for **MajorMUD** and other BBS door games, built in C# / .NET 10 with [Avalonia](https://avaloniaui.net/). It renders a faithful CP437 cell grid with full VT100/ANSI parsing, and layers a MegaMUD-style automation suite (combat, party, navigation, healing, and more) on top — all in modeless, dockable windows so the terminal stays live while you configure anything.

Linux is the primary platform; Windows and macOS are supported through Avalonia.

## Features

- **Faithful terminal** — Telnet (NAWS + TERM-TYPE), explicit VT100/ANSI parsing, and a CP437 cell grid rendered by a custom Avalonia control that scales crisply. No host TTY dependency.
- **Profiles & settings** — per-character profiles over a 4-tier hierarchy (defaults → all characters → BBS → character, deltas only), multiple BBSes with their own accounts, automated logon, and configurable redial/reconnect.
- **Combat** — attack/spell ordering and priority, backstab, single- and area-target debuffs with an immunity-aware fallback cascade, crowd and rest-aware handling, and per-monster overrides.
- **Healing & spells** — HP/mana thresholds, rest management, cures, buffs, mana-regen rerolls, a class-aware **Spell Book** (cast-success odds + damage calculator), and a **Buff Watchdog** with live recast timers.
- **Navigation** — a room-graph map with go-to routing over saved GOTOs, runnable **loops** with exp/hour estimates, an **Auto-Lair** mode, trap / hazard / teleport route pickers, a level-gate overlay, and stash rooms.
- **Party play** — tracking, coordinated healing/blessing, leader-aware wait/invite, reconnect handling, and remote `@`-commands over chat (query, move-me, act-for-me, coordinate) — each gated by per-player permissions.
- **Cash & items** — automated loot / sell / buy / stash / discard, banking, and equipment sets with auto-equip triggers.
- **Character Workshop** — live stats, **Equipment Manager** + an **Item Finder** for what-if gear comparisons, **CP-allocation** plans, level projection, quest / boss / death tracking (boss timers syncable between clients), calculators, and **Roomba** gang-house item sorting with an in-game `@roomba` location log.
- **Game Data** — import MajorMUD `.MDB` sets and browse or override every record across the 4 tiers; filter-rich **Monsters / Items / Players** tables with **batch edit** to set many records at once; and a **Monster Intel** window that answers "can I safely fight this now?" (Hits-You-% vs your live AC, rounds-to-kill, and your combat history).
- **Automation tools** — macros, aliases, triggers, and events; per-engine toggles with a one-press all-off kill switch; and a Sprint mode.
- **Conversation & chat** — a dedicated pane with per-channel filtering, search, logging, and history.
- **Tools & diagnostics** — full-ANSI scrollback (search/filter), a **Program Log**, **Session Stats**, a **Wire Inspector**, and a ***built-in bug reporter — use it when reporting issues; it captures far more than a screenshot***.
- **Quality of life** — editable toolbar, rebindable keys, a customizable terminal right-click menu, edge-snapping windows that move as a cluster, font/nav styling, output scaling, and type-through so keystrokes keep reaching the terminal.

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

1. **Add a board.** File → **Profile Management** → BBSes → **Add**. Drops you into its settings.
2. **Fill it in.** Host, port, your username + password. If the board needs it, add the **logon steps** — a message to wait for, the reply to send — that walk you from the BBS menu into the game.
3. **Add a character** under that board (Profile Management → Characters → **Add**).
4. **Connect.** **Alt+H**, or File → Connect. You're in.
5. **Want the automation?** Open **Game Data** → **Import .mdb** and pick a MajorMUD database. That fills the monster/item/spell/room tables the engines read from. The terminal works fine without it — the robots don't.

### Where your data lives

Everything is stored under a single app-data folder, resolved per platform:

- **Linux** — `~/.local/share/MudPlay/`
- **Windows** — `%AppData%\MudPlay\`
- **macOS** — `~/Library/Application Support/MudPlay/`

Profiles, per-BBS settings, global settings, imported game data, and logs each live in their own subfolder. Settings files store only deltas from the tier beneath them, so they stay small and easy to back up.

## Reporting a bug

MudPlay has a **built-in bug reporter** that snapshots the client's state at the moment of the problem — far more useful than describing it from memory. Please use it when filing an issue:

1. **Capture** — click **Bug Report** in the menu bar (or right-click the terminal → **Bug report…**), type a short description, and confirm.
2. MudPlay writes `<realm>-<timestamp>.md` to your **Desktop**: your settings, character name/stats/inventory, movement-engine state, the program log, and ~750 lines of scrollback — all frozen at click time.
3. **File the issue** at **https://github.com/Tehshortbus/MudPlay/issues/new**, and **attach the `.md` file**.

A short description still helps me target it faster, and you can review the report before sending. ***It does NOT include your BBS login name, password, or logon-menu steps.***

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

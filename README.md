# MudPlay

<!-- current-version:start -->
> **Version 3.105.0**
> - Auto-train party — a new CP Allocation checkbox that makes auto-train work in a group: members tell the leader when their own auto-train settings say they're ready, and the leader takes the party to train together; needs two or more members on MudPlay with it on (a lone leader uses its solo Auto-train and re-invites the party), and keeps telepaths to one ask per member join plus their own change reports
> - The leader goes once a set number of party members (it counts as one) is ready; the rest stay banked and follow, and a power-leveler well above the party is never waited for
> - Party trips visit the trainer serving the most members first, chain across level bands, and at the leader's stop everyone trains at once, then the party is re-formed once and the loop resumes
> - Short members are covered by party members' spare coin, or withdraw their own fee at a bank stop on the way
> - New Settings → Auto-Trainer Party options: members needed ready, level gap, and leave the level-11 train to a solo trip
> - Party window shows each member's level before their class, and — leading with Auto-train party — their exp, time to level at your exp/hour and train readiness under their bars
> - MegaMUD party members' @level replies are now read — their level was never recorded — and, leading with Auto-train party, their exp to next level and time at your rate show under their bars
> - Auto-login presses Enter at a bulletin pager's (N)onstop, (Q)uit, or (C)ontinue? prompt whenever it appears — no menu-nav step needed
> - A member's MudPlay version is recorded from any {MudPlay …} / {MegaMud …} reply, and a stale older-MudPlay record no longer writes them off as another client
> - Telepaths are paced 100 ms apart, and any the server refuses (--- Telepath Not Sent ---) are resent, so a party-join burst no longer loses probes
> - Party window: Level 1 - Druid under each name in a larger font; your own exp updates on every kill, and members' exp is estimated from your gains between their reports
> - A reporting member still waiting 10 minutes past its projected ready time gets one "ready yet?" ask
> - @level / @exp replies over telepath or directed say (.@level) update the member's line
> - Combat no longer keeps swinging at a monster a party member killed: an attack the server reads back as You say "…" (talk-slow off) drops the target and re-looks the room
> - A member's `I can now train to level: N` announcement shows as `can train LN` on their Party-window line — display only, for members that won't be auto-trained
> - Following with Auto-train party on, the Party window shows the train lines too — your own status, and the others' from their `@level` / `@exp` replies at your exp/hour
> - A member shut out of a combat-restricted trainer room walks in and trains once its fight ends, instead of reporting "done 0" where it stood; the leader re-invites everyone who set out
> - The `@join` nag isn't cancelled by a member's automatic `@where`, and a member left in a quiet `[Invited]` slot is re-invited and nagged when seen
> - A hand `train stats` with nothing in the CP plan to apply (e.g. before the `train` that earns the level's CP) says so in the Program log
> - A CP plan applied from your own `train stats` clears that level's row from the CP Allocation tab, as Train Now does
> - TNL counts down like a timer on the status bar, Session Stats and the Party window — one shared clock, so all three match — resetting only when the estimate really changes
> - TNL under 10 minutes shows minutes and seconds (`4m 12s`), under a minute just seconds
> - One `invite` per returning member: the realm re-entry re-invite, invite-if-seen and the reform no longer each send their own
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

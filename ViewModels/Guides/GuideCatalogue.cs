using A = MudPlay.ViewModels.Guides.GuideActions;

namespace MudPlay.ViewModels.Guides;

// Every Visual Guide, in the order Help → Visual Guides lists them (grouped by
// Area). A guide is data: steps of checklist lines, each worded from the real UI
// labels. Do lines tick on their GuideActions key; Note lines explain and move on
// with Next. TargetName rings a named control on the main window. Adding a guide
// is adding an entry here — the picker and the step card need nothing new.
internal static class GuideCatalogue
{
    public const string SetupId = "setup";

    private const string GettingStarted = "Getting started";
    private const string Navigation = "Navigation";
    private const string Automation = "Automation";
    private const string Character = "Character & game data";
    private const string Settings = "Settings & help";

    private static GuideLine Do(string text, string key) => new(text, key);
    private static GuideLine Note(string text) => new(text);
    private static GuideStep Step(string title, string? target, params GuideLine[] lines) => new(title, lines, target);

    private static GuideStep OpenNavigation() =>
        Step("Open Navigation", "ViewMenu", Do("View → Navigation", A.NavigationOpened));

    private static GuideStep OpenWorkshop() =>
        Step("Open the Player Workshop", "ViewMenu", Do("View → Player Workshop", A.WorkshopOpened));

    public static IReadOnlyList<GuideDefinition> Build(GuideProbes p) => new[]
    {
        Setup(p),

        // ----- Navigation -------------------------------------------------
        new GuideDefinition("loop", Navigation, "Build, save and run a loop",
            "Click a circuit of rooms on the map, save it as a loop, and run it.", new[]
            {
                OpenNavigation(),
                Step("Build the loop", null,
                    Note("Click the Loop mode chip above the map — it reads Building"),
                    Do("Left-click each room of the circuit on the map, in order", A.LoopRoomClicked),
                    Note("Alt+click takes a room back out; the list's ↑ ↓ ✕ tidy it")),
                Step("Save it", null,
                    Note("Click Save on the chip row — Create Loop opens"),
                    Note("Give it a Name (it starts as Loop and the time)"),
                    Do("Click Save", A.LoopSaved)),
                Step("Run it", null,
                    Note("Saved loops are listed under LOOPS + AUTO-LAIRS on the left"),
                    Do("Click Go on the loop's row", A.LoopStarted),
                    Note("Not standing on the loop? MudPlay walks you to it first")),
                Step("Pause or stop", null,
                    Note("While the loop runs, the Go chip reads Pause"),
                    Note("The Loop chip reads Stop — click it to end the loop")),
            }),
        new GuideDefinition("autolair", Navigation, "Run Auto-Lair",
            "Mark lair rooms on the map and let Auto-Lair circle them.", new[]
            {
                OpenNavigation(),
                Step("Mark the lairs", null,
                    Note("Click the Lair mode chip above the map"),
                    Do("Left-click each lair room to mark it — at least two", A.LairMarked),
                    Note("Marked lairs list under CURRENT NAV: ✎ sets a lair's own timer, ✕ unmarks it")),
                Step("Save the setup (optional)", null,
                    Note("Click Save on the chip row — Create Auto-Lair Setup opens"),
                    Note("★ Set as favorite puts it in the terminal's right-click Favorites"),
                    Do("Name it, then click Save", A.LairSetupSaved)),
                Step("Start it", null,
                    Do("Click Go on the chip row, or Go on the setup's row", A.AutoLairStarted),
                    Note("It needs two marked lairs and a known room; the Program Log says why if it won't start")),
                Step("Pause or stop", null,
                    Note("While it runs, the Go chip reads Pause"),
                    Note("The Lair chip reads Stop — click it to end the run")),
            }),
        new GuideDefinition("walkto", Navigation, "Walk to a room",
            "Pick a destination and let MudPlay walk the route.", new[]
            {
                OpenNavigation(),
                Step("Pick where to go", null,
                    Do("Type in Search rooms, lairs, loops… and click the room", A.DestinationQueued),
                    Note("Or Ctrl+click a room on the map, or click a Go To in the GOTO rail")),
                Step("Walk", null,
                    Do("Click Go on the chip row", A.WalkStarted),
                    Note("More than one way there? Choose a route asks which to take")),
                Step("Quicker ways", null,
                    Note("Right-click a room on the map → Walk here walks at once"),
                    Note("Right-click a room → Save as Go To keeps it in the GOTO rail"),
                    Note("The terminal's right-click Favorites and Recent destinations walk there too")),
            }),
        new GuideDefinition("loopsettings", Navigation, "Loop settings",
            "Whole-loop options, and per-room commands, delays, rest and attack rules.", new[]
            {
                OpenNavigation(),
                Step("Whole-loop settings", null,
                    Note("Build or run a loop, then click ⚙ Entire Loop Settings ⚙ above its room list"),
                    Note("Only attack in lair rooms — walk through the rest"),
                    Note("Wait to enter lairs until I can debuff — then pick Wait for spells, Block spells or Wait for priority buff spells"),
                    Note("A saved loop has the same boxes in Edit Loop (right-click it on the rail → Edit…)")),
                Step("One room's settings", null,
                    Note("Click a room's row in the loop's list — Waypoint action opens"),
                    Note("Command: what to send on arrival — chain with ; or ^M"),
                    Note("Delay: minutes (m) and seconds (s) to wait; 0 moves on once the command is answered"),
                    Note("Do not rest in this room · Do not attack in this room · Rest up here (HP) · Rest up here (mana)"),
                    Do("Click Save", A.WaypointActionSaved)),
                Step("Where they're kept", null,
                    Note("While building, room settings are saved with the loop"),
                    Note("On a running saved loop they apply at once and save to the loop"),
                    Note("Edit Loop's table has them as columns: Command, Delay (min / s), No rest, No atk, Rest HP, Rest MA")),
            }),
        new GuideDefinition("mpimport", Navigation, "Import MegaMUD loops",
            "Turn a MegaMUD .mp loop file into a MudPlay loop.", new[]
            {
                Step("Pick the file", "GameDataMenu",
                    Do("Game Data → Import loops (MegaMUD .mp)…", A.MpImportStarted),
                    Note("Or Navigation Management → Loops & Auto-Lairs → Import .mp"),
                    Note("Choose the .mp file — the import review window opens")),
                Step("Review the steps", null,
                    Note("Left: what the file says. Right: the MudPlay loop it becomes"),
                    Note("Each line pairs a MegaMUD step with its MudPlay room; ✗ marks one that couldn't be followed"),
                    Note("Type a map/room into Set room to fix a step; ✕ leaves one out, + adds one"),
                    Note("Verify loop in MudPlay checks the result walks; Display on map draws both")),
                Step("Accept", null,
                    Note("Check the Loop name"),
                    Do("Click Accept to save the loop (Reject drops it)", A.MpImportAccepted)),
            }),

        // ----- Automation -------------------------------------------------
        new GuideDefinition("macros", Automation, "Macros",
            "Bind a key to commands you send often.", new[]
            {
                Step("Open Macros", "ViewMenu",
                    Do("View → Macros — the Game Data Browser opens on Macros", A.MacrosShown)),
                Step("Add a macro", null,
                    Note("Click Add"),
                    Note("Click Capture, then press the key or key chord"),
                    Note("Type the Command — split steps with ^M or ;"),
                    Do("Click Save", A.MacroAdded)),
                Step("Use it", null,
                    Note("Press the key in the terminal to send the command"),
                    Note("Double-click a macro to change it; Remove deletes the selected one"),
                    Note("Macros belong to the loaded character")),
            }),
        new GuideDefinition("aliases", Automation, "Aliases",
            "Short words that expand into longer commands.", new[]
            {
                Step("Open Aliases", "ViewMenu",
                    Do("View → Aliases — the Game Data Browser opens on Aliases", A.AliasesShown)),
                Step("Add an alias", null,
                    Note("Click Add"),
                    Note("Name: the word you'll type, no spaces (e.g. ws)"),
                    Note("Expansion: what it sends instead"),
                    Do("Click Save", A.AliasAdded)),
                Step("Use it", null,
                    Note("Aliases fire from the Conversation window's input line"),
                    Note("Double-click an alias to change it; Remove deletes it")),
            }),
        new GuideDefinition("triggers", Automation, "Triggers",
            "Answer a line from the game automatically.", new[]
            {
                Step("Open Triggers", "ViewMenu",
                    Do("View → Triggers — the Game Data Browser opens on Triggers", A.TriggersShown)),
                Step("Add a trigger", null,
                    Note("Click Add, and give it a Name"),
                    Note("Scope: Game messages, a chat channel, or System log"),
                    Note("Match type Literal or Regex, then the Pattern to watch for"),
                    Note("Response: what to send when it matches; a Sound is under Optional"),
                    Do("Click Save", A.TriggerAdded)),
                Step("Patterns", null,
                    Note("Help → Help topics → Writing a match pattern covers wildcards and captures"),
                    Note("Untick Enabled to park a trigger without deleting it")),
            }),
        new GuideDefinition("events", Automation, "Scheduled events",
            "Do something at logon, at a time, every so often, or when a condition holds.", new[]
            {
                Step("Open Events", "ViewMenu",
                    Do("View → Events — Settings opens on Events", A.EventsShown)),
                Step("Add an event", null,
                    Note("Click New… — New Event opens"),
                    Note("WHEN: Logon, Logoff, Re-log, At time, Every, Boss, or When with conditions"),
                    Note("DO: Walk to, Start loop, Auto-lair, Command, and more"),
                    Note("STOP AFTER and THEN decide how a run ends"),
                    Do("Click Save", A.EventAdded)),
                Step("Manage them", null,
                    Note("Events save straight away — no need for OK"),
                    Note("Modify… and Remove act on the selected event; Disable all events pauses them all")),
            }),

        // ----- Character & game data ------------------------------------
        new GuideDefinition("monsterintel", Character, "Monster Intel",
            "Check whether you can safely fight a monster right now.", new[]
            {
                Step("Open Monster Intel", "ViewMenu",
                    Do("View → Monster Intel", A.MonsterIntelOpened)),
                Step("Find the monster", null,
                    Do("Type in Filter by name…", A.MonsterFiltered),
                    Note("Hide regen timers and Hide 0 exp trim the list"),
                    Do("Click a monster", A.MonsterSelected)),
                Step("Read the matchup", null,
                    Note("Hits You % and Est. Rounds to Kill in the list are the quick answer"),
                    Note("Your Matchup: incoming threat, then your physical and spell attacks"),
                    Note("Abilities & resistances, Your Observations and Attacks fill in the rest")),
                Step("Ask what-if", null,
                    Note("The character bar's Defense (what-if) row: change AC, Shadow AC or the wards"),
                    Note("Hits You % recomputes at once — nothing changes in the game")),
            }),
        new GuideDefinition("workshop", Character, "Player Workshop tour",
            "What each Player Workshop tab is for.", new[]
            {
                OpenWorkshop(),
                Step("Your character", null,
                    Note("Character Info: the live stat sheet and combat numbers"),
                    Note("Death Recovery: your deathpile and getting it back"),
                    Note("Auto-Train: CP Allocation plans your points; Level Projection shows each level's growth")),
                Step("Gear and quests", null,
                    Note("Quest Status: your quest checklist and its bonuses"),
                    Note("My Equipment: Equipment Manager holds your gear sets; Item Finder searches every item"),
                    Note("Calculators: combat what-if tools")),
                Step("Record Keeping", null,
                    Note("Bosses: the boss list and respawn timers"),
                    Note("Chest Offload: chest loot grouped by shop"),
                    Note("Roomba: gang-house room sort rules"),
                    Note("Realm Rankings: the realm's top experience list")),
            }),
        new GuideDefinition("itemfinder", Character, "Item Finder",
            "Search every equippable item and plan a loadout.", new[]
            {
                OpenWorkshop(),
                Step("Open Item Finder", null,
                    Do("Click the My Equipment tab, then Item Finder", A.ItemFinderShown)),
                Step("Search", null,
                    Note("Left panel: Character, Slot & type and Filter by stats; Reset filters clears them"),
                    Note("Filter by name… narrows the list; Showing … of … items counts it"),
                    Note("Click a column header to sort; double-click a row for the item's full record")),
                Step("Plan a loadout", null,
                    Note("Right-click an item → Trial-Equip this Item"),
                    Note("Gear Finder ▸ opens the planning panel"),
                    Do("Click + Add to search order, then Find Best", A.GearFinderRun),
                    Note("Projected stats shows what the trial set would give you")),
            }),
        new GuideDefinition("gamedata", Character, "Game Data Browser",
            "Look up any monster, item, spell, room or shop from your imported game data.", new[]
            {
                Step("Open the browser", "GameDataMenu",
                    Do("Game Data → Open Browser…", A.GameDataBrowserOpened)),
                Step("Pick a table", null,
                    Do("Click a table under Imported tables — Monsters, Items, Spells, Rooms…", A.BrowserSectionPicked),
                    Note("The Search… box above the list finds a table by name")),
                Step("Filter it", null,
                    Do("Type in Filter… (Enter) and press Enter", A.BrowserFiltered),
                    Note("Columns ▾ picks which columns show")),
                Step("Go further", null,
                    Note("Players, Macros, Triggers and Aliases are editors in the same list"),
                    Note("Help → Help topics → Overriding a record explains changing a record")),
            }),
        new GuideDefinition("permissions", Character, "Player permissions",
            "Choose what other players may ask your client to do with @-commands.", new[]
            {
                Step("Open Players", "ViewMenu",
                    Do("View → Players — the Game Data Browser opens on Players", A.PlayersShown)),
                Step("Pick the player", null,
                    Note("Players you've seen are listed; Add adds one by Given name"),
                    Note("Double-click a player to open them")),
                Step("Set permissions", null,
                    Note("Allowed Remote Control: tick what they may do — Query health/status, Move player, Execute commands, …"),
                    Note("All ticks every box"),
                    Do("Click Save", A.PlayerPermissionsSaved)),
                Step("How it's used", null,
                    Note("They send @-commands over chat, and your client answers or obeys"),
                    Note("Help → Help topics → Remote @-commands lists each command and its permission"),
                    Note("Batch edit sets the same permissions on several players at once")),
            }),
        new GuideDefinition("buffs", Character, "Buff Watchdog",
            "Keep your blesses and party buffs up automatically.", new[]
            {
                Step("Open the Buff Watchdog", "ViewMenu",
                    Do("View → Buff Watchdog", A.BuffWatchdogOpened)),
                Step("Add a buff", null,
                    Note("Nothing to add? Click Read my spell list (or type sp in the game)"),
                    Note("Click ＋ Add buff, pick the Buff and its Recast (s)"),
                    Do("Click OK", A.BuffAdded),
                    Note("Add all blesses adds every bless you know at once")),
                Step("Watch it work", null,
                    Note("Each row gets a timer bar while the buff is up"),
                    Note("Self and All/None pick who each buff is cast on")),
            }),
        new GuideDefinition("gear", Character, "Gear sets and combat profiles",
            "Save gear loadouts and whole combat setups, and swap between them.", new[]
            {
                OpenWorkshop(),
                Step("Gear sets", null,
                    Do("Click the My Equipment tab — Equipment Manager", A.EquipmentManagerShown),
                    Note("Sets: Default, Backstab, Pre-rest HP, Pre-rest Mana, While Moving, Bossing"),
                    Do("Pick a set and click Update from live, or choose an item per slot", A.GearSetEdited),
                    Note("Action → Equip ▸ wears a set; Equip Now wears the one shown")),
                Step("Combat profiles", "ViewMenu",
                    Do("View → Settings… → Combat", A.CombatSectionShown),
                    Note("Combat profile: + adds a profile, ✕ removes the active one, Profile name renames it"),
                    Note("A profile keeps the Combat and Health tabs' setup, and some of Spells and Party"),
                    Note("Action → Combat Profiles switches between them")),
            }),

        // ----- Settings & help --------------------------------------------
        new GuideDefinition("settings", Settings, "The Settings window",
            "Find your way around Settings: sections, search, and saving.", new[]
            {
                Step("Open Settings", "ViewMenu",
                    Do("View → Settings…", A.SettingsOpened)),
                Step("Sections", null,
                    Do("Pick a section from the list on the left", A.SettingsSectionPicked),
                    Note("Each section's banner says where it's kept: Character profile settings, BBS settings or Global client settings")),
                Step("Search", null,
                    Do("Type in Search settings…", A.SettingsSearched),
                    Note("Matching sections and labels are highlighted — nothing is hidden")),
                Step("Save or throw away", null,
                    Do("Change something, then click Apply (stays open) or OK (saves and closes)", A.SettingsApplied),
                    Note("Cancel, or the window's X, throws unsaved changes away"),
                    Note("Pressing the Settings shortcut again while it's in front saves, like OK")),
            }),
        new GuideDefinition("bugreport", Settings, "Send a bug report",
            "Capture what the client was doing so a problem can be fixed.", new[]
            {
                Step("Write the report", "BugReportMenu",
                    Note("Do it right after the problem, while it's still fresh"),
                    Note("Click Bug Report on the menu bar (or right-click the terminal → Bug report…)"),
                    Do("Describe what went wrong, then click Write report", A.BugReportWritten)),
                Step("Send it", null,
                    Note("The report file is saved on your Desktop"),
                    Note("Help → Report an issue… opens the issue page — attach the file there")),
            }),
    };

    // The first-run setup tour: only the steps still missing, then Connect.
    private static GuideDefinition Setup(GuideProbes p)
    {
        GuideLine Action(string text, string key) => new(text, key);
        return new GuideDefinition(SetupId, GettingStarted, "First-time setup",
            "Add a BBS and a character, import game data, and connect.", new[]
            {
                new GuideStep("Add a BBS", new[]
                {
                    Action("File → Profile Management", A.ProfileManagement),
                    Action("Click Add (under BBSes)", A.AddBbs),
                    Action("Enter the host/IP + port", A.BbsHostPort),
                    Action("Click OK", A.BbsSaved),
                }, "FileMenu", p.HasBbs, Include: () => !p.HasBbs()),

                // Added on the unnamed default profile, the character is loaded and
                // Profile Management closes (ProfileManagerViewModel.AddProfileAsync).
                // The step ends there, not at the add: a new player used to go on
                // playing the unnamed profile, with that window left open over the
                // main one (user, 2026-10-14).
                new GuideStep("Add a character", new[]
                {
                    Action("File → Profile Management", A.ProfileManagement),
                    Action("Click Add (under Characters)", A.AddCharacter),
                    // Ticked by the load, not the click: a step whose lines are all
                    // ticked is done, and this one is done when the character is loaded.
                    new GuideLine("Name it, then Save (it's loaded, and Profile Management closes)",
                        A.CharacterAdded, p.CharacterLoaded),
                }, "FileMenu", () => p.HasCharacter() && p.CharacterLoaded(), Include: () => !p.HasCharacter()),

                new GuideStep("Import game data", new[]
                {
                    Action("Game Data → Import .mdb", A.ImportMdb),
                    Action("Pick your MajorMUD .mdb file", A.GameDataImported),
                }, "GameDataMenu", p.HasGameData, Include: () => !p.HasGameData()),

                new GuideStep("Connect", new[]
                {
                    new GuideLine("File → Connect (Alt+H) to enter the game", Probe: p.IsConnected),
                }, "FileMenu", p.IsConnected),
            }, Resumable: false);
    }
}

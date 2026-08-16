using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.ViewModels.GameData.Edit;

namespace MudPlay.ViewModels.GameData.Tables;

// Game Data Browser → Monsters tab. Renders the imported MajorMUD Monsters table — the static
// MDB table that drives Auto-Lair respawn timers (via RegenTime), CombatManager's per-monster
// behaviour gating, and the Workshop COMBAT preview's damage projection.
//
// Column names mirror the MajorMUD MDB schema verbatim (per data-v1.11p.mdb). EXP is the
// experience reward, MagicRes is the magic-resist score, AvgDmg is the average per-round
// outgoing damage, RegenTime is respawn cadence in ticks. Type and Align render via
// LookupEnums ("Solo" / "Lawful Good" / etc.). Undead is a byte-boolean from the MDB
// (0 = no, non-zero = yes — the MDB stores Boolean True as -1, which arrives as 255).
public sealed class MonstersSectionViewModel : JsonTableSectionViewModel, IEditableTableSectionViewModel
{
    private readonly GameDataCache _cache;
    private readonly DialogService? _dialogs;
    private readonly SettingsResolver? _resolverRef;
    private readonly MonsterOverlaySeedStore? _overlaySeed;
    private readonly RoomGraphManager? _roomGraph;

    public override string Id => "monsters";
    public override string Title => "Monsters";

    protected override string TableName => "Monsters";

    // The monster table's columns, in display order. Several are synthesised in
    // ComputeRowCells (AcDr, Dodge, Mag, Damage, Efficiency, Accuracy, EXP, Lairs)
    // rather than being raw MDB fields — see there for how each is derived.
    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Number",
        "Name",
        "RegenTime",     // "Rgn" — respawn timer
        "EXP",           // "65000 (20x)" — base reward with its multiplier (see ComputeRowCells)
        "HP",
        "AcDr",          // synthesised "AC/DR"
        "Dodge",         // synthesised from ability code 34
        "MagicRes",      // "MR"
        "Accuracy",      // synthesised majority/max attack accuracy
        "Damage",        // rounded AvgDmg
        "Efficiency",    // synthesised "Exp/(Dmg+HP)" exp-per-effort metric
        "AvgLairExp",    // "Lair Exp"
        "Lairs",         // synthesised: Σ TotalLairs across the monster's lair groups
        "AvgLairSize",   // synthesised: lair-count-weighted average mobs per lair
        "BiggestLair",   // synthesised: largest mob count across the monster's lair groups
        "Mag",           // synthesised hitmag level from ability code 28
        "Undead",        // raw MDB flag (0 = living), rendered + filterable
    };

    // Friendly grid headers — the columns above keep their raw MDB keys (so binding / search /
    // formatters work) but render compact labels.
    public override IReadOnlyDictionary<string, string> ColumnHeaders { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Number"]     = "ID",
            ["RegenTime"]  = "Rgn",
            ["EXP"]        = "Exp",
            ["AcDr"]       = "AC/DR",
            ["MagicRes"]   = "MR",
            ["Accuracy"]   = "Acc (Maj/Mx)",
            ["Efficiency"]  = "Exp/(Dmg+HP)",
            ["AvgLairExp"]  = "Lair Exp",
            ["Lairs"]       = "# Lairs",
            ["AvgLairSize"] = "Avg Lair Size",
            ["BiggestLair"] = "Biggest Lair",
        };

    // Carried on each row for filtering but not shown as grid columns: Alignment
    // (its dropdown reads the formatted value), and the raw AC / DR fields so the
    // AC ≥ / DR ≥ threshold filters work even though the table shows them combined.
    protected override IReadOnlyList<string> FilterOnlyColumns { get; } =
        new[] { "Align", "ArmourClass", "DamageResist" };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "monster", "mob", "enemy", "creature", "lair", "regen", "respawn",
    };

    // MajorMUD's HP-regen tick: a monster heals its HPRegen amount once every 90 seconds
    // (18 combat rounds × 5 s). Shared by the "HP Regen" grid column and the edit dialog's HP
    // detail row so the two never drift. (GreaterMUD's 30 s / 6 rounds would branch here off a
    // realm flag if/when that realm is supported.)
    private const int RegenIntervalSeconds = 90;

    protected override IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters { get; } =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["EXP"]        = FormatThousands,
            ["HP"]         = FormatThousands,
            ["AvgLairExp"] = FormatThousands,
            ["Efficiency"] = FormatThousands,
            // Undead monsters render an "✗"; living monsters read blank.
            ["Undead"]     = static raw => raw is null or "" or "0" ? "" : "✗",
            // Filter-only column: format so the Alignment dropdown reads names, not codes.
            ["Align"]      = LookupEnums.FormatMonAlignment,
        };

    // Thousands-separated display for big counts ("300,000"); 0 / blank render empty.
    // The raw value stays comma-free, so the leading-int threshold filters read it
    // directly while the grid shows the grouped form (the sort comparer parses either).
    internal static string? FormatThousands(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long n))
            return raw;
        return n == 0 ? "" : n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    }

    public IRelayCommand<GameDataRow?> OpenEditAsyncCommand { get; }
    ICommand IEditableTableSectionViewModel.OpenEditCommand => OpenEditAsyncCommand;

    public MonstersSectionViewModel(
        GameDataCache cache,
        SettingsResolver? resolver = null,
        DialogService? dialogs = null,
        MonsterOverlaySeedStore? overlaySeed = null,
        RoomGraphManager? roomGraph = null)
        : base(cache, resolver)
    {
        _cache = cache;
        _dialogs = dialogs;
        _resolverRef = resolver;
        _overlaySeed = overlaySeed;
        _roomGraph = roomGraph;
        OpenEditAsyncCommand = new AsyncRelayCommand<GameDataRow?>(OpenEditAsync);

        // Filter panel: each stat carries one single-threshold bound, all "at least" (≥).
        // You're finding monsters that HAVE at least this much of a stat — HP ≥ 5000 to
        // surface the tough targets, Exp ≥ 5000 for the rewarding ones, Acc ≥ N for the
        // dangerous hitters. The value tested is the leading integer of each cell's raw
        // value (so "80/10" AC/DR filters on 80). Undead is a checkbox; Alignment is a
        // dropdown built after load (see OnRowsLoaded).
        foreach ((string label, string column) in new (string, string)[]
        {
            ("Exp",      "EXP"),
            ("HP",       "HP"),
            ("AC",       "ArmourClass"),
            ("DR",       "DamageResist"),
            ("Dodge",    "Dodge"),
            ("MR",       "MagicRes"),
            ("Acc",      "Accuracy"),
            ("Damage",   "Damage"),
            ("Mag",      "Mag"),
            ("Lair Exp", "AvgLairExp"),
            ("# Lairs",  "Lairs"),
            ("Rgn",      "RegenTime"),
        })
            ThresholdFilters.Add(new ThresholdFilter(label, column, ThresholdDirection.AtLeast));

        BoolFilters.Add(new BoolFilter("Undead only", "Undead",
            static raw => !(raw is null or "" or "0")));
    }

    // Programmatically apply "Acc ≥ minAcc" and show the result — the Hit
    // Calculator's "Show me the Monsters" action opens this tab and calls here
    // with the accuracy that hits the player at the picked hit-%.
    public void FilterByAccuracyAtLeast(int minAcc)
    {
        ThresholdFilter? acc = ThresholdFilters.FirstOrDefault(t => t.Column == "Accuracy");
        if (acc is null) return;
        acc.Value = minAcc;
        ApplyFiltersCommand.Execute(null);   // commit the pending value + re-filter
    }

    // Monster Number → lair stats from the room graph: Count (# rooms whose lair tag
    // names it = # Lairs), SumMax + MaxMax of those rooms' per-room "(Max N)" caps
    // (for the average / biggest lair size). Sourced from the immutable
    // RoomGraphManager.LairSizeByMonster snapshot so the per-room lair size matches
    // the monster record's Spawns-In list — the Lairs table's group-level "Mobs" field
    // is a different quantity and gave wrong "Biggest Lair" values. Captured each load.
    private System.Collections.Generic.IReadOnlyDictionary<int, (int Count, long SumMax, int MaxMax)> _lairIndex
        = new Dictionary<int, (int, long, int)>();

    protected override void PopulateRows(System.Collections.Generic.IList<GameDataRow> rows)
    {
        BuildLairIndex();
        base.PopulateRows(rows);
    }

    // Runs on the UI thread after AllRows lands (PopulateRows runs on a worker thread,
    // so the observable-collection mutation must happen here, not there — doing it in
    // PopulateRows corrupted the ItemsControl and doubled the dropdowns).
    protected override void OnRowsLoaded() => RebuildCategoryFilters(AllRows);

    // The Alignment dropdown is data-driven: its options are the distinct alignments
    // actually present in the loaded set. Rebuilt on every load / set switch.
    private void RebuildCategoryFilters(System.Collections.Generic.IList<GameDataRow> rows)
    {
        CategoryFilters.Clear();
        CategoryFilters.Add(BuildCategoryFilter("Alignment", "Align", rows));
        OnPropertyChanged(nameof(HasFilterPanel));
    }

    private CategoryFilter BuildCategoryFilter(string label, string column, System.Collections.Generic.IList<GameDataRow> rows)
    {
        var options = new List<string> { CategoryFilter.AnyOption };
        options.AddRange(rows
            .Select(r => r.GetDisplay(column))
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase));
        return new CategoryFilter(label, column, options);
    }

    // Capture the room graph's immutable per-monster lair-size snapshot (built from
    // each room's lair tag). Empty when no graph is wired (e.g. tests) — the lair
    // columns then render blank.
    private void BuildLairIndex()
        => _lairIndex = _roomGraph?.LairSizeByMonster
            ?? new Dictionary<int, (int, long, int)>();

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    protected override IReadOnlyDictionary<string, string?>? ComputeRowCells(JsonElement element)
    {
        int baseExp = ReadInt(element, "EXP");
        int mult = ReadInt(element, "ExpMulti");
        if (mult <= 0) mult = 1;
        long effExp = (long)baseExp * mult;
        int hp = ReadInt(element, "HP");
        int ac = ReadInt(element, "ArmourClass");
        int dr = ReadInt(element, "DamageResist");
        int damage = (int)Math.Round(ReadDouble(element, "AvgDmg"), MidpointRounding.AwayFromZero);
        int dodge = ReadAbilValue(element, 34);   // ability code 34 = Dodge
        int mag = ReadAbilValue(element, 28);     // ability code 28 = Magical (hitmag level)

        var cells = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            // Exp = the actual experience earned per kill = base × multiplier. Stored
            // comma-free (the formatter groups it) so the threshold filter's leading-int
            // read lands on the full value.
            ["EXP"]        = effExp.ToString(Inv),
            ["AcDr"]       = $"{ac}/{dr}",
            ["Dodge"]      = dodge > 0 ? dodge.ToString(Inv) : null,
            ["Mag"]        = mag > 0 ? mag.ToString(Inv) : null,
            ["Damage"]     = damage > 0 ? damage.ToString(Inv) : null,
            ["Accuracy"]   = ComputeAttackAccuracy(element),
            ["Efficiency"] = ComputeEfficiency(effExp, damage, hp),
        };
        if (_lairIndex.TryGetValue(ReadInt(element, "Number"), out (int Count, long SumMax, int MaxMax) lair)
            && lair.Count > 0)
        {
            cells["Lairs"]       = lair.Count.ToString(Inv);
            cells["AvgLairSize"] = ((double)lair.SumMax / lair.Count).ToString("0.#", Inv);
            cells["BiggestLair"] = lair.MaxMax.ToString(Inv);
        }
        return cells;
    }

    // The "Exp/(Dmg+HP)" exp-per-effort metric — effective exp per (two rounds of the
    // monster's damage + its HP), ×100. Higher = better exp for the risk.
    private static string? ComputeEfficiency(long effExp, int damage, int hp)
    {
        int denom = 2 * damage + hp;
        if (denom <= 0 || effExp <= 0) return null;
        long eff = (long)Math.Round(effExp * 100.0 / denom, MidpointRounding.AwayFromZero);
        return eff.ToString(Inv);
    }

    // Value of an ability code in the monster's Abil-0..9 slots (0 if absent). Monster
    // Dodge (code 34) and hitmag level (code 28 "Magical") are stored as abilities, not
    // base columns, so both surface through here.
    private static int ReadAbilValue(JsonElement el, int code)
    {
        for (int i = 0; i < 10; i++)
            if (ReadInt(el, $"Abil-{i}") == code)
                return ReadInt(el, $"AbilVal-{i}");
        return 0;
    }

    // "Acc (Maj/Mx)" — the accuracy of the monster's majority (most-frequent) physical
    // attack, then its highest accuracy across all physical attacks. Collapses to one
    // number when they match. Only physical attacks count (AttType 1/3 with a non-zero
    // chance). A spell-only monster has no physical accuracy, so it renders blank — the
    // AttAcc-0 slot of a spell attack holds a spell id, not an accuracy, so it must not
    // be shown here.
    internal static string? ComputeAttackAccuracy(JsonElement el)
    {
        int majAcc = 0, maxAcc = 0;
        double bestChance = -1;
        for (int i = 0; i < 6; i++)
        {
            int attType = ReadInt(el, $"AttType-{i}");
            if (attType != 1 && attType != 3) continue;
            if (ReadInt(el, $"Att%-{i}") <= 0) continue;
            int acc = ReadInt(el, $"AttAcc-{i}");
            double chance = ReadDouble(el, $"AttTrue%-{i}");
            if (chance > bestChance) { bestChance = chance; majAcc = acc; }
            if (acc > maxAcc) maxAcc = acc;
        }
        if (bestChance < 0) return null;   // no physical attack → blank
        return majAcc == maxAcc ? majAcc.ToString(Inv) : $"{majAcc}/{maxAcc}";
    }

    private async Task OpenEditAsync(GameDataRow? row)
    {
        if (row is null || _dialogs is null) return;
        string? wcc = row.Get("Number");
        if (string.IsNullOrEmpty(wcc)) return;

        // Pull the MDB row for the right-pane "Other Info" pane.
        IReadOnlyList<MdbInfoRow> mdbInfo = BuildMdbInfo(wcc);

        // Existing overlay — always merged across all 4 tiers (Char →
        // BBS → Global → Defaults). The Defaults-tier baseline comes
        // from the realm-flavored MonsterOverlaySeedStore: for stock
        // realms the seed encodes the relationship + priority + flag
        // values from the decoded stock Monsters.md; for Paradigm realms
        // the seed comes from the Paradigm-build Monsters.md. ResolveGameData
        // then overlays each higher tier's
        // delta in priority order so the dialog opens showing exactly
        // what the runtime engines will see for this monster.
        MonsterOverlay seedDefaults =
            (_overlaySeed is not null && int.TryParse(wcc, out int seedNum))
                ? _overlaySeed.GetOverlay(seedNum)
                : new MonsterOverlay();
        MonsterOverlay existing = _resolverRef?.ResolveGameData<MonsterOverlay>(
            "Monsters", wcc, seedDefaults)
            ?? seedDefaults;

        MonsterEditDialogViewModel vm = new(
            wccNoStr:         wcc,
            mdbName:          row.Get("Name") ?? string.Empty,
            existing:         existing,
            currentTier:      row.SourceTier,
            mdbInfo:          mdbInfo,
            writableTiers:    _resolverRef?.WritableTiers(),
            // Lets "Override Attack" auto-resolve a typed cast-code (e.g.
            // "turn") onto the mana-gated spell rung instead of silently
            // falling through to a raw, ungated command — see
            // MonsterEditDialogViewModel.ParseAttackOverride.
            resolveSpellShort: AppServices.Current.SpellShort.NumberByShort,
            // Inverse — shows the cast-code again on reopen instead of the
            // internal Spells.Number it resolved to.
            resolveSpellNumber: AppServices.Current.SpellShort.ShortByNumber);

        MonsterEditResult? result = await _dialogs.OpenWindowAsync<MonsterEditDialogViewModel, MonsterEditResult>(vm);
        if (result is null) return;

        // The dialog only offers writable tiers, but guard anyway: writing to
        // a tier whose scope can't be resolved (Defaults read-only, Character
        // with no profile loaded, BBS with no active BBS) throws from inside
        // the Save handler and crashed the app. Fall back to the most-specific
        // writable tier and note the redirect in the log instead.
        if (_resolverRef is { } resolver)
        {
            SettingsTier tier = result.Tier;
            if (!resolver.CanWriteAt(tier))
            {
                SettingsTier fallback = resolver.WritableTiers()[0];
                AppServices.Current.Log.Warn("GameData/Monsters",
                    $"Cannot save monster #{result.WccNoStr} at {tier} tier "
                    + $"(scope not active); saved at {fallback} instead.");
                tier = fallback;
            }
            resolver.WriteGameDataAt(tier, "Monsters", result.WccNoStr, result.Overlay);
        }

        Reload();
    }

    // Right-pane "Other Info" for the Monster edit dialog. Row order + transforms:
    //   1. WCC No / Experience (with ExpMulti when > 1)
    //   2. Regen Time / Game Limit (suffix "(no respawn)" when RegenTime = 0)
    //   3. Type / Alignment / Undead
    //   4. HP / HP Regen — kept on separate rows per user spec.
    //   5. AC/DR combined slash, MR, Follow %, Charm LVL
    //   6. Cash — raw R/P/G/S/C breakdown
    //   7. Weapon / Create Spell / Death Spell / Greet (cross-ref names)
    //   8. BS Defense (when > 0)
    //   9. Abilities — friendly labels from AbilityNames; Abil-N = 146 (Guarded by) split into
    //      its own row with monster-name cross-ref.
    //  10. Item Drops 0..9 / Attacks 0..4 / Mid Spells 0..4 — first row in each group carries
    //      the section label; subsequent rows have a blank key so they indent visually under
    //      the header.
    // Per user spec we deliberately omit combat-sim outputs (predicted damage etc. — Workshop
    // COMBAT Preview territory) and the alignment-derived [Hostile]/[Not-Hostile] tag.
    private IReadOnlyList<MdbInfoRow> BuildMdbInfo(string wccNoStr)
    {
        List<MdbInfoRow> kv = new();
        if (!int.TryParse(wccNoStr, out int wccNo)) return kv;

        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is null) return kv;

        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement numProp)) continue;
            if (numProp.ValueKind != JsonValueKind.Number) continue;
            if (numProp.GetInt32() != wccNo) continue;

            AddRow(kv, "WCC No", wccNoStr);

            int exp = ReadInt(el, "EXP");
            int multi = ReadInt(el, "ExpMulti");
            if (exp != 0)
            {
                string expDisplay = exp.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
                if (multi > 1) expDisplay += $" (×{multi})";
                AddRow(kv, "Experience", expDisplay);
            }

            int regenTime = ReadInt(el, "RegenTime");
            if (regenTime > 0)
                AddRow(kv, "Regen Time", $"{regenTime} hour{(regenTime == 1 ? "" : "s")}");

            int gameLimit = ReadInt(el, "GameLimit");
            if (gameLimit > 0)
                AddRow(kv, "Game Limit",
                    regenTime == 0 ? $"{gameLimit} (no respawn)" : gameLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));

            AddRowIfPresent(kv, "Type",     LookupEnums.FormatMonType(ReadString(el, "Type")));
            AddRowIfPresent(kv, "Alignment", LookupEnums.FormatMonAlignment(ReadString(el, "Align")));

            // Undead is a byte-boolean: 0 = not undead, any non-zero = undead.
            // Across 1.11p it holds 0, 1, AND 255 (the MDB's Boolean True stored
            // as -1), so the test must be != 0 — never == 1, which silently drops
            // the 255 rows (banshee, zombie cat, skeletal steed, …).
            if (ReadInt(el, "Undead") != 0) AddRow(kv, "Undead", "Yes");

            // HP + HP Regen combined into one row, in the form
            // "7200 (Regens: 2000 HPs every 90 seconds [18 rounds])".
            // 90s / 18 rounds is the classic MajorMUD tick (5s per round
            // × 18 = 90s). If we add GreaterMUD support later, swap to
            // 30s / 6 rounds on that realm via a Settings.RealmType
            // branch.
            int hp = ReadInt(el, "HP");
            if (hp > 0)
            {
                string hpDisplay = hp.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
                int hpRegen = ReadInt(el, "HPRegen");
                if (hpRegen > 0)
                    hpDisplay += $" (Regens: {hpRegen:N0} HPs every {RegenIntervalSeconds} seconds [{RegenIntervalSeconds / 5} rounds])";
                AddRow(kv, "HP", hpDisplay);
            }

            int ac = ReadInt(el, "ArmourClass");
            int dr = ReadInt(el, "DamageResist");
            if (ac != 0 || dr != 0) AddRow(kv, "AC/DR", $"{ac}/{dr}");

            AddRowIfNonZero(kv, "MR",        ReadInt(el, "MagicRes"));
            AddRowIfNonZero(kv, "Follow %",  ReadInt(el, "Follow%"));
            AddRowIfNonZero(kv, "Charm LVL", ReadInt(el, "CharmLVL"));

            // Cash — raw coin breakdown ("2R/3P/5G/10S/2C"). Skipped
            // entirely when every coin slot is zero.
            string cash = BuildCashBreakdown(el);
            if (!string.IsNullOrEmpty(cash)) AddRow(kv, "Cash (up to)", cash);

            // Weapon — cross-ref to Items.Name
            int weaponId = ReadInt(el, "Weapon");
            if (weaponId > 0)
                AddRow(kv, "Weapon", LookupItemName(weaponId) ?? $"Item #{weaponId}");

            // Create / Death spells — cross-ref to Spells.Name with brief
            // effect descriptor when one of the primary Abil codes is set.
            int createSpell = ReadInt(el, "CreateSpell");
            if (createSpell > 0) AddRow(kv, "Create Spell", ResolveSpellWithEffect(createSpell));
            int deathSpell = ReadInt(el, "DeathSpell");
            if (deathSpell > 0) AddRow(kv, "Death Spell",  ResolveSpellWithEffect(deathSpell));

            int greetTxt = ReadInt(el, "GreetTXT");
            if (greetTxt > 0)
            {
                // Decode the greet textblock into the "what can I ask, and what
                // flags fire when I do" tree, then group it by keyword: each
                // depth-0 line is a player keyword, the deeper lines beneath it
                // are the effects it triggers. The dialog surfaces the keywords as
                // chips and flies out each one's effects on click — so a block with
                // dozens of keywords stays compact on the tab. A block with no
                // player keywords (a pure dialogue greet) keeps the plain row.
                IReadOnlyList<TBInfoActionLine> decoded =
                    TBInfoActionDecoder.DecodeGreet(
                        AppServices.Current.TBInfo, _cache, _roomGraph, greetTxt);
                IReadOnlyList<GreetKeyword> keywords = GroupGreetKeywords(decoded);
                if (keywords.Count > 0)
                    kv.Add(new MdbInfoRow("Greet", $"Textblock #{greetTxt}", Keywords: keywords));
                else
                    AddRow(kv, "Greet", $"Textblock #{greetTxt}");
            }

            AddRowIfNonZero(kv, "BS Defense", ReadInt(el, "BSDefense"));

            // Abilities — iterate Abil-0..9, friendly labels via
            // AbilityNames. Code 146 = "Guarded by" splits into its
            // own row with monster-name resolution. Values render
            // signed ("+5" / "-50") so resist-style abilities read
            // unambiguously.
            List<string> abilities = new();
            List<int>    guards    = new();
            for (int i = 0; i < 10; i++)
            {
                int code = ReadInt(el, $"Abil-{i}");
                if (code == 0) continue;
                int val = ReadInt(el, $"AbilVal-{i}");
                if (code == 146)
                {
                    guards.Add(val);
                    continue;
                }
                string label = AbilityNames.GetName(code) ?? $"Ability {code}";
                abilities.Add(val == 0 ? label : $"{label} {FormatSigned(val)}");
            }
            if (abilities.Count > 0)
                AddRow(kv, "Abilities", string.Join(", ", abilities));
            if (guards.Count > 0)
            {
                List<string> guardNames = new();
                foreach (int g in guards)
                    guardNames.Add(LookupMonsterName(g) ?? $"Monster #{g}");
                AddRow(kv, "Guarded by", string.Join(", ", guardNames));
            }

            // Item Drops — one clickable chip per non-zero DropItem-N slot,
            // each jumping to the item's Items-tab record. Chip label is
            // "name (pct%)" (pct omitted when the slot has no drop chance).
            List<(int Id, string Text)> drops = new();
            for (int i = 0; i < 10; i++)
            {
                int itemId = ReadInt(el, $"DropItem-{i}");
                if (itemId == 0) continue;
                int pct = ReadInt(el, $"DropItem%-{i}");
                string itemName = LookupItemName(itemId) ?? $"Item #{itemId}";
                drops.Add((itemId, pct > 0 ? $"{itemName} ({pct}%)" : itemName));
            }
            AddItemList(kv, "Item Drops", drops);

            // ----- Mob's Attacks (each AttType-N in 1..3 with Att%>0
            // gets its own header + sub-rows) -----
            //
            // Attack rendering format:
            //
            //   Mob's Attacks         {Energy} energy/round
            //   (20%) claws           Min-Max: 50-90
            //                         Accuracy: 60
            //                         Energy: 250 (Max 4x/round)
            //                         Hit Spell: poison cloud      (when AttHitSpell > 0)
            //   (15%) Cast Fireball   Spell: fireball lvl 50
            //                         Success %: 75
            //                         Energy: 300 (Max 3x/round)
            //
            // AttType 1 (normal) / 3 (rob) → Min-Max + Accuracy fields.
            // AttType 2 (spell)            → AttAcc holds the spell id,
            //                                 AttMax holds the cast level,
            //                                 AttMin holds the success %.
            //
            // Per-attack percent uses AttTrue% when present (the actual
            // probability) and falls back to Att% otherwise (the
            // cumulative-threshold value).
            int monsterEnergy = ReadInt(el, "Energy");
            bool hasAttacks = false;
            for (int i = 0; i < 5; i++)
            {
                int at = ReadInt(el, $"AttType-{i}");
                if (at >= 1 && at <= 3 && ReadInt(el, $"Att%-{i}") > 0) { hasAttacks = true; break; }
                if (ReadInt(el, $"MidSpell-{i}") > 0)                  { hasAttacks = true; break; }
            }
            if (hasAttacks)
            {
                // Monster energy is the per-round budget the mob spends
                // on attacks/spells. Stock MajorMUD is always 1000;
                // paradigm / future realms may differ.
                AddRow(kv, "Mob's Attacks",
                    monsterEnergy > 0
                        ? $"{monsterEnergy} energy/round"
                        : string.Empty);

                for (int i = 0; i < 5; i++)
                {
                    int attType = ReadInt(el, $"AttType-{i}");
                    int attPct  = ReadInt(el, $"Att%-{i}");
                    if (attType < 1 || attType > 3 || attPct <= 0) continue;

                    string attName = ReadString(el, $"AttName-{i}").Trim();
                    int attEnergy  = ReadInt(el, $"AttEnergy-{i}");
                    int hitSpell   = ReadInt(el, $"AttHitSpell-{i}");
                    int trueRound  = (int)Math.Round(ReadDouble(el, $"AttTrue%-{i}"));
                    int displayPct = trueRound > 0 ? trueRound : attPct;

                    string header = string.IsNullOrEmpty(attName)
                        ? $"({displayPct}%) Attack {i + 1}"
                        : $"({displayPct}%) {attName}";

                    // The attack name is often a full sentence ("claws you with its
                    // plague-ridden claws") — far wider than the key column. Emit it as
                    // a full-width header row so it wraps in full, then indent its stats
                    // beneath it as empty-key value rows.
                    kv.Add(new MdbInfoRow(header, string.Empty, FullWidth: true));

                    if (attType == 1 || attType == 3)
                    {
                        int min = ReadInt(el, $"AttMin-{i}");
                        int max = ReadInt(el, $"AttMax-{i}");
                        int acc = ReadInt(el, $"AttAcc-{i}");
                        AddRow(kv, string.Empty, $"Min-Max: {min}-{max}");
                        AddRow(kv, string.Empty, $"Accuracy: {acc}");
                        AddRow(kv, string.Empty, FormatEnergyRow(attEnergy, monsterEnergy));
                        if (hitSpell > 0)
                            AddRow(kv, string.Empty, $"Hit Spell: {ResolveSpellWithEffect(hitSpell)}");
                    }
                    else // attType == 2 (spell-attack)
                    {
                        int spellId   = ReadInt(el, $"AttAcc-{i}");
                        int spellLvl  = ReadInt(el, $"AttMax-{i}");
                        int successPc = ReadInt(el, $"AttMin-{i}");
                        string spell  = ResolveSpellWithEffect(spellId, spellLvl);
                        AddRow(kv, string.Empty,
                            spellLvl > 0 ? $"Spell: {spell} lvl {spellLvl}" : $"Spell: {spell}");
                        AddRow(kv, string.Empty, $"Success %: {successPc}");
                        AddRow(kv, string.Empty, FormatEnergyRow(attEnergy, monsterEnergy));
                        if (hitSpell > 0)
                            AddRow(kv, string.Empty, $"Hit Spell: {ResolveSpellWithEffect(hitSpell)}");
                    }
                }
            }

            // ----- Between Rounds (formerly Mid Spells) -----
            // MidSpell% is stored as a cumulative threshold across the 5
            // slots (slot 0's value is its raw chance; slot N's is the
            // running sum). The display shows the DELTA so each row reads
            // as the actual chance for that spell to fire.
            int cumulative = 0;
            for (int i = 0, shown = 0; i < 5; i++)
            {
                int spellId = ReadInt(el, $"MidSpell-{i}");
                if (spellId == 0) continue;
                int threshold = ReadInt(el, $"MidSpell%-{i}");
                int delta = threshold - cumulative;
                cumulative = threshold;
                int lvl = ReadInt(el, $"MidSpellLVL-{i}");
                string spellName = ResolveSpellWithEffect(spellId, lvl);
                string row = lvl > 0 ? $"({delta}%) [{spellName}, lvl {lvl}]" : $"({delta}%) [{spellName}]";
                AddRow(kv, shown == 0 ? "Between Rounds" : string.Empty, row);
                shown++;
            }

            // ----- Summons (what THIS monster spawns) -----
            // Mirror of "Summoned By": some monsters (e.g. Leo the Quick)
            // conjure other monsters — via a summon spell cast in combat
            // (with a % chance), on death, or on spawn. Surface the spawned
            // monster + how + the chance where one applies.
            List<string> produces = FindOutgoingSummons(el);
            for (int j = 0; j < produces.Count; j++)
                AddRow(kv, j == 0 ? "Summons" : string.Empty, produces[j]);

            // ----- Spawns In (lair rooms) -----
            // Reverse lookup over the active room graph: every room whose
            // lair list names this monster is a spawn site. Listed in full
            // as map/room pairs at the bottom per user spec (the pane
            // scrolls; a common mob like giant rat lairs in dozens of
            // rooms and we show them all).
            AddRoomList(kv, "Spawns In", FindSpawnRooms(wccNo),
                k => $"{k.Map}/{k.Room} (lair: {SpawnLairMax(k)})");

            // ----- Placed In (fixed NPC room) -----
            // Placed monsters (bosses / uniques / shopkeepers) live in a
            // single home room via the room's NPC field rather than a
            // lair group — typically the timed-respawn monsters that
            // aren't laired anywhere. Show that room so they aren't a
            // dead end in the panel.
            AddRoomList(kv, "Placed In", FindPlacedRooms(wccNo));

            // ----- Summoned (spell sources) -----
            // The remaining "where does this come from" case: monsters
            // that aren't laired OR placed are often conjured by a summon
            // spell (Abil 12). Resolve who casts that spell:
            //   • a ROOM via its room-spell (cast when a player enters) —
            //     a location, listed like the other room sources;
            //   • a MONSTER, in combat (spell-attack / hit-spell / between-
            //     rounds) and/or as its death-spell and/or on spawn.
            // Best-effort — the summon→monster encoding is inconsistent
            // (see SpellSummons).
            List<(int Number, string Name)> summonSpells = FindSummonSpells(wccNo);
            if (summonSpells.Count > 0)
            {
                HashSet<int> spellIds = summonSpells.Select(s => s.Number).ToHashSet();

                // Room-spell summons (room casts on entry) — a location.
                AddRoomList(kv, "Summoned In", FindRoomSpellRooms(spellIds));

                // Monster casters with their context tag(s) — listed in
                // full (the pane scrolls; no expand affordance on a
                // truncated list). Falls back to the bare spell name when
                // no room or monster caster resolves so the user still
                // knows it's summonable.
                List<string> mobs = FindSummoningMonsters(spellIds);
                string by = mobs.Count > 0
                    ? string.Join(", ", mobs)
                    : string.Join(", ", summonSpells.Select(s => $"{s.Name} (spell)"));
                AddRow(kv, "Summoned By", by);
            }

            break;
        }
        return kv;
    }

    // Rooms whose lair names wccNo, sorted by map then room. Reads the in-memory
    // RoomGraphManager (already parsed for the active set) so no 11 MB Rooms.json reload is
    // needed. Empty when the graph isn't wired or the monster lairs nowhere.
    private IReadOnlyList<RoomKey> FindSpawnRooms(int wccNo)
    {
        if (_roomGraph is null) return Array.Empty<RoomKey>();
        List<RoomKey> rooms = new();
        foreach (Room room in _roomGraph.Rooms)
        {
            if (LairNamesMonster(room.RawLairTag, wccNo))
                rooms.Add(room.Key);
        }
        rooms.Sort(static (a, b) => a.Map != b.Map ? a.Map.CompareTo(b.Map) : a.Room.CompareTo(b.Room));
        return rooms;
    }

    // Rooms that "place" wccNo via their NPC field (fixed-spawn home room), sorted by map then
    // room. Empty when the graph isn't wired or the monster is laired-only / unplaced.
    private IReadOnlyList<RoomKey> FindPlacedRooms(int wccNo)
    {
        if (_roomGraph is null) return Array.Empty<RoomKey>();
        List<RoomKey> rooms = new();
        foreach (Room room in _roomGraph.Rooms)
        {
            if (room.Npc == wccNo)
                rooms.Add(room.Key);
        }
        rooms.Sort(static (a, b) => a.Map != b.Map ? a.Map.CompareTo(b.Map) : a.Room.CompareTo(b.Room));
        return rooms;
    }

    // Spells (number + name) that summon wccNo (best-effort — see SpellSummons). Covers
    // monsters that aren't laired or placed but are conjured by a caster.
    private List<(int Number, string Name)> FindSummonSpells(int wccNo)
    {
        List<(int, string)> spells = new();
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return spells;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!SpellSummons(el, wccNo)) continue;
            int num = ReadInt(el, "Number");
            string name = ReadString(el, "Name").Trim();
            spells.Add((num, string.IsNullOrEmpty(name) ? $"Spell #{num}" : name));
        }
        return spells;
    }

    // Rooms whose room-spell (Room.Spell, cast when a player enters) is one of spellIds — i.e.
    // rooms that summon the monster on entry. Sorted by map then room.
    private IReadOnlyList<RoomKey> FindRoomSpellRooms(IReadOnlySet<int> spellIds)
    {
        if (_roomGraph is null) return Array.Empty<RoomKey>();
        List<RoomKey> rooms = new();
        foreach (Room room in _roomGraph.Rooms)
        {
            if (room.Spell != 0 && spellIds.Contains(room.Spell))
                rooms.Add(room.Key);
        }
        rooms.Sort(static (a, b) => a.Map != b.Map ? a.Map.CompareTo(b.Map) : a.Room.CompareTo(b.Room));
        return rooms;
    }

    // Monsters that cast one of spellIds, each tagged with how (combat / death / on spawn) —
    // e.g. "Argak the Grey (death)". Deduped by label; popular summon spells (cast by many
    // mobs) are capped by the caller.
    private List<string> FindSummoningMonsters(IReadOnlySet<int> spellIds)
    {
        List<string> casters = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is null) return casters;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            string? ctx = SummonContext(el, spellIds);
            if (ctx is null) continue;
            int num = ReadInt(el, "Number");
            string name = ReadString(el, "Name").Trim();
            string label = $"{(string.IsNullOrEmpty(name) ? $"Monster #{num}" : name)} ({ctx})";
            if (seen.Add(label)) casters.Add(label);
        }
        return casters;
    }

    // Monsters this monster summons, each as "<name> (<how>[, <chance>])" — the reverse of
    // FindSummoningMonsters. Walks the viewed monster's own spell slots (create / death /
    // spell-attack / hit-spell / between-rounds), resolves each summon spell to its target
    // monster, and tags how + the % chance where the cast carries one.
    private List<string> FindOutgoingSummons(JsonElement monster)
    {
        JsonDocument? spellsDoc = _cache.GetRawTable("Spells");
        if (spellsDoc is null) return new();

        // Index spells by number so each cast slot is an O(1) resolve.
        Dictionary<int, JsonElement> spellByNum = new();
        foreach (JsonElement s in spellsDoc.RootElement.EnumerateArray())
        {
            int num = ReadInt(s, "Number");
            if (num > 0) spellByNum[num] = s;
        }

        return BuildOutgoingSummonLabels(
            monster,
            spellId => spellByNum.TryGetValue(spellId, out JsonElement s) ? SummonTargets(s) : Array.Empty<int>(),
            target => LookupMonsterName(target) ?? $"Monster #{target}");
    }

    // Pure label-builder behind FindOutgoingSummons — extracted so the context + chance logic
    // is testable without a loaded cache. summonTargetsOf maps a spell id to the monster(s) it
    // summons (empty for non-summon spells); nameOf maps a monster number to its display name.
    internal static List<string> BuildOutgoingSummonLabels(
        JsonElement monster,
        Func<int, IReadOnlyList<int>> summonTargetsOf,
        Func<int, string> nameOf)
    {
        List<string> result = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        void Emit(int spellId, string context, string? chance)
        {
            if (spellId <= 0) return;
            foreach (int target in summonTargetsOf(spellId))
            {
                string label = chance is null
                    ? $"{nameOf(target)} ({context})"
                    : $"{nameOf(target)} ({context}, {chance})";
                if (seen.Add(label)) result.Add(label);
            }
        }

        // Certain casts — no chance figure.
        Emit(ReadInt(monster, "CreateSpell"), "on spawn", null);
        Emit(ReadInt(monster, "DeathSpell"),  "on death", null);

        // Combat — spell-attacks (carry an Att% chance) and per-hit spells.
        for (int i = 0; i < 5; i++)
        {
            if (ReadInt(monster, $"AttType-{i}") == 2)
            {
                int truePct = (int)Math.Round(ReadDouble(monster, $"AttTrue%-{i}"));
                int pct = truePct > 0 ? truePct : ReadInt(monster, $"Att%-{i}");
                Emit(ReadInt(monster, $"AttAcc-{i}"), "combat", pct > 0 ? $"{pct}%" : null);
            }
            Emit(ReadInt(monster, $"AttHitSpell-{i}"), "combat, on hit", null);
        }

        // Between-rounds — MidSpell% is a cumulative threshold across the
        // slots, so the per-spell chance is the delta (mirrors the
        // "Between Rounds" rendering above).
        int cumulative = 0;
        for (int i = 0; i < 5; i++)
        {
            int spellId = ReadInt(monster, $"MidSpell-{i}");
            if (spellId == 0) continue;
            int threshold = ReadInt(monster, $"MidSpell%-{i}");
            int delta = threshold - cumulative;
            cumulative = threshold;
            Emit(spellId, "between rounds", delta > 0 ? $"{delta}%" : null);
        }

        return result;
    }

    // How monster casts a spell in spellIds, or null if it doesn't. Combat = a spell-attack
    // (AttType 2 whose AttAcc is the spell), a per-hit spell (AttHitSpell), or a between-rounds
    // spell (MidSpell); plus the DeathSpell ("death") and CreateSpell ("on spawn"). A monster
    // can carry several.
    internal static string? SummonContext(JsonElement monster, IReadOnlySet<int> spellIds)
    {
        bool combat = false;
        for (int i = 0; i < 5; i++)
        {
            if (ReadInt(monster, $"AttType-{i}") == 2 && spellIds.Contains(ReadInt(monster, $"AttAcc-{i}")))
                combat = true;
            if (spellIds.Contains(ReadInt(monster, $"AttHitSpell-{i}"))) combat = true;
            if (spellIds.Contains(ReadInt(monster, $"MidSpell-{i}"))) combat = true;
        }
        bool death = spellIds.Contains(ReadInt(monster, "DeathSpell"));
        bool spawn = spellIds.Contains(ReadInt(monster, "CreateSpell"));
        if (!combat && !death && !spawn) return null;

        List<string> parts = new(3);
        if (combat) parts.Add("combat");
        if (death) parts.Add("death");
        if (spawn) parts.Add("on spawn");
        return string.Join(", ", parts);
    }

    // True when spell summons monster monsterNo. Summon spells carry ability code 12; the
    // summoned monster number is inconsistently encoded, so this is best-effort: a summon
    // ability's own value (AbilVal) wins when positive, and only when NO summon ability names a
    // target do we fall back to the spell's MinBase (e.g. "raptor summon" → MinBase 509 =
    // tetraraptor). Preferring the ability value avoids mis-attributing odd data — e.g.
    // "summon silver skull" has MinBase 1 (giant rat) but its summon value points elsewhere.
    internal static bool SpellSummons(JsonElement spell, int monsterNo)
        => monsterNo > 0 && SummonTargets(spell).Contains(monsterNo);

    // Monster number(s) spell summons, or empty when it isn't a summon spell. Encoding is
    // inconsistent (see SpellSummons): the summon abilities' own positive values win, and only
    // when none name a target does the spell's MinBase stand in (e.g. "raptor summon" → 509 =
    // tetraraptor).
    internal static IReadOnlyList<int> SummonTargets(JsonElement spell)
    {
        List<int> targets = new();
        bool isSummon = false;
        for (int i = 0; i < 10; i++)
        {
            if (ReadInt(spell, $"Abil-{i}") != 12) continue;   // 12 = summon
            isSummon = true;
            int target = ReadInt(spell, $"AbilVal-{i}");
            if (target > 0 && !targets.Contains(target)) targets.Add(target);
        }
        if (!isSummon) return Array.Empty<int>();
        if (targets.Count == 0)
        {
            int minBase = ReadInt(spell, "MinBase");
            if (minBase > 0) targets.Add(minBase);
        }
        return targets;
    }

    // Append a "label (N)" row listing every room as a clickable map/room chip that opens
    // the room-detail popup. Not truncated — the Other Info pane scrolls and a truncated
    // list has no expand affordance, so show them all. Falls back to a plain comma-joined
    // string when no DialogService is available (design-time). No-op when rooms is empty.
    private void AddRoomList(List<MdbInfoRow> kv, string label, IReadOnlyList<RoomKey> rooms,
        Func<RoomKey, string>? labelFor = null)
    {
        if (rooms.Count == 0) return;
        labelFor ??= static k => $"{k.Map}/{k.Room}";
        string list = string.Join(", ", rooms.Select(labelFor));
        IReadOnlyList<RoomLink>? links = _dialogs is { } dialogs
            ? rooms.Select(k => new RoomLink(labelFor(k), k, dialogs)).ToList()
            : null;
        kv.Add(new MdbInfoRow($"{label} ({rooms.Count})", list, Rooms: links));
    }

    // Per-room lair cap — the "(Max N)" from a room's lair tag, e.g. "(lair: 2)"
    // appended to each Spawns-In room so the record shows how many of the monster
    // spawn there. 0 when the room / tag can't be read (shouldn't happen for a
    // room FindSpawnRooms already matched by its lair tag).
    private int SpawnLairMax(RoomKey key)
        => _roomGraph?.GetRoom(key) is { } room
           && RoomTooltipBuilder.TryParseLairMax(room.RawLairTag, out int max)
            ? max : 0;

    // Append an "Item Drops (N)" row listing every dropped item as a clickable
    // chip that jumps the browser's Items tab to that item. Mirrors AddRoomList;
    // the joined string is the plain-text fallback the row also carries. No-op
    // when the monster drops nothing.
    private static void AddItemList(List<MdbInfoRow> kv, string label, IReadOnlyList<(int Id, string Text)> items)
    {
        if (items.Count == 0) return;
        string list = string.Join(", ", items.Select(i => i.Text));
        IReadOnlyList<ItemLink> links = items.Select(i => new ItemLink(i.Text, i.Id)).ToList();
        kv.Add(new MdbInfoRow($"{label} ({items.Count})", list, Items: links));
    }

    // Group the flat, depth-tagged greet decode into per-keyword blocks: a depth-0
    // line opens a new keyword; the deeper lines that follow (re-indented relative
    // to it) are the effects that fire when it's asked. Depth-0 lines before the
    // first keyword (there shouldn't be any) are ignored. Empty when the block has
    // no player keywords.
    private static IReadOnlyList<GreetKeyword> GroupGreetKeywords(IReadOnlyList<TBInfoActionLine> lines)
    {
        List<GreetKeyword> keywords = new();
        string? keyword = null;
        List<string> effects = new();
        foreach (TBInfoActionLine line in lines)
        {
            if (line.Depth == 0)
            {
                if (keyword is not null)
                    keywords.Add(new GreetKeyword(keyword, effects));
                keyword = line.Text;
                effects = new List<string>();
            }
            else if (keyword is not null)
            {
                effects.Add(new string(' ', (line.Depth - 1) * 2) + line.Text);
            }
        }
        if (keyword is not null)
            keywords.Add(new GreetKeyword(keyword, effects));
        return keywords;
    }

    // True when a Room.RawLairTag lists wccNo as one of its spawn monsters. v1.11p tags are
    // "(Max N): id,id,…,[group-index]" — the monster ids precede the bracketed group key; the
    // [..] suffix is parsed off so its digits aren't mistaken for monster ids.
    internal static bool LairNamesMonster(string? rawLairTag, int wccNo)
    {
        if (string.IsNullOrEmpty(rawLairTag)) return false;
        int bracket = rawLairTag.IndexOf('[');
        string head = bracket >= 0 ? rawLairTag[..bracket] : rawLairTag;
        int colon = head.IndexOf(':');
        if (colon < 0) return false;
        foreach (string token in head[(colon + 1)..]
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out int id) && id == wccNo) return true;
        }
        return false;
    }

    // ----- Field readers + row helpers -----

    private static int ReadInt(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    private static double ReadDouble(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return 0d;
        return v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : 0d;
    }

    // "+5" / "-50" / "0" — used by signed-value ability rows.
    private static string FormatSigned(int n) => n > 0
        ? "+" + n.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // "Energy: 250 (Max 4x/round)" — divides monster total by per-attack cost.
    private static string FormatEnergyRow(int attEnergy, int monsterEnergy)
    {
        if (attEnergy <= 0) return $"Energy: {attEnergy}";
        if (monsterEnergy <= 0) return $"Energy: {attEnergy}";
        return $"Energy: {attEnergy} (Max {monsterEnergy / attEnergy}x/round)";
    }

    private static string ReadString(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return string.Empty;
        return v.ValueKind switch
        {
            JsonValueKind.Null      => string.Empty,
            JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String    => v.GetString() ?? string.Empty,
            JsonValueKind.Number    => v.ToString(),
            _                        => v.ToString(),
        };
    }

    private static void AddRow(List<MdbInfoRow> kv, string label, string value)
        => kv.Add(new MdbInfoRow(label, value));

    private static void AddRowIfPresent(List<MdbInfoRow> kv, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value)) AddRow(kv, label, value);
    }

    private static void AddRowIfNonZero(List<MdbInfoRow> kv, string label, int value)
    {
        if (value != 0) AddRow(kv, label, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // "2R/3P/5G/10S/2C" — only non-zero coins, slash-separated.
    private static string BuildCashBreakdown(JsonElement el)
    {
        List<string> parts = new();
        void Maybe(string field, string letter)
        {
            int v = ReadInt(el, field);
            if (v > 0) parts.Add(v.ToString(System.Globalization.CultureInfo.InvariantCulture) + letter);
        }
        Maybe("R", "R");
        Maybe("P", "P");
        Maybe("G", "G");
        Maybe("S", "S");
        Maybe("C", "C");
        return string.Join("/", parts);
    }

    // ----- Cross-reference helpers (Items / Spells / Monsters) -----
    //
    // Thin shims over GameDataCache.FindNameByNumber that add this VM's
    // two stricter semantics: skip non-positive ids (zero is the "no
    // link" sentinel) and treat empty/missing Name as null so callers
    // get a single boolean check instead of having to also test for "".

    private string? LookupItemName(int itemId)    => LookupName("Items",    itemId);
    private string? LookupSpellName(int spellId)  => LookupName("Spells",   spellId);
    private string? LookupMonsterName(int monNum) => LookupName("Monsters", monNum);

    private string? LookupName(string table, int number)
    {
        if (number <= 0) return null;
        string? name = _cache.FindNameByNumber(table, number);
        return string.IsNullOrEmpty(name) ? null : name;
    }

    // "{spell name} (effect)" — spell name with a brief effect descriptor when the spell's
    // primary Abil-N entries surface a recognisable effect (damage range, heal range, poison,
    // fear, etc.). Falls back to just the spell name when none of the primary effect codes are
    // present. Used by Hit Spell / Death Spell / Create Spell / Between Rounds rows.
    //
    // castLevel is the cast level for damage-range scaling. Pass 0 to use the spell's raw
    // MinBase/MaxBase (correct for monster hit / death / create spells). For Between Rounds
    // spells pass MidSpellLVL-N.
    private string ResolveSpellWithEffect(int spellId, int castLevel = 0)
    {
        string name = LookupSpellName(spellId) ?? $"Spell #{spellId}";
        string effect = ResolveSpellEffect(spellId, castLevel);
        return string.IsNullOrEmpty(effect) ? name : $"{name} ({effect})";
    }

    // Brief comma-joined effect descriptor from a spell's primary Abil-N codes.
    private string ResolveSpellEffect(int spellId, int castLevel)
    {
        if (spellId <= 0) return string.Empty;
        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return string.Empty;

        JsonElement? found = null;
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("Number", out JsonElement n)) continue;
            if (n.ValueKind != JsonValueKind.Number) continue;
            if (n.GetInt32() == spellId) { found = el; break; }
        }
        if (found is null) return string.Empty;
        JsonElement s = found.Value;

        // Min/Max with optional level scaling.
        int minBase    = ReadInt(s, "MinBase");
        int maxBase    = ReadInt(s, "MaxBase");
        int minInc     = ReadInt(s, "MinInc");
        int maxInc     = ReadInt(s, "MaxInc");
        int minIncLvls = ReadInt(s, "MinIncLVLs");
        int maxIncLvls = ReadInt(s, "MaxIncLVLs");
        int cap        = ReadInt(s, "Cap");

        int min = minBase;
        int max = maxBase;
        if (castLevel > 0)
        {
            if (minIncLvls > 0) min += (minInc / minIncLvls) * castLevel;
            if (maxIncLvls > 0) max += (maxInc / maxIncLvls) * castLevel;
            if (cap > 0)
            {
                if (min > cap) min = cap;
                if (max > cap) max = cap;
            }
        }

        // Pick out the primary effect codes. Full ability-chain recursion
        // (nested EndCast / Summon / Teleport / TextBlock descriptors) is
        // out of scope here — the Spells tab is the place to dig into the
        // full ability chain; the Monster dialog only surfaces the
        // headline effect so the row reads at a glance.
        List<string> effects = new();
        for (int i = 0; i < 10; i++)
        {
            int code = ReadInt(s, $"Abil-{i}");
            if (code == 0) continue;
            string? desc = code switch
            {
                 1 => FormatRange("dmg",   min, max),  // Damage
                17 => FormatRange("dmg",   min, max),  // Damage(-MR)
                18 => FormatRange("heal",  min, max),  // Heal
                 8 => FormatRange("drain", min, max),  // DrainLife
                19 => "poison",
                60 => "fear",
                71 => "confusion",
                95 => "slay",
                53 => "blindness",
                12 => "summon",
                _  => null,
            };
            if (!string.IsNullOrEmpty(desc) && !effects.Contains(desc)) effects.Add(desc);
        }
        return string.Join(", ", effects);
    }

    // "dmg 10-30" / "dmg 10" / "" when both ends are zero.
    private static string FormatRange(string label, int min, int max)
    {
        if (min == 0 && max == 0) return string.Empty;
        if (min == max) return $"{label} {min}";
        return $"{label} {min}-{max}";
    }

}

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

    // The right-pane "Other Info" assembly lives in the shared MonsterMdbInfoBuilder now, so
    // the same record opens by Number from outside the browser (the Navigation Room Info panel
    // → MonsterRecordDialogService) as well as from a browser row here.
    private IReadOnlyList<MdbInfoRow> BuildMdbInfo(string wccNoStr)
        => new MonsterMdbInfoBuilder(_cache, _roomGraph, AppServices.Current.TBInfo, _dialogs).Build(wccNoStr);

    // Grid-cell JSON readers kept here (the builder carries its own copies) — used by the
    // synthesised columns in ComputeRowCells.
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


}

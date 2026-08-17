using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.GameData;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.ViewModels.GameData.Edit;

// Builds the Message/Spell edit dialog's "Game Data" tab content — the read-only field
// dump for a spell (effect summary, curated growth block, magery, abilities with their
// cross-referenced names, teleport destination, textblock-walked summons/casts/item-gates,
// and the "Learned From" / "Casted By" source lists). Extracted from the Spells browser
// tab so the same record can be opened by Number from outside the browser (Room Info →
// SpellRecordDialogService) as well as from a browser row (SpellsSectionViewModel). Pure
// read of the active set's tables; the ColumnFormatters (enum column formatting) come from
// the section so the dialog's enum rows match the grid's.
public sealed class SpellInfoRowsBuilder
{
    private readonly GameDataCache _cache;

    // Enum-column formatters for the Game Data tab, shared with the Spells grid
    // (SpellsSectionViewModel.ColumnFormatters returns this) so the dialog's enum rows and the
    // grid's cells never drift.
    internal static readonly IReadOnlyDictionary<string, Func<string?, string?>> ColumnFormatters =
        new Dictionary<string, Func<string?, string?>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Magery"]  = LookupEnums.FormatMagery,
            ["AttType"] = LookupEnums.FormatSpellAttackType,
            ["Targets"] = LookupEnums.FormatSpellTargets,
        };

    // Scaling columns folded into the curated growth block — emitted once, in place of the
    // first of them (MinBase, in MDB key order). The raw per-level numbers are unreadable on
    // their own; SpellGrowthFormatter turns them into a magnitude range + per-level formula.
    private static readonly HashSet<string> _scalingFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "MinBase", "MinInc", "MinIncLVLs",
        "MaxBase", "MaxInc", "MaxIncLVLs",
        "Dur", "DurInc", "DurIncLVLs", "Cap",
    };

    public SpellInfoRowsBuilder(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
    }

    // The spell's full imported data for the dialog's Game Data tab. Enum columns (attack-type /
    // targets) format via the shared lookups; Magery and MageryLVL collapse to one "Mage-1" row;
    // the raw per-level scaling columns collapse to a curated growth block (magnitude range, level
    // cap, per-level formula, at-cap duration); each non-zero ability slot resolves to its name
    // with any numeric reference (CastsSp / Summon / EquipItem / …) translated to the real Spell /
    // Monster / Item name; and the "Learned From" / "Casted By" source lists resolve their
    // "Item #N" / "Monster #N" tokens to real names. Empty when the active set has no Spells table
    // or no matching row.
    public IReadOnlyList<GameDataInfoRow> Build(int spellNumber)
    {
        var rows = new List<GameDataInfoRow>();

        JsonDocument? doc = _cache.GetRawTable("Spells");
        if (doc is null) return rows;

        JsonElement? found = null;
        foreach (JsonElement r in doc.RootElement.EnumerateArray())
            if (ReadInt(r, "Number") == spellNumber) { found = r; break; }
        if (found is not { } el) return rows;

        // Scaling inputs projected once — feeds the curated growth block that
        // replaces the raw MinBase / MaxInc / Cap / … columns.
        KnownSpellCatalog catalog = new(_cache);
        SpellFormulaInput? formula = catalog.GetFormulaByNumber(spellNumber);

        // Plain-English effect summary at the top — the same translator the
        // Items "Other Info" pane + the Spell Book use, so the spell reads as
        // "Dmg 1–4 · then casts X · Slowness" instead of a wall of raw ability
        // codes (the field-by-field rows below stay for the full detail).
        // Rendered at level 0: the formatter clamps Min/Max to ReqLevel so the
        // base figures are still real, and the per-level scaling lives in the
        // curated growth block.
        if (formula is { } effectFormula)
        {
            IReadOnlyDictionary<int, IReadOnlyList<KnownSpell>> tbIndex = catalog.BuildCastByTextblockIndex();
            string effect = SpellEffectFormatter.Format(
                effectFormula, level: 0,
                resolveChain: catalog.GetFormulaByNumber,
                resolveSpellName: catalog.GetSpellNameByNumber,
                resolveTextblockCasts: tb => tbIndex.TryGetValue(tb, out IReadOnlyList<KnownSpell>? list)
                    ? list : Array.Empty<KnownSpell>(),
                resolveMonsterName: n => _cache.FindNameByNumber("Monsters", n));
            // Skip the formatter's unhelpful "TextBlock N" fallback (emitted
            // when a spell's only effect is a textblock it can't expand) — the
            // Summons / Casts / item-gate rows from the textblock walk below
            // carry the real info instead.
            if (effect.Length > 0 && effect != "—" && !BareTextblock.IsMatch(effect))
                rows.Add(new GameDataInfoRow("Effect", effect));
        }

        bool teleportRendered = false;
        bool growthRendered = false;
        foreach (JsonProperty prop in el.EnumerateObject())
        {
            string field = prop.Name;

            // Scaling columns collapse into one curated growth block, emitted
            // in place of the first of them encountered (MinBase).
            if (_scalingFields.Contains(field))
            {
                if (!growthRendered)
                {
                    EmitGrowthBlock(rows, formula);
                    growthRendered = true;
                }
                continue;
            }

            // MageryLVL folds into the Magery row ("Mage-1"); never shown alone.
            if (string.Equals(field, "MageryLVL", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(field, "Magery", StringComparison.OrdinalIgnoreCase))
            {
                if (MageryDisplay(el, prop.Value) is { } magery)
                    rows.Add(new GameDataInfoRow("Magery", magery));
                continue;
            }

            // Ability pairs (Abil-N + AbilVal-N) collapse to one row; skip the
            // value half — it's rendered alongside its code.
            if (field.StartsWith("AbilVal-", StringComparison.Ordinal)) continue;
            if (field.StartsWith("Abil-", StringComparison.Ordinal))
            {
                if (prop.Value.ValueKind != JsonValueKind.Number
                    || !prop.Value.TryGetInt32(out int code) || code == 0)
                    continue;

                // Damage / heal codes (1, 8, 17, 18) feed the curated magnitude
                // range above — don't also render a bare "Damage(-MR): 0" row.
                if (code is 1 or 8 or 17 or 18) continue;

                string slot = field["Abil-".Length..];
                int val = ReadInt(el, $"AbilVal-{slot}");

                // TeleportRoom (140) + TeleportMap (141) collapse into one
                // destination row: "map/room (room name)".
                if (code is 140 or 141)
                {
                    if (!teleportRendered)
                    {
                        rows.Add(new GameDataInfoRow("Teleport Destination", TeleportDestination(el)));
                        teleportRendered = true;
                    }
                    continue;
                }

                // NegateAbility (124) — its value is the negated spell.
                if (code == 124)
                {
                    if (_cache.FindNameByNumber("Spells", val) is { } sp)
                        rows.Add(BuildLinkRow("Negate", "Spells", new[] { val }));
                    else if (val > 0)
                        rows.Add(new GameDataInfoRow("Negate", val.ToString(CultureInfo.InvariantCulture)));
                    continue;
                }

                // TextBlock (148) — the spell executes a TBInfo record. The bare
                // record number means nothing to the user; instead walk the
                // textblock's action chain and surface what it actually does:
                // monsters it summons, spells it casts (the damage), and the
                // item gate around them (e.g. the "silver river" room-damage
                // spell is avoided by carrying a raft; a forest room-spell
                // summons monsters unless you hold an item). AbilVal holds the
                // record number; a few spells stash it in MinBase with a zero
                // AbilVal instead.
                if (code == 148)
                {
                    int tb = val > 0 ? val : ReadInt(el, "MinBase");
                    TextblockEffects fx = WalkTextblockChain(tb);
                    if (fx.HasEffect)
                    {
                        if (fx.Summons.Count > 0)
                            rows.Add(BuildLinkRow("Summons", "Monsters", fx.Summons));
                        if (fx.Casts.Count > 0)
                            rows.Add(BuildLinkRow("Casts", "Spells", fx.Casts));
                        if (fx.Required.Count > 0)
                            rows.Add(BuildLinkRow("Requires carrying", "Items", fx.Required));
                        if (fx.Avoided.Count > 0)
                            rows.Add(BuildLinkRow("Avoided by carrying", "Items", fx.Avoided));
                    }
                    continue;
                }

                string abilName = AbilityNames.GetName(code) ?? $"Ability {code}";
                string valueText = val.ToString(CultureInfo.InvariantCulture);
                if (ResolveAbilityReference(code, val) is { } refName)
                    valueText += $" ({refName})";
                rows.Add(new GameDataInfoRow(abilName, valueText));
                continue;
            }

            // "Casted By" / "Learned From" — a comma-joined list of "<Kind> #N"
            // source tokens. Render each source as a clickable link to its record
            // instead of plain text.
            if (IsSourceListField(field))
            {
                if (BuildSourceListLinkRow(field, prop.Value) is { } srcRow) rows.Add(srcRow);
                continue;
            }

            if (RenderField(field, prop.Value) is { } rendered)
                rows.Add(new GameDataInfoRow(field, rendered));
        }

        AppendNegatedByRow(rows, spellNumber);
        return rows;
    }

    private static bool IsSourceListField(string field) =>
        string.Equals(field, "Casted By", StringComparison.OrdinalIgnoreCase)
        || string.Equals(field, "Learned From", StringComparison.OrdinalIgnoreCase);

    // The blue MdbLink Open command for a record in table (Monsters / Items /
    // Spells), routed through the same AppServices openers the item record's
    // DroppedByRow / ItemLink use. Only the lambda touches AppServices.Current —
    // deferred until a click — so building rows stays free of it.
    private static ICommand OpenCommand(string table, int number) => table switch
    {
        "Monsters" => new RelayCommand(() => AppServices.Current.OpenMonsterGameData(number)),
        "Items"    => new RelayCommand(() => AppServices.Current.OpenItemGameData(number)),
        "Spells"   => new AsyncRelayCommand(() => AppServices.Current.OpenSpellRecordAsync(number)),
        _          => new RelayCommand(() => { }),
    };

    // A row whose value is a list of record references (ids in table), each a
    // clickable link. Value keeps the plain comma-joined names as the text
    // fallback; Links carries the clickable segments with their inline "," gaps.
    private GameDataInfoRow BuildLinkRow(string label, string table, IReadOnlyList<int> ids)
    {
        var names = new List<string>(ids.Count);
        var links = new List<GameDataRecordLink>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            string name = _cache.FindNameByNumber(table, ids[i]) ?? $"{table.TrimEnd('s')} #{ids[i]}";
            names.Add(name);
            string trailing = i < ids.Count - 1 ? ", " : string.Empty;
            links.Add(new GameDataRecordLink(name, trailing, OpenCommand(table, ids[i])));
        }
        return new GameDataInfoRow(label, string.Join(", ", names), links);
    }

    // Link-rendering variant of the source-list cell: resolve each "<Kind> #N"
    // token to its record name, rendering Monsters / Items / Spells as clickable
    // links and other kinds (Room / TextBlock / Class) or unresolvable tokens as
    // inert text. Keeps the MDB's trailing "+" cap marker as a "+ more" tail.
    // Returns null when the cell is empty.
    private GameDataInfoRow? BuildSourceListLinkRow(string field, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || CleanString(value.GetString()) is not { } raw)
            return null;

        string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var names = new List<string>();
        var links = new List<GameDataRecordLink>();
        bool cappedInData = false;
        foreach (string part in parts)
        {
            if (part.Contains('+')) { cappedInData = true; continue; }   // MDB list-cap marker
            (string display, ICommand open, bool linked) = ResolveSource(part);
            if (names.Contains(display, StringComparer.OrdinalIgnoreCase)) continue;
            names.Add(display);
            links.Add(new GameDataRecordLink(display, ", ", open, linked));
        }
        if (links.Count == 0) return cappedInData ? new GameDataInfoRow(field, "+ more") : null;

        // Trim the last real link's separator, then append the cap marker as text.
        GameDataRecordLink last = links[^1];
        links[^1] = new GameDataRecordLink(last.Name, cappedInData ? ", " : string.Empty, last.Open, last.IsLinked);
        if (cappedInData) links.Add(new GameDataRecordLink("+ more", string.Empty, NoOpCommand, isLinked: false));

        string text = string.Join(", ", names) + (cappedInData ? ", + more" : string.Empty);
        return new GameDataInfoRow(field, text, links);
    }

    private static readonly ICommand NoOpCommand = new RelayCommand(() => { });

    // Resolve a "<Kind> #N" source token to its display name and open command:
    // clickable for Monsters / Items / Spells, an inert no-op for other kinds
    // (Room / TextBlock / Class) or a token that doesn't resolve (kept as text so
    // nothing is dropped).
    private (string Display, ICommand Open, bool Linked) ResolveSource(string token)
    {
        Match m = SourceToken.Match(token);
        if (m.Success
            && int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            string? table = m.Groups[1].Value.ToLowerInvariant() switch
            {
                "item"      => "Items",
                "monster"   => "Monsters",
                "spell"     => "Spells",
                "room"      => "Rooms",
                "textblock" => "TextBlocks",
                "class"     => "Classes",
                _           => null,
            };
            if (table is not null && _cache.FindNameByNumber(table, number) is { } name)
            {
                bool linked = table is "Monsters" or "Items" or "Spells";
                return (name, linked ? OpenCommand(table, number) : NoOpCommand, linked);
            }
        }
        return (token, NoOpCommand, false);
    }

    // Reverse cross-reference: the items that negate this spell. The Items table
    // carries a NegateSpell-0..9 column family (10 slots, each a Spells.Number) —
    // the game's item→spell negation relation, the inverse of the item record's
    // own "Negates" rows — so carrying such an item negates this spell against the
    // bearer. Surfacing it on the spell's own record answers "what counters this?"
    // at a glance. Read directly like the Rooms / TBInfo scans above rather than
    // reusing RoomHazardIndex's hazard-scoped copy, which isn't a reusable
    // accessor and would couple this dialog to the hazard service. Omitted when no
    // item negates the spell; one scan per dialog open (not a hot path).
    private void AppendNegatedByRow(List<GameDataInfoRow> rows, int spellNumber)
    {
        if (spellNumber <= 0) return;
        JsonDocument? doc = _cache.GetRawTable("Items");
        if (doc is null) return;

        var itemNumbers = new List<int>();
        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            bool negates = false;
            for (int i = 0; i < 10 && !negates; i++)
                negates = ReadInt(item, $"NegateSpell-{i}") == spellNumber;
            if (!negates) continue;

            int number = ReadInt(item, "Number");
            if (number > 0 && !itemNumbers.Contains(number)) itemNumbers.Add(number);
        }

        if (itemNumbers.Count > 0)
            rows.Add(BuildLinkRow("Negated by", "Items", itemNumbers));
    }

    // "Mage-1" / "Priest" — the spell's casting school with its magery-level suffix folded in
    // (suffix dropped when MageryLVL is 0). Null when the school value can't be formatted.
    private static string? MageryDisplay(JsonElement el, JsonElement mageryValue)
    {
        string? school = LookupEnums.FormatMagery(
            mageryValue.ValueKind == JsonValueKind.Number ? mageryValue.GetRawText() : null);
        if (string.IsNullOrWhiteSpace(school)) return null;
        int lvl = ReadInt(el, "MageryLVL");
        return lvl > 0 ? $"{school}-{lvl.ToString(CultureInfo.InvariantCulture)}" : school;
    }

    // The curated growth block — magnitude range ("Damage(-MR): 18 to 68"), level cap, per-level
    // growth formula ("Max: 24+(2*lvl)"), and at-cap duration.
    private static void EmitGrowthBlock(List<GameDataInfoRow> rows, SpellFormulaInput? formula)
    {
        if (formula is not { } f) return;

        if (SpellGrowthFormatter.MagnitudeRange(f) is { } range)
            rows.Add(new GameDataInfoRow(SpellGrowthFormatter.MagnitudeLabel(f), range));
        if (f.Cap > 0)
            rows.Add(new GameDataInfoRow("LVL Cap", f.Cap.ToString(CultureInfo.InvariantCulture)));
        if (SpellGrowthFormatter.GrowthFormula(f) is { } growth)
            rows.Add(new GameDataInfoRow("LVL Increases", growth));
        long durSecs = SpellGrowthFormatter.DurationSeconds(f);
        if (durSecs > 0)
            rows.Add(new GameDataInfoRow(
                "Duration",
                $"{durSecs.ToString(CultureInfo.InvariantCulture)} {(durSecs == 1 ? "second" : "seconds")}"));
    }

    // "map/room (room name)" for a teleport spell — the destination map comes from TeleportMap
    // (141), the room from TeleportRoom (140), the name resolved against the Rooms table.
    private string TeleportDestination(JsonElement el)
    {
        int map = FindAbilVal(el, 141);
        int room = FindAbilVal(el, 140);
        string dest = $"{map}/{room}";
        if (ResolveRoomName(map, room) is { } name) dest += $" ({name})";
        return dest;
    }

    private static int FindAbilVal(JsonElement el, int code)
    {
        for (int i = 0; i < 10; i++)
            if (ReadInt(el, $"Abil-{i}") == code) return ReadInt(el, $"AbilVal-{i}");
        return 0;
    }

    // Resolve a (map, room) pair to the room's Name in the Rooms table.
    private string? ResolveRoomName(int map, int room)
    {
        if (map <= 0 || room <= 0) return null;
        JsonDocument? doc = _cache.GetRawTable("Rooms");
        if (doc is null) return null;
        foreach (JsonElement r in doc.RootElement.EnumerateArray())
            if (ReadInt(r, "Map Number") == map && ReadInt(r, "Room Number") == room)
                return r.TryGetProperty("Name", out JsonElement e) && e.ValueKind == JsonValueKind.String
                    ? CleanString(e.GetString())
                    : null;
        return null;
    }

    // Render one scalar (non-ability) field: enum columns via the shared formatters, the
    // "Learned From" / "Casted By" source lists resolved to real names, blank / NUL text dropped.
    // Returns null to omit the field.
    private string? RenderField(string field, JsonElement value)
    {
        if (ColumnFormatters.TryGetValue(field, out var fmt))
        {
            string? raw = value.ValueKind switch
            {
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.String => value.GetString(),
                _ => null,
            };
            string? formatted = fmt(raw);
            return string.IsNullOrWhiteSpace(formatted) ? null : formatted;
        }

        if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        if (value.ValueKind == JsonValueKind.String) return CleanString(value.GetString());
        return null;
    }

    // Reference-bearing ability codes → the table their AbilVal points at, resolved to that row's
    // name. Null when the code isn't a reference, the value is non-positive, or no matching row.
    private string? ResolveAbilityReference(int code, int val)
    {
        if (val <= 0) return null;
        string? table = code switch
        {
            // Learn / Casts / Removes / EndCast / KillSpell / GiveTempSpell.
            42 or 43 or 122 or 151 or 153 or 160 => "Spells",
            // Summon / MonsGuards.
            12 or 146 => "Monsters",
            // ClearItem / UnEquipItem / EquipItem / NoAttackIfItemNum.
            143 or 167 or 168 or 185 => "Items",
            _ => null,
        };
        return table is null ? null : _cache.FindNameByNumber(table, val);
    }

    // Effects collected from walking a spell's TBInfo textblock chain.
    private sealed class TextblockEffects
    {
        public readonly List<int> Summons = new();   // summon N (monster numbers)
        public readonly List<int> Casts = new();     // cast N (spell numbers)
        public readonly List<int> Avoided = new();   // failitem N (carrying avoids the effect)
        public readonly List<int> Required = new();  // checkitem N (required for the effect)

        // True once the chain actually does something harmful/active — the item gates are only
        // meaningful when they guard a cast or summon.
        public bool HasEffect => Summons.Count > 0 || Casts.Count > 0;

        public static void AddUnique(List<int> list, int v) { if (!list.Contains(v)) list.Add(v); }
    }

    // Walk a spell's TBInfo textblock action chain (bounded depth, cycle-guarded) and collect what
    // it does: monsters it summons, spells it casts, and the failitem / checkitem item gates around
    // them. Chains follow random N branches and the LinkTo pointer.
    private TextblockEffects WalkTextblockChain(int rootTextblock)
    {
        var fx = new TextblockEffects();
        if (rootTextblock <= 0) return fx;

        JsonDocument? doc = _cache.GetRawTable("TBInfo");
        if (doc is null) return fx;

        var byNumber = new Dictionary<int, JsonElement>();
        foreach (JsonElement el in doc.RootElement.EnumerateArray())
        {
            int num = ReadInt(el, "Number");
            if (num > 0) byNumber.TryAdd(num, el);
        }

        const int MaxDepth = 8;
        var visited = new HashSet<int>();

        void Walk(int tb, int depth)
        {
            if (tb <= 0 || depth > MaxDepth || !visited.Add(tb)) return;
            if (!byNumber.TryGetValue(tb, out JsonElement entry)) return;

            string? action = entry.TryGetProperty("Action", out JsonElement a) && a.ValueKind == JsonValueKind.String
                ? a.GetString() : null;
            if (!string.IsNullOrEmpty(action))
            {
                foreach (string line in action.Split('\n'))
                foreach (string rawCmd in line.Split(':'))
                {
                    string[] tok = rawCmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tok.Length < 2 || !int.TryParse(tok[1], out int arg)) continue;
                    switch (tok[0].ToLowerInvariant())
                    {
                        case "summon":    TextblockEffects.AddUnique(fx.Summons, arg); break;
                        case "cast":      TextblockEffects.AddUnique(fx.Casts, arg); break;
                        case "failitem":  TextblockEffects.AddUnique(fx.Avoided, arg); break;
                        case "checkitem": TextblockEffects.AddUnique(fx.Required, arg); break;
                        case "random":    Walk(arg, depth + 1); break;
                    }
                }
            }

            int linkTo = ReadInt(entry, "LinkTo");
            if (linkTo > 0) Walk(linkTo, depth + 1);
        }

        Walk(rootTextblock, 0);

        // Item gates are only meaningful when the chain produced an active effect.
        if (!fx.HasEffect) { fx.Avoided.Clear(); fx.Required.Clear(); }
        return fx;
    }

    // "<Kind> #<number>" with any trailing chance / qualifier ("(50%)") ignored.
    private static readonly Regex SourceToken = new(@"^([A-Za-z]+)\s*#\s*(\d+)", RegexOptions.Compiled);

    // The effect formatter's bare "TextBlock 9404" fallback — suppressed in favour of the
    // walked Summons / Casts rows.
    private static readonly Regex BareTextblock = new(@"^TextBlock \d+$", RegexOptions.Compiled);

    private static int ReadInt(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement e)
           && e.ValueKind == JsonValueKind.Number
           && e.TryGetInt32(out int n) ? n : 0;

    // NUL-aware trim — the MDB importer writes a literal "\0" for empty Jet text columns, so a
    // plain GetString can hand back NUL / whitespace.
    private static string? CleanString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        foreach (char c in raw)
            if (c != '\0' && !char.IsWhiteSpace(c)) return raw.Trim();
        return null;
    }
}

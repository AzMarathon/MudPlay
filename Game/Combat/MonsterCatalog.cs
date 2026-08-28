using System.Collections.Generic;
using System.Text.Json;
using MudPlay.Game.GameData;
using MudPlay.Services;

namespace MudPlay.Game.Combat;

// One AttType-N/Att%-N/... physical, spell, or rob attack slot off a Monsters
// row. AttType: 1 Normal, 2 Spell, 3 Rob (0 = unused slot). For a spell slot
// (Type == 2), Accuracy actually holds the spell's Number, MaxDamage holds its
// cast level, and MinDamage holds its success % — the same field-reuse the real
// game data carries (see MonsterMdbInfoBuilder.ResolveSpellEffect for the
// existing decode of this). TruePercent is the per-round chance after the
// engine's own normalization; Percent is the raw stored value.
public sealed record MonsterAttackSlot(
    string Name, int Type, int Percent, double TruePercent,
    int MinDamage, int MaxDamage, int Accuracy, int Energy, int HitSpell);

// One MidSpell-N ("between rounds") slot. Percent is stored as a cumulative
// threshold across the 5 slots on the raw row — MonsterCatalog resolves this to
// the per-slot delta chance (see MonsterMdbInfoBuilder's existing identical
// decode) so callers never have to re-derive it.
public sealed record MonsterMidSpellSlot(int SpellId, int Percent, int Level);

// One DropItem-N slot.
public sealed record MonsterDropSlot(int ItemId, int Percent);

// One Abil-N/AbilVal-N slot, generic — decode via AbilityNames.GetName(Code) for
// a friendly label. Kept raw (not pre-decoded into named properties) so a new
// consumer can read any ability code without the catalog special-casing it.
public sealed record MonsterAbilitySlot(int Code, int Value);

// A single typed monster record — every raw Monsters-table field this codebase
// reads somewhere, parsed once, plus a handful of pre-resolved lookups
// (elemental resists, Magical/SpellImmu, spell-cast elements) that several
// independent indexes (MonsterResistIndex, MonsterMagicIndex, ...) otherwise
// each re-derive from the same raw abilities. See MonsterCatalog's own comment
// for why those indexes aren't retired yet.
public sealed record MonsterCatalogEntry(
    int Number,
    string Name,
    int Type,
    int Align,
    bool Undead,           // byte-boolean: raw value is 0/1/255 (MDB True = -1 = 255)
    int Exp,
    int ExpMulti,
    double RegenTime,
    int GameLimit,
    int Hp,
    int HpRegen,
    int ArmourClass,
    int DamageResist,
    int MagicRes,
    int FollowPercent,
    int CharmLevel,
    int CashRunic,
    int CashPlatinum,
    int CashGold,
    int CashSilver,
    int CashCopper,
    int Weapon,
    int CreateSpell,
    int DeathSpell,
    int GreetTextBlock,
    int BsDefense,
    int Energy,
    double AvgDamage,
    IReadOnlyList<MonsterAttackSlot> Attacks,
    IReadOnlyList<MonsterMidSpellSlot> MidSpells,
    IReadOnlyList<MonsterDropSlot> Drops,
    IReadOnlyList<MonsterAbilitySlot> Abilities,
    string SummonedBy,
    // Pre-resolved from Abilities — see MonsterResistIndex / MonsterMagicIndex
    // for the exact codes and why only these two damage flavors are
    // deterministic enough to index (elemental resist codes: Cold 3, Fire 5,
    // Stone 65, Lightning 66, Water 147; signed — negative = vulnerability).
    IReadOnlyDictionary<int, int> ElementalResists,
    int Magical,            // Abil 28 — hit-magic level a weapon must meet
    int SpellImmunity,      // Abil 139 — spell ReqLevel a cast must meet
    int Dodge,              // Abil 34
    bool NonLiving,         // Abil 109 — gates life-drain eligibility with Undead
    // Distinct elements ("Fire", "Cold", ...) this monster can cast, resolved by
    // cross-referencing each spell-attack slot's Accuracy-as-spell-Number and
    // each mid-spell slot's SpellId against Spells.AttType. New — nothing before
    // this rolled a monster's outgoing spell elements up into one place.
    IReadOnlyList<string> CastsElements)
{
    // The physical-attack accuracy summary: (majority, max) across every
    // physical/rob slot (Type 1 or 3) with a positive Percent — "majority" is
    // the slot with the highest TruePercent chance, "max" the highest Accuracy
    // value of any such slot. Null when the monster has no physical attack at
    // all (spell-only). This is a typed equivalent of
    // MonstersSectionViewModel.ComputeAttackAccuracy's existing highest-single-
    // chance logic — deliberately NOT the real upstream majority-threshold
    // formula (see the Monster Intel plan's deferred correctness item; that fix
    // is unconfirmed and out of scope here, not forgotten).
    public (int Majority, int Max)? PhysicalAccuracy
    {
        get
        {
            int majAcc = 0, maxAcc = 0;
            double bestChance = -1;
            foreach (MonsterAttackSlot a in Attacks)
            {
                if (a.Type != 1 && a.Type != 3) continue;
                if (a.Percent <= 0) continue;
                if (a.TruePercent > bestChance) { bestChance = a.TruePercent; majAcc = a.Accuracy; }
                if (a.Accuracy > maxAcc) maxAcc = a.Accuracy;
            }
            return bestChance < 0 ? null : (majAcc, maxAcc);
        }
    }
}

// Typed, parsed-once view of the active game-data set's Monsters table (cross-
// referencing Spells for the attack/mid-spell → element resolution). Built
// lazily and dropped on GameDataCache.ActiveSetChanged, mirroring every
// existing per-concern monster index (MonsterResistIndex, MonsterMagicIndex,
// MonsterHpIndex, MonsterLifeIndex, MonsterDeathSummonIndex,
// MonsterSummonTargetsIndex, Services.MonsterSpawnIndex, Services.MonsterDropIndex).
//
// This is a NEW shared read layer, not yet a replacement for those — each of
// the eight existing indexes carries its own hard-won edge-case handling
// (permissive vs strict Summoned-By token classification, MinBase-vs-AbilVal
// summon-target fallback, fail-open semantics for unknown monsters, etc.) and
// migrating all of them onto this catalog in one pass would be a much larger,
// riskier change than building the catalog itself. They're left as-is for now;
// consolidating them onto MonsterCatalog is deliberately deferred to a later,
// separately-reviewable change once this catalog's shape has proven itself
// against real consumers.
public sealed class MonsterCatalog
{
    // Elemental resist ability codes — mirrors MonsterResistIndex exactly (kept
    // in sync there rather than shared via a constants type, since the two
    // classes' Build() loops don't otherwise share code).
    private const int ResistCold = 3;
    private const int ResistFire = 5;
    private const int ResistStone = 65;
    private const int ResistLightning = 66;
    private const int ResistWater = 147;
    private const int MagicalAbilityCode = 28;
    private const int SpellImmuneAbilityCode = 139;
    private const int DodgeAbilityCode = 34;
    private const int NonLivingAbilityCode = 109;

    private const int AttackSlots = 5;
    private const int MidSpellSlots = 5;
    private const int DropSlots = 10;
    private const int AbilitySlots = 10;

    private readonly GameDataCache _cache;
    private Dictionary<int, MonsterCatalogEntry>? _byNumber;
    private Dictionary<string, List<int>>? _byName;

    public MonsterCatalog(GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _cache = cache;
        _cache.ActiveSetChanged += _ => { _byNumber = null; _byName = null; };
    }

    // The monster record for Number, or null when unknown in the active set.
    public MonsterCatalogEntry? Get(int number)
        => Build().TryGetValue(number, out MonsterCatalogEntry? e) ? e : null;

    // Every Number sharing this display name (case-insensitive) — the active
    // set has 600+ names that resolve to more than one record, so a name alone
    // never uniquely identifies a monster; callers needing the right variant
    // for a specific room use RoomAwareMonsterResolver, not this.
    public IReadOnlyList<int> NumbersByName(string name)
    {
        BuildNameIndex();
        return _byName!.TryGetValue(name, out List<int>? nums) ? nums : Array.Empty<int>();
    }

    public IReadOnlyCollection<MonsterCatalogEntry> All => Build().Values;

    private void BuildNameIndex()
    {
        if (_byName is not null) return;
        Dictionary<int, MonsterCatalogEntry> byNumber = Build();
        Dictionary<string, List<int>> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (MonsterCatalogEntry e in byNumber.Values)
        {
            if (!byName.TryGetValue(e.Name, out List<int>? nums))
                byName[e.Name] = nums = new List<int>();
            nums.Add(e.Number);
        }
        _byName = byName;
    }

    private Dictionary<int, MonsterCatalogEntry> Build()
    {
        if (_byNumber is { } cached) return cached;

        // Spell Number → AttType (element), resolved once and reused for every
        // monster's spell-attack/mid-spell slots — the same cross-reference
        // MonsterMdbInfoBuilder.ResolveSpellEffect and MonsterSummonTargetsIndex
        // already perform independently.
        Dictionary<int, int> spellAttType = new();
        JsonDocument? spells = _cache.GetRawTable("Spells");
        if (spells is not null)
            foreach (JsonElement row in spells.RootElement.EnumerateArray())
                if (TryInt(row, "Number", out int spellNum))
                    spellAttType[spellNum] = TryInt(row, "AttType", out int at) ? at : -1;

        Dictionary<int, MonsterCatalogEntry> map = new();
        JsonDocument? doc = _cache.GetRawTable("Monsters");
        if (doc is not null)
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
                if (TryInt(row, "Number", out int number))
                    map[number] = BuildEntry(row, number, spellAttType);

        _cache.EvictTable("Monsters");
        _cache.EvictTable("Spells");
        _byNumber = map;
        return map;
    }

    private static MonsterCatalogEntry BuildEntry(
        JsonElement row, int number, IReadOnlyDictionary<int, int> spellAttType)
    {
        List<MonsterAttackSlot> attacks = new();
        for (int i = 0; i < AttackSlots; i++)
        {
            int type = ReadInt(row, $"AttType-{i}");
            if (type is < 1 or > 3) continue;
            attacks.Add(new MonsterAttackSlot(
                Name: ReadString(row, $"AttName-{i}"),
                Type: type,
                Percent: ReadInt(row, $"Att%-{i}"),
                TruePercent: ReadDouble(row, $"AttTrue%-{i}"),
                MinDamage: ReadInt(row, $"AttMin-{i}"),
                MaxDamage: ReadInt(row, $"AttMax-{i}"),
                Accuracy: ReadInt(row, $"AttAcc-{i}"),
                Energy: ReadInt(row, $"AttEnergy-{i}"),
                HitSpell: ReadInt(row, $"AttHitSpell-{i}")));
        }

        // MidSpell%-N is a cumulative threshold across the 5 slots; resolve to
        // the per-slot delta once here (mirrors MonsterMdbInfoBuilder's existing
        // identical decode) so every consumer reads the real per-slot chance.
        List<MonsterMidSpellSlot> midSpells = new();
        int cumulative = 0;
        for (int i = 0; i < MidSpellSlots; i++)
        {
            int spellId = ReadInt(row, $"MidSpell-{i}");
            if (spellId == 0) continue;
            int threshold = ReadInt(row, $"MidSpell%-{i}");
            int delta = threshold - cumulative;
            cumulative = threshold;
            midSpells.Add(new MonsterMidSpellSlot(spellId, delta, ReadInt(row, $"MidSpellLVL-{i}")));
        }

        List<MonsterDropSlot> drops = new();
        for (int i = 0; i < DropSlots; i++)
        {
            int itemId = ReadInt(row, $"DropItem-{i}");
            if (itemId == 0) continue;
            drops.Add(new MonsterDropSlot(itemId, ReadInt(row, $"DropItem%-{i}")));
        }

        List<MonsterAbilitySlot> abilities = new();
        Dictionary<int, int> resists = new();
        int magical = 0, spellImmune = 0, dodge = 0;
        bool nonLiving = false;
        for (int i = 0; i < AbilitySlots; i++)
        {
            int code = ReadInt(row, $"Abil-{i}");
            if (code == 0) continue;
            int val = ReadInt(row, $"AbilVal-{i}");
            abilities.Add(new MonsterAbilitySlot(code, val));

            if (IsElementalResistCode(code) && val != 0) resists[code] = val;
            else if (code == MagicalAbilityCode) magical = val;
            else if (code == SpellImmuneAbilityCode) spellImmune = val;
            else if (code == DodgeAbilityCode) dodge = val;
            else if (code == NonLivingAbilityCode) nonLiving = true;
        }

        // Spell-cast elemental rollup: every spell-attack slot's Accuracy field
        // (which holds a spell Number for Type == 2) and every mid-spell slot's
        // SpellId, resolved through the AttType table built once in Build().
        // Excludes AttType 4 (Normal/Magic-Resist-gated, not an "element" in the
        // deterministic-resist sense) and unresolved (-1) spells; a Poison
        // (6) cast is still surfaced, matching LookupEnums.FormatSpellAttackType's
        // own naming.
        HashSet<string> castsElements = new(StringComparer.Ordinal);
        foreach (MonsterAttackSlot a in attacks)
            if (a.Type == 2 && spellAttType.TryGetValue(a.Accuracy, out int at))
                AddElement(castsElements, at);
        foreach (MonsterMidSpellSlot m in midSpells)
            if (spellAttType.TryGetValue(m.SpellId, out int at))
                AddElement(castsElements, at);

        return new MonsterCatalogEntry(
            Number: number,
            Name: ReadString(row, "Name"),
            Type: ReadInt(row, "Type"),
            Align: ReadInt(row, "Align"),
            Undead: ReadInt(row, "Undead") != 0,   // MDB stores True as -1 (255) — never == 1
            Exp: ReadInt(row, "EXP"),
            ExpMulti: ReadInt(row, "ExpMulti"),
            RegenTime: ReadDouble(row, "RegenTime"),
            GameLimit: ReadInt(row, "GameLimit"),
            Hp: ReadInt(row, "HP"),
            HpRegen: ReadInt(row, "HPRegen"),
            ArmourClass: ReadInt(row, "ArmourClass"),
            DamageResist: ReadInt(row, "DamageResist"),
            MagicRes: ReadInt(row, "MagicRes"),
            FollowPercent: ReadInt(row, "Follow%"),
            CharmLevel: ReadInt(row, "CharmLVL"),
            CashRunic: ReadInt(row, "R"),
            CashPlatinum: ReadInt(row, "P"),
            CashGold: ReadInt(row, "G"),
            CashSilver: ReadInt(row, "S"),
            CashCopper: ReadInt(row, "C"),
            Weapon: ReadInt(row, "Weapon"),
            CreateSpell: ReadInt(row, "CreateSpell"),
            DeathSpell: ReadInt(row, "DeathSpell"),
            GreetTextBlock: ReadInt(row, "GreetTXT"),
            BsDefense: ReadInt(row, "BSDefense"),
            Energy: ReadInt(row, "Energy"),
            AvgDamage: ReadDouble(row, "AvgDmg"),
            Attacks: attacks,
            MidSpells: midSpells,
            Drops: drops,
            Abilities: abilities,
            SummonedBy: ReadString(row, "Summoned By"),
            ElementalResists: resists,
            Magical: magical,
            SpellImmunity: spellImmune,
            Dodge: dodge,
            NonLiving: nonLiving,
            CastsElements: new List<string>(castsElements));
    }

    private static void AddElement(HashSet<string> into, int attType)
    {
        if (attType is < 0 or 4) return;   // unresolved, or Normal/Magic-Resist — not an elemental cast
        if (LookupEnums.FormatSpellAttackType(attType.ToString(Inv)) is { } name
            && !name.StartsWith("Unknown(", StringComparison.Ordinal))
            into.Add(name);
    }

    private static bool IsElementalResistCode(int code) =>
        code is ResistCold or ResistFire or ResistStone or ResistLightning or ResistWater;

    private static readonly System.Globalization.CultureInfo Inv =
        System.Globalization.CultureInfo.InvariantCulture;

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

    private static string ReadString(JsonElement el, string field)
    {
        if (!el.TryGetProperty(field, out JsonElement v)) return string.Empty;
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
    }

    private static bool TryInt(JsonElement row, string prop, out int value)
    {
        value = 0;
        return row.TryGetProperty(prop, out JsonElement el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out value);
    }
}

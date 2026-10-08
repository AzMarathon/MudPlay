using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Cash;
using MudPlay.Game.GameData;
using MudPlay.Game.Inventory;
using MudPlay.Game.Quests;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

// CHARACTER INFO section — the live stat sheet:
//   Box A — Base Stats from the last `stat` snapshot (PlayerStats). Mana relabels
//     to Kai for Mystic classes.
//   Box C — Derived combat accuracy (Attack / Bash / Smash / Backstab) from
//     CombatCalculator. The aggregate it consumes additionally folds in innate
//     race + class ability bonuses and the permanent rewards of completed quests
//     (published by the Quest Status tab via QuestBonusState); Smash shows only
//     for smash-capable classes, Backstab only when the character has stealth.
//   Quest Bonuses — a flat readout of every completed quest's permanent stat
//     reward, aggregated by ability. Empty when no completed quest grants a bonus.
//   Inventory — the full carry list harvested from the last `i` dump: every worn
//     item (with its slot) and every carried-but-unworn item.
// Every readout recomputes live — base stats when the `stat` snapshot changes,
// encumbrance / currency / inventory when the `i` dump changes, alignment on a
// `who` refresh, quest bonuses when the completed-quest set changes. There is no
// manual refresh; the panel always mirrors the live state.
public sealed partial class CharacterInfoSectionViewModel : WorkshopSectionViewModel
{
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly InventoryManager _inventory;
    private readonly PlayerDatabase _playerDb;
    private readonly AlignmentTracker _alignmentTracker;
    private readonly QuestBonusState _questBonuses;
    // Shows the Workshop's Chest Offload tab, for the chest icon on a carried container.
    private readonly Action? _openChestOffload;
    // Resolves the per-BBS runic word for the carried-coins readout.
    private readonly CurrencyNaming _naming;
    // Realm-aware limited-use charge lookup (Paradigm look counts / stock counted-uses).
    private readonly Game.Inventory.CarriedChargeReadout _charges;
    // A gear-set swap streams a dozen-plus wear/rem confirmations, each firing
    // InventoryManager.Changed — and they keep arriving AFTER the EquipmentManager's
    // send window closes. Rebuilding the derived-stat + wealth + equipped-list
    // readouts on every one rebuilds the whole EquippedItems collection a dozen times
    // in a burst, re-laying-out the visible Player Info list and lagging the workshop
    // while it's open on that tab. Coalesce it: each change (re)starts this timer and
    // one refresh runs once the burst goes quiet.
    private readonly DispatcherTimer _inventoryRefreshDebounce;
    private Control? _view;
    private StatBreakpointsWindow? _breakpointsWindow;

    public override string Id => "characterinfo";
    public override string Title => "Character Info";
    public override Control View => _view ??= new CharacterInfoSectionView { DataContext = this };

    // ----- Box A: base stats (mirrors the in-game `stat` grid) -----------
    [ObservableProperty] private string _name = "—";
    [ObservableProperty] private string _race = "—";
    [ObservableProperty] private string _charClass = "—";
    [ObservableProperty] private int _level;
    [ObservableProperty] private long _exp;
    [ObservableProperty] private int _lives;
    [ObservableProperty] private int _cp;
    [ObservableProperty] private string _hits = "—";
    // "Mana" for casters, "Kai" for Mystic (magery type 5) classes.
    [ObservableProperty] private string _manaLabel = "Mana";
    [ObservableProperty] private string _manaValue = "—";
    [ObservableProperty] private string _armourClass = "—";

    [ObservableProperty] private int _strength;
    [ObservableProperty] private int _intellect;
    [ObservableProperty] private int _willpower;
    [ObservableProperty] private int _agility;
    [ObservableProperty] private int _health;
    [ObservableProperty] private int _charm;

    [ObservableProperty] private int _perception;
    [ObservableProperty] private int _stealth;
    [ObservableProperty] private int _thievery;
    [ObservableProperty] private int _traps;
    [ObservableProperty] private int _picklocks;
    [ObservableProperty] private int _tracking;
    [ObservableProperty] private int _martialArts;
    [ObservableProperty] private int _magicRes;
    [ObservableProperty] private int _spellcasting;

    // ----- Quest Bonuses: completed-quest permanent rewards --------------
    // One row per ability granted by a completed quest, summed across quests.
    public ObservableCollection<EquipBonusRow> QuestBonusRows { get; } = new();
    // False when no completed quest grants a bonus — drives the empty-state hint.
    [ObservableProperty] private bool _hasQuestBonuses;

    // ----- Box C: derived combat -----------------------------------------
    [ObservableProperty] private string _attackAccuracy = "—";
    [ObservableProperty] private string _bashAccuracy = "—";
    [ObservableProperty] private string _smashAccuracy = "—";
    [ObservableProperty] private string _backstabAccuracy = "—";
    // Normal-attack damage range ("min-max") for the equipped weapon; em-dash when unarmed.
    [ObservableProperty] private string _attackDamage = "—";
    // Bash damage range ("min-max") for the equipped weapon; em-dash when unarmed.
    [ObservableProperty] private string _bashDamage = "—";
    // Smash damage range ("min-max") for the equipped weapon; em-dash when unarmed or not smash-capable.
    [ObservableProperty] private string _smashDamage = "—";
    // Backstab damage range ("min-max") for the equipped weapon; empty when not stealth-capable.
    [ObservableProperty] private string _backstabDamage = string.Empty;

    // Regen per tick from the stat formulas, with the realm's tick timing in the
    // tooltips. Meditate shows only once the Meditate quest is ticked complete.
    [ObservableProperty] private string _hpRegen = "—";
    [ObservableProperty] private string? _hpRegenTip;
    [ObservableProperty] private string _manaRegen = "—";
    [ObservableProperty] private string? _manaRegenTip;
    [ObservableProperty] private bool _showManaRegen;

    // The chance each utility skill gives, from the value shown (user, 2026-09-30).
    [ObservableProperty] private string? _stealthTip;
    [ObservableProperty] private string? _thieveryTip;
    [ObservableProperty] private string? _trapsTip;
    [ObservableProperty] private string? _trackingTip;
    [ObservableProperty] private string? _magicResTip;

    // How each attack row was worked out — the inputs, and which of them came from
    // gear and which from the buffs being cast on us.
    [ObservableProperty] private string? _attackTip;
    [ObservableProperty] private string? _bashTip;
    [ObservableProperty] private string? _backstabTip;
    // Swings/round per attack, from the MajorMUD energy budget
    // (CombatCalculator.CalcSwings): weapon speed + level + class CombatLVL +
    // agility + strength-vs-StrReq + encumbrance. Bash doubles energy per swing
    // (≈half the swings), Smash locks the round to a single swing, and a backstab
    // is always one strike.
    [ObservableProperty] private string _attackSwings = "—";
    [ObservableProperty] private string _bashSwings = "—";
    [ObservableProperty] private string _smashSwings = "—";
    [ObservableProperty] private string _backstabSwings = string.Empty;
    // Smash row visible only for smash-capable classes.
    [ObservableProperty] private bool _showSmash;
    // Backstab row visible only when the character has innate (race or class) stealth.
    [ObservableProperty] private bool _showBackstab;

    // Martial-arts attacks (Mystic). Punch / Kick / Jumpkick accuracy + damage.
    [ObservableProperty] private string _punchAccuracy = "—";
    [ObservableProperty] private string _punchDamage = "—";
    [ObservableProperty] private string _kickAccuracy = "—";
    [ObservableProperty] private string _kickDamage = "—";
    [ObservableProperty] private string _jumpKickAccuracy = "—";
    [ObservableProperty] private string _jumpKickDamage = "—";
    [ObservableProperty] private string _punchSwings = "—";
    [ObservableProperty] private string _kickSwings = "—";
    [ObservableProperty] private string _jumpKickSwings = "—";
    // Martial-arts strike rows are gated per-attack on the class innately granting
    // that strike (Mystic carries Punch / Kick / Jumpkick); a trained Martial Arts
    // skill alone doesn't grant the special strikes, so it no longer drives these.
    [ObservableProperty] private bool _showPunch;
    [ObservableProperty] private bool _showKick;
    [ObservableProperty] private bool _showJumpKick;

    // ----- Box A: alignment standing -------------------------------------
    // Rendered on the last row of Box A (where the game prints "You are
    // <standing>."). Alignment is really a numeric "evil points" stat; the title is just the
    // band `who` reports for it. We can't read exact EP in Stock, so we echo
    // the observed title verbatim (it's realm-specific via a modified helpfile,
    // so no fixed word ladder is hardcoded). Item alignment restrictions are a
    // richer flag set (good-only / no-good / neutral-only / evil-only / no-evil
    // / Abil-98 EP-range) handled by the Equipment Manager filter, not here.
    // Alignment title from our own `who` observation, or "—" when unseen.
    [ObservableProperty] private string _alignment = "—";
    // True after "A dark cloud passes over you" (alignment dropped) until the next
    // `who` refresh — drives the "(stale)" hint next to Alignment.
    [ObservableProperty] private bool _alignmentStale;

    // ----- Box A: carry weight + carried currency ------------------------
    // Both are inventory-sourced (InventoryManager's snapshot), refreshed live
    // on every `i` dump or incremental coin line — no manual re-pull needed.
    // Current / max carry weight from the last inventory reading ("cur / max").
    [ObservableProperty] private string _encumbrance = "—";
    // Per-denomination coins currently carried (nonzero only), or "none".
    [ObservableProperty] private string _currencyHeld = "—";
    // Consolidated wealth in copper farthings (matches the game's `Wealth:` line).
    [ObservableProperty] private string _totalWealth = "—";

    // AC / DR split by source, under the wealth block. Gear = worn armour + items;
    // Buffs = the character's configured self-applicable buffs assumed up (the same
    // shared BuffDefenseCalculator the Monster Intel matchup + Equipment Manager use,
    // so the "as if buffed" numbers agree everywhere).
    [ObservableProperty] private string _gearDefense = "—";
    [ObservableProperty] private string _spellDefense = "—";

    // ----- Inventory: the full carry list from the last `i` dump ---------
    // Worn items split into name + parenthesized slot so the view can align every
    // slot flag in a shared column (like the in-game `look self`), rather than
    // letting each "(Slot)" trail its own name at a ragged offset.
    public ObservableCollection<WorkshopItemRow> EquippedItems { get; } = new();
    // Carried-but-unworn items harvested from the last inventory dump.
    public ObservableCollection<WorkshopItemRow> CarriedItems { get; } = new();
    // Key-ring contents from the dump's "You have the following keys: …" trailer.
    // The game tracks keys apart from the pack, so they get their own list in the
    // Inventory box rather than mixing into CarriedItems. Keys are items too, so
    // they link to their Game Data record the same way.
    public ObservableCollection<WorkshopItemRow> Keys { get; } = new();
    // True once at least one worn item is known — gates the equipped list.
    [ObservableProperty] private bool _hasEquipped;
    // True once at least one carried item is known — gates the carried list.
    [ObservableProperty] private bool _hasCarried;
    // True once at least one key is known — gates the keys list in the Inventory box.
    [ObservableProperty] private bool _hasKeys;
    // False until the first `i` dump is parsed — drives the "type i to load" hint.
    [ObservableProperty] private bool _inventoryLoaded;

    public CharacterInfoSectionViewModel(PlayerStats stats, GameDataCache gameData, InventoryManager inventory, PlayerDatabase playerDb, AlignmentTracker alignmentTracker, QuestBonusState questBonuses, CurrencyNaming naming, Game.Inventory.CarriedChargeReadout charges,
        Action? openChestOffload = null)
    {
        _openChestOffload = openChestOffload;
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(playerDb);
        ArgumentNullException.ThrowIfNull(alignmentTracker);
        ArgumentNullException.ThrowIfNull(questBonuses);
        ArgumentNullException.ThrowIfNull(naming);
        ArgumentNullException.ThrowIfNull(charges);
        _stats = stats;
        _gameData = gameData;
        _inventory = inventory;
        _playerDb = playerDb;
        _alignmentTracker = alignmentTracker;
        _questBonuses = questBonuses;
        _naming = naming;
        _charges = charges;

        _inventoryRefreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _inventoryRefreshDebounce.Tick += (_, _) =>
        {
            _inventoryRefreshDebounce.Stop();
            RefreshDerived();
            RefreshWealth();
            RefreshInventory();
        };

        _stats.PropertyChanged += OnStatsChanged;
        _inventory.Changed += OnInventoryChanged;
        _charges.Changed += OnItemChargesChanged;
        _playerDb.Players.CollectionChanged += OnPlayersChanged;
        _alignmentTracker.StaleChanged += OnAlignmentStaleChanged;
        _questBonuses.Changed += OnQuestBonusesChanged;
        Refresh();
    }

    // Re-pull every live readout: base stats + derived combat from the stat
    // snapshot, alignment from `who`, and wealth + full carry list from the
    // last `i` dump. Wired to every source's change event — no manual refresh.
    private void Refresh()
    {
        RefreshBaseStats();
        RefreshDerived();
        RefreshAlignment();
        RefreshWealth();
        RefreshDefense();
        RefreshInventory();
    }

    // The character's AC / DR split by source, surfaced under the wealth block:
    // what worn gear grants, then — assuming the configured self-buffs are up —
    // what those buffs add on top. The buff side runs through the shared
    // BuffDefenseCalculator (the same "everything that lands on you" roster the
    // Monster Intel matchup and Equipment Manager use), so the numbers agree.
    private void RefreshDefense()
    {
        EquipmentStatBreakdown gear = CharacterCalculator.AggregateEquipmentStats(
            _inventory.Snapshot.EquippedItems, _gameData);
        GearDefense = FormatDefense(gear.Totals.PlusAC, gear.Totals.PlusDR);

        MudPlay.Game.Spells.BuffDefense buff = MudPlay.Game.Spells.BuffDefenseCalculator.Compute(
            AppServices.Current.Profile.Current?.PartyBuffs, _stats.Level,
            AppServices.Current.Spellbook.Available);
        SpellDefense = FormatDefense(buff.Ac, buff.Dr);
    }

    // "AC +45  ·  DR +3.2", with a plain 0 per component when nothing contributes.
    private static string FormatDefense(double ac, double dr)
        => $"AC {ac.ToString("+0;-0;0", CultureInfo.InvariantCulture)}" +
           $"  ·  DR {dr.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)}";

    // ----- Box A ----------------------------------------------------------

    private void RefreshBaseStats()
    {
        Name = Display(_stats.Name);
        Race = Display(_stats.Race);
        CharClass = Display(_stats.Class);
        Level = _stats.Level;
        Exp = _stats.Exp;
        Lives = _stats.Lives;
        Cp = _stats.Cp;
        Hits = $"{_stats.Hits}/{_stats.MaxHits}";

        // Mana vs Kai: Mystic classes (MageryType 5) carry Kai, not Mana.
        JsonElement? classRow = _gameData.FindRowByName("Classes", _stats.Class);
        bool isKai = GetInt(classRow, "MageryType") == 5;
        ManaLabel = isKai ? "Kai" : "Mana";
        ManaValue = isKai
            ? $"{_stats.Kai}/{_stats.MaxKai}"
            : $"{_stats.Mana}/{_stats.MaxMana}";

        ArmourClass = $"{_stats.ArmourClass}/{_stats.MaxArmourClass}";

        Strength = _stats.Strength;
        Intellect = _stats.Intellect;
        Willpower = _stats.Willpower;
        Agility = _stats.Agility;
        Health = _stats.Health;
        Charm = _stats.Charm;

        Perception = _stats.Perception;
        Stealth = _stats.Stealth;
        Thievery = _stats.Thievery;
        Traps = _stats.Traps;
        Picklocks = _stats.Picklocks;
        Tracking = _stats.Tracking;
        MartialArts = _stats.MartialArts;
        MagicRes = _stats.MagicRes;
        Spellcasting = _stats.Spellcasting;
    }

    // ----- Box B + C ------------------------------------------------------

    private void RefreshDerived()
    {
        IReadOnlyList<EquippedItem> worn = _inventory.Snapshot.EquippedItems;

        // Box C consumes a COMBINED aggregate: worn gear plus the character's
        // innate race + class ability bonuses, which the in-game accuracy
        // formulas account for.
        EquipmentStatBreakdown combined = CharacterCalculator.AggregateEquipmentStats(worn, _gameData);
        JsonElement? classRow = _gameData.FindRowByName("Classes", _stats.Class);
        JsonElement? raceRow = _gameData.FindRowByName("Races", _stats.Race);
        if (raceRow is JsonElement r) CharacterCalculator.ApplyAbilityBonuses(combined, r, _stats.Race);
        if (classRow is JsonElement c) CharacterCalculator.ApplyAbilityBonuses(combined, c, _stats.Class);

        // Completed quests grant permanent stat rewards the same accuracy/damage
        // formulas account for — fold them into the combined aggregate (never Box B,
        // which is equipment-only) and surface them in their own readout.
        CharacterCalculator.ApplyQuestBonuses(combined, _questBonuses.Bonuses, "Quests");
        RebuildQuestBonusRows();

        ComputeDerivedCombat(combined.Totals, classRow, raceRow);
        ComputeRegen(combined.Totals, classRow);
        ComputeSkillChances();
        ComputeMagicResTip();
    }

    // HP and mana per regen tick (CharacterCalculator), with when each tick lands
    // (GAME_MECHANICS "Rest and meditate tick timing").
    private void ComputeRegen(EquipmentStatSummary t, JsonElement? classRow)
    {
        RealmType realm = _gameData.ActiveRealm;
        int level = _stats.Level;
        if (level <= 0)
        {
            HpRegen = ManaRegen = "—";
            HpRegenTip = ManaRegenTip = null;
            ShowManaRegen = false;
            return;
        }

        int idle = CharacterCalculator.CalcHpRegen(level, _stats.Health, t.HpRegenPercent, isResting: false, realm);
        int rest = CharacterCalculator.CalcHpRegen(level, _stats.Health, t.HpRegenPercent, isResting: true, realm);
        if (realm == RealmType.ParaMud)
        {
            // Paradigm pays the 30 s amount in thirds, and a rest's full gain is that
            // whole amount, not three times it. The thirds are of the amount before
            // the HP-regen bonus; what the bonus adds comes on the last of each three.
            int unscaled = CharacterCalculator.CalcHpRegen(level, _stats.Health, 0, isResting: false, realm);
            int third = CharacterCalculator.ParadigmHpRegenThird(unscaled);
            int extra = idle - unscaled;
            string lows = extra != 0 ? $"+{third}, +{third}, +{third + extra}" : $"three of +{third}";
            HpRegen = $"+{third} / +{idle}";
            HpRegenTip = (extra != 0
                    ? $"Standing: {lows} over 30 s, one every 10 s (the last with the mana tick).\n"
                    : $"Standing: +{third} HP every 10 s.\n")
                + $"Resting: a gain every 5 s — {lows}, then three of +{idle}, and round again. "
                + "It counts from when you lie down, so resting again starts back at the small gains.";
        }
        else
        {
            HpRegen = $"+{idle} / +{rest}";
            HpRegenTip = $"Standing: +{idle} HP every 30 s.\nResting: that tick keeps paying, plus +{rest} every 21 s.";
        }

        int mageryType = GetInt(classRow, "MageryType"), mageryLevel = GetInt(classRow, "MageryLVL");
        ShowManaRegen = mageryType > 0;
        if (!ShowManaRegen) { ManaRegen = "—"; ManaRegenTip = null; return; }
        int passive = CharacterCalculator.CalcManaRegen(level, _stats.Intellect, _stats.Willpower, _stats.Charm,
            mageryType, mageryLevel, t.MpRegenPercent, isMeditating: false, realm);
        string passiveLine = $"Every 30 s: +{passive} {ManaLabel.ToLowerInvariant()}.";
        if (MeditateLearned())
        {
            int meditate = CharacterCalculator.CalcManaRegen(level, _stats.Intellect, _stats.Willpower, _stats.Charm,
                mageryType, mageryLevel, t.MpRegenPercent, isMeditating: true, realm);
            ManaRegen = $"+{passive} / +{meditate}";
            ManaRegenTip = passiveLine + $"\nMeditating: +{meditate} every 15 s, on top of the 30 s tick.";
        }
        else
        {
            ManaRegen = $"+{passive}";
            ManaRegenTip = passiveLine + "\nMeditate shows once its quest is ticked complete on the Quests tab.";
        }
    }

    // The Meditate quest (Quests tab, by name) ticked complete for this character.
    private static bool MeditateLearned()
    {
        HashSet<int> flags = AppServices.Current.Quests.NamedQuests()
            .Where(q => string.Equals(q.Name.Trim(), "Meditate", StringComparison.OrdinalIgnoreCase))
            .Select(q => q.Flag).ToHashSet();
        return flags.Count > 0
            && AppServices.Current.Profile.Current?.QuestLog?.Any(p => p.Complete && flags.Contains(p.Flag)) == true;
    }

    // What each shown skill value comes to as a chance: the result, any cap, and any
    // penalty that applies (GAME_MECHANICS "Sneaking", "Robbing players", "Exit traps",
    // "Tracking"; Stock rules, assumed for Paradigm).
    private void ComputeSkillChances()
    {
        int stealth = _stats.Stealth;
        EncumbranceReading encum = _inventory.Snapshot.Encumbrance;
        int encPct = encum.MaxWeight > 0 ? encum.CurrentWeight * 100 / encum.MaxWeight : 0;
        int encPenalty = Game.Stealth.SneakChance.EncumbrancePenalty(encPct);
        int chance = Math.Max(0, stealth - encPenalty);
        // A move re-rolls over 0–101 rather than `sn`'s 0–100, so even at its cap of
        // 100 a sneaked move keeps the sneak 100 in 101.
        int keep = Math.Min(chance, 100) * 100 / 101;
        // A completed Perfect Stealth quest (ability 186) makes every sneak take and
        // hold, penalties or not — as the Level Projection counts it.
        bool perfect = _questBonuses.AbilityAwards.Any(award =>
            award.AbilityId == Game.Stealth.SneakChance.PerfectStealthAbility && _stats.Level >= award.FromLevel);
        List<string> stealthLines = perfect
            ? new() { "Start sneaking (sn): 100% (Perfect Stealth)", "Keep it each move: 100% (Perfect Stealth)" }
            : new()
            {
                $"Start sneaking (sn): {Capped(chance, 95)}",
                $"Keep it each move: {keep}%" + (chance > 100 ? $" ({chance}, capped at 100)" : ""),
                "−1 per player or monster in the room",
            };
        if (!perfect && encPenalty > 0) stealthLines.Add($"−{encPenalty} for carrying {encPct}%");
        StealthTip = stealth <= 0 && !perfect ? null : string.Join("\n", stealthLines);

        // The rob roll is 1–99 against Thievery on Stock: at or under it succeeds, up
        // to 10 over is a quiet fail, beyond that you're caught. (Paradigm's roll isn't
        // known; it keeps the plain out-of-100 figures.)
        int thievery = _stats.Thievery;
        int faces = _gameData.ActiveRealm == RealmType.ParaMud ? 100 : 99;
        ThieveryTip = thievery <= 0 ? "Rob: 0%" : string.Join("\n",
            $"Rob: {Math.Min(thievery, faces) * 100.0 / faces:0}%" + (thievery > 100 ? $" ({thievery}, capped at 100)" : ""),
            $"Quiet fail: {Math.Clamp(faces - thievery, 0, 10) * 100.0 / faces:0}%",
            $"Caught: {Math.Max(0, faces - 10 - thievery) * 100.0 / faces:0}%");

        int traps = _stats.Traps;
        TrapDisarmOdds? disarm = AppServices.Current.TrapDisarm.DisarmOdds;
        TrapsTip = traps <= 0 ? "Find: 0%" : string.Join("\n",
            $"Find: {Capped(traps, 100)}",
            disarm is { } d ? $"Disarm: {d.Disarm}%  ·  safe fail {d.SafeMiss}%  ·  trap fires {d.Springs}%" : "Disarm: —");

        int tracking = _stats.Tracking;
        TrackingTip = $"Track: {Capped(Math.Max(0, tracking), 100)} per trail step";
    }

    // What Magic Res comes to against a monster's spell: the change in damage taken
    // and the chance to resist it outright (GAME_MECHANICS "Magic Resist (M.R.) and
    // `TypeOfResists`").
    private void ComputeMagicResTip()
    {
        int mr = _stats.MagicRes;
        if (mr <= 0) { MagicResTip = null; return; }
        RealmType realm = _gameData.ActiveRealm;
        bool antimagic = ItemEquipFilter.ResolveClassProfile(_gameData, _stats.Class).AntiMagic;
        double change = SpellDamageCalculator.PlayerMagicResistDamagePercent(mr, antimagic, realm);
        int cutCap = antimagic ? 75 : 50;
        int rawCut = SpellDamageCalculator.UncappedMagicResistCut(mr, antimagic);
        string damage = change == 0 ? "no change"
            : (change < 0 ? "−" : "+") + Math.Abs(change).ToString("0.#", CultureInfo.InvariantCulture) + "%"
              + (rawCut > cutCap ? $" ({rawCut}, capped at {cutCap})" : "");
        int resistCap = SpellDamageCalculator.PlayerFullResistCap(realm);
        double resist = SpellDamageCalculator.PlayerFullResistChance(mr, realm);
        MagicResTip = string.Join("\n",
            $"Spell damage taken: {damage}" + (antimagic ? " (AntiMagic)" : ""),
            $"Resist a spell outright: {resist:0}%" + (mr / 2 > resistCap ? $" ({mr / 2}, capped at {resistCap})" : ""),
            "Only for spells that magic resistance works on");
    }

    // "95% (105, capped at 95)" — the chance, and the raw figure when a cap cut it.
    private static string Capped(int value, int cap) =>
        value > cap ? $"{cap}% ({value}, capped at {cap})" : $"{value}%";
    // Aggregate the published completed-quest bonuses by ability id (quests stack,
    // so a stat granted by two quests sums) into the Quest Bonuses box rows.
    private void RebuildQuestBonusRows()
    {
        QuestBonusRows.Clear();
        var byAbil = new Dictionary<int, int>();
        foreach (QuestBonus b in _questBonuses.Bonuses)
        {
            if (b.AbilityId <= 0 || b.Value == 0) continue;
            byAbil[b.AbilityId] = byAbil.TryGetValue(b.AbilityId, out int v) ? v + b.Value : b.Value;
        }
        foreach (KeyValuePair<int, int> kv in byAbil.OrderBy(p => p.Key))
        {
            if (kv.Value == 0) continue;
            string display = kv.Value.ToString("+0;-0", CultureInfo.InvariantCulture);
            QuestBonusRows.Add(new EquipBonusRow(AbilityNames.FormatId(kv.Key), display, null));
        }
        HasQuestBonuses = QuestBonusRows.Count > 0;
    }

    private void ComputeDerivedCombat(EquipmentStatSummary gear, JsonElement? classRow, JsonElement? raceRow)
    {
        RealmType realm = _gameData.ActiveRealm;
        int level = _stats.Level;

        // The buffs being cast on us count as the game counts them in `stat all`
        // (report: smite's +max damage and shadowform's BS bonuses were missing).
        // Their values are rolled per cast, so each row is worked out at both ends of
        // the rolls and shown as a pair where they differ ("10-20/21").
        MudPlay.Models.Profile.BuffSettings? buffSlots = AppServices.Current.Profile.Current?.PartyBuffs;
        IReadOnlyList<MudPlay.Game.Spells.KnownSpell> known = AppServices.Current.Spellbook.Available;
        bool inParty = AppServices.Current.PartyState.IsInParty;
        MudPlay.Game.Spells.BuffCombat buff = MudPlay.Game.Spells.BuffCombatCalculator.Compute(
            buffSlots, level, known, inParty);
        MudPlay.Game.Spells.BuffCombat buffLow = MudPlay.Game.Spells.BuffCombatCalculator.Compute(
            buffSlots, level, known, inParty, lowest: true);
        EquipmentStatSummary t = WithBuffs(gear, buff);
        EquipmentStatSummary tLow = WithBuffs(gear, buffLow);
        int nCombatLevel = GetInt(classRow, "CombatLVL");
        int str = _stats.Strength, agi = _stats.Agility, intel = _stats.Intellect, chm = _stats.Charm;

        EncumbranceReading encum = _inventory.Snapshot.Encumbrance;
        int encumCur = encum.CurrentWeight, encumMax = encum.MaxWeight;

        // Abil 22/105/106 accuracy: ParaMUD sums all sources, Stock takes the
        // single highest. PlusAccuracy holds the sum; MaxSingleAbil22 the max.
        int effectiveAbil22 = realm == RealmType.ParaMud ? t.PlusAccuracy : t.MaxSingleAbil22;

        HashSet<string>? smashClasses = ClassCapabilities.GetSmashCapableClasses(_gameData);
        bool canSmash = smashClasses is null
            || (!string.IsNullOrEmpty(_stats.Class) && smashClasses.Contains(_stats.Class));
        ShowSmash = canSmash;

        if (level > 0 && nCombatLevel > 0)
        {
            string AccPair(MudAttackType type) => Pair(
                Acc(type, realm, level, nCombatLevel, str, agi, intel, chm, tLow, encumCur, encumMax),
                Acc(type, realm, level, nCombatLevel, str, agi, intel, chm, t, encumCur, encumMax));
            AttackAccuracy = AccPair(MudAttackType.Normal);
            BashAccuracy = AccPair(MudAttackType.Bash);
            SmashAccuracy = canSmash ? AccPair(MudAttackType.Smash) : "—";
        }
        else
        {
            AttackAccuracy = BashAccuracy = SmashAccuracy = "—";
        }

        // Weapon damage ranges. Only meaningful with a weapon equipped — the
        // unarmed / martial-arts damage path is out of scope for this panel.
        if (t.WeaponMax > 0)
        {
            string DamagePair(MudAttackType type)
            {
                MeleeDamageResult lo = CombatCalculator.CalcMeleeDamage(type, realm, str, tLow.WeaponMin, tLow.WeaponMax, tLow.PlusMaxDamage);
                MeleeDamageResult hi = CombatCalculator.CalcMeleeDamage(type, realm, str, t.WeaponMin, t.WeaponMax, t.PlusMaxDamage);
                return RangePair(lo.MinDamage, lo.MaxDamage, hi.MinDamage, hi.MaxDamage);
            }
            AttackDamage = DamagePair(MudAttackType.Normal);
            BashDamage = DamagePair(MudAttackType.Bash);
            SmashDamage = canSmash ? DamagePair(MudAttackType.Smash) : "—";
        }
        else
        {
            AttackDamage = BashDamage = SmashDamage = "—";
        }
        string? meleeTip = t.WeaponMax > 0
            ? string.Join("\n",
                $"Weapon {t.WeaponMin}-{t.WeaponMax}, strength {str}",
                Part("+max damage", gear.PlusMaxDamage, buffLow, buff, "max damage"),
                Part(realm == RealmType.ParaMud ? "+accuracy (all sources add)" : "+accuracy (highest source)",
                    realm == RealmType.ParaMud ? gear.PlusAccuracy : gear.MaxSingleAbil22, buffLow, buff, "accuracy"),
                $"Worn accuracy {t.TotalWornAccy}")
            : null;
        AttackTip = meleeTip;
        BashTip = meleeTip;

        // Swings/round for the melee rows. Needs a weapon (its speed drives the
        // energy budget), a level and a class CombatLVL. Bash doubles energy per
        // swing; Smash is locked to a single swing (mirrors the Calculators tab).
        if (t.WeaponMax > 0 && level > 0 && nCombatLevel > 0)
        {
            AttackSwings = WeaponSwings(realm, level, nCombatLevel, agi, str, t, encumCur, encumMax, isBashing: false);
            BashSwings = WeaponSwings(realm, level, nCombatLevel, agi, str, t, encumCur, encumMax, isBashing: true);
            SmashSwings = canSmash ? "1.0" : "—";
        }
        else
        {
            AttackSwings = BashSwings = SmashSwings = "—";
        }

        bool hasClassStealth = ClassCapabilities.ClassHasStealth(classRow);
        bool hasRaceStealth = ClassCapabilities.RaceHasStealth(raceRow);
        bool canBackstab = hasClassStealth || hasRaceStealth;
        ShowBackstab = canBackstab;

        int stealth = _stats.Stealth;
        if (canBackstab && level > 0 && stealth > 0)
        {
            int BsAccy(EquipmentStatSummary s)
            {
                int abil22 = realm == RealmType.ParaMud ? s.PlusAccuracy : s.MaxSingleAbil22;
                int norm = realm == RealmType.ParaMud ? s.TotalWornAccy + abil22 : abil22;
                return CombatCalculator.CalcBackstabAccuracy(
                    stealth, agi, level, str, s.WeaponStrReq, s.PlusBSAccuracy, norm, hasClassStealth, realm);
            }
            BackstabAccuracy = Pair(BsAccy(tLow).ToString(CultureInfo.InvariantCulture),
                BsAccy(t).ToString(CultureInfo.InvariantCulture));

            // Damage range for the equipped weapon (WeaponMin/Max are 0 when
            // unarmed, which CalcBSDamage handles as the strength-only profile).
            BSDamageResult BsDmg(EquipmentStatSummary s) => CombatCalculator.CalcBSDamage(
                level, stealth, str, s.WeaponMin, s.WeaponMax,
                s.PlusBSMin, s.PlusBSMax, s.PlusMaxDamage, hasClassStealth, realm);
            BSDamageResult lo = BsDmg(tLow), hi = BsDmg(t);
            BackstabDamage = RangePair(lo.MinDamage, lo.MaxDamage, hi.MinDamage, hi.MaxDamage);
            BackstabTip = string.Join("\n",
                $"Weapon {t.WeaponMin}-{t.WeaponMax}, stealth {stealth}, strength {str}, agility {agi}, level {level}",
                Part("BS accuracy", gear.PlusBSAccuracy, buffLow, buff, "BS accuracy"),
                Part("BS min damage", gear.PlusBSMin, buffLow, buff, "BS min damage"),
                Part("BS max damage", gear.PlusBSMax, buffLow, buff, "BS max damage"),
                Part("+max damage", gear.PlusMaxDamage, buffLow, buff, "max damage"),
                Part(realm == RealmType.ParaMud ? "+accuracy (all sources add)" : "+accuracy (highest source)",
                    realm == RealmType.ParaMud ? gear.PlusAccuracy : gear.MaxSingleAbil22, buffLow, buff, "accuracy"));
            BackstabSwings = "1.0";   // a backstab is always a single strike
        }
        else
        {
            // Char has a stealth source but can't compute yet (no level / stealth
            // snapshot) → em-dash; genuinely non-stealth chars read N/A.
            BackstabAccuracy = stealth > 0 ? "—" : "N/A";
            BackstabDamage = string.Empty;
            BackstabSwings = string.Empty;
            BackstabTip = null;
        }

        // Martial-arts attacks — Mystic special strikes. Each strike row is gated
        // on the class innately granting that ability (Punch 29 / Kick 30 /
        // Jumpkick 35); a trained Martial Arts skill from items/races doesn't
        // unlock the strikes. The damage formula branches Stock vs GreaterMUD
        // inside CalcMartialArtsDamage.
        bool hasPunch = ClassCapabilities.ClassHasPunch(classRow);
        bool hasKick = ClassCapabilities.ClassHasKick(classRow);
        bool hasJumpKick = ClassCapabilities.ClassHasJumpKick(classRow);
        ShowPunch = hasPunch;
        ShowKick = hasKick;
        ShowJumpKick = hasJumpKick;
        bool showMa = hasPunch || hasKick || hasJumpKick;
        if (showMa && level > 0 && nCombatLevel > 0)
        {
            // MA accuracy is the normal-attack accuracy with weapon-hand accy
            // excluded — the wielded weapon's accy doesn't fold into a
            // martial-arts strike — plus the per-attack item accy bonus.
            int maWornAccy = t.TotalWornAccy - t.WeaponHandAccy - t.OffHandAccy;
            if (maWornAccy < 0) maWornAccy = 0;
            int maBaseAccy = CombatCalculator.CalcAccuracy(
                MudAttackType.Normal, realm, level, nCombatLevel,
                str, agi, intel, chm, maWornAccy, effectiveAbil22,
                encumCur, encumMax, weaponStrReq: 0);

            // GreaterMUD applies a per-attack accuracy penalty (kick -10,
            // jumpkick -15); Stock has none.
            int kickAccyPenalty = realm == RealmType.ParaMud ? 10 : 0;
            int jumpKickAccyPenalty = realm == RealmType.ParaMud ? 15 : 0;

            PunchAccuracy = (maBaseAccy + t.PlusPunchAccy).ToString(CultureInfo.InvariantCulture);
            KickAccuracy = (maBaseAccy + t.PlusKickAccy - kickAccyPenalty).ToString(CultureInfo.InvariantCulture);
            JumpKickAccuracy = (maBaseAccy + t.PlusJumpKickAccy - jumpKickAccyPenalty).ToString(CultureInfo.InvariantCulture);

            // The damage formula takes the MA +skill bonus — the item-granted
            // per-attack MA +skill bonus, floored to 1 — NOT the Martial Arts skill
            // stat (that stat drives accuracy above, and gates these rows on/off, but
            // never the damage magnitude). No stock ability grants a +MA-skill bonus,
            // so 1 is the value used.
            const int maPlusSkill = 1;
            PunchDamage = MARange(MudAttackType.Punch, realm, level, maPlusSkill, str, t.PlusMaxDamage, t.PlusPunchDmg, t.PlusMinDamage);
            KickDamage = MARange(MudAttackType.Kick, realm, level, maPlusSkill, str, t.PlusMaxDamage, t.PlusKickDmg, t.PlusMinDamage);
            JumpKickDamage = MARange(MudAttackType.Jumpkick, realm, level, maPlusSkill, str, t.PlusMaxDamage, t.PlusJumpKickDmg, t.PlusMinDamage);

            // Bare-handed strikes have a fixed attack speed (no weapon) and no
            // strength requirement, so their swing rate comes from MartialArtsSpeed.
            PunchSwings = MASwings(MudAttackType.Punch, realm, level, nCombatLevel, agi, str, encumCur, encumMax);
            KickSwings = MASwings(MudAttackType.Kick, realm, level, nCombatLevel, agi, str, encumCur, encumMax);
            JumpKickSwings = MASwings(MudAttackType.Jumpkick, realm, level, nCombatLevel, agi, str, encumCur, encumMax);
        }
        else
        {
            PunchAccuracy = KickAccuracy = JumpKickAccuracy = "—";
            PunchDamage = KickDamage = JumpKickDamage = "—";
            PunchSwings = KickSwings = JumpKickSwings = "—";
        }
    }

    // Swings/round for a weapon row: the energy-budget swing count, formatted like
    // the Calculators tab's "Swings/Rnd". Bash passes isBashing so the doubled
    // energy halves the rate.
    private static string WeaponSwings(RealmType realm, int level, int nCombatLevel, int agi, int str,
                                       EquipmentStatSummary t, int encumCur, int encumMax, bool isBashing)
    {
        SwingCalcResult s = CombatCalculator.CalcSwings(
            nCombatLevel, level, t.WeaponSpeed, agi, str, t.WeaponStrReq,
            encumCur, encumMax, isBashing: isBashing, realmType: realm);
        return s.RawSwings.ToString("0.0", CultureInfo.InvariantCulture);
    }

    // Swings/round for a martial-arts strike: the strike's fixed speed stands in
    // for a weapon's, and it carries no strength requirement.
    private static string MASwings(MudAttackType type, RealmType realm, int level, int nCombatLevel,
                                   int agi, int str, int encumCur, int encumMax)
    {
        SwingCalcResult s = CombatCalculator.CalcSwings(
            nCombatLevel, level, CombatCalculator.MartialArtsSpeed(type, realm), agi, str,
            weaponStrReq: 0, encumCur, encumMax, realmType: realm);
        return s.RawSwings.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static string MARange(MudAttackType type, RealmType realm, int level, int maPlusSkill, int str,
                                  int plusMaxDamage, int maPlusDamage, int plusMinDamage)
    {
        MeleeDamageResult d = CombatCalculator.CalcMartialArtsDamage(
            type, realm, level, maPlusSkill, str, plusMaxDamage, maPlusDamage, plusMinDamage);
        return string.Create(CultureInfo.InvariantCulture, $"{d.MinDamage}-{d.MaxDamage}");
    }

    // "+max damage 3-4 — gear 2, smite +1-2": one tooltip line, the gear part then
    // each cast buff's part, a range where the buff's roll decides it. low and high
    // list the same sources in the same order (BuffCombatCalculator).
    private static string Part(string label, int gearValue, MudPlay.Game.Spells.BuffCombat low,
        MudPlay.Game.Spells.BuffCombat high, string what)
    {
        int spellsLow = 0, spellsHigh = 0;
        List<string> parts = new() { $"gear {gearValue.ToString("+0;-0;0", CultureInfo.InvariantCulture)}" };
        for (int i = 0; i < high.Sources.Count; i++)
        {
            MudPlay.Game.Spells.BuffCombatSource h = high.Sources[i];
            int l = i < low.Sources.Count ? low.Sources[i].Value : h.Value;
            if (h.What != what || (l == 0 && h.Value == 0)) continue;
            spellsLow += l;
            spellsHigh += h.Value;
            parts.Add($"{h.Spell} {Signed(l)}" + (l == h.Value ? "" : $"-{h.Value}"));
        }
        return $"{label} {Pair((gearValue + spellsLow).ToString(CultureInfo.InvariantCulture), (gearValue + spellsHigh).ToString(CultureInfo.InvariantCulture))}"
            + $" — {string.Join(", ", parts)}";
    }

    private static string Signed(int v) => v.ToString("+0;-0;0", CultureInfo.InvariantCulture);

    // "129" or "129-130": one figure, or its range across the buffs' rolls.
    private static string Pair(string low, string high) => low == high ? low : $"{low}-{high}";

    // "10-20", "10-(20-21)" or "(85-87)-(101-103)": a damage range whose ends may each
    // depend on a buff's roll (user's format, 2026-09-30).
    private static string RangePair(int lowMin, int lowMax, int highMin, int highMax)
    {
        static string End(int a, int b) => a == b
            ? a.ToString(CultureInfo.InvariantCulture)
            : $"({Math.Min(a, b).ToString(CultureInfo.InvariantCulture)}-{Math.Max(a, b).ToString(CultureInfo.InvariantCulture)})";
        return $"{End(lowMin, highMin)}-{End(lowMax, highMax)}";
    }

    // The gear summary with the cast buffs' bonuses folded in.
    private static EquipmentStatSummary WithBuffs(EquipmentStatSummary gear, MudPlay.Game.Spells.BuffCombat buff)
    {
        EquipmentStatSummary t = gear.Copy();
        t.PlusAccuracy += buff.Accuracy;
        t.MaxSingleAbil22 = Math.Max(t.MaxSingleAbil22, buff.AccuracyMaxSingle);
        t.PlusMaxDamage += buff.MaxDamage;
        t.PlusBSAccuracy += buff.BsAccuracy;
        t.PlusBSMin += buff.BsMin;
        t.PlusBSMax += buff.BsMax;
        return t;
    }

    private static string Acc(MudAttackType type, RealmType realm, int level, int nCombatLevel,
                              int str, int agi, int intel, int chm,
                              EquipmentStatSummary t, int encumCur, int encumMax)
    {
        int v = CombatCalculator.CalcAccuracy(type, realm, level, nCombatLevel,
            str, agi, intel, chm, t.TotalWornAccy,
            realm == RealmType.ParaMud ? t.PlusAccuracy : t.MaxSingleAbil22,
            encumCur, encumMax, t.WeaponStrReq);
        return v.ToString(CultureInfo.InvariantCulture);
    }

    // ----- Box A: alignment standing --------------------------------------

    // Our own character shows up in our own `who` output, so PlayerDatabase
    // already carries our alignment word — no new parsing needed here.
    private void RefreshAlignment()
    {
        AlignmentStale = _alignmentTracker.IsStale;

        if (string.IsNullOrEmpty(_stats.Name))
        {
            Alignment = "—";
            return;
        }

        (string given, _) = PlayerRecord.SplitName(_stats.Name);
        PlayerRecord? self = null;
        foreach (PlayerRecord r in _playerDb.Players)
        {
            if (string.Equals(r.GivenName, given, StringComparison.OrdinalIgnoreCase))
            {
                self = r;
                break;
            }
        }

        string? word = self?.Alignment;
        Alignment = string.IsNullOrEmpty(word) ? "—" : word;
    }

    // ----- Box A: carry weight + carried currency -------------------------

    // Encumbrance + currency both ride the same InventoryManager snapshot, so
    // they refresh together on every `i` dump or incremental coin line.
    private void RefreshWealth()
    {
        InventorySnapshot snap = _inventory.Snapshot;

        // Show the carry weight, the game's own bracket word (None / Light /
        // Medium / Heavy / Encumbered), and its carry-load percent beside it, so
        // the number carries the same gate the `enc` line reports. Drop the word +
        // percent only if the bracket is Unknown (no reading yet).
        EncumbranceReading enc = snap.Encumbrance;
        Encumbrance = enc.MaxWeight <= 0
            ? "—"
            : enc.Category == EncumbranceLevel.Unknown
                ? string.Create(CultureInfo.InvariantCulture, $"{enc.CurrentWeight} / {enc.MaxWeight}")
                : string.Create(CultureInfo.InvariantCulture, $"{enc.CurrentWeight} / {enc.MaxWeight} — {enc.Category} [{enc.Percentage}%]");

        CurrencyHoldings coins = snap.Currency;
        CurrencyHeld = FormatCoins(coins);
        // The wealth line mirrors the game's own "Wealth:  N copper farthings"
        // summary — the consolidated value in the base denomination, ungrouped
        // like the game (no thousands separator) and not decomposed (the Coins
        // line above carries the per-coin breakdown).
        TotalWealth = coins.TotalCopperValue > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{coins.TotalCopperValue} copper farthings")
            : "—";
    }

    // Per-denomination coins currently carried, high → low, nonzero only.
    private string FormatCoins(CurrencyHoldings c)
    {
        var parts = new List<string>(5);
        if (c.Runic > 0) parts.Add($"{c.Runic:N0} {_naming.RunicName}");
        if (c.Platinum > 0) parts.Add($"{c.Platinum:N0} platinum");
        if (c.Gold > 0) parts.Add($"{c.Gold:N0} gold");
        if (c.Silver > 0) parts.Add($"{c.Silver:N0} silver");
        if (c.Copper > 0) parts.Add($"{c.Copper:N0} copper");
        return parts.Count > 0 ? string.Join(", ", parts) : "none";
    }

    // ----- Inventory: the full carry list from the last `i` dump ----------

    private void RefreshInventory()
    {
        InventorySnapshot snap = _inventory.Snapshot;

        EquippedItems.Clear();
        foreach (EquippedItem item in snap.EquippedItems)
            EquippedItems.Add(new WorkshopItemRow(
                item.Name,
                string.IsNullOrEmpty(item.Slot)
                    ? string.Empty
                    : string.Create(CultureInfo.InvariantCulture, $"({item.Slot})"),
                ResolveItemNumber(item.Name),
                ChargesTextFor(item.Name)));

        CarriedItems.Clear();
        foreach (string name in snap.CarriedItems)
        {
            int number = ResolveItemNumber(name);
            CarriedItems.Add(new WorkshopItemRow(name, string.Empty, number, ChargesTextFor(name),
                isContainer: number > 0
                    && AppServices.Current.ItemNames.ItemTypeOf(number) == Game.Inventory.ChestOffloadPlanner.ContainerItemType,
                openChestOffload: _openChestOffload));
        }

        Keys.Clear();
        if (snap.Keys is { } keys)
            foreach (string name in keys)
                Keys.Add(new WorkshopItemRow(name, string.Empty, ResolveItemNumber(name), ChargesTextFor(name)));

        HasEquipped = EquippedItems.Count > 0;
        HasCarried = CarriedItems.Count > 0;
        HasKeys = Keys.Count > 0;
        InventoryLoaded = _inventory.IsLoaded;
    }

    // "5 Charges" for a limited-use item — its remaining charges, else empty. Realm-aware
    // via the shared readout: Paradigm reads the "Uses remaining" look count, stock uses
    // max − uses-counted for a finite limited-use item.
    private string ChargesTextFor(string name)
        => _charges.RemainingForName(name) is { } n
            ? string.Create(CultureInfo.InvariantCulture, $"{n} Charge{(n == 1 ? "" : "s")}")
            : string.Empty;

    private static string Display(string value) => string.IsNullOrEmpty(value) ? "—" : value;

    private static int GetInt(JsonElement? row, string property)
    {
        if (row is not JsonElement el || el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    // An `i` dump (or incremental coin/item line) landed — refold gear into
    // derived combat and re-pull wealth + the full carry list.
    // Coalesced — see _inventoryRefreshDebounce. A gear swap's per-confirmation change
    // storm collapses to a single derived/wealth/inventory refresh once it settles.
    private void OnInventoryChanged() => ScheduleInventoryRefresh();

    private void ScheduleInventoryRefresh()
    {
        _inventoryRefreshDebounce.Stop();
        _inventoryRefreshDebounce.Start();
    }
    // A limited-use item's charge count landed from a look reply — just re-fold the
    // carry list so the charge readouts update (stats / wealth are unaffected).
    private void OnItemChargesChanged() => ScheduleInventoryRefresh();

    private void OnPlayersChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshAlignment();
    // Dark-cloud line fired (or a `who` cleared it) — just sync the flag; the
    // alignment word itself refreshes on the PlayerDatabase update.
    private void OnAlignmentStaleChanged() => AlignmentStale = _alignmentTracker.IsStale;

    // The Quest Status tab republished the completed-quest bonus set — refold it
    // into derived combat (which consumes the combined aggregate).
    private void OnQuestBonusesChanged() => RefreshDerived();

    // A base-stat label opens the Stat Breakpoints window on that stat. It's a deep
    // link: the same stat again raises or closes the window, another stat switches it
    // and only raises.
    [RelayCommand]
    private void OpenStatBreakpoints(string statName)
    {
        if (!Enum.TryParse(statName, out BaseStat stat)) return;
        if (_breakpointsWindow is { DataContext: StatBreakpointsViewModel open } window)
        {
            if (open.Stat == stat) { DialogService.RaiseOrClose(window); return; }
            open.Show(stat);
            DialogService.RaiseExisting(window);
            return;
        }
        _breakpointsWindow = new StatBreakpointsWindow { DataContext = new StatBreakpointsViewModel(_stats, _gameData, _inventory, stat) };
        _breakpointsWindow.Closed += (_, _) => _breakpointsWindow = null;
        _breakpointsWindow.Show();
    }

    public override void Dispose()
    {
        _breakpointsWindow?.Close();
        _inventoryRefreshDebounce.Stop();
        _stats.PropertyChanged -= OnStatsChanged;
        _inventory.Changed -= OnInventoryChanged;
        _charges.Changed -= OnItemChargesChanged;
        _playerDb.Players.CollectionChanged -= OnPlayersChanged;
        _alignmentTracker.StaleChanged -= OnAlignmentStaleChanged;
        _questBonuses.Changed -= OnQuestBonusesChanged;
    }

    // Resolve an inventory-dump item name to its Items-table Number for a clickable
    // record link, or 0 when it doesn't resolve (dump names can be truncated /
    // pluralised, so a few worn/carried items won't link).
    private int ResolveItemNumber(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        int num = LookupItemNumber(name);
        if (num == 0)
        {
            // A stacked carried item carries a count prefix ("3 piece of amber")
            // the Items-table name ("piece of amber") lacks — strip it and retry.
            string stripped = StripCountPrefix(name);
            if (!ReferenceEquals(stripped, name)) num = LookupItemNumber(stripped);
        }
        return num;
    }

    private int LookupItemNumber(string name)
    {
        if (_gameData.FindRowByName("Items", name) is not { } row) return 0;
        return row.TryGetProperty("Number", out JsonElement n) && n.ValueKind == JsonValueKind.Number
            ? n.GetInt32()
            : 0;
    }

    // "3 piece of amber" → "piece of amber". Returns the input unchanged when it
    // has no leading "<digits> " count prefix. Only the record LOOKUP strips the
    // count; the displayed name keeps it (the quantity is useful).
    private static string StripCountPrefix(string name)
    {
        int i = 0;
        while (i < name.Length && char.IsDigit(name[i])) i++;
        if (i == 0 || i >= name.Length || name[i] != ' ') return name;
        return name[(i + 1)..].TrimStart();
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Avalonia.Collections;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game;
using MudPlay.Game.Calculators;
using MudPlay.Game.Inventory;
using MudPlay.Game.Leaderboard;
using MudPlay.Game.Quests;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Views.CharacterWorkshop;

namespace MudPlay.ViewModels.CharacterWorkshop;

// CALCULATORS section — combat what-if tools that sit apart from the live
// stat sheet:
//   Hit Calculator — pick a monster by name and see the You → Monster
//     projection (hit%, damage, swings, DPS) computed from your gear-derived
//     offense, with an attack-type dropdown (Attack / Bash / Smash and the
//     Mystic strikes Punch / Kick / Jumpkick, filtered to what the class can
//     do) and an optional weapon picker to model a different weapon's damage
//     against the monster's damage resist.
//   Monster → You — the return direction, made interactive: your AC and dodge
//     seed from the live stat + gear values but are editable, and every physical
//     attack the monster has is listed with its own editable accuracy, so you can
//     dial either side and watch the hit chance move.
//   Movement Speed — the encumbrance / quickness / slowness solver against the
//     one-second movement cap.
//   Swing — the swing model (energy per swing and the 10-round carry-over
//     breakdown) for the equipped or a picked weapon.
//   Backstab — the realm-aware backstab damage range for the backstab-set weapon
//     (or a picked one), reading level / strength / stealth / class-stealth and
//     the +BS ability bonuses from the live character.
// The underlying "actual" offense/defense snapshot tracks the live stats,
// inventory, and completed-quest bonuses so the Reset buttons and the Backstab
// read-out always reflect the current character. The editable inputs, though,
// seed from those actuals only once (construction / profile load) and on the
// per-calculator Reset buttons — live game changes never overwrite a value the
// user has dialed in, so a what-if stays put until it's explicitly reset.
public sealed partial class CalculatorsSectionViewModel : WorkshopSectionViewModel
{
    private readonly PlayerStats _stats;
    private readonly GameDataCache _gameData;
    private readonly InventoryManager _inventory;
    private readonly QuestBonusState _questBonuses;
    private readonly ProfileService _profile;
    private readonly LeaderboardSnapshotStore _leaderboards;
    private Control? _view;

    public override string Id => "calculators";
    public override string Title => "Calculators";
    public override Control View => _view ??= new CalculatorsSectionView { DataContext = this };

    // ----- Monster typeahead ---------------------------------------------
    // Typeahead source: "<name> (#<number>)" per monster.
    public ObservableCollection<string> MonsterNames { get; } = new();
    private readonly Dictionary<string, int> _monsterNumberByLabel = new();
    [ObservableProperty] private string? _selectedMonsterName;

    // ----- Attack type (drives the You → Monster projection) --------------
    // Filtered to what the loaded class can actually do: Attack / Bash are
    // universal, Smash rides on the smash-capable class list, and the three
    // Mystic strikes appear only when the class grants that ability.
    public ObservableCollection<string> MatchupAttackTypeOptions { get; } = new();
    // Selected attack type label; a martial-arts pick hides the weapon picker
    // (the strike is bare-handed) and swaps the offense math to the strike model.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMatchupMartialArts))]
    private string _selectedMatchupAttackType = AttackBase;

    // True while a bare-handed Mystic strike is selected — the view hides the
    // weapon picker, since the strike carries no weapon.
    public bool IsMatchupMartialArts => IsMartialArts(MatchupAttackTypeFor(SelectedMatchupAttackType));

    private const string AttackBase = "Attack";

    // ----- Weapon picker (what-if offense) -------------------------------
    // Typeahead source: "<name> (#<number>)" per weapon item.
    public ObservableCollection<string> WeaponNames { get; } = new();
    private readonly Dictionary<string, int> _weaponNumberByLabel = new();
    // Selected what-if weapon — null / unmatched means the equipped weapon.
    [ObservableProperty] private string? _selectedWeaponName;

    // ----- Monster values (editable, seeded by the name picker) -----------
    // Monster AC used by the You → Monster hit calc — seeded on pick, editable. May be negative.
    [ObservableProperty] private int _monsterAc;
    // Monster damage resist — seeded on pick, editable; trims each of your hits.
    [ObservableProperty] private int _monsterDr;
    // Monster dodge (the Dodge ability, abil 34) — seeded on pick, editable; lowers your hit chance. 0 for most monsters.
    [ObservableProperty] private int _monsterDodge;

    // ----- Your values (editable, seeded from the live actuals) -----------
    // Your attack accuracy — seeded from the gear-derived actual, editable.
    [ObservableProperty] private int _playerAccuracy;
    // Your AC used in the incoming-hit calc — seeded from actuals, editable. May be negative.
    [ObservableProperty] private int _playerAc;
    // Your raw dodge used in the incoming-hit calc — seeded from actuals, editable. May be negative.
    [ObservableProperty] private int _playerDodge;

    // "Show me the Monsters" picker: the % chance a monster hits you (vs your AC +
    // dodge above). The button below finds the accuracy that produces this hit-% and
    // opens the Monsters game-data tab filtered to Acc ≥ that value.
    [ObservableProperty] private int _hitPercentTarget = 50;

    // ----- Monster → You (incoming) --------------------------------------
    // One row per monster physical attack — or a single "Custom attack" row when
    // no monster is picked, so the incoming-hit calculator always works. Each
    // row's accuracy is editable and drives its own hit% vs your AC + dodge.
    public ObservableCollection<MonsterAttackRowViewModel> MonsterAttacks { get; } = new();

    // ----- You → Monster (offense projection vs the picked monster) -------
    [ObservableProperty] private string _matchupPlayerHit = "—";
    [ObservableProperty] private string _matchupPlayerDamage = "—";
    [ObservableProperty] private string _matchupSwings = "—";
    [ObservableProperty] private string _matchupDps = "—";
    // False when unarmed — gates the swings / DPS / rounds rows.
    [ObservableProperty] private bool _matchupHasWeapon;

    // ----- Movement Speed calculator -------------------------------------
    // Encumbrance percentage feeding the movement calc — seeded live, editable.
    [ObservableProperty] private int _moveEncumbrance;
    // Total quickness feeding the movement calc — seeded from gear, editable.
    [ObservableProperty] private int _moveQuickness;
    // Modelled slowness effect — seeds to 0, editable.
    [ObservableProperty] private int _moveSlowness;
    [ObservableProperty] private string _moveSpeedText = "—";
    [ObservableProperty] private string _moveStatusText = "—";
    [ObservableProperty] private string _moveAdviceText = string.Empty;
    // True on Paradigm — the Movement Speed calculator shows its interactive
    // ParaMUD-formula solver. On stock the formula doesn't apply (movement is a
    // measured two-band step function), so the view swaps to a static findings
    // card. Set from the active realm on every capture.
    [ObservableProperty] private bool _isParadigmRealm;
    // Stock findings-card numbers, mirrored from the reseeded stock
    // EncumbranceHopTimes defaults so the card can't drift from the buckets the
    // auto-lair scheduler actually uses. None / Light / Medium share the fast
    // band; Heavy / Encumbered share the slow band.
    public string StockMoveFastBandText { get; }
    public string StockMoveSlowBandText { get; }

    // ----- Swing calculator ----------------------------------------------
    // Selected swing weapon — null / unmatched means the equipped weapon.
    [ObservableProperty] private string? _selectedSwingWeaponName;
    // Character level feeding the swing calc — seeded live, editable.
    [ObservableProperty] private int _swingLevel;
    // Class combat level (1–5) — seeded from the class row, editable.
    [ObservableProperty] private int _swingCombatLevel;
    [ObservableProperty] private int _swingAgility;
    [ObservableProperty] private int _swingStrength;
    [ObservableProperty] private int _swingEncumbrance;
    // Speed-modifier label: "Normal (100)" / "Sped (85)" / "Slow (125)".
    [ObservableProperty] private string _swingSpeedOption = "Normal (100)";
    [ObservableProperty] private bool _swingBashing;
    [ObservableProperty] private bool _swingSlowness;

    [ObservableProperty] private string _swingEnergyText = "—";
    [ObservableProperty] private string _swingRawText = "—";
    [ObservableProperty] private string _swingEncumText = "—";
    [ObservableProperty] private string _swingQndText = "—";
    // False when no weapon (equipped or picked) — gates the swing outputs.
    [ObservableProperty] private bool _swingHasWeapon;

    // Fixed speed-modifier choices for the swing calc's combo box.
    public string[] SwingSpeedOptions { get; } = { "Normal (100)", "Sped (85)", "Slow (125)" };

    // 10-round swings / energy-carried breakdown for the picked setup.
    public ObservableCollection<SwingRoundRow> SwingRounds { get; } = new();

    // ----- Backstab calculator -------------------------------------------
    // Selected backstab weapon — the only editable input. Defaults to the weapon
    // on the Equipment Manager's Backstab set (empty when none is set); picking a
    // different weapon models its damage instead. Empty = no result.
    [ObservableProperty] private string? _selectedBackstabWeaponName;

    // Read-only context echoing what feeds the calc — pulled live from the
    // character so the number is legible, not editable inputs.
    [ObservableProperty] private string _backstabLevelText = "—";
    [ObservableProperty] private string _backstabStrengthText = "—";
    [ObservableProperty] private string _backstabStealthText = "—";
    [ObservableProperty] private string _backstabClassStealthText = "—";
    [ObservableProperty] private string _backstabBonusText = "—";
    [ObservableProperty] private string _backstabRealmText = "—";
    [ObservableProperty] private string _backstabWeaponRangeText = "—";

    [ObservableProperty] private string _backstabMinText = "—";
    [ObservableProperty] private string _backstabMaxText = "—";
    [ObservableProperty] private string _backstabAvgText = "—";
    // False until a backstab weapon is chosen — gates the damage rows.
    [ObservableProperty] private bool _backstabHasWeapon;

    // Captured player-side numbers (recomputed on every data refresh).
    private RealmType _realm;
    // Accuracy for the currently-selected attack type (Normal / Bash / Smash /
    // the Mystic strike) — the value the Accuracy input seeds and resets to.
    private int _attackAccuracy;
    private int _avgWeaponDamage;
    private double _swingsPerRound;
    private bool _hasWeapon;
    private int _critChance;
    private int _avgCritDamage;
    private int _damageResist;
    private int _protEvil;
    private int _protGood;
    private int _actualAc;
    private int _actualDodge;

    // Offense inputs captured on refresh so the weapon picker can re-derive
    // damage / swings / crit without re-running the full stat aggregation.
    private int _str, _agi, _level, _nCombatLevel, _encumCur, _encumMax;
    private int _intel, _chm, _wil;

    // Mana-regen calculator: gear/quest +ManaRgn% and the live class's magery,
    // captured on refresh so the calculator can seed itself only when the loaded
    // character is a caster (mage / druid).
    private int _manaGearRegenPct, _liveMageryType, _liveMageryLevel;
    // Lazily scanned once per set for the roll-spell picker; the sublist matching
    // the currently-selected magery drives ManaRollSpellNames.
    private readonly KnownSpellCatalog _spellCatalog;
    private IReadOnlyList<KnownSpell> _rollSpells = Array.Empty<KnownSpell>();
    // Suspends RecomputeManaRegen while a multi-input seed / rebuild is in flight,
    // so the outputs recompute once at the end rather than per property.
    private bool _suspendManaRecompute;
    private int _plusMaxDamage, _plusMinDamage, _plusCrits;
    private int _equipWeaponMin, _equipWeaponMax, _equipWeaponSpeed, _equipWeaponStrReq;

    // Accuracy inputs captured on refresh: the worn accy total and the effective
    // Abil-22 term (both attack types), plus the martial-arts worn accy (weapon
    // hands excluded) and the per-strike accy / damage bonuses.
    private int _totalWornAccy, _effectiveAbil22, _maWornAccy;
    private int _plusPunchAccy, _plusKickAccy, _plusJumpKickAccy;
    private int _plusPunchDmg, _plusKickDmg, _plusJumpKickDmg;

    // Attack-type availability for the loaded class, captured on refresh so the
    // dropdown lists only strikes the character can actually perform.
    private bool _canSmash, _hasPunch, _hasKick, _hasJumpKick;

    // Set while CaptureActuals rebuilds the snapshot so an attack-type reselection
    // it triggers (an option list rebuild) doesn't re-enter the offense recompute
    // before the captured fields are all in place.
    private bool _capturing;

    // Weapon override from the picker — empty / unmatched falls back to equipped.
    private bool _hasSelectedWeapon;
    private int _selWeaponMin, _selWeaponMax, _selWeaponSpeed, _selWeaponStrReq;

    // Movement inputs captured on refresh — encumbrance% and quickness have live
    // sources; slowness is a modelled effect that always re-seeds to 0.
    private int _actualEncumPercent;
    private int _plusQuickness;

    // Swing weapon override from the picker — empty / unmatched falls back to the
    // equipped weapon's speed / str-req captured above.
    private int _swingWeaponSpeed, _swingWeaponStrReq;

    // Backstab inputs captured on refresh. Level / strength / max-damage are
    // shared with the offense block above; stealth, the +BS bonuses, and the
    // class-stealth flag are backstab-specific.
    private int _stealth;
    private int _plusBsMin, _plusBsMax;
    private bool _hasClassStealth;
    // The Backstab-set weapon label the picker seeds to (null = none configured);
    // seeded on construction / profile load so a what-if pick survives data refreshes.
    private string? _backstabDefaultWeaponLabel;
    private int _bsWeaponMin, _bsWeaponMax;

    // Alignment of the picked monster — decides which of the player's wards
    // applies in the incoming-hit calc.
    private bool _monsterIsEvil;
    private bool _monsterIsGood;

    // ----- Mana-regen breakpoint calculator --------------------------------

    // The pure caster archetypes — Mage, Priest, Druid — as they're named in the
    // active set. Mana regen splits cleanly by magery type (Mage → INT + mana flux,
    // Priest → WIL + profane link, Druid → (INT+WIL)/2 + nature tap), so the hybrid
    // caster classes (Gypsy / Warlock / Ranger) are deliberately left off; the
    // selected class carries its MageryType + MageryLVL from game data.
    public ObservableCollection<string> ManaClassOptions { get; } = new();
    [ObservableProperty] private string? _selectedManaClass;
    // Which magery stat drives this class's regen — gates the INT / WIL pickers so
    // only the stat(s) that matter are shown (Mage → INT, Priest → WIL, Druid → both).
    [ObservableProperty] private bool _manaUsesInt = true;
    [ObservableProperty] private bool _manaUsesWil;

    // The archetype class names the dropdown is limited to (matched case-insensitively).
    private static readonly string[] ManaArchetypeClasses = { "Mage", "Priest", "Druid" };

    // When the selected class has more than one mana-regen roll spell (priests get
    // serenity + profane link), the player picks which to model; single-spell classes
    // (mage / druid) hide the picker and just show the one spell's name.
    public ObservableCollection<string> ManaRollSpellOptions { get; } = new();
    [ObservableProperty] private string? _selectedManaRollSpell;
    [ObservableProperty] private bool _manaHasSpellChoice;

    // Every input is editable so the calculator doubles as a planner for stat
    // allocations / level-ups / gear upgrades; it seeds from the live character
    // only when it's a mage or druid (SeedManaFromActuals).
    [ObservableProperty] private int _manaLevel = 1;
    [ObservableProperty] private int _manaIntellect = 1;
    [ObservableProperty] private int _manaWillpower = 1;
    [ObservableProperty] private int _manaGearRegenPercent;

    // Racial floors for the INT / WIL pickers — the lowest base value any race can
    // start with (min mINT / mWIL across the Races table), so a picker opens at a
    // real minimum instead of 1.
    [ObservableProperty] private int _manaIntMinimum = 1;
    [ObservableProperty] private int _manaWilMinimum = 1;

    // Racial trained caps (max xINT / xWIL across races) — the chart never draws a
    // magery-stat line above these. Default high (effectively uncapped) until scanned.
    private int _maxRacialInt = 300;
    private int _maxRacialWil = 300;

    // Outputs.
    [ObservableProperty] private string _manaBaseTickText = "—";
    [ObservableProperty] private string _manaGearTickText = "—";
    [ObservableProperty] private bool _manaHasRollSpell;
    [ObservableProperty] private string _manaRollSpellText = "—";
    [ObservableProperty] private string _manaRollRangeText = "—";
    [ObservableProperty] private string _manaWorstTickText = "—";
    [ObservableProperty] private string _manaBestTickText = "—";

    // "Model a roll" slider: bounds are the spell's min / max roll at the current
    // level; sliding picks a roll value and the readout shows what it is, its
    // position in the range (the 0-100% the player asked for), and the tick it
    // yields — so an observed roll can be lined up against the breakpoints.
    [ObservableProperty] private int _manaRollMin;
    [ObservableProperty] private int _manaRollMax;
    [ObservableProperty] private double _manaRollSample;
    [ObservableProperty] private string _manaSampleReadoutText = "—";

    // Natural-tick-vs-level chart (X = level −1..+5, one line per INT value) so the
    // breakpoint step-ups are visible while planning level-ups / stat points.
    [ObservableProperty] private ManaRegenChartData? _manaChart;

    // "You are here" + how far the next tick is by stat / level / +ManaRgn%.
    [ObservableProperty] private string _manaCurrentTickText = "—";
    [ObservableProperty] private string _manaNextByStatText = "—";
    [ObservableProperty] private string _manaNextByLevelText = "—";
    [ObservableProperty] private string _manaNextByRegenText = "—";

    // Distinct line colours (ARGB), reused from the map spell palette's saturated
    // hues so the series read apart on the panel.
    private static readonly uint[] ChartPalette =
        { 0xFFE6194B, 0xFF3CB44B, 0xFF4363D8, 0xFFF58231, 0xFF911EB4 };

    partial void OnManaRollSampleChanged(double value) => UpdateManaSampleReadout();

    partial void OnSelectedManaClassChanged(string? value)
    {
        RebuildManaRollSpellOptions();
        RecomputeManaRegen();
    }
    partial void OnSelectedManaRollSpellChanged(string? value) => RecomputeManaRegen();
    partial void OnManaLevelChanged(int value) => RecomputeManaRegen();
    partial void OnManaIntellectChanged(int value) => RecomputeManaRegen();
    partial void OnManaWillpowerChanged(int value) => RecomputeManaRegen();
    partial void OnManaGearRegenPercentChanged(int value) => RecomputeManaRegen();

    // Populate the roll-spell picker for the selected class's magery: the roll spells
    // whose short is in the curated set (serenity / profane link for a priest, one
    // each for mage / druid). The picker only shows when there's a real choice; the
    // selection is kept across recomputes and defaults to the first when invalidated.
    private void RebuildManaRollSpellOptions()
    {
        (int mageryType, _) = ResolveManaMagery();
        string[] shorts = RegenShortsFor(mageryType);
        var names = _rollSpells
            .Where(s => s.Magery == mageryType
                     && shorts.Contains(s.Short.Trim(), StringComparer.OrdinalIgnoreCase))
            .Select(s => s.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!ManaRollSpellOptions.SequenceEqual(names))
        {
            ManaRollSpellOptions.Clear();
            foreach (string n in names) ManaRollSpellOptions.Add(n);
        }
        ManaHasSpellChoice = names.Count > 1;

        if (SelectedManaRollSpell is null
            || !names.Contains(SelectedManaRollSpell, StringComparer.OrdinalIgnoreCase))
        {
            bool prev = _suspendManaRecompute;
            _suspendManaRecompute = true;   // don't recompute mid-rebuild; the caller does
            try { SelectedManaRollSpell = names.FirstOrDefault(); }
            finally { _suspendManaRecompute = prev; }
        }
    }

    // MageryType (1 mage / 2 priest / 3 druid) and tier for the selected class.
    private (int Type, int Level) ResolveManaMagery()
    {
        if (SelectedManaClass is { } cls && _gameData.FindRowByName("Classes", cls) is JsonElement row)
            return (GetInt(row, "MageryType"), GetInt(row, "MageryLVL"));
        return (1, 0);
    }

    // The stat that drives a magery type's regen (matching CalcManaRegen): its
    // display label and which of INT / WIL feed it. 1=Mage(INT), 2=Priest(WIL),
    // 3=Druid((INT+WIL)/2, both).
    private static (string Label, bool UsesInt, bool UsesWil) MageryStatKind(int mageryType) => mageryType switch
    {
        2 => ("WIL", false, true),   // Priest — willpower
        3 => ("avg", true, true),    // Druid — (INT+WIL)/2
        _ => ("INT", true, false),   // Mage — intellect
    };

    // The mana-regen roll spells each magery actually rerolls for, by cast short — a
    // curated set, because a magery can carry incidental 145-roll spells the class
    // doesn't use for regen (mage's ethereal taint). Priests have TWO (serenity /
    // profane link, good vs evil), so the class exposes a picker; mage and druid one.
    private static string[] RegenShortsFor(int mageryType) => mageryType switch
    {
        2 => new[] { "nity", "prfl" },   // Priest — serenity, profane link
        3 => new[] { "ntap" },           // Druid — nature tap
        _ => new[] { "flux" },           // Mage — mana flux
    };

    // The magery stat's value from the current INT / WIL for this type.
    private static int MageryStatValue(int mageryType, int intel, int wil) => mageryType switch
    {
        2 => wil,
        3 => (intel + wil) / 2,
        _ => intel,
    };

    // Inputs with the magery stat forced to statValue — feeds the chart's per-stat
    // series and the "next tick via stat" search without disturbing the other stat.
    private static ManaRegenBreakpointCalculator.Inputs WithMageryStat(
        ManaRegenBreakpointCalculator.Inputs i, int statValue) => i.MageryType switch
    {
        2 => i with { Willpower = statValue },
        3 => i with { Intellect = statValue, Willpower = statValue },
        _ => i with { Intellect = statValue },
    };

    // Racial floor / cap for a magery type's driving stat, so the chart seeds at a
    // real minimum and never draws a line above what any race could train to.
    private int MageryStatFloor(int mageryType) => mageryType switch
    {
        2 => ManaWilMinimum,
        3 => (ManaIntMinimum + ManaWilMinimum) / 2,
        _ => ManaIntMinimum,
    };
    private int MageryStatCap(int mageryType) => mageryType switch
    {
        2 => _maxRacialWil,
        3 => (_maxRacialInt + _maxRacialWil) / 2,
        _ => _maxRacialInt,
    };

    // Seed from the live character only when it's a caster this tool models (mage
    // type 1, druid type 3): select that class and adopt its level / stats / gear.
    // A non-caster falls back to the first caster class and the racial stat floors
    // so the tool still opens as a from-scratch planner. Batched under the suspend
    // guard so the assignments recompute once.
    private void SeedManaFromActuals()
    {
        _suspendManaRecompute = true;
        try
        {
            _rollSpells = _spellCatalog.RollSpells();
            ComputeStatFloors();
            RebuildManaClassOptions();

            if (_liveMageryType is 1 or 2 or 3 && !string.IsNullOrEmpty(_stats.Class)
                && ManaClassOptions.Contains(_stats.Class))
            {
                SelectedManaClass = _stats.Class;
                ManaLevel = _level;
                ManaIntellect = _intel;
                ManaWillpower = _wil;
                ManaGearRegenPercent = _manaGearRegenPct;
            }
            else
            {
                // Not one of the archetype classes (a hybrid caster or a non-caster):
                // open on the archetype matching the live magery type, else the first.
                string want = _liveMageryType switch { 2 => "Priest", 3 => "Druid", _ => "Mage" };
                SelectedManaClass = ManaClassOptions.FirstOrDefault(c => c.Equals(want, StringComparison.OrdinalIgnoreCase))
                    ?? ManaClassOptions.FirstOrDefault();
                ManaLevel = Math.Max(1, ManaLevel);
                ManaIntellect = ManaIntMinimum;
                ManaWillpower = ManaWilMinimum;
                ManaGearRegenPercent = 0;
            }
        }
        finally
        {
            _suspendManaRecompute = false;
        }
        RebuildManaRollSpellOptions();   // ensure the picker matches the seeded class
        RecomputeManaRegen();
    }

    // Racial INT / WIL bounds across all races: the min base (mINT / mWIL) is the
    // floor the pickers seed and clamp to; the max trained cap (xINT / xWIL) bounds
    // the chart so it never draws a magery-stat line above what a race could reach.
    // Falls back to sane values when the set has no Races table.
    private void ComputeStatFloors()
    {
        int minInt = int.MaxValue, minWil = int.MaxValue, maxInt = 0, maxWil = 0;
        if (_gameData.GetRawTable("Races") is { } doc)
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                minInt = Math.Min(minInt, GetInt(row, "mINT"));
                minWil = Math.Min(minWil, GetInt(row, "mWIL"));
                maxInt = Math.Max(maxInt, GetInt(row, "xINT"));
                maxWil = Math.Max(maxWil, GetInt(row, "xWIL"));
            }
        ManaIntMinimum = minInt is int.MaxValue or <= 0 ? 1 : minInt;
        ManaWilMinimum = minWil is int.MaxValue or <= 0 ? 1 : minWil;
        _maxRacialInt = maxInt > 0 ? maxInt : 300;
        _maxRacialWil = maxWil > 0 ? maxWil : 300;
    }

    // The active set's pure caster archetypes (Mage / Priest / Druid by name), sorted.
    // Restricted to those three class names on purpose — the hybrid casters that
    // share their magery types (Gypsy / Warlock / Ranger) are left off.
    private void RebuildManaClassOptions()
    {
        var names = new List<string>();
        if (_gameData.GetRawTable("Classes") is { } doc)
            foreach (JsonElement row in doc.RootElement.EnumerateArray())
            {
                if (GetInt(row, "MageryType") is not (1 or 2 or 3)) continue;
                if (row.TryGetProperty("Name", out JsonElement nameEl)
                    && nameEl.GetString() is { Length: > 0 } n
                    && ManaArchetypeClasses.Contains(n, StringComparer.OrdinalIgnoreCase))
                    names.Add(n);
            }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        if (ManaClassOptions.SequenceEqual(names)) return;

        string? keep = SelectedManaClass;
        ManaClassOptions.Clear();
        foreach (string n in names) ManaClassOptions.Add(n);
        if (keep is not null && names.Contains(keep, StringComparer.OrdinalIgnoreCase))
            SelectedManaClass = keep;
    }

    // Resolve the current inputs from the editable fields + the selected class's
    // magery (shared by the full recompute and the slider readout).
    private ManaRegenBreakpointCalculator.Inputs BuildManaInputs()
    {
        (int mageryType, int mageryLevel) = ResolveManaMagery();
        return new ManaRegenBreakpointCalculator.Inputs(
            Level: Math.Max(1, ManaLevel),
            MageryType: mageryType,
            Intellect: ManaIntellect,
            Willpower: ManaWillpower,
            MageryLevel: mageryLevel,
            GearRegenPercent: ManaGearRegenPercent,
            Realm: _realm);
    }

    private void RecomputeManaRegen()
    {
        if (_suspendManaRecompute) return;

        ManaRegenBreakpointCalculator.Inputs inputs = BuildManaInputs();
        (_, bool usesInt, bool usesWil) = MageryStatKind(inputs.MageryType);
        ManaUsesInt = usesInt;
        ManaUsesWil = usesWil;

        ManaBaseTickText = TickText(ManaRegenBreakpointCalculator.Tick(inputs with { GearRegenPercent = 0 }, 0));
        ManaGearTickText = TickText(ManaRegenBreakpointCalculator.Tick(inputs, 0));
        BuildManaChart(inputs);
        ComputeManaDistances(inputs);

        // Use the roll spell the player picked (ManaRollSpellOptions holds only this
        // magery's curated regen spells — serenity / profane link for a priest, mana
        // flux / nature tap for the others; a same-magery lookalike like ethereal
        // taint, or the Magery-0 item-granted "bone necklace", is never in the list).
        // Fall back to the first curated short, then any same-magery roll spell. All
        // matches are magery-scoped. KnownSpell is a struct → project to KnownSpell?
        // for a real null on no match.
        string[] rollShorts = RegenShortsFor(inputs.MageryType);
        KnownSpell? FirstRoll(Func<KnownSpell, bool> match) =>
            _rollSpells.Where(match).Select(s => (KnownSpell?)s).FirstOrDefault();
        KnownSpell? spell =
            FirstRoll(s => s.Magery == inputs.MageryType
                        && string.Equals(s.Name, SelectedManaRollSpell, StringComparison.OrdinalIgnoreCase))
            ?? FirstRoll(s => s.Magery == inputs.MageryType
                        && rollShorts.Contains(s.Short.Trim(), StringComparer.OrdinalIgnoreCase))
            ?? FirstRoll(s => s.Magery == inputs.MageryType);
        ManaHasRollSpell = spell is not null;

        if (spell is not { } roll)
        {
            ManaRollSpellText = "—";
            ManaRollRangeText = "—";
            ManaWorstTickText = "—";
            ManaBestTickText = "—";
            ManaRollMin = 0;
            ManaRollMax = 0;
            ManaSampleReadoutText = "—";
            return;
        }

        ManaRollSpellText = roll.Name;
        (long rmin, long rmax) = SpellCalculator.AffectMagnitude(roll.Formula, Math.Max(1, ManaLevel));
        ManaRegenBreakpointCalculator.Result r =
            ManaRegenBreakpointCalculator.Compute(inputs, (int)rmin, (int)rmax);

        ManaRollRangeText = $"{r.RollMin}–{r.RollMax} %ManaRgn at level {Math.Max(1, ManaLevel)}";
        ManaWorstTickText = TickText(r.WorstTick);
        ManaBestTickText = TickText(r.BestTick);

        // Slider bounds follow the level-scaled range; keep the sample inside it,
        // defaulting to the best roll when the range shifted out from under it.
        ManaRollMin = r.RollMin;
        ManaRollMax = r.RollMax;
        double clampedSample = ManaRollSample < r.RollMin || ManaRollSample > r.RollMax
            ? r.RollMax
            : ManaRollSample;
        if (Math.Abs(clampedSample - ManaRollSample) > double.Epsilon)
            ManaRollSample = clampedSample;   // fires the readout via OnManaRollSampleChanged
        else
            UpdateManaSampleReadout();
    }

    // Natural tick (no spell) vs level over the [level−1, level+3] window, one
    // series per magery-regen-stat value from the current up in +5 steps — so the
    // breakpoint step-ups across levels AND the gain from allocating stat points
    // are both visible. The stepped stat is the one that actually drives regen (via
    // WithMageryStat): INT for a mage, WIL for a priest, the (INT+WIL)/2 average for
    // a druid — the druid series feeds INT=WIL=value, which yields exactly that
    // average. Series stop at MageryStatCap so no line exceeds the racial trained max.
    private void BuildManaChart(ManaRegenBreakpointCalculator.Inputs inputs)
    {
        int lo = Math.Max(1, ManaLevel - 1);
        int hi = Math.Max(lo, ManaLevel + 3);

        string label = MageryStatKind(inputs.MageryType).Label;
        int floor = MageryStatFloor(inputs.MageryType);
        int cap = MageryStatCap(inputs.MageryType);
        int baseStat = Math.Max(floor, MageryStatValue(inputs.MageryType, ManaIntellect, ManaWillpower));

        var series = new List<ManaRegenChartSeries>();
        for (int k = 0; k < ChartPalette.Length; k++)
        {
            int sv = baseStat + k * 5;
            // Never draw a magery-stat line past what any race could train to (but
            // always keep the base line, even if it's already at/over the cap).
            if (k > 0 && sv > cap) break;
            var ticks = new int[hi - lo + 1];
            for (int lv = lo; lv <= hi; lv++)
                ticks[lv - lo] = ManaRegenBreakpointCalculator.Tick(WithMageryStat(inputs with { Level = lv }, sv), 0);
            series.Add(new ManaRegenChartSeries($"{label} {sv}", ChartPalette[k], ticks));
        }
        int currentTick = ManaRegenBreakpointCalculator.Tick(inputs, 0);
        ManaChart = new ManaRegenChartData(lo, hi, Math.Clamp(ManaLevel, lo, hi), currentTick, series);
    }

    // "You are here" + how far the NEXT whole-MP tick is, measured three
    // independent ways from the current level / stat / gear: magery stat points,
    // levels, and +ManaRgn%. Each search raises one axis until the tick steps up.
    private void ComputeManaDistances(ManaRegenBreakpointCalculator.Inputs inputs)
    {
        int cur = ManaRegenBreakpointCalculator.Tick(inputs, 0);
        int target = cur + 1;
        bool druid = inputs.MageryType == 3;
        string label = MageryStatKind(inputs.MageryType).Label;
        int curStat = MageryStatValue(inputs.MageryType, inputs.Intellect, inputs.Willpower);
        ManaCurrentTickText = $"{cur} MP/tick  •  level {inputs.Level}  •  {label} {curStat}";

        int? statDelta = null;
        for (int d = 1; d <= 300; d++)
            if (ManaRegenBreakpointCalculator.Tick(WithMageryStat(inputs, curStat + d), 0) >= target) { statDelta = d; break; }
        ManaNextByStatText = statDelta is { } sd
            ? (druid ? $"+{sd} magery stat  (raise INT+WIL so the avg climbs {sd})" : $"+{sd} {label}")
            : "> 300";

        int? lvlDelta = null;
        for (int d = 1; d <= 300; d++)
            if (ManaRegenBreakpointCalculator.Tick(inputs with { Level = inputs.Level + d }, 0) >= target)
            { lvlDelta = d; break; }
        ManaNextByLevelText = lvlDelta is { } ld ? $"+{ld} level{(ld == 1 ? "" : "s")}" : "> 300";

        int? regenDelta = null;
        for (int d = 1; d <= 500; d++)
            if (ManaRegenBreakpointCalculator.Tick(inputs, d) >= target) { regenDelta = d; break; }
        ManaNextByRegenText = regenDelta is { } rd ? $"+{rd}% ManaRgn (gear or roll)" : "> 500";
    }

    // Live readout for the "model a roll" slider: the picked roll value, where it
    // sits in the range (0-100%), and the tick it produces.
    private void UpdateManaSampleReadout()
    {
        if (!ManaHasRollSpell || ManaRollMax <= 0)
        {
            ManaSampleReadoutText = "—";
            return;
        }
        int roll = (int)Math.Round(ManaRollSample);
        roll = Math.Clamp(roll, ManaRollMin, ManaRollMax);
        double frac = ManaRollMax > ManaRollMin
            ? (double)(roll - ManaRollMin) / (ManaRollMax - ManaRollMin)
            : 0;
        int tick = ManaRegenBreakpointCalculator.Tick(BuildManaInputs(), roll);
        ManaSampleReadoutText = $"roll {roll}  •  {frac * 100:0}% of range  •  {tick} MP/tick";
    }

    private static string TickText(int tick)
        => $"{tick} MP / {ManaRegenBreakpointCalculator.PassiveTickSeconds}s  (~{tick * 60 / ManaRegenBreakpointCalculator.PassiveTickSeconds} MP/min)";

    public CalculatorsSectionViewModel(PlayerStats stats, GameDataCache gameData, InventoryManager inventory, QuestBonusState questBonuses, ProfileService profile, LeaderboardSnapshotStore leaderboards)
    {
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(gameData);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(questBonuses);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(leaderboards);
        _stats = stats;
        _gameData = gameData;
        _spellCatalog = new KnownSpellCatalog(gameData);
        _inventory = inventory;
        _questBonuses = questBonuses;
        _profile = profile;
        _leaderboards = leaderboards;
        LeaderboardView = new DataGridCollectionView(Leaderboard);

        EncumbranceHopTimes stockHops = new();
        StockMoveFastBandText = string.Create(CultureInfo.InvariantCulture,
            $"~{stockHops.None:0.0} s / hop");
        StockMoveSlowBandText = string.Create(CultureInfo.InvariantCulture,
            $"~{stockHops.Heavy:0.0} s / hop");

        _stats.PropertyChanged += OnStatsChanged;
        _inventory.Changed += OnInventoryChanged;
        _questBonuses.Changed += OnQuestBonusesChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _leaderboards.Changed += OnLeaderboardChanged;
        EnsureMonsterNames();
        EnsureWeaponNames();
        EnsureClassFilterOptions();
        SeedAll();
        RebuildLeaderboard();
    }

    // Full (re)seed: refresh the actual snapshot, push it into the editable
    // inputs, and rebuild the monster side. Runs on construction and profile
    // load — the two moments the inputs should adopt the live character.
    private void SeedAll()
    {
        CaptureActuals();
        SeedInputsFromActuals();
        RebuildMonster();
    }

    // Live refresh: recompute the actual snapshot (keeping the Reset buttons and
    // the Backstab read-out current) and re-run the read-only projections, but
    // leave every editable input exactly as the user left it.
    private void RefreshActuals()
    {
        CaptureActuals();
        RecomputeBackstab();
        RecomputeOutgoing();
        RecomputeAllRows();
    }

    // Re-aggregate worn gear + innate race/class bonuses + completed-quest
    // rewards into a combined stat total, then derive the player's offense and
    // defense numbers. This repeats the Character Info tab's aggregation rather
    // than sharing it: the two tabs consume the result for different views, and
    // routing them through one helper would entangle the delicate combat math
    // for no real gain. Second occurrence — extract only if a third appears.
    private void CaptureActuals()
    {
        _capturing = true;
        try
        {
            IReadOnlyList<EquippedItem> worn = _inventory.Snapshot.EquippedItems;
            EquipmentStatBreakdown combined = CharacterCalculator.AggregateEquipmentStats(worn, _gameData);
            JsonElement? classRow = _gameData.FindRowByName("Classes", _stats.Class);
            JsonElement? raceRow = _gameData.FindRowByName("Races", _stats.Race);
            if (raceRow is JsonElement r) CharacterCalculator.ApplyAbilityBonuses(combined, r, _stats.Race);
            if (classRow is JsonElement c) CharacterCalculator.ApplyAbilityBonuses(combined, c, _stats.Class);
            CharacterCalculator.ApplyQuestBonuses(combined, _questBonuses.Bonuses, "Quests");
            EquipmentStatSummary t = combined.Totals;

            _realm = _gameData.ActiveRealm;
            IsParadigmRealm = _realm == RealmType.ParaMud;
            _level = _stats.Level;
            _nCombatLevel = GetInt(classRow, "CombatLVL");
            _str = _stats.Strength;
            _agi = _stats.Agility;
            _intel = _stats.Intellect;
            _chm = _stats.Charm;
            _wil = _stats.Willpower;

            // Mana-regen inputs: the summed +ManaRgn% (gear + race/class + quests)
            // and the loaded class's magery type/tier drive the calculator's seed.
            _manaGearRegenPct = t.MpRegenPercent;
            _liveMageryType = GetInt(classRow, "MageryType");
            _liveMageryLevel = GetInt(classRow, "MageryLVL");
            EncumbranceReading encum = _inventory.Snapshot.Encumbrance;
            _encumCur = encum.CurrentWeight;
            _encumMax = encum.MaxWeight;

            // Accuracy inputs. TotalWornAccy + the effective Abil-22 term feed the
            // weapon attacks; the martial-arts strikes drop the weapon-hand accy
            // (a strike doesn't use the wielded weapon's accuracy) and add their
            // own per-strike bonuses.
            _totalWornAccy = t.TotalWornAccy;
            _effectiveAbil22 = _realm == RealmType.ParaMud ? t.PlusAccuracy : t.MaxSingleAbil22;
            _maWornAccy = Math.Max(0, t.TotalWornAccy - t.WeaponHandAccy - t.OffHandAccy);
            _plusPunchAccy = t.PlusPunchAccy;
            _plusKickAccy = t.PlusKickAccy;
            _plusJumpKickAccy = t.PlusJumpKickAccy;
            _plusPunchDmg = t.PlusPunchDmg;
            _plusKickDmg = t.PlusKickDmg;
            _plusJumpKickDmg = t.PlusJumpKickDmg;

            // Attack-type availability: Attack / Bash are universal; Smash rides on
            // the smash-capable class list; the Mystic strikes need the granting
            // class ability. Refresh the dropdown before computing accuracy so the
            // selection is valid for the current class.
            HashSet<string>? smashClasses = ClassCapabilities.GetSmashCapableClasses(_gameData);
            _canSmash = smashClasses is null
                || (!string.IsNullOrEmpty(_stats.Class) && smashClasses.Contains(_stats.Class));
            _hasPunch = ClassCapabilities.ClassHasPunch(classRow);
            _hasKick = ClassCapabilities.ClassHasKick(classRow);
            _hasJumpKick = ClassCapabilities.ClassHasJumpKick(classRow);
            RebuildAttackTypeOptions();

            // Capture the offense inputs, then derive accuracy / damage / swings /
            // crit for the selected attack type through the shared helpers so the
            // weapon picker + attack-type dropdown can re-run them in isolation.
            _plusMaxDamage = t.PlusMaxDamage;
            _plusMinDamage = t.PlusMinDamage;   // abil 1 "Damage" — the low-end add, was previously dropped
            _plusCrits = t.PlusCrits;
            _equipWeaponMin = t.WeaponMin;
            _equipWeaponMax = t.WeaponMax;
            _equipWeaponSpeed = t.WeaponSpeed;
            _equipWeaponStrReq = t.WeaponStrReq;
            _attackAccuracy = ComputeAttackAccuracy(MatchupAttackTypeFor(SelectedMatchupAttackType));
            ComputeOffense();

            _actualAc = _stats.ArmourClass;
            _actualDodge = CombatCalculator.CalcDodge(_level, _agi, _chm, t.PlusDodge, _encumCur, _encumMax);
            _protEvil = t.PlusProtEvil;
            _protGood = t.PlusProtGood;
            _damageResist = (int)Math.Round(t.PlusDR, MidpointRounding.AwayFromZero);

            _actualEncumPercent = encum.Percentage;
            _plusQuickness = t.PlusQuickness;

            _stealth = _stats.Stealth;
            _plusBsMin = t.PlusBSMin;
            _plusBsMax = t.PlusBSMax;
            _hasClassStealth = ClassCapabilities.ClassHasStealth(classRow);
            _backstabDefaultWeaponLabel = ResolveBackstabSetWeaponLabel();
            // Keep whatever backstab weapon is selected; refresh its damage range.
            ResolveBackstabWeapon();
        }
        finally
        {
            _capturing = false;
        }
    }

    // Push the captured actuals into the editable inputs. Runs only when the
    // inputs should adopt the live character — construction, profile load, and
    // (via the Reset commands, which re-seed their own subset) an explicit reset.
    private void SeedInputsFromActuals()
    {
        PlayerAccuracy = _attackAccuracy;
        PlayerAc = _actualAc;
        PlayerDodge = _actualDodge;

        MoveEncumbrance = _actualEncumPercent;
        MoveQuickness = _plusQuickness;
        MoveSlowness = 0;
        // Seeding to a value that equals the current backing field won't fire the
        // setter, so recompute explicitly to guarantee the outputs are current.
        RecomputeMovement();

        SwingLevel = _level;
        SwingCombatLevel = _nCombatLevel;
        SwingAgility = _agi;
        SwingStrength = _str;
        SwingEncumbrance = _actualEncumPercent;
        ResolveSwingWeapon();
        RecomputeSwing();

        SelectedBackstabWeaponName = _backstabDefaultWeaponLabel;
        ResolveBackstabWeapon();
        RecomputeBackstab();

        SeedManaFromActuals();
    }

    // Accuracy for a given attack type from the captured inputs. Normal / Bash /
    // Smash key off the equipped weapon's str-req (the picker deliberately leaves
    // accuracy alone); the Mystic strikes use the weapon-hand-excluded worn accy,
    // add their per-strike accy bonus, and take GreaterMUD's kick / jumpkick
    // accuracy penalty (Stock has none).
    private int ComputeAttackAccuracy(MudAttackType type)
    {
        if (_level <= 0 || _nCombatLevel <= 0) return 0;

        if (IsMartialArts(type))
        {
            int maBase = CombatCalculator.CalcAccuracy(
                MudAttackType.Normal, _realm, _level, _nCombatLevel,
                _str, _agi, _intel, _chm, _maWornAccy, _effectiveAbil22,
                _encumCur, _encumMax, weaponStrReq: 0);
            int bonus = type switch
            {
                MudAttackType.Punch => _plusPunchAccy,
                MudAttackType.Kick => _plusKickAccy,
                MudAttackType.Jumpkick => _plusJumpKickAccy,
                _ => 0,
            };
            int penalty = _realm == RealmType.ParaMud
                ? type switch { MudAttackType.Kick => 10, MudAttackType.Jumpkick => 15, _ => 0 }
                : 0;
            return maBase + bonus - penalty;
        }

        return CombatCalculator.CalcAccuracy(type, _realm, _level, _nCombatLevel,
            _str, _agi, _intel, _chm, _totalWornAccy, _effectiveAbil22,
            _encumCur, _encumMax, _equipWeaponStrReq);
    }

    // Rebuild the attack-type dropdown to just the strikes the class can perform.
    // SequenceEqual guards against needless collection churn on live refreshes
    // (the capability set only changes on a profile / class swap).
    private void RebuildAttackTypeOptions()
    {
        var desired = new List<string> { AttackBase, "Bash" };
        if (_canSmash) desired.Add("Smash");
        if (_hasPunch) desired.Add("Punch");
        if (_hasKick) desired.Add("Kick");
        if (_hasJumpKick) desired.Add("Jumpkick");
        if (MatchupAttackTypeOptions.SequenceEqual(desired)) return;

        MatchupAttackTypeOptions.Clear();
        foreach (string d in desired) MatchupAttackTypeOptions.Add(d);
        if (!desired.Contains(SelectedMatchupAttackType))
            SelectedMatchupAttackType = AttackBase;
    }

    // Derive avg damage / swings / crit for the selected attack type from the
    // captured offense inputs, honoring the weapon-picker override (empty picker =
    // the equipped weapon). Accuracy is computed separately in ComputeAttackAccuracy.
    private void ComputeOffense()
    {
        MudAttackType type = MatchupAttackTypeFor(SelectedMatchupAttackType);
        if (IsMartialArts(type))
            ComputeMartialArtsOffense(type);
        else
            ComputeWeaponOffense(type);
    }

    // Normal / Bash / Smash against the picked-or-equipped weapon.
    private void ComputeWeaponOffense(MudAttackType type)
    {
        int wMin = _hasSelectedWeapon ? _selWeaponMin : _equipWeaponMin;
        int wMax = _hasSelectedWeapon ? _selWeaponMax : _equipWeaponMax;
        int wSpeed = _hasSelectedWeapon ? _selWeaponSpeed : _equipWeaponSpeed;
        int wStrReq = _hasSelectedWeapon ? _selWeaponStrReq : _equipWeaponStrReq;

        // Damage / swings / crit through the shared CombatCalculator helper, so
        // this and Monster Intel's rounds-to-kill read identically (it carries the
        // +MinDamage add — abil 1 — the old inline call here silently dropped).
        MeleeOffense off = CombatCalculator.ComputeMeleeOffense(
            type, _realm, _level, _nCombatLevel, _str, _agi, wMin, wMax, wSpeed, wStrReq,
            _plusMaxDamage, _plusMinDamage, _plusCrits, _encumCur, _encumMax);
        _hasWeapon = off.HasWeapon;
        _avgWeaponDamage = off.AvgDamage;
        _swingsPerRound = off.SwingsPerRound;
        _critChance = off.CritChance;
        _avgCritDamage = off.AvgCritDamage;
    }

    // Punch / Kick / Jumpkick — a bare-handed strike whose fixed attack speed
    // stands in for a weapon's and which carries no strength requirement. The
    // strike itself is the damage source, so the projection is always "armed".
    // Crit is not modelled (no verified QnD interaction for the strikes).
    private void ComputeMartialArtsOffense(MudAttackType type)
    {
        _hasWeapon = true;
        int maPlusDmg = type switch
        {
            MudAttackType.Punch => _plusPunchDmg,
            MudAttackType.Kick => _plusKickDmg,
            MudAttackType.Jumpkick => _plusJumpKickDmg,
            _ => 0,
        };
        // No stock ability grants a +MA-skill bonus; the calc floors it to 1.
        const int maPlusSkill = 1;
        MeleeDamageResult dmg = CombatCalculator.CalcMartialArtsDamage(
            type, _realm, _level, maPlusSkill, _str, _plusMaxDamage, maPlusDmg);
        _avgWeaponDamage = (dmg.MinDamage + dmg.MaxDamage) / 2;

        int speed = CombatCalculator.MartialArtsSpeed(type, _realm);
        SwingCalcResult swings = CombatCalculator.CalcSwings(
            _nCombatLevel, _level, speed, _agi, _str, weaponStrReq: 0,
            _encumCur, _encumMax, realmType: _realm);
        _swingsPerRound = swings.RawSwings;

        _critChance = 0;
        _avgCritDamage = 0;
    }

    private static MudAttackType MatchupAttackTypeFor(string? label) => label switch
    {
        "Bash" => MudAttackType.Bash,
        "Smash" => MudAttackType.Smash,
        "Punch" => MudAttackType.Punch,
        "Kick" => MudAttackType.Kick,
        "Jumpkick" => MudAttackType.Jumpkick,
        _ => MudAttackType.Normal,
    };

    private static bool IsMartialArts(MudAttackType type) =>
        type is MudAttackType.Punch or MudAttackType.Kick or MudAttackType.Jumpkick;

    // Populate the typeahead once from the active set. Cheap to retry if the set
    // wasn't loaded at construction (no monsters yet).
    private void EnsureMonsterNames()
    {
        if (MonsterNames.Count > 0) return;
        JsonDocument? doc = _gameData.GetRawTable("Monsters");
        if (doc is null) return;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Name", out JsonElement nameEl)) continue;
            if (nameEl.ValueKind != JsonValueKind.String) continue;
            string? name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) continue;

            int number = row.TryGetProperty("Number", out JsonElement numEl)
                         && numEl.ValueKind == JsonValueKind.Number && numEl.TryGetInt32(out int n)
                ? n : 0;
            string label = string.Create(CultureInfo.InvariantCulture, $"{name} (#{number})");
            MonsterNames.Add(label);
            _monsterNumberByLabel[label] = number;
        }
    }

    // Populate the weapon typeahead once from the active set. Weapons are Items
    // with ItemType == 1 (armour is 0). Cheap to retry if the set wasn't loaded
    // at construction (no items yet).
    private void EnsureWeaponNames()
    {
        if (WeaponNames.Count > 0) return;
        JsonDocument? doc = _gameData.GetRawTable("Items");
        if (doc is null) return;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (GetInt(row, "ItemType") != 1) continue;
            if (!row.TryGetProperty("Name", out JsonElement nameEl)) continue;
            if (nameEl.ValueKind != JsonValueKind.String) continue;
            string? name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) continue;

            int number = GetInt(row, "Number");
            string label = string.Create(CultureInfo.InvariantCulture, $"{name} (#{number})");
            WeaponNames.Add(label);
            _weaponNumberByLabel[label] = number;
        }
    }

    // Resolve an Items record by its Number — names aren't unique, so the
    // typeahead label carries the number and we look up against it.
    private JsonElement? FindItemRowByNumber(int number)
    {
        JsonDocument? doc = _gameData.GetRawTable("Items");
        if (doc is null) return null;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.TryGetProperty("Number", out JsonElement n)
                && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int v) && v == number)
                return row;
        }
        return null;
    }

    // Resolve the exact monster record by its Number — names aren't unique, so
    // the typeahead label carries the number and we look up against it.
    private JsonElement? FindMonsterRowByNumber(int number)
    {
        JsonDocument? doc = _gameData.GetRawTable("Monsters");
        if (doc is null) return null;
        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (row.TryGetProperty("Number", out JsonElement n)
                && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int v) && v == number)
                return row;
        }
        return null;
    }

    partial void OnSelectedMonsterNameChanged(string? value) => RebuildMonster();

    partial void OnSelectedWeaponNameChanged(string? value)
    {
        ResolveSelectedWeapon();
        ComputeOffense();
        RecomputeOutgoing();
    }

    // Switching attack type is a fresh modelling choice: re-seed the accuracy
    // input to that type's actual (each type has its own accuracy), re-derive the
    // offense, and refresh the projection. Suppressed while CaptureActuals is
    // mid-rebuild (it recomputes these itself once every field is in place).
    partial void OnSelectedMatchupAttackTypeChanged(string value)
    {
        if (_capturing) return;
        _attackAccuracy = ComputeAttackAccuracy(MatchupAttackTypeFor(value));
        PlayerAccuracy = _attackAccuracy;
        ComputeOffense();
        RecomputeOutgoing();
    }

    // Load the picked weapon's damage / speed / str-req, or clear the override
    // (fall back to the equipped weapon) when the picker is empty or unmatched.
    private void ResolveSelectedWeapon()
    {
        if (SelectedWeaponName is not null
            && _weaponNumberByLabel.TryGetValue(SelectedWeaponName, out int number)
            && FindItemRowByNumber(number) is JsonElement row)
        {
            _selWeaponMin = GetInt(row, "Min");
            _selWeaponMax = GetInt(row, "Max");
            _selWeaponSpeed = GetInt(row, "Speed");
            _selWeaponStrReq = GetInt(row, "StrReq");
            _hasSelectedWeapon = true;
        }
        else
        {
            _hasSelectedWeapon = false;
        }
    }

    partial void OnPlayerAccuracyChanged(int value) => RecomputeOutgoing();
    partial void OnPlayerAcChanged(int value) => RecomputeAllRows();
    partial void OnPlayerDodgeChanged(int value) => RecomputeAllRows();
    partial void OnMonsterAcChanged(int value) => RecomputeOutgoing();
    partial void OnMonsterDrChanged(int value) => RecomputeOutgoing();
    partial void OnMonsterDodgeChanged(int value) => RecomputeOutgoing();

    // Seed the editable monster values + attack rows from the picked monster (or
    // reset to a blank, single-row state when the name is cleared) so the calc is
    // usable as a general-purpose tool with hand-entered numbers on either side.
    private void RebuildMonster()
    {
        EnsureMonsterNames();
        MonsterAttacks.Clear();

        if (string.IsNullOrEmpty(SelectedMonsterName)
            || !_monsterNumberByLabel.TryGetValue(SelectedMonsterName, out int monsterNumber)
            || FindMonsterRowByNumber(monsterNumber) is not JsonElement row)
        {
            _monsterIsEvil = _monsterIsGood = false;
            MonsterAc = MonsterDr = MonsterDodge = 0;
            MonsterAttacks.Add(NewAttackRow("—", 50));
            RenumberAttacks();
            RecomputeAllRows();
            RecomputeOutgoing();
            return;
        }

        int align = GetInt(row, "Align");
        _monsterIsEvil = align is 1 or 2 or 5 or 6;
        _monsterIsGood = align is 0 or 4;

        MonsterAc = GetInt(row, "ArmourClass");
        MonsterDr = GetInt(row, "DamageResist");
        // Dodge isn't a top-level column — it rides in the monster's ability
        // slots (Abil-N == 34), like Lord of the Hunt's 70 dodge.
        MonsterDodge = GetAbilityValue(row, 34);

        // Enumerate every physical attack (melee = 1, rob = 3) into an editable
        // row. Spell slots (type 2) carry spell metadata in those columns, so we
        // skip them.
        for (int i = 0; i < 5; i++)
        {
            int type = GetInt(row, $"AttType-{i}");
            if (type is not (1 or 3)) continue;
            int acc = GetInt(row, $"AttAcc-{i}");
            int min = GetInt(row, $"AttMin-{i}");
            int max = GetInt(row, $"AttMax-{i}");
            string damage = string.Create(CultureInfo.InvariantCulture, $"{min}-{max}");
            MonsterAttacks.Add(NewAttackRow(damage, acc));
        }

        // A caster / passive monster with no physical slot still gets one editable
        // row so the incoming calculator isn't left empty.
        if (MonsterAttacks.Count == 0)
            MonsterAttacks.Add(NewAttackRow("—", 50));

        RenumberAttacks();
        RecomputeAllRows();
        RecomputeOutgoing();
    }

    private MonsterAttackRowViewModel NewAttackRow(string damage, int accuracy)
        => new(string.Empty, damage, accuracy, RecomputeRow, RemoveAttackRow);

    // Keep the labels sequential ("Attack 1..N") after any add / remove / rebuild.
    private void RenumberAttacks()
    {
        for (int i = 0; i < MonsterAttacks.Count; i++)
            MonsterAttacks[i].Label = string.Create(CultureInfo.InvariantCulture, $"Attack {i + 1}");
    }

    // Append a fresh editable attack row (custom what-if attack).
    [RelayCommand]
    private void AddAttack()
    {
        MonsterAttackRowViewModel row = NewAttackRow("—", 50);
        MonsterAttacks.Add(row);
        RenumberAttacks();
        RecomputeRow(row);
    }

    // "Show me the Monsters": turn the hit-% picker into the monster accuracy that
    // hits you that often (inverting the live hit formula against your AC + dodge),
    // then open the Monsters game-data tab filtered to Acc ≥ that accuracy.
    [RelayCommand]
    private void ShowMonstersAtHitPercent()
    {
        int acc = CombatCalculator.AccuracyForHitChance(HitPercentTarget, PlayerAc, PlayerDodge, _realm);
        AppServices.Current.OpenMonstersWithAccuracy(acc);
    }

    // Row-invoked removal. Keep at least one row so the incoming calc is never
    // empty; clearing the last row just resets it to a blank custom attack.
    private void RemoveAttackRow(MonsterAttackRowViewModel row)
    {
        MonsterAttacks.Remove(row);
        if (MonsterAttacks.Count == 0)
            MonsterAttacks.Add(NewAttackRow("—", 50));
        RenumberAttacks();
        RecomputeAllRows();
    }

    // You → Monster projection. Uses the editable player accuracy (so tweaking the
    // ticker moves the hit% and DPS) against the editable monster AC / DR. Always
    // computed — with no monster picked the values default to 0, hand-editable.
    private void RecomputeOutgoing()
    {
        // Only the monster's AC / DR feed the player-side outputs we show; the
        // attack fields drive the return direction (computed per-row instead) and
        // HP would only drive rounds-to-kill (dropped — this isn't a round sim),
        // so they can stay zeroed here.
        var monster = new MonsterMatchupProfile(
            ArmourClass: MonsterAc,
            DamageResist: MonsterDr,
            Hp: 0,
            Dodge: MonsterDodge,
            HasPhysicalAttack: false,
            AttackAccuracy: 0,
            AvgAttackDamage: 0,
            IsEvil: _monsterIsEvil,
            IsGood: _monsterIsGood);

        var player = new PlayerMatchupProfile(
            Realm: _realm,
            NormalAccuracy: PlayerAccuracy,
            AvgWeaponDamage: _avgWeaponDamage,
            SwingsPerRound: _swingsPerRound,
            HasWeapon: _hasWeapon,
            ArmourClass: PlayerAc,
            Dodge: PlayerDodge,
            ProtEvil: _protEvil,
            ProtGood: _protGood,
            DamageResist: _damageResist,
            CritChancePercent: _critChance,
            AvgCritDamage: _avgCritDamage);

        MonsterMatchupResult res = MonsterMatchupCalculator.Compute(player, monster);
        MatchupHasWeapon = res.HasWeapon;
        MatchupPlayerHit = $"{res.PlayerHitPercent}%";
        MatchupPlayerDamage = $"{res.PlayerDamagePerHit} / hit";
        MatchupSwings = res.HasWeapon
            ? res.PlayerSwingsPerRound.ToString("0.0", CultureInfo.InvariantCulture)
            : "—";
        MatchupDps = res.HasWeapon
            ? res.PlayerDps.ToString("0.0", CultureInfo.InvariantCulture)
            : "—";
    }

    // Return-hit chance of one monster attack against the current (editable)
    // player AC + dodge, with the player's ward applied only for the matching
    // alignment. Mirrors MonsterMatchupCalculator's Monster → player direction.
    private int HitFor(int accuracy) => CombatCalculator.CalculateHitChance(
        attackerAccuracy: accuracy,
        defenderAC: PlayerAc,
        defenderDodge: PlayerDodge,
        protEvil: _monsterIsEvil ? _protEvil : 0,
        protGood: _monsterIsGood ? _protGood : 0,
        realmType: _realm).OverallHitPercent;

    private void RecomputeRow(MonsterAttackRowViewModel row)
        => row.HitPercent = string.Create(CultureInfo.InvariantCulture, $"{HitFor(row.Accuracy)}%");

    private void RecomputeAllRows()
    {
        foreach (MonsterAttackRowViewModel row in MonsterAttacks) RecomputeRow(row);
    }

    // Discard manual weapon / accuracy / AC / dodge edits and re-seed from the live
    // actuals. Accuracy re-seeds to the currently-selected attack type's value.
    [RelayCommand]
    private void ResetDefenses()
    {
        SelectedWeaponName = null;
        PlayerAccuracy = _attackAccuracy;
        PlayerAc = _actualAc;
        PlayerDodge = _actualDodge;
    }

    // ----- Movement Speed --------------------------------------------------

    partial void OnMoveEncumbranceChanged(int value) => RecomputeMovement();
    partial void OnMoveQuicknessChanged(int value) => RecomputeMovement();
    partial void OnMoveSlownessChanged(int value) => RecomputeMovement();

    // Solve the movement speed vs the 1-second cap and phrase the advice. "Above
    // cap" means faster than the cap (quickness to spare, since the game clamps
    // movement at one second); "too slow" means more quickness is needed.
    private void RecomputeMovement()
    {
        MovementSpeedResult res = MovementSpeedCalculator.Compute(MoveEncumbrance, MoveQuickness, MoveSlowness);
        MoveSpeedText = string.Create(CultureInfo.InvariantCulture, $"{res.SpeedMillis / 1000.0:0.00} s");
        (MoveStatusText, MoveAdviceText) = res.State switch
        {
            MovementCapState.AboveCap => ("Above cap",
                string.Create(CultureInfo.InvariantCulture,
                    $"You can shed {res.QuicknessToCap:0.0} quickness and stay capped.")),
            MovementCapState.TooSlow => ("Too slow",
                string.Create(CultureInfo.InvariantCulture,
                    $"Need {res.QuicknessToCap:0.0} more quickness to reach the 1-second cap.")),
            _ => ("Perfect", "Exactly at the 1-second movement cap."),
        };
    }

    // Discard manual movement edits and re-seed encumbrance / quickness from the live actuals.
    [RelayCommand]
    private void ResetMovement()
    {
        MoveEncumbrance = _actualEncumPercent;
        MoveQuickness = _plusQuickness;
        MoveSlowness = 0;
    }

    // ----- Swings ----------------------------------------------------------

    partial void OnSelectedSwingWeaponNameChanged(string? value)
    {
        ResolveSwingWeapon();
        RecomputeSwing();
    }

    partial void OnSwingLevelChanged(int value) => RecomputeSwing();
    partial void OnSwingCombatLevelChanged(int value) => RecomputeSwing();
    partial void OnSwingAgilityChanged(int value) => RecomputeSwing();
    partial void OnSwingStrengthChanged(int value) => RecomputeSwing();
    partial void OnSwingEncumbranceChanged(int value) => RecomputeSwing();
    partial void OnSwingSpeedOptionChanged(string value) => RecomputeSwing();
    partial void OnSwingBashingChanged(bool value) => RecomputeSwing();
    partial void OnSwingSlownessChanged(bool value) => RecomputeSwing();

    // Load the picked swing weapon's speed / str-req, or fall back to the equipped
    // weapon when the picker is empty or unmatched.
    private void ResolveSwingWeapon()
    {
        if (SelectedSwingWeaponName is not null
            && _weaponNumberByLabel.TryGetValue(SelectedSwingWeaponName, out int number)
            && FindItemRowByNumber(number) is JsonElement row)
        {
            _swingWeaponSpeed = GetInt(row, "Speed");
            _swingWeaponStrReq = GetInt(row, "StrReq");
        }
        else
        {
            _swingWeaponSpeed = _equipWeaponSpeed;
            _swingWeaponStrReq = _equipWeaponStrReq;
        }
    }

    // Run the full swing model (energy per swing → 1000-budget swings with the
    // round remainder carried forward) for the picked weapon + modifiers.
    private void RecomputeSwing()
    {
        SwingHasWeapon = _swingWeaponSpeed > 0;
        SwingRounds.Clear();

        if (!SwingHasWeapon)
        {
            SwingEnergyText = SwingRawText = SwingEncumText = SwingQndText = "—";
            return;
        }

        int speedModifier = SwingSpeedOption switch
        {
            "Sped (85)" => 85,
            "Slow (125)" => 125,
            _ => 100,
        };

        // CalcSwings derives the encumbrance % from current/max weight; the swing
        // calc exposes the percentage directly, so pass it against a max of 100
        // to make the internal ratio equal the entered percent.
        SwingCalcResult res = CombatCalculator.CalcSwings(
            SwingCombatLevel, SwingLevel, _swingWeaponSpeed, SwingAgility, SwingStrength,
            _swingWeaponStrReq, currentEncum: SwingEncumbrance, maxEncum: 100,
            speedModifier: speedModifier, hasSlowness: SwingSlowness,
            isBashing: SwingBashing, realmType: _realm);

        SwingEnergyText = res.EnergyPerSwing.ToString(CultureInfo.InvariantCulture);
        SwingRawText = res.RawSwings.ToString("0.0", CultureInfo.InvariantCulture);
        SwingEncumText = string.Create(CultureInfo.InvariantCulture, $"{res.EncumPercent}%");
        SwingQndText = res.QnDCritBonus.ToString(CultureInfo.InvariantCulture);

        for (int i = 0; i < res.SwingsPerRound.Length; i++)
            SwingRounds.Add(new SwingRoundRow(i + 1, res.SwingsPerRound[i], res.EnergyRemaining[i]));
    }

    // Discard manual swing edits and re-seed weapon / stats from the live actuals.
    [RelayCommand]
    private void ResetSwing()
    {
        SelectedSwingWeaponName = null;
        SwingLevel = _level;
        SwingCombatLevel = _nCombatLevel;
        SwingAgility = _agi;
        SwingStrength = _str;
        SwingEncumbrance = _actualEncumPercent;
        SwingSpeedOption = "Normal (100)";
        SwingBashing = false;
        SwingSlowness = false;
    }

    // ----- Backstab --------------------------------------------------------

    partial void OnSelectedBackstabWeaponNameChanged(string? value)
    {
        ResolveBackstabWeapon();
        RecomputeBackstab();
    }

    // Load the picked backstab weapon's damage range, or clear it (no result)
    // when the picker is empty or unmatched. Unlike the swing calc, there is no
    // equipped-weapon fallback: the picker defaults to the Backstab set's weapon.
    private void ResolveBackstabWeapon()
    {
        if (SelectedBackstabWeaponName is not null
            && _weaponNumberByLabel.TryGetValue(SelectedBackstabWeaponName, out int number)
            && FindItemRowByNumber(number) is JsonElement row)
        {
            _bsWeaponMin = GetInt(row, "Min");
            _bsWeaponMax = GetInt(row, "Max");
        }
        else
        {
            _bsWeaponMin = _bsWeaponMax = 0;
        }
    }

    // Run the realm-aware backstab range (CalcBSDamage folds strength into the
    // weapon bounds, applies the +BS ability bonuses, and scales by class vs
    // racial stealth — Stock and ParaMUD differ inside the calculator).
    private void RecomputeBackstab()
    {
        BackstabHasWeapon = _bsWeaponMax > 0;

        BackstabLevelText = _level.ToString(CultureInfo.InvariantCulture);
        BackstabStrengthText = _str.ToString(CultureInfo.InvariantCulture);
        BackstabStealthText = _stealth.ToString(CultureInfo.InvariantCulture);
        BackstabClassStealthText = _hasClassStealth ? "Class (scales with level)" : "Racial only (×75%)";
        BackstabBonusText = string.Create(CultureInfo.InvariantCulture,
            $"+{_plusBsMin} min / +{_plusBsMax} max / +{_plusMaxDamage} dmg");
        BackstabRealmText = _realm == RealmType.Stock ? "Stock" : "ParaMUD / GreaterMUD";

        if (!BackstabHasWeapon)
        {
            BackstabWeaponRangeText = "—";
            BackstabMinText = BackstabMaxText = BackstabAvgText = "—";
            return;
        }

        BackstabWeaponRangeText = string.Create(CultureInfo.InvariantCulture, $"{_bsWeaponMin}–{_bsWeaponMax}");
        BSDamageResult res = CombatCalculator.CalcBSDamage(
            _level, _stealth, _str, _bsWeaponMin, _bsWeaponMax,
            _plusBsMin, _plusBsMax, _plusMaxDamage, _hasClassStealth, _realm);
        BackstabMinText = res.MinDamage.ToString(CultureInfo.InvariantCulture);
        BackstabMaxText = res.MaxDamage.ToString(CultureInfo.InvariantCulture);
        BackstabAvgText = res.AvgDamage.ToString("0.0", CultureInfo.InvariantCulture);
    }

    // Reset the backstab weapon back to the Equipment Manager's Backstab-set weapon.
    [RelayCommand]
    private void ResetBackstab() => SelectedBackstabWeaponName = _backstabDefaultWeaponLabel;

    // The Backstab-set weapon mapped to a "<name> (#<number>)" picker label, or
    // null when the set has no weapon (or the item isn't a known weapon). Building
    // the label from the gamedata row's own Name + Number keeps it byte-identical
    // to the entries EnsureWeaponNames produced, so the picker matches on it.
    private string? ResolveBackstabSetWeaponLabel()
    {
        string? itemName = BackstabSetWeaponName();
        if (itemName is null) return null;
        if (_gameData.FindRowByName("Items", itemName) is not JsonElement el) return null;
        if (!el.TryGetProperty("Name", out JsonElement nameEl) || nameEl.ValueKind != JsonValueKind.String)
            return null;
        string realName = nameEl.GetString() ?? itemName;
        int number = GetInt(el, "Number");
        string label = string.Create(CultureInfo.InvariantCulture, $"{realName} (#{number})");
        return _weaponNumberByLabel.ContainsKey(label) ? label : null;
    }

    // The trimmed weapon name on the profile's Backstab equipment set, or null
    // when there's no profile / set / weapon slot filled.
    private string? BackstabSetWeaponName()
    {
        EquipmentSettings? eq = _profile.Current?.Equipment;
        EquipmentSet? set = eq?.Sets.FirstOrDefault(s => s.Trigger == EquipTriggerType.Backstab);
        string? name = set?.Slots.FirstOrDefault(e => e.Slot == EquipmentSlot.Weapon)?.ItemName?.Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static int GetInt(JsonElement? row, string property)
    {
        if (row is not JsonElement el || el.ValueKind != JsonValueKind.Object) return 0;
        if (!el.TryGetProperty(property, out JsonElement v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
    }

    // Scan a monster's ability slots (Abil-0..9) for the given code and return
    // its paired AbilVal, or 0 when absent — how monsters carry Dodge (34) and
    // other stat-style perks that aren't top-level columns.
    private static int GetAbilityValue(JsonElement row, int code)
    {
        for (int i = 0; i < 10; i++)
        {
            if (GetInt(row, $"Abil-{i}") == code)
                return GetInt(row, $"AbilVal-{i}");
        }
        return 0;
    }

    private void OnStatsChanged(object? sender, PropertyChangedEventArgs e) => RefreshActuals();
    private void OnInventoryChanged() => RefreshActuals();
    private void OnQuestBonusesChanged() => RefreshActuals();

    // A profile swap is a seed moment: adopt the new character's stats / gear /
    // Backstab set into every editable input.
    private void OnProfileLoaded(CharacterProfile _) => SeedAll();

    // ----- Top-100 XP/HR leaderboard -------------------------------------
    // The latest capture's ranked rows with a derived XP/HR column. Fed by the
    // per-BBS LeaderboardSnapshotStore — every character on the board shares it —
    // and rebuilt whenever a fresh `top N` block is captured off the terminal.
    public ObservableCollection<LeaderboardRow> Leaderboard { get; } = new();

    // The grid binds this, not the raw collection: a DataGridCollectionView is what
    // makes the columns user-sortable (an ObservableCollection alone is inert to
    // the grid's sort). Each column's SortMemberPath points at a typed sort key on
    // LeaderboardRow so numbers order by magnitude, not formatted text.
    public DataGridCollectionView LeaderboardView { get; }

    // Raised after the rows are rebuilt so the view can size the grid's columns to
    // the whole capture. The DataGrid's Auto columns otherwise measure only the
    // realized rows and creep wider as more scroll into view.
    public event Action? LeaderboardRebuilt;

    // One-line capture context (when, how many shown vs asked, whole-list vs
    // truncated, snapshot count) or the empty-state hint.
    [ObservableProperty] private string _leaderboardStatus = string.Empty;

    // Reroll / dropout suspects since the previous capture (empty when none).
    [ObservableProperty] private string _leaderboardRerolls = string.Empty;

    // True once at least one capture exists — gates the table vs the empty state.
    [ObservableProperty] private bool _hasLeaderboard;

    // True when there are reroll/dropout notices to show.
    [ObservableProperty] private bool _hasLeaderboardNotices;

    // Class-filter choices for the leaderboard grid: a leading "No Filter"
    // sentinel followed by every class name from the active set. Populated once.
    public ObservableCollection<string> ClassFilterOptions { get; } = new();

    private const string NoClassFilter = "No Filter";

    // The chosen class filter. "No Filter" (the default) clears the filter; any
    // real class narrows the grid to rows of that class via the view's filter.
    [ObservableProperty] private string _selectedClassFilter = NoClassFilter;

    partial void OnSelectedClassFilterChanged(string value)
    {
        LeaderboardView.Filter = string.Equals(value, NoClassFilter, StringComparison.Ordinal)
            ? null
            : o => o is LeaderboardRow row
                   && string.Equals(row.Class, value, StringComparison.OrdinalIgnoreCase);
    }

    // Populate the class filter once from the active set, with the "No Filter"
    // sentinel first. Cheap to retry if the set wasn't loaded at construction.
    private void EnsureClassFilterOptions()
    {
        if (ClassFilterOptions.Count > 0) return;
        ClassFilterOptions.Add(NoClassFilter);
        JsonDocument? doc = _gameData.GetRawTable("Classes");
        if (doc is null) return;

        foreach (JsonElement row in doc.RootElement.EnumerateArray())
        {
            if (!row.TryGetProperty("Name", out JsonElement nameEl)) continue;
            if (nameEl.ValueKind != JsonValueKind.String) continue;
            string? name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) continue;
            ClassFilterOptions.Add(name);
        }
    }

    // Request a fresh listing at the DEEPEST depth we've ever captured (a 300-deep
    // list re-requests top 300), defaulting to 100 before the first capture. Reading
    // the depth from the widest stored capture — not just the newest — means a recent
    // small "top 10" view doesn't shrink the refresh back down. Asking at least 100
    // also keeps the request over-running a small board so its short reply reveals
    // the true cap (IsComplete). The reply is captured automatically off the
    // terminal, so the table refreshes on its own.
    [RelayCommand]
    private void ParseToplist()
    {
        int n = 100;
        foreach (LeaderboardSnapshot snap in _leaderboards.Snapshots)
        {
            if (snap.Entries.Count == 0) continue;
            n = Math.Max(n, Math.Max(snap.Entries.Count, snap.Entries[^1].Rank));
        }
        AppServices.Current.SendGameCommand($"top {n}");
    }

    // Wipe this BBS's capture history.
    [RelayCommand]
    private void ClearLeaderboard() => _leaderboards.Clear();

    private void OnLeaderboardChanged() => RebuildLeaderboard();

    private void RebuildLeaderboard()
    {
        LeaderboardReport report = LeaderboardXpRateCalculator.Build(_leaderboards.Snapshots);

        Leaderboard.Clear();
        foreach (LeaderboardRankRow r in report.Rows)
            Leaderboard.Add(LeaderboardRow.From(r));

        HasLeaderboard = report.Rows.Count > 0;

        if (report.CapturedAtUtc is { } at)
        {
            string asked = report.RequestedCount > 0 ? $" (asked top {report.RequestedCount})" : string.Empty;
            string scope = report.IsComplete ? "whole list visible" : "list may extend below";
            LeaderboardStatus =
                $"Last capture {at.ToLocalTime():yyyy-MM-dd HH:mm} · {report.ShownCount} shown{asked} · "
                + $"{scope} · {report.SnapshotCount} capture(s) stored";
        }
        else
        {
            LeaderboardStatus =
                "No captures yet. Press Parse Toplist (or run `top <N>` in-game) to capture the leaderboard.";
        }

        LeaderboardRerolls = report.Notices.Count > 0
            ? "Reroll suspects since last capture: " + string.Join(", ", report.Notices)
            : string.Empty;
        HasLeaderboardNotices = report.Notices.Count > 0;

        LeaderboardRebuilt?.Invoke();
    }

    public override void Dispose()
    {
        _stats.PropertyChanged -= OnStatsChanged;
        _inventory.Changed -= OnInventoryChanged;
        _questBonuses.Changed -= OnQuestBonusesChanged;
        _profile.ProfileLoaded -= OnProfileLoaded;
        _leaderboards.Changed -= OnLeaderboardChanged;
    }
}

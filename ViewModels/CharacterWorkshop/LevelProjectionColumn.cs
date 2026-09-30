using System;
using System.Collections.Generic;
using System.Linq;

namespace MudPlay.ViewModels.CharacterWorkshop;

// One toggleable column in the Level Projection grid: the key persisted in the
// character profile, the header, the LevelProjectionRow property its cells bind
// to, a pixel width, and whether it shows for a character that has never opened
// the picker. Muted renders the column in the secondary foreground — the grid
// alternates emphasis so the headline numbers (level, XP, HP, mana, accuracy,
// damage) stay readable against the supporting ones.
//
// The catalogue order IS the grid order; the picker only toggles visibility.
// Tip is the header's hover text: how the figure is worked out and what goes into it.
// ParadigmTip replaces it while a Paradigm set is loaded, for a column whose formula
// differs there (or that's only verified on Stock).
public readonly record struct LevelProjectionColumn(
    string Key, string Header, string Binding, int Width, bool DefaultVisible, bool Muted,
    string? Tip = null, string? ParadigmTip = null)
{
    public string? TipFor(Game.RealmType realm) =>
        realm == Game.RealmType.ParaMud && ParadigmTip is not null ? ParadigmTip : Tip;

    // Level is the row's identity — hiding it would leave rows unlabelled, so the
    // picker renders its checkbox disabled the way the Game Data browser pins Name.
    public const string PinnedKey = "level";

    // Every column the grid can show, in display order. Keys are persisted, so
    // renaming one silently resets that column for existing characters — add a new
    // key instead. The utility skills default off: they only matter to classes that
    // were granted them, and 21 columns at once is unreadable.
    public static IReadOnlyList<LevelProjectionColumn> All { get; } = new[]
    {
        new LevelProjectionColumn(PinnedKey, "Lvl", "Level", 60, true, false,
            "The level. Your current level is highlighted; each row assumes today's stats plus your CP plan's increases up to that level."),
        new LevelProjectionColumn("exptolevel", "Exp to level", "ExpToLevel", 150, true, false,
            "Experience still needed to reach this level: its Total XP minus the experience you have now. 0 for a level you've already reached."),
        new LevelProjectionColumn("totalxp", "Total XP", "TotalXp", 150, true, true,
            "Total experience that reaches this level, from the Stock experience table scaled by your class's and race's exp chart (%).",
            "Total experience that reaches this level, from the Paradigm experience table scaled by your class's and race's exp chart (%). Paradigm's table differs from Stock's."),
        new LevelProjectionColumn("cost", "Train (copper)", "Cost", 130, true, true,
            "Copper to train to this level at the cheapest trainer that serves your class and level: level × 5 × (100 + trainer markup %) ÷ 100, × 10. Raw copper so it pastes straight into the game."),
        new LevelProjectionColumn("hp", "HP", "HpRange", 120, true, false,
            "Max HP: HEA ÷ 2 + level × your class's minimum hits per level + (HEA − 50) × level ÷ 16, plus the random per-level roll (the range runs from a minimum roll to a maximum roll every level), + your race's HP per level × level + gear and quest +max HP."),
        new LevelProjectionColumn("hpregen", "HP/tick (idle/rest)", "HpRegen", 130, true, true,
            "HP per regen tick, idle / resting: (level + 20) × HEA ÷ 750, at least 1; ×3 while resting; then + gear and quest HP-regen %.",
            "HP per regen tick, idle / resting: (level + 20) × HEA ÷ 500, at least 1; ×3 while resting; then + gear and quest HP-regen %. (Stock divides by 750.)"),
        new LevelProjectionColumn("mana", "Mana / Kai", "Mana", 100, true, false,
            "Max mana: magery level × level × 2 + 6 + gear and quest +max mana. Mystics show Kai instead, which is level − 1. — for a non-caster."),
        new LevelProjectionColumn("mpregen", "MP / tick", "MpRegen", 90, true, true,
            "Mana per regen tick (not meditating): (level + 20) × your casting stat × (magery level + 2) ÷ 1650, then + gear mana-regen %. The casting stat is INT for mages, WIL for priests, (INT + WIL) ÷ 2 for druids, CHM for bards; Kai is a flat 1."),
        new LevelProjectionColumn("spellcasting", "Spellcast", "Spellcasting", 90, false, true,
            "Spellcasting: level × 2 + your casting-stat term + magery level × 5 + gear +spellcasting. The stat term is (3 INT + WIL) ÷ 6 for mages, (3 WIL + INT) ÷ 6 for priests, (INT + WIL) ÷ 3 for druids, (3 CHM + WIL) ÷ 6 for bards. — for a non-caster."),
        new LevelProjectionColumn("accuracy", "Accy", "Accuracy", 70, true, false,
            "Your stats' share of normal-attack accuracy: (STR − 50) ÷ 3 + (AGL − 50) ÷ 6. Your weapon's own accuracy isn't included, since it can't be projected.",
            "Your stats' share of normal-attack accuracy: (AGL − 50) ÷ 3 + (INT − 50) ÷ 6 + (CHM − 50) ÷ 10. On Paradigm, STR doesn't help normal attacks (it does on Stock). Your weapon's own accuracy isn't included."),
        new LevelProjectionColumn("bsaccuracy", "BS Accy", "BsAccuracy", 80, false, false,
            "Backstab accuracy: (Stealth + AGL) ÷ 2 + gear +backstab accuracy ÷ 2, +5 if your class sneaks or −15 if only your race does, + your best single +accuracy item. Uses today's weapon. — without a stealth class or race.",
            "Backstab accuracy: Stealth ÷ 3 + (AGL − 50 + level) ÷ 2 + 15 + gear +backstab accuracy + your weapon's and gear's +accuracy, −15 if your STR is under the weapon's requirement. Uses today's weapon. — without a stealth class or race."),
        new LevelProjectionColumn("crit", "Crit", "Crit", 70, true, true,
            "Crit rating: level ÷ 10 + (INT − 50) ÷ 10 + (AGL − 50) ÷ 20 + (CHM − 50) ÷ 30, kept between 1 and 75, + gear +crits. In a fight, crit above 40 counts one point in three.",
            "Crit rating: level ÷ 10 + (INT − 50) ÷ 10 + (AGL − 50) ÷ 20 + (CHM − 50) ÷ 30, at least 1 (Stock also caps it at 75), + gear +crits. In a fight, crit is capped at 65."),
        new LevelProjectionColumn("dodge", "Dodge", "Dodge", 70, true, true,
            "Raw dodge: level ÷ 5 + (CHM − 50) ÷ 5 + (AGL − 50) ÷ 3 + gear +dodge. Your chance to dodge a given attack also depends on the attacker's accuracy."),
        new LevelProjectionColumn("stealth", "Stealth (sneak %)", "Stealth", 125, true, true,
            "Stealth: AGL ÷ 4 + INT ÷ 8 + CHM ÷ 6 (each rounded down) + level × 2 (level + 15 from level 16) + 20 + gear +stealth. In brackets, the chance a sneak (sn) takes in an empty room with a light load, up to 95%. Each monster and each other player in the room takes 1% off; carrying over a third of your weight limit takes 5% off, over two thirds 10%.",
            "Stealth: AGL ÷ 4 + INT ÷ 8 + CHM ÷ 6 (added together, then rounded once) + level × 2 (level + 15 from level 16) + 20 + gear +stealth. In brackets, the chance a sneak (sn) takes in an empty room with a light load, up to 95%. Each monster and each other player in the room takes 1% off; carrying over a third of your weight limit takes 5% off, over two thirds 10%."),
        new LevelProjectionColumn("meleedmg", "Melee dmg", "MeleeDmg", 100, true, false,
            "What STR adds to your weapon's damage: +2 × ((STR − 100) ÷ 10) to the minimum, never below 0, and +(STR − 50) ÷ 10 to the maximum, which goes negative below 50 STR. + gear +max damage.",
            "What STR adds to your weapon's damage: +(STR − 100) ÷ 10 to the minimum and +(STR − 50) ÷ 10 to the maximum, never below 0, + gear +damage. (Stock doubles the minimum term.)"),
        new LevelProjectionColumn("maxenc", "Max enc", "MaxEnc", 90, true, true,
            "Weight limit: STR × 48, plus STR × 36 − 3600 above 100 STR, + gear +carry."),
        new LevelProjectionColumn("magicres", "Magic res", "MagicRes", 90, true, true,
            "Magic resistance: (INT + 3 × WIL) ÷ 4 + gear +magic resist."),
        new LevelProjectionColumn("perception", "Percep", "Perception", 80, false, true,
            "Perception: (5 × INT + 2 × WIL + CHM) ÷ 8 + gear +perception. Every class has it. It's your chance to find a hidden exit (Perception − 15) or a hidden player (Perception) when you search, and to notice your own failed sneak or hide.",
            "Perception: (5 × INT + 2 × WIL + CHM) ÷ 8 + gear +perception. Every class has it; used when searching and to notice a failed sneak or hide. This is the Stock formula; it isn't confirmed for Paradigm."),
        new LevelProjectionColumn("thievery", "Thievery", "Thievery", 80, false, true,
            "Thievery: (AGL + INT + CHM + lvl × 24) ÷ 6 + gear +thievery, where lvl is your level up to 15 and grows half as fast after. Your chance to rob someone is about Thievery %.",
            "Thievery: (AGL + INT + CHM + lvl × 24) ÷ 6 + gear +thievery, where lvl is your level up to 15 and grows half as fast after. This is the Stock formula; it isn't confirmed for Paradigm."),
        new LevelProjectionColumn("traps", "Traps", "Traps", 70, false, true,
            "Traps (finding): (INT + AGL + 2 × CHM + lvl × 28) ÷ 7 + gear +traps, lvl as for Thievery. It's your chance to find a trap by searching. Disarming uses the same base without +Traps gear: about that % to disarm, 10% a safe miss, the rest springs the trap.",
            "Traps (finding): (INT + AGL + 2 × CHM + lvl × 28) ÷ 7 + gear +traps, lvl as for Thievery. Disarming uses the same base without +Traps gear. This is the Stock formula; it isn't confirmed for Paradigm."),
        new LevelProjectionColumn("picklocks", "Picklocks", "Picklocks", 85, false, true,
            "Picklocks: (AGL + INT + lvl × 10) × 2 ÷ 7 + gear +picklocks, lvl as for Thievery. Picking a lock succeeds about (Picklocks − the door's requirement + 1)% of the time: a [101 picklocks] door needs over 100, and at 150 opens about half the time. 0 Picklocks always fails.",
            "Picklocks: (INT + AGL + 2 × CHM + lvl × 28) ÷ 7 + gear +picklocks, lvl as for Thievery. On Stock, picking succeeds about (Picklocks − the door's requirement + 1)% of the time; that rule isn't confirmed for Paradigm."),
        new LevelProjectionColumn("tracking", "Tracking", "Tracking", 80, false, true,
            "Tracking: (2 × INT + WIL + CHM + lvl × 40) ÷ 8 + gear +tracking, lvl as for Thievery. Your chance, per exit, that track shows which way someone went.",
            "Tracking: (2 × INT + WIL + CHM + lvl × 40) ÷ 8 + gear +tracking, lvl as for Thievery. This is the Stock formula; it isn't confirmed for Paradigm."),
    };

    // The columns to render for a saved HIDDEN set. Storing the exceptions rather
    // than the selection is what lets a column added in a later release show up on
    // an existing character instead of staying invisible because its key predates
    // the save. A null save (picker never touched) falls back to DefaultVisible.
    // The pinned column survives a save that somehow lists it.
    public static IReadOnlyList<LevelProjectionColumn> Resolve(IReadOnlyList<string>? hidden)
    {
        if (hidden is null) return All.Where(c => c.DefaultVisible).ToList();

        HashSet<string> off = new(hidden, StringComparer.OrdinalIgnoreCase);
        return All.Where(c => c.Key == PinnedKey || !off.Contains(c.Key)).ToList();
    }
}

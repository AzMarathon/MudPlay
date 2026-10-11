using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MudPlay.Game.Spells;

namespace MudPlay.Game.Map;

// Reads whether a room's own spell damages whoever stands in the room, how much and
// on what (GAME_MECHANICS "Resting in a room whose spell does damage").
//
// A room spell reaches damage three ways: its own Damage ability, an EndCast to a
// spell that has one, or a `cast` of such a spell in the textblock its TextBlock
// ability runs. What a path passes on the way decides how it counts:
//   - a counter (`failitem`, `checkspell`, `failspell`) is no gate here. The damage
//     is the room's default and the item or buff is the way out of it, which
//     RoomHazardIndex already knows how to check for this character.
//   - a roll (a `random` table that doesn't damage whatever is rolled, a
//     `testskill`, an EndCast% under 100) makes the path OnARoll.
//   - an EndCast from a spell that has a duration fires when that spell ends, so
//     the damage behind it is AfterATimer: not what a cast does, and kept apart
//     from it (freezing water's 1 to 4 a tick, and the drowning 25 rounds on).
//   - any other condition on the character or the room (class, level, alignment,
//     monsters present, a flag) makes it Conditional, roll or no roll: the client
//     doesn't work those out, and a room that only burns the evil-aligned is no
//     reason to keep a good character from resting in it.
// The spell takes the strongest of its paths, and the reading describes those.
//
// The same chain RoomSpellTeleportClassifier walks, asked a different question (which
// gates count, and what a roll does to the answer), so it walks the chain itself and
// shares that classifier's list of condition steps and RoomSummonParser's table
// reader.
public static class RoomSpellDamageClassifier
{
    // Damage, and damage that magic resistance lessens (chaos storm, the room spell
    // of Paradigm's Barley Fields).
    private const int DamageAbility = 1;
    private const int DamageMrAbility = 17;
    private const int EndCastAbility = 151;

    // A d100 table runs the first line whose number is above the roll, so only a
    // table whose lines reach 100 runs a line every time.
    private const int RollCeiling = 100;

    // Follow-on spells the data does not tie to their room spell, on the user's
    // word. Paradigm's two desert spells run `failspell 711 <block>` and the set
    // has neither block, so nothing in it says what is cast on a character without
    // the waterskin buff; the user confirmed it is desert damage #712, 5 to 20 a
    // cast (2026-10-10, asked exactly that: "yes"). Read only where the block is
    // missing: a set that has the block (Stock) is read from its own data. An
    // entry goes in here on a confirmation and never on a guess.
    private static readonly Dictionary<int, int> ConfirmedFollowOns = new()
    {
        [683] = 712,   // desert spell
        [684] = 712,   // desert spell 2
    };

    // spellOf resolves a spell number to its record, textblock a TBInfo number to its
    // entry; either returns null for a number the set doesn't have.
    public static RoomSpellDamageReading Classify(
        int spell, Func<int, SpellFormulaInput?> spellOf, Func<int, TBInfoEntry?> textblock)
    {
        ArgumentNullException.ThrowIfNull(spellOf);
        ArgumentNullException.ThrowIfNull(textblock);
        var walk = new Walk(spell, spellOf, textblock);
        List<Hit> hits = walk.Spell(spell, [], 0);
        if (hits.Count == 0) return RoomSpellDamageReading.NoDamage(walk.Gap);

        RoomSpellDamage kind = hits.Max(static h => h.Kind);
        List<Hit> strongest = hits.Where(h => h.Kind == kind).ToList();
        // The range is what lands first. An every-tick spell's is what its casts
        // do; damage a timer brings later is told apart (Timed), or freezing water
        // would read "1 to 9999" for the drowning its held breath ends in.
        int soonest = strongest.Min(static h => h.AfterRounds);
        List<Hit> sized = strongest.Where(h => h.AfterRounds == soonest && h.Max > 0).ToList();
        bool skillTest = strongest.Any(static h => h.SkillTest);
        // Bands of one table are apart from each other, so their shares add up.
        int percent = kind is RoomSpellDamage.EveryTick or RoomSpellDamage.AfterATimer || skillTest
            ? 0
            : Math.Clamp((int)Math.Round(strongest.Sum(static h => h.Chance) * 100), 1, 100);
        return new RoomSpellDamageReading(
            kind,
            sized.Count == 0 ? 0 : sized.Min(static h => h.Min),
            sized.Count == 0 ? 0 : sized.Max(static h => h.Max),
            sized.Any(static h => h.Grows),
            percent,
            skillTest,
            strongest.Select(static h => string.Join(", ", h.Conditions))
                .Where(static c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            walk.Gap)
        {
            FollowOnSpell = sized.Select(static h => h.FollowOn).FirstOrDefault(static f => f > 0),
            FollowOnWithoutBuff = sized.Select(static h => h.WithoutBuff).FirstOrDefault(static b => b > 0),
            Timed = hits.Where(static h => h.Kind == RoomSpellDamage.AfterATimer)
                .OrderBy(static h => h.AfterRounds)
                .Select(static h => new RoomSpellDamageStage(h.Spell, h.Min, h.Max, h.AfterRounds))
                .Distinct()
                .ToList(),
        };
    }

    // One damage spell a path ends in. Chance is the share of casts that reach it
    // past the table bands and EndCast% on the way (1 when there is none);
    // AfterRounds the durations of the spells whose ending leads to it (0 for
    // damage the cast itself does). FollowOn and WithoutBuff are set for damage read
    // through ConfirmedFollowOns: the spell, and the buff whose absence casts it.
    private readonly record struct Hit(
        RoomSpellDamage Kind, int Spell, int Min, int Max, bool Grows, double Chance, bool SkillTest,
        string[] Conditions, int AfterRounds = 0, int FollowOn = 0, int WithoutBuff = 0);

    private sealed class Walk(int roomSpell, Func<int, SpellFormulaInput?> spellOf, Func<int, TBInfoEntry?> textblock)
    {
        // What is being read right now: a chain that comes back to one of these has
        // looped, and the second visit adds nothing the first won't find.
        private readonly HashSet<int> _spells = new();
        private readonly HashSet<(bool Table, int Block)> _blocks = new();

        public string? Gap { get; private set; }

        public List<Hit> Spell(int number, string[] conditions, int depth)
        {
            if (number <= 0) return [];
            if (depth > RoomSpellTeleportClassifier.MaxChainDepth)
                return Missed($"chain cut at spell {number}, {RoomSpellTeleportClassifier.MaxChainDepth} steps in");
            if (spellOf(number) is not { } spell) return Missed($"spell {number} missing");
            if (!_spells.Add(number)) return [];

            // An EndCast% under 100 makes the follow-on spell a roll.
            int endCastPercent = SpellEffectFormatter.EndCastPercent(spell);
            bool endCastRolls = endCastPercent is > 0 and < 100;
            // A textblock spell with no value in the slot keeps the block's number
            // in its base values, which are then no damage range.
            bool baseIsBlock = spell.Abilities.Any(
                static a => a.Code == SpellTextBlock.AbilityCode && a.Value <= 0);

            var hits = new List<Hit>();
            foreach (SpellAbility ability in spell.Abilities)
            {
                if (ability.Code is DamageAbility or DamageMrAbility)
                {
                    // A value in the slot is the amount; an empty slot takes the
                    // roll between the record's base values.
                    (int min, int max) = ability.Value != 0 ? (ability.Value, ability.Value)
                        : baseIsBlock ? (0, 0)
                        : (Math.Min(spell.MinBase, spell.MaxBase), Math.Max(spell.MinBase, spell.MaxBase));
                    // The room's own spell is rolled between its two base values
                    // and no level enters (GAME_MECHANICS "Resting in a room whose
                    // spell does damage": chaos storm and the drowning spells do
                    // their set damage). A spell a textblock casts is rolled at
                    // the character's own level, so its per-level step counts.
                    bool roomsOwnCast = depth == 0;
                    bool grows = !roomsOwnCast && ability.Value == 0 && !baseIsBlock
                        && ((spell.MinInc != 0 && spell.MinIncLVLs != 0) || (spell.MaxInc != 0 && spell.MaxIncLVLs != 0));
                    hits.Add(new Hit(
                        conditions.Length > 0 ? RoomSpellDamage.Conditional : RoomSpellDamage.EveryTick,
                        number, min, max, grows, 1, false, conditions));
                }
                else if (ability.Code == SpellTextBlock.AbilityCode)
                    hits.AddRange(Lines(
                        SpellTextBlock.Number(ability.Value, spell.MinBase, spell.MaxBase), conditions, depth + 1));
                else if (ability.Code == EndCastAbility && ability.Value > 0)
                {
                    List<Hit> follow = Spell(ability.Value, conditions, depth + 1);
                    // The follow-on is cast when this spell ends: for one with a
                    // duration, that many rounds on (holding breath's 25).
                    if (spell.Dur > 0) follow = Later(follow, spell.Dur);
                    hits.AddRange(endCastRolls ? Past(follow, endCastPercent / 100.0, skillTest: false) : follow);
                }
            }
            _spells.Remove(number);
            return hits;
        }

        // A block run line by line: the game takes the first line whose steps all
        // pass, and which that is depends on the character, so each line is a path
        // of its own from where the block was entered.
        private List<Hit> Lines(int block, string[] conditions, int depth)
        {
            if (!Enter(table: false, block, depth, out string action)) return [];
            var hits = new List<Hit>();
            foreach (string line in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                hits.AddRange(Steps(line.Split(':', StringSplitOptions.TrimEntries), conditions, depth));
            _blocks.Remove((false, block));
            return hits;
        }

        // A block rolled as a d100 table: one line runs, picked by the roll. A table
        // that damages whatever is rolled is no roll (the ice slide always drops you;
        // the roll only picks where).
        private List<Hit> Table(int block, string[] conditions, int depth)
        {
            if (!Enter(table: true, block, depth, out string action)) return [];
            int covered = 0;
            bool everyBand = true;
            var bands = new List<(List<Hit> Hits, int Width)>();
            foreach ((int threshold, string[] steps) in RoomSummonParser.ReadBands(action))
            {
                // A line at or under an earlier line's number is never the first one
                // above the roll.
                if (threshold <= covered || covered >= RollCeiling) continue;
                int width = Math.Min(threshold, RollCeiling) - covered;
                covered = threshold;
                List<Hit> band = Steps(steps, conditions, depth);
                everyBand &= band.Any(static h => h.Kind == RoomSpellDamage.EveryTick);
                bands.Add((band, width));
            }
            _blocks.Remove((true, block));

            bool noRoll = everyBand && covered >= RollCeiling;
            var hits = new List<Hit>();
            foreach ((List<Hit> band, int width) in bands)
                hits.AddRange(noRoll ? band : Past(band, width / (double)RollCeiling, skillTest: false));
            return hits;
        }

        // One line's steps, left to right. Only what comes before a cast gates it.
        private List<Hit> Steps(IEnumerable<string> steps, string[] conditions, int depth)
        {
            var hits = new List<Hit>();
            foreach (string step in steps)
            {
                string[] words = step.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                string verb = words[0];

                if (Is(verb, "cast"))
                    hits.AddRange(Spell(Number(words, 1), conditions, depth + 1));
                else if (Is(verb, "random"))
                    hits.AddRange(Table(Number(words, 1), conditions, depth + 1));
                else if (Is(verb, "checkspell") || Is(verb, "failspell"))
                {
                    // The second number is the block run when the buff is missing.
                    int absent = Number(words, 2);
                    // A block the set lacks, behind a room spell whose follow-on
                    // the user has named: read that spell, and say whose word it is.
                    if (Is(verb, "failspell") && absent > 0 && textblock(absent) is null
                        && ConfirmedFollowOns.TryGetValue(roomSpell, out int followOn) && spellOf(followOn) is not null)
                    {
                        int buff = Number(words, 1);
                        foreach (Hit hit in Spell(followOn, conditions, depth + 1))
                            hits.Add(hit with { FollowOn = followOn, WithoutBuff = buff });
                        continue;
                    }
                    hits.AddRange(Lines(absent, conditions, depth + 1));
                    // `failspell`: without the buff "the damage fires" (GAME_MECHANICS
                    // "Room-spell hazard shape 3 — buff check (`checkspell` /
                    // `failspell`): the desert waterskin"). Paradigm's desert names a
                    // block its data doesn't have and does its thirst damage all the
                    // same, so a `failspell` whose block is missing is damage of a
                    // size the data doesn't give, not no damage.
                    if (Is(verb, "failspell") && absent > 0 && textblock(absent) is null)
                        hits.Add(new Hit(
                            conditions.Length > 0 ? RoomSpellDamage.Conditional : RoomSpellDamage.EveryTick,
                            0, 0, 0, false, 1, false, conditions));
                }
                else if (Is(verb, "testskill"))
                    // `testskill <skill> [<modifier>] <failTextblock>`: the block runs on a miss.
                    hits.AddRange(Past(
                        Lines(Number(words, words.Length - 1), conditions, depth + 1), 1, skillTest: true));
                else if (Is(verb, "failitem"))
                    continue;
                else if (RoomSpellTeleportClassifier.IsConditionStep(verb))
                    conditions = [.. conditions, string.Join(' ', words)];
            }
            return hits;
        }

        private bool Enter(bool table, int block, int depth, out string action)
        {
            action = string.Empty;
            if (block <= 0) return false;
            if (depth > RoomSpellTeleportClassifier.MaxChainDepth)
            {
                Missed($"chain cut at textblock {block}, {RoomSpellTeleportClassifier.MaxChainDepth} steps in");
                return false;
            }
            if (textblock(block) is not { } entry)
            {
                Missed($"textblock {block} missing");
                return false;
            }
            if (string.IsNullOrWhiteSpace(entry.Action)) return false;
            if (!_blocks.Add((table, block))) return false;
            action = entry.Action;
            return true;
        }

        private List<Hit> Missed(string what)
        {
            Gap ??= what;
            return [];
        }

        // Damage reached past a roll is no longer damage on every tick, or sure to
        // come when a timer is out.
        private static List<Hit> Past(List<Hit> hits, double chance, bool skillTest)
        {
            for (int i = 0; i < hits.Count; i++)
                hits[i] = hits[i] with
                {
                    Kind = hits[i].Kind is RoomSpellDamage.EveryTick or RoomSpellDamage.AfterATimer
                        ? RoomSpellDamage.OnARoll : hits[i].Kind,
                    Chance = hits[i].Chance * chance,
                    SkillTest = hits[i].SkillTest || skillTest,
                };
            return hits;
        }

        // Damage that waits for a spell to end is not damage the cast does.
        private static List<Hit> Later(List<Hit> hits, int rounds)
        {
            for (int i = 0; i < hits.Count; i++)
                hits[i] = hits[i] with
                {
                    Kind = hits[i].Kind == RoomSpellDamage.EveryTick ? RoomSpellDamage.AfterATimer : hits[i].Kind,
                    AfterRounds = hits[i].AfterRounds + rounds,
                };
            return hits;
        }

        private static bool Is(string verb, string name) => verb.Equals(name, StringComparison.OrdinalIgnoreCase);

        private static int Number(string[] words, int index) =>
            index > 0 && index < words.Length
            && int.TryParse(words[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }
}

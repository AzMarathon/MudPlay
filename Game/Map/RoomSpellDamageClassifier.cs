using System.Collections.Generic;
using System.Globalization;
using MudPlay.Game.Spells;

namespace MudPlay.Game.Map;

// Reads whether a room's own spell damages whoever stands in the room (GAME_MECHANICS
// "Resting in a room whose spell does damage").
//
// A room spell reaches damage three ways: its own Damage ability, an EndCast to a
// spell that has one, or a `cast` of such a spell in the textblock its TextBlock
// ability runs. What a path passes on the way decides how it counts:
//   - a counter (`failitem`, `checkspell`, `failspell`) is no gate here. The damage
//     is the room's default and the item or buff is the way out of it, which
//     RoomHazardIndex already knows how to check for this character.
//   - a roll (a `random` table that doesn't damage whatever is rolled, a
//     `testskill`, an EndCast% under 100) makes the path OnARoll.
//   - any other condition on the character or the room (class, level, alignment,
//     monsters present, a flag) makes it Conditional, roll or no roll: the client
//     doesn't work those out, and a room that only burns the evil-aligned is no
//     reason to keep a good character from resting in it.
// The spell takes the strongest of its paths.
//
// The same chain RoomSpellTeleportClassifier walks, asked a different question (which
// gates count, and what a roll does to the answer), so it walks the chain itself and
// shares that classifier's list of condition steps and RoomSummonParser's table
// reader.
public static class RoomSpellDamageClassifier
{
    private const int DamageAbility = 1;
    private const int TextBlockAbility = 148;
    private const int EndCastAbility = 151;

    // A d100 table runs the first line whose number is above the roll, so only a
    // table whose lines reach 100 runs a line every time.
    private const int RollCeiling = 100;

    // spellOf resolves a spell number to its record, textblock a TBInfo number to its
    // entry; either returns null for a number the set doesn't have. gap names the
    // first thing the walk couldn't read, or is null when it read everything.
    public static RoomSpellDamage Classify(
        int spell, Func<int, SpellFormulaInput?> spellOf, Func<int, TBInfoEntry?> textblock, out string? gap)
    {
        ArgumentNullException.ThrowIfNull(spellOf);
        ArgumentNullException.ThrowIfNull(textblock);
        var walk = new Walk(spellOf, textblock);
        RoomSpellDamage found = walk.Spell(spell, conditioned: false, 0);
        gap = walk.Gap;
        return found;
    }

    private sealed class Walk(Func<int, SpellFormulaInput?> spellOf, Func<int, TBInfoEntry?> textblock)
    {
        // What is being read right now: a chain that comes back to one of these has
        // looped, and the second visit adds nothing the first won't find.
        private readonly HashSet<int> _spells = new();
        private readonly HashSet<(bool Table, int Block)> _blocks = new();

        public string? Gap { get; private set; }

        public RoomSpellDamage Spell(int number, bool conditioned, int depth)
        {
            if (number <= 0) return RoomSpellDamage.None;
            if (depth > RoomSpellTeleportClassifier.MaxChainDepth)
                return Missed($"chain cut at spell {number}, {RoomSpellTeleportClassifier.MaxChainDepth} steps in");
            if (spellOf(number) is not { } spell) return Missed($"spell {number} missing");
            if (!_spells.Add(number)) return RoomSpellDamage.None;

            // An EndCast% under 100 makes the follow-on spell a roll.
            bool endCastRolls = SpellEffectFormatter.EndCastPercent(spell) is > 0 and < 100;
            RoomSpellDamage found = RoomSpellDamage.None;
            foreach (SpellAbility ability in spell.Abilities)
            {
                if (ability.Code == DamageAbility)
                    found = Stronger(found, conditioned ? RoomSpellDamage.Conditional : RoomSpellDamage.EveryTick);
                else if (ability.Code == TextBlockAbility)
                {
                    // A textblock spell with no value in the slot keeps the block's
                    // number in MinBase / MaxBase (GAME_MECHANICS "Room-spell hazard
                    // shape 2 — TextBlock action guarded by `failitem <itemNum>`").
                    int block = ability.Value > 0 ? ability.Value : spell.MinBase > 0 ? spell.MinBase : spell.MaxBase;
                    found = Stronger(found, Lines(block, conditioned, depth + 1));
                }
                else if (ability.Code == EndCastAbility && ability.Value > 0)
                    found = Stronger(found, Past(Spell(ability.Value, conditioned, depth + 1), endCastRolls));
            }
            _spells.Remove(number);
            return found;
        }

        // A block run line by line: the game takes the first line whose steps all
        // pass, and which that is depends on the character, so each line is a path
        // of its own from where the block was entered.
        private RoomSpellDamage Lines(int block, bool conditioned, int depth)
        {
            if (!Enter(table: false, block, depth, out string action)) return RoomSpellDamage.None;
            RoomSpellDamage found = RoomSpellDamage.None;
            foreach (string line in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                found = Stronger(found, Steps(line.Split(':', StringSplitOptions.TrimEntries), conditioned, depth));
            _blocks.Remove((false, block));
            return found;
        }

        // A block rolled as a d100 table: one line runs, picked by the roll. A table
        // that damages whatever is rolled is no roll (the ice slide always drops you;
        // the roll only picks where).
        private RoomSpellDamage Table(int block, bool conditioned, int depth)
        {
            if (!Enter(table: true, block, depth, out string action)) return RoomSpellDamage.None;
            int covered = 0;
            bool everyBand = true;
            RoomSpellDamage strongest = RoomSpellDamage.None;
            foreach ((int threshold, string[] steps) in RoomSummonParser.ReadBands(action))
            {
                // A line at or under an earlier line's number is never the first one
                // above the roll.
                if (threshold <= covered || covered >= RollCeiling) continue;
                covered = threshold;
                RoomSpellDamage band = Steps(steps, conditioned, depth);
                strongest = Stronger(strongest, band);
                everyBand &= band == RoomSpellDamage.EveryTick;
            }
            _blocks.Remove((true, block));
            return everyBand && covered >= RollCeiling ? strongest : Past(strongest, roll: true);
        }

        // One line's steps, left to right. Only what comes before a cast gates it.
        private RoomSpellDamage Steps(IEnumerable<string> steps, bool conditioned, int depth)
        {
            RoomSpellDamage found = RoomSpellDamage.None;
            foreach (string step in steps)
            {
                string[] words = step.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                string verb = words[0];

                if (Is(verb, "cast"))
                    found = Stronger(found, Spell(Number(words, 1), conditioned, depth + 1));
                else if (Is(verb, "random"))
                    found = Stronger(found, Table(Number(words, 1), conditioned, depth + 1));
                else if (Is(verb, "checkspell") || Is(verb, "failspell"))
                    // The second number is the block run when the buff is missing.
                    found = Stronger(found, Lines(Number(words, 2), conditioned, depth + 1));
                else if (Is(verb, "testskill"))
                    // `testskill <skill> [<modifier>] <failTextblock>`: the block runs on a miss.
                    found = Stronger(found, Past(Lines(Number(words, words.Length - 1), conditioned, depth + 1), roll: true));
                else if (Is(verb, "failitem"))
                    continue;
                else if (RoomSpellTeleportClassifier.IsConditionStep(verb))
                    conditioned = true;
            }
            return found;
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

        private RoomSpellDamage Missed(string what)
        {
            Gap ??= what;
            return RoomSpellDamage.None;
        }

        // Damage reached past a roll is no longer damage on every tick.
        private static RoomSpellDamage Past(RoomSpellDamage found, bool roll) =>
            roll && found == RoomSpellDamage.EveryTick ? RoomSpellDamage.OnARoll : found;

        private static RoomSpellDamage Stronger(RoomSpellDamage a, RoomSpellDamage b) => a > b ? a : b;

        private static bool Is(string verb, string name) => verb.Equals(name, StringComparison.OrdinalIgnoreCase);

        private static int Number(string[] words, int index) =>
            index > 0 && index < words.Length
            && int.TryParse(words[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }
}

using System.Collections.Generic;
using System.Globalization;
using MudPlay.Game.Spells;

namespace MudPlay.Game.Map;

// Reads whether a room's cast-on-enter spell teleports whoever it lands on, for the
// Navigation map's by-teleport overlay (GAME_MECHANICS "Room spells that teleport").
//
// A room spell reaches a teleport three ways: its own TeleportRoom ability, a
// `teleport` step in the textblock its TextBlock ability runs, or a `cast` in that
// textblock of a spell that does either. What stands in the way is of two kinds,
// and only one of them is luck:
//   - a condition on the character or the room (failitem / checkitem, class, level,
//     alignment, nomonsters, a buff check, a quest flag). It holds or it doesn't; the
//     map can't know which for the character looking at it, so a teleport behind
//     conditions alone counts as a teleport.
//   - a roll: a `random` table with a band that doesn't teleport, a `testskill`, or
//     an EndCast that fires only some of the time. A teleport behind one is a chance.
// A `random` table whose every band teleports is no roll at all for this purpose:
// the ice slide lands in one of two rooms, but it always slides.
//
// The summon read (RoomSummonParser) follows one textblock to one table; this needs
// every branch and whether a roll was passed on the way there, so it walks the
// chain itself and shares only that parser's table reader.
public static class RoomSpellTeleportClassifier
{
    private const int TextBlockAbility = 148;
    private const int EndCastAbility = 151;

    // A d100 table is rolled 0–99 and runs the first line whose number is above the
    // roll, so only a table whose lines reach 100 runs a line every time.
    private const int RollCeiling = 100;

    // Past this many hops a chain is a data loop the busy sets didn't catch.
    private const int MaxChainDepth = 16;

    // spellOf resolves a spell number to its record, textblock a TBInfo number to
    // its Action; either returns null for a number the set doesn't have.
    public static RoomSpellTeleport Classify(
        int spell, Func<int, SpellFormulaInput?> spellOf, Func<int, string?> textblock)
    {
        ArgumentNullException.ThrowIfNull(spellOf);
        ArgumentNullException.ThrowIfNull(textblock);
        return new Walk(spellOf, textblock).Spell(spell, 0);
    }

    private sealed class Walk(Func<int, SpellFormulaInput?> spellOf, Func<int, string?> textblock)
    {
        // What is being read right now. A chain that comes back to one of these has
        // looped (the sea's tables re-roll each other), and the second visit adds
        // nothing the first won't find.
        private readonly HashSet<int> _spells = new();
        private readonly HashSet<(bool Table, int Block)> _blocks = new();

        public RoomSpellTeleport Spell(int number, int depth)
        {
            if (number <= 0 || depth > MaxChainDepth || spellOf(number) is not { } spell) return RoomSpellTeleport.None;
            if (TBInfoCastTeleportResolver.IsTeleportSpell(spell)) return RoomSpellTeleport.Always;
            if (!_spells.Add(number)) return RoomSpellTeleport.None;

            // An EndCast% under 100 makes the follow-on spell a roll; without one it
            // always fires when this spell ends.
            bool endCastRolls = SpellEffectFormatter.EndCastPercent(spell) is > 0 and < 100;
            RoomSpellTeleport result = RoomSpellTeleport.None;
            foreach (SpellAbility ability in spell.Abilities)
            {
                if (ability.Code == TextBlockAbility)
                {
                    // A textblock spell with no value in the slot keeps the block's
                    // number in MinBase / MaxBase (GAME_MECHANICS "Room-spell hazard
                    // shape 2 — TextBlock action guarded by `failitem <itemNum>`").
                    int block = ability.Value > 0 ? ability.Value : spell.MinBase > 0 ? spell.MinBase : spell.MaxBase;
                    result = Stronger(result, Lines(block, depth + 1));
                }
                else if (ability.Code == EndCastAbility && ability.Value > 0)
                {
                    result = Stronger(result, Capped(Spell(ability.Value, depth + 1), endCastRolls));
                }
            }
            _spells.Remove(number);
            return result;
        }

        // A block run line by line: the game takes the first line whose steps all
        // pass, and which that is depends on the character, so the block is as
        // strong as its strongest line.
        private RoomSpellTeleport Lines(int block, int depth)
        {
            if (!Enter(table: false, block, depth, out string action)) return RoomSpellTeleport.None;
            RoomSpellTeleport result = RoomSpellTeleport.None;
            foreach (string line in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                result = Stronger(result, Steps(line.Split(':', StringSplitOptions.TrimEntries), depth));
            _blocks.Remove((false, block));
            return result;
        }

        // A block rolled as a d100 table: one line runs, picked by the roll. It
        // teleports for sure only when every line that can be picked does.
        private RoomSpellTeleport Table(int block, int depth)
        {
            if (!Enter(table: true, block, depth, out string action)) return RoomSpellTeleport.None;
            int covered = 0;
            bool any = false, every = true;
            foreach ((int threshold, string[] steps) in RoomSummonParser.ReadBands(action))
            {
                // A line at or under an earlier line's number is never the first one
                // above the roll.
                if (threshold <= covered || covered >= RollCeiling) continue;
                covered = threshold;
                RoomSpellTeleport band = Steps(steps, depth);
                any |= band != RoomSpellTeleport.None;
                every &= band == RoomSpellTeleport.Always;
            }
            _blocks.Remove((true, block));
            if (!any) return RoomSpellTeleport.None;
            return every && covered >= RollCeiling ? RoomSpellTeleport.Always : RoomSpellTeleport.Chance;
        }

        private RoomSpellTeleport Steps(IEnumerable<string> steps, int depth)
        {
            bool rolled = false;
            RoomSpellTeleport result = RoomSpellTeleport.None;
            foreach (string step in steps)
            {
                string[] words = step.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                string verb = words[0];

                // `teleport <room> <map>`, and the named forms (`teleport_sewers`)
                // that pick a room of their area.
                if (verb.StartsWith("teleport", StringComparison.OrdinalIgnoreCase))
                    result = Stronger(result, Capped(RoomSpellTeleport.Always, rolled));
                else if (Is(verb, "cast"))
                    result = Stronger(result, Capped(Spell(Number(words, 1), depth + 1), rolled));
                else if (Is(verb, "random"))
                    result = Stronger(result, Capped(Table(Number(words, 1), depth + 1), rolled));
                else if (Is(verb, "checkspell") || Is(verb, "failspell"))
                    // The second number is the block run when the buff is missing: a
                    // condition, like the rest of the line.
                    result = Stronger(result, Capped(Lines(Number(words, 2), depth + 1), rolled));
                else if (Is(verb, "testskill"))
                {
                    // `testskill <skill> [<modifier>] <failTextblock>` rolls: the block
                    // runs on a miss, the rest of the line on a pass.
                    result = Stronger(result, Capped(Lines(Number(words, words.Length - 1), depth + 1), rolled: true));
                    rolled = true;
                }
            }
            return result;
        }

        private bool Enter(bool table, int block, int depth, out string action)
        {
            action = string.Empty;
            if (block <= 0 || depth > MaxChainDepth) return false;
            if (textblock(block) is not { } text || string.IsNullOrWhiteSpace(text)) return false;
            if (!_blocks.Add((table, block))) return false;
            action = text;
            return true;
        }

        private static bool Is(string verb, string name) => verb.Equals(name, StringComparison.OrdinalIgnoreCase);

        private static int Number(string[] words, int index) =>
            index > 0 && index < words.Length
            && int.TryParse(words[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;

        private static RoomSpellTeleport Stronger(RoomSpellTeleport a, RoomSpellTeleport b) => a > b ? a : b;

        // A teleport found past a roll is a chance at best.
        private static RoomSpellTeleport Capped(RoomSpellTeleport found, bool rolled) =>
            rolled && found > RoomSpellTeleport.Chance ? RoomSpellTeleport.Chance : found;
    }
}

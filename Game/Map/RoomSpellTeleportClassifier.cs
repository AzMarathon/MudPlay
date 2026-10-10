using System.Collections.Generic;
using System.Globalization;
using MudPlay.Game.Spells;

namespace MudPlay.Game.Map;

// Reads whether a room's cast-on-enter spell teleports whoever it lands on, for the
// Navigation map's by-teleport overlay (GAME_MECHANICS "Room spells that teleport").
//
// A room spell reaches a teleport three ways: its own TeleportRoom ability, a
// `teleport` step in the textblock its TextBlock ability runs, or a `cast` in that
// textblock of a spell that does either. Each way there is a path, and what a path
// passes on the way is of two kinds:
//   - a condition on the character or the room (an item, class, level, alignment,
//     monsters present, a quest flag, a buff). It holds or it doesn't.
//   - a roll: a `random` table, a `testskill`, an EndCast that fires only some of
//     the time.
// A teleport behind conditions alone is Conditional. One behind nothing, or behind a
// roll (with or without conditions beside it), is Sudden. The spell takes the
// strongest of its paths.
//
// A `random` table whose every band teleports is no roll: the ice slide lands in
// one of two rooms, but it always slides. A band counts as teleporting when it
// reaches a teleport with no roll of its own, conditions or not. So a table whose
// bands teleport under different conditions reads as conditional, though for a
// character meeting only some of them it is a roll; no table a room spell reaches
// in either realm's data is built that way.
//
// The summon read (RoomSummonParser) follows one textblock to one table; this needs
// every branch and what was passed on the way there, so it walks the chain itself
// and shares only that parser's table reader.
public static class RoomSpellTeleportClassifier
{
    private const int EndCastAbility = 151;

    // A d100 table is rolled 0–99 and runs the first line whose number is above the
    // roll, so only a table whose lines reach 100 runs a line every time.
    private const int RollCeiling = 100;

    // The busy sets stop a chain that loops; this stops one that is merely long,
    // and the walk says so rather than call what it didn't read "no teleport".
    internal const int MaxChainDepth = 16;

    // The steps of the game's textblock interpreter that pass or fail on a fact
    // about the character or the room (GAME_MECHANICS "Textblock directives — the
    // Stock interpreter's list"). `checkspell` / `failspell` are conditions too, read
    // apart because they name a block. A word outside the list doesn't gate anything:
    // the interpreter passes over what it doesn't know.
    private static readonly HashSet<string> ConditionVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "minlevel", "maxlevel", "checkitem", "failitem", "takeitem", "nomonsters", "monsters", "needmonster",
        "class", "race", "goodaligned", "evilaligned", "checkability", "testability", "failability", "price",
        "checkskill", "flag", "test_tournament", "roomitem", "failroomitem", "clearitem",
    };

    // spellOf resolves a spell number to its record, textblock a TBInfo number to
    // its entry; either returns null for a number the set doesn't have. gap names
    // the first thing the walk couldn't read, or is null when it read everything;
    // a spell with a gap and no teleport found comes back Unknown, not None.
    public static RoomSpellTeleport Classify(
        int spell, Func<int, SpellFormulaInput?> spellOf, Func<int, TBInfoEntry?> textblock, out string? gap)
    {
        ArgumentNullException.ThrowIfNull(spellOf);
        ArgumentNullException.ThrowIfNull(textblock);
        var walk = new Walk(spellOf, textblock);
        Reach reach = walk.Spell(spell, default, 0);
        gap = walk.Gap;
        if (reach.Found != RoomSpellTeleport.None) return reach.Found;
        return gap is null ? RoomSpellTeleport.None : RoomSpellTeleport.Unknown;
    }

    // What the path so far has passed.
    private readonly record struct Gates(bool Rolled, bool Conditioned)
    {
        public RoomSpellTeleport Teleport =>
            Rolled || !Conditioned ? RoomSpellTeleport.Sudden : RoomSpellTeleport.Conditional;
    }

    // What a part of the chain reaches: its strongest teleport, and whether it gets
    // to one without a roll of its own (what lets a table of such parts be no roll).
    private readonly record struct Reach(RoomSpellTeleport Found, bool Unrolled)
    {
        public Reach With(Reach other) =>
            new(Found > other.Found ? Found : other.Found, Unrolled || other.Unrolled);

        public Reach Past(bool roll) => roll ? this with { Unrolled = false } : this;
    }

    private sealed class Walk(Func<int, SpellFormulaInput?> spellOf, Func<int, TBInfoEntry?> textblock)
    {
        // What is being read right now. A chain that comes back to one of these has
        // looped (the sea's tables re-roll each other), and the second visit adds
        // nothing the first won't find.
        private readonly HashSet<int> _spells = new();
        private readonly HashSet<(bool Table, int Block)> _blocks = new();

        public string? Gap { get; private set; }

        public Reach Spell(int number, Gates gates, int depth)
        {
            if (number <= 0) return default;
            if (depth > MaxChainDepth) return Missed($"chain cut at spell {number}, {MaxChainDepth} steps in");
            if (spellOf(number) is not { } spell) return Missed($"spell {number} missing");
            if (TBInfoCastTeleportResolver.IsTeleportSpell(spell)) return new Reach(gates.Teleport, Unrolled: true);
            if (!_spells.Add(number)) return default;

            // An EndCast% under 100 makes the follow-on spell a roll; without one it
            // always fires when this spell ends.
            bool endCastRolls = SpellEffectFormatter.EndCastPercent(spell) is > 0 and < 100;
            Reach reach = default;
            foreach (SpellAbility ability in spell.Abilities)
            {
                if (ability.Code == SpellTextBlock.AbilityCode)
                {
                    int block = SpellTextBlock.Number(ability.Value, spell.MinBase, spell.MaxBase);
                    reach = reach.With(Lines(block, gates, depth + 1));
                }
                else if (ability.Code == EndCastAbility && ability.Value > 0)
                {
                    Gates after = gates with { Rolled = gates.Rolled || endCastRolls };
                    reach = reach.With(Spell(ability.Value, after, depth + 1).Past(endCastRolls));
                }
            }
            _spells.Remove(number);
            return reach;
        }

        // A block run line by line: the game takes the first line whose steps all
        // pass, and which that is depends on the character, so each line is a path
        // of its own from where the block was entered.
        private Reach Lines(int block, Gates gates, int depth)
        {
            if (!Enter(table: false, block, depth, out string action)) return default;
            Reach reach = default;
            foreach (string line in action.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                reach = reach.With(Steps(line.Split(':', StringSplitOptions.TrimEntries), gates, depth));
            _blocks.Remove((false, block));
            return reach;
        }

        // A block rolled as a d100 table: one line runs, picked by the roll.
        private Reach Table(int block, Gates gates, int depth)
        {
            if (!Enter(table: true, block, depth, out string action)) return default;
            int covered = 0;
            bool everyBandTeleports = true;
            RoomSpellTeleport strongest = RoomSpellTeleport.None;
            foreach ((int threshold, string[] steps) in RoomSummonParser.ReadBands(action))
            {
                // A line at or under an earlier line's number is never the first one
                // above the roll.
                if (threshold <= covered || covered >= RollCeiling) continue;
                covered = threshold;
                Reach band = Steps(steps, gates, depth);
                if (band.Found > strongest) strongest = band.Found;
                everyBandTeleports &= band.Unrolled;
            }
            _blocks.Remove((true, block));
            if (strongest == RoomSpellTeleport.None) return default;
            // A table that teleports whatever is rolled only picks the landing. Any
            // other is a roll, and a teleport past a roll is Sudden whatever else
            // gates it.
            return everyBandTeleports && covered >= RollCeiling
                ? new Reach(strongest, Unrolled: true)
                : new Reach(RoomSpellTeleport.Sudden, Unrolled: false);
        }

        // One line's steps, left to right. Only what comes before a teleport gates
        // it: a step that fails later on the line can't take the move back.
        private Reach Steps(IEnumerable<string> steps, Gates gates, int depth)
        {
            bool rolledHere = false;
            Reach reach = default;
            foreach (string step in steps)
            {
                string[] words = step.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                string verb = words[0];

                // `teleport <room> <map>`, and the named forms (`teleport_sewers`)
                // that pick a room of their area.
                if (verb.StartsWith("teleport", StringComparison.OrdinalIgnoreCase))
                    reach = reach.With(new Reach(gates.Teleport, Unrolled: !rolledHere));
                else if (Is(verb, "cast"))
                    reach = reach.With(Spell(Number(words, 1), gates, depth + 1).Past(rolledHere));
                else if (Is(verb, "random"))
                    reach = reach.With(Table(Number(words, 1), gates, depth + 1).Past(rolledHere));
                else if (Is(verb, "checkspell") || Is(verb, "failspell"))
                {
                    // The second number is the block run when the buff is missing;
                    // the rest of the line runs when it is up. Either way the buff
                    // decided it.
                    gates = gates with { Conditioned = true };
                    reach = reach.With(Lines(Number(words, 2), gates, depth + 1).Past(rolledHere));
                }
                else if (Is(verb, "testskill"))
                {
                    // `testskill <skill> [<modifier>] <failTextblock>` rolls: the block
                    // runs on a miss, the rest of the line on a pass.
                    gates = gates with { Rolled = true };
                    rolledHere = true;
                    reach = reach.With(Lines(Number(words, words.Length - 1), gates, depth + 1).Past(roll: true));
                }
                else if (ConditionVerbs.Contains(verb))
                    gates = gates with { Conditioned = true };
            }
            return reach;
        }

        private bool Enter(bool table, int block, int depth, out string action)
        {
            action = string.Empty;
            if (block <= 0) return false;
            if (depth > MaxChainDepth)
            {
                Missed($"chain cut at textblock {block}, {MaxChainDepth} steps in");
                return false;
            }
            if (textblock(block) is not { } entry)
            {
                Missed($"textblock {block} missing");
                return false;
            }
            // A continuation record may hold more steps; how the game runs one from
            // a spell's textblock isn't known, so it is left unread and said so.
            if (entry.LinkTo > 0) Missed($"textblock {block} continues in {entry.LinkTo}, not read");
            // A block that is there and empty does nothing, which is an answer.
            if (string.IsNullOrWhiteSpace(entry.Action)) return false;
            if (!_blocks.Add((table, block))) return false;
            action = entry.Action;
            return true;
        }

        private Reach Missed(string what)
        {
            Gap ??= what;
            return default;
        }

        private static bool Is(string verb, string name) => verb.Equals(name, StringComparison.OrdinalIgnoreCase);

        private static int Number(string[] words, int index) =>
            index > 0 && index < words.Length
            && int.TryParse(words[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0;
    }
}

using System.Globalization;
using MudPlay.Game.Map;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// Turns the script lines that touch one ability flag into a walkthrough. A step is put into
// words only where GAME_MECHANICS.md settles what the directive does; every other step is
// carried over exactly as the data writes it, so nothing here guesses at a script's meaning.
//
// The settled directives, with the GAME_MECHANICS topic behind each:
//   failability / checkability / testability — "NPC keyword hand-over detection" (at least /
//     at most) and "Deck of cards (Gypsy)" (failability stops the line for a character who
//     has the ability; its second number is a message).
//   giveability / addability / removeability — "Quest stat rewards — giveability vs addability".
//   minlevel / maxlevel, checkitem / takeitem, nomonsters / monsters / needmonster, and the
//     trailing message number on a condition — "Room-command refusals".
//   class — "Greet teleports — an NPC transports a player who asks".
//   failitem — "Room-spell hazard shape 2 — TextBlock action guarded by failitem <itemNum>".
//   roomitem — "Item-use teleports".
//   price — "Repeated price directives add up".
//   giveitem — "Chests and chest loot tables"; teleport <room> <map> — "CMD-driven room
//     teleports split the party".
// race, addexp and learnspell are read the way QuestCrawler and KnownSpellCatalog already read
// them. cast and summon stay verbatim with the name of the record they point at.
public static class QuestFlagWalkthroughBuilder
{
    private const int MaxRoomsNamed = 6;
    private const int MaxPlacementsNamed = 3;

    // monsterRooms is where each monster stands (RoomSearchService.QuestKillRooms); without it
    // an NPC or a kill target is named with no room.
    public static QuestFlagWalkthrough Build(
        QuestFlagIndex index, GameDataCache cache, int flag,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(cache);

        string flagName = QuestScriptNames.Ability(flag);
        IReadOnlyList<QuestFlagLine> lines = index.LinesFor(flag);
        if (index.Names is not { } names)
            return new QuestFlagWalkthrough(flag, flagName, Array.Empty<CrawledQuest>(), string.Empty, string.Empty,
                Array.Empty<QuestFlagStepEntry>(), Array.Empty<QuestFlagStepEntry>(), Array.Empty<int>());

        // Lines that say the same thing fold into one entry, keeping the first one's place.
        List<List<Described>> groups = new();
        Dictionary<string, List<Described>> byKey = new(StringComparer.Ordinal);
        foreach (QuestFlagLine line in lines)
        {
            Described d = Describe(line, flag, index, names);
            if (!byKey.TryGetValue(d.Key, out List<Described>? group))
            {
                byKey[d.Key] = group = new List<Described>();
                groups.Add(group);
            }
            group.Add(d);
        }

        List<List<Described>> ordered = groups
            .OrderBy(g => g[0].LowKey)
            .ThenBy(g => g[0].HighKey)
            .ThenBy(g => g[0].Leaves)
            .ThenBy(g => g[0].Line.Textblock)
            .ThenBy(g => g[0].Line.Order)
            .ToList();

        // A line that only asks for the flag to be absent and leaves it alone belongs to
        // something else (the alignment flags each bar the other two paths' whole quests), so
        // those are listed apart rather than ahead of the flag's own steps.
        List<QuestFlagStepEntry> steps = new(), withoutFlag = new();
        foreach (List<Described> group in ordered)
            (group[0].OnlyWithout ? withoutFlag : steps).Add(ToEntry(group, names, monsterRooms));

        List<int> otherFlags = steps.Concat(withoutFlag)
            .SelectMany(s => s.OtherFlags).Distinct().OrderBy(f => f).ToList();

        List<CrawledQuest> quests = QuestCrawler.Crawl(cache, null).Where(q => q.Flag == flag).ToList();
        return new QuestFlagWalkthrough(
            flag, flagName, quests, CompleteText(quests), RestrictionText(quests, names),
            steps, withoutFlag, otherFlags);
    }

    // ----- One line -----

    // What one line asks of an ability and what it then does to it.
    private sealed class FlagCondition
    {
        public bool Without;
        public int? Low;
        public int? High;
    }

    private sealed class Described
    {
        public required QuestFlagLine Line { get; init; }
        public required string Key { get; init; }
        public required string Heading { get; init; }
        public int LowKey { get; init; }
        public int HighKey { get; init; }
        public int Leaves { get; init; }
        public bool OnlyWithout { get; init; }
        public string? LevelNeed { get; init; }
        public string? RaceNeed { get; init; }
        public required List<string> Needs { get; init; }
        public required List<string> Gives { get; init; }
        public required List<string> Also { get; init; }
        public required List<int> OtherFlags { get; init; }
    }

    private static Described Describe(QuestFlagLine line, int flag, QuestFlagIndex index, QuestScriptNames names)
    {
        QuestScriptLine script = line.Script;
        FlagCondition subject = new();
        List<string> effects = new();
        int? setTo = null;
        int added = 0;
        bool changed = false;

        List<(int Ability, FlagCondition Condition)> others = new();
        List<int> otherFlags = new();
        List<int> held = new(), mustNotHold = new(), inRoom = new();
        List<(int Item, int Count)> taken = new(), given = new();
        List<(int Amount, string Coin, int Count)> prices = new();
        List<string> monsterNeeds = new(), alignment = new(), gives = new(), also = new();

        foreach (QuestScriptStep step in script.Steps)
        {
            int? a = step.Int(0), b = step.Int(1);
            bool onSubject = a == flag;
            switch (step.Verb)
            {
                // A check that follows the line's own change to the flag reads the new value,
                // so it says nothing about the value going in and stays verbatim.
                // failability <ability> [message] is the settled form; one written with more
                // numbers falls through and is quoted.
                case "failability" when onSubject && !changed && step.Args.Count <= 2:
                    subject.Without = true;
                    break;
                case "checkability" when onSubject && !changed && b is int atLeast:
                    subject.Low = subject.Low is int low ? Math.Max(low, atLeast) : atLeast;
                    break;
                case "testability" when onSubject && !changed && b is int atMost:
                    subject.High = subject.High is int high ? Math.Min(high, atMost) : atMost;
                    break;
                case "giveability" when onSubject && b is int value:
                    effects.Add($"sets {Num(value)}");
                    setTo = value;
                    added = 0;
                    changed = true;
                    break;
                case "addability" when onSubject && b is int delta:
                    effects.Add(delta >= 0 ? $"adds {Num(delta)}" : $"takes off {Num(-delta)}");
                    added += delta;
                    changed = true;
                    break;
                case "removeability" when onSubject:
                    effects.Add("clears the flag");
                    setTo = 0;
                    added = 0;
                    changed = true;
                    break;

                case "failability" when a is int ability && ability > 0 && !onSubject && step.Args.Count <= 2:
                    ConditionFor(others, ability).Without = true;
                    AddOnce(otherFlags, ability);
                    break;
                case "checkability" when a is int ability && ability > 0 && !onSubject && b is int atLeast:
                {
                    FlagCondition c = ConditionFor(others, ability);
                    c.Low = c.Low is int otherLow ? Math.Max(otherLow, atLeast) : atLeast;
                    AddOnce(otherFlags, ability);
                    break;
                }
                case "testability" when a is int ability && ability > 0 && !onSubject && b is int atMost:
                {
                    FlagCondition c = ConditionFor(others, ability);
                    c.High = c.High is int otherHigh ? Math.Min(otherHigh, atMost) : atMost;
                    AddOnce(otherFlags, ability);
                    break;
                }
                case "giveability" when a is int ability && ability > 0 && !onSubject && b is int value:
                    gives.Add($"Sets {AbilityLabel(ability)} to {Num(value)}");
                    AddOnce(otherFlags, ability);
                    break;
                case "addability" when a is int ability && ability > 0 && !onSubject && b is int delta:
                    // An addability to something no script grants with giveability is a stat
                    // reward, the rule the quest crawl uses.
                    if (index.IsQuestFlag(ability))
                    {
                        gives.Add($"Adds {Num(delta)} to {AbilityLabel(ability)}");
                        AddOnce(otherFlags, ability);
                    }
                    else
                    {
                        gives.Add($"{QuestScriptNames.Ability(ability)} {delta.ToString("+#;-#;0", CultureInfo.InvariantCulture)}");
                    }
                    break;
                case "removeability" when a is int ability && ability > 0 && !onSubject:
                    gives.Add($"Clears {AbilityLabel(ability)}");
                    AddOnce(otherFlags, ability);
                    break;

                // The line's level, class and race come off the parsed line as a whole.
                case "minlevel" or "maxlevel" or "class" or "race" when a is > 0:
                    break;

                case "checkitem" when a is int item && item > 0:
                    AddOnce(held, item);
                    break;
                case "takeitem" when a is int item && item > 0:
                    Count(taken, item);
                    break;
                case "failitem" when a is int item && item > 0:
                    AddOnce(mustNotHold, item);
                    break;
                case "roomitem" when a is int item && item > 0:
                    AddOnce(inRoom, item);
                    break;
                case "price" when a is int amount && amount > 0:
                    CountPrice(prices, amount, Coin(step.Raw));
                    break;
                case "nomonsters":
                    AddOnce(monsterNeeds, "No monster in the room (an NPC counts as one)");
                    break;
                case "monsters":
                    AddOnce(monsterNeeds, "A monster in the room");
                    break;
                case "needmonster" when a is int monster && monster > 0:
                    AddOnce(monsterNeeds, $"{names.Monster(monster)} (monster {Id(monster)}) in the room");
                    break;
                // The threshold's meaning isn't settled, so the step is quoted; its trailing
                // number is the refusal message and is left off.
                case "goodaligned" or "evilaligned" when step.Args.Count > 0:
                    AddOnce(alignment, $"{step.Verb} {step.Args[0]}");
                    break;

                case "giveitem" when a is int item && item > 0:
                    Count(given, item);
                    break;
                case "addexp" when a is int exp && exp > 0:
                    gives.Add($"{Num(exp)} experience");
                    break;
                case "learnspell" when a is int spell && spell > 0:
                    gives.Add($"Teaches the spell {names.Spell(spell)} (spell {Id(spell)})");
                    break;
                case "teleport" when a is int room && room > 0 && b is int map && map > 0:
                    gives.Add($"Teleports you to {names.Room(map, room)} ({Id(map)}/{Id(room)})");
                    break;

                case "cast" when a is int spell && spell > 0:
                    AddRaw(also, $"{step.Raw}  [spell: {names.Spell(spell)}]");
                    break;
                case "summon" when a is int monster && monster > 0:
                    AddRaw(also, $"{step.Raw}  [monster: {names.Monster(monster)}]");
                    break;
                default:
                    AddRaw(also, step.Raw);
                    break;
            }
        }

        List<string> needs = new();
        foreach ((int ability, FlagCondition condition) in others)
            needs.Add(OtherConditionText(condition, AbilityLabel(ability)));
        foreach (int item in held)
            if (!taken.Exists(t => t.Item == item)) needs.Add($"Have {ItemLabel(names, item)}");
        foreach ((int item, int count) in taken)
            needs.Add($"Taken from you: {Times(count)}{ItemLabel(names, item)}");
        foreach (int item in mustNotHold)
            needs.Add($"Must not have {ItemLabel(names, item)}");
        foreach (int item in inRoom)
            needs.Add($"{ItemLabel(names, item)} must be in the room");
        foreach ((int amount, string coin, int count) in prices)
            needs.Add(count > 1
                ? $"Costs {Num((long)amount * count)} {coin} ({Num(count)} × {Num(amount)})"
                : $"Costs {Num(amount)} {coin}");
        needs.AddRange(monsterNeeds);
        if (alignment.Count > 0)
            needs.Add("Alignment, as the script writes it: " + string.Join(", ", alignment));

        // Items first: they are what a player is usually after.
        gives.InsertRange(0, given.Select(g => $"{Times(g.Count)}{ItemLabel(names, g.Item)}"));

        // An "at most N" line with no floor is placed at N, beside the steps for that value,
        // rather than ahead of everything.
        int lowKey = subject.Low ?? (subject.High is int h && !subject.Without ? h : 0);
        int highKey = subject.High ?? (subject.Without && subject.Low is null ? 0 : int.MaxValue);
        return new Described
        {
            Line = line,
            Key = KeyOf(line),
            Heading = HeadingText(subject, effects),
            LowKey = lowKey,
            HighKey = highKey,
            Leaves = (setTo ?? lowKey) + added,
            OnlyWithout = subject.Without && subject.Low is null && subject.High is null && effects.Count == 0,
            LevelNeed = LevelNeed(script),
            RaceNeed = script.Races.Count > 0 ? "Race: " + string.Join(", ", script.Races.Select(names.Race)) : null,
            Needs = needs,
            Gives = gives,
            Also = also,
            OtherFlags = otherFlags,
        };
    }

    // Two lines fold together when they are the same script reached the same way: apart from
    // the class step (the entry lists the classes) and the wording of a typed command (the
    // entry lists every wording). Any other difference keeps them apart.
    private static string KeyOf(QuestFlagLine line)
    {
        System.Text.StringBuilder sb = new();
        foreach (QuestScriptStep step in line.Script.Steps)
        {
            if (step.Verb == "class" && step.Int(0) is > 0) continue;
            sb.Append(step.Raw).Append(':');
        }
        sb.Append('|').Append(line.Script.RollBand?.ToString(CultureInfo.InvariantCulture)).Append('|');
        foreach (QuestTrigger t in line.Triggers)
        {
            sb.Append((int)t.Kind).Append('/').Append(t.Monster).Append('/').Append(t.Map).Append('/').Append(t.Room);
            if (t.Kind != QuestTriggerKind.RoomCommand) sb.Append('/').Append(t.Command);
            sb.Append(';');
        }
        // With no way in established, only lines of one textblock are the same script.
        if (line.Triggers.Count == 0)
            sb.Append(line.Textblock).Append('/').Append(line.Script.Command);
        return sb.ToString();
    }

    private static string HeadingText(FlagCondition subject, List<string> effects)
    {
        List<string> parts = new();
        if (subject.Without) parts.Add("without the flag");
        if (subject.Low is int low && subject.High is int high)
            parts.Add(low == high ? $"at exactly {Num(low)}"
                : low < high ? $"at {Num(low)} to {Num(high)}"
                : $"at {Num(low)} or more and at {Num(high)} or less");
        else if (subject.Low is int atLeast)
            parts.Add($"at {Num(atLeast)} or more");
        else if (subject.High is int atMost)
            // testability fails for a character who doesn't have the flag at all.
            parts.Add(atMost >= 0 ? $"holding the flag at {Num(atMost)} or less" : $"at {Num(atMost)} or less");

        string effect = effects.Count == 0 ? "no change" : string.Join(", then ", effects);
        if (parts.Count == 0) return $"No check on the flag → {effect}";
        string condition = string.Join(" and ", parts);
        if (subject.Without && parts.Count == 1 && effects.Count > 0) return $"Start — {condition} → {effect}";
        return $"{char.ToUpperInvariant(condition[0])}{condition[1..]} → {effect}";
    }

    private static string OtherConditionText(FlagCondition c, string label)
    {
        List<string> parts = new();
        if (c.Without) parts.Add($"Without {label}");
        if (c.Low is int low && c.High is int high)
            parts.Add(low == high ? $"{label} exactly {Num(low)}" : $"{label} from {Num(low)} to {Num(high)}");
        else if (c.Low is int atLeast)
            parts.Add($"{label} at least {Num(atLeast)}");
        else if (c.High is int atMost)
            parts.Add($"{label} at most {Num(atMost)}");
        return string.Join("; ", parts);
    }

    private static string? LevelNeed(QuestScriptLine script)
    {
        if (script.MinLevel > 0 && script.MaxLevel > 0)
            return $"Level {Num(script.MinLevel)} to {Num(script.MaxLevel)}";
        if (script.MinLevel > 0) return $"Level {Num(script.MinLevel)} or higher";
        return script.MaxLevel > 0 ? $"Level {Num(script.MaxLevel)} or lower" : null;
    }

    // ----- One entry -----

    private static QuestFlagStepEntry ToEntry(
        List<Described> group, QuestScriptNames names,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms)
    {
        Described first = group[0];

        List<string> needs = new();
        if (first.LevelNeed is { } level) needs.Add(level);
        List<int> classes = group.SelectMany(d => d.Line.Script.Classes).Distinct().ToList();
        if (classes.Count > 0) needs.Add("Class: " + string.Join(", ", classes.Select(names.Class)));
        if (first.RaceNeed is { } race) needs.Add(race);
        needs.AddRange(first.Needs);

        // Lines of one textblock that differ only in the typed command are one script; the
        // first is quoted and the rest counted, since Do already lists every wording.
        List<string> script = new();
        List<(string Body, int At, int More)> quoted = new();
        foreach (Described d in group)
        {
            QuestScriptLine line = d.Line.Script;
            string body = $"{Id(d.Line.Textblock)}|{string.Join(':', line.Steps.Select(s => s.Raw))}";
            int at = line.Command is null ? -1 : quoted.FindIndex(q => q.Body == body);
            if (at >= 0)
            {
                quoted[at] = (body, quoted[at].At, quoted[at].More + 1);
                continue;
            }
            quoted.Add((body, script.Count, 0));
            script.Add($"Textblock #{Id(d.Line.Textblock)}: {line.Raw}");
        }
        foreach ((_, int at, int more) in quoted.OrderByDescending(q => q.At))
            if (more > 0)
                script.Insert(at + 1, $"    (and {Num(more)} more {(more == 1 ? "line" : "lines")} with the other {(more == 1 ? "wording" : "wordings")} of the command, otherwise the same)");

        return new QuestFlagStepEntry(
            first.Heading, DoLines(group, names, monsterRooms), needs, first.Gives, first.Also, script, first.OtherFlags);
    }

    private static List<string> DoLines(
        List<Described> group, QuestScriptNames names,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms)
    {
        List<string> lines = new();
        List<QuestTrigger> triggers = group.SelectMany(d => d.Line.Triggers).ToList();

        // Room commands: every wording, then every room that takes them.
        List<string> typed = new();
        List<(int Map, int Room)> rooms = new();
        foreach (QuestTrigger t in triggers.Where(t => t.Kind == QuestTriggerKind.RoomCommand))
        {
            AddOnce(typed, t.Command);
            AddOnce(rooms, (t.Map, t.Room));
        }
        if (typed.Count > 0)
        {
            string wording = typed.Count == 1 ? $"Type \"{typed[0]}\"" : "Type any of: " + string.Join(", ", typed.Select(c => $"\"{c}\""));
            List<string> where = rooms.Take(MaxRoomsNamed)
                .Select(r => $"{names.Room(r.Map, r.Room)} ({Id(r.Map)}/{Id(r.Room)})").ToList();
            if (rooms.Count > MaxRoomsNamed) where.Add($"{Num(rooms.Count - MaxRoomsNamed)} more rooms");
            lines.Add($"{wording} in {string.Join(", ", where)}");
        }

        foreach (QuestTrigger t in triggers.Where(t => t.Kind == QuestTriggerKind.Ask))
        {
            string line = $"Type \"{t.Command}\"";
            if (t.OtherKeywords.Count > 0) line += $" (the same reply comes from: {string.Join(", ", t.OtherKeywords)})";
            line += $" — {names.Monster(t.Monster)}{Placement(t.Monster, " is in ", names, monsterRooms)}";
            if (!lines.Contains(line)) lines.Add(line);
        }

        List<int> seenKill = new();
        foreach (QuestTrigger t in triggers.Where(t => t.Kind == QuestTriggerKind.Kill))
        {
            if (seenKill.Contains(t.Monster)) continue;
            seenKill.Add(t.Monster);
            lines.Add($"Kill {names.Monster(t.Monster)} (monster {Id(t.Monster)}){Placement(t.Monster, ", found in ", names, monsterRooms)} — its death runs this script");
        }

        QuestFlagLine head = group[0].Line;
        if (triggers.Count == 0)
        {
            // With a textblock in between, name both the parent and where its chain starts;
            // a line hanging straight off a room, spell or monster needs only the one.
            bool viaTextblock = head.CalledFrom.Contains("Textblock", StringComparison.OrdinalIgnoreCase);
            if (head.CalledFrom.Length == 0)
                lines.Add("The data records nothing that calls this textblock");
            else if (viaTextblock || head.Sources.Length == 0)
                lines.Add($"Reached from {head.CalledFrom}");
            if (head.Sources.Length > 0)
                lines.Add(viaTextblock ? $"That chain of textblocks starts at {head.Sources}" : $"Reached from {head.Sources}");
        }
        if (head.Script.RollBand is int band)
        {
            string share = head.RollChance is double chance
                ? $" ({chance.ToString("0.#", CultureInfo.InvariantCulture)}% of draws)"
                : string.Empty;
            lines.Add($"One outcome of a random draw from textblock #{Id(head.Textblock)}: the band up to {Num(band)}{share}");
        }
        return lines;
    }

    private static string Placement(
        int monster, string lead, QuestScriptNames names,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms)
    {
        if (monsterRooms is null || !monsterRooms.TryGetValue(monster, out IReadOnlyList<RoomKey>? rooms) || rooms.Count == 0)
            return string.Empty;
        List<string> where = rooms.Take(MaxPlacementsNamed)
            .Select(r => $"{names.Room(r.Map, r.Room)} ({Id(r.Map)}/{Id(r.Room)})").ToList();
        if (rooms.Count > MaxPlacementsNamed) where.Add($"{Num(rooms.Count - MaxPlacementsNamed)} more rooms");
        return lead + string.Join(", ", where);
    }

    // ----- The quest crawl's findings -----

    private static string CompleteText(List<CrawledQuest> quests)
    {
        if (quests.Count == 0) return "The quest list has no quest for this flag.";
        if (quests.Count == 1)
            return quests[0].CompleteValue is int value
                ? $"Reads complete at {Num(value)}."
                : "No detectable complete value.";
        return "Its tiers read complete at "
            + string.Join(" / ", quests.Select(q => q.CompleteValue is int v ? Num(v) : "no detectable value")) + ".";
    }

    private static string RestrictionText(List<CrawledQuest> quests, QuestScriptNames names)
    {
        if (quests.Count == 0) return string.Empty;
        CrawledQuest quest = quests[0];
        List<string> parts = new();
        if (quest.ClassIds is { Count: > 0 } classes)
            parts.Add("Classes: " + string.Join(", ", classes.Select(c =>
                quest.ClassLevels is { } levels && levels.TryGetValue(c, out int lvl)
                    ? $"{names.Class(c)} (level {Num(lvl)})" : names.Class(c))));
        if (quest.RaceIds is { Count: > 0 } races)
            parts.Add("Races: " + string.Join(", ", races.Select(names.Race)));
        return parts.Count == 0 ? "No class or race restriction found." : string.Join(". ", parts) + ".";
    }

    // ----- Small helpers -----

    private static string AbilityLabel(int ability) => $"{QuestScriptNames.Ability(ability)} ({Id(ability)})";

    private static string ItemLabel(QuestScriptNames names, int item) => $"{names.Item(item)} (item {Id(item)})";

    private static string Times(int count) => count > 1 ? $"{Num(count)} × " : string.Empty;

    // The coin a price names is its trailing letter; no letter is copper.
    private static string Coin(string rawStep) => char.ToUpperInvariant(rawStep[^1]) switch
    {
        'R' => "runic",
        'P' => "platinum",
        'G' => "gold",
        'S' => "silver",
        _   => "copper",
    };

    private static FlagCondition ConditionFor(List<(int Ability, FlagCondition Condition)> list, int ability)
    {
        foreach ((int a, FlagCondition c) in list)
            if (a == ability) return c;
        FlagCondition added = new();
        list.Add((ability, added));
        return added;
    }

    private static void Count(List<(int Item, int Count)> list, int item)
    {
        int at = list.FindIndex(e => e.Item == item);
        if (at < 0) list.Add((item, 1));
        else list[at] = (item, list[at].Count + 1);
    }

    private static void CountPrice(List<(int Amount, string Coin, int Count)> list, int amount, string coin)
    {
        int at = list.FindIndex(e => e.Amount == amount && e.Coin == coin);
        if (at < 0) list.Add((amount, coin, 1));
        else list[at] = (amount, coin, list[at].Count + 1);
    }

    // A step repeated back to back is written once with its count.
    private static void AddRaw(List<string> also, string text)
    {
        if (also.Count > 0)
        {
            string last = also[^1];
            int mark = last.LastIndexOf("  ×", StringComparison.Ordinal);
            string lastText = mark < 0 ? last : last[..mark];
            if (lastText == text)
            {
                int count = mark < 0 ? 1 : int.Parse(last[(mark + 3)..], CultureInfo.InvariantCulture);
                also[^1] = $"{text}  ×{Id(count + 1)}";
                return;
            }
        }
        also.Add(text);
    }

    private static void AddOnce<T>(List<T> list, T value)
    {
        if (!list.Contains(value)) list.Add(value);
    }

    // Counts and amounts get thousands separators; record numbers don't.
    private static string Num(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
    private static string Id(int value) => value.ToString(CultureInfo.InvariantCulture);
}

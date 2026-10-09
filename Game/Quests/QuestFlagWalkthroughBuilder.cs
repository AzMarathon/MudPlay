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
//   price with no coin letter — "Repeated price directives add up".
//   giveitem — "Chests and chest loot tables"; teleport <room> <map> — "CMD-driven room
//     teleports split the party"; random <table> — "Chests and chest loot tables".
// race, addexp and learnspell are read the way QuestCrawler and KnownSpellCatalog already read
// them. cast and summon stay verbatim with the name of the record they point at.
//
// A line runs left to right and stops at the first condition that fails ("Room-command
// refusals"), so the reading is in step order: what a line asks before it changes the flag is
// what the change needs, a condition written after it is only checked afterwards, and a check
// on an ability the line has itself just changed is judged by the value the line left there —
// when the line's own earlier steps pin that value — or else quoted without a verdict.
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

        // Random tables whose lines change the flag: a line that draws from one is part of
        // the flag's own steps even when it leaves the flag alone itself.
        HashSet<int> changingTables = lines
            .Where(l => l.Callers.Count > 0 && l.Script.Steps.Any(s => IsChangeOf(s, flag)))
            .Select(l => l.Textblock)
            .ToHashSet();

        // Lines that say the same thing fold into one entry, keeping the first one's place. A
        // line drawn from a random table is read once behind each line that draws it, since
        // that line's conditions stand in front of it.
        List<List<Described>> groups = new();
        Dictionary<string, List<Described>> byKey = new(StringComparer.Ordinal);
        foreach (QuestFlagLine line in lines)
        {
            IEnumerable<QuestFlagLine?> callers = line.Callers.Count > 0
                ? line.Callers
                : Enumerable.Repeat<QuestFlagLine?>(null, 1);
            foreach (QuestFlagLine? caller in callers)
            {
                Described d = Describe(line, caller, flag, index, names, changingTables);
                if (!byKey.TryGetValue(d.Key, out List<Described>? group))
                {
                    byKey[d.Key] = group = new List<Described>();
                    groups.Add(group);
                }
                group.Add(d);
            }
        }

        List<List<Described>> ordered = groups
            .OrderBy(g => g[0].Rank)
            .ThenBy(g => g[0].HighKey)
            .ThenBy(g => g[0].Leaves)
            .ThenBy(g => g[0].Line.Textblock)
            .ThenBy(g => g[0].Line.Order)
            .ToList();

        // A line that only asks for the flag to be absent and leaves it alone is usually part
        // of something else (the alignment flags each bar the other two paths' whole quests),
        // so those are listed apart rather than ahead of the flag's steps — unless they are
        // all there is.
        List<QuestFlagStepEntry> steps = new(), withoutFlag = new();
        foreach (List<Described> group in ordered)
            (group[0].OnlyWithout ? withoutFlag : steps).Add(ToEntry(group, names, monsterRooms));
        if (steps.Count == 0)
        {
            steps = withoutFlag;
            withoutFlag = new();
        }

        List<int> otherFlags = steps.Concat(withoutFlag)
            .SelectMany(s => s.OtherFlags).Distinct().OrderBy(f => f).ToList();

        List<CrawledQuest> quests = QuestCrawler.Crawl(cache, null).Where(q => q.Flag == flag).ToList();
        return new QuestFlagWalkthrough(
            flag, flagName, quests, CompleteText(quests), RestrictionText(quests, names),
            steps, withoutFlag, otherFlags);
    }

    // ----- One line -----

    // What a line asks of an ability before it changes it.
    private sealed class FlagCondition
    {
        public bool Without;
        public int? Low;
        public int? High;

        public bool Any => Without || Low is not null || High is not null;

        public void Apply(string verb, int value)
        {
            if (verb == "failability") Without = true;
            else if (verb == "checkability") Low = Low is int low ? Math.Max(low, value) : value;
            else High = High is int high ? Math.Min(high, value) : value;
        }
    }

    // What the line's own steps establish about an ability's value once the line has changed it.
    private enum Certainty { Unknown, Absent, Exact }

    // An ability as the line goes along: what it asked of it, and what it then left there.
    private sealed class AbilityTrack
    {
        public readonly FlagCondition Asked = new();
        public bool Changed;
        public Certainty State;
        public int Value;
    }

    // The conditions of one part of a line: those the change needs, or those checked afterwards.
    private sealed class Conditions
    {
        public readonly List<(int Ability, FlagCondition Condition)> Flags = new();
        public readonly List<int> Held = new(), MustNotHold = new(), InRoom = new();
        public readonly List<(int Item, int Count)> Taken = new();
        public readonly List<(int Amount, int Count)> Prices = new();
        public readonly List<string> Monsters = new(), Alignment = new();

        public FlagCondition FlagFor(int ability)
        {
            foreach ((int a, FlagCondition c) in Flags)
                if (a == ability) return c;
            FlagCondition added = new();
            Flags.Add((ability, added));
            return added;
        }

        public List<string> ToLines(QuestScriptNames names)
        {
            List<string> lines = new();
            foreach ((int ability, FlagCondition condition) in Flags)
                lines.Add(OtherConditionText(condition, AbilityLabel(ability)));
            foreach (int item in Held)
                if (!Taken.Exists(t => t.Item == item)) lines.Add($"Have {ItemLabel(names, item)}");
            foreach ((int item, int count) in Taken)
                lines.Add($"Taken from you: {Times(count)}{ItemLabel(names, item)}");
            foreach (int item in MustNotHold)
                lines.Add($"Must not have {ItemLabel(names, item)}");
            foreach (int item in InRoom)
                lines.Add($"{ItemLabel(names, item)} must be in the room");
            foreach ((int amount, int count) in Prices)
                lines.Add(count > 1
                    ? $"Costs {Num((long)amount * count)} copper ({Num(count)} × {Num(amount)})"
                    : $"Costs {Num(amount)} copper");
            lines.AddRange(Monsters);
            if (Alignment.Count > 0)
                lines.Add("Alignment, as the script writes it: " + string.Join(", ", Alignment));
            return lines;
        }
    }

    private sealed class Described
    {
        public required QuestFlagLine Line { get; init; }
        public QuestFlagLine? Caller { get; init; }
        public required string Key { get; init; }
        public required string Heading { get; init; }
        public int Rank { get; init; }
        public int HighKey { get; init; }
        public int Leaves { get; init; }
        public bool OnlyWithout { get; init; }
        public string? LevelNeed { get; init; }
        public required List<int> Classes { get; init; }
        public string? RaceNeed { get; init; }
        public required List<string> Needs { get; init; }
        public required List<string> Gives { get; init; }
        public required List<string> After { get; init; }
        public required List<string> Also { get; init; }
        public string LaterLabel { get; init; } = string.Empty;
        public required List<string> Later { get; init; }
        public required List<int> OtherFlags { get; init; }
    }

    // caller is the line that draws this line's textblock at random, when there is one: its
    // steps up to that draw run first, so they are read in front of the line's own.
    private static Described Describe(
        QuestFlagLine line, QuestFlagLine? caller, int flag, QuestFlagIndex index,
        QuestScriptNames names, HashSet<int> changingTables)
    {
        List<QuestScriptStep> steps = new();
        if (caller is not null)
        {
            foreach (QuestScriptStep step in caller.Script.Steps)
            {
                if (step.Verb == "random" && step.Int(0) == line.Textblock) break;
                steps.Add(step);
            }
        }
        steps.AddRange(line.Script.Steps);
        bool hasSubjectChange = steps.Exists(s => IsChangeOf(s, flag));

        List<(int Ability, AbilityTrack Track)> tracks = new();
        FlagCondition subject = new(), lateSubject = new();
        Conditions needs = new(), after = new();
        List<string> effects = new(), gives = new(), also = new(), later = new();
        List<(int Item, int Count)> given = new();
        List<int> otherFlags = new();
        int? setTo = null, drawsTable = null;
        int added = 0;
        bool subjectChanged = false, gave = false, inTail = false;
        string? stoppedAt = null, unsettledAt = null;
        string laterLabel = string.Empty;

        foreach (QuestScriptStep step in steps)
        {
            if (inTail)
            {
                later.Add(step.Raw);
                continue;
            }

            int? a = step.Int(0), b = step.Int(1);
            // Once the line has changed the flag, or on a line that leaves the flag alone once
            // it has given something, a condition no longer stands in front of that.
            bool late = hasSubjectChange ? subjectChanged : gave;
            Conditions bucket = late ? after : needs;
            switch (step.Verb)
            {
                // failability <ability> [message] is the settled form; one written with more
                // numbers falls through and is quoted.
                case "failability" when a is > 0 && step.Args.Count <= 2:
                case "checkability" or "testability" when a is > 0 && b is not null:
                {
                    int ability0 = a!.Value;
                    AbilityTrack track = TrackFor(tracks, ability0);
                    if (ability0 != flag) AddOnce(otherFlags, ability0);
                    if (!track.Changed)
                    {
                        track.Asked.Apply(step.Verb, b ?? 0);
                        FlagCondition shown = ability0 == flag
                            ? (late ? lateSubject : subject)
                            : bucket.FlagFor(ability0);
                        shown.Apply(step.Verb, b ?? 0);
                        break;
                    }

                    // The line has already changed this ability, so the check reads what the
                    // line left there.
                    string label = ability0 == flag ? "the flag" : AbilityLabel(ability0);
                    string left = track.State == Certainty.Absent ? $"cleared {label}" : $"left {label} at {Num(track.Value)}";
                    bool? passes = Passes(track, step.Verb, b ?? 0);
                    if (passes == true)
                    {
                        AddRaw(also, $"{step.Raw}  [passes: this line has just {left}]");
                    }
                    else if (passes == false)
                    {
                        stoppedAt = step.Raw;
                        laterLabel = $"Not reached — the line stops at `{step.Raw}`, having just {left}";
                        inTail = true;
                    }
                    else
                    {
                        unsettledAt = step.Raw;
                        laterLabel = $"After `{step.Raw}` — a check on {label} as this line has just changed it; "
                            + "whether it passes is not worked out here";
                        inTail = true;
                    }
                    break;
                }

                case "giveability" or "addability" when a is > 0 && b is not null:
                case "removeability" when a is > 0:
                {
                    int ability0 = a!.Value;
                    int amount = b ?? 0;
                    bool onSubject = ability0 == flag;
                    bool questFlag = onSubject || step.Verb != "addability" || index.IsQuestFlag(ability0);
                    Change(TrackFor(tracks, ability0), step.Verb, amount);
                    gave = true;
                    if (onSubject)
                    {
                        subjectChanged = true;
                        if (step.Verb == "giveability") { effects.Add($"sets {Num(amount)}"); setTo = amount; added = 0; }
                        else if (step.Verb == "addability") { effects.Add(amount >= 0 ? $"adds {Num(amount)}" : $"takes off {Num(-amount)}"); added += amount; }
                        else { effects.Add("clears the flag"); setTo = 0; added = 0; }
                    }
                    else if (!questFlag)
                    {
                        // An addability to something no script grants with giveability is a
                        // stat reward, the rule the quest crawl uses.
                        gives.Add($"{QuestScriptNames.Ability(ability0)} {amount.ToString("+#;-#;0", CultureInfo.InvariantCulture)}");
                    }
                    else
                    {
                        AddOnce(otherFlags, ability0);
                        gives.Add(step.Verb switch
                        {
                            "giveability" => $"Sets {AbilityLabel(ability0)} to {Num(amount)}",
                            "addability"  => $"Adds {Num(amount)} to {AbilityLabel(ability0)}",
                            _             => $"Clears {AbilityLabel(ability0)}",
                        });
                    }
                    break;
                }

                // The line's level, class and race come off the parsed line as a whole.
                case "minlevel" or "maxlevel" or "class" or "race" when a is > 0:
                    break;

                case "checkitem" when a is int item && item > 0:
                    AddOnce(bucket.Held, item);
                    break;
                case "takeitem" when a is int item && item > 0:
                    Count(bucket.Taken, item);
                    break;
                case "failitem" when a is int item && item > 0:
                    AddOnce(bucket.MustNotHold, item);
                    break;
                case "roomitem" when a is int item && item > 0:
                    AddOnce(bucket.InRoom, item);
                    break;
                // The amount is copper when the step names no coin; one that carries anything
                // but an amount and a message number is quoted.
                case "price" when a is int amount && amount > 0 && step.Args.Count <= 2
                                  && (step.Args.Count == 1 || b is not null):
                    Count(bucket.Prices, amount);
                    break;
                case "nomonsters":
                    AddOnce(bucket.Monsters, "No monster in the room (an NPC counts as one)");
                    break;
                case "monsters":
                    AddOnce(bucket.Monsters, "A monster in the room");
                    break;
                case "needmonster" when a is int monster && monster > 0:
                    AddOnce(bucket.Monsters, $"{names.Monster(monster)} (monster {Id(monster)}) in the room");
                    break;
                // The threshold's meaning isn't settled, so the step is quoted; its trailing
                // number is the refusal message and is left off.
                case "goodaligned" or "evilaligned" when step.Args.Count > 0:
                    AddOnce(bucket.Alignment, $"{step.Verb} {step.Args[0]}");
                    break;

                case "giveitem" when a is int item && item > 0:
                    Count(given, item);
                    gave = true;
                    break;
                case "addexp" when a is int exp && exp > 0:
                    gives.Add($"{Num(exp)} experience");
                    gave = true;
                    break;
                case "learnspell" when a is int spell && spell > 0:
                    gives.Add($"Teaches the spell {names.Spell(spell)} (spell {Id(spell)})");
                    gave = true;
                    break;
                case "teleport" when a is int room && room > 0 && b is int map && map > 0:
                    gives.Add($"Teleports you to {names.Room(map, room)} ({Id(map)}/{Id(room)})");
                    gave = true;
                    break;

                case "cast" when a is int spell && spell > 0:
                    AddRaw(also, $"{step.Raw}  [spell: {names.Spell(spell)}]");
                    break;
                case "summon" when a is int monster && monster > 0:
                    AddRaw(also, $"{step.Raw}  [monster: {names.Monster(monster)}]");
                    break;
                case "random" when a is int table && changingTables.Contains(table):
                    drawsTable ??= table;
                    AddRaw(also, step.Raw);
                    break;
                default:
                    AddRaw(also, step.Raw);
                    break;
            }
        }

        // Items first: they are what a player is usually after.
        gives.InsertRange(0, given.Select(g => $"{Times(g.Count)}{ItemLabel(names, g.Item)}"));

        // A line that never changes the flag and only checks it after its gives is still
        // described by that check, marked as coming late.
        bool lateCheck = !subject.Any && lateSubject.Any;
        FlagCondition shownSubject = lateCheck ? lateSubject : subject;
        int leaves = (setTo ?? shownSubject.Low ?? shownSubject.High ?? 0) + added;
        // A line with no check on the flag is placed by the value it leaves, just ahead of the
        // steps that need that value; an "at most N" line with no floor sits at N.
        int rank = shownSubject.Low is int low ? 2 * low
            : shownSubject.Without ? 0
            : shownSubject.High is int high ? 2 * high
            : effects.Count > 0 && leaves > 0 ? (2 * leaves) - 1
            : 0;

        List<string> afterLines = after.ToLines(names);
        if (lateSubject.Any)
        {
            string text = OtherConditionText(lateSubject, "this flag");
            afterLines.Insert(0, char.ToUpperInvariant(text[0]) + text[1..]);
        }

        // A change to the flag written behind that check is not one the heading can promise.
        string? laterChange = later.Select(QuestScriptStep.Parse)
            .Where(s => IsChangeOf(s, flag)).Select(s => s.Raw).FirstOrDefault();
        QuestScriptLine own = line.Script;
        int minLevel = Math.Max(own.MinLevel, caller?.Script.MinLevel ?? 0);
        int maxLevel = LowestSet(own.MaxLevel, caller?.Script.MaxLevel ?? 0);
        List<int> races = (caller?.Script.Races ?? Array.Empty<int>()).Concat(own.Races).Distinct().ToList();

        return new Described
        {
            Line = line,
            Caller = caller,
            Key = KeyOf(line, caller, steps),
            Heading = HeadingText(shownSubject, lateCheck, effects, drawsTable, stoppedAt, unsettledAt, laterChange),
            Rank = rank,
            HighKey = shownSubject.High ?? (shownSubject.Without && shownSubject.Low is null ? 0 : int.MaxValue),
            Leaves = leaves,
            OnlyWithout = shownSubject.Without && shownSubject.Low is null && shownSubject.High is null
                          && effects.Count == 0 && drawsTable is null && laterChange is null,
            LevelNeed = LevelNeed(minLevel, maxLevel),
            Classes = (caller?.Script.Classes ?? Array.Empty<int>()).Concat(own.Classes).Distinct().ToList(),
            RaceNeed = races.Count > 0 ? "Race: " + string.Join(", ", races.Select(names.Race)) : null,
            Needs = needs.ToLines(names),
            Gives = gives,
            After = afterLines,
            Also = also,
            LaterLabel = laterLabel,
            Later = later,
            OtherFlags = otherFlags,
        };
    }

    private static bool IsChangeOf(QuestScriptStep step, int flag)
        => step.Int(0) == flag
           && (step.Verb == "removeability"
               || ((step.Verb == "giveability" || step.Verb == "addability") && step.Int(1) is not null));

    // What the line knows of the ability's value going into its own change: nothing unless
    // its earlier steps pinned it, by asking for it to be absent or to be one exact value.
    private static void Change(AbilityTrack track, string verb, int amount)
    {
        if (!track.Changed)
        {
            FlagCondition asked = track.Asked;
            if (asked.Without && asked.Low is null && asked.High is null) track.State = Certainty.Absent;
            else if (asked.Low is int low && asked.High == low) { track.State = Certainty.Exact; track.Value = low; }
            else track.State = Certainty.Unknown;
            track.Changed = true;
        }

        switch (verb)
        {
            case "removeability":
                track.State = Certainty.Absent;
                break;
            case "addability" when track.State == Certainty.Absent:
                track.State = Certainty.Exact;
                track.Value = amount;
                break;
            case "addability" when track.State == Certainty.Exact:
                track.Value += amount;
                break;
            case "giveability" when track.State == Certainty.Absent:
                track.State = Certainty.Exact;
                track.Value = amount;
                break;
            // Stock keeps the higher value when the character already holds one, so setting
            // a lower value than the one pinned leaves it unsettled across the realms.
            case "giveability" when track.State == Certainty.Exact:
                if (amount >= track.Value) track.Value = amount;
                else track.State = Certainty.Unknown;
                break;
        }
    }

    // Whether a check on an ability the line has just changed passes: true / false when the
    // value is pinned, null when it isn't.
    private static bool? Passes(AbilityTrack track, string verb, int value) => (track.State, verb) switch
    {
        (Certainty.Exact, "checkability") => track.Value >= value,
        (Certainty.Exact, "testability")  => track.Value <= value,
        (Certainty.Exact, _)              => false,           // failability: the character has it
        (Certainty.Absent, "failability") => true,
        // testability fails for a character without the ability unless the value is negative.
        (Certainty.Absent, "testability") => value < 0,
        _                                 => null,
    };

    // Two readings fold together when they are the same script reached the same way: apart
    // from the class step (the entry lists the classes) and the wording of a typed command
    // (the entry lists every wording). Any other difference keeps them apart.
    private static string KeyOf(QuestFlagLine line, QuestFlagLine? caller, List<QuestScriptStep> steps)
    {
        System.Text.StringBuilder sb = new();
        foreach (QuestScriptStep step in steps)
        {
            if (step.Verb == "class" && step.Int(0) is > 0) continue;
            sb.Append(step.Raw).Append(':');
        }
        sb.Append('|').Append(line.Script.RollBand?.ToString(CultureInfo.InvariantCulture)).Append('|');
        foreach (QuestTrigger t in line.Triggers.Concat(caller?.Triggers ?? Array.Empty<QuestTrigger>()))
        {
            sb.Append((int)t.Kind).Append('/').Append(t.Monster).Append('/').Append(t.Map).Append('/').Append(t.Room);
            if (t.Kind != QuestTriggerKind.RoomCommand) sb.Append('/').Append(t.Command);
            sb.Append(';');
        }
        if (caller is not null) sb.Append("|from ").Append(caller.Textblock);
        // With no way in established, only lines of one textblock are the same script.
        if (line.Triggers.Count == 0)
            sb.Append('|').Append(line.Textblock).Append('/').Append(line.Script.Command);
        return sb.ToString();
    }

    private static string HeadingText(
        FlagCondition subject, bool lateCheck, List<string> effects, int? drawsTable,
        string? stoppedAt, string? unsettledAt, string? laterChange)
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

        string effect = effects.Count > 0 ? string.Join(", then ", effects)
            : drawsTable is int table ? $"no change on this line, which draws from textblock #{Id(table)}"
            : "no change";
        if (stoppedAt is not null)
        {
            effect += $", then stops at `{stoppedAt}`";
            if (laterChange is not null) effect += $" before its `{laterChange}`";
        }
        else if (unsettledAt is not null && laterChange is not null)
        {
            effect += $", then checks `{unsettledAt}` before its `{laterChange}`";
        }

        if (parts.Count == 0) return $"No check on the flag → {effect}";
        string condition = string.Join(" and ", parts);
        if (lateCheck) condition += ", checked after the line's gives";
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

    private static string? LevelNeed(int minLevel, int maxLevel)
    {
        if (minLevel > 0 && maxLevel > 0) return $"Level {Num(minLevel)} to {Num(maxLevel)}";
        if (minLevel > 0) return $"Level {Num(minLevel)} or higher";
        return maxLevel > 0 ? $"Level {Num(maxLevel)} or lower" : null;
    }

    private static int LowestSet(int a, int b) => a == 0 ? b : b == 0 ? a : Math.Min(a, b);

    // ----- One entry -----

    private static QuestFlagStepEntry ToEntry(
        List<Described> group, QuestScriptNames names,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms)
    {
        Described first = group[0];

        List<string> needs = new();
        if (first.LevelNeed is { } level) needs.Add(level);
        List<int> classes = group.SelectMany(d => d.Classes).Distinct().ToList();
        if (classes.Count > 0) needs.Add("Class: " + string.Join(", ", classes.Select(names.Class)));
        if (first.RaceNeed is { } race) needs.Add(race);
        needs.AddRange(first.Needs);

        // Lines of one textblock that differ only in the typed command are one script; the
        // first is quoted and the rest counted, since Do already lists every wording. A line
        // drawn at random is quoted behind the line that draws it.
        List<string> script = new();
        List<(string Body, int At, int More)> quoted = new();
        foreach (Described d in group)
        {
            if (d.Caller is not { } caller) continue;
            string callerText = $"Textblock #{Id(caller.Textblock)}: {caller.Script.Raw}";
            if (!script.Contains(callerText)) script.Add(callerText);
        }
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
            string text = $"Textblock #{Id(d.Line.Textblock)}: {line.Raw}";
            if (script.Contains(text)) continue;
            quoted.Add((body, script.Count, 0));
            script.Add(text);
        }
        foreach ((_, int at, int more) in quoted.OrderByDescending(q => q.At))
            if (more > 0)
                script.Insert(at + 1, $"    (and {Num(more)} more {(more == 1 ? "line" : "lines")} with the other {(more == 1 ? "wording" : "wordings")} of the command, otherwise the same)");

        return new QuestFlagStepEntry(
            first.Heading, DoLines(group, names, monsterRooms), needs, first.Gives, first.After, first.Also,
            first.LaterLabel, first.Later, script, first.OtherFlags);
    }

    private static List<string> DoLines(
        List<Described> group, QuestScriptNames names,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms)
    {
        List<string> lines = new();
        List<QuestTrigger> triggers = group
            .SelectMany(d => d.Line.Triggers.Concat(d.Caller?.Triggers ?? Array.Empty<QuestTrigger>()))
            .ToList();

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

        // Shared dialogue hangs off several NPCs; each gets its own line.
        foreach (QuestTrigger t in triggers.Where(t => t.Kind == QuestTriggerKind.Ask))
        {
            string line = $"Type \"{t.Command}\"";
            if (t.OtherKeywords.Count > 0) line += $" (the same reply comes from: {string.Join(", ", t.OtherKeywords)})";
            line += $" — {names.Monster(t.Monster)}{Placement(t.Monster, " is in ", names, monsterRooms)}";
            AddOnce(lines, line);
        }

        List<int> seenKill = new();
        foreach (QuestTrigger t in triggers.Where(t => t.Kind == QuestTriggerKind.Kill))
        {
            if (seenKill.Contains(t.Monster)) continue;
            seenKill.Add(t.Monster);
            lines.Add($"Kill {names.Monster(t.Monster)} (monster {Id(t.Monster)}){Placement(t.Monster, ", found in ", names, monsterRooms)} — its death runs this script");
        }

        // A keyword the NPC shows by itself is where the line comes from, not a thing to type.
        foreach (QuestTrigger t in triggers.Where(t => t.Kind == QuestTriggerKind.AutoShown))
        {
            string keywords = string.Join(" / ", new[] { t.Command }.Concat(t.OtherKeywords).Select(k => $"\"{k}\""));
            AddOnce(lines, $"Reached from the {keywords} keyword of {names.Monster(t.Monster)} (monster {Id(t.Monster)}), "
                + $"listed in textblock #{Id(t.Textblock)}{Placement(t.Monster, "; the NPC is in ", names, monsterRooms)} — "
                + "a keyword the NPC shows by itself, not one you ask");
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

        string band = head.Script.RollBand is int top
            ? $"the band up to {Num(top)}" + (head.RollChance is double chance
                ? $" ({chance.ToString("0.#", CultureInfo.InvariantCulture)}% of draws)" : string.Empty)
            : string.Empty;
        if (group[0].Caller is { } caller)
            lines.Add($"Then a random draw: textblock #{Id(caller.Textblock)} draws from textblock #{Id(head.Textblock)} "
                + $"with `random {Id(head.Textblock)}`" + (band.Length > 0 ? $", and this outcome is {band}" : string.Empty));
        else if (band.Length > 0)
            lines.Add($"One outcome of a random draw from textblock #{Id(head.Textblock)}: {band}");
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

    // The crawl's class and race limits, when it records any. It can miss one the steps
    // plainly carry (it only reports a limit every granting chain shares), so nothing is said
    // when it found none; a tiered flag shows each tier's limit unless they all agree.
    private static string RestrictionText(List<CrawledQuest> quests, QuestScriptNames names)
    {
        List<string> perTier = quests.Select(q => RestrictionOf(q, names)).ToList();
        if (perTier.Count == 0 || perTier.TrueForAll(t => t == perTier[0])) return perTier.FirstOrDefault() ?? string.Empty;
        return string.Join(" ", perTier.Select((text, i) =>
            $"Tier {Num(i + 1)}: {(text.Length > 0 ? text : "no limit recorded.")}"));
    }

    private static string RestrictionOf(CrawledQuest quest, QuestScriptNames names)
    {
        List<string> parts = new();
        if (quest.ClassIds is { Count: > 0 } classes)
            parts.Add("Classes: " + string.Join(", ", classes.Select(c =>
                quest.ClassLevels is { } levels && levels.TryGetValue(c, out int lvl)
                    ? $"{names.Class(c)} (level {Num(lvl)})" : names.Class(c))));
        if (quest.RaceIds is { Count: > 0 } races)
            parts.Add("Races: " + string.Join(", ", races.Select(names.Race)));
        return parts.Count == 0 ? string.Empty : string.Join(". ", parts) + ".";
    }

    // ----- Small helpers -----

    private static string AbilityLabel(int ability) => $"{QuestScriptNames.Ability(ability)} ({Id(ability)})";

    private static string ItemLabel(QuestScriptNames names, int item) => $"{names.Item(item)} (item {Id(item)})";

    private static string Times(int count) => count > 1 ? $"{Num(count)} × " : string.Empty;

    private static AbilityTrack TrackFor(List<(int Ability, AbilityTrack Track)> tracks, int ability)
    {
        foreach ((int a, AbilityTrack t) in tracks)
            if (a == ability) return t;
        AbilityTrack added = new();
        tracks.Add((ability, added));
        return added;
    }

    private static void Count(List<(int Key, int Count)> list, int key)
    {
        int at = list.FindIndex(e => e.Key == key);
        if (at < 0) list.Add((key, 1));
        else list[at] = (key, list[at].Count + 1);
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

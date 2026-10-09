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
// refusals"), so the reading keeps the order: conditions and gives come out as alternating
// runs, and a give only depends on the conditions written before it. A check on an ability the
// line has itself just changed is judged by the value the line left there — when the line's own
// steps since pin that value, with nothing in between that could run a spell or other script —
// or else quoted without a verdict.
public static class QuestFlagWalkthroughBuilder
{
    private const int MaxRoomsNamed = 6;
    private const int MaxPlacementsNamed = 3;
    private const int MaxOtherRootsNamed = 4;

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

        // A line drawn from a random table is read once behind each chain of lines that
        // draws it, since their conditions stand in front of it.
        List<(QuestFlagLine Line, List<QuestFlagLine> Chain)> readings = new();
        foreach (QuestFlagLine line in lines)
            foreach (List<QuestFlagLine> chain in ChainsOf(line))
                readings.Add((line, chain));

        // Random tables that lead to a change of the flag: a line that draws from one is part
        // of the flag's own steps even when it leaves the flag alone itself.
        HashSet<int> changingTables = new();
        foreach ((QuestFlagLine line, List<QuestFlagLine> chain) in readings)
        {
            if (chain.Count == 0 || !line.Script.Steps.Any(s => IsChangeOf(s, flag))) continue;
            changingTables.Add(line.Textblock);
            foreach (QuestFlagLine caller in chain.Skip(1)) changingTables.Add(caller.Textblock);
        }

        // Readings that say the same thing fold into one entry, keeping the first one's place.
        List<List<Described>> groups = new();
        Dictionary<string, List<Described>> byKey = new(StringComparer.Ordinal);
        foreach ((QuestFlagLine line, List<QuestFlagLine> chain) in readings)
        {
            Described d = Describe(line, chain, flag, index, names, changingTables);
            if (!byKey.TryGetValue(d.Key, out List<Described>? group))
            {
                byKey[d.Key] = group = new List<Described>();
                groups.Add(group);
            }
            group.Add(d);
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

    // The chains of drawing lines that lead to a line, outermost first; one empty chain for a
    // line nothing draws.
    private static IEnumerable<List<QuestFlagLine>> ChainsOf(QuestFlagLine line)
    {
        if (line.Callers.Count == 0)
        {
            yield return new List<QuestFlagLine>();
            yield break;
        }
        foreach (QuestFlagLine caller in line.Callers)
            foreach (List<QuestFlagLine> outer in ChainsOf(caller))
            {
                outer.Add(caller);
                yield return outer;
            }
    }

    // ----- One line -----

    // What a line asks of an ability.
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

    // An ability as the line goes along: what it has asked of it since anything else could
    // have changed it, and what it then left there.
    private sealed class AbilityTrack
    {
        public FlagCondition Asked = new();
        public bool Changed;
        public Certainty State;
        public int Value;
    }

    // One run of the line: conditions, or things it gives.
    private sealed class Part
    {
        public bool IsCheck;
        // The step that could run a spell or other script just ahead of these conditions.
        public string? After;

        public int MinLevel, MaxLevel;
        public bool HasClass;
        public readonly List<int> Races = new();
        public FlagCondition? Subject;
        public readonly List<(int Ability, FlagCondition Condition)> Flags = new();
        public readonly List<int> Held = new(), MustNotHold = new(), InRoom = new();
        public readonly List<(int Item, int Count)> Taken = new(), Given = new();
        public readonly List<(int Amount, int Count)> Prices = new();
        public readonly List<string> Monsters = new(), Alignment = new(), Gives = new();
        public readonly List<string> SubjectChanges = new();

        public FlagCondition FlagFor(int ability)
        {
            foreach ((int a, FlagCondition c) in Flags)
                if (a == ability) return c;
            FlagCondition added = new();
            Flags.Add((ability, added));
            return added;
        }
    }

    private sealed class Described
    {
        public required QuestFlagLine Line { get; init; }
        public required List<QuestFlagLine> Chain { get; init; }
        public required string Key { get; init; }
        public required string Heading { get; init; }
        public int Rank { get; init; }
        public int HighKey { get; init; }
        public int Leaves { get; init; }
        public bool OnlyWithout { get; init; }
        public required List<Part> Parts { get; init; }
        public required List<int> Classes { get; init; }
        public required List<string> Also { get; init; }
        public string LaterLabel { get; init; } = string.Empty;
        public required List<string> Later { get; init; }
        public required List<int> OtherFlags { get; init; }
    }

    // chain is the lines that draw this line's textblock at random, outermost first: each
    // one's steps up to its draw run first, so they are read in front of the line's own.
    private static Described Describe(
        QuestFlagLine line, List<QuestFlagLine> chain, int flag, QuestFlagIndex index,
        QuestScriptNames names, HashSet<int> changingTables)
    {
        List<QuestScriptStep> steps = new();
        for (int i = 0; i < chain.Count; i++)
        {
            int drawn = (i + 1 < chain.Count ? chain[i + 1] : line).Textblock;
            foreach (QuestScriptStep step in chain[i].Script.Steps)
            {
                if (step.Verb == "random" && step.Int(0) == drawn) break;
                steps.Add(step);
            }
        }
        steps.AddRange(line.Script.Steps);
        bool hasSubjectChange = steps.Exists(s => IsChangeOf(s, flag));

        List<(int Ability, AbilityTrack Track)> tracks = new();
        List<Part> parts = new();
        FlagCondition subject = new(), lateSubject = new();
        List<string> effects = new(), also = new(), later = new();
        List<int> otherFlags = new(), classes = new();
        int? setTo = null, drawsTable = null;
        int added = 0, levelFloor = 0, levelCeiling = 0;
        bool gave = false, inTail = false;
        // The first step that could have run a spell or other script, and the latest one not
        // yet named on a run of conditions.
        string? ranOther = null, ranOtherPending = null;
        string? stoppedAt = null, unsettledAt = null, lateReason = null;
        string laterLabel = string.Empty;

        Part Checks()
        {
            if (parts.Count == 0 || !parts[^1].IsCheck)
            {
                parts.Add(new Part { IsCheck = true, After = ranOtherPending });
                ranOtherPending = null;
            }
            return parts[^1];
        }

        Part Gives()
        {
            if (parts.Count == 0 || parts[^1].IsCheck) parts.Add(new Part());
            gave = true;
            return parts[^1];
        }

        // A spell or another textblock may change any ability, so what the line knew of
        // them — a value it left, or a value it asked for and has not yet built on — is gone.
        void Forget()
        {
            foreach ((_, AbilityTrack track) in tracks)
            {
                if (track.Changed) track.State = Certainty.Unknown;
                else track.Asked = new FlagCondition();
            }
        }

        foreach (QuestScriptStep step in steps)
        {
            if (inTail)
            {
                later.Add(step.Raw);
                continue;
            }

            int? a = step.Int(0), b = step.Int(1);
            switch (step.Verb)
            {
                // failability <ability> [message] is the settled form; one written with more
                // numbers falls through and is quoted.
                case "failability" when a is > 0 && step.Args.Count <= 2:
                case "checkability" or "testability" when a is > 0 && b is not null:
                {
                    int ability = a!.Value;
                    AbilityTrack track = TrackFor(tracks, ability);
                    if (ability != flag) AddOnce(otherFlags, ability);
                    if (!track.Changed)
                    {
                        track.Asked.Apply(step.Verb, b ?? 0);
                        bool leading = parts.Count == 0 || (parts.Count == 1 && parts[0].IsCheck);
                        Part part = Checks();
                        if (ability != flag)
                        {
                            part.FlagFor(ability).Apply(step.Verb, b ?? 0);
                            break;
                        }

                        // A check on the flag is the line's entry condition unless something
                        // ran first that could have changed the flag — or, on a line that
                        // never changes it, the line has already started giving.
                        bool demoted = ranOther is not null || (!hasSubjectChange && gave);
                        if (demoted)
                            lateReason ??= ranOther is not null ? $"checked after `{ranOther}`" : "checked after the line's gives";
                        (demoted ? lateSubject : subject).Apply(step.Verb, b ?? 0);
                        if (demoted || !leading)
                            (part.Subject ??= new FlagCondition()).Apply(step.Verb, b ?? 0);
                        break;
                    }

                    // The line has already changed this ability, so the check reads what the
                    // line left there.
                    string label = ability == flag ? "the flag" : AbilityLabel(ability);
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
                    int ability = a!.Value;
                    int amount = b ?? 0;
                    Change(TrackFor(tracks, ability), step.Verb, amount);
                    Part part = Gives();
                    if (ability == flag)
                    {
                        if (step.Verb == "giveability") { effects.Add($"sets {Num(amount)}"); setTo = amount; added = 0; }
                        else if (step.Verb == "addability") { effects.Add(amount >= 0 ? $"adds {Num(amount)}" : $"takes off {Num(-amount)}"); added += amount; }
                        else { effects.Add("clears the flag"); setTo = 0; added = 0; }
                        part.SubjectChanges.Add($"This flag: {effects[^1]}");
                    }
                    else if (step.Verb == "addability" && !index.IsQuestFlag(ability))
                    {
                        // An addability to something no script grants with giveability is a
                        // stat reward, the rule the quest crawl uses.
                        part.Gives.Add($"{QuestScriptNames.Ability(ability)} {amount.ToString("+#;-#;0", CultureInfo.InvariantCulture)}");
                    }
                    else
                    {
                        AddOnce(otherFlags, ability);
                        part.Gives.Add(step.Verb switch
                        {
                            "giveability" => $"Sets {AbilityLabel(ability)} to {Num(amount)}",
                            "addability"  => $"Adds {Num(amount)} to {AbilityLabel(ability)}",
                            _             => $"Clears {AbilityLabel(ability)}",
                        });
                    }
                    break;
                }

                // Every level gate is enforced, so one no stricter than an earlier one adds
                // nothing and is left to the Script line.
                case "minlevel" when a is int level && level > 0:
                    if (level > levelFloor) Checks().MinLevel = levelFloor = level;
                    break;
                case "maxlevel" when a is int level && level > 0:
                    if (levelCeiling == 0 || level < levelCeiling) Checks().MaxLevel = levelCeiling = level;
                    break;
                case "class" when a is int id && id > 0:
                    Checks().HasClass = true;
                    AddOnce(classes, id);
                    break;
                case "race" when a is int id && id > 0:
                    AddOnce(Checks().Races, id);
                    break;

                case "checkitem" when a is int item && item > 0:
                    AddOnce(Checks().Held, item);
                    break;
                case "takeitem" when a is int item && item > 0:
                    Count(Checks().Taken, item);
                    break;
                case "failitem" when a is int item && item > 0:
                    AddOnce(Checks().MustNotHold, item);
                    break;
                case "roomitem" when a is int item && item > 0:
                    AddOnce(Checks().InRoom, item);
                    break;
                // The amount is copper when the step names no coin; one that carries anything
                // but an amount and a message number is quoted.
                case "price" when a is int amount && amount > 0 && step.Args.Count <= 2
                                  && (step.Args.Count == 1 || b is not null):
                    Count(Checks().Prices, amount);
                    break;
                case "nomonsters":
                    AddOnce(Checks().Monsters, "No monster in the room (an NPC counts as one)");
                    break;
                case "monsters":
                    AddOnce(Checks().Monsters, "A monster in the room");
                    break;
                case "needmonster" when a is int monster && monster > 0:
                    AddOnce(Checks().Monsters, $"{names.Monster(monster)} (monster {Id(monster)}) in the room");
                    break;
                // The threshold's meaning isn't settled, so the step is quoted; its trailing
                // number is the refusal message and is left off.
                case "goodaligned" or "evilaligned" when step.Args.Count > 0:
                    AddOnce(Checks().Alignment, $"{step.Verb} {step.Args[0]}");
                    break;

                case "giveitem" when a is int item && item > 0:
                    Count(Gives().Given, item);
                    break;
                case "addexp" when a is int exp && exp > 0:
                    Gives().Gives.Add($"{Num(exp)} experience");
                    break;
                case "learnspell" when a is int spell && spell > 0:
                    Gives().Gives.Add($"Teaches the spell {names.Spell(spell)} (spell {Id(spell)})");
                    break;
                case "teleport" when a is int room && room > 0 && b is int map && map > 0:
                    Gives().Gives.Add($"Teleports you to {names.Room(map, room)} ({Id(map)}/{Id(room)})");
                    break;

                // A message prints a line and nothing else, so it breaks no run.
                case "message":
                    AddRaw(also, step.Raw);
                    break;

                // cast, random and text hand over to a spell or another textblock: a check on
                // the flag written after one is no longer what the line asks going in.
                case "cast" or "random" or "text":
                    if (step.Verb == "cast" && a is int castSpell && castSpell > 0)
                        AddRaw(also, $"{step.Raw}  [spell: {names.Spell(castSpell)}]");
                    else
                        AddRaw(also, step.Raw);
                    if (step.Verb == "random" && a is int table && changingTables.Contains(table)) drawsTable ??= table;
                    ranOther ??= step.Raw;
                    ranOtherPending = step.Raw;
                    Forget();
                    break;

                case "summon" when a is int monster && monster > 0:
                    AddRaw(also, $"{step.Raw}  [monster: {names.Monster(monster)}]");
                    Forget();
                    break;
                // What a quoted step does isn't settled, so nothing the line knew of an
                // ability's value is carried across it.
                default:
                    AddRaw(also, step.Raw);
                    Forget();
                    break;
            }
        }

        // A line that only checks the flag late is still described by that check, marked as
        // coming late.
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

        // A change to the flag written behind that check is not one the heading can promise.
        string? laterChange = later.Select(QuestScriptStep.Parse)
            .Where(s => IsChangeOf(s, flag)).Select(s => s.Raw).FirstOrDefault();

        return new Described
        {
            Line = line,
            Chain = chain,
            Key = KeyOf(line, chain, steps),
            Heading = HeadingText(shownSubject, lateCheck ? lateReason : null, effects, drawsTable, stoppedAt, unsettledAt, laterChange),
            Rank = rank,
            HighKey = shownSubject.High ?? (shownSubject.Without && shownSubject.Low is null ? 0 : int.MaxValue),
            Leaves = leaves,
            OnlyWithout = shownSubject.Without && shownSubject.Low is null && shownSubject.High is null
                          && effects.Count == 0 && drawsTable is null && laterChange is null,
            Parts = parts,
            Classes = classes,
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
    // its steps since the last spell or other script pinned it, by asking for it to be absent
    // or to be one exact value.
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
    // from which class a class step names (the entry lists the classes) and the wording of a
    // typed command (the entry lists every wording). Any other difference keeps them apart.
    private static string KeyOf(QuestFlagLine line, List<QuestFlagLine> chain, List<QuestScriptStep> steps)
    {
        System.Text.StringBuilder sb = new();
        foreach (QuestScriptStep step in steps)
            sb.Append(step.Verb == "class" && step.Int(0) is > 0 ? "class" : step.Raw).Append(':');
        sb.Append('|').Append(line.Script.RollBand?.ToString(CultureInfo.InvariantCulture)).Append('|');
        foreach (QuestTrigger t in TriggersOf(line, chain))
        {
            sb.Append((int)t.Kind).Append('/').Append(t.Monster).Append('/').Append(t.Map).Append('/').Append(t.Room);
            if (t.Kind != QuestTriggerKind.RoomCommand) sb.Append('/').Append(t.Command);
            sb.Append(';');
        }
        foreach (QuestFlagLine caller in chain) sb.Append("|from ").Append(caller.Textblock);
        // With no way in established, only lines of one textblock are the same script.
        if (line.Triggers.Count == 0)
            sb.Append('|').Append(line.Textblock).Append('/').Append(line.Script.Command);
        return sb.ToString();
    }

    private static IEnumerable<QuestTrigger> TriggersOf(QuestFlagLine line, List<QuestFlagLine> chain)
        => line.Triggers.Concat(chain.SelectMany(c => c.Triggers));

    private static IEnumerable<QuestTrigger> AllTriggers(QuestFlagLine line)
        => line.Triggers.Concat(line.Callers.SelectMany(AllTriggers));

    private static string HeadingText(
        FlagCondition subject, string? lateReason, List<string> effects, int? drawsTable,
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
        if (lateReason is not null) return $"{char.ToUpperInvariant(condition[0])}{condition[1..]}, {lateReason} → {effect}";
        if (subject.Without && parts.Count == 1 && effects.Count > 0) return $"Start — {condition} → {effect}";
        return $"{char.ToUpperInvariant(condition[0])}{condition[1..]} → {effect}";
    }

    private static string ConditionText(FlagCondition c, string label)
    {
        List<string> parts = new();
        if (c.Without) parts.Add($"Without {label}");
        if (c.Low is int low && c.High is int high)
            parts.Add(low == high ? $"{label} exactly {Num(low)}" : $"{label} from {Num(low)} to {Num(high)}");
        else if (c.Low is int atLeast)
            parts.Add($"{label} at least {Num(atLeast)}");
        else if (c.High is int atMost)
            parts.Add($"{label} at most {Num(atMost)}");
        string text = string.Join("; ", parts);
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    // ----- One entry -----

    private static QuestFlagStepEntry ToEntry(
        List<Described> group, QuestScriptNames names,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms)
    {
        Described first = group[0];
        List<int> classes = group.SelectMany(d => d.Classes).Distinct().ToList();

        // Lines of one textblock that differ only in the typed command are one script; the
        // first is quoted and the rest counted, since Do already lists every wording. A line
        // drawn at random is quoted behind the lines that draw it.
        List<string> script = new();
        foreach (Described d in group)
        {
            foreach (QuestFlagLine caller in d.Chain)
            {
                string callerText = $"Textblock #{Id(caller.Textblock)}: {caller.Script.Raw}";
                if (!script.Contains(callerText)) script.Add(callerText);
            }
        }
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
            string text = $"Textblock #{Id(d.Line.Textblock)}: {line.Raw}";
            if (script.Contains(text)) continue;
            quoted.Add((body, script.Count, 0));
            script.Add(text);
        }
        foreach ((_, int at, int more) in quoted.OrderByDescending(q => q.At))
            if (more > 0)
                script.Insert(at + 1, $"    (and {Num(more)} more {(more == 1 ? "line" : "lines")} with the other {(more == 1 ? "wording" : "wordings")} of the command, otherwise the same)");

        return new QuestFlagStepEntry(
            first.Heading, DoLines(group, names, monsterRooms), PartsOf(first.Parts, classes, names), first.Also,
            first.LaterLabel, first.Later, script, first.OtherFlags);
    }

    // The runs in words, labelled by where they stand: what is given ahead of every condition
    // comes whatever the conditions say, and each later run only follows the ones before it.
    private static List<QuestFlagStepPart> PartsOf(List<Part> parts, List<int> classes, QuestScriptNames names)
    {
        // The flag's own change is in the heading; it is repeated among the gives only when
        // the line checks things at more than one point, to show where the change falls.
        bool showChange = parts.Count(p => p.IsCheck) > 1;
        bool anyChecks = parts.Exists(p => p.IsCheck);
        List<QuestFlagStepPart> shown = new();
        int checks = 0, givesAfterChecks = 0;
        bool classShown = false;
        foreach (Part part in parts)
        {
            List<string> lines = new();
            string label;
            if (part.IsCheck)
            {
                if (part.MinLevel > 0 && part.MaxLevel > 0) lines.Add($"Level {Num(part.MinLevel)} to {Num(part.MaxLevel)}");
                else if (part.MinLevel > 0) lines.Add($"Level {Num(part.MinLevel)} or higher");
                else if (part.MaxLevel > 0) lines.Add($"Level {Num(part.MaxLevel)} or lower");
                if (part.HasClass && !classShown && classes.Count > 0)
                {
                    lines.Add("Class: " + string.Join(", ", classes.Select(names.Class)));
                    classShown = true;
                }
                if (part.Races.Count > 0) lines.Add("Race: " + string.Join(", ", part.Races.Select(names.Race)));
                if (part.Subject is { } subject) lines.Add(ConditionText(subject, "this flag"));
                foreach ((int ability, FlagCondition condition) in part.Flags)
                    lines.Add(ConditionText(condition, AbilityLabel(ability)));
                foreach (int item in part.Held)
                    if (!part.Taken.Exists(t => t.Item == item)) lines.Add($"Have {ItemLabel(names, item)}");
                foreach ((int item, int count) in part.Taken)
                    lines.Add($"Taken from you: {Times(count)}{ItemLabel(names, item)}");
                foreach (int item in part.MustNotHold)
                    lines.Add($"Must not have {ItemLabel(names, item)}");
                foreach (int item in part.InRoom)
                    lines.Add($"{ItemLabel(names, item)} must be in the room");
                foreach ((int amount, int count) in part.Prices)
                    lines.Add(count > 1
                        ? $"Costs {Num((long)amount * count)} copper ({Num(count)} × {Num(amount)})"
                        : $"Costs {Num(amount)} copper");
                lines.AddRange(part.Monsters);
                if (part.Alignment.Count > 0)
                    lines.Add("Alignment, as the script writes it: " + string.Join(", ", part.Alignment));
                // A run that holds only the flag's own entry check (it is in the heading) shows
                // nothing, but what follows it still comes after a check.
                label = checks == 0 ? "Needs" : "Then checks";
                if (part.After is not null) label += $" (after `{part.After}`)";
                checks++;
                if (lines.Count == 0) continue;
            }
            else
            {
                // Items first: they are what a player is usually after.
                lines.AddRange(part.Given.Select(g => $"{Times(g.Count)}{ItemLabel(names, g.Item)}"));
                lines.AddRange(part.Gives);
                if (showChange) lines.AddRange(part.SubjectChanges);
                if (lines.Count == 0) continue;

                label = !anyChecks ? "Gives"
                    : checks == 0 ? "First, whatever the checks say"
                    : givesAfterChecks == 0 ? "Gives"
                    : "Then gives";
                if (checks > 0) givesAfterChecks++;
            }
            shown.Add(new QuestFlagStepPart(label, lines));
        }
        return shown;
    }

    private static List<string> DoLines(
        List<Described> group, QuestScriptNames names,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms)
    {
        List<string> lines = new();
        List<QuestTrigger> triggers = group.SelectMany(d => TriggersOf(d.Line, d.Chain)).ToList();

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
            string roots = RootsText(head.Roots);
            if (head.CalledFrom.Length == 0)
                lines.Add("The data records nothing that calls this textblock");
            else if (viaTextblock || roots.Length == 0)
                lines.Add($"Reached from {head.CalledFrom}");
            if (roots.Length > 0)
                lines.Add(viaTextblock ? $"That chain of textblocks starts at {roots}" : $"Reached from {roots}");
        }
        else
        {
            // The line's other starting points, which no way in accounts for — here or behind
            // another line that draws it.
            List<QuestTrigger> every = AllTriggers(head).ToList();
            List<QuestFlagSource> others = head.Roots.Where(r => !every.Exists(t => Accounts(t, r))).ToList();
            if (others.Count > 0) lines.Add($"Also reached from {RootsText(others)}");
        }

        List<QuestFlagLine> chain = group[0].Chain;
        for (int i = 0; i < chain.Count; i++)
        {
            QuestFlagLine drawn = i + 1 < chain.Count ? chain[i + 1] : head;
            string band = BandText(drawn);
            lines.Add($"Then a random draw: textblock #{Id(chain[i].Textblock)} draws from textblock #{Id(drawn.Textblock)} "
                + $"with `random {Id(drawn.Textblock)}`" + (band.Length > 0 ? $", and this outcome is {band}" : string.Empty));
        }
        if (chain.Count == 0 && BandText(head) is { Length: > 0 } own)
            lines.Add($"One outcome of a random draw from textblock #{Id(head.Textblock)}: {own}");
        return lines;
    }

    private static bool Accounts(QuestTrigger trigger, QuestFlagSource root) => trigger.Kind switch
    {
        QuestTriggerKind.RoomCommand => root.Kind == QuestFlagSourceKind.Room && trigger.Map == root.Map && trigger.Room == root.Room,
        QuestTriggerKind.Kill        => root.Kind == QuestFlagSourceKind.Spell && trigger.Spell == root.Number,
        _                            => root.Kind == QuestFlagSourceKind.Monster && trigger.Monster == root.Number,
    };

    private static string RootsText(IReadOnlyList<QuestFlagSource> roots)
    {
        List<string> named = roots.Take(MaxOtherRootsNamed).Select(r => r.Text).ToList();
        if (roots.Count > MaxOtherRootsNamed) named.Add($"+{Num(roots.Count - MaxOtherRootsNamed)} more");
        return string.Join(", ", named);
    }

    private static string BandText(QuestFlagLine line)
        => line.Script.RollBand is int top
            ? $"the band up to {Num(top)}" + (line.RollChance is double chance
                ? $" ({chance.ToString("0.#", CultureInfo.InvariantCulture)}% of draws)" : string.Empty)
            : string.Empty;

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

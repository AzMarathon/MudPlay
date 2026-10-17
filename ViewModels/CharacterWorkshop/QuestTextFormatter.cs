using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Game.Quests;
using MudPlay.Services;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Presentation formatting shared by the Quest Status tab and the Quest editor window —
// turns crawled quest mechanics (CrawledQuest / QuestStep) into the human-readable
// labels both surfaces render. Pure functions over the active GameDataCache; no state.
internal static partial class QuestTextFormatter
{
    // Auto-draft title for a quest when the user hasn't named it: the flag's ability
    // name for a single-part quest; for a multi-part band, the flag's base name (its
    // trailing "Quest" dropped) plus the 1-based band number — e.g. "Good 1" from the
    // GoodQuest flag's first band.
    public static string FallbackTitle(CrawledQuest q)
    {
        string flagName = AbilityNames.FormatId(q.Flag);
        return q.BandOrdinal > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{StripQuestSuffix(flagName)} {q.BandOrdinal}")
            : flagName;
    }

    // Drop a trailing "Quest" so the alignment band names read "Good 1" not
    // "GoodQuest 1"; leave a name that is only "Quest" (nothing else) intact.
    private static string StripQuestSuffix(string name) =>
        name.Length > 5 && name.EndsWith("Quest", StringComparison.Ordinal)
            ? name[..^5]
            : name;

    // Level-gate label ("Level N"), or empty when ungated.
    public static string Level(int level) =>
        level > 0 ? string.Create(CultureInfo.InvariantCulture, $"Level {level}") : string.Empty;

    // Class-resolved permanent stat-bonus summary, or empty when the quest grants none.
    public static string Bonuses(IReadOnlyList<QuestBonus> bonuses) =>
        bonuses.Count == 0 ? string.Empty
            : AbilityNames.SummarizeAbilities(bonuses.Select(b => (b.AbilityId, b.Value)));

    // The quest's reward label: comma-joined keeper-item award names, or — when the
    // quest awards no item or stat but the ability it grants is the prize (Smash,
    // Meditate, SeeHidden) — the flag's ability name. Empty when neither.
    public static string Awards(GameDataCache gameData, CrawledQuest q) =>
        q.AwardItems.Count > 0
            ? string.Join(", ", q.AwardItems.Select(id => ItemName(gameData, id)))
            : q.AwardsAbility ? AbilityNames.FormatId(q.Flag) : string.Empty;

    // The quest's completion experience, thousands-separated with an "exp" suffix
    // ("1,500,000 exp"); empty when the quest (or band) hands none. A distinct reward
    // line from the keeper-item award — this is the raw exp the give-chain grants.
    public static string Experience(CrawledQuest q) =>
        q.ExpAward > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{q.ExpAward:N0} exp")
            : string.Empty;

    // The class / race the crawl found this quest restricted to, as
    // "Classes: Warrior, Cleric  ·  Races: Gaunt One"; empty when the quest is open to
    // all (no restriction surfaced). Informational — the crawl reads guards off the
    // grant chains and can't see gating that lives upstream in the textblock flow, so
    // this is "what the crawl grabbed", not a hard eligibility verdict.
    public static string Requirements(GameDataCache gameData, CrawledQuest q)
    {
        var parts = new List<string>();
        if (q.ClassIds is { Count: > 0 } cls)
            parts.Add("Classes: " + string.Join(", ", cls.Select(id => ClassRequirement(gameData, id, q.ClassLevels))));
        if (q.RaceIds is { Count: > 0 } rcs)
            parts.Add("Races: " + string.Join(", ", rcs.Select(id => RestrictionName(gameData, "Races", id))));
        return string.Join("  ·  ", parts);
    }

    // A restricted class with its own level gate appended ("Priest-20"), or the bare
    // class name when the quest carries no per-class level for it. Lets a multi-class
    // ability quest (Smash, Meditate) show each class's distinct unlock level.
    private static string ClassRequirement(GameDataCache gameData, int id, IReadOnlyDictionary<int, int>? levels)
    {
        string name = RestrictionName(gameData, "Classes", id);
        return levels is not null && levels.TryGetValue(id, out int lvl) && lvl > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{name}-{lvl}")
            : name;
    }

    private static string RestrictionName(GameDataCache gameData, string table, int number) =>
        gameData.FindNameByNumber(table, number)
        ?? string.Create(CultureInfo.InvariantCulture, $"#{number}");

    // Room links named on one kill line for a monster that spawns in many rooms.
    private const int MaxDropperRooms = 3;

    // One followable step drafted in the hand-written guide's own shape:
    //   (map/room) `command` (item note)
    // The Called-From location's rooms become clickable (map/room) links (all of
    // them, for a multi-room list); a player command is backtick-wrapped as the
    // literal to type; a command-less step whose script a monster's death runs is
    // narrated "kill <monster> (<drop>)" and a bare item grant "obtain <item>",
    // matching how the seed guides read. Items the step needs / turns in trail as
    // a parenthetical note. Falls back to a bare "Step N" label when the crawl
    // captured nothing followable (see StepOrNull) — kept for any caller that wants
    // a label for every step; the auto-draft (StepLines) drops those steps instead.
    public static string Step(GameDataCache gameData, QuestStep s,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms = null)
        => StepOrNull(gameData, s, monsterRooms)
           ?? string.Create(CultureInfo.InvariantCulture, $"Step {s.Order}");

    // The step's followable body — room links, command, kill/obtain narration and
    // item notes joined into one line — or null when the crawl captured no action
    // the player can take. Many quest steps are pure flag-advances the crawl can't
    // render: an alignment ladder's automatic value ticks, a story textblock the
    // player never directly triggers (Called-From another textblock/spell, no room,
    // no command, no item). Those carry nothing to do, so the auto-draft omits them
    // rather than listing an opaque "Step 31" the player can't act on.
    //
    // sameStep is every drafted step that lands on the same give-step: when several
    // monsters' deaths run it (two records of one boss), the kill names each and
    // links every room one of them stands in, so an unplaced record listed first
    // doesn't leave the step with nowhere to go.
    public static string? StepOrNull(GameDataCache gameData, QuestStep s,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms = null,
        IEnumerable<QuestStep>? sameStep = null)
    {
        var segments = new List<string>();

        string granted = string.Join(", ", s.GrantedItems.Select(id => ItemName(gameData, id)));

        // A gather step for a dropped item: the rooms its droppers stand in, any room
        // command that brings the dropper, then the kill.
        if (s.Droppers is { Count: > 0 } droppers)
        {
            string where = string.Join(" ", droppers
                .SelectMany(m => monsterRooms is not null && monsterRooms.TryGetValue(m, out IReadOnlyList<RoomKey>? keys)
                    ? keys : Array.Empty<RoomKey>())
                .Distinct()
                .Take(MaxDropperRooms)
                .Select(k => string.Create(CultureInfo.InvariantCulture, $"({k.Map}/{k.Room})")));
            if (where.Length == 0) where = RoomLinks(s.Location);
            if (where.Length > 0) segments.Add(where);
            if (!string.IsNullOrWhiteSpace(s.Command)) segments.Add($"`{s.Command.Trim()}`");
            string names = string.Join(" or ", droppers.Select(m => MonsterName(gameData, m)).Distinct());
            segments.Add(granted.Length > 0 ? $"kill {names} ({granted})" : $"kill {names}");
            return string.Join(" ", segments);
        }

        int monster = 0;
        bool monsterLoc = TryMonsterRef(s.Location, out monster);
        bool hasCommand = !string.IsNullOrWhiteSpace(s.Command);
        List<int> killTargets = hasCommand ? new List<int>() : KillTargets(gameData, s.Location);
        if (killTargets.Count > 0 && sameStep is not null)
            foreach (QuestStep other in sameStep)
                if (string.IsNullOrWhiteSpace(other.Command))
                    foreach (int id in KillTargets(gameData, other.Location))
                        if (!killTargets.Contains(id)) killTargets.Add(id);

        // Room link(s): a kill step links where its target stands, an ask step the NPC
        // it asks, and a room-anchored step its own Called-From room. A step that hangs
        // off an NPC with nothing to ask (a keyword the NPC shows by itself) has no room
        // to send the player to.
        string rooms = killTargets.Count > 0
                ? string.Join(" ", killTargets
                    .SelectMany(m => monsterRooms is not null && monsterRooms.TryGetValue(m, out IReadOnlyList<RoomKey>? keys)
                        ? keys : Array.Empty<RoomKey>())
                    .Distinct()
                    .Select(k => string.Create(CultureInfo.InvariantCulture, $"({k.Map}/{k.Room})")))
            : monsterLoc ? (hasCommand ? MonsterRoomLinks(monster, monsterRooms) : string.Empty)
            : RoomLinks(s.Location);
        if (rooms.Length > 0) segments.Add(rooms);

        if (hasCommand)
        {
            segments.Add($"`{s.Command!.Trim()}`");
            if (granted.Length > 0) segments.Add($"(get {granted})");
        }
        else if (killTargets.Count > 0)
        {
            string name = string.Join(" or ", killTargets.Select(m => MonsterName(gameData, m)).Distinct());
            segments.Add(granted.Length > 0 ? $"kill {name} ({granted})" : $"kill {name}");
        }
        else if (granted.Length > 0)
        {
            segments.Add($"obtain {granted}");
        }

        if (s.TurnInItems.Count > 0)
            segments.Add("(turn in " + string.Join(", ", s.TurnInItems.Select(id => ItemName(gameData, id))) + ")");

        // A required item the step also turns in is already named by the turn-in note
        // (a checkitem + takeitem of the same id), so it isn't repeated as "required".
        var required = s.RequiredItems.Where(id => !s.TurnInItems.Contains(id)).ToList();
        if (required.Count > 0)
            segments.Add("(" + string.Join(", ", required.Select(id => ItemName(gameData, id))) + " required)");

        if (!string.IsNullOrWhiteSpace(s.Note)) segments.Add($"({s.Note})");

        return segments.Count > 0 ? string.Join(" ", segments) : null;
    }

    // The Called-From location's room coordinates as space-joined (map/room) link
    // tokens — every room in a multi-room list, so each renders as its own walk-to
    // link. Empty when the location names no room (a Monster / Spell / Textblock ref).
    private static string RoomLinks(string? location) =>
        string.IsNullOrWhiteSpace(location)
            ? string.Empty
            : string.Join(" ", RoomRef().Matches(location)
                .Select(m => string.Create(CultureInfo.InvariantCulture, $"({m.Groups[1].Value}/{m.Groups[2].Value})")));

    // A kill or ask step's room link(s): every room the quest places the monster in,
    // as space-joined (map/room) tokens, drawn from the pre-built placement map so no
    // per-step room scan happens. Empty when the monster has no resolved placement —
    // the kill step then renders room-less rather than offering a dead link.
    private static string MonsterRoomLinks(int monster,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms) =>
        monsterRooms is not null && monsterRooms.TryGetValue(monster, out IReadOnlyList<RoomKey>? keys)
            ? string.Join(" ", keys.Select(k =>
                string.Create(CultureInfo.InvariantCulture, $"({k.Map}/{k.Room})")))
            : string.Empty;

    // The monsters whose death runs the step's script: its Called-From names the spell
    // at the end of their death-spell chain. Empty for any other step — a script that
    // hangs off a monster itself is that NPC's dialogue, not its death (GAME_MECHANICS
    // "Quest kill steps & monster placement").
    private static List<int> KillTargets(GameDataCache gameData, string? location)
    {
        List<int> targets = new();
        if (string.IsNullOrWhiteSpace(location)) return targets;
        IReadOnlyDictionary<int, IReadOnlyList<int>> deaths = QuestDeathSpells.For(gameData);
        foreach (Match m in SpellRef().Matches(location))
            if (int.TryParse(m.Groups[1].Value, out int spell) && deaths.TryGetValue(spell, out IReadOnlyList<int>? monsters))
                foreach (int id in monsters)
                    if (!targets.Contains(id)) targets.Add(id);
        return targets;
    }

    // The step's monster anchor, if any: a "Monster #N" location. A monster step
    // carrying an "ask <npc> ..." command is an NPC dialogue step re-anchored on that
    // NPC by QuestStepGraph so the guide can link its room.
    private static bool TryMonsterRef(string? location, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(location)) return false;
        Match m = MonsterRef().Match(location);
        return m.Success && int.TryParse(m.Groups[1].Value, out number);
    }

    private static string MonsterName(GameDataCache gameData, int number) =>
        gameData.FindNameByNumber("Monsters", number)
        ?? string.Create(CultureInfo.InvariantCulture, $"monster #{number}");

    // Item display name by id, falling back to #id when the active set has no such row.
    public static string ItemName(GameDataCache gameData, int id) =>
        gameData.FindNameByNumber("Items", id)
        ?? string.Create(CultureInfo.InvariantCulture, $"#{id}");

    // The crawler's auto-draft followable steps for a quest, one markdown line per
    // give-step in order — each a checkbox line "[] {step}" (the seed guides carry no
    // flag/order prefix, so the draft matches them) so the Quest Status tab renders it
    // as a tickable item and the editor pre-fills it verbatim. For a multi-part band
    // only the give-steps inside the band's StepRangeStart..StepRangeEnd span are
    // emitted; a single-part quest (range 0/0) emits every step. Empty when the flag
    // drafts no steps.
    //
    // itemSources / itemDrops let the draft add a line for fetching an item a step needs
    // that no earlier step hands over (QuestItemGathering); without them those items are
    // only named on the step.
    public static IReadOnlyList<string> StepLines(GameDataCache gameData, CrawledQuest q,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms = null,
        ItemSourceIndex? itemSources = null, MonsterDropIndex? itemDrops = null)
        => StepEntries(gameData, q, monsterRooms, itemSources, itemDrops).Select(e => e.Line).ToList();

    // StepLines with each line's give-step Order alongside — the flag value that step
    // sets, so a live flag read can say which checklist lines it proves done. A gather
    // line carries the Order of the step it fetches for: that step done, it was too.
    public static IReadOnlyList<(int Order, string Line)> StepEntries(GameDataCache gameData, CrawledQuest q,
        IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms = null,
        ItemSourceIndex? itemSources = null, MonsterDropIndex? itemDrops = null)
    {
        var lines = new List<(int Order, string Line)>();
        foreach ((int order, string? body, _, _) in DraftSteps(gameData, q, monsterRooms, itemSources, itemDrops))
        {
            // A pure flag-advance (no room, command, kill or item) carries nothing the
            // player can act on, so it's dropped from the draft rather than listed as an
            // opaque "Step N" — the seed guides list actions, not narrative ticks.
            if (body is null) continue;
            lines.Add((order, string.Create(CultureInfo.InvariantCulture, $"[] {body}")));
        }
        return lines;
    }

    // Every step the draft considers for a quest, in checklist order: its body (null
    // when it has nothing followable), whether the draft had a line for it before kill
    // steps were read off the death-spell chain, and whether it is a gather line (the
    // draft had none of those before them either).
    private static IEnumerable<(int Order, string? Body, bool HadLineBefore, bool Gathers)> DraftSteps(
        GameDataCache gameData, CrawledQuest q, IReadOnlyDictionary<int, IReadOnlyList<RoomKey>>? monsterRooms,
        ItemSourceIndex? itemSources, MonsterDropIndex? itemDrops)
    {
        IReadOnlyList<QuestStep> steps = QuestStepGraph.Build(gameData, q.Flag, q.ProgressByValue);
        foreach (QuestStep s in QuestItemGathering.Draft(steps, q.ProgressByValue, itemSources, itemDrops))
        {
            if (q.StepRangeEnd > 0 && (s.Order < q.StepRangeStart || s.Order > q.StepRangeEnd)) continue;
            IEnumerable<QuestStep>? sameStep = q.ProgressByValue || s.Gathers
                ? null : steps.Where(o => !o.Gathers && o.Order == s.Order);
            yield return (s.Order, StepOrNull(gameData, s, monsterRooms, sameStep),
                !s.Gathers && HadLineBeforeKillSteps(s), s.Gathers);
        }
    }

    // Whether the draft listed this step when a kill was only told from a "Monster #N"
    // location: anything with a command, a room or monster to link, or an item changing
    // hands. A step off a death spell with none of those was left out then.
    private static bool HadLineBeforeKillSteps(QuestStep s) =>
        !string.IsNullOrWhiteSpace(s.Command)
        || TryMonsterRef(s.Location, out _)
        || RoomLinks(s.Location).Length > 0
        || s.GrantedItems.Count > 0 || s.TurnInItems.Count > 0 || s.RequiredItems.Count > 0;

    // For step ticks saved before then: where each checkbox of that draft sits in
    // today's — the entry at an old checkbox number is its number now, or -1 when the
    // step no longer has a line. A quest whose draft didn't change maps each number to
    // itself. The gather lines count in today's numbering whatever indexes were at
    // hand, so the caller passes the ones the Quest tab drafts with.
    public static IReadOnlyList<int> CheckboxesSinceKillSteps(GameDataCache gameData, CrawledQuest q,
        ItemSourceIndex? itemSources = null, MonsterDropIndex? itemDrops = null)
        => CheckboxMap(gameData, q, itemSources, itemDrops, e => e.HadLineBefore);

    // The same for ticks saved after kill steps but before the gather lines: every line
    // of that draft is in today's, which only adds gather lines between them.
    public static IReadOnlyList<int> CheckboxesSinceGatherSteps(GameDataCache gameData, CrawledQuest q,
        ItemSourceIndex? itemSources = null, MonsterDropIndex? itemDrops = null)
        => CheckboxMap(gameData, q, itemSources, itemDrops, e => !e.Gathers && e.Body is not null);

    private static List<int> CheckboxMap(GameDataCache gameData, CrawledQuest q,
        ItemSourceIndex? itemSources, MonsterDropIndex? itemDrops,
        Func<(int Order, string? Body, bool HadLineBefore, bool Gathers), bool> wasLine)
    {
        var map = new List<int>();
        int now = 0;
        foreach ((int Order, string? Body, bool HadLineBefore, bool Gathers) e in DraftSteps(gameData, q, null, itemSources, itemDrops))
        {
            if (wasLine(e)) map.Add(e.Body is null ? -1 : now);
            if (e.Body is not null) now++;
        }
        return map;
    }

    // Parse user-or-crawler step markdown into render rows. Each non-blank line is one
    // row: a leading [] / [ ] / [x] marker makes it a tickable checkbox whose label is
    // the text after the marker; a line with no marker is a plain, non-tickable label.
    // Blank lines are skipped.
    public static IEnumerable<(bool Checkable, string Text)> ParseStepLines(string steps)
    {
        if (string.IsNullOrEmpty(steps)) yield break;
        foreach (string raw in steps.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            Match m = CheckboxMarker().Match(line);
            if (m.Success)
                yield return (true, line[m.Length..].TrimStart());
            else
                yield return (false, line);
        }
    }

    // Leading checkbox marker: "[", optional whitespace, optional x/X, optional
    // whitespace, "]". The text after it is the row label.
    [GeneratedRegex(@"^\[\s*[xX]?\s*\]")]
    private static partial Regex CheckboxMarker();

    // Split a step label into render segments, isolating the two clickable token
    // kinds so the view can wrap each in a link: a (map/room) coordinate (e.g.
    // (5/297)) carries the parsed RoomKey for a walk-to link; a quoted command
    // carries the unquoted command text for a send-to-game link. Backticks
    // (`ask jorah transport`) are the guides' and the draft's own mark; a
    // single-quoted 'ask jorah transport' is the older one, still in many saved
    // guides; and a write-up pasted from elsewhere quotes with "…" or “…”, which
    // count only for two words or more that don't end a sentence, so a quoted word
    // or a quoted reply stays prose. Surrounding prose stays as plain segments (both
    // null). A coordinate whose numbers don't fit a positive int, or an empty quote,
    // is left folded into the prose. Returns a single plain segment when the label
    // holds no token, and an empty list for empty input.
    public static IReadOnlyList<(string Text, RoomKey? Room, string? Command)> SplitStepLinks(string text)
    {
        var segments = new List<(string Text, RoomKey? Room, string? Command)>();
        if (string.IsNullOrEmpty(text)) return segments;

        int pos = 0;
        foreach (Match m in StepLink().Matches(text))
        {
            if (m.Groups["map"].Success)
            {
                // Non-positive or over-range coordinate: not a real room — leave the
                // token in the prose run rather than offering a dead link.
                if (!int.TryParse(m.Groups["map"].Value, out int map)
                    || !int.TryParse(m.Groups["room"].Value, out int room)
                    || map <= 0 || room <= 0)
                    continue;

                if (m.Index > pos) segments.Add((text[pos..m.Index], null, null));
                segments.Add((m.Value, new RoomKey(map, room), null));
                pos = m.Index + m.Length;
            }
            else // a quoted command
            {
                string command = m.Groups["cmd"].Value.Trim();
                if (command.Length == 0) continue;   // empty quote — leave in prose

                if (m.Index > pos) segments.Add((text[pos..m.Index], null, null));
                segments.Add((m.Value, null, command));
                pos = m.Index + m.Length;
            }
        }
        if (pos < text.Length) segments.Add((text[pos..], null, null));
        return segments;
    }

    // A clickable quest-step token: a (map/room) coordinate (walk link) or a quoted
    // command (send-to-game link). A backtick run is taken whole, apostrophes and all.
    // The single-quote arm requires the quotes to sit on non-letter boundaries so a
    // prose apostrophe (don't, they're, Jorah's) never opens a bogus quoted run, and
    // an internal apostrophe ends the token. The double-quote arms want a space inside
    // and no trailing . ! ? or comma (see SplitStepLinks).
    [GeneratedRegex(@"\((?<map>\d+)/(?<room>\d+)\)"
        + @"|`(?<cmd>[^`]+)`"
        + @"|(?<![A-Za-z])'(?<cmd>[^']+?)'(?![A-Za-z])"
        + "|\"(?<cmd>[^\"\\s][^\"]*\\s[^\"]*[^\".!?,\\s])\""
        + "|\u201C(?<cmd>[^\u201D\\s][^\u201D]*\\s[^\u201D]*[^\u201D.!?,\\s])\u201D")]
    private static partial Regex StepLink();

    // "Room 9/1259" inside a Called-From string — a location's room reference.
    [GeneratedRegex(@"Room\s+(\d+)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RoomRef();

    // "Spell #604" inside a Called-From string — a script a spell runs.
    [GeneratedRegex(@"Spell\s+#(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SpellRef();

    // "Monster #39" inside a Called-From string — a monster-sourced chain.
    [GeneratedRegex(@"Monster\s+#(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MonsterRef();
}

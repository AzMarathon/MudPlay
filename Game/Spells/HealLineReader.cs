using System.Text.Json;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Spells;

// Recognises the lines that say a heal landed on someone, so a party member's HP can
// be kept between `par` polls (GAME_MECHANICS "Heal lines — who was healed, and by
// how much"). Built from the active game data: every instant heal spell and the
// message record the catalogue keeps for it.
//
// Which lines: the caster's view (our own cast) and the witness view (someone else's
// cast seen in the room). The target's own view is skipped — a heal on us shows up in
// our prompt — except for a party heal, where the line we get when it heals us also
// says every other member got the same.
//
// Who was healed: the {target} capture. A template with no {target} slot but exactly
// one name capture means that capture, whatever the template calls it, because some
// records name the healed player with {spellname} or {source}. A party heal heals the
// caster's whole party. Anything else can't say who was healed and is left out.
//
// Several spells often share one template ("{source} casts {spellname} on {target}!"
// is every targeted spell's room line), so a template with a {spellname} slot counts
// only when the captured name is one of its heal spells.
//
// Performance: this runs on every server line, on the UI thread. Each template is
// filed under the longest literal word it contains, and a line only runs the regexes
// whose word it contains — so almost every line leaves after a few ordinal Contains.
public sealed class HealLineReader
{
    // Spells-table ability codes.
    private const int AbilDamage = 1;
    private const int AbilDrain = 8;
    private const int AbilDamageMr = 17;
    private const int AbilHeal = 18;

    // Targets value of a party-wide spell, and of a spell that only ever affects its
    // user (potions, fungus, food).
    private const int TargetsParty = 13;
    private const int TargetsSelfOnly = 1;

    private enum View { Caster, Witness, PartyTarget }

    private enum Healed { Party, TargetSlot, OnlyName }

    private sealed class Entry(CasterMessageMatcher matcher, Healed healed, int anchor)
    {
        public CasterMessageMatcher Matcher { get; } = matcher;
        public Healed Healed { get; } = healed;
        public int Anchor { get; } = anchor;
        // Heal spells by name when the {spellname} capture picks the spell; null when
        // every spell in All could be the one.
        public Dictionary<string, List<HealSpell>>? ByName { get; set; }
        public List<HealSpell> All { get; } = new();
        public bool AllCaster { get; set; } = true;
        public bool AllPartyTarget { get; set; } = true;
    }

    private readonly string[] _anchors;
    private readonly Entry[] _entries;

    // Distinct heal templates recognised, and the heal spells they came from, for the
    // log and the bug report.
    public int TemplateCount => _entries.Length;
    public int SpellCount { get; }

    public HealLineReader(IReadOnlyList<HealSpell> heals, IEnumerable<MessageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(heals);
        ArgumentNullException.ThrowIfNull(records);

        List<MessageRecord> all = records.ToList();
        Dictionary<int, MessageRecord> byLink = new();
        Dictionary<string, MessageRecord> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (MessageRecord r in all)
        {
            if (r.Links is not null)
                foreach (GameDataLink link in r.Links)
                    if (link.Table.Equals("Spells", StringComparison.OrdinalIgnoreCase))
                        byLink.TryAdd(link.Number, r);
            byName.TryAdd(r.Name.Trim(), r);
        }

        // Each heal's record — by its Spells link first, then by name, the same lookup
        // the rest of the client uses — and the templates it contributes.
        Dictionary<(string Template, bool Party), List<(HealSpell Spell, View View)>> groups = new();
        HashSet<MessageRecord> used = new(ReferenceEqualityComparer.Instance);
        int spells = 0;
        foreach (HealSpell heal in heals)
        {
            MessageRecord? rec = byLink.TryGetValue(heal.Number, out MessageRecord? linked) ? linked
                : byName.TryGetValue(heal.Name.Trim(), out MessageRecord? named) ? named : null;
            if (rec is null) continue;
            used.Add(rec);
            spells++;
            bool party = heal.Targets == TargetsParty;
            AddView(groups, rec.CasterMessage, heal, View.Caster, party);
            foreach (string wording in Wordings(rec.WitnessMessage))
                AddView(groups, wording, heal, View.Witness, party);
            if (party) AddView(groups, rec.TargetMessage, heal, View.PartyTarget, party);
        }
        SpellCount = spells;

        // Templates other records use too. Without a number or a spell name to check,
        // a line in one of those could be the other record's, so it can't be read as
        // a heal of a guessed size.
        HashSet<string> foreign = new(StringComparer.Ordinal);
        foreach (MessageRecord r in all)
        {
            if (used.Contains(r)) continue;
            foreach (string t in AllTemplates(r)) foreign.Add(t);
        }

        List<string> anchors = new();
        List<Entry> entries = new();
        foreach (var ((template, party), views) in groups)
        {
            if (CasterMessageMatcher.LiteralTextLength(template) < MessageRecord.MinRecognitionPatternLength) continue;
            if (CasterMessageMatcher.LongestLiteralWord(template) is not { Length: > 0 } word) continue;
            if (CasterMessageMatcher.TryCreate(template, compiled: false) is not { } matcher) continue;

            Healed? healed = party ? Healed.Party
                : matcher.PinsTarget ? Healed.TargetSlot
                : matcher.NameCaptureCount == 1 ? Healed.OnlyName
                : null;
            if (healed is null) continue;

            // The spell-name capture picks the spell unless it's the one naming the
            // healed player.
            bool bySpellName = matcher.PinsSpell && healed != Healed.OnlyName;
            if (!bySpellName)
            {
                if (!matcher.HasNumber && foreign.Contains(template)) continue;
                // One template read as both a party heal and a single heal, with
                // nothing to tell them apart: who was healed is unknown.
                if (groups.ContainsKey((template, !party))) continue;
            }

            int anchor = anchors.IndexOf(word);
            if (anchor < 0) { anchor = anchors.Count; anchors.Add(word); }
            Entry entry = new(matcher, healed.Value, anchor);
            if (bySpellName) entry.ByName = new(StringComparer.OrdinalIgnoreCase);
            foreach ((HealSpell spell, View view) in views)
            {
                if (!entry.All.Contains(spell)) entry.All.Add(spell);
                if (entry.ByName is { } names)
                {
                    if (!names.TryGetValue(spell.Name.Trim(), out List<HealSpell>? list))
                        names[spell.Name.Trim()] = list = new();
                    if (!list.Contains(spell)) list.Add(spell);
                }
                if (view != View.Caster) entry.AllCaster = false;
                if (view != View.PartyTarget) entry.AllPartyTarget = false;
            }
            entries.Add(entry);
        }

        // The template with the most literal text first, so when two fit one line the
        // more specific reading wins ("You cast X on Y, healing N damage!" before "You
        // cast X on Y!").
        entries.Sort((a, b) => CasterMessageMatcher.LiteralTextLength(b.Matcher.Template)
            .CompareTo(CasterMessageMatcher.LiteralTextLength(a.Matcher.Template)));
        _anchors = anchors.ToArray();
        _entries = entries.ToArray();
    }

    // The instant heals in a Spells table: a heal ability, no duration (a heal over
    // time restores an amount no single line states — `par` corrects those), not a
    // self-only spell (a potion heals only whoever uses it), and no damage or drain
    // ability, which would make who gained the HP ambiguous.
    public static IReadOnlyList<HealSpell> InstantHeals(JsonElement spellsTable)
    {
        List<HealSpell> heals = new();
        if (spellsTable.ValueKind != JsonValueKind.Array) return heals;
        foreach (JsonElement row in spellsTable.EnumerateArray())
        {
            if (!row.TryGetProperty("Name", out JsonElement nameEl)
                || nameEl.GetString() is not { Length: > 0 } name) continue;
            SpellFormulaInput formula = SpellFormulaReader.Read(row);
            if (formula.Dur != 0 || formula.DurInc != 0) continue;
            int targets = row.TryGetProperty("Targets", out JsonElement t) && t.TryGetInt32(out int tv) ? tv : 0;
            if (targets == TargetsSelfOnly) continue;
            bool heals18 = false, harms = false;
            foreach (SpellAbility a in formula.Abilities)
            {
                if (a.Code == AbilHeal) heals18 = true;
                else if (a.Code is AbilDamage or AbilDrain or AbilDamageMr) harms = true;
            }
            if (heals18 && !harms) heals.Add(new HealSpell(formula.Number, name, targets, formula));
        }
        return heals;
    }

    // Read line as a heal. casterLevel(caster, ours) gives the level to average an
    // unnumbered heal at: 0 when unknown, which the formula clamps up to the spell's
    // lowest level so a guess is never over-counted.
    public bool TryRead(string? line, Func<string?, bool, int> casterLevel, out HealSeen heal)
    {
        heal = default;
        if (string.IsNullOrEmpty(line) || _entries.Length == 0) return false;

        // A handful of anchor words across the whole catalogue, so this stays on the stack.
        Span<bool> hit = stackalloc bool[_anchors.Length];
        bool any = false;
        for (int i = 0; i < _anchors.Length; i++)
            any |= hit[i] = line.Contains(_anchors[i], StringComparison.Ordinal);
        if (!any) return false;

        foreach (Entry entry in _entries)
        {
            if (!hit[entry.Anchor]) continue;
            if (!entry.Matcher.TryMatchCaptures(line, out MessageCaptures caps)) continue;
            if (Resolve(entry, caps, casterLevel, out heal)) return true;
        }
        return false;
    }

    private static bool Resolve(Entry entry, MessageCaptures caps, Func<string?, bool, int> casterLevel, out HealSeen heal)
    {
        heal = default;
        List<HealSpell>? candidates = entry.All;
        if (entry.ByName is { } byName && (caps.Spell is null || !byName.TryGetValue(caps.Spell, out candidates)))
            return false;
        if (candidates is null || candidates.Count == 0) return false;

        string? target = entry.Healed switch
        {
            Healed.TargetSlot => caps.Target,
            Healed.OnlyName   => caps.Names[0],
            _                 => null,
        };
        if (entry.Healed != Healed.Party && string.IsNullOrEmpty(target)) return false;

        // When the lone name capture is the healed player, the line names no caster.
        string? caster = entry.Healed == Healed.OnlyName ? null : caps.Source;
        bool ours = entry.AllCaster && (caster is null || caster.Equals("You", StringComparison.OrdinalIgnoreCase));
        if (ours) caster = null;

        if (caps.Number is { } shown)
        {
            if (shown <= 0) return false;
            heal = new HealSeen(target, shown, AmountShown: true, candidates[0].Number, candidates[0].Name,
                caster, ours, entry.AllPartyTarget, CasterLevel: 0);
            return true;
        }

        // No number: the average heal, taking the smallest when the name fits several
        // spells, so the estimate never runs ahead of the member's real HP.
        int level = casterLevel(caster, ours);
        HealSpell? pick = null;
        long best = long.MaxValue;
        foreach (HealSpell spell in candidates)
        {
            long avg = (SpellCalculator.SingleCastMinHeal(spell.Formula, level)
                      + SpellCalculator.SingleCastMaxHeal(spell.Formula, level)) / 2;
            if (avg < best) { best = avg; pick = spell; }
        }
        if (pick is null || best <= 0) return false;
        heal = new HealSeen(target, (int)Math.Min(best, int.MaxValue), AmountShown: false, pick.Number, pick.Name,
            caster, ours, entry.AllPartyTarget, level);
        return true;
    }

    private static void AddView(
        Dictionary<(string, bool), List<(HealSpell, View)>> groups,
        string? template, HealSpell spell, View view, bool party)
    {
        if (MessageRecord.IsBlankOrAbsent(template)) return;
        string text = template!.Trim();
        if (!text.Contains('{')) return;   // a literal line names nobody
        if (!groups.TryGetValue((text, party), out List<(HealSpell, View)>? list))
            groups[(text, party)] = list = new();
        list.Add((spell, view));
    }

    // A witness slot can hold several wordings, one per line.
    private static IEnumerable<string> Wordings(string? slot)
    {
        if (MessageRecord.IsBlankOrAbsent(slot)) yield break;
        foreach (string w in slot!.Split('\n'))
            if (!MessageRecord.IsBlankOrAbsent(w)) yield return w.Trim();
    }

    private static IEnumerable<string> AllTemplates(MessageRecord r)
    {
        if (!MessageRecord.IsBlankOrAbsent(r.CasterMessage)) yield return r.CasterMessage.Trim();
        if (!MessageRecord.IsBlankOrAbsent(r.TargetMessage)) yield return r.TargetMessage.Trim();
        foreach (string w in Wordings(r.WitnessMessage)) yield return w;
    }
}

using System.Text.RegularExpressions;

namespace MudPlay.Game.Combat;

// Reads a "... for N damage!" line and names who dealt the damage and who took it,
// for the per-round damage ledger. Every engine damage line ends the same way, but
// the rest of the wording varies without limit: the engine's own melee forms ("You
// %s %s", "%s %s you", "%s %s %s"), each monster attack's own verb phrase ("The %s
// claws you with its pincers"), and each spell's caster / target / room wordings.
// So rather than parse a grammar, the attributor looks for the people and monsters
// it knows are there — the room roster, the party, and "you" — inside the line.
//
// A side it can't name is null (unknown): a spell whose line names no caster ("Acid
// sears the orc", which is also exactly what the caster sees), an area effect ("An
// earthquake rocks the room"), damage over time ("You are poisoned"). The caller
// decides what else can settle it (our own attack spell, cast this round).
public static partial class DamageLineAttributor
{
    // The ledger's name for the local player.
    public const string Self = "You";

    // A damage line: everything before " for N damage" is the part naming the two
    // sides. Anchored at the end so a quoted chat line carrying the phrase mid-line
    // still has to end on it; chat is filtered by the caller in any case. "points
    // damage" is the Stock evil-punishment / sysop lightning bolt.
    [GeneratedRegex(@"^(?<pre>.+?) for (?<dmg>\d+) (?:points )?damage[!.]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DamageLine();

    // The other damage wordings in the Stock message table (spells, traps, hazards):
    // "You take 12 damage from the flames!" / "You took 5 damage!" — also after a
    // sentence ("The box snaps shut tightly. You take 8 damage!").
    [GeneratedRegex(@"(?:^|[.!] )You (?:take|took) (?<dmg>\d+) (?:\w+ )?damage(?: from .+)?[!.]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex YouTakeDamage();

    // "The orc takes 20 acid damage!" / "Bob takes 8 damage from the fall!"
    [GeneratedRegex(@"^(?<victim>.+?) takes (?<dmg>\d+) (?:\w+ )?damage(?<from> from .+)?[!.]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TakesDamage();

    // "The flask explodes, causing 14 damage!" / "..., causing you 6 damage!" / "...
    // upon the room, doing 30 damage!" / "A counterstrike at you does 9 damage!" /
    // "You fall to the ground with a thud, taking 12 damage!" / "You sing the song of
    // blasting, causing 40 damage to your foes!"
    [GeneratedRegex(@"^(?<pre>.+?),? (?<how>causing|doing|does|taking) (?<you>you )?(?<dmg>\d+) damage(?: to your foes)?[!.]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EffectDamage();

    // Stock's evil punishment / a sysop's punish command — no combatant dealt it (user,
    // 2026-09-29).
    private const string HeavensBolt = "A bolt of lightning from the heavens";

    // "you" as a whole word — never "your" / "yours".
    [GeneratedRegex(@"\byou\b(?!r)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YouWord();

    // "Your blood / life / soul is drained": the only "Your ..." damage lines that
    // hurt us (Stock 1.11p message table).
    [GeneratedRegex(@"^Your \w+ is ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YourPartIsHit();

    // What follows a leading name when it's the victim: "is / are / was / were ..."
    // or "'s <part> is ...".
    [GeneratedRegex(@"^(?:'s \w+)? (?:is|are|was|were) ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PassiveAfterName();

    // "your" before a body part ("bites your ankle", "into your guts") is us; "your
    // foe(s)" is who we're fighting.
    [GeneratedRegex(@"\byour (?!\S*foe)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex YourBodyPart();

    // First words after a leading "You" that make the line damage we TOOK: "You are
    // poisoned for", "You are seared by the flames for", "You took 5 damage".
    private static readonly HashSet<string> PassiveAfterYou =
        new(StringComparer.OrdinalIgnoreCase) { "are", "were", "take", "took", "feel" };

    // Try to read line as a damage line. names are the other combatants that can be
    // named in it (room roster and party, not the local player); the longest match
    // wins, so "large orc" beats "orc". Heals ("healing 12 damage", "is healed of 12
    // damage") match none of the shapes.
    public static bool TryAttribute(string line, IReadOnlyCollection<string> names, out DamageAttribution result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        string text = line.Trim();

        if (DamageLine().Match(text) is { Success: true } m && int.TryParse(m.Groups["dmg"].Value, out int amount))
        {
            string pre = m.Groups["pre"].Value;
            result = pre.StartsWith(HeavensBolt, StringComparison.Ordinal)
                ? new DamageAttribution(null, Self, amount, NoDealer: true)
                : FromPre(pre, amount, names);
            return true;
        }

        if (YouTakeDamage().Match(text) is { Success: true } take && int.TryParse(take.Groups["dmg"].Value, out amount))
        {
            result = new DamageAttribution(null, Self, amount, NoDealer: true);
            return true;
        }

        if (TakesDamage().Match(text) is { Success: true } takes && int.TryParse(takes.Groups["dmg"].Value, out amount))
        {
            // A caster's own view of a spell ("The orc takes 20 acid damage!") names only
            // the victim; a fall is damage nobody dealt.
            string victimText = takes.Groups["victim"].Value;
            int at = StartsWithWord(victimText, "The") ? 4 : 0;
            string? victim = LongestNameAt(victimText, at, names) is { } v && at + v.Length == victimText.Length
                ? v.Name : null;
            bool fall = takes.Groups["from"].Value.Trim().Equals("from the fall", StringComparison.OrdinalIgnoreCase);
            result = new DamageAttribution(null, victim, amount, NoDealer: fall);
            return true;
        }

        if (EffectDamage().Match(text) is { Success: true } effect && int.TryParse(effect.Groups["dmg"].Value, out amount))
        {
            string pre = effect.Groups["pre"].Value;
            if (effect.Groups["how"].Value == "taking")
            {
                // "You fall to the ground with a thud, taking 12 damage!": whoever the
                // line starts with took it, and nobody dealt it.
                int at = StartsWithWord(pre, "The") ? 4 : 0;
                string? victim = StartsWithWord(pre, "You") ? Self
                    : LongestNameAt(pre, at, names) is { } v ? v.Name : null;
                result = new DamageAttribution(null, victim, amount, NoDealer: true);
                return true;
            }
            // "causing you N damage" names us as the victim.
            result = FromPre(effect.Groups["you"].Success ? pre + " you" : pre, amount, names);
            return true;
        }
        return false;
    }

    // Name both sides from the part of a damage line before its amount.
    private static DamageAttribution FromPre(string pre, int amount, IReadOnlyCollection<string> names)
    {
        string? source = null;
        int rest;

        if (StartsWithWord(pre, "You"))
        {
            string next = FirstWord(pre[3..]);
            // "You are poisoned" / "You combust for 12 damage!" (a bare verb with no one
            // after it): a condition or effect on us — nobody dealt it.
            if (PassiveAfterYou.Contains(next) || pre[3..].Trim().IndexOf(' ') < 0)
                return new DamageAttribution(null, Self, amount, NoDealer: true);
            source = Self;
            rest = 3;
        }
        else if (StartsWithWord(pre, "Your"))
        {
            // "Your blood / life / soul is drained" is damage we took. Every other
            // "Your ..." damage line is our weapon or spell hitting something ("Your
            // sword strikes the orc", "Your foes are drenched in acid") — unless it
            // names us as the victim ("Your flesh dissolves, causing you 6 damage!").
            if (YourPartIsHit().IsMatch(pre))
                return new DamageAttribution(null, Self, amount, NoDealer: true);
            string? hit = FindTarget(pre, 4, names);
            return hit == Self
                ? new DamageAttribution(null, Self, amount)
                : new DamageAttribution(Self, hit, amount);
        }
        else
        {
            int start = StartsWithWord(pre, "The") ? 4 : 0;
            if (LongestNameAt(pre, start, names) is { } lead)
            {
                // "Bob is scorched" / "The orc's life is drained": the name is the
                // victim, and the line doesn't say who did it.
                if (PassiveAfterName().IsMatch(pre[(start + lead.Length)..]))
                    return new DamageAttribution(null, lead.Name, amount);
                source = lead.Name;
                rest = start + lead.Length;
            }
            else
            {
                rest = 0;
            }
        }

        // Only the caster sees a room spell's damage aimed at "your foes" / "your
        // enemies" — the room sees "... scorch the room!" with no amount — so the line
        // is ours, and its victims are the whole room rather than us ("A hellish storm
        // of fire and brimstone scorches your foes for 603 damage!", report
        // paradigm-20260929-063213).
        if ((source is null || source == Self)
            && (pre.Contains("your foes", StringComparison.OrdinalIgnoreCase)
                || pre.Contains("your enemies", StringComparison.OrdinalIgnoreCase)))
            return new DamageAttribution(Self, null, amount);

        string? target = FindTarget(pre, rest, names);
        // A named attacker with no victim named is the victim's own view of the hit —
        // the room sees "... at <victim> for N damage!" instead ("The mad wizard throws a
        // flask, which explodes for 5 damage!", report paradigm-20260928-231609; Stock
        // message 2431 has the same pair).
        if (source is not null && source != Self && target is null) target = Self;
        return new DamageAttribution(source, target, amount);
    }

    // The combatant the line names after position from: "you", or the known name
    // ending LAST in the text (the victim follows the verb; a name inside a spell or
    // weapon phrase comes before it), the longest where two end together ("large orc"
    // over "orc").
    private static string? FindTarget(string pre, int from, IReadOnlyCollection<string> names)
    {
        string tail = pre[from..];
        if (YouWord().IsMatch(tail)) return Self;

        string? best = null;
        int bestEnd = -1;
        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            int at = LastWordIndex(tail, name);
            if (at < 0) continue;
            int end = at + name.Length;
            if (end > bestEnd || (end == bestEnd && name.Length > best!.Length))
            {
                best = name;
                bestEnd = end;
            }
        }
        if (best is null && YourBodyPart().IsMatch(tail)) return Self;
        return best;
    }

    private static (string Name, int Length)? LongestNameAt(string pre, int at, IReadOnlyCollection<string> names)
    {
        (string Name, int Length)? best = null;
        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!IsWordAt(pre, at, name)) continue;
            if (best is null || name.Length > best.Value.Length) best = (name, name.Length);
        }
        return best;
    }

    private static int LastWordIndex(string text, string word)
    {
        int at = text.LastIndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            if (IsWordAt(text, at, word)) return at;
            at = at == 0 ? -1 : text.LastIndexOf(word, at - 1, StringComparison.OrdinalIgnoreCase);
        }
        return -1;
    }

    private static bool IsWordAt(string text, int at, string word)
    {
        if (at < 0 || at + word.Length > text.Length) return false;
        if (string.Compare(text, at, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
        bool startOk = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
        int end = at + word.Length;
        bool endOk = end == text.Length || !char.IsLetterOrDigit(text[end]);
        return startOk && endOk;
    }

    private static bool StartsWithWord(string text, string word) => IsWordAt(text, 0, word);

    private static string FirstWord(string text)
    {
        string t = text.TrimStart();
        int space = t.IndexOf(' ');
        return space < 0 ? t : t[..space];
    }
}

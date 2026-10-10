using System.Text.RegularExpressions;
using MudPlay.Game.Spells;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Combat;

// Tells a "… for N damage!" line that is no part of the combat round from one that
// is. A room's own spell is re-cast every second spell round, about 6.05 s
// (GAME_MECHANICS "Room-spell monster summons"), so its damage falls anywhere in the
// 5 s combat round, in wording the hit pattern takes for a hit. Read as a round it
// moved the round clock onto itself and freed the round's between-round cast early
// (report paradigm-20261009-120757).
//
// Two rules, in order:
//   1. The line is the damage text of the spell on the room we stand in. That is
//      exact, and the only way to catch a room spell worded with a subject ("A magma
//      explosion hits you for 12 damage!"), one of which is also a monster's attack.
//   2. Otherwise, the line names nobody who dealt it ("You are seared by the flames
//      for 46 damage!") and is not the victim text of a spell some monster casts as
//      its attack. That keeps a room's heat off the clock when the room isn't known,
//      while a monster's attack spell worded the same way ("You are struck by a dark
//      force for 80 damage!") stays the round it is.
// What rule 2 still leaves out and shouldn't matter: a monster's on-hit effect ("You
// are burned for 5 damage!") lands on the round, but only behind the hit that caused
// it, which marks the round by itself.
//
// Built from the message catalogue and the active set's monsters; whoever holds it
// rebuilds it when either changes.
public sealed partial class OffRoundDamageLines
{
    private readonly Dictionary<int, List<string>> _damageTemplates = new();
    private readonly Dictionary<int, CasterMessageMatcher[]> _damageMatchers = new();
    private readonly CasterMessageMatcher[] _monsterAttackTexts;

    // How many monster attack texts rule 2 has to step around.
    public int MonsterAttackTextCount => _monsterAttackTexts.Length;

    [GeneratedRegex(@"\{[^{}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    // monsterAttackSpells: the spell numbers monsters cast in an attack slot.
    public OffRoundDamageLines(IEnumerable<int> monsterAttackSpells, IEnumerable<MessageRecord> records)
    {
        ArgumentNullException.ThrowIfNull(monsterAttackSpells);
        ArgumentNullException.ThrowIfNull(records);
        HashSet<int> attacks = new(monsterAttackSpells);
        HashSet<string> victimTexts = new(StringComparer.Ordinal);

        foreach (MessageRecord r in records)
        {
            if (r.Links is null) continue;
            bool monsterAttack = false;
            List<int>? spells = null;
            foreach (GameDataLink link in r.Links)
            {
                if (!link.Table.Equals("Spells", StringComparison.OrdinalIgnoreCase)) continue;
                (spells ??= new()).Add(link.Number);
                monsterAttack |= attacks.Contains(link.Number);
            }
            if (spells is null) continue;

            // On a room's spell we are the one it is cast on and, for the record, the
            // one casting it: the line is in either slot.
            foreach (string? slot in new[] { r.CasterMessage, r.TargetMessage })
            {
                if (MessageRecord.IsBlankOrAbsent(slot)) continue;
                string template = slot!.Trim();
                foreach (int spell in spells)
                {
                    if (!_damageTemplates.TryGetValue(spell, out List<string>? list))
                        _damageTemplates[spell] = list = new();
                    if (!list.Contains(template)) list.Add(template);
                }
            }

            // Only a victim text rule 2 would otherwise take needs stepping around:
            // most name the monster and never reach it.
            if (monsterAttack && !MessageRecord.IsBlankOrAbsent(r.TargetMessage)
                && NobodyDealtIt(Placeholder().Replace(r.TargetMessage.Trim(), "1")))
                victimTexts.Add(r.TargetMessage.Trim());
        }

        _monsterAttackTexts = victimTexts
            .Select(t => CasterMessageMatcher.TryCreate(t, compiled: false))
            .Where(m => m is { HasNumber: true })
            .Select(m => m!)
            .ToArray();
    }

    // roomSpell: Rooms.Spell of the room we stand in, 0 when it has none or the room
    // isn't known. castByAMonsterHere: whether a monster now in the room casts that
    // spell as its attack. Then the room's line and the monster's hit are the same
    // text (a high druid's chaos storm in a chaos storm room) and can't be told
    // apart, so rule 1 stands aside: a fight's hit left off the round costs more
    // than a room's cast taken for one, and rule 2 still keeps a line that names a
    // dealer on the round.
    public bool IsOffRound(string? line, int roomSpell, Func<int, bool>? castByAMonsterHere = null)
    {
        if (string.IsNullOrEmpty(line)) return false;
        if (roomSpell > 0 && castByAMonsterHere?.Invoke(roomSpell) != true)
            foreach (CasterMessageMatcher text in DamageTextsOf(roomSpell))
                if (text.TryMatchDamage(line, out _)) return true;
        if (!NobodyDealtIt(line)) return false;
        foreach (CasterMessageMatcher text in _monsterAttackTexts)
            if (text.TryMatchDamage(line, out _)) return false;
        return true;
    }

    // Damage on us that the line gives no dealer for: "You are …", "You feel …",
    // "Your soul is drained …". The whole of rule 2 where no game data is at hand.
    public static bool NobodyDealtIt(string line) =>
        DamageLineAttributor.TryAttribute(line, Array.Empty<string>(), out DamageAttribution sides)
        && sides.NoDealer
        && sides.Target == DamageLineAttributor.Self;

    // Matchers are made on first use: a session stands in a handful of the spells
    // rooms carry, out of the thousand-odd spells with damage text.
    private CasterMessageMatcher[] DamageTextsOf(int spell)
    {
        if (_damageMatchers.TryGetValue(spell, out CasterMessageMatcher[]? built)) return built;
        built = _damageTemplates.TryGetValue(spell, out List<string>? templates)
            ? templates.Select(t => CasterMessageMatcher.TryCreate(t, compiled: false))
                .Where(m => m is { HasNumber: true })
                .Select(m => m!)
                .ToArray()
            : Array.Empty<CasterMessageMatcher>();
        _damageMatchers[spell] = built;
        return built;
    }
}

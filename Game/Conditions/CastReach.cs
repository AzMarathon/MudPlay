using MudPlay.Game.Combat;
using MudPlay.Game.GameData;
using MudPlay.Models.GameData;

namespace MudPlay.Game.Conditions;

// Whether a message record's spell could have been cast where the character
// stands: by the room's own spell, or by a monster the client has in the room.
// ConditionTracker asks it of the records one applied line matched, to leave out
// the ones nobody here casts (KeepWhatCouldBeCastHere).
//
// It answers OutOfReach sparingly, because that is the answer that drops a
// condition: only when every caster of every spell the record is for is a monster
// or a room (SpellCasterIndex), and the client can vouch that none of them is
// here. Anything it can't vouch for is Unknown, and an Unknown record is latched as
// it always was.
public sealed class CastReach
{
    public enum Verdict
    {
        // No spell on the record, a spell the set lacks, a caster that is not a
        // monster or a room, or a roster that can't be trusted.
        Unknown,
        InReach,
        OutOfReach,
    }

    // Where the character stands, as far as the client can vouch for it. RoomSpell
    // is the spell of the room, 0 for none. Monsters are the names of the monsters
    // in it; null when the roster is not the whole of who is here.
    public readonly record struct Here(int RoomSpell, IReadOnlyCollection<string>? Monsters);

    private readonly SpellCasterIndex _casters;
    private readonly Func<Here?> _here;

    // here: null while the character's room is not confirmed, and then nothing is
    // ruled out at all.
    public CastReach(SpellCasterIndex casters, Func<Here?> here)
    {
        ArgumentNullException.ThrowIfNull(casters);
        ArgumentNullException.ThrowIfNull(here);
        _casters = casters;
        _here = here;
    }

    public Here? Now() => _here();

    // by: what casts it here, for the log; null unless InReach.
    public Verdict Of(MessageRecord record, Here here, out string? by)
    {
        by = null;
        if (record.Links is null) return Verdict.Unknown;

        bool anySpell = false;
        bool ruledOut = true;
        foreach (GameDataLink link in record.Links)
        {
            if (!link.Table.Equals("Spells", StringComparison.OrdinalIgnoreCase)) continue;
            anySpell = true;
            if (_casters.Of(link.Number) is not { } casters)
            {
                ruledOut = false;
                continue;
            }
            if (here.RoomSpell > 0 && casters.RoomSpells.Contains(here.RoomSpell))
            {
                by = $"room spell #{here.RoomSpell}";
                return Verdict.InReach;
            }
            if (here.Monsters is { } present)
                foreach (string monster in present)
                    if (casters.Monsters.Contains(monster))
                    {
                        by = $"{monster} here";
                        return Verdict.InReach;
                    }
            // A monster's spell is ruled out only by a roster that can be trusted; a
            // spell rooms alone cast, by the room.
            if (!casters.OnlyMonstersAndRooms || (casters.Monsters.Count > 0 && here.Monsters is null))
                ruledOut = false;
        }
        return anySpell && ruledOut ? Verdict.OutOfReach : Verdict.Unknown;
    }

    // Where the character stands, from what the client holds of it; null while the
    // position is not confirmed, and then nothing is ruled out at all. The room's
    // spell is read off the map, so it stands whenever the position does. The
    // monsters stand only when the room can be seen and its roster is whole.
    public static Here? Standing(
        Map.RoomConfidence confidence, Map.Room? room,
        bool inDarkRoom, bool enteredBlind, bool blinded,
        RoomEntitiesObservation? roster, bool rosterIsWhole, Func<string, bool> isMonsterOfTheSet)
    {
        if (confidence != Map.RoomConfidence.Confirmed || room is null) return null;
        return new Here(room.Spell, MonstersSeen(
            roster, cannotSee: inDarkRoom || enteredBlind || blinded, rosterIsWhole, isMonsterOfTheSet));
    }

    // The names of the monsters the roster lists, or null when it can't be taken for
    // everyone in the room: nothing was read (no roster since the game was entered),
    // the room can't be seen (dark, or the character blind), someone may have come
    // in since it was read whom it doesn't name (RoomEntityClassifier.RosterIsWhole),
    // or it lists a name the set has no monster for, which could be anything.
    public static IReadOnlyCollection<string>? MonstersSeen(
        RoomEntitiesObservation? roster, bool cannotSee, bool rosterIsWhole, Func<string, bool> isMonsterOfTheSet)
    {
        ArgumentNullException.ThrowIfNull(isMonsterOfTheSet);
        if (cannotSee || !rosterIsWhole || roster is not { } seen) return null;

        List<string> names = new();
        foreach (RoomEntity entity in seen.Entities)
        {
            if (entity.Kind == EntityKind.Player) continue;
            if (entity.Kind != EntityKind.Monster || !isMonsterOfTheSet(entity.ResolvedName)) return null;
            names.Add(entity.ResolvedName);
        }
        return names;
    }
}

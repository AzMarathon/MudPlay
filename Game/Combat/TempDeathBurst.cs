namespace MudPlay.Game.Combat;

// Which death gets the temp-death-spell response (TempDeathResponse).
//
// A death that names its monster is answered whenever that monster has such a
// spell. A kill of our own room spell names nobody: it carries the kinds the room
// listed, and several of them can die in one round. A room holds only one monster
// with a temp death spell at a time (GAME_MECHANICS "Silent death spells that
// stall the room"), so however many of the listed monsters the round killed there
// is one stall to answer. Such a kill is answered once for a kind, and not again
// until the room has been read anew and lists that kind still, or has stopped
// listing it and lists it afresh.
public sealed class TempDeathBurst
{
    private readonly Func<int, bool> _hasTempDeathSpell;
    private readonly HashSet<int> _answeredKinds = new();

    public TempDeathBurst(Func<int, bool> hasTempDeathSpell) => _hasTempDeathSpell = hasTempDeathSpell;

    // The Monsters-table Number to answer this death for, out of the Numbers the
    // death could belong to; null when it gets no response.
    public int? KindToAnswer(MonsterDeathEvent death, IEnumerable<int> dyingNumbers)
    {
        int? answer = null;
        foreach (int number in dyingNumbers)
        {
            if (!_hasTempDeathSpell(number)) continue;
            if (death.RoomSpellRoster is null) return number;
            if (_answeredKinds.Add(number)) answer ??= number;
        }
        return answer;
    }

    public void NoteRoomListed(RoomEntitiesObservation room)
    {
        if (_answeredKinds.Count == 0) return;
        if (room.Source == RoomObservationSource.AlsoHere)
        {
            _answeredKinds.Clear();
            return;
        }
        // An emptied room is drawn with no "Also here:" line, so there is no fresh
        // read to go by: a kind the roster has dropped is a new monster when an
        // arrival lists it again.
        _answeredKinds.RemoveWhere(kind =>
        {
            foreach (RoomEntity e in room.Entities)
                if (e.Kind == EntityKind.Monster && e.MonsterNumber == kind) return false;
            return true;
        });
    }
}

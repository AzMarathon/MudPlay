namespace MudPlay.Game.Spells;

// What the cast scheduler asks of, and tells, a loop's lair-entry hold
// (Game.Map.LairEntryDebuffHold): a loop about to step into a lair keeps the round's
// one between-round cast for the combat profile's pre-attack debuff, and holds buffs
// back to do it. The questions only ask: answering one changes nothing in the hold.
public interface ILairEntryBuffHold
{
    // Every buff waits.
    bool BlockingBuffs { get; }

    // The buffs not ticked "Priority buff" wait (true whenever every buff does).
    bool BlockingOrdinaryBuffs { get; }

    // Every buff waits because the loop stands for mana: the buffs that bring mana
    // back are let through all the same.
    bool HoldingForMana { get; }

    // The loop stands at a lair's doorstep: a due buff that can't be paid for is
    // passed over for the next one instead of standing in its way.
    bool SkipsUnpayableBuffs { get; }

    // A priority buff the wait has given up on and holds with the others, named as
    // CastingDirector.BuffsDue names it.
    bool HoldsPriorityBuff(string buff);

    // A decision pass is starting: the hold looks at what has happened since the last
    // (a fight begun or over, its time up) before it is asked anything.
    void Advance();

    void NotePriorityBuffCast(string buff);

    void NoteOrdinaryBuffCast();

    // The combat profile's debuff went out, by a decision pass or sent by the combat
    // engine itself.
    void NoteDebuffSent(string spell);
}

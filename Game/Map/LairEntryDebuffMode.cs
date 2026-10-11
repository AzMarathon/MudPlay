namespace MudPlay.Game.Map;

// A loop's "wait to enter lairs until the debuff can be cast" option. The combat
// profile's pre-attack debuff shares the round's one between-round cast with every
// buff, heal and cure; a buff cast a moment before walking into a lair leaves the
// debuff uncastable that round, and the attack goes out ahead of it. With the option
// on, the loop holds one step short of a lair until that cast is free.
public enum LairEntryDebuffMode
{
    Off = 0,
    // Let what is due be cast first (a round each, a few rounds at most), then enter
    // once the cast is free: buffs stay up, at the cost of the wait.
    WaitForSpells = 1,
    // Hold buffs back from the moment the loop reaches the lair's doorstep and enter
    // as soon as the cast is free (a round at most). Buffs are let through again once
    // the debuff has gone out in the lair.
    BlockSpells = 2,
    // Between the two: every buff ticked "Priority buff" that is due is cast first,
    // however many (LairEntryDebuffHold has what ends that early), and every other
    // buff is held back as in BlockSpells. Loop files store a member by name and a
    // shared loop sends its number, so neither is ever changed and a new member
    // takes the next number.
    WaitForPriorityBuffs = 3,
}

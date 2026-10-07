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
    // as soon as the cast is free (a round at most). Buffs go out after the fight.
    BlockSpells = 2,
}

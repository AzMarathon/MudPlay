namespace MudPlay.Game.Map;

// The two controls a loop's "wait to enter lairs until I can debuff" option is shown
// as (a tick box, and a choice of how to wait) against the one stored mode.
public static class LairEntryDebuffModes
{
    // Index of each way of waiting in the choice list.
    public const int WaitForSpellsChoice = 0;
    public const int BlockSpellsChoice = 1;

    public static LairEntryDebuffMode From(bool on, int choice) =>
        !on ? LairEntryDebuffMode.Off
        : choice == BlockSpellsChoice ? LairEntryDebuffMode.BlockSpells
        : LairEntryDebuffMode.WaitForSpells;

    public static bool IsOn(LairEntryDebuffMode mode) => mode != LairEntryDebuffMode.Off;

    // The choice to show for a mode; Off shows the first, ready for when it is ticked.
    public static int ChoiceOf(LairEntryDebuffMode mode) =>
        mode == LairEntryDebuffMode.BlockSpells ? BlockSpellsChoice : WaitForSpellsChoice;
}

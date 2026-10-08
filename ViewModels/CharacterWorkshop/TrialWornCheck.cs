namespace MudPlay.ViewModels.CharacterWorkshop;

// What the Gear Finder's "Missing?" check found for one trial slot.
public enum TrialWornCheck
{
    // The check is off, or the worn gear hasn't been read yet.
    NotChecked,
    // The slot's item is being worn right now.
    Worn,
    // The slot's item isn't being worn.
    NotWorn,
    // Nothing is picked for the slot.
    Unfilled,
}

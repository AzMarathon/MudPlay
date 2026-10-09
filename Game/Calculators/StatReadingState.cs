namespace MudPlay.Game.Calculators;

// How far the last `stat` screen can be trusted as a source of trained stats.
public enum StatReadingState
{
    // No marks on record: a saved snapshot, a screen still printing, or one with no
    // colour to read. The values are taken as they stand, less worn gear.
    Unverified,

    // Every stat the screen marked as modified is explained by one figure.
    Accounted,

    // The screen marked a stat as modified and worn gear plus the effects it listed
    // don't come to a single figure for it. That stat's trained value isn't known.
    Unexplained,
}

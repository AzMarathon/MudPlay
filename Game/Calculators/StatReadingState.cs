namespace MudPlay.Game.Calculators;

// How far the last `stat` screen can be trusted as a source of trained stats.
public enum StatReadingState
{
    // No marks on record: a snapshot saved before they were kept, a screen still
    // printing, or one with no colour to read. Worn gear comes off the values as
    // it did before the marks were read, and nothing may be acted on.
    Unverified,

    // Every stat the screen marked as modified is explained by one figure.
    Accounted,

    // The screen marked a stat as modified and the effects it listed (with worn
    // gear, on Paradigm) don't come to a single figure for it. That stat's trained
    // value isn't known.
    Unexplained,
}

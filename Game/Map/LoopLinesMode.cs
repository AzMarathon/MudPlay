namespace MudPlay.Game.Map;

// How the Navigation map draws a running loop. The Loop lines chip cycles through
// these in order: Steps -> NoSteps -> Off -> Steps. Serialized by name
// (JsonStringEnumConverter) when persisted per-character, so the declaration order
// is free to change without breaking saved profiles.
public enum LoopLinesMode
{
    // The loop's line with a numbered circle on each of its steps.
    Steps,

    // The line alone: one unbroken route through the whole loop, no circles.
    NoSteps,

    // Neither. A loop being built, and a saved loop previewed by hand, still draw.
    Off,
}

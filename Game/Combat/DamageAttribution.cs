namespace MudPlay.Game.Combat;

// One damage line's two sides, as DamageLineAttributor reads them; null is a side
// the line doesn't name.
public readonly record struct DamageAttribution(string? Source, string? Target, int Amount);

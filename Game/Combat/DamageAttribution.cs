namespace MudPlay.Game.Combat;

// One damage line's two sides, as DamageLineAttributor reads them; null is a side
// the line doesn't name. NoDealer marks damage nobody dealt — a condition or effect
// on the victim ("You are poisoned for 2 damage!") — as opposed to a dealer the line
// just doesn't name ("Acid sears you"): it counts only as damage taken.
public readonly record struct DamageAttribution(string? Source, string? Target, int Amount, bool NoDealer = false);

namespace MudPlay.Game.Calculators;

// Physical attack mode for accuracy/swing math. Values match MajorMUD's own
// attack-type field values so they can be compared against game-data
// attack-type fields directly.
public enum MudAttackType
{
    Punch = 1,
    Kick = 2,
    Jumpkick = 3,
    // Backstab is a bool flag in the low-level hit/swing math, so this slot sat
    // empty. The shared matchup builder (CharacterCalculator.BuildMeleeAttackProfile)
    // needs it as a first-class type, so it takes the free 4 — a client-side
    // discriminator, never compared against a game-data attack-type field.
    Backstab = 4,
    Normal = 5,
    Bash = 6,
    Smash = 7,
}

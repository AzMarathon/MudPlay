namespace MudPlay.Game.Spells;

// One entry in a Settings spell-picker typeahead: the 4-letter Short cast-code
// the game actually recognises, paired with the full Name for readability.
//
// The picker commits Short so the stored — and ultimately cast — value is the
// code the game accepts; typing the full name only ever speaks it. The dropdown
// shows Display ("code — name") and filtering matches either field, so the user
// can still find a slot by name.
//
// Learned = the character has actually obtained this spell (SpellbookState's
// obtained set). The picker strikes through + dims the unlearned entries so a
// slot can't be pointed at a spell the class can technically learn but this
// character hasn't (the "agon that was never learned" misconfiguration). When
// the obtained set is unknown (never parsed), every pick is Learned = true so
// nothing is falsely flagged.
public readonly record struct SpellPick(string Short, string Name, bool Learned = true)
{
    // Dropdown label: e.g. swan — way of the swan.
    public string Display => $"{Short} — {Name}";
}

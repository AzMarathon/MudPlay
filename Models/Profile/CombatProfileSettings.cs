using System.Collections.Generic;

namespace MudPlay.Models.Profile;

// The character's casting-spell-profile set — the ordered list of named combat
// spell profiles plus which one is active. Persisted as the top-level
// CharacterProfile.CombatProfiles blob (like Equipment / PartyBuffs), never a
// tier-merged Settings section, since it is whole-character state.
//
// Invariant maintained by CombatProfileManager: there is always at least one
// profile, and the active profile's spell fields mirror the live
// CombatSettings' spell fields (both change only on Save or a profile switch).
public sealed class CombatProfileSettings
{
    // Profiles in display order; the 1-based position is the user-facing "number".
    public List<CombatSpellProfile> Profiles { get; set; } = new();

    // Id of the active profile. Empty / unknown resolves to the first profile.
    public string ActiveId { get; set; } = string.Empty;
}

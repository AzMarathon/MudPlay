using MudPlay.Game;

namespace MudPlay.Models.Profile;

// Persisted snapshot of the character's last-observed carry weight — written on
// ProfileSaving from Game.Inventory.InventoryManager and rehydrated on
// ProfileService.ProfileLoaded. Encumbrance only changes while in the realm, so
// the value the client last saw is still accurate the moment the next session
// reconnects; persisting it means the travel-cost models, the hop-timing
// calibrator, and the Workshop start with the real bracket instead of Unknown,
// rather than waiting for the connect-time `i` (which never fires on a manual
// login or a hangup-suppressed relog).
//
// A restored reading is a STALE ESTIMATE, not a fresh dump: the hydrate path
// leaves InventoryManager unloaded so the connect-`i` still authoritatively
// re-bases the full snapshot (coins, items, worn gear). Stored on
// CharacterProfile.LastKnownEncumbrance; null until the first `i` of any session.
public sealed class LastKnownEncumbrance
{
    // JSON schema version for forward-compat migrations.
    public int SchemaVersion { get; set; } = 1;

    public int CurrentWeight { get; set; }
    public int MaxWeight { get; set; }
    public int Percentage { get; set; }
    public EncumbranceLevel Category { get; set; } = EncumbranceLevel.Unknown;
}

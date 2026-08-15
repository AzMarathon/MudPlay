namespace MudPlay.Models.GameData;

// Per-monster name/prefix record — what the room classifier needs to resolve a
// monster's display name (with its flavor adjective) back to a canonical monster.
//
// Combat MESSAGE recognition no longer lives here. Hit / miss / dodge / armor-block
// are recognized generically from line colour + wording (Game.Combat.CombatLineClassifier),
// and a death is recognized generically from the exp line (Game.Combat.MonsterDeathWatcher)
// with our own targeting naming the mob — so the old per-monster HitYou / … / MissOther
// and DeathLine fields were retired (they were arbitrary per-monster flavor the user had
// to hand-maintain, unworkable for a custom game with tens of thousands of monsters).
//
// One record per MonsterRecord Number. Records are NOT deduplicated by name — multiple
// monsters can share a name in the game data (giant rat #1 vs #109 are different rows).
//
// Storage parallels MessageRecord: per-set runtime file at game data/{set}/monster-messages.json,
// universal seed at Global/MonsterMessages.seed.json (bootstrapped from the bundled
// Defaults/ copy on first launch). The seed's now-unused message fields are simply
// ignored on load.
//
// FlavorPrefixes: adjective prefixes the game may insert before the monster name
// ("nasty", "large", …). The classifier also strips the shared global vocabulary
// (Game.Combat.MonsterFlavorPrefixes), so this per-monster list is now a supplementary
// fallback for a non-standard prefix. AllowNoPrefix is true when the monster can appear
// with just the bare Name.
//
// Links: back-references to the game-data rows this record is anchored to — normally one
// (Monsters, N) matching the record's monster Number.
public sealed record MonsterMessageRecord(
    string                       Id,
    string                       Name,
    IReadOnlyList<string>        FlavorPrefixes,
    bool                         AllowNoPrefix,
    IReadOnlyList<GameDataLink>? Links = null);

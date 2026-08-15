namespace MudPlay.Models.GameData;

// Per-monster name record — the canonical monster name the room classifier resolves a
// display name back to, and its back-reference to the Monsters-table row.
//
// Combat MESSAGE recognition no longer lives here. Hit / miss / dodge / armor-block
// are recognized generically from line colour + wording (Game.Combat.CombatLineClassifier),
// and a death is recognized generically from the exp line (Game.Combat.MonsterDeathWatcher)
// with our own targeting naming the mob — so the old per-monster HitYou / … / MissOther
// and DeathLine fields were retired.
//
// Flavor adjectives ("large", "nasty", …) are no longer per-monster data either. The
// classifier strips a leading word in the shared per-set vocabulary (Services.FlavorPrefixStore,
// editable in the Game Data Browser) and matches the bare Name — so a custom realm with
// tens of thousands of monsters needs no per-monster prefix list at all.
//
// One record per MonsterRecord Number. Records are NOT deduplicated by name — multiple
// monsters can share a name in the game data (giant rat #1 vs #109 are different rows).
//
// Storage parallels MessageRecord: per-set runtime file at game data/{set}/monster-messages.json,
// universal seed at Global/MonsterMessages.seed.json (bootstrapped from the bundled
// Defaults/ copy on first launch). The seed's now-unused message + flavor fields are simply
// ignored on load.
//
// Links: back-references to the game-data rows this record is anchored to — normally one
// (Monsters, N) matching the record's monster Number.
public sealed record MonsterMessageRecord(
    string                       Id,
    string                       Name,
    IReadOnlyList<GameDataLink>? Links = null);

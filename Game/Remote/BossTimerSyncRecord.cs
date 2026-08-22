using System;

namespace MudPlay.Game.Remote;

// One boss-timer entry as it travels over the @timer sync wire: the boss IDENTITY
// (its MDB MonsterNumber when known — compact + rename-proof + resolvable to a name
// from game data even when the receiver doesn't track the boss — else its Name) plus
// the raw KILLED-AT time. Nothing derived (windows, next-spawn) travels; the receiver
// recomputes those locally. Room pins are NEVER part of identity — they're a user-
// editable navigation detail, so add/remove of a room can't affect a sync match.
public readonly record struct BossTimerSyncRecord(int? MonsterNumber, string? Name, DateTimeOffset KilledAt)
{
    // A record must carry at least one identity handle.
    public bool HasIdentity => MonsterNumber is not null || !string.IsNullOrEmpty(Name);
}

using System;

namespace MudPlay.Game.Remote;

// One item sighting as it travels over the @roomba sync wire: the item's MDB
// record NUMBER (never its name — every sender/receiver on a BBS shares the
// same imported item table, so the number round-trips through GetName on the
// other end) plus the room it was seen in, the stack size, and the raw
// SEEN-AT time. Simpler than BossTimerSyncRecord's identity (no name
// fallback needed — an item always resolves to a number on the sending side,
// see GhItemLocationStore.ToSyncRecords) and it carries a room, since unlike
// a boss kill a sighting IS its location.
public readonly record struct GhItemSyncRecord(int Map, int Room, int ItemNumber, int Quantity, DateTimeOffset SeenAt);

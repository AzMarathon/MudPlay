namespace FujinTerm.Game.Map;

// One sailing a sea-captain dock offers: typing Keyword at the dock room
// CMD-teleports the caster onto a ship and, several buff-locked transit rooms
// later, lands them at ArrivalRoom. Discovered data-driven off a room's CMD
// TBInfo `secure passage to <place>` lines by BoatPassageResolver — never
// hardcoded per realm.
//
// It is a CMD teleport, so it SPLITS the party exactly like `ring chime`: the
// leader relays `.@party <keyword>`, every member fires their own copy, and the
// party re-forms at the destination port. Each member is gated INDIVIDUALLY on
// both MinLevel and FareCopper — the captain rejects an under-level or too-poor
// member at the dock and leaves them behind — so routing treats the poorest /
// lowest member as the constraint. RequiresCheckability flags a port whose
// captain also demands an untrackable quest-flag / rank attunement (talkiran's
// MerchantCaptain rank): the client can't read that flag, so it's the user's
// responsibility, and an un-attuned member simply never boards (the walker
// fails the voyage out on the arrival timeout).
//
// FareCopper is in copper farthings (the base coin unit), so it folds into the
// same on-hand-wealth gate as a (Toll: N) exit — unlike a toll's N*100
// copper-from-gold, a boat fare is already copper.
public readonly record struct BoatPassage(
    RoomKey DockRoom,
    string Place,
    string Keyword,
    RoomKey ArrivalRoom,
    int MinLevel,
    long FareCopper,
    bool RequiresCheckability);

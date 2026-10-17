namespace MudPlay.Game.Light;

// How a room-light spell lights a room, read from its ability codes. The kind
// decides whether it adds to a lit light item or takes the item's place.
public enum RoomLightSpellKind
{
    // Grants no illumination.
    None,

    // An Illu (13) or RoomIllu (14) buff: its strength adds to the character's
    // illumination, on top of whatever light item is lit.
    Buff,

    // A TextBlock (148) spell whose action `giveitem`s a light item (Stock's
    // illuminate makes a light ball): the spell lights nothing itself, the item it
    // makes is lit like any carried light and holds the one lit slot.
    MakesLightItem,
}

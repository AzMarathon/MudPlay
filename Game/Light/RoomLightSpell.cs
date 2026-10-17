namespace MudPlay.Game.Light;

// The configured room-light spell resolved against the active game data: the cast
// code the buff slot stores, the spell's name, how it lights a room and how
// strongly. MadeItem is the light item a MakesLightItem spell gives (null for a
// buff); Strength is the buff's magnitude, or the made item's IlluTarget.
public readonly record struct RoomLightSpell(
    string CastCode, string Name, RoomLightSpellKind Kind, int Strength, LightItem? MadeItem)
{
    public bool IsBuff => Kind == RoomLightSpellKind.Buff;
}

namespace MudPlay.Game.Pvp;

// How another player's attack on us was recognised.
public enum PvpAttackKind
{
    // "<player> moves to attack you!" — a weapon or an attack spell aimed at us.
    Announced,

    // A damage line naming them. The backstop for an announce we never saw.
    Damage,

    // They came into the room we were already in and started a room attack.
    RoomAttack,
}

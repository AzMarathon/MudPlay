namespace MudPlay.Game.Recovery;

// What HangupDeath.Judge makes of a hang-up while dropped.
public enum HangupDeathVerdict
{
    // The character wasn't dropped when it left the game, or the realm doesn't
    // penalise the hang-up: nothing to judge.
    NotSuspected,

    // It died after the link was gone.
    Died,

    // It did not: still dropped, or its lives are what they were.
    Alive,

    // It is standing again, which a death would explain, but neither its lives
    // nor the room can confirm one.
    Unsure,
}

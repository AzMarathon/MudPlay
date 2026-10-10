namespace MudPlay.Game.Recovery;

// What HangupDeath.Judge makes of a hang-up the penalty could have killed for.
public enum HangupDeathVerdict
{
    // The realm doesn't penalise the hang-up, or the character had more HP than
    // the penalty takes: nothing to judge.
    NotSuspected,

    // It died after the link was gone: exactly one life fewer, and nothing the
    // entry showed says otherwise.
    Died,

    // It did not: no life was lost, or it came back with the HP of a character
    // that wasn't killed.
    Alive,

    // The lives now aren't known yet. A `stat` read on this connection settles it.
    NeedsLives,

    // It can't be told, and nothing more will tell: the lives weren't known
    // before, more than one is gone, or a life was lost but not to this hang-up
    // by what the entry showed.
    Unsure,
}

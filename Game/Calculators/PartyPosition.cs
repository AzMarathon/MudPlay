namespace MudPlay.Game.Calculators;

// Where a member sits in the party formation — the Paradigm aggro score's
// position term (front-rankers draw the most attention). A solo player counts as
// front-rank: "just as exposed as a frontliner."
public enum PartyPosition
{
    Frontrank,
    Midrank,
    Backrank,
    Solo,
}

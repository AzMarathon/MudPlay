namespace MudPlay.Game;

// The last HP the game stated for a party member: the percentage, when, and where it
// came from ("par" or "@health").
public readonly record struct PartyHpReading(int Percent, DateTimeOffset At, string Source);

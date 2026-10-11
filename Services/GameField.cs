namespace MudPlay.Services;

// One named field of a GameRow, as its Fields list yields them.
public readonly record struct GameField(string Name, GameValue Value);

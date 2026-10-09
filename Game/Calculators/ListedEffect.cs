namespace MudPlay.Game.Calculators;

// One effect off the `stat` screen's list, with every spell that prints that line.
// Several spells can share a line ("You feel strong" is one song that raises
// Strength and another that doesn't), so each is kept as a possible reading. No
// readings means the line matched no spell on record.
public sealed record ListedEffect(string Text, IReadOnlyList<EffectStatReading> Readings);

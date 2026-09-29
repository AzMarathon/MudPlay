namespace MudPlay.Game.Spells;

// What CasterMessageMatcher.TryMatchCaptures pulled out of a matched line: every name
// capture in template order, the ones the template pinned by role ({spellname},
// {target}, {source}; null when it pins none), and the first number (null when the
// template has no numeric slot).
public readonly record struct MessageCaptures(
    IReadOnlyList<string> Names, string? Spell, string? Target, string? Source, int? Number);

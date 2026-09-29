namespace MudPlay.Game.Spells;

// One of our spells' damage lines, as Session Stats recognises it: the spell it
// belongs to, and whether it's a follow-up — the line of a spell the cast chains to
// (necromantic bolt's drain), whose damage belongs to the cast it follows.
public readonly record struct SpellLineMatcher(string Name, CasterMessageMatcher Matcher, bool FollowUp = false);

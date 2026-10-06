namespace MudPlay.Game.Spells;

// One of our spells' damage lines, as Session Stats recognises it: the spell it
// belongs to, and whether it's a follow-up — the line of a spell the cast chains to
// (necromantic bolt's drain), whose damage belongs to the cast it follows. HitsRoom is
// the spell's own scope from the game data: its line lands on every monster in the
// room, whatever victim its wording does or doesn't name.
public readonly record struct SpellLineMatcher(
    string Name, CasterMessageMatcher Matcher, bool FollowUp = false, bool HitsRoom = false);

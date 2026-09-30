namespace MudPlay.Game.Quests;

// A completed quest whose reward is an ability (Perfect Stealth, SeeHidden, …) and
// the level this character can first do it at — the Level Projection applies the
// ability from that level on.
public readonly record struct QuestAbilityAward(int AbilityId, int FromLevel);

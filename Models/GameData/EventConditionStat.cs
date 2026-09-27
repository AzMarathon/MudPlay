namespace MudPlay.Models.GameData;

// The character stat an EventCondition checks.
//   Money       — coin carried, compared in the condition's denomination.
//   Encumbrance — carried weight as a percent of max.
//   Experience  — total experience.
//   Level       — character level.
public enum EventConditionStat
{
    Money = 0,
    Encumbrance = 1,
    Experience = 2,
    Level = 3,
}

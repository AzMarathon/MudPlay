namespace MudPlay.Game.Train;

// One character's own party-train picture, as TrainerWalkManager works it out from
// its Auto-Trainer thresholds: where it is, how far this trip would take it, what
// that costs, and whether it's ready. EtaSeconds is the projected time to Ready
// (-1 = unknown, 0 = ready). Exp / NextExp: the running exp total and the total
// that reaches the next not-yet-reached level (0 = unknown chart).
public readonly record struct PartyTrainSelfAssessment(
    int Level,
    int ClassNumber,
    int LevelsToTrain,
    long CostCopper,
    PartyTrainReadiness Readiness,
    int EtaSeconds,
    long Exp,
    long NextExp);

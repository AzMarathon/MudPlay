namespace MudPlay.Models.Profile;

// Where auto-train may fetch coin when the purse can't cover a run. Stored as its
// number, so the default (0) keeps a profile saved before this existed on the
// original behavior.
public enum TrainFundingMode
{
    // Flagged stash rooms first, then a bank.
    StashAndBank = 0,
    BankOnly = 1,
    StashOnly = 2,
    // Fetch nothing: stay armed and keep looping until the purse covers the run.
    None = 3,
}

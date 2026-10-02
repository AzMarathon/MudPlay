namespace MudPlay.Game.Cash;

// How a stash transfer ended: the stash is empty (whether or not it had anything
// to move), it gave up (no route, nothing could be picked up, the bank took no
// deposit), or it was stopped from outside (a user halt, Reset States).
public enum StashTransferOutcome { Done, Failed, Stopped }

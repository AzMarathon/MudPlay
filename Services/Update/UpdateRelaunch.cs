namespace MudPlay.Services.Update;

// What the post-update relaunch should restore. The app hands this back when the
// installer asks it to stand down: which profile the new build should reopen
// ("BBS/Name", or null when nothing named is loaded) and whether the session was
// live at the time, so a client that was mid-game comes back in-game rather than
// dropping the user on a cold client.
public readonly record struct UpdateRelaunch(string? ProfileToken, bool Reconnect);

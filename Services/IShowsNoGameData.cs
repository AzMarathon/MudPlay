namespace MudPlay.Services;

// Marks a window that shows no game data, so opening it leaves the startup draft's
// put-off game-data set unbuilt (AppServices.BuildDeferredGameData). Any other
// window builds the set as it opens: a window left off this list only costs the
// saving, while one wrongly on it would open on stores not built yet.
public interface IShowsNoGameData;

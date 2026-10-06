using MudPlay.Game.Cash;

namespace MudPlay.Services;

// Keeps the believed stash balances (Game.Cash.StashLedger) in the realm's folder.
// Coin hidden in a room is there for every character the player runs on that realm,
// so the tally is shared by them rather than kept per character.
//
// Those characters are often online together, one client each, so the file is
// re-read whenever it changed on disk before a balance is read or moved. Each
// change is then written on top of what the other client last saved.
public sealed class StashBalanceStore
{
    public const string LogCategory = "Stash";

    private readonly StashLedger _ledger;
    private readonly LogService? _log;
    private DateTime _seenWrite = DateTime.MinValue;
    // Set while the ledger is being filled from disk, so that isn't written back.
    private bool _loading;

    // Folder of the realm whose balances are loaded. null keeps them in memory only.
    public string? ActiveRealmFolder { get; private set; }

    public StashBalanceStore(StashLedger ledger, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        _ledger = ledger;
        _log = log;
        _ledger.Accessing += TakeInOutsideChanges;
        _ledger.Changed += Persist;
    }

    public void OnRealmChanged(string? realmFolder)
    {
        ActiveRealmFolder = string.IsNullOrWhiteSpace(realmFolder) ? null : realmFolder;
        _seenWrite = DateTime.MinValue;
        Load(ActiveRealmFolder is null ? null : AppPaths.RealmStashBalancesFile(ActiveRealmFolder));
    }

    // Move balances a character profile carried (they were per character once) into
    // the realm's tally. False when there's no realm to hold them: the caller then
    // leaves the profile's copy alone.
    public bool Adopt(IReadOnlyDictionary<string, long> fromProfile, string characterName)
    {
        if (ActiveRealmFolder is null || fromProfile.Count == 0) return false;
        _ledger.Merge(fromProfile);
        _log?.Info(LogCategory,
            $"moved {fromProfile.Count} stash balance(s) from '{characterName}' to the realm; every character on it now shares them");
        return true;
    }

    private void TakeInOutsideChanges()
    {
        if (_loading || ActiveRealmFolder is null) return;
        string path = AppPaths.RealmStashBalancesFile(ActiveRealmFolder);
        if (WriteTime(path) == _seenWrite) return;
        Load(path);
        _log?.Debug(LogCategory, "stash balances re-read: another client on this realm changed them");
    }

    private void Load(string? path)
    {
        _loading = true;
        try
        {
            _ledger.Hydrate(path is null ? null : JsonStore.Load<Dictionary<string, long>>(path));
            if (path is not null) _seenWrite = WriteTime(path);
        }
        finally { _loading = false; }
    }

    private void Persist()
    {
        if (_loading || ActiveRealmFolder is null) return;
        string path = AppPaths.RealmStashBalancesFile(ActiveRealmFolder);
        // Fired from the hide / pick-up echo path: a failed write must not break play.
        // The tally stands in memory and the next change writes again.
        try
        {
            JsonStore.Save(path, _ledger.Snapshot());
            _seenWrite = WriteTime(path);
        }
        catch (Exception ex)
        {
            _log?.Warn(LogCategory, $"failed to save stash balances: {ex.Message}");
        }
    }

    private static DateTime WriteTime(string path) =>
        File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
}

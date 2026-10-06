using System.IO;
using MudPlay.Game.Cash;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// Stash balances are kept per realm: every character on it shares one tally, and two
// clients on the same realm see each other's changes.
public sealed class StashBalanceStoreTests : IDisposable
{
    private readonly string _realm = Path.Combine(Path.GetTempPath(), "stash-realm-" + Path.GetRandomFileName());
    private static readonly RoomKey Room = new(1, 100);

    public StashBalanceStoreTests() => Directory.CreateDirectory(_realm);
    public void Dispose() => Directory.Delete(_realm, recursive: true);

    private (StashLedger Ledger, StashBalanceStore Store) Client()
    {
        StashLedger ledger = new();
        StashBalanceStore store = new(ledger);
        store.OnRealmChanged(_realm);
        return (ledger, store);
    }

    [Fact]
    public void AnotherCharacterOnTheRealm_SeesWhatWasHidden()
    {
        (StashLedger first, _) = Client();
        first.NoteHidden(Room, 5000);

        (StashLedger second, _) = Client();

        Assert.Equal(5000, second.Believed(Room));
    }

    [Fact]
    public void TwoClientsOnline_EachBuildsOnTheOthersChange()
    {
        (StashLedger a, _) = Client();
        (StashLedger b, _) = Client();

        a.NoteHidden(Room, 5000);
        File.SetLastWriteTimeUtc(AppPaths.RealmStashBalancesFile(_realm), DateTime.UtcNow.AddSeconds(5));
        b.NoteRecovered(Room, 2000);
        File.SetLastWriteTimeUtc(AppPaths.RealmStashBalancesFile(_realm), DateTime.UtcNow.AddSeconds(10));

        Assert.Equal(3000, a.Believed(Room));
        Assert.Equal(3000, b.Believed(Room));
    }

    [Fact]
    public void ProfileCarriedBalances_AreAddedToTheRealms()
    {
        (StashLedger ledger, StashBalanceStore store) = Client();
        ledger.NoteHidden(Room, 1000);

        bool adopted = store.Adopt(new Dictionary<string, long> { ["1/100"] = 500, ["1/200"] = 300 }, "Alt");

        Assert.True(adopted);
        (StashLedger other, _) = Client();
        Assert.Equal(1500, other.Believed(Room));
        Assert.Equal(300, other.Believed(new RoomKey(1, 200)));
    }

    [Fact]
    public void NoRealm_NothingAdoptedOrWritten()
    {
        StashLedger ledger = new();
        StashBalanceStore store = new(ledger);
        store.OnRealmChanged(null);

        Assert.False(store.Adopt(new Dictionary<string, long> { ["1/100"] = 500 }, "Alt"));
        ledger.NoteHidden(Room, 100);

        Assert.False(File.Exists(AppPaths.RealmStashBalancesFile(_realm)));
    }

    [Fact]
    public void RealmSwitch_LoadsThatRealmsBalances()
    {
        (StashLedger ledger, StashBalanceStore store) = Client();
        ledger.NoteHidden(Room, 5000);
        string other = Path.Combine(_realm, "other");
        Directory.CreateDirectory(other);

        store.OnRealmChanged(other);

        Assert.Equal(0, ledger.Believed(Room));
    }
}

using System.Text.Json;
using MudPlay.Game.Inventory;
using MudPlay.Game.Map;
using MudPlay.Game.Recovery;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A death is stamped with the game data its room numbers are of. Under another set
// the same number is another room, so nothing walks to the pile, arms a grab for it
// or draws it there; an unstamped record from before the stamp is taken as the
// active set's, as it always was.
public sealed partial class DeathRecoveryManagerTests
{
    private static GraphHarness DiedAtTheGatesOnAlpha(bool autoRecover)
    {
        GraphHarness h = new();
        Die(h, new[] { new EquippedItem("rusty dagger", "Weapon Hand") }, new[] { "torch" });
        h.Recovery.AutoRecover = autoRecover;
        h.Tracker.NoteRoomObserved(Obs3());   // standing next door (1/3)
        h.Sent.Clear();
        return h;
    }

    [Fact]
    public void ADeath_IsStampedWithTheGameDataItWasRecordedOn()
    {
        using GraphHarness h = DiedAtTheGatesOnAlpha(autoRecover: false);

        Assert.Equal("alpha", h.Latest.GameData);
        Assert.Null(h.Recovery.OtherGameDataRefusal(h.Latest));
    }

    [Fact]
    public void RecoverNow_OnAnotherGameData_IsRefused_AndAllowedAgainBackOnItsOwn()
    {
        using GraphHarness h = DiedAtTheGatesOnAlpha(autoRecover: false);
        h.SwitchGameData("beta");
        h.EnterGates();                       // beta's 1/1, a room of the same number
        h.Sent.Clear();

        Assert.Equal(
            "This death's rooms belong to game data 'alpha', and 'beta' is active now — "
            + "switch back to that game data to recover it.",
            h.Recovery.OtherGameDataRefusal(h.Latest));
        Assert.False(h.Recovery.RecoverNow(h.Latest));
        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Active, h.Latest.Status);

        h.SwitchGameData("alpha");
        h.EnterGates();
        Assert.Null(h.Recovery.OtherGameDataRefusal(h.Latest));
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        Assert.Contains("look", h.Sent);
    }

    [Fact]
    public void AnUnstampedRecord_IsAllowedOnAnyGameData()
    {
        using GraphHarness h = DiedAtTheGatesOnAlpha(autoRecover: false);
        h.Latest.GameData = null;             // written before the stamp was kept
        h.SwitchGameData("beta");
        h.EnterGates();
        h.Sent.Clear();

        Assert.Null(h.Recovery.OtherGameDataRefusal(h.Latest));
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        Assert.Contains("look", h.Sent);
    }

    [Fact]
    public void AutoRecover_DoesNotStartForAnotherGameDatasDeath()
    {
        using GraphHarness h = DiedAtTheGatesOnAlpha(autoRecover: true);
        h.SwitchGameData("beta");
        h.Tracker.NoteRoomObserved(Obs3());
        h.Sent.Clear();

        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();

        Assert.Empty(h.Sent);
        Assert.Equal(DeathRecoveryStatus.Active, h.Latest.Status);

        // Back on its own game data the same walk-in recovers it.
        h.SwitchGameData("alpha");
        h.Tracker.NoteRoomObserved(Obs3());
        h.Sent.Clear();
        h.Tracker.NoteMoveSentByObserver(Direction.S);
        h.FeedSurvey("corpse of Ermias");
        h.EnterGates();

        Assert.Contains("recover corpse Ermias", h.Sent);
    }

    // A hang-up death worked out under another set than the one its held list was
    // written on keeps the list's set, and its room isn't named from this graph.
    [Fact]
    public void AnUnwitnessedDeath_KeepsTheGameDataOfItsHeldList()
    {
        using GraphHarness h = new();
        h.SwitchGameData("beta");
        h.EnterGates();

        DeathRecord? record = h.Tracker.NoteUnwitnessedDeath(new UnwitnessedDeath(
            new RoomRef(1, 1), DateTimeOffset.UtcNow.AddMinutes(-3), LivesRemaining: 6,
            "Killed by the hang-up penalty (not seen: worked out on entering the game).",
            Equipped: null, Lost: [new DeathItem("torch")], Coins: null, GameData: "alpha"));

        Assert.Equal("alpha", record!.GameData);
        Assert.Null(record.RoomName);
        Assert.NotNull(h.Recovery.OtherGameDataRefusal(record));

        // A list from before the stamp was kept gives an unstamped record, named here.
        DeathRecord? older = h.Tracker.NoteUnwitnessedDeath(new UnwitnessedDeath(
            new RoomRef(1, 1), DateTimeOffset.UtcNow.AddMinutes(-2), LivesRemaining: 5,
            "Killed by the hang-up penalty (not seen: worked out on entering the game).",
            Equipped: null, Lost: [new DeathItem("torch")], Coins: null));
        Assert.Null(older!.GameData);
        Assert.Equal("Town Gates", older.RoomName);
    }

    // A set that is no longer installed can't be switched back to, so its deaths are
    // taken as the active set's, like a record from before the stamp (user, 2026-10-17).
    [Fact]
    public void ADeathOfAGameDataNoLongerInstalled_IsTakenAsTheActiveOnes()
    {
        using GraphHarness h = DiedAtTheGatesOnAlpha(autoRecover: false);
        HashSet<string> installed = new(StringComparer.OrdinalIgnoreCase) { "alpha", "beta" };
        h.Recovery.SetGameDataInstalledProbe(installed.Contains);
        h.SwitchGameData("beta");
        h.EnterGates();
        Assert.NotNull(h.Recovery.OtherGameDataRefusal(h.Latest));

        installed.Remove("alpha");            // deleted, or renamed in Manage Sets
        h.Sent.Clear();

        Assert.Null(h.Recovery.OtherGameDataRefusal(h.Latest));
        Assert.False(h.Recovery.GameDataInstalled("alpha"));
        Assert.True(h.Recovery.RecoverNow(h.Latest));
        Assert.Contains("look", h.Sent);
    }

    [Fact]
    public void TheSavedRecord_RoundTripsWithAndWithoutTheGameData()
    {
        DeathRecord stamped = new(DateTimeOffset.UnixEpoch, new RoomRef(1, 1), 5, "You now have 5 lives remaining.")
        {
            RecordNumber = 1,
            GameData = "alpha",
        };
        string json = JsonSerializer.Serialize(stamped, JsonStore.Options);
        Assert.Equal("alpha", JsonSerializer.Deserialize<DeathRecord>(json, JsonStore.Options)!.GameData);

        // A record saved before the stamp was kept reads in unstamped.
        const string older = """{ "RecordNumber": 2, "Room": { "Map": 1, "Room": 3 }, "LivesRemaining": 4 }""";
        DeathRecord read = JsonSerializer.Deserialize<DeathRecord>(older, JsonStore.Options)!;
        Assert.Null(read.GameData);
        Assert.Equal(2, read.RecordNumber);

        json = JsonSerializer.Serialize(read, JsonStore.Options);
        Assert.Null(JsonSerializer.Deserialize<DeathRecord>(json, JsonStore.Options)!.GameData);
    }
}

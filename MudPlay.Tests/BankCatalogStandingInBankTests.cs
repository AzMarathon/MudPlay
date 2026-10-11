using System.IO;
using MudPlay.Game.GameData;
using MudPlay.Game.Map;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// A remote @deposit-all is turned down outside a bank only when that is sure:
// the room is confirmed and the set lists banks. Anything less is "can't tell",
// and the deposit is sent as it always was.
public sealed class BankCatalogStandingInBankTests : IDisposable
{
    private static readonly RoomKey Bank = new(1, 5);
    private static readonly RoomKey Street = new(1, 6);

    private const string ShopsWithABank = """
        [
          { "Number": 3, "Name": "First Bank", "ShopType": 7, "Assigned To": "Room 1/5" },
          { "Number": 4, "Name": "General Store", "ShopType": 1, "Assigned To": "Room 1/6" }
        ]
        """;

    private const string ShopsWithoutABank = """
        [
          { "Number": 4, "Name": "General Store", "ShopType": 1, "Assigned To": "Room 1/6" }
        ]
        """;

    private readonly string _root;

    public BankCatalogStandingInBankTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-standing-in-bank-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private GameDataCache Set(string shopsJson)
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Shops.json"), shopsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        return cache;
    }

    [Fact]
    public void AConfirmedRoomThatIsNoBank_InASetWithBanks_IsNo()
    {
        Assert.False(BankCatalog.StandingInBank(Set(ShopsWithABank), Street, RoomConfidence.Confirmed));
    }

    [Theory]
    [InlineData(RoomConfidence.Confirmed)]
    [InlineData(RoomConfidence.Suspect)]
    [InlineData(RoomConfidence.Pending)]
    public void TheBankRoom_IsYes_HoweverSureTheTrackerIs(RoomConfidence confidence)
    {
        Assert.True(BankCatalog.StandingInBank(Set(ShopsWithABank), Bank, confidence));
    }

    // The tracker's guess may be wrong: we may be standing in the bank it failed
    // to match, so the deposit is still sent.
    [Theory]
    [InlineData(RoomConfidence.Suspect)]
    [InlineData(RoomConfidence.Pending)]
    [InlineData(RoomConfidence.Unknown)]
    public void ARoomTheTrackerIsNotSureOf_IsUnknown(RoomConfidence confidence)
    {
        Assert.Null(BankCatalog.StandingInBank(Set(ShopsWithABank), Street, confidence));
    }

    [Fact]
    public void ASetWithNoBankRows_IsUnknown_EvenInAConfirmedRoom()
    {
        Assert.Null(BankCatalog.StandingInBank(Set(ShopsWithoutABank), Street, RoomConfidence.Confirmed));
    }

    [Fact]
    public void NoRoomAtAll_IsUnknown()
    {
        Assert.Null(BankCatalog.StandingInBank(Set(ShopsWithABank), null, RoomConfidence.Unknown));
    }
}

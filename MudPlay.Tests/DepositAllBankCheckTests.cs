using System.Text;
using MudPlay.Game;
using MudPlay.Game.Cash;
using MudPlay.Game.Inventory;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// @deposit-all answers "depositing" only where a deposit can start: in a room
// known to be no bank it sends nothing and says so.
public sealed class DepositAllBankCheckTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (RemoteCommandManager engine, List<string> game) Setup(bool? inBank)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState party = new();
        PlayerDatabase players = new();
        players.RecordObservation("Leader", @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        players.EditCustomization("Leader", new PlayerCustomization(RemoteControls: PlayerRemoteControls.ExecuteCommands));
        RemoteCommandManager engine = new(new ChatRouter(router), party, players);
        InventoryManager inventory = new(log: null, itemWeightResolver: null, slotResolver: null);
        inventory.AttachLineExtractor(new LineExtractor(new TerminalEmulator(80, 24)));
        CashSettings cash = new();
        List<string> game = new();
        InventoryActionHandler handler = new(engine, inventory, new GroundItemTracker(router, new CurrencyNaming()),
            party, () => cash, new CurrencyNaming())
        {
            InBank = () => inBank,
        };
        handler.SetWireSender(bytes => game.Add(Encoding.Latin1.GetString(bytes)));
        return (engine, game);
    }

    private static ChatLogEntry Telepath(string msg) =>
        new(Now, ChatChannel.TelepathIncoming, "Leader", msg, $"Leader telepaths: {msg}");

    private static string Only(RemoteCommandManager engine) =>
        Encoding.Latin1.GetString(Assert.Single(engine.LastSentForTests));

    [Fact]
    public void OutsideABank_NothingIsSent_AndTheAnswerSaysSo()
    {
        var (engine, game) = Setup(inBank: false);

        engine.DispatchForTests(Telepath("@deposit-all"));

        Assert.Equal("/Leader {not in a bank - nothing deposited}\r", Only(engine));
        Assert.Empty(game);
    }

    // A leader's stash transfer waits for this answer, so the Warn setting
    // doesn't silence it.
    [Fact]
    public void OutsideABank_TheAnswerGoesWithWarnOffToo()
    {
        var (engine, _) = Setup(inBank: false);
        engine.WarnOnDenial = false;

        engine.DispatchForTests(Telepath("@deposit-all"));

        Assert.StartsWith("/Leader {not in a bank", Only(engine));
    }

    // In a bank, or in a room the client can't place, the command is worked as
    // before (here it stops at the unread inventory).
    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void InABank_OrInARoomWeCannotPlace_TheDepositIsWorked(bool? inBank)
    {
        var (engine, _) = Setup(inBank);

        engine.DispatchForTests(Telepath("@deposit-all"));

        Assert.StartsWith("/Leader {wealth unknown", Only(engine));
    }
}

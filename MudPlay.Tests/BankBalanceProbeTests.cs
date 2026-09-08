using System.Collections.Generic;
using System.Threading.Tasks;
using MudPlay.Game.Remote;
using Xunit;

namespace MudPlay.Tests;

// Pins the `bank` command parser: per-bank "Your balance at X is:" +
// "On deposit: N copper farthings [G gold crowns]" blocks, Paradigm (name only)
// and Stock (name + "(#N)" shop number), and the awaitable QueryAsync that sends
// `bank` and returns the parsed deposits.
public sealed class BankBalanceProbeTests
{
    // Injected arm-window fires the completion synchronously so QueryAsync
    // resolves in-test without a real timer.
    private static BankBalanceProbe Make(List<string> sent, Action<Action> arm) =>
        new(send: sent.Add, armWindow: arm, log: null);

    [Fact]
    public void Parses_ParadigmSingleBank()
    {
        var probe = Make(new(), a => a());
        probe.HandleLine("Your balance at Bank of Godfrey is:");
        probe.HandleLine("On deposit: 19578816 copper farthings [195,788.16 gold crowns]");

        Assert.Equal(19578816, probe.Balance("Bank of Godfrey"));
    }

    [Fact]
    public void Parses_StockBankWithShopNumber_KeepsBareName()
    {
        var probe = Make(new(), a => a());
        probe.HandleLine("Your balance at Bank of Godfrey (#8) is:");
        probe.HandleLine("On deposit: 4512 copper farthings [45.12 gold crowns]");

        // The (#8) shop-number suffix is dropped so the key matches the bank's
        // shop name (what BankCatalog rooms are keyed on).
        Assert.Equal(4512, probe.Balance("Bank of Godfrey"));
    }

    [Fact]
    public void Parses_MultipleBanksInOneListing()
    {
        var probe = Make(new(), a => a());
        probe.HandleLine("Your balance at Bank of Godfrey is:");
        probe.HandleLine("On deposit: 37509700 copper farthings [375,097.00 gold crowns]");
        probe.HandleLine("Your balance at Lost City Bank is:");
        probe.HandleLine("On deposit: 1000000 copper farthings [10,000.00 gold crowns]");
        probe.HandleLine("Your balance at Bank of Albion is:");
        probe.HandleLine("On deposit: 34109878 copper farthings [341,098.78 gold crowns]");

        Assert.Equal(37509700, probe.Balance("Bank of Godfrey"));
        Assert.Equal(1000000, probe.Balance("Lost City Bank"));
        Assert.Equal(34109878, probe.Balance("Bank of Albion"));
    }

    [Fact]
    public void Balance_UnknownBank_IsNull()
    {
        var probe = Make(new(), a => a());
        Assert.Null(probe.Balance("Never Used Bank"));
    }

    [Fact]
    public async Task QueryAsync_SendsBank_AndReturnsParsedBalances()
    {
        List<string> sent = new();
        Action? armed = null;
        var probe = Make(sent, a => armed = a);   // capture the completion, fire it after feeding lines

        Task<IReadOnlyDictionary<string, long>> query = probe.QueryAsync();

        Assert.Contains("bank", sent);            // it asked the game
        Assert.False(query.IsCompleted);          // still awaiting the reply window

        probe.HandleLine("Your balance at Bank of Godfrey is:");
        probe.HandleLine("On deposit: 4512 copper farthings [45.12 gold crowns]");
        armed!.Invoke();                          // window elapses

        IReadOnlyDictionary<string, long> result = await query;
        Assert.Equal(4512, result["Bank of Godfrey"]);
    }
}

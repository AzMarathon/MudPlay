using MudPlay.Game.Train;
using Xunit;

namespace MudPlay.Tests;

// The leader's roll call for a party train trip: the members' clients are told
// when it sets out and when it is over, and at its end everyone it came back
// without is named and handed on, with how the trip ended.
public sealed class PartyTrainTripRollTests
{
    private sealed class Harness
    {
        public List<string> Wire { get; } = new();
        public List<string> Following { get; } = new() { "Tank", "Healer", "Scout" };
        public HashSet<string> Speakers { get; } = new(StringComparer.OrdinalIgnoreCase) { "Tank", "Healer" };
        public List<(string SetOut, bool ByItself)> Ended { get; } = new();
        public List<string> Order { get; } = new();
        public PartyTrainTripRoll Roll { get; }

        public Harness()
        {
            Roll = new PartyTrainTripRoll(
                send: line => { Wire.Add(line); Order.Add(line); },
                speaks: Speakers.Contains,
                following: () => Following.ToList(),
                ended: (setOut, byItself) =>
                {
                    Ended.Add((string.Join(",", setOut), byItself));
                    Order.Add("ended");
                });
        }
    }

    // Only a client that understands `@ptrain` is told; the rest set out all the same.
    [Fact]
    public void Open_TellsTheMembersWhoseClientsUnderstand_AndTakesTheRoll()
    {
        Harness h = new();

        h.Roll.Open();

        Assert.True(h.Roll.IsOpen);
        Assert.Equal(new[] { "/Tank @ptrain trip on", "/Healer @ptrain trip on" }, h.Wire);
        Assert.Equal(new[] { "Tank", "Healer", "Scout" }, h.Roll.SetOut);
    }

    // Two were turned away on the way. The members told the trip was on are told
    // it is over, and then the roll is handed on: everyone who set out, since the
    // leader's own list may not show who the game dropped without a word.
    [Fact]
    public void Close_TellsThem_ThenHandsOnTheRoll()
    {
        Harness h = new();
        h.Roll.Open();
        h.Wire.Clear();
        h.Order.Clear();
        h.Following.Remove("Healer");
        h.Following.Remove("Scout");

        h.Roll.Close(byItself: true);

        Assert.False(h.Roll.IsOpen);
        Assert.Equal(new[] { "/Tank @ptrain trip off", "/Healer @ptrain trip off" }, h.Wire);
        Assert.Equal(new[] { ("Tank,Healer,Scout", true) }, h.Ended);
        Assert.Equal("ended", h.Order[^1]);
        Assert.Empty(h.Roll.SetOut);
    }

    // A trip the player took over is handed on as that: what is done about the
    // missing is not the roll's to decide.
    [Fact]
    public void Close_OfATripTakenOver_SaysSo()
    {
        Harness h = new();
        h.Roll.Open();
        h.Following.Remove("Scout");

        h.Roll.Close(byItself: false);

        Assert.Equal(new[] { ("Tank,Healer,Scout", false) }, h.Ended);
    }

    // With everyone back the end is still told: the leader may have kept a member
    // who asked during the trip, and it is the listener's own business to find it
    // has nobody left to fetch.
    [Fact]
    public void Close_WithEveryoneBack_StillTellsTheEnd()
    {
        Harness h = new();
        h.Roll.Open();
        h.Order.Clear();

        h.Roll.Close(byItself: true);

        Assert.Single(h.Ended);
        Assert.Equal("ended", h.Order[^1]);
    }

    // A trip that never set out has nobody to tell and nobody to fetch, and a
    // second Close does nothing.
    [Fact]
    public void Close_WithoutATripOpen_DoesNothing()
    {
        Harness h = new();

        h.Roll.Close(byItself: true);
        h.Roll.Open();
        h.Roll.Close(byItself: true);
        h.Wire.Clear();
        h.Order.Clear();
        h.Roll.Close(byItself: true);

        Assert.Empty(h.Wire);
        Assert.Empty(h.Order);
    }
}

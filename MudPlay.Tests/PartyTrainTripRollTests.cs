using MudPlay.Game.Train;
using Xunit;

namespace MudPlay.Tests;

// The leader's roll call for a party train trip: the members' clients are told
// when it sets out and when it is over, and at its end everyone it came back
// without is named, expected, and gone back for.
public sealed class PartyTrainTripRollTests
{
    private sealed class Harness
    {
        public List<string> Wire { get; } = new();
        public List<string> Following { get; } = new() { "Tank", "Healer", "Scout" };
        public HashSet<string> Speakers { get; } = new(StringComparer.OrdinalIgnoreCase) { "Tank", "Healer" };
        public List<string> Expected { get; } = new();
        public List<string> Order { get; } = new();
        public PartyTrainTripRoll Roll { get; }

        public Harness()
        {
            Roll = new PartyTrainTripRoll(
                send: line => { Wire.Add(line); Order.Add(line); },
                speaks: Speakers.Contains,
                following: () => Following.ToList(),
                expectComeback: name => { Expected.Add(name); Order.Add($"expect {name}"); },
                fetchLeftBehind: () => Order.Add("fetch"));
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
    // it is over, the missing are expected (whatever client they are on), and then
    // the fetch is asked for.
    [Fact]
    public void Close_TellsThem_ExpectsWhoeverIsMissing_ThenAsksForTheFetch()
    {
        Harness h = new();
        h.Roll.Open();
        h.Wire.Clear();
        h.Order.Clear();
        h.Following.Remove("Healer");
        h.Following.Remove("Scout");

        h.Roll.Close();

        Assert.False(h.Roll.IsOpen);
        Assert.Equal(new[] { "/Tank @ptrain trip off", "/Healer @ptrain trip off" }, h.Wire);
        Assert.Equal(new[] { "Healer", "Scout" }, h.Expected);
        Assert.Equal("fetch", h.Order[^1]);
        Assert.Empty(h.Roll.SetOut);
    }

    // With everyone back the fetch is still asked for: the leader may have kept a
    // member who asked during the trip and has rejoined since, and it is the
    // fetch's own business to find it has nobody left.
    [Fact]
    public void Close_WithEveryoneBack_ExpectsNobody()
    {
        Harness h = new();
        h.Roll.Open();
        h.Order.Clear();

        h.Roll.Close();

        Assert.Empty(h.Expected);
        Assert.Equal("fetch", h.Order[^1]);
    }

    // A trip that never set out has nobody to tell and nobody to fetch, and a
    // second Close does nothing.
    [Fact]
    public void Close_WithoutATripOpen_DoesNothing()
    {
        Harness h = new();

        h.Roll.Close();
        h.Roll.Open();
        h.Roll.Close();
        h.Wire.Clear();
        h.Order.Clear();
        h.Roll.Close();

        Assert.Empty(h.Wire);
        Assert.Empty(h.Order);
    }
}

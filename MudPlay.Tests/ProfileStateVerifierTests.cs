using MudPlay.Game;
using Xunit;

namespace MudPlay.Tests;

// A copied profile's first entry: a `stat` and an inventory list are read before the
// copy's unverified mark comes off.
public sealed class ProfileStateVerifierTests
{
    private sealed class Harness
    {
        public bool Pending = true;
        public bool CanAsk = true;
        public bool InventoryLoaded;
        public List<string> Sent = new();
        public DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        public ProfileStateVerifier Verifier { get; }

        public Harness() => Verifier = new ProfileStateVerifier(
            () => Pending, () => Pending = false, () => CanAsk,
            () => InventoryLoaded, Sent.Add, () => Now);

        public void Pass(int seconds)
        {
            Now += TimeSpan.FromSeconds(seconds);
            Verifier.Poll();
        }
    }

    [Fact]
    public void LoginReadBoth_VerifiedWithoutSending()
    {
        Harness h = new();
        h.Verifier.Poll();
        h.Verifier.OnStatScreen();
        h.InventoryLoaded = true;

        h.Pass(6);

        Assert.False(h.Pending);
        Assert.Empty(h.Sent);
    }

    [Fact]
    public void EnteredByHand_AsksForStatThenInventory()
    {
        Harness h = new();
        h.Verifier.Poll();
        h.Pass(2);
        Assert.Empty(h.Sent);                     // the login's own refresh gets its chance

        h.Pass(4);
        h.Pass(1);
        Assert.Equal(new[] { "stat" }, h.Sent);   // once, then throttled

        h.Verifier.OnStatScreen();
        h.Pass(6);
        Assert.Equal(new[] { "stat", "i" }, h.Sent);
        Assert.True(h.Pending);

        h.InventoryLoaded = true;
        h.Pass(1);
        Assert.False(h.Pending);
    }

    [Fact]
    public void Busy_OrNotMarked_SendsNothing()
    {
        Harness h = new() { CanAsk = false };
        h.Verifier.Poll();
        h.Pass(30);
        Assert.Empty(h.Sent);

        h.CanAsk = true;
        h.Pending = false;
        h.Pass(30);
        Assert.Empty(h.Sent);
    }
}

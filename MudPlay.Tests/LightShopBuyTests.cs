using System;
using System.Collections.Generic;
using MudPlay.Game.Light;
using Xunit;

namespace MudPlay.Tests;

// LightShopBuy — the buy at a light shop an errand walked to: one `buy` per copy
// short of the target, ended by the first fresh copy or by the buy window passing.
// The window runs on an injected scheduler the tests fire by hand.
public sealed class LightShopBuyTests
{
    private sealed class Harness
    {
        public int Carried;
        public readonly List<string> Sent = new();
        public readonly List<bool> Done = new();
        public Action? Window;
        public bool WindowCancelled;
        public readonly LightShopBuy Buyer;

        public Harness()
        {
            Buyer = new LightShopBuy(_ => Carried, (_, action) =>
            {
                Window = action;
                return new Cancel(() => WindowCancelled = true);
            });
        }

        public void Begin(int count) => Buyer.Begin(new AutoLightBuyRequest(1, "torch", count), Sent.Add, Done.Add);
    }

    private sealed class Cancel(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    [Fact]
    public void BuysTheCopiesShortOfTheTarget_TheFirstFreshCopyEndsIt()
    {
        Harness h = new() { Carried = 1 };
        h.Begin(count: 3);
        Assert.Equal(new[] { "buy torch", "buy torch" }, h.Sent);

        h.Buyer.OnInventoryChanged();   // nothing new yet
        Assert.Empty(h.Done);
        h.Carried = 2;
        h.Buyer.OnInventoryChanged();
        Assert.Equal(new[] { true }, h.Done);
        Assert.True(h.WindowCancelled);
        Assert.False(h.Buyer.IsBuying);
    }

    [Fact]
    public void TheWindowPassingWithNoCopy_EndsItUnbought()
    {
        Harness h = new();
        h.Begin(count: 1);
        h.Window!();
        Assert.Equal(new[] { false }, h.Done);
        h.Carried = 1;
        h.Buyer.OnInventoryChanged();   // over: nothing more is said
        Assert.Single(h.Done);
    }

    [Fact]
    public void Cancelled_SaysNothing()
    {
        Harness h = new();
        h.Begin(count: 1);
        h.Buyer.Cancel();
        h.Carried = 1;
        h.Buyer.OnInventoryChanged();
        Assert.Empty(h.Done);
        Assert.True(h.WindowCancelled);
    }
}

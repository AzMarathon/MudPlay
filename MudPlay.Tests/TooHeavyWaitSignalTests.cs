using System.Reflection;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Inventory;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A frail (Encum% cut) can push a follower over max encumbrance: "You are too
// heavy to move". Not a hold — the follower tells the leader to @wait and @oks once
// a fresh `i` shows them back under the lowered max (report paradigm-20260926-195517).
public sealed class TooHeavyWaitSignalTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public PartyState Party { get; } = new();
        public PartyRestSync Rest { get; }
        public InventoryManager Inv { get; } = new(log: null);
        public LineExtractor Lines { get; } = new(new TerminalEmulator(80, 24));
        public TooHeavyWaitSignal Signal { get; }
        public List<string> RestWire { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Inv.AttachLineExtractor(Lines);
            Rest = new PartyRestSync(Party);
            Rest.SetWireSender(b => RestWire.Add(Encoding.Latin1.GetString(b)));
            Signal = new TooHeavyWaitSignal(Router, Inv, Rest);
            Signal.SetWireSender(_ => { });
            Party.IsInParty = true;
            Party.LeaderName = "Boss";
        }

        public void Game(string text) =>
            Router.Dispatch(new LineExtractor.EmittedLine(text, new CellAttributes[text.Length], DateTimeOffset.UtcNow, IsPromptLine: false));

        public void Inventory(int cur, int max)
        {
            FieldInfo? field = typeof(LineExtractor).GetField("LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(Lines) is not Action<LineExtractor.EmittedLine> handler) return;
            foreach (string line in new[]
            {
                "You are carrying 5 copper farthings.",
                "You have no keys.",
                "Wealth:    5 copper farthings",
                $"Encumbrance:    {cur}/{max}  -  Heavy  [{cur * 100 / max}%]",
            })
                handler(new LineExtractor.EmittedLine(line, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        }

        public void Dispose()
        {
            Signal.Dispose();
            Inv.Dispose();
        }
    }

    [Fact]
    public void TooHeavy_TelepathsWaitWithTheReason_AndReadsInventory()
    {
        using Harness h = new();

        h.Game("You are too heavy to move!");

        Assert.Equal("/Boss @wait (too heavy to move)\r", h.RestWire[^1]);
        Assert.True(h.Signal.IsTooHeavy);
        Assert.Contains(h.Signal.LastSentForTests, b => Encoding.Latin1.GetString(b) == "i\r");
    }

    [Fact]
    public void StillOverTheLoweredMax_KeepsWaiting()
    {
        using Harness h = new();
        h.Game("You are too heavy to move!");

        h.Inventory(cur: 1300, max: 1200);

        Assert.True(h.Signal.IsTooHeavy);
        Assert.True(h.Rest.IsHoldingWait);
    }

    [Fact]
    public void BackUnderMax_SendsOk()
    {
        using Harness h = new();
        h.Game("You are too heavy to move!");
        h.Inventory(cur: 1300, max: 1200);

        h.Inventory(cur: 1300, max: 1290 + 60);   // the debuff wore off

        Assert.False(h.Signal.IsTooHeavy);
        Assert.Equal("/Boss @ok\r", h.RestWire[^1]);
    }

    [Fact]
    public void StaleReadingBeforeTheFreshI_DoesNotClear()
    {
        using Harness h = new();
        h.Inventory(cur: 1284, max: 3840);        // read before the debuff
        h.Game("You are too heavy to move!");

        h.Game("You picked up 5 copper farthings.");   // an incremental change, not a fresh `i`

        Assert.True(h.Signal.IsTooHeavy);
    }
}

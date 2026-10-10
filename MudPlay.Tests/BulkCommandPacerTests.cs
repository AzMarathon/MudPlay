using MudPlay.Game.Inventory;
using Xunit;

namespace MudPlay.Tests;

// The Stock engine queues commands while the character is under an action delay,
// warns at 8 waiting and drops the newest from 12. The pacer keeps at most 6
// unanswered, releasing one per prompt, with a small gap between sends.
public sealed class BulkCommandPacerTests
{
    private sealed class Harness
    {
        public DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        public readonly List<string> Sent = new();
        public readonly List<(DateTimeOffset Due, Action Run)> Timers = new();
        public readonly BulkCommandPacer Pacer;

        public Harness()
        {
            Pacer = new BulkCommandPacer(Sent.Add, (delay, run) => Timers.Add((Now + delay, run)), () => Now);
        }

        // Move the clock on, firing whatever falls due.
        public void Advance(TimeSpan by)
        {
            Now += by;
            while (Timers.FirstOrDefault(t => t.Due <= Now) is { Run: not null } due)
            {
                Timers.Remove(due);
                due.Run();
            }
        }

        public void Drain(int steps)
        {
            for (int i = 0; i < steps; i++) Advance(BulkCommandPacer.MinGap);
        }
    }

    private static IEnumerable<string> Drops(int n) => Enumerable.Range(1, n).Select(i => $"drop item{i}");

    [Fact]
    public void NoMoreThanTheWindowGoesOutUnanswered()
    {
        Harness h = new();
        h.Pacer.Enqueue(Drops(20));
        h.Drain(10);

        Assert.Equal(BulkCommandPacer.Window, h.Sent.Count);
    }

    [Fact]
    public void EachPromptReleasesOneMore()
    {
        Harness h = new();
        h.Pacer.Enqueue(Drops(20));
        h.Drain(10);

        h.Pacer.NotePrompt();
        h.Drain(2);
        h.Pacer.NotePrompt();
        h.Drain(2);

        Assert.Equal(BulkCommandPacer.Window + 2, h.Sent.Count);
        Assert.Equal("drop item8", h.Sent[^1]);
    }

    [Fact]
    public void SendsAreSpacedByTheMinimumGap()
    {
        Harness h = new();
        h.Pacer.Enqueue(Drops(3));
        Assert.Single(h.Sent);

        h.Advance(BulkCommandPacer.MinGap / 2);
        Assert.Single(h.Sent);

        h.Advance(BulkCommandPacer.MinGap / 2);
        Assert.Equal(2, h.Sent.Count);
    }

    // A prompt can go unseen; the sweep must not stall for good.
    [Fact]
    public void WithNoAnswerForAWhile_TheWindowIsTreatedAsDrained()
    {
        Harness h = new();
        h.Pacer.Enqueue(Drops(10));
        h.Drain(10);
        Assert.Equal(BulkCommandPacer.Window, h.Sent.Count);

        h.Advance(BulkCommandPacer.AnswerTimeout);
        h.Drain(10);

        Assert.Equal(10, h.Sent.Count);
    }

    // Which command was ignored can't be told, and a second drop of a stack drops
    // another copy: nothing is re-sent, the rest waits for the game's queue to empty.
    [Fact]
    public void AfterTheGameIgnoresACommand_TheSweepPausesAndNothingIsResent()
    {
        Harness h = new();
        h.Pacer.Enqueue(Drops(10));
        h.Drain(10);

        h.Pacer.NoteRateLimited();
        h.Pacer.NotePrompt();
        h.Drain(5);
        Assert.Equal(BulkCommandPacer.Window, h.Sent.Count);

        h.Advance(BulkCommandPacer.RateLimitBackoff);
        h.Drain(10);
        Assert.Equal(10, h.Sent.Count);
        Assert.Equal(10, h.Sent.Distinct().Count());
    }

    // One sender takes back its own waiting commands; another's, and what has
    // already gone out, stay as they were and in their order.
    [Fact]
    public void AnOwnerTakesBackItsOwnWaitingCommands_AndNothingElse()
    {
        Harness h = new();
        object engine = new();
        h.Pacer.Enqueue(Enumerable.Repeat("drop dagger", 8), engine);
        h.Pacer.Enqueue(new[] { "drop moonstone", "drop dagger" });   // nobody's to take back
        h.Pacer.Enqueue(new[] { "drop ruby" }, engine);
        h.Drain(10);
        Assert.Equal(BulkCommandPacer.Window, h.Sent.Count);

        IReadOnlyList<string> taken = h.Pacer.CancelOwned(engine, command => command == "drop dagger");

        Assert.Equal(new[] { "drop dagger", "drop dagger" }, taken);
        Assert.Equal(3, h.Pacer.Pending);
        h.Advance(BulkCommandPacer.AnswerTimeout);
        h.Drain(10);
        Assert.Equal(new[] { "drop moonstone", "drop dagger", "drop ruby" }, h.Sent.Skip(BulkCommandPacer.Window));
    }
}

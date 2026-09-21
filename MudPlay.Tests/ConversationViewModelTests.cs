using System.Collections.Specialized;
using Avalonia;
using MudPlay.Game;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using MudPlay.ViewModels;
using Xunit;

namespace MudPlay.Tests;

// Once ChatHistoryStore is at its cap (a long-lived character's replayed talk log fills it at
// startup) every new chat line is an Add plus a front-trim Remove. The window used to answer the
// Remove with a full Rebuild — Rows.Clear() + re-adding thousands of rows — which reset the list's
// scroll offset, so the box jumped on every incoming line even with Auto-scroll unchecked.
public sealed class ConversationViewModelTests
{
    // ChatHistoryStore.MaxEntries is private; the tests only need to be at least this many.
    private const int StoreCap = 5_000;

    private static readonly DateTimeOffset T0 = new(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);

    private static LineExtractor.EmittedLine Line(string text, int second) =>
        new(text, new CellAttributes[text.Length], T0.AddSeconds(second), IsPromptLine: false, IsChat: true);

    private static (MessageRouter Router, ChatHistoryStore History) NewStore()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        return (router, new ChatHistoryStore(new ChatRouter(router)));
    }

    private static ConversationViewModel NewViewModel(ChatHistoryStore history, TalkSettings? talk = null)
        => new(history, new CommandHistory(), _ => { }, new Application(), talk ?? new TalkSettings(),
               new ProfileService(), new DisplayConfig());

    // Fill the store to its cap with gossip "msg 0" … "msg 4999".
    private static void FillToCap(MessageRouter router)
    {
        for (int i = 0; i < StoreCap; i++)
            router.Dispatch(Line($"Forged gossips: msg {i}", i));
    }

    [Fact]
    public void StoreAtCap_NewEntry_AddsAndTrimsInPlace_WithoutResettingRows()
    {
        (MessageRouter router, ChatHistoryStore history) = NewStore();
        FillToCap(router);
        using ConversationViewModel vm = NewViewModel(history);
        Assert.Equal(StoreCap, vm.Rows.Count);
        Assert.Equal("msg 0", vm.Rows[0].Entry.Message);

        List<NotifyCollectionChangedAction> actions = new();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        router.Dispatch(Line("Forged gossips: newest", StoreCap));

        // No Reset: Rows.Clear() is what threw the scroll offset back.
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Remove }, actions);
        Assert.Equal(StoreCap, vm.Rows.Count);
        Assert.Equal("msg 1", vm.Rows[0].Entry.Message);
        Assert.Equal("newest", vm.Rows[^1].Entry.Message);
    }

    [Fact]
    public void StoreAtCap_ManyEntries_RowsStayInStep_NeverReset()
    {
        (MessageRouter router, ChatHistoryStore history) = NewStore();
        FillToCap(router);
        using ConversationViewModel vm = NewViewModel(history);

        int resets = 0;
        vm.Rows.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };

        for (int i = 0; i < 25; i++)
            router.Dispatch(Line($"Forged gossips: new {i}", StoreCap + i));

        Assert.Equal(0, resets);
        Assert.Equal(history.Entries.Count, vm.Rows.Count);
        Assert.Equal(history.Entries[0].Message, vm.Rows[0].Entry.Message);
        Assert.Equal("new 24", vm.Rows[^1].Entry.Message);
    }

    // A trimmed entry that the channel filter had hidden never had a row — nothing to drop, and
    // still no rebuild.
    [Fact]
    public void StoreAtCap_TrimmedEntryWasFilteredOut_DropsNoRow_NoReset()
    {
        (MessageRouter router, ChatHistoryStore history) = NewStore();
        FillToCap(router);
        using ConversationViewModel vm = NewViewModel(history, new TalkSettings { ConvoShowGossip = false });
        Assert.Empty(vm.Rows);

        List<NotifyCollectionChangedAction> actions = new();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        router.Dispatch(Line("Forged telepaths: hello", StoreCap));   // shown; trims a hidden gossip

        Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, actions);
        Assert.Equal("hello", Assert.Single(vm.Rows).Entry.Message);
    }

    [Fact]
    public void StoreBelowCap_NewEntry_JustAppends()
    {
        (MessageRouter router, ChatHistoryStore history) = NewStore();
        router.Dispatch(Line("Forged gossips: one", 0));
        using ConversationViewModel vm = NewViewModel(history);

        List<NotifyCollectionChangedAction> actions = new();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        router.Dispatch(Line("Forged gossips: two", 1));

        Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, actions);
        Assert.Equal(new[] { "one", "two" }, vm.Rows.Select(r => r.Entry.Message).ToArray());
    }
}

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
[Collection(EmoteRuntimeCollection.Name)]
public sealed class ConversationViewModelTests
{
    // ChatHistoryStore.MaxEntries is private; the tests only need to be at least this many.
    private const int StoreCap = 5_000;

    // Local noon: the store adds a DaySeparator entry when the LOCAL date rolls over, and the
    // fill spans 5,000 seconds — anchored to a UTC instant it crosses midnight in UTC+11 zones,
    // which puts one entry too many in the store.
    private static readonly DateTimeOffset T0 = new(new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Local));

    private static LineExtractor.EmittedLine Line(string text, int second, bool isChat = true) =>
        new(text, new CellAttributes[text.Length], T0.AddSeconds(second), IsPromptLine: false, IsChat: isChat);

    private static (MessageRouter Router, ChatHistoryStore History) NewStore()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        return (router, new ChatHistoryStore(new ChatRouter(router)));
    }

    private static ConversationViewModel NewViewModel(ChatHistoryStore history, TalkSettings? talk = null,
                                                      Action? clearChatlog = null, DisplayConfig? display = null,
                                                      Action<Action>? post = null)
        => new(history, new CommandHistory(), _ => { }, clearChatlog ?? (() => { }), new Application(),
               talk ?? new TalkSettings(), new ProfileService(), display ?? new DisplayConfig(), post ?? (run => run()));

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

    // Bug: with Gossip hidden, an incoming gossip line is filtered out (no AddRow, so no
    // auto-scroll re-pin), but at cap it still trims the oldest entry — and when that entry
    // is a VISIBLE row, dropping it shrinks the list from the top with nothing re-pinning to
    // the bottom, so the auto-scrolling view drifts and jumps. The trim must re-pin too.
    [Fact]
    public void StoreAtCap_FilteredAddTrimsVisibleTopRow_RepinsToNewest()
    {
        (MessageRouter router, ChatHistoryStore history) = NewStore();
        // Telepath lines are shown even with Gossip hidden, so every row is visible.
        for (int i = 0; i < StoreCap; i++)
            router.Dispatch(Line($"Forged telepaths: msg {i}", i));
        using ConversationViewModel vm = NewViewModel(history, new TalkSettings { ConvoShowGossip = false });
        Assert.Equal(StoreCap, vm.Rows.Count);

        int repins = 0;
        ConversationRowViewModel? lastTarget = null;
        vm.ScrollToRowRequested += r => { repins++; lastTarget = r; };

        // Gossip line: filtered out (adds no row) but trims the oldest telepath — a visible
        // top row. Auto-scroll must re-pin to the newest visible row.
        router.Dispatch(Line("Forged gossips: newest", StoreCap));

        Assert.Equal(StoreCap - 1, vm.Rows.Count);   // trimmed a visible row, added none
        Assert.Equal(1, repins);                      // re-pinned exactly once, on the trim
        Assert.Same(vm.Rows[^1], lastTarget);         // to the newest visible row
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

    // Clear All hands the wipe to the owner of the history and its saved copy; the
    // store's reset then empties the window's rows.
    [Fact]
    public void ClearAll_RunsTheWipe_AndTheRowsEmpty()
    {
        (MessageRouter router, ChatHistoryStore history) = NewStore();
        router.Dispatch(Line("Forged gossips: one", 0));
        router.Dispatch(Line("Forged gossips: two", 1));
        int wipes = 0;
        using ConversationViewModel vm = NewViewModel(history, clearChatlog: () => { wipes++; history.Clear(); });
        Assert.Equal(2, vm.Rows.Count);

        vm.ClearAllCommand.Execute(null);

        Assert.Equal(1, wipes);
        Assert.Empty(vm.Rows);
    }

    // A yell from the next room names no one, so its row says where it came from; a
    // same-room yell, our own and the old bare form show no qualifier.
    [Theory]
    [InlineData("Someone yells from the north \"over here\"", "Someone (from the north):")]
    [InlineData("Someone yells from above \"over here\"",     "Someone (from above):")]
    [InlineData("Someone yells from below \"over here\"",     "Someone (from below):")]
    [InlineData("From Alder (Yelling): \"over here\"",        "Alder:")]
    [InlineData("Alder yells \"over here\"",                  "Alder:")]
    [InlineData("You yell \"over here\"",                     "You:")]
    public void YellRow_ShowsWhereItCameFrom_OnlyForTheNextRoomShapes(string wire, string speakerText)
    {
        (MessageRouter router, ChatHistoryStore history) = NewStore();
        router.Dispatch(Line(wire, 0));
        using ConversationViewModel vm = NewViewModel(history);

        ConversationRowViewModel row = Assert.Single(vm.Rows);
        Assert.Equal("YELL", row.ChannelText);
        Assert.Equal(speakerText, row.SpeakerText);
        Assert.Equal("over here", row.MessageText);
        Assert.EndsWith($"{speakerText} over here", row.CopyText);
    }

    // The header's filter pills and the rows' channel chips read from one table, so each pill
    // is labelled exactly like the rows it shows or hides; a telepath row only adds its arrow.
    // Every channel that shares a pill is covered: both telepath directions, realm notices and
    // PvP messages. The server's own lines (a sent-telepath confirmation, a realm notice, a PvP
    // message) are not player chat. Actions share SAY; see the test after this one.
    public static TheoryData<string, bool, string, Func<ConversationViewModel, string>> PillCases => new()
    {
        { "Forged gossips: hi",                               true,  "GOS",    vm => vm.GossipTag },
        { "Forged says \"hi\"",                               true,  "SAY",    vm => vm.LocalTag },
        { "Forged telepaths: hi",                             true,  "←TELE",  vm => vm.TelepathTag },
        { "--- Telepath sent to Forged ---",                  false, "TELE→",  vm => vm.TelepathTag },
        { "Forged gangpaths: hi",                             true,  "GANG",   vm => vm.GangpathTag },
        { "Broadcast from Forged \"hi\"",                     true,  "BCAST",  vm => vm.BroadcastTag },
        { "Forged yells \"hi\"",                              true,  "YELL",   vm => vm.YellTag },
        { "Raijin just entered the Realm.",                   false, "SERVER", vm => vm.RealmTag },
        { "Server PvP Message: Balgor just killed Fizznod!",  false, "SERVER", vm => vm.RealmTag },
    };

    [Theory]
    [MemberData(nameof(PillCases))]
    public void FilterPill_IsLabelledLikeTheRowsItFilters(string wire, bool isChat, string rowTag,
                                                          Func<ConversationViewModel, string> pill)
    {
        (MessageRouter router, ChatHistoryStore history) = NewStore();
        router.Dispatch(Line(wire, 0, isChat));
        using ConversationViewModel vm = NewViewModel(history);

        ConversationRowViewModel row = Assert.Single(vm.Rows);
        Assert.Equal(rowTag, row.ChannelText);
        Assert.Equal(rowTag.Trim('←', '→'), pill(vm));
    }

    // An action rides the SAY pill. It reaches the window only off the screen (green, then its
    // prompt), so it goes in through the terminal feed.
    [Fact]
    public void FilterPill_Say_IsLabelledLikeAnActionRow()
    {
        (MessageRouter router, ChatHistoryStore history) = NewStore();
        ServerFeed feed = new(router);
        feed.Raw("[HP=100/MA=50]:act\r\n\u001b[0;32mYou laugh!\r\n\u001b[0;37m[HP=100/MA=50]:");
        using ConversationViewModel vm = NewViewModel(history);

        ConversationRowViewModel row = Assert.Single(vm.Rows);
        Assert.Equal(ChatChannel.Social, row.Entry.Channel);
        Assert.Equal("SAY", row.ChannelText);
        Assert.Equal(row.ChannelText, vm.LocalTag);
    }

    [Fact]
    public void YellRow_NextRoomYell_StaysUnderTheYellFilterAndFoundBySearch()
    {
        (MessageRouter router, ChatHistoryStore history) = NewStore();
        router.Dispatch(Line("Someone yells from the north \"over here\"", 0));
        router.Dispatch(Line("Forged gossips: elsewhere", 1));
        using ConversationViewModel vm = NewViewModel(history);
        Assert.Equal(2, vm.Rows.Count);

        vm.SearchText = "over here";
        Assert.Equal(ChatChannel.Yell, Assert.Single(vm.Rows).Entry.Channel);

        vm.SearchText = "Someone";
        Assert.Equal(ChatChannel.Yell, Assert.Single(vm.Rows).Entry.Channel);

        vm.SearchText = string.Empty;
        vm.ShowYell = false;
        Assert.Equal(ChatChannel.Gossip, Assert.Single(vm.Rows).Entry.Channel);
    }

    // Rows carry their typography (the row template binds nothing off the window's
    // view-model). A Talk Apply sets the font, size, colours and emote flag one after
    // another on the Display channel; the open window restyles once after the last of
    // them, in place (Replace, never a Reset), so the reader keeps their place, and a
    // line arriving afterwards gets the new look too.
    [Fact]
    public void TalkApply_RestylesEveryRowOnceInPlace()
    {
        (MessageRouter router, ChatHistoryStore history) = NewStore();
        router.Dispatch(Line("Forged gossips: one", 0));
        router.Dispatch(Line("Forged gossips: two", 1));
        DisplayConfig display = new();
        Queue<Action> posted = new();
        using ConversationViewModel vm = NewViewModel(history, display: display, post: posted.Enqueue);
        ConversationRowLook before = vm.Rows[0].Look;
        Assert.Equal(12 * 96.0 / 72.0, before.MessageFontSize, 6);
        Assert.True(before.EmotesEnabled);

        List<NotifyCollectionChangedAction> actions = new();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);
        // The order TalkSectionViewModel's Apply writes them in.
        display.ConvoFontFamily = "";
        display.ConvoFontSize = 15;
        display.ConvoChannelColors = new Dictionary<string, ChannelColor>();
        display.ConvoShowEmotes = false;

        Assert.Single(posted);
        Assert.Empty(actions);
        posted.Dequeue()();

        Assert.Equal(new[] { NotifyCollectionChangedAction.Replace, NotifyCollectionChangedAction.Replace }, actions);
        router.Dispatch(Line("Forged gossips: three", 2));
        Assert.Equal(new[] { "one", "two", "three" }, vm.Rows.Select(r => r.Entry.Message));
        foreach (ConversationRowViewModel row in vm.Rows)
        {
            Assert.Equal(15 * 96.0 / 72.0, row.Look.MessageFontSize, 6);
            Assert.Equal(14 * 96.0 / 72.0, row.Look.MetaFontSize, 6);
            Assert.Equal(row.Look.MessageFontSize * 1.45, row.Look.MessageLineHeight, 6);
            Assert.False(row.Look.EmotesEnabled);
        }
    }

    // The backlog a profile load replays from its character's talk log
    // (SessionLogService, opened as the profile loads) is what the window opens on: a
    // row per line, in the log's order, reading as the line was written
    // (Hang-20261026-215238 opened on 2,000 of them). A rebuild of the whole list
    // asks to scroll once, to the newest row, not once per row.
    [Fact]
    public void TalkLogBacklog_OpensAsOneRowPerLine_InOrder_AndARebuildScrollsOnce()
    {
        ScratchRoot.Require();
        string board = "convo-test-" + Guid.NewGuid().ToString("N");
        const string character = "Backlog";
        ChatChannel[] channels = { ChatChannel.Gossip, ChatChannel.Local, ChatChannel.TelepathIncoming,
                                   ChatChannel.Gangpath, ChatChannel.Broadcast, ChatChannel.Yell, ChatChannel.Social };
        string talkLog = Path.Combine(AppPaths.LogsDir, $"{character}.{board}.talk.log");
        try
        {
            ProfileService profile = TestProfiles.Loaded();
            profile.SaveAs(board, character);
            List<string> log = new();
            for (int i = 0; i < 2_000; i++)
            {
                ChatChannel channel = channels[i % channels.Length];
                string? speaker = channel == ChatChannel.Social ? null : $"Speaker{i % 7}";
                string message = $"line {i}: see https://example.org/{i} :)";
                log.Add(SessionLogService.FormatChatLine(new ChatLogEntry(T0.AddSeconds(i), channel, speaker, message, message)));
            }
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.WriteAllLines(talkLog, log);

            (MessageRouter router, ChatHistoryStore history) = NewStore();
            ChatRouter chat = new(router);
            using SessionLogService sessionLog = new(profile, chat, history, new Game.Cash.TransactionHistoryTracker(),
                                                     new LogService(), () => new TalkSettings());
            using ConversationViewModel vm = NewViewModel(history);

            Assert.Equal(2_000, vm.Rows.Count);
            for (int i = 0; i < 2_000; i++)
            {
                ConversationRowViewModel row = vm.Rows[i];
                ChatChannel channel = channels[i % channels.Length];
                Assert.Equal(channel, row.Entry.Channel);
                Assert.Equal($"line {i}: see https://example.org/{i} :)", row.MessageText);
                Assert.Equal(T0.AddSeconds(i).ToLocalTime().ToString("HH:mm:ss"), row.TimestampText);
                Assert.Equal(channel == ChatChannel.Social ? "" : $"Speaker{i % 7}:", row.SpeakerText);
            }

            List<ConversationRowViewModel> scrolledTo = new();
            vm.ScrollToRowRequested += scrolledTo.Add;
            vm.SearchText = "line";   // every line matches: the whole list again
            Assert.Equal(2_000, vm.Rows.Count);
            Assert.Same(vm.Rows[^1], Assert.Single(scrolledTo));
        }
        finally
        {
            try
            {
                if (File.Exists(talkLog)) File.Delete(talkLog);
                if (Directory.Exists(AppPaths.BbsFolder(board))) Directory.Delete(AppPaths.BbsFolder(board), recursive: true);
            }
            catch (IOException) { /* best-effort temp cleanup */ }
        }
    }
}

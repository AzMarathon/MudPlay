using MudPlay.Game;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

public sealed class ChatRouterTests
{
    private static LineExtractor.EmittedLine Line(string text) =>
        new(text, new CellAttributes[text.Length], DateTimeOffset.UnixEpoch, IsPromptLine: false);

    /// <summary>
    /// Build a router with the default catalog seeded and a ChatRouter
    /// wired to it; collect every EntryClassified event into a list for
    /// assertions.
    /// </summary>
    private static (MessageRouter router, ChatRouter chat, List<ChatLogEntry> entries) Setup(
        Func<bool>? isParadigmRealm = null)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router, isParadigmRealm);
        List<ChatLogEntry> entries = new();
        chat.EntryClassified += entries.Add;
        return (router, chat, entries);
    }

    [Fact]
    public void Gossip_ClassifiesWithSpeakerAndMessage()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged gossips: hello world"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Gossip, e.Channel);
        Assert.Equal("Forged",           e.Speaker);
        Assert.Equal("hello world",      e.Message);
    }

    [Fact]
    public void TelepathIn_ClassifiesAsTelepathIncoming()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged telepaths: pst!"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.TelepathIncoming, e.Channel);
        Assert.Equal("Forged", e.Speaker);
        Assert.Equal("pst!",   e.Message);
    }

    [Fact]
    public void TelepathOut_ClassifiesAsOutgoingWithRecipientAsSpeaker()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("--- Telepath sent to Goblin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.TelepathOutgoing, e.Channel);
        Assert.Equal("Goblin", e.Speaker);
        Assert.Equal(string.Empty, e.Message);
    }

    [Fact]
    public void TelepathOut_TypedLineOnScreen_AttributesMessage()
    {
        var (router, _, entries) = Setup();
        // The typed /-line renders on the terminal and is sniffed off the screen.
        router.Dispatch(Line("/Goblin hello there"));
        router.Dispatch(Line("--- Telepath sent to Goblin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.TelepathOutgoing, e.Channel);
        Assert.Equal("Goblin",      e.Speaker);
        Assert.Equal("hello there",  e.Message);
    }

    [Fact]
    public void TelepathOut_EngineOutboundBurst_AttributesMessage()
    {
        var (router, chat, entries) = Setup();
        // An engine broadcast (@wealth toll probe) never touches the screen —
        // the raw "/<recipient> <atCommand>\r" burst must attribute the entry.
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("/Raijin @wealth\r"));
        router.Dispatch(Line("--- Telepath sent to Raijin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.TelepathOutgoing, e.Channel);
        Assert.Equal("Raijin",  e.Speaker);
        Assert.Equal("@wealth", e.Message);
    }

    [Fact]
    public void ObserveOutbound_NonTelepathBytes_LeaveMessageEmpty()
    {
        var (router, chat, entries) = Setup();
        // A plain movement burst isn't a /-telepath — it must not seed a message.
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes("north\r"));
        router.Dispatch(Line("--- Telepath sent to Raijin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(string.Empty, e.Message);
    }

    [Fact]
    public void Yell_SelfHasNoSpeaker()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"You yell ""help""")) ;

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Yell, e.Channel);
        Assert.Null(e.Speaker);
        Assert.Equal("help", e.Message);
    }

    [Fact]
    public void Yell_OtherHasSpeaker()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"Forged yells ""ouch"""));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Yell, e.Channel);
        Assert.Equal("Forged", e.Speaker);
        Assert.Equal("ouch",   e.Message);
    }

    [Fact]
    public void Say_OtherClassifiesAsLocal()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"Forged says ""hi there"""));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Local, e.Channel);
        Assert.Equal("Forged",   e.Speaker);
        Assert.Equal("hi there", e.Message);
        Assert.Null(e.DirectedTo);   // undirected
    }

    [Fact]
    public void Say_Directed_CapturesTarget_EvenAsAThirdParty()
    {
        // A directed say is seen by the target AND third parties in the room; capture
        // the named target so the window shows "Suijin (to Fujin): hi".
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"Suijin says (to Fujin) ""hi"""));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Local, e.Channel);
        Assert.Equal("Suijin", e.Speaker);
        Assert.Equal("hi",     e.Message);
        Assert.Equal("Fujin",  e.DirectedTo);
    }

    [Fact]
    public void Say_DirectedToUs_CapturesYouAsTarget()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"Raijin says (to you) ""hi"""));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal("Raijin", e.Speaker);
        Assert.Equal("you",    e.DirectedTo);
    }

    [Fact]
    public void DirectedSayOut_TypedLine_AttributesMessageAndTarget()
    {
        // Our own outgoing directed say: the typed ">Target msg" carries the message,
        // the "--- Message Directed to Target ---" confirmation the target. Speaker is
        // null (us) so it renders "You (to Fujin): hi".
        var (router, _, entries) = Setup();
        router.Dispatch(Line(">fujin hi"));
        router.Dispatch(Line("--- Message Directed to Fujin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Local, e.Channel);
        Assert.Null(e.Speaker);        // us -> "You"
        Assert.Equal("hi",    e.Message);
        Assert.Equal("Fujin", e.DirectedTo);
    }

    [Fact]
    public void DirectedSayOut_EngineOutboundBurst_AttributesMessage()
    {
        // A gate-wrapped @-command reply never renders on-screen, so the message is
        // captured from the outbound bytes instead of the terminal line.
        var (router, chat, entries) = Setup();
        chat.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes(">Raijin {HP 100/100}\r"));
        router.Dispatch(Line("--- Message Directed to Raijin ---"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal("{HP 100/100}", e.Message);
        Assert.Equal("Raijin",       e.DirectedTo);
    }

    [Fact]
    public void DirectedSay_ToYou_ClassifiesAsLocalWithSpeaker()
    {
        // Report 225011: a directed reply ("Tristian says (to you) ""…""")
        // was dropped entirely because the (to you) clause broke the say
        // regex. It must land in the say channel like any other say.
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"Tristian says (to you) ""{Yes: 1.}"""));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Local, e.Channel);
        Assert.Equal("Tristian", e.Speaker);
        Assert.Equal("{Yes: 1.}", e.Message);
    }

    [Fact]
    public void DirectedSay_SelfToOther_HasNoSpeaker()
    {
        // Own directed say keeps "You" out of the speaker group (so it's
        // never mistaken for an inbound @-command), same as a plain say.
        var (router, _, entries) = Setup();
        router.Dispatch(Line(@"You say (to Tristian) ""@have rope"""));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Local, e.Channel);
        Assert.Null(e.Speaker);
        Assert.Equal("@have rope", e.Message);
    }

    [Fact]
    public void PlayerEnters_ClassifiesAsRealmEvent()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged just entered the Realm."));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.RealmEvent, e.Channel);
        Assert.Equal("Forged",               e.Speaker);
        Assert.Equal("entered the Realm",    e.Message);
    }

    [Fact]
    public void BuiltInDisconnect_ClassifiesAsRealmEvent()
    {
        // The built-in pattern requires the trailing period after "!!!".
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged just disconnected!!!."));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.RealmEvent, e.Channel);
        Assert.Equal("Forged",               e.Speaker);
        Assert.Equal("disconnected",         e.Message);
    }

    [Fact]
    public void CustomDisconnectLine_NonStandardForm_ClassifiesAsRealmEvent()
    {
        // A board whose logoff line has no trailing period never matches the
        // built-in pattern; the configured custom pattern must classify it so the
        // conversation window logs the disconnect.
        var (router, chat, entries) = Setup();
        chat.DisconnectPatternProvider = () => "{name} just disconnected!!!";
        router.Dispatch(Line("Forged just disconnected!!!"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.RealmEvent, e.Channel);
        Assert.Equal("Forged",               e.Speaker);
        Assert.Equal("disconnected",         e.Message);
    }

    [Fact]
    public void CustomDisconnectLine_MultiWordName_CapturesFullName()
    {
        var (router, chat, entries) = Setup();
        chat.DisconnectPatternProvider = () => "{name} just disconnected!!!";
        router.Dispatch(Line("MudPlay WuzHere just disconnected!!!"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.RealmEvent, e.Channel);
        Assert.Equal("MudPlay WuzHere",        e.Speaker);
        Assert.Equal("disconnected",         e.Message);
    }

    [Fact]
    public void NoCustomPattern_NonStandardDisconnect_Ignored()
    {
        // No custom pattern configured + a line the built-in can't match (no
        // trailing period) → nothing classified.
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged just disconnected!!!"));
        Assert.Empty(entries);
    }

    [Fact]
    public void ServerPvp_KillOnParadigmRealm_ClassifiesAsServerWithFullBodyMessage()
    {
        var (router, _, entries) = Setup(isParadigmRealm: () => true);
        router.Dispatch(Line("Server PvP Message: Balgor just killed Fizznod!"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Server, e.Channel);
        Assert.Null(e.Speaker);
        Assert.Equal("Balgor just killed Fizznod!", e.Message);
    }

    [Fact]
    public void ServerPvp_NonKillBody_ClassifiesGenericallyByPrefix()
    {
        // The "Server PvP Message: " prefix covers PvP events beyond kills; any
        // body after it must classify as Server, not just the "just killed" form.
        var (router, _, entries) = Setup(isParadigmRealm: () => true);
        router.Dispatch(Line("Server PvP Message: Balgor has declared war on Fizznod!"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Server, e.Channel);
        Assert.Null(e.Speaker);
        Assert.Equal("Balgor has declared war on Fizznod!", e.Message);
    }

    [Fact]
    public void ServerPvp_OnNonParadigmRealm_IsSuppressed()
    {
        var (router, _, entries) = Setup(isParadigmRealm: () => false);
        router.Dispatch(Line("Server PvP Message: Balgor just killed Fizznod!"));

        Assert.Empty(entries);
    }

    [Fact]
    public void ServerPvp_NullGate_ClassifiesAsServer()
    {
        // Default Setup passes no gate (null) — the channel is open so bare-router
        // tests still see the entry.
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Server PvP Message: Balgor just killed Fizznod!"));

        ChatLogEntry e = Assert.Single(entries);
        Assert.Equal(ChatChannel.Server, e.Channel);
    }

    [Fact]
    public void Dispose_UnsubscribesAllHandlers()
    {
        var (router, chat, entries) = Setup();
        chat.Dispose();
        router.Dispatch(Line("Forged gossips: hi"));
        Assert.Empty(entries);
        Assert.Equal(0, router.SubscriptionCount);
    }

    [Fact]
    public void NonChatLines_AreIgnored()
    {
        var (router, _, entries) = Setup();
        router.Dispatch(Line("Forged slashes Goblin for 17 damage!"));
        router.Dispatch(Line("Obvious exits: north, south"));
        Assert.Empty(entries);
    }
}

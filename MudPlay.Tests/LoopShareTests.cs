using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Remote;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// `@loop send`: the codec round-trips a loop through chat-safe chunks and refuses
// hostile payloads; the sender offers, then sends on yes / drops on no; the requester
// reassembles and saves without clobbering a loop of its own.
public sealed class LoopShareTests : IDisposable
{
    // Loops persist under the set's Loops/ folder in the real app data root (AppPaths
    // caches it at static init), so each test uses its own set name and deletes it.
    private readonly string _setName = "test-set-" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _bbs = "test-loopshare-" + Guid.NewGuid().ToString("N")[..12];
    private DateTimeOffset _now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        foreach (string folder in new[] { Path.Combine(AppPaths.GameDataRoot, _setName), AppPaths.BbsFolder(_bbs) })
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch { /* best-effort cleanup of the per-test set and its one profile */ }
        }
    }

    private LoopManager NewLoops()
    {
        string setRoot = Path.Combine(AppPaths.GameDataRoot, _setName);
        Directory.CreateDirectory(setRoot);
        File.WriteAllText(Path.Combine(setRoot, "Rooms.json"), "[]");
        GameDataCache cache = new();
        cache.SwitchSet(_setName);
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged(_setName);
        LoopManager loops = new(new BfsMapper(graph), graph);
        loops.LoadAll(_setName);
        return loops;
    }

    private static Loop SampleLoop(string name = "King's Road") => new(name, new[]
    {
        new LoopWaypoint(new RoomKey(1, 297), command: "ask barmaid ale", delayMs: 1500),
        new LoopWaypoint(new RoomKey(1, 298), doNotRest: true),
        new LoopWaypoint(new RoomKey(2, 14), doNotAttack: true),
    })
    {
        Notes = "watch the bridge", OnlyAttackInLairRooms = true, Favorite = true, SharedFavorite = true,
        LairEntryDebuff = LairEntryDebuffMode.BlockSpells,
    };

    // ----- Codec -----------------------------------------------------

    [Fact]
    public void Codec_RoundTripsTheRoute_ButNotFavourite()
    {
        Loop decoded = LoopShareCodec.Decode(LoopShareCodec.Encode(SampleLoop()));

        Assert.Equal("King's Road", decoded.Name);
        Assert.Equal("watch the bridge", decoded.Notes);
        Assert.True(decoded.OnlyAttackInLairRooms);
        Assert.Equal(LairEntryDebuffMode.BlockSpells, decoded.LairEntryDebuff);
        Assert.False(decoded.Favorite);   // the receiver's own choice
        Assert.False(decoded.SharedFavorite);
        Assert.Equal(new[] { "1/297", "1/298", "2/14" }, decoded.Waypoints.Select(w => w.Room));
        Assert.Equal("ask barmaid ale", decoded.Waypoints[0].Command);
        Assert.Equal(1500, decoded.Waypoints[0].DelayMs);
        Assert.True(decoded.Waypoints[1].DoNotRest);
        Assert.True(decoded.Waypoints[2].DoNotAttack);
    }

    [Fact]
    public void Codec_RoundTripsThePriorityBuffWait_AsTheNumberThree()
    {
        Loop loop = SampleLoop();
        loop.LairEntryDebuff = LairEntryDebuffMode.WaitForPriorityBuffs;

        IReadOnlyList<string> chunks = LoopShareCodec.Encode(loop);

        Assert.Contains("\"ld\":3", Unpack(string.Concat(chunks)));
        Assert.Equal(LairEntryDebuffMode.WaitForPriorityBuffs, LoopShareCodec.Decode(chunks).LairEntryDebuff);
    }

    // A way of waiting this client doesn't know (one a newer client added) arrives as
    // off: the loop is still received and runs, without the wait. A client from before
    // the priority-buff wait reads the 3 the same way.
    [Fact]
    public void Codec_UnknownLairEntryDebuffValue_DecodesAsOff_AndKeepsTheLoop()
    {
        string blob = Pack("{\"n\":\"x\",\"w\":[{\"r\":\"1/1\"},{\"r\":\"1/2\"}],\"ld\":99}");

        Loop decoded = LoopShareCodec.Decode(new[] { blob });

        Assert.Equal(LairEntryDebuffMode.Off, decoded.LairEntryDebuff);
        Assert.Equal(2, decoded.Waypoints.Count);
    }

    // The way of waiting is the character's own for each loop now, not the loop
    // file's: a received loop's mode is kept on the receiving character's profile,
    // and the file, which every character on the game data reads, holds off.
    [Fact]
    public void Receive_PriorityBuffWaitLoop_IsSavedWithItsMode_AsTheReceivingCharactersOwn()
    {
        var (receiver, loops, _) = NewReceiver();
        Directory.CreateDirectory(AppPaths.ProfileFolder(_bbs, "Receiver"));
        File.WriteAllText(AppPaths.CharacterProfileFile(_bbs, "Receiver"), """{ "Name": "Receiver", "SchemaVersion": 8 }""");
        ProfileService profile = new();
        LoopSettingsStore settings = new(profile, loops);
        profile.Load(_bbs, "Receiver");
        Loop sent = SampleLoop();
        sent.LairEntryDebuff = LairEntryDebuffMode.WaitForPriorityBuffs;

        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(sent)) receiver.Ingest(Tele("Raijin", line));

        Assert.Equal(LairEntryDebuffMode.WaitForPriorityBuffs, loops.Get("King's Road")!.LairEntryDebuff);
        Assert.True(settings.HasOwn("King's Road"));
        Assert.Equal("WaitForPriorityBuffs", profile.Current!.LoopSettings!["King's Road"].LairEntryDebuff);

        // Read again from what was saved: the mode comes back from the profile, and
        // the loop file holds none.
        LoopManager reread = NewLoops();
        Assert.Equal(LairEntryDebuffMode.Off, reread.Get("King's Road")!.SharedLairEntryDebuff);
        ProfileService profileAgain = new();
        _ = new LoopSettingsStore(profileAgain, reread);
        profileAgain.Load(_bbs, "Receiver");
        Assert.Equal(LairEntryDebuffMode.WaitForPriorityBuffs, reread.Get("King's Road")!.LairEntryDebuff);
    }

    [Fact]
    public void Codec_BigLoop_SplitsIntoChatSizedChunks_AndReassembles()
    {
        Loop big = new("Grand tour", Enumerable.Range(1, 400)
            .Select(i => new LoopWaypoint(new RoomKey(1 + i % 7, i * 13), command: $"search {i}")));

        IReadOnlyList<string> chunks = LoopShareCodec.Encode(big);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, LoopShareCodec.MaxChunkChars));
        Assert.Equal(400, LoopShareCodec.Decode(chunks).Waypoints.Count);
    }

    [Fact]
    public void Codec_Garbage_IsAFormatException()
    {
        Assert.Throws<FormatException>(() => LoopShareCodec.Decode(new[] { "not*base64!" }));
        Assert.Throws<FormatException>(() => LoopShareCodec.Decode(new[] { "AAAA" }));
    }

    [Fact]
    public void Codec_OneRoomLoop_Refused()
    {
        string blob = Pack("{\"n\":\"x\",\"w\":[{\"r\":\"1/1\"}]}");
        Assert.Throws<FormatException>(() => LoopShareCodec.Decode(new[] { blob }));
    }

    [Fact]
    public void Codec_DeflateBomb_Refused()
    {
        string blob = Pack("{\"n\":\"" + new string('a', 2_000_000) + "\"}");
        Assert.True(blob.Length < 10_000);   // tiny on the wire…
        Assert.Throws<FormatException>(() => LoopShareCodec.Decode(new[] { blob }));   // …huge inflated
    }

    private static string Pack(string json)
    {
        using MemoryStream packed = new();
        using (DeflateStream d = new(packed, CompressionLevel.SmallestSize, leaveOpen: true))
            d.Write(Encoding.UTF8.GetBytes(json));
        return System.Buffers.Text.Base64Url.EncodeToString(packed.ToArray());
    }

    private static string Unpack(string blob)
    {
        using DeflateStream inflate = new(
            new MemoryStream(System.Buffers.Text.Base64Url.DecodeFromChars(blob)), CompressionMode.Decompress);
        using StreamReader reader = new(inflate, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ----- Sender (LoopShareHandler) ---------------------------------

    // Refusals land in the same list, as they do from an engine with the Warn
    // setting on.
    private static RemoteCommandContext Ctx(string sender, List<string> replies) =>
        new(sender, "@loop", Array.Empty<string>(), "@loop send", RemoteChannel.Telepath, replies.Add)
        {
            Refusal = why => replies.Add(why ?? "command invalid or not allowed"),
        };

    [Fact]
    public void Send_OffersTheBestMatch_ThenSendsOnYes()
    {
        LoopManager loops = NewLoops();
        loops.Save(SampleLoop());
        LoopShareHandler share = new(loops, clock: () => _now);
        List<string> replies = new();

        share.OnSend(Ctx("Raijin", replies), "kings road");
        Assert.Equal($"preparing to send: King's Road, yes to confirm, no to deny (code {LoopSignature.ShortOf(SampleLoop())})",
            replies.Single());

        replies.Clear();
        share.OnSend(Ctx("Raijin", replies), "yes");
        Assert.StartsWith("sending loop 'King's Road' (3 rooms, ", replies[0]);
        List<string> data = replies.Skip(1).ToList();
        Assert.All(data, l => Assert.StartsWith(LoopShareHandler.DataToken + " ", l));

        // The data lines decode back to the loop.
        Loop decoded = LoopShareCodec.Decode(data.Select(l => l.Split(' ')[3]));
        Assert.Equal("King's Road", decoded.Name);
    }

    [Fact]
    public void Send_No_Cancels_AndALaterYesHasNothingPending()
    {
        LoopManager loops = NewLoops();
        loops.Save(SampleLoop());
        LoopShareHandler share = new(loops, clock: () => _now);
        List<string> replies = new();

        share.OnSend(Ctx("Raijin", replies), "king");
        share.OnSend(Ctx("Raijin", replies), "no");
        share.OnSend(Ctx("Raijin", replies), "yes");

        Assert.Equal("loop send of 'King's Road' cancelled", replies[1]);
        Assert.Equal("no loop send pending - use @loop send <name> first", replies[2]);
    }

    [Fact]
    public void Send_StaleOffer_Lapses()
    {
        LoopManager loops = NewLoops();
        loops.Save(SampleLoop());
        LoopShareHandler share = new(loops, clock: () => _now);
        List<string> replies = new();

        share.OnSend(Ctx("Raijin", replies), "king");
        _now += TimeSpan.FromMinutes(5);
        share.OnSend(Ctx("Raijin", replies), "yes");

        Assert.Equal("no loop send pending - use @loop send <name> first", replies[1]);
    }

    [Fact]
    public void Send_OfferIsPerRequester()
    {
        LoopManager loops = NewLoops();
        loops.Save(SampleLoop());
        LoopShareHandler share = new(loops, clock: () => _now);
        List<string> replies = new();

        share.OnSend(Ctx("Raijin", replies), "king");
        share.OnSend(Ctx("Suijin", replies), "yes");   // someone else can't confirm Raijin's offer

        Assert.Equal("no loop send pending - use @loop send <name> first", replies[1]);
    }

    [Fact]
    public void Send_MissingOrAmbiguousName_Replies()
    {
        LoopManager loops = NewLoops();
        loops.Save(SampleLoop("Sewer loop east"));
        loops.Save(SampleLoop("Sewer loop west"));
        LoopShareHandler share = new(loops, clock: () => _now);
        List<string> replies = new();

        share.OnSend(Ctx("Raijin", replies), "dragon");
        share.OnSend(Ctx("Raijin", replies), "sewer");
        share.OnSend(Ctx("Raijin", replies), "");

        Assert.Equal("no saved loop named 'dragon'", replies[0]);
        Assert.StartsWith("'sewer' matches 2 loops:", replies[1]);
        Assert.Equal("not running a loop - @loop send <name> for a saved one", replies[2]);
    }

    [Fact]
    public void Send_Bare_OffersTheRunningLoop_ThenSendsOnYes()
    {
        LoopManager loops = NewLoops();
        Loop running = SampleLoop("Ad-hoc grind");
        LoopShareHandler share = new(loops, clock: () => _now, runningLoop: () => running);
        List<string> replies = new();

        share.OnSend(Ctx("Raijin", replies), "");
        Assert.Equal($"preparing to send: Ad-hoc grind, yes to confirm, no to deny (code {LoopSignature.ShortOf(running)})",
            replies.Single());

        replies.Clear();
        share.OnSend(Ctx("Raijin", replies), "yes");
        Assert.StartsWith("sending loop 'Ad-hoc grind' (3 rooms, ", replies[0]);
    }

    // ----- Receiver (LoopShareReceiver) ------------------------------

    private (LoopShareReceiver Receiver, LoopManager Loops, List<string> Notices) NewReceiver()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        LoopManager loops = NewLoops();
        List<string> notices = new();
        return (new LoopShareReceiver(new ChatRouter(router), loops, notices.Add, clock: () => _now), loops, notices);
    }

    private static List<string> DataLines(Loop loop, string id = "a1b2")
    {
        IReadOnlyList<string> chunks = LoopShareCodec.Encode(loop);
        return chunks.Select((c, i) => $"{{{LoopShareHandler.DataToken} {id} {i + 1}/{chunks.Count} {c}}}").ToList();
    }

    private static ChatLogEntry Tele(string sender, string msg) =>
        new(DateTimeOffset.UnixEpoch, ChatChannel.TelepathIncoming, sender, msg, $"{sender} telepaths: {msg}");

    // What a notice says of a loop whose offer carried no code: none of these
    // receiver tests sends an offer line unless it says so.
    private const string NoCodeOffered = "not verified: no code came with the offer";

    // The sender's offer as it reaches the asker, braces and all.
    private static ChatLogEntry Offer(string sender, Loop loop, string? code = null) =>
        Tele(sender, "{" + LoopShareHandler.OfferLine(loop.Name, code ?? LoopSignature.ShortOf(loop)) + "}");

    // ----- the code on the offer, and the loop checked against it ----------

    [Fact]
    public void Offer_CarriesTheCode_AndReadsBack()
    {
        string line = LoopShareHandler.OfferLine("King's Road", "9da659db");

        Assert.Equal("preparing to send: King's Road, yes to confirm, no to deny (code 9da659db)", line);
        Assert.True(LoopShareHandler.TryParseOffer("{" + line + "}", out string? code));
        Assert.Equal("9da659db", code);

        // A loop named to look like the rest of the line changes nothing: the code
        // is what stands at the very end.
        string tricky = LoopShareHandler.OfferLine("x, yes to confirm, no to deny (code 00000000)", "9da659db");
        Assert.True(LoopShareHandler.TryParseOffer(tricky, out code));
        Assert.Equal("9da659db", code);
    }

    [Theory]
    [InlineData("{preparing to send: King's Road, yes to confirm, no to deny}")]              // an older client
    [InlineData("{preparing to send: King's Road, yes to confirm, no to deny (code 9da6}")]  // cut short
    [InlineData("{preparing to send: King's Road, yes to confirm, no to deny (code 9da6)}")] // too short
    [InlineData("{preparing to send: King's Road, yes to confirm, no to deny (code 9da659dz)}")]
    public void Offer_WithNoWholeCode_IsAnOfferWithNone(string line)
    {
        Assert.True(LoopShareHandler.TryParseOffer(line, out string? code));
        Assert.Null(code);
    }

    [Theory]
    [InlineData("{sending loop 'King's Road' (3 rooms, 1 line)}")]
    [InlineData("preparing to send a letter")]
    [InlineData("{@loopdata a1b2 1/1 abc}")]
    public void OtherLines_AreNotOffers(string line) =>
        Assert.False(LoopShareHandler.TryParseOffer(line, out _));

    // One telepath has to hold the offer whole, code included, or the asker's
    // client sees no code. With the longest name a loop can be sent under, the
    // line in its braces leaves room for the "/<name> " it is addressed with.
    [Fact]
    public void Offer_ForTheLongestLoopName_FitsOneTelepath()
    {
        string line = LoopShareHandler.OfferLine(new string('n', 120), "9da659db");

        Assert.True(line.Length + 2 <= 225 - 25, $"the offer is {line.Length + 2} characters in its braces");
    }

    [Fact]
    public void Receive_LoopThatMatchesTheOfferedCode_IsSavedAsVerified()
    {
        var (receiver, loops, notices) = NewReceiver();
        Loop sent = SampleLoop();

        receiver.Ingest(Offer("Raijin", sent));
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(sent)) receiver.Ingest(Tele("Raijin", line));

        Assert.NotNull(loops.Get("King's Road"));
        Assert.Equal($"[Received loop 'King's Road' 3 rooms from Raijin ({LoopSignature.ShortOf(sent)}, verified)]",
            notices.Single());
    }

    // What arrived is not what was offered: a damaged transfer, or the lines of
    // another. It is not saved.
    [Fact]
    public void Receive_LoopThatDoesNotMatchTheOfferedCode_IsNotSaved()
    {
        var (receiver, loops, notices) = NewReceiver();
        Loop offered = SampleLoop();
        Loop arrived = SampleLoop();
        arrived.Waypoints[0].DelayMs += 1;

        receiver.Ingest(Offer("Raijin", offered));
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(arrived)) receiver.Ingest(Tele("Raijin", line));

        Assert.Empty(loops.Loops);
        Assert.Equal($"[Received loop 'King's Road' 3 rooms from Raijin does not match the loop offered "
            + $"(received {LoopSignature.ShortOf(arrived)}, offered {LoopSignature.ShortOf(offered)}), not saved]", notices.Single());
    }

    // Each player's loop is checked against that player's own offer, and an offer
    // answers one transfer.
    [Fact]
    public void Receive_ChecksAgainstTheSendersOwnOffer_Once()
    {
        var (receiver, loops, notices) = NewReceiver();
        Loop theirs = SampleLoop("Theirs"), others = SampleLoop("Others");
        others.Waypoints[0].DelayMs += 1;

        receiver.Ingest(Offer("Suijin", others));
        receiver.Ingest(Offer("Raijin", theirs));
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(theirs)) receiver.Ingest(Tele("Raijin", line));
        Assert.EndsWith(", verified)]", notices.Single());

        // The same player sends again with no new offer: nothing to check it by.
        notices.Clear();
        theirs.Name = "Theirs again";
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(theirs, id: "c3d4")) receiver.Ingest(Tele("Raijin", line));
        Assert.EndsWith($", {NoCodeOffered})]", notices.Single());
        Assert.Equal(2, loops.Loops.Count);
    }

    // A loop from a later version with a way of waiting this client doesn't know is
    // still taken, with the wait off, as it always was. It can't match the code, and
    // isn't held to it.
    [Fact]
    public void Receive_LoopWithALairWaitThisClientDoesNotKnow_IsSavedUnverified()
    {
        var (receiver, loops, notices) = NewReceiver();
        string blob = Pack("{\"n\":\"Later\",\"w\":[{\"r\":\"1/1\"},{\"r\":\"1/2\"}],\"ld\":99}");

        receiver.Ingest(Tele("Raijin", "{" + LoopShareHandler.OfferLine("Later", "0123abcd") + "}"));
        receiver.NoteSendConfirmed("Raijin");
        receiver.Ingest(Tele("Raijin", $"{{{LoopShareHandler.DataToken} a1b2 1/1 {blob}}}"));

        Assert.Equal(LairEntryDebuffMode.Off, loops.Get("Later")!.LairEntryDebuff);
        Assert.Contains("not verified: its lair wait is one this client does not know", notices.Single());
    }

    // The asker said yes to the loop as it was offered. Changed since, it is not
    // sent: the asker's client would turn it away as not the loop offered.
    [Fact]
    public void Send_LoopChangedSinceItWasOffered_IsNotSent()
    {
        LoopManager loops = NewLoops();
        loops.Save(SampleLoop());
        LoopShareHandler share = new(loops, clock: () => _now);
        List<string> replies = new();

        share.OnSend(Ctx("Raijin", replies), "kings road");
        loops.Get("King's Road")!.Waypoints[0].DoNotAttack = true;
        replies.Clear();
        share.OnSend(Ctx("Raijin", replies), "yes");

        Assert.Equal("loop 'King's Road' changed since it was offered - ask for it again", replies.Single());
    }

    // The two ends together: what the sender's client puts on the wire, read by the
    // asker's.
    [Fact]
    public void SendThenReceive_TheLoopSavedIsTheLoopOffered()
    {
        LoopManager senders = NewLoops();
        senders.Save(SampleLoop());
        LoopShareHandler share = new(senders, clock: () => _now);
        List<string> wire = new();
        share.OnSend(Ctx("Raijin", wire), "kings road");
        share.OnSend(Ctx("Raijin", wire), "yes");
        senders.Delete("King's Road");   // one folder stands in for both clients' here
        var (receiver, loops, notices) = NewReceiver();

        receiver.Ingest(Tele("Scout", "{" + wire[0] + "}"));
        receiver.NoteSendConfirmed("Scout");
        foreach (string line in wire.Skip(1)) receiver.Ingest(Tele("Scout", "{" + line + "}"));

        Assert.Equal($"[Received loop 'King's Road' 3 rooms from Scout ({LoopSignature.ShortOf(SampleLoop())}, verified)]",
            notices.Single());
        Assert.Equal(LoopSignature.Of(SampleLoop()), LoopSignature.Of(loops.Get("King's Road")!));
    }

    [Fact]
    public void Receive_AfterOurYes_ReassemblesOutOfOrder_AndSaves()
    {
        var (receiver, loops, notices) = NewReceiver();
        Loop big = new("Grand tour", Enumerable.Range(1, 200)
            .Select(i => new LoopWaypoint(new RoomKey(1, i), command: $"search {i}")));
        List<string> lines = DataLines(big);
        Assert.True(lines.Count > 1);

        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in Enumerable.Reverse(lines)) receiver.Ingest(Tele("Raijin", line));

        Assert.Equal(200, loops.Get("Grand tour")!.Waypoints.Count);
        Assert.Equal($"[Received loop 'Grand tour' 200 rooms from Raijin ({LoopSignature.ShortOf(big)}, {NoCodeOffered})]",
            notices.Single());
    }

    [Fact]
    public void Receive_WithoutOurYes_Ignored()
    {
        var (receiver, loops, notices) = NewReceiver();
        foreach (string line in DataLines(SampleLoop())) receiver.Ingest(Tele("Raijin", line));

        Assert.Empty(loops.Loops);
        Assert.Empty(notices);
    }

    [Fact]
    public void Receive_FromAPlayerWeDidntConfirmWith_Ignored()
    {
        var (receiver, loops, _) = NewReceiver();
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(SampleLoop())) receiver.Ingest(Tele("Mallory", line));

        Assert.Empty(loops.Loops);
    }

    [Fact]
    public void Receive_DifferentLoopUnderOurName_SavedAlongsideIt()
    {
        var (receiver, loops, notices) = NewReceiver();
        loops.Save(new Loop("King's Road", new[] { new RoomKey(9, 1), new RoomKey(9, 2) }));

        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(SampleLoop())) receiver.Ingest(Tele("Raijin", line));

        Assert.Equal("9/1", loops.Get("King's Road")!.Waypoints[0].Room);   // ours untouched
        Assert.Equal(3, loops.Get("King's Road 2")!.Waypoints.Count);
        string theirs = LoopSignature.ShortOf(SampleLoop()), ours = LoopSignature.ShortOf(loops.Get("King's Road")!);
        Assert.Equal($"[Received loop 'King's Road' 3 rooms from Raijin ({theirs}, {NoCodeOffered}), saved as 'King's Road 2': "
            + $"your 'King's Road' is a different loop ({ours})]", notices.Single());
    }

    [Fact]
    public void Receive_IdenticalLoopAlreadyHeld_NothingSaved()
    {
        var (receiver, loops, notices) = NewReceiver();
        loops.Save(SampleLoop());

        receiver.NoteSendConfirmed(null);   // confirmed on gangpath / say
        foreach (string line in DataLines(SampleLoop())) receiver.Ingest(Tele("Raijin", line));

        Assert.Single(loops.Loops);
        Assert.Equal($"[Received loop 'King's Road' 3 rooms from Raijin ({LoopSignature.ShortOf(SampleLoop())}, {NoCodeOffered}), "
            + "already have it as 'King's Road']", notices.Single());
    }

    [Fact]
    public void Receive_OneTransferPerYes()
    {
        var (receiver, loops, _) = NewReceiver();
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(SampleLoop("First"))) receiver.Ingest(Tele("Raijin", line));
        foreach (string line in DataLines(SampleLoop("Second"), id: "c3d4")) receiver.Ingest(Tele("Raijin", line));

        Assert.NotNull(loops.Get("First"));
        Assert.Null(loops.Get("Second"));
    }
}

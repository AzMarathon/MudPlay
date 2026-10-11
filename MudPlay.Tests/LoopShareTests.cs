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

    // A loop's name is written to the terminal and to a file name, and a room's
    // command is sent to the game: a control character in either is refused, the
    // whole loop with it. (\u001b is escape; \r would end the command and start a
    // line of its own.)
    [Theory]
    [InlineData("{\"n\":\"x\\u001b[2J\",\"w\":[{\"r\":\"1/1\"},{\"r\":\"1/2\"}]}")]
    [InlineData("{\"n\":\"two\\nlines\",\"w\":[{\"r\":\"1/1\"},{\"r\":\"1/2\"}]}")]
    [InlineData("{\"n\":\"x\",\"w\":[{\"r\":\"1/1\",\"c\":\"n\\rdrop all\"},{\"r\":\"1/2\"}]}")]
    [InlineData("{\"n\":\"x\",\"w\":[{\"r\":\"1/1\"},{\"r\":\"1/2\",\"c\":\"say hi\\u0007\"}]}")]
    public void Codec_ControlCharacterInTheNameOrACommand_Refused(string json) =>
        Assert.Throws<FormatException>(() => LoopShareCodec.Decode(new[] { Pack(json) }));

    // What chains commands in a loop is plain text, and still goes through.
    [Fact]
    public void Codec_CommandsChainedTheUsualWays_AreKept()
    {
        Loop decoded = LoopShareCodec.Decode(new[]
        {
            Pack("{\"n\":\"x\",\"w\":[{\"r\":\"1/1\",\"c\":\"open door;n^Msearch\"},{\"r\":\"1/2\"}]}"),
        });

        Assert.Equal("open door;n^Msearch", decoded.Waypoints[0].Command);
    }

    // Such a loop is turned away as one that arrived damaged: nothing saved, the
    // notice free of the character, and the sender told to send again.
    [Fact]
    public void Receive_LoopNamedWithAnEscapeSequence_IsTurnedAwayAsDamaged()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        LoopManager loops = NewLoops();
        List<string> notices = new();
        List<string> told = new();
        LoopShareReceiver receiver = new(new ChatRouter(router), loops, notices.Add, clock: () => _now,
            tell: (_, _, text, _) => { told.Add(text); return true; });
        string blob = Pack("{\"n\":\"x\\u001b[2J\",\"w\":[{\"r\":\"1/1\"},{\"r\":\"1/2\"}]}");

        receiver.NoteSendConfirmed("Raijin");
        receiver.Ingest(Tele("Raijin", $"{{{LoopShareHandler.DataToken} a1b2 1/1 {blob}}}"));

        Assert.Empty(loops.Loops);
        Assert.Equal("[Loop from Raijin arrived damaged, not saved]", notices.Single());
        Assert.Equal("@loopresult - not saved: it arrived damaged - send it again", told.Single());
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
        Assert.Equal($"[Received loop 'Later' 2 rooms from Raijin ({LoopSignature.ShortOf(loops.Get("Later")!)}, "
            + "not verified: it uses a setting this version does not know)]", notices.Single());
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

        Assert.Equal("loop send of 'King's Road' stopped: it changed since it was offered - ask for it again", replies.Single());
    }

    // ----- the asker's client answers the sender ---------------------------

    private sealed record Told(RemoteChannel Channel, string To, string Text, bool Failed);

    private (LoopShareReceiver Receiver, LoopManager Loops, List<Told> Told) NewAnsweringReceiver()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        LoopManager loops = NewLoops();
        List<Told> told = new();
        LoopShareReceiver receiver = new(new ChatRouter(router), loops, _ => { }, clock: () => _now,
            tell: (channel, to, text, failed) => { told.Add(new Told(channel, to, text, failed)); return true; });
        return (receiver, loops, told);
    }

    private static void Transfer(LoopShareReceiver receiver, Loop loop, string sender = "Raijin", string id = "a1b2")
    {
        receiver.Ingest(Offer(sender, loop));
        receiver.NoteSendConfirmed(sender);
        foreach (string line in DataLines(loop, id)) receiver.Ingest(Tele(sender, line));
    }

    [Fact]
    public void Receive_TellsTheSenderOnce_WhatBecameOfTheLoop()
    {
        var (receiver, loops, told) = NewAnsweringReceiver();
        Loop sent = SampleLoop();
        string code = LoopSignature.ShortOf(sent);

        Transfer(receiver, sent);
        Assert.Equal(new Told(RemoteChannel.Telepath, "Raijin", $"@loopresult {code} saved as 'King's Road'", Failed: false),
            told.Single());

        // The same loop again: turned away, and the sender hears which loop it is here.
        told.Clear();
        Transfer(receiver, sent, id: "c3d4");
        Assert.Equal(new Told(RemoteChannel.Telepath, "Raijin", $"@loopresult {code} already have it as 'King's Road'", false),
            told.Single());

        // A different loop of that name: the sender hears the name it was saved under.
        told.Clear();
        Loop other = SampleLoop();
        other.Waypoints[0].DelayMs += 1;
        Transfer(receiver, other, id: "e5f6");
        Assert.Equal($"@loopresult {LoopSignature.ShortOf(other)} saved as 'King's Road 2'", told.Single().Text);
        Assert.Equal(2, loops.Loops.Count);
    }

    private static ChatLogEntry Heard(ChatChannel channel, string sender, string msg) =>
        new(DateTimeOffset.UnixEpoch, channel, sender, msg, $"{sender}: {msg}");

    private static string OfferText(Loop loop) =>
        "{" + LoopShareHandler.OfferLine(loop.Name, LoopSignature.ShortOf(loop)) + "}";

    // ----- a player makes other offers in between -------------------------

    // A player offers loops to others too, where we hear it. The offer made to us is
    // not lost under one made to someone else.
    [Fact]
    public void Receive_OfferedAnotherLoopElsewhereInBetween_OursIsStillVerified()
    {
        var (receiver, loops, told) = NewAnsweringReceiver();
        Loop ours = SampleLoop("Ours"), someoneElses = SampleLoop("Someone else's");
        someoneElses.Waypoints[0].DelayMs += 1;

        receiver.Ingest(Tele("Raijin", OfferText(ours)));
        receiver.Ingest(Heard(ChatChannel.Gangpath, "Raijin", OfferText(someoneElses)));   // an older client, to another gang member
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(ours)) receiver.Ingest(Tele("Raijin", line));

        Assert.NotNull(loops.Get("Ours"));
        Assert.Equal(new Told(RemoteChannel.Telepath, "Raijin", $"@loopresult {LoopSignature.ShortOf(ours)} saved as 'Ours'", false),
            told.Single());
    }

    // Asked for a second loop while the first is still arriving: the second offer
    // must not make the first loop "not the one offered". Each offer answers the
    // loop it was made for.
    [Fact]
    public void Receive_AskedForASecondLoopWhileTheFirstArrives_BothAreVerified()
    {
        var (receiver, loops, told) = NewAnsweringReceiver();
        Loop first = SampleLoop("First"), second = SampleLoop("Second");
        second.Waypoints[0].DelayMs += 1;

        receiver.Ingest(Tele("Raijin", OfferText(first)));
        receiver.NoteSendConfirmed("Raijin");
        receiver.Ingest(Tele("Raijin", OfferText(second)));        // the second ask's offer lands mid-transfer
        foreach (string line in DataLines(first)) receiver.Ingest(Tele("Raijin", line));

        Assert.Equal($"@loopresult {LoopSignature.ShortOf(first)} saved as 'First'", told.Single().Text);

        told.Clear();
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(second, id: "c3d4")) receiver.Ingest(Tele("Raijin", line));
        Assert.Equal($"@loopresult {LoopSignature.ShortOf(second)} saved as 'Second'", told.Single().Text);
        Assert.Equal(2, loops.Loops.Count);
    }

    // A loop that is none of the loops that player offered is still turned away.
    [Fact]
    public void Receive_LoopThatIsNoneOfSeveralOffered_IsNotSaved()
    {
        var (receiver, loops, notices) = NewReceiver();
        Loop one = SampleLoop("One"), two = SampleLoop("Two"), arrived = SampleLoop("Three");
        two.Waypoints[0].DelayMs += 1;
        arrived.Waypoints[0].DelayMs += 2;

        receiver.Ingest(Offer("Raijin", one));
        receiver.Ingest(Offer("Raijin", two));
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(arrived)) receiver.Ingest(Tele("Raijin", line));

        Assert.Empty(loops.Loops);
        Assert.Equal($"[Received loop 'Three' 3 rooms from Raijin does not match the loop offered (received {LoopSignature.ShortOf(arrived)}, "
            + $"offered {LoopSignature.ShortOf(one)} or {LoopSignature.ShortOf(two)}), not saved]", notices.Single());
    }

    [Fact]
    public void Receive_OfferOlderThanFiveMinutes_NoLongerVerifies()
    {
        var (receiver, _, notices) = NewReceiver();
        Loop sent = SampleLoop();

        receiver.Ingest(Offer("Raijin", sent));
        _now += TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1);
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(sent)) receiver.Ingest(Tele("Raijin", line));

        Assert.EndsWith($", {NoCodeOffered})]", notices.Single());
    }

    // A handful of offers are kept for a player, the newest: one who offers without
    // end cannot make the list grow.
    [Fact]
    public void Receive_KeepsOnlyTheLastFewOffersOfAPlayer()
    {
        var (receiver, loops, notices) = NewReceiver();
        Loop first = SampleLoop("First");
        receiver.Ingest(Offer("Raijin", first));
        for (int i = 0; i < 8; i++) receiver.Ingest(Offer("Raijin", first, code: $"0000000{i}"));

        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(first)) receiver.Ingest(Tele("Raijin", line));

        Assert.Empty(loops.Loops);
        Assert.Contains("does not match the loop offered", notices.Single());
    }

    // ----- by telepath only ------------------------------------------------

    // A loop is taken by telepath and by nothing else. On a shared channel the lines
    // are an older client's (this version sends none there): passed over, also from
    // the very player we said yes to, with no answer, and the loop that then comes
    // by telepath is still let in.
    [Theory]
    [InlineData(ChatChannel.Gangpath)]
    [InlineData(ChatChannel.Local)]
    [InlineData(ChatChannel.Broadcast)]
    [InlineData(ChatChannel.Gossip)]
    public void Receive_LoopLinesOnAnyChannelButTelepath_ArePassedOver(ChatChannel channel)
    {
        var (receiver, loops, told) = NewAnsweringReceiver();
        Loop asked = SampleLoop();

        receiver.Ingest(Tele("Raijin", OfferText(asked)));
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(asked, id: "old1")) receiver.Ingest(Heard(channel, "Raijin", line));
        foreach (string line in DataLines(asked, id: "old2")) receiver.Ingest(Heard(channel, "Mallory", line));

        Assert.Empty(loops.Loops);
        Assert.Empty(told);

        foreach (string line in DataLines(asked)) receiver.Ingest(Tele("Raijin", line));

        Assert.NotNull(loops.Get("King's Road"));
        Assert.Equal(new Told(RemoteChannel.Telepath, "Raijin", $"@loopresult {LoopSignature.ShortOf(asked)} saved as 'King's Road'", false),
            told.Single());
    }

    // An offer heard on a shared channel is no offer to us: the loop that player
    // then telepaths is checked against nothing.
    [Fact]
    public void Receive_OfferHeardOnASharedChannel_IsNotKept()
    {
        var (receiver, _, notices) = NewReceiver();
        Loop sent = SampleLoop();

        receiver.Ingest(Heard(ChatChannel.Gangpath, "Raijin", OfferText(sent)));
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(sent)) receiver.Ingest(Tele("Raijin", line));

        Assert.EndsWith($", {NoCodeOffered})]", notices.Single());
    }

    // An older client's telepathed offer has no code; its loop is taken unverified.
    [Fact]
    public void Receive_AnOlderSendersLoopByTelepath_IsStillTaken()
    {
        var (receiver, loops, notices) = NewReceiver();
        Loop sent = SampleLoop();

        receiver.Ingest(Tele("Raijin", "{preparing to send: King's Road, yes to confirm, no to deny}"));
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(sent)) receiver.Ingest(Tele("Raijin", line));

        Assert.NotNull(loops.Get("King's Road"));
        Assert.EndsWith($", {NoCodeOffered})]", notices.Single());
    }

    // The client asked refuses every form of the command that comes by another
    // channel, with where it does work, and offers or sends nothing.
    [Theory]
    [InlineData(RemoteChannel.Gangpath, "kings road")]
    [InlineData(RemoteChannel.Local, "kings road")]
    [InlineData(RemoteChannel.Broadcast, "kings road")]
    [InlineData(RemoteChannel.Gangpath, "")]
    [InlineData(RemoteChannel.Gangpath, "yes")]
    [InlineData(RemoteChannel.Local, "no")]
    public void Send_OnAnyChannelButTelepath_IsRefusedWithWhereItWorks(RemoteChannel channel, string rest)
    {
        LoopManager loops = NewLoops();
        loops.Save(SampleLoop());
        LogService log = new();
        LoopShareHandler share = new(loops, log, clock: () => _now, runningLoop: () => loops.Get("King's Road"));
        List<string> replies = new(), refusals = new();
        RemoteCommandContext On(RemoteChannel via) =>
            new("Raijin", "@loop", Array.Empty<string>(), "@loop send", via, replies.Add) { Refusal = why => refusals.Add(why ?? "") };

        // An offer made by telepath is not confirmed, or cancelled, from elsewhere.
        share.OnSend(On(RemoteChannel.Telepath), "kings road");
        replies.Clear();

        share.OnSend(On(channel), rest);

        Assert.Empty(replies);
        Assert.Equal("@loop send works by telepath only", refusals.Single());
        Assert.Contains(log.Snapshot(), e => e.Severity == LogSeverity.Info
            && e.Message.Contains($"refused @loop send from Raijin on {channel}"));

        // The telepathed offer still stands.
        share.OnSend(On(RemoteChannel.Telepath), "yes");
        Assert.StartsWith("sending loop 'King's Road'", replies[0]);
    }

    // A loop that did not arrive as offered, or could not be read at all, is a
    // failure: the sender is told so it can send again, as a failure answer.
    [Fact]
    public void Receive_LoopThatDidNotArriveIntact_TellsTheSenderAsAFailure()
    {
        var (receiver, loops, told) = NewAnsweringReceiver();
        Loop offered = SampleLoop();
        Loop arrived = SampleLoop();
        arrived.Waypoints[0].DelayMs += 1;

        receiver.Ingest(Offer("Raijin", offered));
        receiver.NoteSendConfirmed("Raijin");
        foreach (string line in DataLines(arrived)) receiver.Ingest(Tele("Raijin", line));

        Assert.Equal(new Told(RemoteChannel.Telepath, "Raijin",
            $"@loopresult {LoopSignature.ShortOf(arrived)} not saved: it is not the loop offered ({LoopSignature.ShortOf(offered)}) - send it again",
            Failed: true), told.Single());

        told.Clear();
        receiver.NoteSendConfirmed("Raijin");
        receiver.Ingest(Tele("Raijin", $"{{{LoopShareHandler.DataToken} a1b2 1/1 AAAA}}"));

        Assert.Equal(new Told(RemoteChannel.Telepath, "Raijin", "@loopresult - not saved: it arrived damaged - send it again", true),
            told.Single());
        Assert.Empty(loops.Loops);
    }

    private static RemoteCommandManager Engine(bool masterSwitchOff = false, bool warn = true, bool stealthed = false)
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        return new RemoteCommandManager(new ChatRouter(router), new PartyState(), new PlayerDatabase())
        {
            BlockedByMasterSwitch = _ => masterSwitchOff,
            WarnOnDenial = warn,
            StealthedProvider = () => stealthed,
        };
    }

    private static string[] Wire(RemoteCommandManager engine) =>
        engine.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).ToArray();

    // The answer leaves as every remote reply does: braced, to the sender's given
    // name, on that channel, and by telepath in place of a say while sneaking.
    [Fact]
    public void Answer_GoesOutAsARemoteReply()
    {
        RemoteCommandManager engine = Engine();
        // A two-word name is addressed by its first word, as the game wants it.
        Assert.True(engine.SendOutcome(RemoteChannel.Telepath, "Raijin Surname", "@loopresult 9da659db saved as 'X'", failed: false));
        Assert.True(engine.SendOutcome(RemoteChannel.Gangpath, "Raijin", "@loopresult 9da659db saved as 'X'", failed: false));
        Assert.True(engine.SendOutcome(RemoteChannel.Local, "Raijin", "@loopresult 9da659db saved as 'X'", failed: false));
        Assert.True(engine.SendOutcome(RemoteChannel.Broadcast, "Raijin", "@loopresult 9da659db saved as 'X'", failed: false));

        Assert.Equal(new[]
        {
            "/Raijin {@loopresult 9da659db saved as 'X'}\r",
            "bg {@loopresult 9da659db saved as 'X'}\r",
            ">Raijin {@loopresult 9da659db saved as 'X'}\r",
            "br {@loopresult 9da659db saved as 'X'}\r",
        }, Wire(engine));

        RemoteCommandManager sneaking = Engine(stealthed: true);
        sneaking.SendOutcome(RemoteChannel.Local, "Raijin", "@loopresult 9da659db saved as 'X'", failed: false);
        Assert.Equal("/Raijin {@loopresult 9da659db saved as 'X'}\r", Wire(sneaking).Single());
    }

    // What became of a loop the user accepted is an answer, not a refusal: the Warn
    // setting does not silence it. "It did not arrive" is a failure and follows it.
    // With the master switch off nothing goes out by itself, either kind.
    [Fact]
    public void Answer_IsHeldBackByTheMasterSwitch_AndAFailureByTheWarnSetting()
    {
        RemoteCommandManager warnOff = Engine(warn: false);
        Assert.True(warnOff.SendOutcome(RemoteChannel.Telepath, "Raijin", "@loopresult 9da659db saved as 'X'", failed: false));
        Assert.False(warnOff.SendOutcome(RemoteChannel.Telepath, "Raijin", "@loopresult - not saved", failed: true));
        Assert.Equal("/Raijin {@loopresult 9da659db saved as 'X'}\r", Wire(warnOff).Single());

        RemoteCommandManager warnOn = Engine(warn: true);
        Assert.True(warnOn.SendOutcome(RemoteChannel.Telepath, "Raijin", "@loopresult - not saved", failed: true));
        Assert.Equal("/Raijin {@loopresult - not saved}\r", Wire(warnOn).Single());

        RemoteCommandManager switchedOff = Engine(masterSwitchOff: true);
        Assert.False(switchedOff.SendOutcome(RemoteChannel.Telepath, "Raijin", "@loopresult 9da659db saved as 'X'", failed: false));
        Assert.False(switchedOff.SendOutcome(RemoteChannel.Telepath, "Raijin", "@loopresult - not saved", failed: true));
        Assert.Empty(Wire(switchedOff));

        // "Disallow all remote commands" stops players' commands being obeyed; the
        // answer ends something the user started, and still goes.
        RemoteCommandManager disallowed = Engine();
        disallowed.MasterDisable = true;
        Assert.True(disallowed.SendOutcome(RemoteChannel.Telepath, "Raijin", "@loopresult 9da659db saved as 'X'", failed: false));
    }

    // The answer must be nothing to any client but the sender's `@loop send`: not a
    // command to obey or refuse, and not the answer to some other question.
    [Theory]
    [InlineData("{@loopresult 9da659db saved as 'King's Road 2'}")]
    [InlineData("@loopresult 9da659db saved as 'King's Road 2'")]
    [InlineData("{@loopresult - not saved: it arrived damaged - send it again}")]
    public void Answer_IsNoCommand_AndNoOtherReply(string line)
    {
        RemoteCommandManager engine = Engine();

        engine.DispatchForTests(Tele("Raijin", line));

        Assert.Empty(engine.LastSentForTests);
        Assert.False(PartyProbeManager.TryParseVersion(line, out _));
    }

    // Every line `@loop send` puts on the wire, from either end, is known as one,
    // whatever the loop is called: the readers of other replies skip them.
    [Theory]
    [InlineData("I can't come")]
    [InlineData("no ailments")]
    [InlineData("ailments: blind")]
    [InlineData("path failed, going idle")]
    public void EveryLineOfALoopSend_IsKnownAsOne_WhateverTheLoopIsNamed(string name)
    {
        LoopManager loops = NewLoops();
        Loop loop = SampleLoop(name);
        loops.Save(loop);
        LoopShareHandler share = new(loops, clock: () => _now);
        List<string> wire = new();
        share.OnSend(Ctx("Raijin", wire), name);                 // the offer
        share.OnSend(Ctx("Raijin", wire), "no");                 // cancelled
        share.OnSend(Ctx("Raijin", wire), name);
        loop.Waypoints[0].DelayMs += 1;
        share.OnSend(Ctx("Raijin", wire), "yes");                // stopped: it changed
        share.OnSend(Ctx("Raijin", wire), name);
        share.OnSend(Ctx("Raijin", wire), "yes");                // sending, and the data
        wire.Add(LoopShareHandler.ResultLine("9da659db", $"saved as '{name}'"));
        wire.Add(LoopShareHandler.ResultLine("9da659db", $"already have it as '{name}'"));
        wire.Add(LoopShareHandler.ResultLine(null, "not saved: it arrived damaged - send it again"));

        Assert.True(wire.Count >= 9);
        Assert.All(wire, line => Assert.True(LoopShareHandler.IsLoopSendLine(line), line));
    }

    [Theory]
    [InlineData("I can't come, forget me")]
    [InlineData("Standing; not moving; no ailments")]
    [InlineData("loop 'King's Road' not started: it has fewer than two rooms")]
    [InlineData("coming to your location for pickup")]
    public void OtherReplies_AreNotLoopSendLines(string inner) =>
        Assert.False(LoopShareHandler.IsLoopSendLine(inner));

    // Typed by hand without its braces, an answer or data line reaches the command
    // engine. Both tokens are reserved there (AppServices does the same), so it is
    // swallowed with no word back whatever the Warn setting says.
    [Theory]
    [InlineData("@loopresult 9da659db saved as 'King's Road 2'")]
    [InlineData("@loopresult - not saved")]
    [InlineData("@loopdata a1b2 1/1 AAAA")]
    [InlineData("@LOOPRESULT 9da659db saved as 'reroll'")]
    public void AnswerOrDataTypedWithoutBraces_IsSwallowedByTheCommandEngine(string line)
    {
        LogService log = new();
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        RemoteCommandManager engine = new(new ChatRouter(router), new PartyState(), new PlayerDatabase(), log) { WarnOnDenial = true };
        engine.RegisterIgnored(LoopShareHandler.DataToken);
        engine.RegisterIgnored(LoopShareHandler.ResultToken);

        engine.DispatchForTests(Tele("Raijin", line));

        Assert.Empty(engine.LastSentForTests);
        // Swallowed as a reserved token, ahead of the unknown-command and blocked-word paths.
        Assert.Empty(log.Snapshot());
    }

    // The offer line is longer than a client version can be, code and all, so a
    // client waiting on a @version answer never takes it for one.
    [Fact]
    public void Offer_IsNotTakenForAClientVersion() =>
        Assert.False(PartyProbeManager.TryParseVersion("{" + LoopShareHandler.OfferLine("x", "9da659db") + "}", out _));

    private (LoopShareHandler Share, List<string> Notices, string Code) SenderThatSent(Loop loop, string to = "Raijin")
    {
        LoopManager loops = NewLoops();
        loops.Save(loop);
        List<string> notices = new();
        LoopShareHandler share = new(loops, clock: () => _now, notice: notices.Add);
        List<string> replies = new();
        share.OnSend(Ctx(to, replies), loop.Name);
        share.OnSend(Ctx(to, replies), "yes");
        return (share, notices, LoopSignature.ShortOf(loop));
    }

    [Fact]
    public void Sender_ShowsTheAskersAnswer_AndThatTheCodesAreTheSame()
    {
        var (share, notices, code) = SenderThatSent(SampleLoop());

        share.NoteChat(Tele("Raijin", $"{{@loopresult {code} saved as 'King's Road 2'}}"));

        Assert.Equal($"[Raijin on loop 'King's Road': saved as 'King's Road 2' (code {code}, the same as sent)]", notices.Single());

        // One answer to a send. A second line is about nothing we sent.
        share.NoteChat(Tele("Raijin", $"{{@loopresult {code} saved as 'King's Road 3'}}"));
        Assert.Single(notices);
    }

    [Fact]
    public void Sender_SaysSoWhenTheAskersCodeIsNotTheOneSent()
    {
        var (share, notices, code) = SenderThatSent(SampleLoop());

        share.NoteChat(Tele("Raijin", "{@loopresult 0123abcd not saved: it is not the loop offered (9da659db) - send it again}"));

        Assert.Equal("[Raijin on loop 'King's Road': not saved: it is not the loop offered (9da659db) - send it again "
            + $"(code 0123abcd, NOT the code sent, {code})]", notices.Single());
    }

    [Fact]
    public void Sender_TakesAnAnswerOnlyFromThePlayerItSentTo_AndOnlyForAWhile()
    {
        var (share, notices, code) = SenderThatSent(SampleLoop());

        share.NoteChat(Tele("Mallory", $"{{@loopresult {code} saved as 'King's Road'}}"));
        share.NoteChat(Tele("Raijin", "{@loopresult nonsense saved}"));
        share.NoteChat(Tele("Raijin", "{loopresult saved as 'King's Road'}"));
        Assert.Empty(notices);

        _now += TimeSpan.FromMinutes(11);
        share.NoteChat(Tele("Raijin", $"{{@loopresult {code} saved as 'King's Road'}}"));
        Assert.Empty(notices);
    }

    // A loop goes by telepath and so does its answer. A line of that shape heard on
    // a shared channel is an older client's, about a transfer of its own: passed
    // over, even with our code in it, and the telepathed answer is still taken.
    [Theory]
    [InlineData(ChatChannel.Gangpath)]
    [InlineData(ChatChannel.Local)]
    [InlineData(ChatChannel.Broadcast)]
    public void Sender_TakesAnAnswerByTelepathOnly(ChatChannel channel)
    {
        var (share, notices, code) = SenderThatSent(SampleLoop());

        share.NoteChat(Heard(channel, "Raijin", "{@loopresult 0123abcd saved as 'Some other loop'}"));
        share.NoteChat(Heard(channel, "Raijin", $"{{@loopresult {code} saved as 'King's Road'}}"));
        Assert.Empty(notices);

        share.NoteChat(Tele("Raijin", $"{{@loopresult {code} saved as 'King's Road'}}"));
        Assert.Equal($"[Raijin on loop 'King's Road': saved as 'King's Road' (code {code}, the same as sent)]", notices.Single());
    }

    // By telepath an answer can only be about the loop we sent that player, so one
    // with no code (it could not be read there) is shown too.
    [Fact]
    public void Sender_ByTelepath_ShowsAnAnswerThatHasNoCode()
    {
        var (share, notices, _) = SenderThatSent(SampleLoop());

        share.NoteChat(Tele("Raijin", "{@loopresult - not saved: it arrived damaged - send it again}"));

        Assert.Equal("[Raijin on loop 'King's Road': not saved: it arrived damaged - send it again]", notices.Single());
    }

    // The words are another player's client's: shown as plain text, and not at length.
    [Fact]
    public void Sender_ShowsOnlyPlainTextOfTheAnswer()
    {
        var (share, notices, code) = SenderThatSent(SampleLoop());

        share.NoteChat(Tele("Raijin", $"{{@loopresult {code} saved\u001b[2J as {new string('x', 500)}}}"));

        Assert.DoesNotContain('\u001b', notices.Single());
        Assert.True(notices.Single().Length < 260);
    }

    // The two ends together, the answer included: the sender's client reads the
    // asker's answer and finds its own code in it.
    [Fact]
    public void SendReceiveAndAnswer_BothEndsShowTheSameCode()
    {
        var (share, senderNotices, code) = SenderThatSent(SampleLoop(), to: "Asker");
        var (receiver, loops, told) = NewAnsweringReceiver();
        loops.Delete("King's Road");   // one folder stands in for both clients' here

        Transfer(receiver, SampleLoop(), sender: "Scout");
        share.NoteChat(Tele("Asker", "{" + told.Single().Text + "}"));

        Assert.Equal($"[Asker on loop 'King's Road': saved as 'King's Road' (code {code}, the same as sent)]", senderNotices.Single());
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

        receiver.NoteSendConfirmed("Raijin");
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

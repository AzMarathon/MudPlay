using System.Text;
using System.Text.Json;
using MudPlay.Game;
using MudPlay.Game.Combat;
using MudPlay.Game.Gmcp;
using MudPlay.Game.Map;
using MudPlay.Models.Settings;
using MudPlay.Net;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

public sealed partial class GmcpIntegrationTests
{
    private sealed class Session : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mudplay-gmcp-" + Guid.NewGuid().ToString("N"));
        private readonly object _dispatch = new();
        private readonly StringBuilder _text = new();
        public T Read<T>(Func<T> inspect) { lock (_dispatch) return inspect(); }
        public string Transcript => Read(() => _text.ToString());
        public void ClearText() { lock (_dispatch) _text.Clear(); }
        public ServerFeed Feed { get; } = new();
        public WirePromptScanner Scanner { get; } = new();
        public PlayerState Player { get; } = new();
        public PlayerStats Stats { get; } = new();
        public LogService Log { get; } = new();
        public PromptParser Prompt { get; }
        public StatParser StatParser { get; }
        public RoomTracker Tracker { get; }
        public RoomGraphManager Graph { get; }
        public RoomEntityClassifier Classifier { get; }
        public CombatStateTracker Combat { get; }
        public Game.Conditions.ConditionTracker Conditions { get; } = new(new MessageStore());
        public Game.Stealth.StealthManager Stealth { get; }
        public MonsterHpTracker Hp { get; } = new(_ => 100, _ => 0);
        public GmcpIntegration Gmcp { get; }
        public TelnetClient Telnet { get; } = new();
        public Game.Inventory.InventoryManager Inventory { get; }
        public Game.Inventory.AutoBuyManager Shop { get; }
        public Game.Spells.SpellbookState Book { get; }
        public Game.Spells.SpellListParser SpellParser { get; }
        public PartyManager Party { get; }
        public ChatRouter Chat { get; }
        public AbilBreakdownParser Abilities { get; }
        public Game.Spells.CastCoordinator Cast { get; }
        public Game.Spells.CastingDirector Director { get; }
        public MessageRouter Router { get; }
        private readonly RoomDisplayParser _display;
        private readonly EncumbranceParser _encumbrance;

        public Session(bool active = true)
        {
            Directory.CreateDirectory(_root);
            MessageRouter router = new(); Router = router; DefaultPatterns.Seed(router);
            Feed.Lines.LineEmitted += router.Dispatch;
            Feed.Lines.ChatLineEmitted += router.Dispatch;
            Feed.Lines.ReadEnded += router.EndRead;
            Prompt = new(Scanner, Player);
            StatParser = new(Stats, Log, schedule: new ManualTimers().Arm);
            StatParser.AttachLineExtractor(Feed.Lines);
            GameDataCache cache = new(_root);
            Graph = new(cache);
            Tracker = new(Graph);
            MonsterMessageStore monsters = new();
            Classifier = new(router, monsters, new PlayerDatabase(new ProfileService(), () => null), Tracker, Log);
            Classifier.AttachLineExtractor(Feed.Lines);
            Combat = new(router, new MovementCoordinator(Log), Classifier, monsters, Player, () => true, Log);
            _encumbrance = new(router, Player, Log);
            Gmcp = new(Prompt, StatParser, Graph, Tracker, Classifier, Combat, _encumbrance, Log);
            Stealth = new(router, Player, Log);
            Gmcp.BindConsumers(Conditions, Stealth, Hp);
            Inventory = new(Log, isKey: name => name.Contains("key", StringComparison.OrdinalIgnoreCase)); Inventory.AttachLineExtractor(Feed.Lines);
            Shop = new(router, _ => null, _ => 0, () => false, Log); Shop.AttachLineExtractor(Feed.Lines);
            Book = new(new Game.Spells.KnownSpellCatalog(cache));
            SpellParser = new(Book, Log); SpellParser.AttachLineExtractor(Feed.Lines);
            Party = new(router, new PartyState(), Log) { LocalCharacterName = "Tester" }; Party.AttachLineExtractor(Feed.Lines);
            Chat = new(router, log: Log); Abilities = new(Log); Abilities.AttachLineExtractor(Feed.Lines);
            Cast = new(router, Log);
            Director = new(Player, Cast, () => new Models.Profile.SpellsSettings(), () => new Models.Profile.HealthSettings(), () => false, Log);
            Gmcp.BindMilestone2(Inventory, Shop, Book, SpellParser, Party, Chat, Abilities, Director);
            Classifier.EntitiesObserved += Hp.NoteRoomEntities;
            _display = new(Feed.Lines, Tracker, Log) { GmcpOwnsRoom = () => Gmcp.State.Owns("Anomaly.Room.Info") };
            _display.RoomParsed += Gmcp.ObserveRoom;
            Scanner.PromptObserved += Gmcp.ObservePrompt;
            Gmcp.Configure(GmcpIntegration.Packages.ToDictionary(p => p, _ => active ? GmcpMode.Active : GmcpMode.Shadow));
            Telnet.DataReceived += Display;
            Telnet.GmcpReceived += message => { lock (_dispatch) Gmcp.Receive(message); };
            Telnet.GmcpNegotiated += enabled => { lock (_dispatch) { if (!enabled) Gmcp.Disconnected(); } };
            Gmcp.SetSender((package, payload) => Telnet.SendGmcpAsync(package, payload));
        }

        public void Send(string package, object payload) => Gmcp.Receive(new(package, JsonSerializer.SerializeToElement(payload)));
        private void Display(ReadOnlyMemory<byte> bytes)
        {
            lock (_dispatch)
            {
                _text.Append(Encoding.Latin1.GetString(bytes.Span));
                if (_text.Length > 64000) _text.Remove(0, _text.Length - 32000);
                Scanner.Append(bytes.Span);
                Feed.Emulator.Feed(bytes.Span);
            }
        }
        public void Text(string text) { Scanner.Append(Encoding.ASCII.GetBytes(text)); Feed.Raw(text); }
        private void Wire(byte[] bytes)
        {
            byte[] output = new byte[Math.Max(1, bytes.Length)];
            int count = Telnet.Interpret(bytes, output);
            if (count > 0) Display(output.AsMemory(0, count));
        }
        private static byte[] ServerPacket(string package, JsonElement payload) => new byte[] { 255, 250, 201 }
            .Concat(Encoding.UTF8.GetBytes(package + " " + payload.GetRawText())).Concat(new byte[] { 255, 240 }).ToArray();

        public void Replay(string name)
        {
            foreach (JsonElement frame in GmcpStateTests.Frames(name))
            {
                if (frame.GetProperty("dir").GetString() != "s2c") continue;
                string? kind = frame.GetProperty("kind").GetString();
                if (frame.TryGetProperty("raw", out JsonElement raw)) Wire(Convert.FromBase64String(raw.GetString()!));
                else if (kind == "gmcp") Wire(ServerPacket(frame.GetProperty("pkg").GetString()!, frame.GetProperty("msg")));
                else if (kind == "negotiation") Wire(new byte[] { 255, frame.GetProperty("telnet").GetString()!.Contains("WONT", StringComparison.Ordinal) ? (byte)252 : (byte)251, 201 });
                else if (kind == "text") Text(frame.GetProperty("text").GetString()! + "\r\n");
            }
        }
        public void ReplayCapture(string name)
        {
            foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Gmcp", "Captured", name + ".jsonl")).Skip(1))
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement frame = document.RootElement;
                if (frame.GetProperty("dir").GetString() != "s2c") continue;
                if (frame.TryGetProperty("raw", out JsonElement raw)) { Wire(Convert.FromBase64String(raw.GetString()!)); continue; }
                string? kind = frame.GetProperty("kind").GetString();
                if (kind == "text") Text(frame.GetProperty("text").GetString()!);
                else if (kind == "gmcp") Wire(ServerPacket(frame.GetProperty("pkg").GetString()!, frame.GetProperty("msg")));
                else if (kind == "negotiation")
                {
                    string[] tokens = frame.GetProperty("telnet").GetString()!.Split(' ');
                    byte command = tokens[1] switch { "DO" => 253, "DONT" => 254, "WILL" => 251, "WONT" => 252, _ => 0 };
                    byte option = tokens[2] switch { "GMCP" => 201, "ECHO" => 1, "SGA" => 3, "BINARY" => 0, "NAWS" => 31, "LINEMODE" => 34, "TTYPE" => 24, _ => byte.Parse(tokens[2]) };
                    Wire(new byte[] { 255, command, option });
                }
            }
        }

        public void Dispose()
        {
            Director.Dispose(); Cast.Dispose(); Inventory.Dispose(); Shop.Dispose(); SpellParser.Dispose(); Party.Dispose(); Chat.Dispose(); Abilities.Dispose();
            Conditions.Dispose(); Stealth.Dispose();
            _display.Dispose(); _encumbrance.Dispose(); Combat.Dispose(); Classifier.Dispose(); StatParser.Dispose(); Prompt.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task Until(Func<bool> ready, string condition, int seconds = 30, Func<string?>? ended = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!ready() && DateTime.UtcNow < deadline)
        {
            Assert.True(ended?.Invoke() is null, "Connection ended while waiting for " + condition + ": " + ended?.Invoke());
            await Task.Delay(25);
        }
        Assert.True(ready(), "Timed out waiting for " + condition);
    }

    private static async Task Login(Session session, string account, string password)
    {
        string host = Environment.GetEnvironmentVariable("MUDPLAY_GMCP_HOST") ?? "192.168.1.100";
        int port = int.Parse(Environment.GetEnvironmentVariable("MUDPLAY_GMCP_PORT") ?? "2350");
        Assert.True(host == "127.0.0.1" || host == "192.168.1.100" && port == 2350, "Live tests only run against the isolated host or its local recording proxy.");
        await session.Telnet.ConnectAsync(host, port);
        await Until(() => session.Transcript.Contains("account name", StringComparison.OrdinalIgnoreCase), "test account prompt", ended: () => session.Telnet.EndedBecause);
        session.ClearText(); await session.Telnet.SendTextAsync(account + "\r\n");
        await Until(() => session.Transcript.Contains("password", StringComparison.OrdinalIgnoreCase), "test password prompt", ended: () => session.Telnet.EndedBecause);
        session.ClearText(); await session.Telnet.SendTextAsync(password + "\r\n");
        await Until(() => session.Transcript.Contains("Selection:", StringComparison.OrdinalIgnoreCase), "test board menu", ended: () => session.Telnet.EndedBecause);
        session.ClearText(); await session.Telnet.SendTextAsync("1\r\n");
        await Until(() => session.Transcript.Contains("[MMUDREBORN]:", StringComparison.OrdinalIgnoreCase), "test realm menu", ended: () => session.Telnet.EndedBecause);
        session.ClearText(); await session.Telnet.SendTextAsync("e\r\n");
        await Until(() => session.Read(() => session.Gmcp.State.Owns("Anomaly.Room.Info") || session.Gmcp.State.Snapshot("Anomaly.Room.Info") is not null), "initial server snapshots", ended: () => session.Telnet.EndedBecause);
        await Until(() => session.Read(() => session.Gmcp.State.RosterComplete), "complete initial monster baselines", ended: () => session.Telnet.EndedBecause);
    }

    private static async Task Move(Session session, string direction)
    {
        JsonElement room = session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value);
        string destination = room.GetProperty("exits").EnumerateArray().Single(e => e.GetProperty("dir").GetString() == direction).GetProperty("to").GetString()!;
        await session.Telnet.SendTextAsync(direction + "\r\n");
        await Until(() => session.Read(() => session.Gmcp.State.CurrentRoom == destination && session.Gmcp.State.Owns("Anomaly.Room.Info") && session.Gmcp.State.RosterComplete), "movement to server-provided exit destination");
    }

    [LiveGmcpFact]
    public async Task IsolatedHost_ActiveDarkLockedHiddenAndCombatLoop()
    {
        using Session session = new();
        try
        {
            await Login(session, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_ACCOUNT")!, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_PASSWORD")!);
            Assert.Equal("1,1", session.Read(() => session.Gmcp.State.CurrentRoom));
            foreach (string direction in new[] { "n", "n", "n", "w" }) await Move(session, direction);
            Assert.Equal("1,198", session.Read(() => session.Gmcp.State.CurrentRoom));
            Assert.True(session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value.GetProperty("light").GetInt32() < 0));
            JsonElement Door() => session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value)
                .GetProperty("exits").EnumerateArray().Single(e => e.GetProperty("dir").GetString() == "n").GetProperty("door");
            Assert.NotEqual(JsonValueKind.Null, Door().ValueKind);
            for (int attempt = 0; attempt < 5 && Door().GetProperty("state").GetString() != "open"; attempt++)
            {
                await session.Telnet.SendTextAsync("pick n\r\n"); await Task.Delay(1100);
                await session.Telnet.SendTextAsync("open n\r\n"); await Task.Delay(1100);
            }
            Assert.Equal("open", Door().GetProperty("state").GetString());
            await Move(session, "n"); await Move(session, "s");
            foreach (string direction in new[] { "e", "s", "s", "s" }) await Move(session, direction);
            Assert.Equal("1,1", session.Read(() => session.Gmcp.State.CurrentRoom));
            foreach (string direction in new[] { "s", "s", "w", "w", "s", "s" }) await Move(session, direction);
            Assert.Equal("1,123", session.Read(() => session.Gmcp.State.CurrentRoom));
            RoomEntity target = session.Read(() => session.Classifier.Current!.Value.Entities.First(e => e.Kind == EntityKind.Monster && !e.RawName.Contains("guard", StringComparison.OrdinalIgnoreCase)));
            await session.Telnet.SendTextAsync("attack " + target.RawName + "\r\n");
            await Until(() => session.Read(() => target.RuntimeId is { } uid && !session.Gmcp.State.StaticMonsters.ContainsKey(uid)), "monster death and identity eviction", seconds: 90);
            await session.Telnet.SendTextAsync("rest\r\n");
            await Until(() => session.Read(() => session.Player.Position == PlayerPosition.Resting), "server-driven resting state");
            foreach (string direction in new[] { "s", "w", "w" }) await Move(session, direction);
            Assert.Equal("1,140", session.Read(() => session.Gmcp.State.CurrentRoom));
            JsonElement hidden = session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value)
                .GetProperty("exits").EnumerateArray().Single(e => e.GetProperty("dir").GetString() == "u");
            Assert.True(hidden.GetProperty("hidden").GetBoolean());
            await Move(session, "u"); await Move(session, "d");
            foreach (string direction in new[] { "n", "n", "n", "n", "n", "e", "e", "e", "e" }) await Move(session, direction);
            Assert.Equal("1,1", session.Read(() => session.Gmcp.State.CurrentRoom));
            Assert.DoesNotContain(session.Log.Snapshot(), entry => entry.Source == "GMCP" && entry.Message.Contains("Cannot apply", StringComparison.Ordinal));
            await session.Telnet.SendTextAsync("quit\r\n");
        }
        finally { await session.Telnet.DisconnectAsync(); }
    }

    [LiveGmcpFact]
    public async Task IsolatedHost_ActiveWalkRecoveryResubscriptionWontAndReconnect()
    {
        string account = Environment.GetEnvironmentVariable("MUDPLAY_GMCP_ACCOUNT")!;
        string password = Environment.GetEnvironmentVariable("MUDPLAY_GMCP_PASSWORD")!;
        using Session session = new();
        using Session sysop = new(active: false);
        try
        {
            await Login(session, account, password);
            string origin = session.Read(() => session.Gmcp.State.CurrentRoom!);
            JsonElement room = session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value);
            JsonElement exit = room.GetProperty("exits").EnumerateArray().First(e => e.GetProperty("kind").GetString() == "normal");
            string destination = exit.GetProperty("to").GetString()!;
            await session.Telnet.SendTextAsync(exit.GetProperty("dir").GetString()! + "\r\n");
            await Until(() => session.Read(() => session.Gmcp.State.CurrentRoom == destination && session.Gmcp.State.Owns("Anomaly.Room.Info")), "active movement confirmation");
            JsonElement back = session.Read(() => session.Gmcp.State.Snapshot("Anomaly.Room.Info")!.Value)
                .GetProperty("exits").EnumerateArray().First(e => e.GetProperty("to").GetString() == origin);
            await session.Telnet.SendTextAsync(back.GetProperty("dir").GetString()! + "\r\n");
            await Until(() => session.Read(() => session.Gmcp.State.CurrentRoom == origin && session.Gmcp.State.RosterComplete), "return baseline");
            await session.Telnet.SendGmcpAsync("Core.Supports.Remove", new[] { "Anomaly.Mob" });
            await Until(() => session.Read(() => session.Gmcp.State.StaticMonsters.Count == 0), "scoped monster removal");
            await session.Telnet.SendGmcpAsync("Core.Supports.Add", new[] { "Anomaly.Mob 1" });
            await Until(() => session.Read(() => session.Gmcp.State.RosterComplete), "subscription restoration");
            await session.Gmcp.RefreshAsync();
            await Login(sysop, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_SYSOP_ACCOUNT")!, Environment.GetEnvironmentVariable("MUDPLAY_GMCP_SYSOP_PASSWORD")!);
            await sysop.Telnet.SendTextAsync("sysop configure gmcp off\r\n");
            await Until(() => session.Read(() => !session.Gmcp.State.Compatible), "WONT fallback");
            session.ClearText(); await session.Telnet.SendTextAsync("look\r\n");
            await Until(() => session.Transcript.Contains("[HP=", StringComparison.Ordinal), "text fallback prompt");
            Assert.False(session.Read(() => session.Gmcp.State.Owns("Anomaly.Char.Vitals")));
            await sysop.Telnet.SendTextAsync("sysop configure gmcp on\r\n");
            await Until(() => session.Read(() => session.Gmcp.State.Owns("Anomaly.Char.Vitals") && session.Gmcp.State.RosterComplete), "fresh handshake after WILL");
            await session.Telnet.SendTextAsync("quit\r\n");
            await session.Telnet.DisconnectAsync();
            using Session reconnect = new();
            try
            {
                await Login(reconnect, account, password);
                Assert.Equal(origin, reconnect.Read(() => reconnect.Gmcp.State.CurrentRoom));
                Assert.True(reconnect.Read(() => reconnect.Gmcp.State.RosterComplete));
                await reconnect.Telnet.SendTextAsync("quit\r\n");
            }
            finally { await reconnect.Telnet.DisconnectAsync(); }
        }
        finally
        {
            await sysop.Telnet.SendTextAsync("sysop configure gmcp on\r\n");
            await session.Telnet.DisconnectAsync();
            await sysop.Telnet.DisconnectAsync();
        }
    }

    [Theory]
    [InlineData("01-handshake")]
    [InlineData("02-walk")]
    [InlineData("03-fight")]
    [InlineData("04-leave-and-return")]
    [InlineData("06-resubscribe")]
    [InlineData("07-recovery-request")]
    [InlineData("09-reconnect")]
    [InlineData("10-gmcp-off-midsession")]
    [InlineData("12-transport")]
    public void CapturedServerSessions_ReachExistingModelsWithoutContractFailures(string name)
    {
        using Session session = new();
        session.ReplayCapture(name);
        Assert.True(session.Player.HasPromptData);
        Assert.DoesNotContain(session.Log.Snapshot(), entry => entry.Source == "GMCP" &&
            (entry.Message.Contains("Rejected", StringComparison.Ordinal) || entry.Message.Contains("Cannot apply", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("01-handshake")]
    [InlineData("02-walk")]
    [InlineData("03-fight")]
    [InlineData("04-leave-and-return")]
    [InlineData("06-resubscribe")]
    [InlineData("07-recovery-request")]
    public void CapturedServerSessions_ShadowParityHasNoUnexplainedDifferences(string name)
    {
        using Session session = new(active: false);
        session.ReplayCapture(name);
        Assert.Empty(session.Log.Snapshot().Where(entry => entry.Source == "GMCP shadow").Select(entry => entry.Message));
    }

    [Fact]
    public void ActiveVitals_OverrideTextOnlyUntilTheirOwnPackageIsReset()
    {
        using Session session = new(); session.Replay("01-handshake");
        Assert.Equal(150, session.Player.Hp); Assert.Equal(150, session.Player.MaxHp);
        session.Text("[HP=3]:\r\n"); Assert.Equal(150, session.Player.Hp);
        session.Send("Anomaly.Session.Reset", new { scope = "package", package = "Anomaly.Char.Vitals" });
        session.Text("[HP=7]:\r\n"); Assert.Equal(7, session.Player.Hp);
        Assert.True(session.Gmcp.State.Owns("Anomaly.Char.Status"));
    }

    [Fact]
    public void RoomReset_DoesNotLoseTheMoveItsBaselineMustConfirm()
    {
        using Session session = new(); session.Replay("01-handshake");
        int answers = 0; session.Tracker.MoveConfirmed += () => answers++;
        session.Tracker.NoteMoveSent(Direction.N);
        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,2" });
        // Between the reset and the arriving room the position is still known; an Unknown here sends the walker into an rm resync each step.
        Assert.NotEqual(RoomConfidence.Unknown, session.Tracker.State.Confidence);
        Assert.Equal(new RoomKey(1, 1), session.Tracker.State.CurrentRoom!.Key);
        Dictionary<string, JsonElement> room = GmcpStateTests.Frames("01-handshake").First(f => f.TryGetProperty("pkg", out JsonElement package) && package.GetString() == "Anomaly.Room.Info")
            .GetProperty("msg").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        room["room"] = JsonSerializer.SerializeToElement("1,2");
        session.Send("Anomaly.Room.Info", room);
        Assert.Equal(1, answers);
        Assert.False(session.Tracker.HasQueuedMoves);
        Assert.Equal(new RoomKey(1, 2), session.Tracker.State.CurrentRoom!.Key);
        Assert.NotNull(session.Graph.GetRoom(new RoomKey(1, 1)));
    }

    [Fact]
    public void CharacterReset_DropsOldMovementBeforeTheNewCharactersBaseline()
    {
        using Session session = new(); session.Replay("01-handshake");
        session.Tracker.NoteMoveSent(Direction.N);
        session.Send("Anomaly.Session.Reset", new { scope = "all", reason = "character" });
        Assert.False(session.Tracker.HasQueuedMoves);
        Assert.Null(session.Tracker.State.CurrentRoom);
        session.Replay("01-handshake");
        session.Tracker.NoteMoveSent(Direction.N);
        session.Gmcp.Disconnected();
        session.Replay("09-reconnect");
        Assert.False(session.Tracker.HasQueuedMoves);
    }

    [Fact]
    public void ActiveStats_KeepUnprovidedIdentityLivesAndCpOnText()
    {
        using Session session = new(); session.Replay("01-handshake");
        Assert.Equal(70, session.Stats.Strength); Assert.Equal(12, session.Stats.Level);
        Assert.Equal(24, session.Stats.ArmourClass); Assert.Equal(4, session.Stats.MaxArmourClass);
        session.StatParser.ObserveOutbound(Encoding.ASCII.GetBytes("stat\r"));
        session.Text("Name: Test Hero                  Lives/CP:           8/15\r\n");
        session.Text("Class: Warrior        Level: 1        Stealth:            65\r\n");
        session.Text("Strength:   3       Agility:   4          Tracking:     0\r\n");
        Assert.Equal("Test Hero", session.Stats.Name); Assert.Equal(8, session.Stats.Lives); Assert.Equal(15, session.Stats.Cp);
        Assert.Equal(70, session.Stats.Strength); Assert.Equal(12, session.Stats.Level);
    }

    [Fact]
    public void ActiveRoster_ReachesCombatModelsAndDeathClearsTheGate()
    {
        using Session session = new(); session.Replay("07-recovery-request");
        RoomEntity monster = Assert.Single(session.Classifier.Current!.Value.Entities.Where(e => e.Kind == EntityKind.Monster));
        Assert.Equal(102, monster.RuntimeId); Assert.Equal(12, monster.MonsterNumber);
        Assert.True(session.Combat.HasRoomNpc);
        session.Send("Anomaly.Mob.Gone", new { uid = 102, room = session.Gmcp.State.CurrentRoom, why = "died" });
        Assert.Empty(session.Classifier.Current!.Value.Entities);
        Assert.False(session.Combat.HasRoomNpc);
    }

    [Fact]
    public void WontAndReconnect_RestoreTextThenFreshServerState()
    {
        using Session session = new(); session.Replay("07-recovery-request");
        session.Gmcp.Disconnected();
        session.Text("[HP=9/MA=2]:\r\n");
        Assert.Equal(9, session.Player.Hp); Assert.Empty(session.Gmcp.State.StaticMonsters);
        Assert.Null(session.Classifier.Current);
        session.Replay("09-reconnect");
        Assert.True(session.Gmcp.State.RosterComplete);
        Assert.True(session.Gmcp.State.Owns("Anomaly.Char.Vitals"));
    }

    [Fact]
    public void RoomProjection_PreservesCommandTeleportsAndKnownRemoteActionRequirements()
    {
        JsonElement value = GmcpStateTests.Frames("01-handshake").First(frame => frame.TryGetProperty("pkg", out JsonElement package) && package.GetString() == "Anomaly.Room.Info").GetProperty("msg");
        Dictionary<string, JsonElement> fields = value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        List<Dictionary<string, JsonElement>> exits = value.GetProperty("exits").EnumerateArray().Select(e => e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)).ToList();
        exits[0]["kind"] = JsonSerializer.SerializeToElement("hidden");
        exits[0]["hidden"] = JsonSerializer.SerializeToElement(true);
        fields["exits"] = JsonSerializer.SerializeToElement(exits);
        Room previous = new() { Key = new(1, 1), Name = "Town Square", Exits = new Dictionary<Direction, RoomExit>
        {
            [Direction.N] = new(new(1, 2), RoomExitHint.MultiActionHidden, "remote actions"),
            [Direction.Teleport] = new(new(9, 9), RoomExitHint.Teleport, null, TextCommands: new[] { "go portal" })
        } };
        Room projected = GmcpProjection.Room(JsonSerializer.SerializeToElement(fields), previous);
        Assert.Equal(RoomExitHint.MultiActionHidden, projected.Exits[Direction.N].Hint);
        Assert.Equal(new RoomKey(9, 9), projected.Exits[Direction.Teleport].Target);
        Assert.Equal(0u, projected.ExitMask & (1u << (int)Direction.Teleport));
    }

    [Fact]
    public void RoomSnapshot_UpdatesGraphAndPreservesLockedAndHiddenExits()
    {
        using Session session = new(); session.Replay("02-walk");
        Assert.Equal(GmcpProjection.Key(session.Gmcp.State.CurrentRoom!), session.Tracker.State.CurrentRoom!.Key);
        Assert.Equal(RoomConfidence.Confirmed, session.Tracker.State.Confidence);
        Assert.NotNull(session.Graph.GetRoom(session.Tracker.State.CurrentRoom.Key));
    }

    [Fact]
    public void ShadowMode_ReportsOnlyComparableTextFieldsAndDoesNotOwnModels()
    {
        using Session session = new(active: false); session.Replay("01-handshake");
        Assert.False(session.Player.HasPromptData);
        session.Text("[HP=4]:\r\n");
        Assert.Equal(4, session.Player.Hp);
        Assert.Contains(session.Log.Snapshot(), item => item.Source == "GMCP shadow" && item.Message.Contains("Char.Vitals.hp", StringComparison.Ordinal));
        Assert.DoesNotContain(session.Log.Snapshot(), item => item.Source == "GMCP shadow" && item.Message.Contains("drop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShadowVisibility_UsesTheTextWhenAHiddenOccupantIsActuallyVisible()
    {
        using Session session = new(active: false); session.Replay("07-recovery-request");
        session.Send("Anomaly.Room.Occupants", new { room = session.Gmcp.State.CurrentRoom, mobs = new[] { new { uid = 102, hidden = true } }, players = Array.Empty<object>() });
        session.Text("Also here: large giant rat.\r\n");
        Assert.DoesNotContain(session.Log.Snapshot(), entry => entry.Source == "GMCP shadow");
    }

    [Fact]
    public void CurrentMonsterHp_ComesFromLiveBaselineAndDeltaWithoutDoubleCountingTextDamage()
    {
        using Session session = new(); session.Replay("07-recovery-request");
        RoomEntity monster = session.Classifier.Current!.Value.Entities.Single(e => e.Kind == EntityKind.Monster);
        Assert.Equal(12, session.Hp.Estimate(monster.RawName));
        session.Send("Anomaly.Mob.Update", new { uid = monster.RuntimeId, room = session.Gmcp.State.CurrentRoom, full = false, hp = 5 });
        Assert.Equal(5, session.Hp.Estimate(monster.RawName));
        Assert.Equal(5, session.Hp.TargetOf(monster.RawName)!.Value.Hp);
    }

    [Fact]
    public void ActiveStatus_UpdatesConditionsAndStealthAndReleasesThemOnFallback()
    {
        using Session session = new(); session.Replay("01-handshake");
        Dictionary<string, JsonElement> value = session.Gmcp.State.Snapshot("Anomaly.Char.Status")!.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        foreach (string field in new[] { "blind", "poisoned", "diseased", "confused", "sneaking" }) value[field] = JsonSerializer.SerializeToElement(true);
        session.Send("Anomaly.Char.Status", value);
        Assert.True(session.Conditions.IsBlinded); Assert.True(session.Conditions.IsPoisoned);
        Assert.True(session.Conditions.IsDiseased); Assert.True(session.Conditions.IsConfused);
        Assert.True(session.Stealth.IsSneaking);
        session.Gmcp.Disconnected();
        Assert.False(session.Conditions.IsBlinded);
    }

    [Fact]
    public void LairDeadline_IsEligibilityForCurrentRoomAndExpiresWithRoomState()
    {
        using Session session = new(); session.Replay("07-recovery-request");
        RoomKey room = GmcpProjection.Key(session.Gmcp.State.CurrentRoom!);
        Assert.True(session.Gmcp.LairEligibility(room).Known);
        Assert.NotNull(session.Gmcp.LairEligibility(room).EligibleAt);
        session.Send("Anomaly.Session.Reset", new { scope = "room", room = "1,999" });
        Assert.False(session.Gmcp.LairEligibility(room).Known);
    }

    [Fact]
    public void ActiveStealth_IsNotClearedByTheTextArrivalWatchdog()
    {
        using Session session = new(); session.Replay("01-handshake");
        Dictionary<string, JsonElement> status = session.Gmcp.State.Snapshot("Anomaly.Char.Status")!.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        status["sneaking"] = JsonSerializer.SerializeToElement(true);
        session.Send("Anomaly.Char.Status", status);
        session.Stealth.NoteRoomChanged();
        session.Stealth.NoteRoomChanged();
        Assert.True(session.Stealth.IsSneaking);
        Assert.True(session.Stealth.IsStealthedHere);
    }

    [Fact]
    public async Task ExplicitRefresh_RequestsSnapshotsAndMonsterBaselines()
    {
        using Session session = new(); session.Replay("07-recovery-request");
        List<(string Package, JsonElement Payload)> requests = new();
        session.Gmcp.SetSender((package, payload) => { requests.Add((package, JsonSerializer.SerializeToElement(payload))); return Task.CompletedTask; });
        await session.Gmcp.RefreshAsync();
        Assert.Contains(requests, r => r.Payload.GetProperty("package").GetString() == "Anomaly.Mob.Info" && r.Payload.GetProperty("uid").GetInt64() == 102);
        Assert.Contains(requests, r => r.Payload.GetProperty("package").GetString() == "Anomaly.Char.Vitals");
    }

    [Fact]
    public void Settings_RoundTripPerPackageModesWithoutAffectingOtherBoards()
    {
        BbsProfile profile = new() { GmcpModes = new() { ["Anomaly.Char.Vitals"] = GmcpMode.Active } };
        BbsProfile restored = JsonSerializer.Deserialize<BbsProfile>(JsonSerializer.Serialize(profile))!;
        Assert.Equal(GmcpMode.Active, restored.GmcpModes["Anomaly.Char.Vitals"]);
        Assert.Empty(new BbsProfile().GmcpModes);
    }
}

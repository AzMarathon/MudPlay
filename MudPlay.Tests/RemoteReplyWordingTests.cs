using System.Reflection;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Cash;
using MudPlay.Game.Combat;
using MudPlay.Game.Inventory;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// The wording of three answers: what a hide sweep kept back, the profile roster,
// and a full party.
public sealed class RemoteReplyWordingTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ChatLogEntry Telepath(string msg) =>
        new(Now, ChatChannel.TelepathIncoming, "Friend", msg, $"Friend telepaths: {msg}");

    private static (RemoteCommandManager engine, MessageRouter router, PartyState party) Engine()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        PartyState party = new();
        PlayerDatabase players = new();
        players.RecordObservation("Friend", @class: null, race: null, alignment: null,
            title: null, gang: null, role: null, nowUtc: Now);
        players.EditCustomization("Friend", new PlayerCustomization(RemoteControls: PlayerRemoteControls.All));
        return (new RemoteCommandManager(new ChatRouter(router), party, players), router, party);
    }

    private static string Only(RemoteCommandManager engine) =>
        Encoding.Latin1.GetString(Assert.Single(engine.LastSentForTests));

    // The extractor's line event, raised the way a line from the game raises it.
    private static void Feed(LineExtractor lines, string text)
    {
        FieldInfo? field = typeof(LineExtractor).GetField(
            "LineEmitted", BindingFlags.Instance | BindingFlags.NonPublic);
        if (field?.GetValue(lines) is Action<LineExtractor.EmittedLine> handler)
        {
            handler(new LineExtractor.EmittedLine(
                text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false));
        }
    }

    [Theory]
    [InlineData("@drop-all", "can't be dropped: cursed ring)")]
    [InlineData("@hide-all", "can't be hidden: cursed ring)")]
    public void ASweep_NamesWhatItKeptBack_InPlainWords(string command, string tail)
    {
        var (engine, router, party) = Engine();
        InventoryManager inventory = new(log: null, itemWeightResolver: null, slotResolver: null);
        LineExtractor lines = new(new TerminalEmulator(80, 24));
        inventory.AttachLineExtractor(lines);
        CashSettings cash = new();
        _ = new InventoryActionHandler(engine, inventory, new GroundItemTracker(router, new CurrencyNaming()),
            party, () => cash, new CurrencyNaming(), cannotDrop: (name, _) => name == "cursed ring");
        Feed(lines, "You are carrying a rusty dagger, cursed ring.");
        Feed(lines, "You have no keys.");
        Feed(lines, "Wealth:    0 copper farthings");
        Feed(lines, "Encumbrance:    36/2880  -  Light  [1%]");

        engine.DispatchForTests(Telepath(command));

        Assert.EndsWith(tail + "}\r", Only(engine));
    }

    // The roster is written in braces for the terminal; the reply's own pair is
    // the only one that goes out.
    [Fact]
    public void ProfileRoster_GoesOutInOnePairOfBraces()
    {
        var (engine, _, _) = Engine();
        _ = new ProfileSwapHandler(engine, TwoProfiles());

        engine.DispatchForTests(Telepath("@profile"));

        Assert.Equal("/Friend {Current: 1)Fire, On Standby: 2)Cold}\r", Only(engine));
    }

    // The swap report is the terminal's, with a long dash and dots; the copy that
    // goes to the asker has neither.
    [Fact]
    public void ProfileSwitch_AnswersInPlainAscii()
    {
        var (engine, _, _) = Engine();
        _ = new ProfileSwapHandler(engine, TwoProfiles());

        engine.DispatchForTests(Telepath("@profile 2"));

        Assert.Equal("/Friend {Combat profile 2 (Cold) - normal: lbol, drain: drai}\r", Only(engine));
    }

    private static CombatProfileManager TwoProfiles()
    {
        CombatSpellProfile fire = new() { Name = "Fire" };
        CombatSpellProfile cold = new() { Name = "Cold" };
        cold.NormalAttackSpell.SpellName = "lbol";
        cold.DrainSpell.SpellName = "drai";
        CharacterProfile profile = new()
        {
            CombatProfiles = new CombatProfileSettings { Profiles = { fire, cold }, ActiveId = fire.Id },
        };
        CombatSettings combat = new();
        HealthSettings health = new();
        SpellsSettings spells = new();
        return new CombatProfileManager(
            profile: () => profile,
            readCombat: () => combat,
            writeCombat: _ => { },
            readHealth: () => health,
            writeHealth: _ => { },
            readSpells: () => spells,
            writeSpells: _ => { },
            equipment: () => null,
            save: () => { });
    }

    [Fact]
    public void FullParty_WithOneOtherMemberListed_ReadsInTheSingular()
    {
        var (engine, _, party) = Engine();
        PartyEssentialHandlers handlers = new(engine, new PlayerState(), party);
        handlers.SetWireSender(_ => { });
        party.IsInParty = true;
        party.SelfIsLeader = true;
        party.Members.Add(new PartyMember { Name = "Tank" });
        for (int i = 0; i < 5; i++) party.Members.Add(new PartyMember { Name = string.Empty });

        engine.DispatchForTests(Telepath("@invite"));

        Assert.Equal("/Friend {My party is full: Tank is following me.}\r", Only(engine));
    }
}

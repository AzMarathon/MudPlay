using MudPlay.Game;
using MudPlay.Game.Map;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// A loop set to "Wait for priority buff spells" stands one step short of a lair while
// the buffs ticked "Priority buff" that are due are cast, holds every other buff back,
// and enters once none is left due. The hold and the cast scheduler are wired to each
// other here as the app wires them, so what is "due" is the scheduler's own answer.
public sealed class LairEntryPriorityBuffWaitTests
{
    private sealed class Harness : IDisposable
    {
        public MessageRouter Router { get; } = new();
        public LogService Log { get; } = new();
        public PlayerState State { get; } = new();
        public CastCoordinator Cast { get; }
        public CastingDirector Director { get; }
        public MovementCoordinator Coordinator { get; } = new();
        public LairEntryDebuffHold Hold { get; }
        public List<string> CastsSent { get; } = new();
        public List<string> ItemsUsed { get; } = new();
        public BuffSettings Buffs { get; } = new();
        public SpellsSettings Spells { get; } = new();
        public HealthSettings Health { get; } = new();
        public Dictionary<string, int> ManaCosts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool AutoHeal { get; set; } = true;
        public bool AutoBless { get; set; } = true;
        public bool SneakKept { get; set; }
        // The combat profile's debuff, offered once the loop is in the lair.
        public string? DebuffOwed { get; set; }
        public DateTime Now { get; set; } = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        public List<Action> Scheduled { get; } = new();

        public Harness()
        {
            DefaultPatterns.Seed(Router);
            Cast = new CastCoordinator(Router, Log);
            Cast.SetWireSender(_ => { });
            Cast.CastSent += CastsSent.Add;
            Director = new CastingDirector(State, Cast,
                readSpells: () => Spells,
                readHealth: () => Health,
                isEnabled: () => AutoHeal,
                log: Log);
            Director.SetAutoBlessGate(() => AutoBless);
            Director.SetManaCostLookup(code => ManaCosts.TryGetValue(code, out int cost) ? cost : null);
            Director.SetClock(() => Now);
            Director.SetPartyBuffSource(() => Buffs);
            Director.SetStealthMaintenanceDeferGate(() => SneakKept);
            Director.SetBuffDurationSources(_ => (string.Empty, 600), _ => null);
            Director.SetItemCastSource(_ => 600, token => { ItemsUsed.Add(token); return true; });
            Director.SetCombatDebuffSource(
                () => DebuffOwed is { } spell ? (spell, null) : null,
                () => DebuffOwed = null);

            Hold = new LairEntryDebuffHold(Coordinator,
                slotUsed: () => Director.BetweenRoundSlotUsed,
                castDue: Director.HasCastDue,
                scheduleAfter: (_, action) => Scheduled.Add(action),
                now: () => new DateTimeOffset(Now),
                log: Log,
                buffsDue: Director.BuffsDue);
            Director.SetLairEntryBuffHold(
                () => Hold.BlockingBuffs, () => Hold.BlockingOrdinaryBuffs, Hold.NotePriorityBuffCast);

            // Healthy and full of mana, so only what a test sets up is due. The buffs
            // are added after this, so nothing is cast until a test runs a pass.
            State.MaxHp = 200;
            State.Hp = 200;
            State.MaxMa = 100;
            State.Ma = 100;
            State.HasPromptData = true;
        }

        public void AddBuff(string spell, bool priority) =>
            Buffs.Slots.Add(new BuffSlot { Spell = spell, CastOnSelf = true, PriorityBuff = priority });

        public bool Held => Coordinator.IsGateAsserted(MovementCoordinator.LairDebuffGate);

        public bool AskToEnter() => Hold.ReadyToEnter(LairEntryDebuffMode.WaitForPriorityBuffs);

        // Let the hold's timer look again.
        public void Poll()
        {
            Action[] due = Scheduled.ToArray();
            Scheduled.Clear();
            foreach (Action a in due) a();
        }

        // The game's round turns over: the between-round cast is free again.
        public void NextRound(int seconds = 5)
        {
            Now = Now.AddSeconds(seconds);
            Cast.OnCombatTick();
            Director.NotifyRoundComplete();
        }

        // The game refuses the cast just sent: the scheduler takes its timer back, so
        // the buff is due again next round.
        public void GameFizzlesTheCast(string spell) =>
            Router.Dispatch(new LineExtractor.EmittedLine(
                $"You attempt to cast {spell}, but fail.", Array.Empty<CellAttributes>(),
                DateTimeOffset.UtcNow, IsPromptLine: false));

        public IEnumerable<string> InfoLog(string source) =>
            Log.Snapshot().Where(e => e.Severity == LogSeverity.Info && e.Source == source).Select(e => e.Message);

        public void Dispose()
        {
            Director.Dispose();
            Cast.Dispose();
        }
    }

    [Fact]
    public void PriorityBuffDue_IsWaitedForAndCast_ThenTheLoopEnters_AndTheDebuffGetsTheCast()
    {
        using Harness h = new();
        h.AddBuff("star", priority: true);
        h.AddBuff("plai", priority: false);

        Assert.False(h.AskToEnter());                       // star is due: stand outside
        Assert.True(h.Held);

        Assert.Equal("star", h.Director.Evaluate());        // the wait lets it through
        h.Poll();
        Assert.True(h.Held);                                // the cast is spent this round

        h.NextRound();
        h.Poll();

        Assert.False(h.Held);                               // nothing priority left due: go in
        Assert.True(h.AskToEnter());
        Assert.False(h.Director.BetweenRoundSlotUsed);      // and the cast is free for the debuff
        h.DebuffOwed = "dbuf";
        Assert.Equal("dbuf", h.Director.Evaluate());        // which goes out ahead of the held plai
        Assert.Equal(new[] { "star", "dbuf" }, h.CastsSent);
    }

    [Fact]
    public void TwoPriorityBuffsDue_AreCastOverTwoRounds_ThenTheLoopEnters()
    {
        using Harness h = new();
        h.AddBuff("sta1", priority: true);
        h.AddBuff("sta2", priority: true);

        Assert.False(h.AskToEnter());
        Assert.Equal("sta1", h.Director.Evaluate());
        Assert.Null(h.Director.Evaluate());                 // one a round
        h.Poll();

        h.NextRound();
        h.Poll();
        Assert.True(h.Held);                                // the second is still due
        Assert.Equal("sta2", h.Director.Evaluate());
        h.Poll();

        h.NextRound();
        h.Poll();

        Assert.False(h.Held);
        Assert.Equal(new[] { "sta1", "sta2" }, h.CastsSent);
    }

    [Fact]
    public void OrdinaryBuffDue_IsNotCastDuringTheWait_AndGoesOutAfterTheFight()
    {
        using Harness h = new();
        h.AddBuff("plai", priority: false);
        h.AddBuff("star", priority: true);

        Assert.False(h.AskToEnter());
        Assert.Equal("star", h.Director.Evaluate());
        h.Poll();
        h.NextRound();
        Assert.Null(h.Director.Evaluate());                 // plai is due and the cast is free, yet held
        h.Poll();
        Assert.False(h.Held);
        Assert.Null(h.Director.Evaluate());                 // held for the step in as well
        Assert.Equal(new[] { "star" }, h.CastsSent);

        // The step landed and the fight is over: the loop's next step is no lair.
        Assert.True(h.Hold.ReadyToEnter(LairEntryDebuffMode.Off));
        Assert.Equal("plai", h.Director.Evaluate());
    }

    [Fact]
    public void NoPriorityBuffDue_EntersAsSoonAsTheCastIsFree_WithTheOthersHeld()
    {
        using Harness h = new();
        h.AddBuff("plai", priority: false);

        // The cast is free and only an ordinary buff is due: straight in, as Block spells.
        Assert.True(h.AskToEnter());
        Assert.False(h.Held);
        Assert.Null(h.Director.Evaluate());
        Assert.Empty(h.CastsSent);
    }

    [Fact]
    public void NoPriorityBuffDue_CastSpentOnArrival_WaitsOutThatRoundOnly()
    {
        using Harness h = new();
        h.AddBuff("plai", priority: false);
        h.Director.MarkBetweenRoundSlotUsed();              // something took this round's cast

        Assert.False(h.AskToEnter());
        Assert.True(h.Held);

        h.NextRound();
        Assert.Null(h.Director.Evaluate());                 // plai doesn't take the freed cast
        h.Poll();

        Assert.False(h.Held);
        Assert.Empty(h.CastsSent);
    }

    // Too little mana for the spell itself, with mana above the buff's own floor: the
    // scheduler counts it due and skips the cast each pass. With no priority buff going
    // out, 18 seconds end the wait.
    [Fact]
    public void PriorityBuffThatCannotBePaidFor_EndsTheWaitAfterEighteenSeconds_AndTheLogNamesIt()
    {
        using Harness h = new();
        h.AddBuff("star", priority: true);
        h.ManaCosts["star"] = 500;

        Assert.False(h.AskToEnter());
        Assert.Null(h.Director.Evaluate());
        h.NextRound(seconds: 10);
        Assert.Null(h.Director.Evaluate());
        h.Poll();
        Assert.True(h.Held);                                // 10 s in: still waiting on it

        h.NextRound(seconds: 10);
        h.Poll();

        Assert.False(h.Held);                               // 20 s in: past the 18 s limit
        Assert.Empty(h.CastsSent);
        Assert.Contains(h.InfoLog("Loop"),
            m => m.StartsWith("no priority buff went out for 18 seconds (priority buffs still due: star)"));
    }

    // Held to keep a sneak: due, never sent. The same 18 seconds end the wait.
    [Fact]
    public void PriorityBuffHeldToKeepASneak_EndsTheWaitAfterEighteenSeconds()
    {
        using Harness h = new() { SneakKept = true };
        h.AddBuff("star", priority: true);

        Assert.False(h.AskToEnter());
        Assert.Null(h.Director.Evaluate());
        h.NextRound(seconds: 20);
        h.Poll();

        Assert.False(h.Held);
        Assert.Empty(h.CastsSent);
    }

    // Every due priority buff is waited for, however many: five take five rounds (six
    // here, the cast being spent on arrival), well past where Wait for spells stops.
    [Fact]
    public void FivePriorityBuffsDue_AreAllCast_ThenTheLoopEnters()
    {
        using Harness h = new();
        for (int i = 1; i <= 5; i++) h.AddBuff($"sta{i}", priority: true);
        h.AddBuff("plai", priority: false);
        h.Director.MarkBetweenRoundSlotUsed();              // spent before we got here

        Assert.False(h.AskToEnter());
        for (int i = 1; i <= 5; i++)
        {
            h.NextRound(); h.Poll();
            Assert.True(h.Held);
            Assert.Equal($"sta{i}", h.Director.Evaluate());
            h.Poll();
        }
        h.NextRound();
        Assert.Null(h.Director.Evaluate());                 // none left due; plai stays held
        h.Poll();

        Assert.False(h.Held);                               // 30 seconds after arriving
        Assert.Equal(new[] { "sta1", "sta2", "sta3", "sta4", "sta5" }, h.CastsSent);
        Assert.Equal("no priority buff is due and the between-round cast is free — entering the lair",
            h.InfoLog("Loop").Last());
    }

    // One that can't be paid for among them: the ones that can go out do, and the wait
    // ends 18 seconds after the last of those, not 18 seconds after it began.
    [Fact]
    public void OneUncastableAmongThem_TheOthersGoOut_ThenEntryEighteenSecondsAfterTheLast()
    {
        using Harness h = new();
        h.AddBuff("sta1", priority: true);
        h.AddBuff("sta2", priority: true);
        h.AddBuff("cost", priority: true);
        h.ManaCosts["cost"] = 500;

        Assert.False(h.AskToEnter());
        Assert.Equal("sta1", h.Director.Evaluate());        // at 0 s
        h.Poll();
        h.NextRound(); h.Poll();
        Assert.Equal("sta2", h.Director.Evaluate());        // at 5 s: the last to go out
        h.Poll();
        h.NextRound();
        Assert.Null(h.Director.Evaluate());                 // cost is due and can't be paid for
        h.Poll();
        h.NextRound(seconds: 12);                           // 22 s in, 17 s since sta2
        Assert.Null(h.Director.Evaluate());
        h.Poll();
        Assert.True(h.Held);

        h.NextRound(seconds: 2);                            // 19 s since sta2
        h.Poll();

        Assert.False(h.Held);
        Assert.Equal(new[] { "sta1", "sta2" }, h.CastsSent);
        Assert.Contains(h.InfoLog("Loop"),
            m => m.StartsWith("no priority buff went out for 18 seconds (priority buffs still due: cost)"));
    }

    // The game refuses the buff every time, so it is due again every round and each
    // send restarts the 18 seconds. Its third send is the last the wait allows.
    [Fact]
    public void PriorityBuffRefusedEveryRound_EndsTheWaitAtItsThirdSend_AndTheLogSaysSo()
    {
        using Harness h = new();
        h.AddBuff("star", priority: true);

        Assert.False(h.AskToEnter());
        for (int send = 1; send <= LairEntryDebuffHold.MaxSendsOfOnePriorityBuff; send++)
        {
            Assert.True(h.Held);
            Assert.Equal("star", h.Director.Evaluate());
            h.GameFizzlesTheCast("star");
            h.Poll();
            h.NextRound(); h.Poll();
        }

        Assert.False(h.Held);                               // a fourth send is not waited for
        Assert.Null(h.Director.Evaluate());                 // nor made: buffs are held for the step in
        Assert.Equal(new[] { "star", "star", "star" }, h.CastsSent);
        Assert.Contains(h.InfoLog("Loop"), m => m.StartsWith(
            "priority buff star went out 3 times in this wait, the most one may (priority buffs still due: star)"));
    }

    // The same five buffs under Wait for spells: its three rounds are what they were.
    [Fact]
    public void WaitForSpells_StillStopsAtThreeRounds_WithFivePriorityBuffsDue()
    {
        using Harness h = new();
        for (int i = 1; i <= 5; i++) h.AddBuff($"sta{i}", priority: true);

        Assert.False(h.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
        for (int i = 1; i <= LairEntryDebuffHold.MaxRoundsWaited; i++)
        {
            Assert.Equal($"sta{i}", h.Director.Evaluate());
            h.Poll();
            h.NextRound(); h.Poll();
        }

        Assert.False(h.Held);
        Assert.Equal(new[] { "sta1", "sta2", "sta3" }, h.CastsSent);
        Assert.Contains(h.InfoLog("Loop"),
            m => m.StartsWith("waited the limit of 3 rounds of casts with buffs still due (sta4, sta5)"));
    }

    // What the scheduler isn't casting right now isn't "due", so it isn't waited for.
    [Fact]
    public void PriorityBuffTheSchedulerIsNotCasting_IsNotWaitedFor()
    {
        using Harness belowFloor = new();
        belowFloor.AddBuff("star", priority: true);
        belowFloor.State.Ma = 40;                           // under the buff's own 50% floor
        Assert.True(belowFloor.AskToEnter());

        using Harness blessOff = new() { AutoBless = false };
        blessOff.AddBuff("star", priority: true);
        Assert.True(blessOff.AskToEnter());

        using Harness everythingOff = new() { AutoBless = false, AutoHeal = false };
        everythingOff.AddBuff("star", priority: true);
        Assert.True(everythingOff.AskToEnter());

        using Harness whenFull = new();
        whenFull.Buffs.Slots.Add(new BuffSlot
            { Spell = "star", CastOnSelf = true, PriorityBuff = true, OnlyWhenHpFull = true });
        whenFull.State.Hp = 100;                            // not topped off: its condition isn't met
        Assert.True(whenFull.AskToEnter());
    }

    // Heals and cures are as in Wait for spells: never held, and a due one is waited for.
    [Fact]
    public void DueHeal_IsCastAndWaitedFor_WhileOrdinaryBuffsStayHeld()
    {
        using Harness h = new();
        h.Spells.MajorHealSpell = "heal";
        h.AutoHeal = false;                                 // take the wound without a pass healing it
        h.State.Hp = 70;                                    // 35%: under the 40% major-heal trigger
        h.AutoHeal = true;
        h.AddBuff("plai", priority: false);

        Assert.False(h.AskToEnter());                       // the heal is due: stand outside
        Assert.Equal("heal", h.Director.Evaluate());
        h.Poll();
        h.State.Hp = 200;
        h.NextRound();
        h.Poll();

        Assert.False(h.Held);
        Assert.Equal(new[] { "heal" }, h.CastsSent);
    }

    // An item-cast buff ticked priority is a priority buff to the scheduler, so it is
    // used during the wait like a cast one.
    [Fact]
    public void ItemCastPriorityBuff_IsUsedDuringTheWait()
    {
        using Harness h = new();
        h.AddBuff("#silver wand", priority: true);

        Assert.False(h.AskToEnter());
        Assert.Equal("#silver wand", h.Director.Evaluate());
        Assert.Equal(new[] { "#silver wand" }, h.ItemsUsed);
        h.Poll();
        h.NextRound();
        h.Poll();

        Assert.False(h.Held);
    }

    [Fact]
    public void TheLog_SaysWhatIsWaitedFor_WhatIsHeld_EachCast_AndWhyTheWaitEnded()
    {
        using Harness h = new();
        h.AddBuff("plai", priority: false);
        h.AddBuff("star", priority: true);

        Assert.False(h.AskToEnter());
        h.Director.Evaluate();
        h.Poll();
        h.NextRound();
        h.Poll();

        Assert.Equal(new[]
        {
            "waiting outside the lair until the debuff can be cast — casting priority buffs first "
                + "(due: star); other buffs held back (due: plai)",
            "priority buff star cast while waiting outside the lair",
            "no priority buff is due and the between-round cast is free — entering the lair",
        }, h.InfoLog("Loop"));
    }

    // The same buffs under the two older modes, to pin that they are untouched: Wait
    // for spells casts both kinds, Block spells casts neither.
    [Fact]
    public void TheOtherModes_TreatPriorityAndOrdinaryBuffsAlike()
    {
        using Harness wait = new();
        wait.AddBuff("plai", priority: false);
        wait.AddBuff("star", priority: true);
        Assert.False(wait.Hold.ReadyToEnter(LairEntryDebuffMode.WaitForSpells));
        Assert.Equal("star", wait.Director.Evaluate());
        wait.Poll(); wait.NextRound(); wait.Poll();
        Assert.True(wait.Held);
        Assert.Equal("plai", wait.Director.Evaluate());

        using Harness block = new();
        block.AddBuff("plai", priority: false);
        block.AddBuff("star", priority: true);
        Assert.True(block.Hold.ReadyToEnter(LairEntryDebuffMode.BlockSpells));
        Assert.Null(block.Director.Evaluate());
    }

    [Fact]
    public void BuffsDue_ListsEachKindInCastOrder_WhetherOrNotTheHoldKeepsItBack()
    {
        using Harness h = new();
        h.AddBuff("pla1", priority: false);
        h.AddBuff("star", priority: true);
        h.AddBuff("pla2", priority: false);

        Assert.False(h.AskToEnter());                       // the ordinary ones are held now

        Assert.Equal(new[] { "star" }, h.Director.BuffsDue(priority: true));
        Assert.Equal(new[] { "pla1", "pla2" }, h.Director.BuffsDue(priority: false));
        Assert.True(h.Director.HasCastDue());

        h.Director.Evaluate();                              // star goes out
        Assert.Empty(h.Director.BuffsDue(priority: true));
        Assert.False(h.Director.HasCastDue());              // the held ones don't count as due
    }
}

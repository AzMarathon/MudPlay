using System;
using System.Collections.Generic;
using System.Linq;
using MudPlay.Game;
using MudPlay.Game.Quests;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The announcer's crossing / dedup / login-dump / toggle state machine, driven with a
// fake eligibility provider (a real one needs a TBInfo crawl — see QuestEligibility).
public sealed class QuestAvailabilityAnnouncerTests
{
    private sealed record FakeQuest(int Flag, string Name, int MinLevel);

    private sealed class Harness : IDisposable
    {
        public PlayerStats Stats { get; } = new() { Level = 1 };
        public StatParser Parser { get; }
        public ProfileService Profile { get; } = new();
        public QuestAvailabilityAnnouncer Announcer { get; }
        public List<string> Announced { get; } = new();
        public int LoginLevel { get; set; }
        private readonly List<FakeQuest> _quests;

        public Harness(params FakeQuest[] quests)
        {
            _quests = quests.ToList();
            Parser = new StatParser(Stats);
            Profile.LoadBlank();   // Current set; AnnounceAvailableQuests defaults true
            Announcer = new QuestAvailabilityAnnouncer(
                Parser, Profile,
                currentLevel: () => LoginLevel,
                eligibleAtLevel: level => _quests
                    .Where(q => q.MinLevel > 0 && q.MinLevel <= level)
                    .Select(q => new QuestAvailabilityInfo(q.Flag, 0, q.Name))
                    .ToList());
            Announcer.QuestBecameAvailable += Announced.Add;
        }

        public void Dispose() { Announcer.Dispose(); Parser.Dispose(); }
    }

    [Fact]
    public void Training_CrossesMinLevel_Announces()
    {
        using var h = new Harness(new FakeQuest(1, "Sunstone Wristband", 10));
        h.Announcer.Observe(5);    // baseline (silent) — quest not yet available
        Assert.Empty(h.Announced);

        h.Announcer.Observe(10);   // trained to the min level
        Assert.Equal(new[] { "Sunstone Wristband" }, h.Announced);
    }

    [Fact]
    public void MultiLevelJump_AnnouncesEveryCrossedQuest()
    {
        using var h = new Harness(
            new FakeQuest(1, "A", 8), new FakeQuest(2, "B", 12), new FakeQuest(3, "C", 20));
        h.Announcer.Observe(5);    // baseline
        h.Announcer.Observe(15);   // jumped past A and B, not C

        Assert.Equal(new[] { "A", "B" }, h.Announced);
    }

    [Fact]
    public void AlreadyAvailableAtBaseline_NotAnnouncedOnLaterTraining()
    {
        using var h = new Harness(new FakeQuest(1, "Early", 3));
        h.Announcer.Observe(5);    // baseline — Early already available, absorbed silently
        h.Announcer.Observe(6);    // train up

        Assert.Empty(h.Announced);
    }

    [Fact]
    public void SameQuest_AnnouncedOnce()
    {
        using var h = new Harness(new FakeQuest(1, "Once", 10));
        h.Announcer.Observe(5);
        h.Announcer.Observe(10);
        h.Announcer.Observe(11);   // still eligible, but already announced

        Assert.Equal(new[] { "Once" }, h.Announced);
    }

    [Fact]
    public void LoginDump_AnnouncesEveryEligibleQuest()
    {
        using var h = new Harness(
            new FakeQuest(1, "A", 5), new FakeQuest(2, "B", 10), new FakeQuest(3, "C", 25))
        { LoginLevel = 12 };

        h.Announcer.AnnounceLoginAvailable();

        Assert.Equal(new[] { "A", "B" }, h.Announced);   // C's gate (25) not met
    }

    [Fact]
    public void ToggleOff_SuppressesButStillTracks()
    {
        using var h = new Harness(new FakeQuest(1, "Q", 10));
        h.Profile.Current!.AnnounceAvailableQuests = false;
        h.Announcer.Observe(5);
        h.Announcer.Observe(10);
        Assert.Empty(h.Announced);

        // Re-enabling doesn't replay the backlog — Q was absorbed while disabled.
        h.Profile.Current!.AnnounceAvailableQuests = true;
        h.Announcer.Observe(11);
        Assert.Empty(h.Announced);
    }
}

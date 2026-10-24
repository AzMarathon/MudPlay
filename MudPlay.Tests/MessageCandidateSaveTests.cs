using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MudPlay.Models.GameData;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The unrecognized-line queue is written on a delay and off the UI thread, and kept
// to MaxPending pending lines. A grind that repeats a staged line used to rewrite the
// whole file on the UI thread with every repeat, thousands of times a night, and one
// of those writes froze the client for 5.3 s (reports Hang-20261021-035337,
// Hang-20261021-065221).
public sealed class MessageCandidateSaveTests : IDisposable
{
    private readonly string _bbs =
        Path.Combine(AppPaths.BbsDir, "test-candsave-" + Guid.NewGuid().ToString("N").Substring(0, 12));
    private readonly ManualTimers _timers = new();

    private string Realm(string name)
    {
        string folder = Path.Combine(_bbs, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_bbs)) Directory.Delete(_bbs, recursive: true); }
        catch { /* best-effort */ }
    }

    private MessageCandidateStore NewStore(string realm)
    {
        MessageCandidateStore store = new(arm: _timers.Arm);
        store.Load(realm);
        return store;
    }

    private static List<MessageCandidateRecord> OnFile(string realm) =>
        JsonStore.Load<List<MessageCandidateRecord>>(AppPaths.RealmMessageCandidatesFile(realm)) ?? [];

    [Fact]
    public async Task Sightings_AreWrittenTogether_SaveDelayAfterTheFirst()
    {
        string realm = Realm("realm");
        MessageCandidateStore store = NewStore(realm);
        DateTimeOffset t = DateTimeOffset.UtcNow;

        store.RecordSighting("You clench a fist and point a finger at angry fugitive.", t);
        store.RecordSighting("You clench a fist and point a finger at angry fugitive.", t.AddSeconds(5));
        store.RecordSighting("The zephyr dissipates, its debris falling to the ground.", t.AddSeconds(9));

        // Nothing written yet, and one save waits for all three.
        Assert.False(File.Exists(AppPaths.RealmMessageCandidatesFile(realm)));
        Assert.Equal(new[] { MessageCandidateStore.SaveDelay }, _timers.PendingDelays);

        _timers.Advance(MessageCandidateStore.SaveDelay);
        await store.WhenSavedAsync();

        List<MessageCandidateRecord> saved = OnFile(realm);
        Assert.Equal(2, saved.Count);
        Assert.Equal(2, saved.Single(c => c.RawText.StartsWith("You clench")).Occurrences);
        Assert.Equal(0, _timers.Pending);

        // The next change arms a new save.
        store.RecordSighting("The zephyr dissipates, its debris falling to the ground.", t.AddSeconds(40));
        Assert.Equal(1, _timers.Pending);
    }

    [Fact]
    public void Save_WritesWhatWaits_AtOnce_AndDropsTheDeferredSave()
    {
        string realm = Realm("realm");
        MessageCandidateStore store = NewStore(realm);

        store.RecordSighting("A strange hum fills the air.", DateTimeOffset.UtcNow);
        store.Save();

        Assert.Equal("A strange hum fills the air.", Assert.Single(OnFile(realm)).RawText);
        Assert.Equal(0, _timers.Pending);
    }

    // A profile or realm switch reloads the store: what the realm being left had
    // waiting goes to that realm's file, not the next realm's.
    [Fact]
    public void Load_OfAnotherRealm_WritesTheFirstRealmsWaitingLines_ToItsOwnFile()
    {
        string first = Realm("first");
        string second = Realm("second");
        MessageCandidateStore store = NewStore(first);

        store.RecordSighting("The floor shakes.", DateTimeOffset.UtcNow);
        store.Load(second);
        _timers.RunAll();

        Assert.Equal("The floor shakes.", Assert.Single(OnFile(first)).RawText);
        Assert.Empty(store.Candidates);
        Assert.False(File.Exists(AppPaths.RealmMessageCandidatesFile(second)));
    }

    // Past MaxPending a new line pushes out the pending line seen longest ago; a
    // dismissed line is the user's verdict and stays.
    [Fact]
    public void NewLine_PastTheCap_PushesOutThePendingLineSeenLongestAgo()
    {
        MessageCandidateStore store = new(arm: _timers.Arm);
        DateTimeOffset t = DateTimeOffset.UtcNow;
        (MessageCandidateRecord dismissed, _) = store.RecordSighting("Dismissed long ago.", t.AddDays(-30));
        store.Dismiss(dismissed.Id);
        store.RecordSighting("Seen longest ago.", t.AddDays(-20));
        for (int i = 1; i < MessageCandidateStore.MaxPending; i++)
            store.RecordSighting($"Line {i}.", t.AddSeconds(i));
        Assert.True(store.Contains("Seen longest ago."));

        store.RecordSighting("One line too many.", t.AddDays(1));

        Assert.False(store.Contains("Seen longest ago."));
        Assert.True(store.IsDismissed("Dismissed long ago."));
        Assert.True(store.Contains("One line too many."));
        Assert.Equal(MessageCandidateStore.MaxPending, store.Candidates.Count(c => !c.Dismissed));
    }
}

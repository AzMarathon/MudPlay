using System;
using System.Collections.Generic;
using System.IO;
using MudPlay.Game;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using MudPlay.Terminal;
using Xunit;

namespace MudPlay.Tests;

// Replays the lines from a real unrecognized-lines export through the whole capture
// path — real stock catalogue, real router patterns, real exclusions — rather than a
// synthetic catalogue. The reported queue was almost entirely noise: party-screen
// rows, room light, melee swings, known casts, and monster death flavour, which
// buried the handful of genuinely uncatalogued room-spell messages it existed to
// surface. These pin which of those classes stay out and, just as importantly, which
// still come through.
public sealed class MessageCandidateReportReplayTests : IDisposable
{
    private readonly string _dir;
    private readonly LogService _log = new();
    private readonly MessageRouter _router = new();
    private readonly MessageStore _messages = new();
    private readonly MessageCandidateStore _candidates = new();
    private readonly MessageCandidateWatcher _watcher;

    public MessageCandidateReportReplayTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mudplay-replay-" + Path.GetRandomFileName());
        AppPaths.ExtractEmbeddedSeeds(_dir);
        // ReplaceAll raises one Reset, so the watcher rebuilds its indexes once
        // instead of once per record.
        _messages.Messages.ReplaceAll(
            JsonStore.Load<List<MessageRecord>>(Path.Combine(_dir, "Messages.stock.seed.json"))
            ?? new List<MessageRecord>());
        DefaultPatterns.Seed(_router);
        _watcher = new MessageCandidateWatcher(
            _router, _messages, _candidates, log: _log,
            isRecognizedByDirectParser: PartyManager.IsRosterRow);
        _watcher.NotifyInGame();
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup */ }
    }

    private void Feed(string text, bool flush = true)
    {
        var emitted = new LineExtractor.EmittedLine(
            text, Array.Empty<CellAttributes>(), DateTimeOffset.UtcNow, IsPromptLine: false);
        Invoke("OnLine", emitted);
        if (flush) Invoke("CommitPending");
    }

    private void Invoke(string method, params object[] args) =>
        typeof(MessageCandidateWatcher)
            .GetMethod(method,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(_watcher, args);

    [Theory]
    // Party screen — read by PartyManager's block parser, not a router pattern.
    [InlineData("  Raijin WuzHere                 (Priest)     [M:100%] [H: 85%]   - Backrank")]
    [InlineData("  Suijin WuzHere                 (Witchunter)          [H:100%]   - Midrank")]
    [InlineData("  Fujin WuzHere                  (Mystic)     [K:100%] [H: 94%]   - Frontrank")]
    [InlineData("  Raijin WuzHere                 (Priest)     [M: 94%] [H: 87%]   - Backrank")]
    // Room light — fully known from game data.
    [InlineData("The room is dimly lit")]
    // Known casts, stored as catalogue templates.
    [InlineData("Raijin casts minor healing on Raijin!")]
    [InlineData("Raijin casts bless on Raijin!")]
    [InlineData("Raijin casts bless on Suijin!")]
    [InlineData("Raijin casts a spell on you!")]
    [InlineData("You invoke the way of the swan.")]
    [InlineData("You invoke the way of the tiger.")]
    // Buff expiry — the catalogue wording omits the trailing "!".
    [InlineData("The effects of way of the tiger wear off!")]
    // Another player's failed cast.
    [InlineData("Raijin attempted to cast minor healing, but failed.")]
    // Third-party physical attacks, weapon / body part named.
    [InlineData("The wild dog snaps at Suijin with its teeth!")]
    [InlineData("The dark goblin archer shoots an arrow at Suijin with their shortbow!")]
    [InlineData("The nasty bandit swings at Suijin with their broadsword!")]
    [InlineData("The bandit swings at Raijin with their broadsword!")]
    [InlineData("The bandit swings at Suijin with their broadsword!")]
    // Bare swings with no weapon named.
    [InlineData("Raijin swipes at dark goblin archer!")]
    [InlineData("Raijin swipes at wild dog!")]
    [InlineData("Raijin swipes at nasty bandit!")]
    [InlineData("Raijin swipes at bandit!")]
    public void ReportedNoise_IsNoLongerCaptured(string line)
    {
        Feed(line);
        Assert.Empty(_candidates.Candidates);
    }

    [Theory]
    [InlineData("The dog yelps loudly, and dies.")]
    [InlineData("The dark goblin archer collapses with a spiteful hiss.")]
    public void ReportedDeathFlavour_IsNoLongerCaptured(string line)
    {
        Feed(line, flush: false);
        Feed("You gain 350 experience.");
        Assert.Empty(_candidates.Candidates);
    }

    [Theory]
    [InlineData("swan")]
    [InlineData("tige")]
    public void ReportedEngineCommandEcho_IsNoLongerCaptured(string echo)
    {
        // These were engine-issued casts whose truncated echo bounced back. The echo
        // ring is now fed from the engine's raw-send path too, not just typed input.
        _watcher.ObserveOutbound(System.Text.Encoding.Latin1.GetBytes(echo + "\r\n"));
        Feed(echo);
        Assert.Empty(_candidates.Candidates);
    }

    [Theory]
    // Darkwood room-spell triggers with no catalogue entry — the reason the feature
    // exists. Every exclusion above has to leave these alone.
    [InlineData("An ominous wind blows through the trees")]
    [InlineData("A flock of birds fly overhead.")]
    [InlineData("The forest becomes strangely silent.")]
    [InlineData("The leaves begin to rustle, as if some beast were about to spring forth!")]
    // A ranged attack naming its ammunition but no weapon reads exactly like a
    // projectile SPELL ("<name> hurls a fireball at <target>!"), so it is left
    // captured on purpose: suppressing this shape would cost real monster spell
    // messages, and an extra row to dismiss is the cheaper mistake.
    [InlineData("Suijin shoots an arrow at bandit!")]
    public void GenuineUnknowns_AreStillCaptured(string line)
    {
        Feed(line);
        Assert.Single(_candidates.Candidates);
    }
}

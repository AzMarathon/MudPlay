using System;
using System.IO;
using System.Linq;
using System.Text;
using MudPlay.Game;
using MudPlay.Game.Remote;
using MudPlay.Models.GameData;
using MudPlay.Services;
using MudPlay.Services.Patterns;
using Xunit;

namespace MudPlay.Tests;

// Persisted HydraReportStore (report / reset / per-set isolation / reload) and
// the @hydra dead / @hydra status remote report.
public sealed class HydraTimerTests : IDisposable
{
    private readonly string _set = "hydra-test-" + Path.GetRandomFileName();
    private readonly string _set2 = "hydra-test2-" + Path.GetRandomFileName();

    public void Dispose()
    {
        foreach (string s in new[] { _set, _set2 })
        {
            try { string d = AppPaths.GameDataSetDir(s); if (Directory.Exists(d)) Directory.Delete(d, true); }
            catch { /* best-effort cleanup */ }
        }
    }

    // ----- HydraReportStore ---------------------------------------------

    [Fact]
    public void NoPriorReport_CurrentIsNull()
    {
        var store = new HydraReportStore();
        store.OnActiveSetChanged(_set);
        Assert.Null(store.Current);
    }

    [Fact]
    public void Report_SetsCurrent_AndPersistsAcrossReload()
    {
        var store = new HydraReportStore();
        store.OnActiveSetChanged(_set);
        DateTimeOffset at = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

        store.Report("Alice", at);

        Assert.Equal("Alice", store.Current?.ReportedBy);
        Assert.Equal(at, store.Current?.At);

        // A fresh store instance loading the same set picks up the persisted report.
        var reloaded = new HydraReportStore();
        reloaded.OnActiveSetChanged(_set);
        Assert.Equal("Alice", reloaded.Current?.ReportedBy);
        Assert.Equal(at, reloaded.Current?.At);
    }

    [Fact]
    public void Report_Overwrites_KeepingOnlyTheLatest()
    {
        var store = new HydraReportStore();
        store.OnActiveSetChanged(_set);
        store.Report("Alice", DateTimeOffset.UtcNow.AddHours(-3));
        store.Report("Bob", DateTimeOffset.UtcNow);

        Assert.Equal("Bob", store.Current?.ReportedBy);
    }

    [Fact]
    public void Reset_ClearsCurrent_AndRemovesTheFile()
    {
        var store = new HydraReportStore();
        store.OnActiveSetChanged(_set);
        store.Report("Alice", DateTimeOffset.UtcNow);
        string path = AppPaths.HydraReportFile(_set);
        Assert.True(File.Exists(path));

        store.Reset();

        Assert.Null(store.Current);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void DifferentSets_TrackSeparateReports()
    {
        var store = new HydraReportStore();
        store.OnActiveSetChanged(_set);
        store.Report("Alice", DateTimeOffset.UtcNow);

        store.OnActiveSetChanged(_set2);
        Assert.Null(store.Current);   // a different realm's data, never reported here

        store.OnActiveSetChanged(_set);
        Assert.Equal("Alice", store.Current?.ReportedBy);   // switching back reloads it
    }

    [Fact]
    public void Changed_FiresOnReportAndReset()
    {
        var store = new HydraReportStore();
        store.OnActiveSetChanged(_set);
        int fired = 0;
        store.Changed += () => fired++;

        store.Report("Alice", DateTimeOffset.UtcNow);
        store.Reset();

        Assert.Equal(2, fired);
    }

    // ----- @hydra handler -------------------------------------------------

    private static readonly DateTime Now = new(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc);

    private (RemoteCommandManager engine, HydraReportStore reports) SetupHandler()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        players.RecordObservation("Bob", null, null, null, null, null, null, Now);
        players.EditCustomization("Bob", new PlayerCustomization(RemoteControls: PlayerRemoteControls.QueryHydraTimer));

        HydraReportStore reports = new();
        reports.OnActiveSetChanged(_set);
        _ = new HydraTimerQueryHandler(engine, reports);
        return (engine, reports);
    }

    private static ChatLogEntry Telepath(string msg) =>
        new(Now, ChatChannel.TelepathIncoming, "Bob", msg, $"Bob telepaths: {msg}");

    private static ChatLogEntry Broadcast(string msg) =>
        new(Now, ChatChannel.Broadcast, "Bob", msg, $"Broadcast from Bob \"{msg}\"");

    private static string Reply(RemoteCommandManager engine) =>
        engine.LastSentForTests.Select(b => Encoding.Latin1.GetString(b)).Select(StripWire).Single();

    private static string StripWire(string wire)
    {
        string s = wire.TrimEnd('\r');
        int open = s.IndexOf('{');
        int close = s.LastIndexOf('}');
        return open >= 0 && close > open ? s[(open + 1)..close] : s;
    }

    [Fact]
    public void Hydra_NoArgs_RepliesUsage()
    {
        var (engine, _) = SetupHandler();
        engine.DispatchForTests(Telepath("@hydra"));
        Assert.Equal("usage: @hydra dead | @hydra status", Reply(engine));
    }

    [Fact]
    public void Hydra_Dead_RecordsReport_AttributedToTheSender()
    {
        var (engine, reports) = SetupHandler();
        engine.DispatchForTests(Telepath("@hydra dead"));

        Assert.Equal("Bob", reports.Current?.ReportedBy);
        Assert.Contains("Bob", Reply(engine));
    }

    [Fact]
    public void Hydra_Status_WithNoReport_SaysSo()
    {
        var (engine, _) = SetupHandler();
        engine.DispatchForTests(Telepath("@hydra status"));
        Assert.Equal("no hydra kill reported yet", Reply(engine));
    }

    [Fact]
    public void Hydra_Status_ReportsWhoAndElapsed()
    {
        var (engine, reports) = SetupHandler();
        reports.Report("Alice", DateTimeOffset.UtcNow.AddHours(-2));

        engine.DispatchForTests(Telepath("@hydra status"));

        string reply = Reply(engine);
        Assert.Contains("Alice", reply);
        Assert.Contains("2h", reply);
        Assert.Contains("ago", reply);
    }

    [Fact]
    public void Hydra_WorksOverBroadcast_NotJustTelepath()
    {
        // The whole point of extending RemoteChannel to cover Broadcast: a
        // gang-wide "-@hydra dead" must reach the handler and reply on the
        // same channel.
        var (engine, reports) = SetupHandler();
        engine.DispatchForTests(Broadcast("@hydra dead"));

        Assert.Equal("Bob", reports.Current?.ReportedBy);
        string wire = Encoding.Latin1.GetString(engine.LastSentForTests[0]);
        Assert.StartsWith("-{", wire);
    }

    [Fact]
    public void Hydra_DeniedWithoutPermission()
    {
        MessageRouter router = new();
        DefaultPatterns.Seed(router);
        ChatRouter chat = new(router);
        PartyState party = new();
        PlayerDatabase players = new();
        RemoteCommandManager engine = new(chat, party, players);
        players.RecordObservation("Stranger", null, null, null, null, null, null, Now);
        // No EditCustomization — defaults to PlayerRemoteControls.None.

        HydraReportStore reports = new();
        reports.OnActiveSetChanged(_set);
        _ = new HydraTimerQueryHandler(engine, reports);

        engine.DispatchForTests(new ChatLogEntry(Now, ChatChannel.TelepathIncoming, "Stranger", "@hydra dead",
            "Stranger telepaths: @hydra dead"));

        Assert.Null(reports.Current);
    }
}

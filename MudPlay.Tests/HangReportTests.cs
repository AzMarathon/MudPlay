using System.IO;
using System.Threading;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

// The freeze detector: PerformanceMonitor's probe, driven on a manual clock and
// timers, with a UI thread that answers only when the test says so, writing
// HangReport files into a folder of the test's own.
public sealed class HangReportTests
{
    private const string LoginPassword = "Zq8shadowpw";
    private const string CorpsePassword = "Kx3corpsepw";

    private sealed class Rig : IDisposable
    {
        public ManualClock Clock { get; } = new();
        public ManualTimers Timers { get; } = new();
        // What the probe posted to the UI thread, run when the test lets it answer.
        public List<Action> Posted { get; } = new();
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "mudplay-hang-" + Guid.NewGuid().ToString("N"));
        public PerformanceMonitor Monitor { get; }
        public LogService Log { get; } = new();
        public SentCommandTrail Sent { get; } = new();

        // writeAside / runAside: where the report's file work and the probe's log
        // lines go; by default each runs at once.
        public Rig(Func<bool>? onUiThread = null, Func<BugReportScrub.Scrubber>? scrubber = null, bool arm = true,
            Action<Action>? writeAside = null, Action<Action>? runAside = null)
        {
            // The Desktop is there already: the client never makes one.
            Directory.CreateDirectory(Folder);
            Monitor = new PerformanceMonitor(onUiThread ?? (() => true),
                new PerformanceMonitor.ProbeHost(Posted.Add, Timers.Schedule, Clock.Timestamp, () => Clock.Now, () => Folder,
                    writeAside ?? (work => work()), runAside ?? (work => work())));
            Monitor.HangSources = new HangReport.Sources(Log, Sent,
                scrubber ?? (() => BugReportScrub.Build(
                    [new(BugReportScrub.LoginPasswordLabel, LoginPassword), new(BugReportScrub.CorpsePasswordLabel, CorpsePassword)],
                    Array.Empty<string?>(), compiled: false)),
                () => [("Character", "Fujin"), ("Realm", "paradigm")]);
            if (arm) Monitor.ArmHangReports();
        }

        // Time passes a second at a time, the probe ticking once a second; the UI
        // thread answers nothing.
        public void Pass(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                Clock.Advance(TimeSpan.FromSeconds(1));
                Timers.Advance(TimeSpan.FromSeconds(1));
            }
        }

        // As Pass, the UI thread answering each ping as it comes.
        public void PassAnswering(int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                Pass(1);
                Answer();
            }
        }

        public void Answer()
        {
            Action[] pings = Posted.ToArray();
            Posted.Clear();
            foreach (Action ping in pings) ping();
        }

        public string[] Reports =>
            Directory.Exists(Folder) ? Directory.GetFiles(Folder, HangReport.FilePrefix + "*.md") : [];

        public string Report => File.ReadAllText(Assert.Single(Reports));

        public void Dispose()
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
    }

    [Fact]
    public void A_ui_thread_silent_past_the_threshold_writes_one_report()
    {
        using Rig rig = new();
        using (rig.Monitor.Measure("stuck"))
        {
            rig.Pass(5);
            Assert.Empty(rig.Reports);
            rig.Pass(1);
            Assert.Single(rig.Reports);
            rig.Pass(120);
        }

        string report = rig.Report;
        Assert.StartsWith("Hang-20261001-120006", Path.GetFileName(rig.Reports[0]));
        Assert.Contains("- **UI thread unresponsive**: 5.0 s when this was written", report);
        Assert.Contains("- **Version**: " + AppInfo.Version, report);
        Assert.Contains("- **Character**: Fujin", report);
        Assert.Contains("still not answering after 15.0 s; innermost scope `stuck`", report);
    }

    [Fact]
    public void The_report_names_each_open_scope_with_its_file_member_and_line_and_what_ran_before()
    {
        using Rig rig = new();
        rig.Monitor.NoteWindowOpened("MainWindow");
        rig.Monitor.NoteWindowOpened("SettingsWindow");
        rig.Monitor.NoteWindowFocused("SettingsWindow");
        for (int i = 0; i < 3; i++)
            using (rig.Monitor.Measure("terminal feed")) rig.Clock.Advance(TimeSpan.FromMilliseconds(4));
        OpenSettingsAndHang(rig);

        string report = rig.Report;
        int outer = report.IndexOf("1. `open Settings window` at `MudPlay.Tests/HangReportTests.cs:", StringComparison.Ordinal);
        int inner = report.IndexOf("2. `Combat weapon suggestions` at `MudPlay.Tests/HangReportTests.cs:", StringComparison.Ordinal);
        Assert.True(outer > 0 && inner > outer, report);
        Assert.Contains($"`, in `{nameof(OpenSettingsAndHang)}`: open 6.0 s", report);
        Assert.Contains($"`, in `{nameof(PickCombatSection)}`: open 6.0 s", report);
        Assert.Matches(@"4\.0 ms  terminal feed  MudPlay\.Tests/HangReportTests\.cs:\d+ \(The_report_names_\w+\)  x3", report);
        Assert.Contains("- **Open**: MainWindow, SettingsWindow", report);
        Assert.Contains("- **Last opened or closed**: opening SettingsWindow at", report);
        Assert.Contains("- **Last focused**: SettingsWindow at", report);
    }

    private static void OpenSettingsAndHang(Rig rig)
    {
        using (rig.Monitor.Measure("open Settings window")) PickCombatSection(rig);
    }

    private static void PickCombatSection(Rig rig)
    {
        using (rig.Monitor.Measure("Combat weapon suggestions")) rig.Pass(6);
    }

    [Fact]
    public void Recovery_is_added_to_the_report_and_logged()
    {
        using Rig rig = new();
        rig.Pass(7);
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        rig.Answer();                       // at 8 s; the ping went out at 1 s
        rig.Timers.Advance(TimeSpan.FromSeconds(1));

        Assert.Contains("the UI thread answered again. The hang lasted 7.0 s.", rig.Report);
        Assert.StartsWith("1; the last ended 12:00:08 after 7.0 s, written to Hang-20261001-120006", rig.Monitor.FreezesThisSession);
        Assert.Contains(rig.Log.Snapshot(), e => e.Severity == LogSeverity.Warn
            && e.Message.StartsWith("The UI thread answered again after 7.0 s unresponsive; see Hang-", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_is_written_below_the_threshold_or_before_the_window_opens()
    {
        using Rig rig = new();
        rig.Pass(5);                        // the ping has waited 4 s
        rig.Answer();
        rig.PassAnswering(60);
        Assert.Empty(rig.Reports);

        using Rig starting = new(arm: false);
        starting.Pass(30);
        Assert.Empty(starting.Reports);
    }

    [Fact]
    public void A_probe_that_was_itself_stopped_does_not_call_it_a_hang()
    {
        using Rig rig = new();
        rig.Pass(1);                        // a ping in flight
        rig.Clock.Advance(TimeSpan.FromSeconds(30));   // the machine slept
        rig.Pass(2);
        Assert.Empty(rig.Reports);
    }

    [Fact]
    public void A_later_hang_is_a_new_report_but_not_within_the_gap()
    {
        using Rig rig = new();
        rig.Pass(7);
        rig.Answer();
        rig.Pass(10);                       // hangs again at once: inside the gap
        rig.Answer();
        rig.PassAnswering(1);
        Assert.Single(rig.Reports);
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.Contains("(no Hang file: one was written less than 2 minutes before)", StringComparison.Ordinal));

        rig.PassAnswering((int)PerformanceMonitor.ReportGap.TotalSeconds);
        rig.Pass(7);
        Assert.Equal(2, rig.Reports.Length);
    }

    [Fact]
    public void Account_details_and_corpse_passwords_never_reach_the_file()
    {
        using Rig rig = new();
        rig.Log.Info("Telnet", $"login answered with {LoginPassword}");
        rig.Log.Info("Chat", $"Raijin telepaths: the word is {CorpsePassword}");
        rig.Sent.Record($"recover corpse raijin {CorpsePassword}", SentCommandTrail.Origin.Engine, "Recovery", SentCommandTrail.Outcome.Sent);
        rig.Sent.Record("say hello", SentCommandTrail.Origin.Typed, null, SentCommandTrail.Outcome.Sent);
        rig.Pass(6);

        string report = rig.Report;
        Assert.DoesNotContain(LoginPassword, report);
        Assert.DoesNotContain(CorpsePassword, report);
        Assert.Contains(BugReportScrub.Mask, report);
        Assert.Contains("say hello", report);
        Assert.Contains("## Program log (last", report);
    }

    [Fact]
    public void Without_a_scrub_the_logs_are_left_out_and_the_code_location_stays()
    {
        using Rig rig = new(scrubber: () => throw new IOException("profile unreadable"));
        rig.Log.Info("Telnet", $"login answered with {LoginPassword}");
        using (rig.Monitor.Measure("profile save")) rig.Pass(6);

        string report = rig.Report;
        Assert.DoesNotContain(LoginPassword, report);
        Assert.DoesNotContain("## Program log", report);
        Assert.Contains("not included: the saved account details could not be read (IOException)", report);
        Assert.Contains("1. `profile save` at `MudPlay.Tests/HangReportTests.cs:", report);
    }

    [Fact]
    public void A_freeze_under_way_when_the_window_opens_counts_from_then()
    {
        using Rig rig = new(arm: false);
        rig.Pass(1);                        // the ping goes out while the client is still starting
        rig.Clock.Advance(TimeSpan.FromMilliseconds(500));
        rig.Monitor.ArmHangReports();
        rig.Pass(4);
        Assert.Empty(rig.Reports);          // 4 s since the arm
        rig.Pass(1);
        Assert.Single(rig.Reports);
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.StartsWith("Freeze watch armed: a UI thread silent 5.0 s", StringComparison.Ordinal));
    }

    [Fact]
    public void A_known_long_operation_under_its_threshold_writes_nothing()
    {
        using Rig rig = new();
        using (rig.Monitor.MeasureLong("game-data set switch", "data-Paradigm"))
        using (rig.Monitor.Measure("parse", "Rooms"))
            rig.Pass(29);
        rig.Answer();
        rig.PassAnswering(5);
        Assert.Empty(rig.Reports);
    }

    [Fact]
    public void A_known_long_operation_past_its_threshold_is_reported_as_one()
    {
        using Rig rig = new();
        using (rig.Monitor.MeasureLong("game-data set switch", "data-Paradigm")) rig.Clock.Advance(TimeSpan.FromSeconds(3));
        using (rig.Monitor.MeasureLong("game-data set switch", "data-Stock"))
        using (rig.Monitor.Measure("parse", "Rooms"))
        {
            rig.Pass(30);
            Assert.Empty(rig.Reports);
            rig.Pass(1);
        }

        string report = rig.Report;
        Assert.Contains("- **Inside a known long operation**: `game-data set switch data-Stock`, open 31.0 s: "
            + "this session it has taken up to 3.0 s, 3.0 s the last time. Such work is reported only past 30.0 s", report);
        Assert.Contains("2. `parse Rooms` at `MudPlay.Tests/HangReportTests.cs:", report);
    }

    [Fact]
    public void A_stall_while_collecting_is_written_with_what_the_ui_thread_was_doing()
    {
        using Rig rig = new();
        List<string> lines = new();
        rig.Monitor.CollectTo(lines.Add);
        rig.Pass(1);                        // a ping in flight; the probe now looks every 20 ms
        using (rig.Monitor.Measure("parse", "Rooms"))
            for (int i = 0; i < 10; i++)
            {
                rig.Clock.Advance(TimeSpan.FromMilliseconds(20));
                rig.Timers.Advance(TimeSpan.FromMilliseconds(20));
            }
        rig.Answer();

        Assert.Equal(["stall 200 ms  parse Rooms"], lines);
    }

    [Fact]
    public void A_sleep_inside_a_hang_ends_its_figures()
    {
        using Rig rig = new();
        rig.Pass(7);                        // reported at 6 s
        rig.Clock.Advance(TimeSpan.FromMinutes(10));   // the machine slept
        rig.Pass(1);
        rig.Answer();
        rig.PassAnswering(2);

        string report = rig.Report;
        Assert.Contains("the freeze watch was itself stopped for 601.0 s", report);
        Assert.DoesNotContain("answered again", report);
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.StartsWith("The freeze watch was itself stopped", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Log.Snapshot(), e => e.Message.StartsWith("The UI thread answered again", StringComparison.Ordinal));
    }

    [Fact]
    public void Log_lines_left_waiting_on_a_stuck_log_are_dropped_and_counted()
    {
        List<Action> held = new();
        using Rig rig = new(runAside: held.Add);
        rig.Pass(7);
        rig.Answer();
        rig.PassAnswering(1);               // recovery: its log line is out, the log not answering
        rig.Pass(10);                       // a second freeze, inside the gap
        rig.Answer();
        rig.PassAnswering(1);
        Assert.Single(held);

        held[0]();                          // the log answers
        held.Clear();
        rig.PassAnswering((int)PerformanceMonitor.ReportGap.TotalSeconds);
        rig.Pass(7);
        rig.Answer();
        rig.PassAnswering(1);
        held.Single()();
        Assert.Contains(rig.Log.Snapshot(), e => e.Message.EndsWith(
            "(1 earlier line(s) from the freeze watch dropped: the log did not answer)", StringComparison.Ordinal));
    }

    [Fact]
    public void The_probe_goes_on_while_the_report_is_stuck_on_the_disk()
    {
        List<Action> stuck = new();
        using Rig rig = new(writeAside: stuck.Add);
        using (rig.Monitor.Measure("stuck")) rig.Pass(40);

        // The disk never answered, and the probe went on ticking: the milestones are queued behind the report.
        Assert.Empty(rig.Reports);
        Assert.Equal(3, stuck.Count);
        rig.Answer();
        rig.Pass(1);
        foreach (Action work in stuck.ToArray()) work();

        string report = rig.Report;
        Assert.Contains("still not answering after 30.0 s", report);
        Assert.True(report.IndexOf("after 15.0 s", StringComparison.Ordinal) < report.IndexOf("answered again", StringComparison.Ordinal));
    }

    [Fact]
    public void A_log_write_stuck_on_the_ui_thread_keeps_neither_the_report_nor_its_log_tail()
    {
        Thread? ui = null;
        using Rig rig = new(onUiThread: () => Thread.CurrentThread == ui);
        rig.Log.Info("Telnet", "before the stall");
        using ManualResetEventSlim entered = new(), release = new();
        // As the program log's file writer does: every entry written on the
        // logging thread, here one whose disk never answers.
        rig.Log.EntryAdded += _ =>
        {
            if (Thread.CurrentThread != ui) return;
            entered.Set();
            release.Wait();
        };
        ui = new Thread(() =>
        {
            using (rig.Monitor.Measure("program log file write")) rig.Log.Info("Combat", "the line being written");
        });
        ui.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            rig.Pass(6);
            string report = rig.Report;
            Assert.Contains("1. `program log file write` at `MudPlay.Tests/HangReportTests.cs:", report);
            Assert.Contains("[Info]  Telnet: before the stall", report);
            Assert.Contains("[Info]  Combat: the line being written", report);
        }
        finally
        {
            release.Set();
            ui.Join();
        }
    }

    [Fact]
    public void The_probe_reads_a_frozen_ui_thread_without_its_help()
    {
        Thread? ui = null;
        using Rig rig = new(onUiThread: () => Thread.CurrentThread == ui);
        using ManualResetEventSlim entered = new(), release = new();
        ui = new Thread(() =>
        {
            using (rig.Monitor.Measure("parse Rooms"))
            {
                entered.Set();
                release.Wait();
            }
        });
        ui.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            rig.Pass(6);
            string report = rig.Report;
            Assert.Contains("1. `parse Rooms` at `MudPlay.Tests/HangReportTests.cs:", report);
            // Written with the ping still waiting: nothing was asked of the UI thread.
            Assert.Single(rig.Posted);
        }
        finally
        {
            release.Set();
            ui.Join();
        }
    }
}

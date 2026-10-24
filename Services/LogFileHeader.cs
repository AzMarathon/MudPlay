namespace MudPlay.Services;

// The lines every Logs/ file opens with, so a file read on its own says what
// wrote it. Auto-collect logs is often switched on hours into a session, and a
// file that began then had no word of the build, the system, how long the client
// had been up or whose session it was: the reader had to ask the player.
public static class LogFileHeader
{
    // Whose session the file is from, as far as the client knows when it opens.
    public sealed record Session(string Character, string Bbs, string Realm, string GameDataSet)
    {
        public static readonly Session NotKnown = new("(not known)", "(not known)", "(not known)", "(not known)");

        public string Line => $"character {Character}; BBS {Bbs}; realm {Realm}; game data {GameDataSet}";
    }

    // session: null for a file that belongs to no session (a walk or match trace).
    public static IReadOnlyList<string> Lines(string topic, DateTimeOffset started, Session? session)
    {
        List<string> lines =
        [
            $"# MudPlay debug log — topic={topic}",
            $"# started {started:O}",
        ];
        foreach (string line in ClientHeartbeat.ClientLines()) lines.Add("# " + line);
        lines.Add($"# client up {BugReportHoldsAndWaits.Age(started - ClientEnvironment.ProcessStart)} when this file started");
        if (session is not null) lines.Add("# " + session.Line);
        return lines;
    }

    // Reads the session through read, which may throw while the client is still
    // being built or a profile is swapping: the header then says it isn't known.
    public static Session Read(Func<Session>? read)
    {
        if (read is null) return Session.NotKnown;
        try { return read(); }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            return Session.NotKnown;
        }
    }
}

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using MudPlay.Game;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Terminal;

namespace MudPlay.Services;

// In-memory cache of the active character's triggers + the app-session named
// variable store, plus the runtime dispatch path.
//
// Three subscription surfaces: ChatRouter.EntryClassified for chat-scoped
// triggers, LineExtractor.LineEmitted for GameMessages (non-chat) lines, and
// LogService.EntryAdded for the SystemLog scope. Chat lines are tracked in a
// small recency queue so the GameMessages path can dedupe them — every line
// goes through LineExtractor, including those ChatRouter already classified.
//
// Wildcards (captured values) are app-session-scoped (in-memory, wiped on app
// close — same lifetime as ChatHistoryStore) so a trigger that captures a value
// on one character can be read by another trigger on a different character within
// the same session. This store belongs to the TRIGGER system alone — it is NOT
// shared with aliases (which compute their own positional {0}/{1} tokens per
// expansion), macros, or events. Keeping the namespace isolated is deliberate:
// {1} in a trigger pattern captures the first match span into THIS store; {1} in
// an alias means the first typed token, a different thing entirely. The
// substitution syntax is {name} in both patterns (capture) and Response / Notify
// text (read); a name is any run of letters, digits, and underscores, so {1},
// {who}, and {tgt2} are all valid wildcards.
public sealed class TriggerEngine
{
    // Recent-classification queue depth — enough to cover bursts without
    // retaining stale entries.
    private const int RecentChatCapacity = 32;

    // Source tag the engine uses on its own log writes — skipped in the
    // SystemLog scope to avoid feedback loops.
    public const string LogSource = "Trigger";

    private readonly ProfileService? _profile;
    private readonly ChatRouter? _chat;
    private readonly LogService? _log;
    private LineExtractor? _lines;
    private Action<byte[]>? _sender;
    // The set whose per-set triggers (TriggerLocation.GameData) are currently in
    // the live collection. null when no set is active — GameData-scoped triggers
    // can't be loaded or saved. Tracked here so save routing knows which file to
    // write.
    private string? _activeSet;

    // Game-data triggers live in one file per set, shared by every client running on
    // this machine — but each client only read it when its set loaded, so a trigger one
    // client moved to (or edited at) the game-data location never reached another open
    // client until it restarted. The watcher reloads the game-data slice when another
    // process rewrites the file. _syncedText is the file content we last loaded or
    // wrote, so our own save (or a no-op rewrite) doesn't bounce back as a reload.
    private System.IO.FileSystemWatcher? _setFileWatcher;
    private string? _syncedText;
    private int _reloadGeneration;
    private static readonly TimeSpan ExternalReloadDebounce = TimeSpan.FromMilliseconds(300);

    // True while the active set's triggers file exists but our last read of it failed —
    // on Windows a just-written file is briefly held (the antivirus scan, the other
    // client's rename), and a client on another version may not parse a newer file.
    // The live list is kept as it was rather than cleared, the read is retried, and
    // until one succeeds we never WRITE the file: saving our stale (or empty) slice over
    // it would wipe the other client's triggers for both.
    private bool _gameDataUnread;
    private int _readRetries;
    private const int MaxReadRetries = 10;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromSeconds(2);

    // Compiled-regex cache, keyed by (match type, raw pattern). Built lazily as
    // triggers fire.
    private readonly Dictionary<(TriggerMatchType Kind, string Pattern), Regex?> _regexCache = new();

    // Recent ChatRouter.EntryClassified snapshots used to dedupe the parallel
    // LineExtractor.LineEmitted path.
    private readonly Queue<(DateTimeOffset At, string Text)> _recentChat = new();

    // The loaded character's triggers — empty when no profile is active.
    public ObservableCollection<Trigger> Triggers { get; } = new();

    // App-session-scoped trigger-wildcard store. Populated by pattern captures;
    // read during Response / Notify interpolation. Trigger-only — see the class
    // comment on why this namespace stays isolated from aliases / macros / events.
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Raised whenever a capture writes the wildcard store (or ClearWildcards
    // empties it) so a live viewer can refresh. Fires on the UI thread — every
    // dispatch path is already marshalled there upstream.
    public event Action? WildcardsChanged;

    public TriggerEngine(ProfileService profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        profile.ProfileLoaded += LoadFrom;
        profile.ProfileClosed += Clear;
        profile.ProfileSaving += SnapshotForSave;
        // Bust the regex cache whenever the trigger list mutates — Save
        // path uses Replace, which swaps in a new Trigger record, so the
        // old cached compile may no longer match the new pattern.
        Triggers.CollectionChanged += (_, _) => _regexCache.Clear();
        if (profile.Current is { } current) LoadFrom(current);
    }

    // Production ctor — wires the chat + log subscriptions at construction time.
    public TriggerEngine(ProfileService profile, ChatRouter chat, LogService log) : this(profile)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(log);
        _chat = chat;
        _log = log;
        chat.EntryClassified += OnChatClassified;
        log.EntryAdded       += OnLogEntry;
    }

    // Parameterless ctor for tests / in-memory scenarios — no profile
    // persistence, no live subscriptions.
    public TriggerEngine() { }

    // Wire up the LineExtractor source. Called from MainWindowViewModel after
    // the extractor is constructed — AppServices can't reach LineExtractor
    // because it's owned by the main window, not the service container.
    public void AttachLineExtractor(LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (_lines is not null) _lines.LineEmitted -= OnLineEmitted;
        _lines = lines;
        lines.LineEmitted += OnLineEmitted;
    }

    // Bind the wire-send callback used to deliver a fired trigger's Response to
    // the live telnet connection. Mirrors MacroDispatcher.SetSender; same sender
    // instance is fine.
    public void SetSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _sender = sender;
    }

    // Insert a new trigger and persist to its Location-matching file
    // (GameData-scoped → per-set; Profile-scoped → profile).
    public void Add(Trigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        Triggers.Add(trigger);
        PersistAll();
    }

    // Replace an existing trigger by reference. Always writes both buckets
    // because the edit may have moved the trigger between Locations — the entry
    // needs to disappear from the old file and appear in the new. Returns false
    // when the original isn't found.
    public bool Replace(Trigger original, Trigger updated)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(updated);
        int index = Triggers.IndexOf(original);
        if (index < 0) return false;
        Triggers[index] = updated;
        PersistAll();
        return true;
    }

    // Remove a trigger by reference. Persists both buckets. Returns false if not found.
    public bool Remove(Trigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        bool removed = Triggers.Remove(trigger);
        if (removed) PersistAll();
        return removed;
    }

    // Write both the per-set file (GameData-scoped triggers) and the profile
    // (Profile-scoped triggers via SnapshotForSave). Two writes on every edit is
    // wasteful but correct under all the Location-change cases; trigger lists are
    // small enough that the cost is negligible.
    private void PersistAll()
    {
        SavePerSetTriggers();
        _profile?.Save();
    }

    private void SavePerSetTriggers()
    {
        if (string.IsNullOrWhiteSpace(_activeSet)) return;
        if (_gameDataUnread)
        {
            _log?.Log(LogSeverity.Warn, LogSource,
                $"Not saving game-data triggers for '{_activeSet}': the file couldn't be read, so "
                + "writing now would overwrite triggers another client saved. Retrying the read.");
            ScheduleExternalReload(_activeSet, ReadRetryDelay);
            return;
        }
        List<Trigger> gd = Triggers
            .Where(t => t.Location == TriggerLocation.GameData)
            .ToList();
        string path = AppPaths.TriggersFile(_activeSet);
        JsonStore.Save(path, gd);
        _syncedText = ReadTextOrNull(path);
    }

    // Empty the wildcard store — the viewer's "Clear" affordance. Fires
    // WildcardsChanged only when there was something to clear so an already-empty
    // store doesn't churn the viewer.
    public void ClearWildcards()
    {
        if (Variables.Count == 0) return;
        Variables.Clear();
        WildcardsChanged?.Invoke();
    }

    // ----- Dispatch -----------------------------------------------------

    private void OnLineEmitted(LineExtractor.EmittedLine line)
    {
        // Skip the status-line prompt — fires on every server update and
        // would spam GameMessages-scoped triggers that match anything.
        if (line.IsPromptLine) return;

        // Lines classified as chat already fired via OnChatClassified —
        // suppress the parallel GameMessages path so a chat-scoped trigger
        // doesn't also fire as a game-message-scoped one.
        if (WasRecentlyClassified(line)) return;

        DispatchScope(TriggerScope.GameMessages, line.Text);
    }

    private void OnChatClassified(ChatLogEntry entry)
    {
        RememberClassified(entry);

        // ChatAny always fires for any classified entry; the specific
        // channel scope fires too when the channel maps to one of our
        // user-facing enum members.
        DispatchScope(TriggerScope.ChatAny, entry.RawText);
        if (MapChannelToScope(entry.Channel) is { } specific && specific != TriggerScope.ChatAny)
            DispatchScope(specific, entry.RawText);
    }

    private void OnLogEntry(LogEntry entry)
    {
        // Skip our own log writes so a trigger that emits a SystemLog
        // entry can't loop into firing itself.
        if (string.Equals(entry.Source, LogSource, StringComparison.Ordinal)) return;

        // LogService fires on the producer's thread; marshal onto the UI
        // thread so the dispatch path stays single-threaded (same place
        // OnLineEmitted / OnChatClassified run from).
        if (Dispatcher.UIThread.CheckAccess())
            DispatchScope(TriggerScope.SystemLog, entry.Message);
        else
            Dispatcher.UIThread.Post(() => DispatchScope(TriggerScope.SystemLog, entry.Message));
    }

    // Test hook — drives the full match → capture → interpolate → send path for a
    // given scope against the live Triggers collection, exactly as the production
    // subscriptions do. Lets a test verify the end-to-end wildcard flow.
    internal void DispatchForTests(TriggerScope scope, string text) => DispatchScope(scope, text);

    private void DispatchScope(TriggerScope scope, string text)
    {
        foreach (Trigger t in Triggers)
        {
            if (!t.Enabled) continue;
            if (t.Scope != scope) continue;
            TryFire(t, text);
        }
    }

    private void TryFire(Trigger t, string text)
    {
        Regex? regex = GetOrCompile(t);
        if (regex is null) return;

        Match m = regex.Match(text);
        if (!m.Success) return;

        // Push captures into the trigger-wildcard store. A Literal pattern emits
        // only the whole-match group plus named captures (no unnamed groups), so
        // every group past index 0 is a user wildcard — numeric names ({1}, {2})
        // included. A Regex pattern can carry unnamed (...) groups, whose Name is
        // their number, so there skip any digit-named group and bind only the
        // user's (?<name>…) captures.
        bool literal = t.MatchType == TriggerMatchType.Literal;
        bool changed = false;
        int index = -1;
        foreach (Group g in m.Groups)
        {
            index++;
            if (index == 0) continue;   // whole-match group, never a wildcard
            if (!g.Success) continue;
            string gname = g.Name;
            if (!literal && (gname.Length == 0 || char.IsDigit(gname[0]))) continue;
            Variables[gname] = g.Value;
            changed = true;
        }
        if (changed) WildcardsChanged?.Invoke();

        // A matched-but-not-sent trigger (undefined wildcard in the Response) is
        // the common "my trigger didn't work" report, so log the match at Debug
        // either way — the send path logs its own outcome via the response.
        _log?.Log(LogSeverity.Debug, LogSource, $"'{t.Name}' matched: {text}");

        // Substitute + send the Response. Empty Response → bare CR.
        if (!TryInterpolate(t.Response, t.Name, out string responseOut)) return;
        SendResponse(responseOut);

        // Optional sound sidecar — stubbed; playback isn't wired yet.
        // No OS / toast notifications anywhere in the app, ever.
        if (!string.IsNullOrWhiteSpace(t.SoundFile))
            _log?.Log(LogSeverity.Debug, LogSource, $"'{t.Name}' fired — would play sound: {t.SoundFile}");
    }

    private void SendResponse(string substituted)
    {
        if (_sender is null) return;
        if (string.IsNullOrWhiteSpace(substituted))
        {
            _sender(new byte[] { (byte)'\r' });
            return;
        }
        foreach (string step in MacroStore.SplitCommandSteps(substituted))
        {
            // Latin-1: BBSes expect 8-bit clean bytes, not UTF-8 — same
            // encoding the terminal uses for ordinary keystrokes.
            byte[] bytes = Encoding.Latin1.GetBytes(step + "\r");
            _sender(bytes);
        }
    }

    // Substitute every {name} in template with the current value of that
    // variable. Returns false + logs a warning when any referenced variable is
    // undefined — silent substitution to empty would send broken commands.
    // Matches the rule established for macros.
    internal bool TryInterpolate(string template, string triggerName, out string result)
    {
        if (string.IsNullOrEmpty(template))
        {
            result = string.Empty;
            return true;
        }

        StringBuilder sb = new(template.Length);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '{' && TryReadName(template, i, out string name, out int next))
            {
                if (!Variables.TryGetValue(name, out string? value))
                {
                    _log?.Log(LogSeverity.Warn, LogSource,
                        $"'{triggerName}' skipped — variable {{{name}}} is undefined.");
                    result = string.Empty;
                    return false;
                }
                sb.Append(value);
                i = next;
                continue;
            }
            sb.Append(c);
            i++;
        }
        result = sb.ToString();
        return true;
    }

    // Look ahead from start (pointing at {) for a well-formed {name}
    // placeholder. Returns false when the brace doesn't open a valid identifier
    // — caller treats the brace as a literal character.
    private static bool TryReadName(string s, int start, out string name, out int nextIndex)
    {
        name = string.Empty;
        nextIndex = start + 1;
        int end = s.IndexOf('}', start + 1);
        if (end <= start + 1) return false;
        string candidate = s.Substring(start + 1, end - start - 1);
        if (!IsValidName(candidate)) return false;
        name = candidate;
        nextIndex = end + 1;
        return true;
    }

    // A wildcard name is any non-empty run of letters, digits, and underscores.
    // Digit-first names are allowed on purpose so {1} / {2} work as numbered
    // wildcards. A brace pair holding anything else — {HP=10/20}, {a b} — isn't a
    // name and stays literal.
    private static bool IsValidName(string s)
    {
        if (s.Length == 0) return false;
        for (int i = 0; i < s.Length; i++)
            if (!char.IsLetterOrDigit(s[i]) && s[i] != '_') return false;
        return true;
    }

    private Regex? GetOrCompile(Trigger t)
    {
        var key = (t.MatchType, t.Pattern);
        if (_regexCache.TryGetValue(key, out Regex? cached)) return cached;
        Regex? compiled = TryCompile(t);
        _regexCache[key] = compiled;
        return compiled;
    }

    private Regex? TryCompile(Trigger t)
    {
        if (string.IsNullOrEmpty(t.Pattern)) return null;
        try
        {
            string regex = t.MatchType == TriggerMatchType.Literal
                ? LiteralToRegex(t.Pattern)
                : t.Pattern;
            return new Regex(regex, RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            _log?.Log(LogSeverity.Warn, LogSource,
                $"'{t.Name}' has an invalid pattern — {ex.Message}");
            return null;
        }
    }

    // Translate a Literal pattern (with * wildcards and {name} placeholders)
    // into the equivalent .NET regex. Greedy spans so an end-of-pattern {name}
    // captures the rest of the line (non-greedy would settle for one character);
    // multi-capture patterns still resolve correctly because the regex engine
    // backtracks against the intervening literals. Internal for test visibility.
    internal static string LiteralToRegex(string literal)
    {
        StringBuilder sb = new(literal.Length + 16);
        int i = 0;
        while (i < literal.Length)
        {
            char c = literal[i];
            if (c == '{' && TryReadName(literal, i, out string name, out int next))
            {
                // Emit the user's name verbatim as the group name — {who}→(?<who>…),
                // {1}→(?<1>…). .NET accepts a numeric group name; the trigger push
                // loop reads it back by skipping only the whole-match group. Other
                // consumers of this helper (ChatRouter / PartyManager) read the group
                // by its exact name, so the name must stay unmangled.
                sb.Append("(?<").Append(name).Append(">.+)");
                i = next;
                continue;
            }
            if (c == '*')
            {
                sb.Append(".+");
                i++;
                continue;
            }
            sb.Append(Regex.Escape(c.ToString()));
            i++;
        }
        return sb.ToString();
    }

    private static TriggerScope? MapChannelToScope(ChatChannel channel) => channel switch
    {
        ChatChannel.Gossip            => TriggerScope.ChatGossip,
        ChatChannel.Local             => TriggerScope.ChatSay,
        ChatChannel.TelepathIncoming  => TriggerScope.ChatTelepath,
        ChatChannel.TelepathOutgoing  => TriggerScope.ChatTelepath,
        ChatChannel.Gangpath          => TriggerScope.ChatGangpath,
        ChatChannel.Broadcast         => TriggerScope.ChatBroadcast,
        ChatChannel.Yell              => TriggerScope.ChatYell,
        // RealmEvent + DaySeparator deliberately drop through — they
        // fire only the ChatAny scope, not any specific channel.
        _                             => null,
    };

    private void RememberClassified(ChatLogEntry entry)
    {
        _recentChat.Enqueue((entry.Timestamp, entry.RawText));
        while (_recentChat.Count > RecentChatCapacity) _recentChat.Dequeue();
    }

    private bool WasRecentlyClassified(LineExtractor.EmittedLine line)
    {
        foreach ((DateTimeOffset at, string text) in _recentChat)
        {
            if (at == line.Timestamp && string.Equals(text, line.Text, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ----- Per-set sync (GameData-scoped triggers) -----------------------

    // External hook (wired from AppServices) — called whenever the active
    // game-data set changes. Drops the prior set's GameData-scoped triggers from
    // the live collection and loads the new set's per-set file (or seeds from
    // defaults when the file doesn't exist yet). Profile-scoped triggers are left
    // untouched.
    public void OnActiveSetChanged(string? newSet)
    {
        _activeSet = string.IsNullOrWhiteSpace(newSet) ? null : newSet;
        DropTriggersByLocation(TriggerLocation.GameData);
        WatchPerSetFile(_activeSet);
        if (_activeSet is null) return;
        LoadPerSetTriggers(_activeSet);
    }

    // Watch the active set's triggers file for another client's writes. Watches the
    // directory rather than the file: the save lands as a temp file renamed over the
    // target, which a plain file watch can miss.
    private void WatchPerSetFile(string? setName)
    {
        _setFileWatcher?.Dispose();
        _setFileWatcher = null;
        if (setName is null) return;

        string path = AppPaths.TriggersFile(setName);
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            System.IO.FileSystemWatcher w = new(dir)
            {
                NotifyFilter = System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.LastWrite
                             | System.IO.NotifyFilters.Size,
            };
            string file = System.IO.Path.GetFileName(path);
            w.Changed += (_, e) => { if (e.Name == file) ScheduleExternalReload(setName); };
            w.Created += (_, e) => { if (e.Name == file) ScheduleExternalReload(setName); };
            w.Renamed += (_, e) => { if (e.Name == file) ScheduleExternalReload(setName); };
            w.EnableRaisingEvents = true;
            _setFileWatcher = w;
        }
        catch (Exception ex)
        {
            // Watching is a convenience — without it the file still loads on the next
            // set switch or restart, exactly as before.
            _log?.Log(LogSeverity.Warn, LogSource, $"Can't watch '{path}' for other clients' edits: {ex.Message}");
        }
    }

    // Watcher events arrive on a pool thread, several per save; coalesce them and
    // reload on the UI thread (the live collection is bound to the Triggers tab).
    private void ScheduleExternalReload(string setName) => ScheduleExternalReload(setName, ExternalReloadDebounce);

    private void ScheduleExternalReload(string setName, TimeSpan delay)
    {
        int generation = Interlocked.Increment(ref _reloadGeneration);
        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            if (generation != Volatile.Read(ref _reloadGeneration)) return;
            Dispatcher.UIThread.Post(() => ReloadIfChangedExternally(setName));
        }, TaskScheduler.Default);
    }

    private void ReloadIfChangedExternally(string setName)
    {
        if (!string.Equals(_activeSet, setName, StringComparison.Ordinal)) return;
        string path = AppPaths.TriggersFile(setName);
        if (!_gameDataUnread && ReadTextOrNull(path) is { } current && current == _syncedText)
            return;   // our own save, or nothing new
        if (TryReadPerSet(setName, out _))
            _log?.Log(LogSeverity.Info, LogSource,
                $"Game-data triggers for '{setName}' changed in another client — reloaded.");
    }

    // Read the set's triggers file and, only on success, replace the game-data slice
    // with it. On failure the slice is left alone, _gameDataUnread blocks saving over
    // the file, and the read is retried a few times.
    private bool TryReadPerSet(string setName, out bool fileExists)
    {
        string path = AppPaths.TriggersFile(setName);
        fileExists = System.IO.File.Exists(path);
        if (!fileExists) { _gameDataUnread = false; return false; }
        List<Trigger>? loaded;
        string? text;
        try
        {
            text = System.IO.File.ReadAllText(path);
            loaded = JsonStore.Load<List<Trigger>>(path);
        }
        catch (Exception ex)
        {
            _gameDataUnread = true;
            if (_readRetries++ < MaxReadRetries)
                ScheduleExternalReload(setName, ReadRetryDelay);
            _log?.Log(LogSeverity.Warn, LogSource,
                $"Couldn't read game-data triggers '{path}' ({ex.Message}) — keeping the current list"
                + (_readRetries <= MaxReadRetries ? ", retrying." : "; giving up until the set reloads."));
            return false;
        }
        DropTriggersByLocation(TriggerLocation.GameData);
        foreach (Trigger t in loaded ?? new List<Trigger>())
            Triggers.Add(t with { Location = TriggerLocation.GameData });
        _syncedText = text;
        _gameDataUnread = false;
        _readRetries = 0;
        return true;
    }

    private static string? ReadTextOrNull(string path)
    {
        try { return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null; }
        catch (System.IO.IOException) { return null; }   // mid-rename; the next event re-reads
        catch (UnauthorizedAccessException) { return null; }
    }

    // Read the active set's triggers.json. Falls back to the universal seed when
    // the per-set file doesn't exist; that seed "promotes" to a per-set file on
    // the next edit (PersistAll writes the new file).
    private void LoadPerSetTriggers(string setName)
    {
        _readRetries = 0;
        TryReadPerSet(setName, out bool fileExists);
        if (fileExists) return;
        // No per-set file yet → seed from the universal default.
        AppendFromFile(AppPaths.DefaultTriggersSeedFile, TriggerLocation.GameData);
    }

    // ----- Profile sync (Profile-scoped triggers) ------------------------

    // Apply the loaded profile's persisted Profile-scoped triggers.
    // GameData-scoped triggers from the active set are left in place; only the
    // Profile-scoped slice is refreshed.
    //
    // One-time migration runs inline: before the per-set storage model existed,
    // the universal seed was written straight into CharacterProfile.Triggers.
    // Any entry on the profile whose (Name, Pattern) matches the seed is treated
    // as legacy seed-leakage and dropped — it'll re-appear from the per-set
    // bucket via LoadPerSetTriggers, so the user doesn't see duplicates. Entries
    // that don't match the seed are genuine personal triggers and load as
    // Profile-scoped. The migration is idempotent: once profile.Triggers
    // contains no seed-matching entries, subsequent loads are a no-op.
    private void LoadFrom(CharacterProfile profile)
    {
        DropTriggersByLocation(TriggerLocation.Profile);
        if (profile.Triggers is null) return;

        HashSet<(string Name, string Pattern)> seedKeys = LoadSeedKeys();

        int dropped = 0;
        foreach (Trigger t in profile.Triggers)
        {
            if (seedKeys.Contains((t.Name, t.Pattern)))
            {
                dropped++;
                continue;
            }
            // Storage location is the truth — anything persisted on
            // the profile is Profile-scoped, regardless of any
            // Location value the record carries from disk.
            Triggers.Add(t with { Location = TriggerLocation.Profile });
        }

        if (dropped > 0)
        {
            _log?.Log(LogSeverity.Info, LogSource,
                $"Migrated profile triggers: dropped {dropped} entries that match the universal seed (they now load as GameData-scoped from the active set's triggers.json). Re-save the profile to persist the cleanup.");
        }
    }

    // Memoised (Name, Pattern) set for every entry in the universal seed, used
    // by LoadFrom to spot legacy seed-leakage on the profile. Empty when the seed
    // is missing (dev build / no Defaults folder) — degrades to no migration,
    // safe.
    private static HashSet<(string, string)>? _seedKeysCache;
    private static HashSet<(string, string)> LoadSeedKeys()
    {
        if (_seedKeysCache is not null) return _seedKeysCache;
        HashSet<(string, string)> set = new();
        string path = AppPaths.DefaultTriggersSeedFile;
        if (System.IO.File.Exists(path))
        {
            try
            {
                List<Trigger>? loaded = JsonStore.Load<List<Trigger>>(path);
                if (loaded is not null)
                    foreach (Trigger t in loaded) set.Add((t.Name, t.Pattern));
            }
            catch
            {
                // Corrupt seed ⇒ empty set; migration falls through harmless.
            }
        }
        _seedKeysCache = set;
        return set;
    }

    private void Clear() => Triggers.Clear();

    // Snapshot only the Profile-scoped slice onto the profile DTO. GameData-scoped
    // triggers persist via SavePerSetTriggers to the active set's per-set file
    // instead.
    private void SnapshotForSave(CharacterProfile profile)
    {
        profile.Triggers = Triggers
            .Where(t => t.Location == TriggerLocation.Profile)
            .Select(t => t with { Location = TriggerLocation.Profile })
            .ToList();
    }

    // ----- Shared helpers ------------------------------------------------

    // Remove every trigger whose Location matches location. Used by both refresh
    // paths to clear their own slice without touching the other.
    private void DropTriggersByLocation(TriggerLocation location)
    {
        for (int i = Triggers.Count - 1; i >= 0; i--)
        {
            if (Triggers[i].Location == location) Triggers.RemoveAt(i);
        }
    }

    // Read a list of triggers from path and append them to the live collection,
    // forcing each record's Location to forceLocation. The
    // file-location-is-the-truth rule lets us safely round-trip records between
    // the two buckets.
    private void AppendFromFile(string path, TriggerLocation forceLocation)
    {
        if (!System.IO.File.Exists(path)) return;
        try
        {
            List<Trigger>? loaded = JsonStore.Load<List<Trigger>>(path);
            if (loaded is null) return;
            foreach (Trigger t in loaded)
                Triggers.Add(t with { Location = forceLocation });
        }
        catch (Exception ex)
        {
            _log?.Log(LogSeverity.Warn, LogSource,
                $"Failed to load triggers from '{path}': {ex.Message}");
        }
    }
}

using System.Text.Json;
using MudPlay.Models.Profile;

namespace MudPlay.Services;

// Owns Data/BBS/{bbs}/profiles/{char}/profile.json — the Character tier of the
// settings hierarchy. Profiles nest under their BBS folder because each
// MajorMUD server allows only one character of a given name, so the same name
// on two BBSes is two different people. Tracks at most one loaded profile at a
// time (identified by the CurrentBbsName + CurrentProfileName pair);
// per-character services subscribe to ProfileLoaded / ProfileClosed to swap
// their per-char state.
public sealed class ProfileService
{
    // The currently loaded character profile. Set on startup so settings edits
    // always have a target — either the auto-loaded last-used profile, or the
    // default profile (see LoadDefaultProfile). Goes null only after an
    // explicit Close.
    public CharacterProfile? Current { get; private set; }

    // Profile-name identifier (filename without extension) of the loaded
    // profile, or null when the loaded profile is a blank in-memory draft that
    // hasn't been saved yet. Distinct from Current.Name because the in-game
    // name may differ from the profile filename when two characters share a
    // name across BBSes.
    public string? CurrentProfileName { get; private set; }

    // BBS folder the loaded profile lives under — the authoritative link
    // between a character and its server (there is no longer a BbsName field on
    // the DTO; folder location is the source of truth). For a named profile
    // this is set from disk on Load; for a blank draft it stays null until the
    // user pins a BBS via PinDraftBbs (Settings → BBS Apply). Consumed by
    // SettingsResolver and AppServices.ResolveActiveBbs to decide the active BBS.
    public string? CurrentBbsName { get; private set; }

    // Whether the current no-name profile persists to the Global default-profile
    // file on Save. LoadDefaultProfile sets it (the real default profile); the
    // in-memory scratch draft from LoadBlank leaves it false so its Save is a
    // no-op. Irrelevant once a named profile is loaded (Save keys on the name).
    private bool _defaultProfilePersists;

    // True when the loaded profile is the default profile (no named character) —
    // it persists to the Global default-profile file rather than a per-character
    // one, and gates File → Save's label + the "not a named character" UI.
    public bool IsBlankDraft => Current is not null && CurrentProfileName is null;

    // Late-bound system-log sink. Set by AppServices after construction so the
    // character-lifecycle transitions (load / swap / close / re-home) land in
    // the always-on Info stream — the peer of the game-data set audit in
    // GameDataCache. Left null in tests.
    public LogService? Log { get; set; }

    private const string LogCategory = "Profile";

    // Fired after a profile becomes Current. Per-character services rebind
    // their state inside the handler.
    public event Action<CharacterProfile>? ProfileLoaded;

    // Fired after Close wipes Current. Used by per-character services to
    // release per-char resources.
    public event Action? ProfileClosed;

    // Fired inside Save just before serialization. Subscribers write their
    // latest state into the profile DTO so it lands in the JSON. Example:
    // FloatingPanelHost updates CharacterProfile.PanelLayouts.
    public event Action<CharacterProfile>? ProfileSaving;

    // Fired whenever in-memory state on Current changes via the settings UI
    // (BBS pin, credential edit, etc.) — i.e. anywhere a disk save isn't
    // guaranteed (blank drafts no-op the save path but observers still need to
    // refresh). Bindings like the main window's title + active-BBS-derived
    // Host / Port listen here.
    public event Action<CharacterProfile>? ProfileMutated;

    // Fire ProfileMutated for the current profile, if any.
    public void NotifyMutated()
    {
        if (Current is not null) ProfileMutated?.Invoke(Current);
    }

    // Raised specifically from the Settings → BBS tab's Apply path so consumers
    // that care about an explicit BBS-pin selection — like the main window's
    // Quick Connect override — can clear themselves even when the user
    // re-selected the same BBS (i.e. the pinned name didn't change and
    // ProfileMutated would not otherwise convey new intent).
    public event Action<CharacterProfile>? BbsPinApplied;

    // Fire BbsPinApplied for the current profile, if any.
    public void NotifyBbsPinApplied()
    {
        if (Current is not null) BbsPinApplied?.Invoke(Current);
    }

    // Load the profile stored at
    // Data/BBS/{bbsName}/profiles/{profileName}/profile.json and fire
    // ProfileLoaded. If a different profile is already loaded it is closed
    // first (ProfileClosed fires before the new load). Throws
    // FileNotFoundException when no file exists at the expected path.
    public CharacterProfile Load(string bbsName, string profileName)
    {
        if (string.IsNullOrWhiteSpace(bbsName))
            throw new ArgumentException("BBS name is required.", nameof(bbsName));
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));

        string path = AppPaths.CharacterProfileFile(bbsName, profileName);
        CharacterProfile loaded = JsonStore.Load<CharacterProfile>(path)
            ?? throw new FileNotFoundException(
                $"Character profile '{profileName}' on '{bbsName}' not found.", path);
        NormalizeForLoad(loaded);
        // Upgrade older profiles before ProfileLoaded fires so per-character
        // services (KeybindingStore, toolbar) rebind from the migrated shape.
        bool migrated = ProfileMigrations.Apply(loaded);

        string? outgoing = CurrentProfileName;
        if (Current is not null)
        {
            // Auto-save the outgoing profile so per-session edits don't
            // bleed into / get lost behind the incoming profile. Save()
            // already no-ops on blank drafts (no name to write to), so
            // this is only consequential for the common "swap from one
            // named profile to another" path.
            try { Save(); }
            catch { /* swallow — Load shouldn't fail because the outgoing save did */ }
            Current = null;
            CurrentProfileName = null;
            CurrentBbsName = null;
            ProfileClosed?.Invoke();
        }

        Current = loaded;
        CurrentProfileName = profileName;
        CurrentBbsName = bbsName;
        Log?.Info(LogCategory, outgoing is null
            ? $"Loaded profile '{profileName}' on '{bbsName}'."
            : $"Swapped profile '{outgoing}' → '{profileName}' on '{bbsName}'.");
        ProfileLoaded?.Invoke(loaded);
        if (migrated)
        {
            // Persist the upgrade now that Current + paths are set — Save runs
            // after ProfileLoaded so the keybind snapshot reflects the reset
            // defaults, not the outgoing profile's bindings.
            Save();
            Log?.Info(LogCategory,
                $"Migrated profile '{profileName}' to schema v{CharacterProfile.CurrentSchemaVersion} " +
                "(reset keybindings + toolbar layout to the new defaults).");
        }
        return loaded;
    }

    // Normalize a freshly-deserialized profile's BBS-keyed lookups to be
    // case-insensitive. BBS names are case-insensitive (they're folder names on
    // a case-insensitive filesystem), but CharacterProfile.BbsCredentials may
    // have been keyed with different casing than the BBS profile's Name (e.g. a
    // "Playpen" credential key for a "playpen" BBS), which a default
    // case-sensitive dictionary fails to resolve. Rebuilds the dictionary with
    // StringComparer.OrdinalIgnoreCase (last-wins on any case-duplicate key).
    // Internal for test access.
    internal static void NormalizeForLoad(CharacterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.BbsCredentials is { Count: > 0 } creds
            && !ReferenceEquals(creds.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            var normalized = new Dictionary<string, BbsCredentials>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, BbsCredentials> kv in creds)
                normalized[kv.Key] = kv.Value;
            profile.BbsCredentials = normalized;
        }
    }

    // Start a fresh, never-persisted in-memory draft profile and fire
    // ProfileLoaded — a scratch profile whose edits stay in memory (Save is a
    // no-op, see _defaultProfilePersists) until it's named via File → Save As.
    // Distinct from LoadDefaultProfile, which loads the persisted Global default.
    // Any outgoing profile is auto-saved first so per-session edits aren't dropped.
    public CharacterProfile LoadBlank()
    {
        string? outgoing = CurrentProfileName;
        if (Current is not null)
        {
            try { Save(); }
            catch { /* swallow — a load shouldn't fail because the outgoing save did */ }
            Current = null;
            CurrentProfileName = null;
            ProfileClosed?.Invoke();
        }

        CharacterProfile draft = new();
        Current = draft;
        CurrentProfileName = null;
        CurrentBbsName = null;
        _defaultProfilePersists = false;
        Log?.Info(LogCategory, outgoing is null
            ? "Started a blank draft profile (no character loaded)."
            : $"Closed profile '{outgoing}'; started a blank draft profile.");
        ProfileLoaded?.Invoke(draft);
        return draft;
    }

    // Load the "default profile" — the CharacterProfile persisted at
    // AppPaths.DefaultProfileFile in the Global folder — and fire ProfileLoaded.
    // Used on app start when no last-used named profile exists, and by File → New.
    // When the file doesn't exist yet (fresh install) a new CharacterProfile
    // (installed defaults) stands in; the first Save creates it.
    //
    // CurrentProfileName stays null: the default profile isn't a named character,
    // so File → Save persists it back to the Global file (see Save) and File →
    // Save As copies it into a named character. Any outgoing profile is auto-saved
    // first so per-session edits aren't dropped.
    public CharacterProfile LoadDefaultProfile()
    {
        string? outgoing = CurrentProfileName;
        if (Current is not null)
        {
            try { Save(); }
            catch { /* swallow — a load shouldn't fail because the outgoing save did */ }
            Current = null;
            CurrentProfileName = null;
            ProfileClosed?.Invoke();
        }

        CharacterProfile profile = ReadDefaultProfileFile();
        NormalizeForLoad(profile);
        ProfileMigrations.Apply(profile);

        Current = profile;
        CurrentProfileName = null;
        CurrentBbsName = null;
        _defaultProfilePersists = true;
        Log?.Info(LogCategory, outgoing is null
            ? "Loaded the default profile (no character loaded)."
            : $"Closed profile '{outgoing}'; loaded the default profile.");
        ProfileLoaded?.Invoke(profile);
        return profile;
    }

    // Deserialize the Global default-profile file, or a fresh CharacterProfile
    // (installed defaults) when it's missing or unreadable — a corrupt default
    // must never block startup.
    private CharacterProfile ReadDefaultProfileFile()
    {
        if (!File.Exists(AppPaths.DefaultProfileFile)) return new CharacterProfile();
        try
        {
            return JsonStore.Load<CharacterProfile>(AppPaths.DefaultProfileFile)
                   ?? new CharacterProfile();
        }
        catch (Exception ex)
        {
            Log?.Warn(LogCategory,
                $"Default profile at '{AppPaths.DefaultProfileFile}' was unreadable ({ex.Message}); using installed defaults.");
            return new CharacterProfile();
        }
    }

    // The startup-animation preference is install-global, sourced from the Global
    // default profile — an attract screen shown before any character is active, so it
    // must not depend on which profile "auto-load last" happened to open. Reads
    // Current when the default profile is the loaded one, else the persisted file.
    public bool ReadDefaultProfileStartupAnimation()
        => ReadGeneralSection(CurrentProfileName is null && Current is not null
            ? Current
            : ReadDefaultProfileFile()).ShowStartupMudAnimation;

    // Persist the install-global startup-animation flag onto the Global default
    // profile. When the default profile is the loaded one, edit it in place and Save
    // (that path writes the Global file); otherwise load-modify-save the Global file
    // directly, leaving any loaded named profile untouched.
    public void WriteDefaultProfileStartupAnimation(bool value)
    {
        if (CurrentProfileName is null && Current is not null)
        {
            GeneralSettings live = ReadGeneralSection(Current);
            live.ShowStartupMudAnimation = value;
            WriteGeneralSection(Current, live);
            Save();
            return;
        }
        CharacterProfile profile = ReadDefaultProfileFile();
        GeneralSettings general = ReadGeneralSection(profile);
        general.ShowStartupMudAnimation = value;
        WriteGeneralSection(profile, general);
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.DefaultProfileFile)!);
        JsonStore.Save(AppPaths.DefaultProfileFile, profile);
    }

    private static GeneralSettings ReadGeneralSection(CharacterProfile profile)
    {
        if (profile.Settings is { } settings
            && settings.TryGetValue("General", out JsonElement json))
        {
            try { return JsonSerializer.Deserialize<GeneralSettings>(json) ?? new(); }
            catch { return new(); }
        }
        return new();
    }

    private static void WriteGeneralSection(CharacterProfile profile, GeneralSettings general)
    {
        profile.Settings ??= new();
        profile.Settings["General"] = JsonSerializer.SerializeToElement(general);
    }

    // Pin a BBS onto the loaded blank draft so its credentials / overrides have
    // a home before the draft is named. No-op (and ignored) for a named profile
    // — those re-home via ReHome instead, since the folder already exists on
    // disk. Fires nothing; the Settings → BBS Apply path that calls this also
    // raises NotifyMutated / NotifyBbsPinApplied.
    public void PinDraftBbs(string? bbsName)
    {
        if (CurrentProfileName is not null) return; // named profile → ReHome path owns this.
        CurrentBbsName = string.IsNullOrWhiteSpace(bbsName) ? null : bbsName;
    }

    // Move the loaded named profile's folder from its current BBS (and name) to
    // newBbs (and optionally newName), updating CurrentBbsName /
    // CurrentProfileName / CharacterProfile.Name to match. Silent — mirrors the
    // prompt-less rename the Settings → BBS tab does for the no-clash case.
    // Throws if no named profile is loaded or the destination folder already
    // exists (the caller resolves a name clash first, then re-invokes with a
    // clash-free newName).
    public void ReHome(string newBbs, string? newName = null)
    {
        if (string.IsNullOrWhiteSpace(newBbs))
            throw new ArgumentException("Destination BBS is required.", nameof(newBbs));
        if (Current is null || CurrentProfileName is null || CurrentBbsName is null)
            throw new InvalidOperationException("ReHome requires a loaded named profile.");

        string targetName = string.IsNullOrWhiteSpace(newName) ? CurrentProfileName : newName;
        if (string.Equals(newBbs, CurrentBbsName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetName, CurrentProfileName, StringComparison.Ordinal))
            return; // no-op move.

        string sourceFolder = AppPaths.ProfileFolder(CurrentBbsName, CurrentProfileName);
        string destFolder = AppPaths.ProfileFolder(newBbs, targetName);
        if (Directory.Exists(destFolder))
            throw new IOException($"A profile already exists at '{destFolder}'.");

        Directory.CreateDirectory(AppPaths.BbsProfilesDir(newBbs));
        if (Directory.Exists(sourceFolder))
            Directory.Move(sourceFolder, destFolder);
        else
            Directory.CreateDirectory(destFolder); // never-saved-yet edge: just stake the destination.

        string fromBbs = CurrentBbsName;
        string fromName = CurrentProfileName;
        Current.Name = targetName;
        CurrentProfileName = targetName;
        CurrentBbsName = newBbs;
        Log?.Info(LogCategory,
            $"Re-homed profile '{fromName}' ({fromBbs}) → '{targetName}' ({newBbs}).");
    }

    // Cascade a BBS rename across the character profiles stored under it. The
    // BBS folder has already been moved to newBbs (BbsProfileStore.Rename), so
    // the profiles now sit under the new name; this re-keys each profile's
    // BbsCredentials entry from oldBbs to newBbs — credentials are keyed by BBS
    // name, and a stale key breaks logon-nav / password lookup and shows the
    // dead name in the "import logon steps" picker. When the loaded profile
    // lives under the renamed BBS, its in-memory copy and CurrentBbsName are
    // updated too so the live session keeps resolving credentials and the
    // active-BBS link.
    public void RenameBbs(string oldBbs, string newBbs)
    {
        if (string.IsNullOrWhiteSpace(oldBbs) || string.IsNullOrWhiteSpace(newBbs)) return;
        if (string.Equals(oldBbs, newBbs, StringComparison.OrdinalIgnoreCase)) return;

        bool currentAffected = string.Equals(CurrentBbsName, oldBbs, StringComparison.OrdinalIgnoreCase);
        if (currentAffected) CurrentBbsName = newBbs;

        foreach (ProfileRef reference in ListAll())
        {
            if (!string.Equals(reference.Bbs, newBbs, StringComparison.OrdinalIgnoreCase)) continue;

            // The loaded profile's on-disk file is authored from its in-memory
            // copy below — skip it here to avoid a lost update.
            bool isCurrent = currentAffected
                && CurrentProfileName is not null
                && string.Equals(reference.Name, CurrentProfileName, StringComparison.Ordinal);
            if (isCurrent) continue;

            string path = AppPaths.CharacterProfileFile(newBbs, reference.Name);
            CharacterProfile? profile = JsonStore.Load<CharacterProfile>(path);
            if (profile is null) continue;
            NormalizeForLoad(profile);
            if (RekeyBbsCredentials(profile, oldBbs, newBbs))
                JsonStore.Save(path, profile);
        }

        if (currentAffected && Current is not null)
        {
            RekeyBbsCredentials(Current, oldBbs, newBbs);
            Save();
        }
    }

    // Move a BbsCredentials entry from oldBbs to newBbs on a profile's
    // case-insensitive credential map. Returns true if anything changed.
    private static bool RekeyBbsCredentials(CharacterProfile profile, string oldBbs, string newBbs)
    {
        if (profile.BbsCredentials is not { Count: > 0 } creds) return false;
        if (!creds.TryGetValue(oldBbs, out BbsCredentials? cred)) return false;
        creds.Remove(oldBbs);
        // TryAdd, not indexer: never clobber a pre-existing new-key entry. The
        // destination BBS was clash-checked before the rename, so this only
        // guards a corrupt double-keyed profile.
        creds.TryAdd(newBbs, cred);
        return true;
    }

    // Persist the currently loaded profile back to disk. No-op when no profile
    // is loaded or the loaded profile is a blank draft (IsBlankDraft) — drafts
    // must be named via the File → Save profile flow before they can be saved.
    // When backup is true and a previously-saved file exists, copy it to
    // {name}.json.bak (overwriting any prior backup) before the new content is
    // written. The General-settings Apply path passes true when the user opts
    // in via the "Backup profile when making changes" toggle.
    public void Save(bool backup = false)
    {
        if (Current is null) return;

        // The default profile (no named character) persists to the Global folder.
        // That file is the install-wide defaults and the template File → Save As
        // copies into named characters. A LoadBlank scratch draft leaves the flag
        // false, so its Save stays a no-op (edits land only when it's named).
        if (CurrentProfileName is null)
        {
            if (!_defaultProfilePersists) return;
            ProfileSaving?.Invoke(Current);
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.DefaultProfileFile)!);
            if (backup && File.Exists(AppPaths.DefaultProfileFile))
                File.Copy(AppPaths.DefaultProfileFile, AppPaths.DefaultProfileFile + ".bak", overwrite: true);
            JsonStore.Save(AppPaths.DefaultProfileFile, Current);
            return;
        }

        // A named profile with no pinned BBS has nowhere to write — it's named +
        // BBS-pinned via the File → Save As / Settings → BBS Apply flow first.
        if (CurrentBbsName is null) return;
        ProfileSaving?.Invoke(Current);

        Directory.CreateDirectory(AppPaths.ProfileFolder(CurrentBbsName, CurrentProfileName));
        string path = AppPaths.CharacterProfileFile(CurrentBbsName, CurrentProfileName);
        if (backup && File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }
        JsonStore.Save(path, Current);
    }

    // Read-modify-write one section of the loaded profile's Settings: deserialize
    // Settings[key] as T (a fresh default T when absent or unparseable), hand it to
    // the mutator, then serialize it back and persist. The single safe path when a
    // section is written from more than one view (e.g. Settings → Talk and the
    // Conversation window both touch "Talk") — each mutates only its own fields, so
    // neither clobbers the other. No-op when no profile is loaded; for an unnamed
    // draft the in-memory change stays and lands on the next named Save.
    public void UpdateSection<T>(string key, Action<T> mutate) where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(mutate);
        if (Current is not { } profile) return;

        T dto = new();
        if (profile.Settings is { } settings && settings.TryGetValue(key, out JsonElement json))
        {
            try { dto = JsonSerializer.Deserialize<T>(json) ?? new T(); }
            catch { dto = new T(); }
        }
        mutate(dto);
        profile.Settings ??= new();
        profile.Settings[key] = JsonSerializer.SerializeToElement(dto);
        Save();
    }

    // Clear Current and fire ProfileClosed.
    public void Close()
    {
        if (Current is null) return;
        string? outgoing = CurrentProfileName;
        Current = null;
        CurrentProfileName = null;
        CurrentBbsName = null;
        Log?.Info(LogCategory, outgoing is null
            ? "Closed the blank draft profile."
            : $"Closed profile '{outgoing}'.");
        ProfileClosed?.Invoke();
    }

    // Save the in-memory profile as profileName under bbsName. Used by
    // File → New profile (to name a fresh blank), File → Save As, and the "name
    // your draft" path of File → Save when the loaded profile doesn't have a
    // name yet. Replaces an existing file at that path without asking — the
    // caller owns the confirm-overwrite UX.
    public void SaveAs(string bbsName, string profileName)
    {
        if (string.IsNullOrWhiteSpace(bbsName))
            throw new ArgumentException("BBS name is required.", nameof(bbsName));
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required.", nameof(profileName));
        if (Current is null)
            throw new InvalidOperationException("No profile loaded to save.");

        Current.Name = profileName;
        CurrentProfileName = profileName;
        CurrentBbsName = bbsName;
        ProfileSaving?.Invoke(Current);
        Directory.CreateDirectory(AppPaths.ProfileFolder(bbsName, profileName));
        JsonStore.Save(AppPaths.CharacterProfileFile(bbsName, profileName), Current);
    }

    // Enumerate every profile across every BBS that has a primary profile.json
    // on disk, as (bbs, char) pairs. Folders missing a primary file are skipped
    // — they aren't fully initialised yet. Drives the File → Open picker and the
    // recent-profiles list.
    public IEnumerable<ProfileRef> ListAll()
    {
        if (!Directory.Exists(AppPaths.BbsDir)) yield break;
        foreach (string bbsFolder in Directory.EnumerateDirectories(AppPaths.BbsDir))
        {
            string bbs = Path.GetFileName(bbsFolder);
            if (string.IsNullOrEmpty(bbs)) continue;
            string profilesDir = AppPaths.BbsProfilesDir(bbs);
            if (!Directory.Exists(profilesDir)) continue;
            foreach (string charFolder in Directory.EnumerateDirectories(profilesDir))
            {
                string name = Path.GetFileName(charFolder);
                if (string.IsNullOrEmpty(name)) continue;
                if (!File.Exists(AppPaths.CharacterProfileFile(bbs, name))) continue;
                yield return new ProfileRef(bbs, name);
            }
        }
    }

    // True if a saved profile with the given name already exists under the BBS.
    public bool Exists(string bbsName, string profileName)
        => !string.IsNullOrWhiteSpace(bbsName)
           && !string.IsNullOrWhiteSpace(profileName)
           && File.Exists(AppPaths.CharacterProfileFile(bbsName, profileName));
}

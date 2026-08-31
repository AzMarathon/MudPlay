using System.Text;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Spells;

// Low-level spell-send layer. Builds the <cast-code> [target] wire command (the
// 4-letter cast-code is typed directly — NOT prefixed with the c cast verb), gates
// on a recent-cast cooldown + a "block until next combat tick" latch (set by
// server failure messages), and emits CastSent / CastFailed events so
// CastingDirector can sequence decisions on top.
//
// Three gates compose the "can I cast right now?" check:
//   - Recent-cast cooldown — one cast per combat round (5.5s default, matches
//     MajorMUD's between-round cap). Cleared by OnCombatTick so the very next round
//     can cast immediately.
//   - Cast-blocked latch — set on server failure lines (fizzle, no-mana,
//     already-cast-this-round, interrupted). Cleared by OnCombatTick OR by the
//     CastBlockExpiry timeout (safety net: out of combat no tick will fire, so we'd
//     be stuck otherwise).
//   - Min recast interval — short sub-cooldown between two consecutive TryCast
//     attempts (500ms) to absorb burst calls when CastingDirector evaluates
//     multiple candidates in the same frame.
//
// This is a foundation layer — it does NOT decide what to cast; CastingDirector
// picks the spell and target and calls TryCast. External engines that cast spells
// outside the director (CombatManager's pre-attack chain, for example) must call
// NotifyExternalCastSent so the cooldown is honoured.
public sealed class CastCoordinator : IDisposable
{
    // LogService category — appears as [Cast] rows per send + failure detection +
    // block-expire.
    public const string LogCategory = "Cast";

    // One cast per combat round (5.5s — slightly longer than the 5s tick to
    // account for server-side rounding). Reset by OnCombatTick.
    public static readonly TimeSpan CastCommandCooldown = TimeSpan.FromMilliseconds(5500);

    // Burst-absorb gap between two TryCast attempts. Keeps a single decision frame
    // from queuing two casts when only the first should land.
    public static readonly TimeSpan MinRecastInterval = TimeSpan.FromMilliseconds(500);

    // How long the cast-blocked latch lives without a combat tick clearing it. Out
    // of combat the tick never fires, so the latch must auto-expire or buffs would
    // never recast. Must match (or exceed) CastCommandCooldown — both gate the same
    // once-per-round cast slot, just from opposite directions (this is "you got
    // rejected, wait"; that one is "don't even try yet"). A shorter value here
    // self-clears the latch before the server's own slot has actually refreshed, so
    // the very next retry gets rejected again — report paradigm-20260831-091839
    // ("WHY DOES IT KEEP BUFFING ITSELF"): a self-buff spammed a reject/retry loop
    // every ~3-5s indefinitely while out of combat because this used to be a bare
    // 3s, racing the confirmed ~5.04s combat tick (GAME_MECHANICS.md) instead of
    // this file's own already-correct 5.5s cooldown for the identical constraint.
    public static readonly TimeSpan CastBlockExpiry = CastCommandCooldown;

    private readonly LogService? _log;
    private readonly IDisposable _fizzleSub;
    private readonly IDisposable _noManaSub;
    private readonly IDisposable _alreadySub;
    private readonly IDisposable _interruptSub;

    private Action<byte[]>? _wireSender;
    private DateTimeOffset _lastCastSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset _castBlockedSince = DateTimeOffset.MinValue;
    private bool _castBlocked;
    private bool _disposed;

    // Fires after a cast command was successfully written to the wire. Carries the
    // literal command line (no trailing CR).
    public event Action<string>? CastSent;

    // Fires whenever a cast attempt was rejected — either by our local gates or by
    // a server failure line. Reason carries the classification; the second arg is a
    // free-text detail (spell name from the fizzle regex, "cooldown" for local
    // gating, etc.); the third is the cast code the rejection applies to — the
    // spell just attempted for a local-gate block, or the last spell actually sent
    // to the wire for a server-line rejection (null if nothing's gone out yet).
    // MUST be checked against a pending cast's own code before reacting: multiple
    // callers (CastingDirector's between-round casts, CombatManager's attack-spell
    // cascade) share this coordinator, and the server's "already cast this round"
    // line never says which spell it's rejecting — treating every rejection as
    // "my pending cast failed" regardless of which cast it actually was drops a
    // landed buff's timer on an unrelated collision (report paradigm-20260824-233439:
    // an attack-spell resume racing the round slot repeatedly killed vlwa's just-armed
    // timer, forcing an immediate spurious recast every few seconds).
    public event Action<CastFailureReason, string, string?>? CastFailed;

    // The cast code most recently written to the wire via TryCast — what a
    // server-line rejection (fizzle / no-mana / already-cast-this-round /
    // interrupted) is presumably about, since those arrive asynchronously with no
    // spell identity of their own. Not updated by NotifyExternalCastSent (callers
    // outside TryCast don't report a spell code); a rejection landing while this is
    // stale from an external send is the same ambiguity the server's own message
    // already carries, not something this coordinator can resolve further.
    private string? _lastSpellSent;

    public CastCoordinator(MessageRouter router, LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        _log = log;
        _fizzleSub    = router.Subscribe(KnownPatterns.CastFizzled,          OnFizzle);
        _noManaSub    = router.Subscribe(KnownPatterns.CastNoMana,           OnNoMana);
        _alreadySub   = router.Subscribe(KnownPatterns.CastAlreadyThisRound, OnAlreadyThisRound);
        _interruptSub = router.Subscribe(KnownPatterns.CastInterrupted,      OnInterrupted);
    }

    // Bind the wire sender — typically the TelnetClient.SendAsync wrapper exposed
    // by MainWindowViewModel. Until set, TryCast short-circuits to false.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Drop a stale block latch that no combat tick ever cleared (out of combat the
    // tick never fires, so the latch must self-expire or buffs would never recast).
    private void ExpireStaleBlockLatch()
    {
        if (_castBlocked && DateTimeOffset.Now - _castBlockedSince >= CastBlockExpiry)
        {
            _castBlocked = false;
            _log?.Debug(LogCategory, "cast-block latch expired (no combat tick received)");
        }
    }

    // True while the failure latch or the recent-cast cooldown would reject a cast.
    // Auto-clears the block latch on read once CastBlockExpiry elapses.
    public bool IsCastBlocked
    {
        get
        {
            ExpireStaleBlockLatch();
            if (_castBlocked) return true;
            return DateTimeOffset.Now - _lastCastSentAt < CastCommandCooldown;
        }
    }

    // Attempt to send <cast-code> [target] (the configured 4-letter cast-code typed
    // directly — not prefixed with c). Returns true only if the command actually
    // went to the wire. Burst-absorbs back-to-back attempts via MinRecastInterval +
    // checks IsCastBlocked. spellName is the configured spell command-name (e.g.
    // "heal", "freedom"); whitespace-only is a no-op false return. target is an
    // optional explicit target — "self", a party member's name, or a monster name;
    // omit for self-cast spells where the server defaults the target to the caster.
    // bypassRoundCooldown skips the once-per-round CastCommandCooldown so a combat
    // engage / resume casts at the monster instantly — mirroring a weapon attack,
    // which has no cooldown gate at all. The failure latch still applies, so it can't
    // retry a fizzle. Per-round heartbeat re-casts leave it false.
    // bypassRecastInterval additionally skips the 500ms MinRecastInterval burst guard.
    // That guard exists to absorb CastingDirector's OWN back-to-back survival re-casts;
    // a combat-attack resume fired the instant a survival buff/heal's *Combat Off*
    // lands is a legitimate back-to-back (buff, then immediately attack) that the 500ms
    // window would otherwise defer to the next tick — a wasted round the mob swings
    // through (the "broke combat to cast armr, didn't re-attack until after they swung"
    // report). The combat dispatch is already paced once-per-round by ResumePacing /
    // the round cooldown, so it can't burst; CastingDirector's casts leave this false.
    public bool TryCast(string spellName, string? target = null,
                        bool bypassRoundCooldown = false, bool bypassRecastInterval = false)
    {
        if (string.IsNullOrWhiteSpace(spellName)) return false;
        if (_wireSender is null) return false;

        // Item-casts (#-prefixed buff-slot tokens) never go through the raw
        // cast path — they need an equip → use → re-equip sequence owned by the
        // item-cast engine. Reject here so a misrouted token can't be typed to
        // the wire as a bogus cast command.
        if (ItemCastToken.IsToken(spellName))
        {
            _log?.Debug(LogCategory, $"ignored item-cast token via TryCast: {spellName.Trim()}");
            CastFailed?.Invoke(CastFailureReason.Blocked, "item-cast-token", spellName.Trim());
            return false;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        ExpireStaleBlockLatch();

        // The failure latch (fizzle / no-mana / interrupt) blocks regardless of the
        // bypass — the last cast didn't take, so an instant retry would just re-fail.
        if (_castBlocked)
        {
            CastFailed?.Invoke(CastFailureReason.Blocked, "cast-blocked", spellName.Trim());
            return false;
        }
        // The once-per-round cooldown gates re-casts; the initial engage bypasses it.
        if (!bypassRoundCooldown && now - _lastCastSentAt < CastCommandCooldown)
        {
            CastFailed?.Invoke(CastFailureReason.Blocked, "cast-blocked", spellName.Trim());
            return false;
        }
        if (!bypassRecastInterval && now - _lastCastSentAt < MinRecastInterval)
        {
            CastFailed?.Invoke(CastFailureReason.Blocked, "recast-interval", spellName.Trim());
            return false;
        }

        // The configured spell value is already MajorMUD's cast-code (the
        // 4-letter abbreviation from game data), which is typed directly to
        // cast — NOT a spell name fed to the "c" (cast) command. Prefixing
        // "c " would make the server look up a spell literally named e.g.
        // "shce". Send the code as-is; append the target when given.
        string spell = spellName.Trim();
        string line = string.IsNullOrWhiteSpace(target)
            ? spell
            : $"{spell} {target.Trim()}";
        _wireSender(Encoding.Latin1.GetBytes(line + "\r"));
        _lastCastSentAt = now;
        _lastSpellSent = spell;
        _log?.Info(LogCategory, $"cast spell={spell} target={target ?? "<self>"}");
        CastSent?.Invoke(line);
        return true;
    }

    // External-cast notification. Engines that issue spell commands outside the
    // coordinator (CombatManager's pre-attack debuff, a user-typed `c X` line, etc.)
    // must call this so the cooldown blocks subsequent TryCast attempts for this
    // round.
    public void NotifyExternalCastSent()
    {
        _lastCastSentAt = DateTimeOffset.Now;
        _log?.Debug(LogCategory, "external cast noted — cooldown started");
    }

    // Hook the combat-tick boundary. Clears the block latch + resets the
    // recent-cast cooldown so the next round can cast immediately. Subscribe by
    // wiring TickEngine.CombatTickElapsed to this method in AppServices.
    public void OnCombatTick()
    {
        if (_castBlocked)
        {
            _castBlocked = false;
            _log?.Debug(LogCategory, "cast-block latch cleared on combat tick");
        }
        _lastCastSentAt = DateTimeOffset.MinValue;
    }

    // ----- failure handlers ------------------------------------------

    private void OnFizzle(MatchResult m)
    {
        string spell = m.Groups.Count > 0 ? m.Groups[0] : "<unknown>";
        BlockAndLog(CastFailureReason.Fizzled, $"spell={spell}");
    }

    private void OnNoMana(MatchResult _) =>
        BlockAndLog(CastFailureReason.NotEnoughMana, "insufficient-mana");

    private void OnAlreadyThisRound(MatchResult _) =>
        BlockAndLog(CastFailureReason.AlreadyCastThisRound, "already-cast-this-round");

    private void OnInterrupted(MatchResult _) =>
        BlockAndLog(CastFailureReason.Interrupted, "concentration-lost");

    private void BlockAndLog(CastFailureReason reason, string detail)
    {
        _castBlocked = true;
        _castBlockedSince = DateTimeOffset.Now;
        _log?.Info(LogCategory, $"cast failed reason={reason} {detail} spell={_lastSpellSent ?? "<unknown>"}");
        CastFailed?.Invoke(reason, detail, _lastSpellSent);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _fizzleSub.Dispose();
        _noManaSub.Dispose();
        _alreadySub.Dispose();
        _interruptSub.Dispose();
    }
}

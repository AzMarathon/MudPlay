using System.Text;
using MudPlay.Services;
using MudPlay.Services.Patterns;

namespace MudPlay.Game.Inventory;

// Auto-get items engine. Parses the room "You notice <list> here." survey
// line, resolves each entry against game data, and sends get <item name> for
// any item flagged ItemOverlay.AutoCollect.
//
// There is no bulk "get all" verb in MajorMUD — each item is collected
// individually by name. Every entry the survey line names is run through the
// injected resolver, which maps the loose room wording back to an item Number
// and reads its per-character AutoCollect override. Non-items (cash entries,
// scenery) and items not flagged for collection are skipped.
//
// Collect-after-combat: when CashSettings.CollectAfterCombatFinished is set
// (the shared Cash + Items timing toggle) and the room still holds engageable
// hostiles, the gets are queued and flushed on OnRoomObserved once combat
// clears (no engageable hostiles remain). When the toggle is off, or no
// hostiles are present, the gets fire immediately. A room change (OnRoomChanged)
// discards any un-flushed queue — those items belong to a room we've left.
//
// Movement gate: collecting (or deferring) items asserts the shared
// AcquisitionGate so the walker holds until get-clear — deferred items hold the
// gate before CombatStateTracker clears the Combat gate, defeating the
// synchronous walker-resume race; immediate gets hold it through a settle
// window. Bound via SetAcquisitionGate (optional — unbound, the engine doesn't
// gate movement).
//
// Master switch: AutoActionDefaults.AutoGetItems (shared with the Settings →
// General toggle and the toolbar Toggle command).
//
// Not yet handled: needs-fulfillment (grabbing a torch to satisfy a LightSource
// need), encumbrance gating, batching, and party provisioning.
public sealed class AutoGetItemsManager : IDisposable
{
    // LogService category — [AutoGet] rows per collected / deferred item.
    public const string LogCategory = "AutoGet";

    // One resolved room entry: the item's canonical Number (for the held-count
    // lookup), the name to send to the game, whether the user flagged it for
    // auto-collection, whether it is marked CannotBeTaken (a hard never-pick-up
    // flag that wins over AutoCollect — the engine never sends get for it), the
    // MaxToGet carry cap (int.MaxValue = unbounded), and the item's carry Weight
    // (MDB Encum; 0 when unknown — such items skip the encumbrance gate).
    public sealed record ResolvedItem(int Number, string Name, bool AutoCollect,
                                      bool CannotBeTaken, int MaxToGet, int Weight);

    private readonly Func<string, ResolvedItem?> _resolve;
    private readonly Func<int, int> _heldCount;
    private readonly Func<bool> _isEnabled;
    private readonly Func<bool> _collectAfterCombatFinished;
    private readonly Func<bool> _hasEngageableHostiles;
    private readonly Func<bool> _isPeekSuppressed;
    private readonly Func<EncumbranceReading> _encumbrance;
    private readonly Func<(bool Light, bool Medium, bool Heavy)> _itemEncGates;
    private readonly LogService? _log;
    private readonly IDisposable _noticeSub;

    // Cooldown so an area kill whose several corpses each drop a flagged item
    // re-surveys the room once, not once per corpse.
    private const int ReLookCooldownMs = 750;
    private DateTime _lastReLookAt = DateTime.MinValue;

    private Terminal.LineExtractor? _lines;
    private string? _noticeBuffer;            // multi-line continuation

    // Items deferred until the room's combat finishes. Cleared on flush
    // and on room change. Holds canonical names (already resolved).
    private readonly List<string> _deferred = new();

    // Command-side dedup ledger: gets already sent for the current room's floor,
    // keyed by item name. There is no server-side pickup-confirmation line to key
    // on (see AcquisitionGate), so a burst of "You notice" re-renders — the Cash
    // engine re-displaying to grab ground coin, our own post-kill re-look, the
    // combat-clear redisplay — all re-list the same still-unprocessed floor items.
    // Without this ledger every re-render fired another get, so four ground
    // orc-heads became eight-plus gets. Reconciled against the survey's current
    // count so a genuinely larger pile (a fresh drop) still tops up. Cleared on
    // room change and once the window below elapses — by then the sent gets are
    // reflected on the floor, so the next survey is authoritative again.
    private readonly Dictionary<string, int> _floorCollected =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTime _floorCollectedAt = DateTime.MinValue;

    // How long a sent get counts against re-collection. Long enough to outlast a
    // re-render burst (observed well under a second), short enough that a later
    // kill's drop — a full combat round away — starts from a clean slate.
    private const int FloorLedgerWindowMs = 1500;

    private Action<byte[]>? _wireSender;
    private AcquisitionGate? _gate;
    private bool _disposed;

    public AutoGetItemsManager(
        MessageRouter router,
        Func<string, ResolvedItem?> resolve,
        Func<bool> isEnabled,
        Func<bool> collectAfterCombatFinished,
        Func<bool> hasEngageableHostiles,
        Func<bool>? isPeekSuppressed = null,
        Func<int, int>? heldCount = null,
        Func<EncumbranceReading>? encumbrance = null,
        Func<(bool Light, bool Medium, bool Heavy)>? itemEncGates = null,
        LogService? log = null)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(isEnabled);
        ArgumentNullException.ThrowIfNull(collectAfterCombatFinished);
        ArgumentNullException.ThrowIfNull(hasEngageableHostiles);
        _resolve = resolve;
        // Null when unbound (tests without a cap case) → nothing is ever held,
        // so a MaxToGet cap can still fire off the first pickup. Live wiring
        // supplies the carried+worn+key-ring count.
        _heldCount = heldCount ?? (static _ => 0);
        _isEnabled = isEnabled;
        _collectAfterCombatFinished = collectAfterCombatFinished;
        _hasEngageableHostiles = hasEngageableHostiles;
        // Null when unbound (tests) → never a peek. A `look <dir>` peek renders a
        // full "You notice" survey for the adjacent room; gate the get path on it
        // so we don't send get commands (and trigger the get→inventory→equip
        // chain) against a room the player never entered.
        _isPeekSuppressed = isPeekSuppressed ?? (static () => false);
        // Null when unbound (tests) → Empty reading (MaxWeight 0) disables the
        // encumbrance gate, so those cases collect ungated. Live wiring supplies
        // the parsed InventorySnapshot.Encumbrance and the per-character item
        // bracket gates (the "Cash + Items" tab checkboxes).
        _encumbrance = encumbrance ?? (static () => EncumbranceReading.Empty);
        _itemEncGates = itemEncGates ?? (static () => (false, false, false));
        _log = log;

        _noticeSub = router.Subscribe(KnownPatterns.YouNoticeRoom, OnYouNoticeRoom);
    }

    // Bind the wire sender — the gate-wrapped engine pipeline from
    // MainWindowViewModel.
    public void SetWireSender(Action<byte[]> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        _wireSender = sender;
    }

    // Bind the shared AcquisitionGate so collecting (or deferring) items holds
    // the walker until get-clear. Optional — when unbound the engine doesn't
    // gate movement.
    public void SetAcquisitionGate(AcquisitionGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        _gate = gate;
    }

    // Bind the per-session LineExtractor so the manager can stitch a wrapped
    // "You notice" survey back together.
    public void AttachLineExtractor(Terminal.LineExtractor lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (ReferenceEquals(_lines, lines)) return;
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = lines;
        _lines.LineEmitted += OnLine;
    }

    // Called on each room-entity observation (wired after the combat tracker so
    // the hostile check is current). Flushes the deferred queue once no
    // engageable hostiles remain — the "combat finished for this room" signal.
    public void OnRoomObserved()
    {
        if (_deferred.Count == 0) return;
        if (_hasEngageableHostiles()) return;   // still fighting — keep waiting
        FlushDeferred();
    }

    // Called on actual room change. Discards any un-flushed deferred gets and the
    // dedup ledger — both belonged to the room we just left.
    public void OnRoomChanged()
    {
        _floorCollected.Clear();
        CancelDeferredCollect("room changed");
    }

    // Drop any deferred gets and release the acquisition hold — the items belonged
    // to a room/session we're no longer collecting for. Shared by the room-change
    // path and the reconnect resume: a disconnect mid-defer strands the hold (no
    // self-heal), leaving the walker paused until it's dropped.
    public void CancelDeferredCollect(string reason)
    {
        if (_deferred.Count == 0) return;
        _log?.Debug(LogCategory, $"{reason} — dropping {_deferred.Count} deferred get(s)");
        _deferred.Clear();
        _gate?.NoteDeferredCleared();
    }

    // Re-survey the room after a kill whose monster could drop an
    // AutoCollect-flagged item. A drop lands loose on the ground as the item but
    // isn't announced on the kill line, so a re-render of the "You notice … here."
    // survey the get path already parses is needed. A bare Enter (carriage return)
    // re-renders that survey exactly like `look` would but without look's room
    // description / flavor text, so we send an empty line — Send appends the \r,
    // making an empty payload a lone Enter. No-op while the master toggle is off;
    // cooldown-guarded so an area kill re-looks once (the single survey collects
    // every ground drop at once).
    public void RequestDropReLook()
    {
        if (_disposed || !_isEnabled()) return;
        DateTime now = DateTime.UtcNow;
        if ((now - _lastReLookAt).TotalMilliseconds < ReLookCooldownMs) return;
        _lastReLookAt = now;
        _log?.Info(LogCategory, "re-look after kill (monster drops a flagged item)");
        Send("");
    }

    // ----- notice parsing ----------------------------------------------

    // Single-line "You notice <list> here." — the pattern subscription path.
    // Multi-line wraps stitch through OnLine and feed the same dispatch.
    private void OnYouNoticeRoom(MatchResult m)
    {
        if (m.Groups.Count == 0) return;
        DispatchList(m.Groups[0]);
    }

    // Multi-line stitch mirrors CashManager.OnLine — a wrapped survey
    // ("You notice ...\n... here.") arrives as two emitted lines, so we
    // buffer from the "You notice " row until a row ends with '.'.
    // Duplicated rather than shared because the two engines own
    // independent buffers and CashManager is already shipped; factoring
    // a common stitcher would mean touching that engine for no behaviour
    // change. Single-line surveys are skipped here (the pattern
    // subscription handles them) to avoid double-processing.
    private void OnLine(Terminal.LineExtractor.EmittedLine line)
    {
        if (line.IsPromptLine) return;
        string text = line.Text.TrimEnd();
        if (text.Length == 0) return;

        if (_noticeBuffer is not null)
        {
            _noticeBuffer = _noticeBuffer + " " + text;
            if (text.EndsWith('.'))
            {
                string complete = _noticeBuffer;
                _noticeBuffer = null;
                ProcessMultiLine(complete);
            }
            return;
        }

        if (text.StartsWith("You notice ", StringComparison.Ordinal)
            && !text.EndsWith('.'))
        {
            _noticeBuffer = text;
        }
    }

    private void ProcessMultiLine(string completeLine)
    {
        const string prefix = "You notice ";
        if (!completeLine.StartsWith(prefix, StringComparison.Ordinal)) return;
        string body = completeLine[prefix.Length..].TrimEnd();
        const string suffix = " here.";
        if (body.EndsWith(suffix, StringComparison.Ordinal))
            body = body[..^suffix.Length];
        else if (body.EndsWith('.'))
            body = body[..^1];
        DispatchList(body);
    }

    // Split the survey list into entries, resolve each against game data, and
    // collect (or defer) those flagged for auto-collection. The deferred queue
    // is rebuilt per survey — a fresh "You notice" supersedes the prior room
    // snapshot.
    private void DispatchList(string list)
    {
        if (!_isEnabled()) return;
        // A look-direction peek renders a full "You notice" survey for the
        // adjacent room. Getting items from a room we never entered wastes
        // commands and (via the resulting inventory change) can fire auto-equip;
        // skip while the peek window is armed.
        if (_isPeekSuppressed())
        {
            _log?.Debug(LogCategory, "skipped you-notice survey (look-direction peek)");
            return;
        }

        ExpireFloorLedgerIfStale();

        bool deferMode = _collectAfterCombatFinished() && _hasEngageableHostiles();
        if (deferMode) _deferred.Clear();   // rebuild for this survey

        // Copies decided in this survey, by item Number — so a MaxToGet cap
        // holds even when a single "You notice" lists the same item twice
        // (the held-count snapshot doesn't move until the get echoes back).
        Dictionary<int, int>? decidedThisPass = null;

        // Floor-dedup bookkeeping for this survey (immediate path). _floorCollected
        // is read (not mutated) during the pass so the dedup skip is measured
        // against the pre-survey count — two separate "long sword" entries in ONE
        // survey are two floor copies and both collect, while the SAME sword
        // re-listed by a later re-render is skipped. seenThisSurvey tracks copies
        // encountered so far (to know our position past the already-sent count);
        // sentThisSurvey is merged into _floorCollected once, after the pass.
        Dictionary<string, int>? seenThisSurvey = null;
        Dictionary<string, int>? sentThisSurvey = null;

        // Encumbrance gate. With a known carry weight, cap what we grab this
        // survey so a pickup can't push past the character's capacity (the
        // always-on hard cap = MaxWeight) or past a user-flagged bracket. The
        // running projection charges each accepted item's weight so a survey
        // that lists several heavy items stops at the first that would overflow.
        EncumbranceReading enc = _encumbrance();
        bool encKnown = enc.MaxWeight > 0;
        long capWeight = long.MaxValue;
        if (encKnown)
        {
            (bool gL, bool gM, bool gH) = _itemEncGates();
            capWeight = EncumbranceGate.ComputeCapWeight(gL, gM, gH, enc);
        }
        long projectedWeight = enc.CurrentWeight;

        foreach (string entry in SplitEntries(list))
        {
            ResolvedItem? item = _resolve(entry);
            if (item is null) continue;          // not an item / not in game data
            if (item.CannotBeTaken)              // hard never-pick-up flag wins over AutoCollect
            {
                _log?.Debug(LogCategory, $"skipped item={item.Name} (cannot be taken)");
                continue;
            }
            if (!item.AutoCollect) continue;     // user didn't flag it

            // A stacked ground pile is surveyed with a leading count ("5 piece of
            // amber"): there is no bulk-get verb, so each get grabs a single unit
            // and the pile needs one get per counted unit — otherwise only one is
            // taken and the rest linger until the next room re-display. Each unit
            // re-checks the MaxToGet cap and the encumbrance projection on its own,
            // so a cap or weight ceiling stops the pile partway rather than after
            // the whole stack.
            int units = ParseLeadingCount(entry);
            // Command-side dedup (immediate path only — the deferred build is
            // reconciled at flush). Skip the copies a re-render of this same floor
            // already sent a get for; anything the survey lists beyond that count
            // is a genuinely larger pile (a fresh drop) and still gets collected.
            // Measured against the pre-survey count minus copies of this item
            // already seen earlier in THIS survey, so duplicate entries in one
            // survey collect while a re-render's re-listing is skipped.
            int skip = 0;
            if (!deferMode)
            {
                int alreadySent = _floorCollected.GetValueOrDefault(item.Name);
                int seenBefore = seenThisSurvey?.GetValueOrDefault(item.Name) ?? 0;
                skip = Math.Min(units, Math.Max(0, alreadySent - seenBefore));
                (seenThisSurvey ??= new(StringComparer.OrdinalIgnoreCase))[item.Name] =
                    seenBefore + units;
                if (skip > 0)
                    _log?.Debug(LogCategory,
                        $"re-render: skipping {skip} already-collected {item.Name} this room");
            }
            for (int u = skip; u < units; u++)
            {

                // MaxToGet carry cap. Count what we already hold (carried + worn +
                // key ring — key-type items land in the ring, not the pack) plus
                // anything already decided in this same survey; stop at the cap.
                // Decided-count and projected-weight are committed only after BOTH
                // gates pass, so a weight refusal doesn't burn a cap slot.
                int decidedSoFar = decidedThisPass?.GetValueOrDefault(item.Number) ?? 0;
                if (item.MaxToGet != int.MaxValue)
                {
                    int have = _heldCount(item.Number) + decidedSoFar;
                    if (have >= item.MaxToGet)
                    {
                        _log?.Debug(LogCategory,
                            $"skipped item={item.Name} (have {have} >= max {item.MaxToGet})");
                        break;   // cap reached — leave the rest of the pile
                    }
                }

                // Weight gate — skip a pickup that would breach the cap. Weight 0
                // (unknown OR genuinely weightless) means "don't gate": a weightless
                // item fits even at capacity, and an unknown-weight get is
                // self-correcting (the server picks up a weightless item and rejects
                // an over-cap one), so there's no reliable signal to suppress on.
                if (encKnown && item.Weight > 0 && projectedWeight + item.Weight > capWeight)
                {
                    _log?.Info(LogCategory,
                        $"skipped item={item.Name} (encumbrance: {projectedWeight}+{item.Weight} > cap {capWeight})");
                    break;   // over the weight cap — leave the rest of the pile
                }

                if (item.MaxToGet != int.MaxValue)
                {
                    decidedThisPass ??= new Dictionary<int, int>();
                    decidedThisPass[item.Number] = decidedSoFar + 1;
                }
                if (encKnown && item.Weight > 0) projectedWeight += item.Weight;

                if (deferMode)
                {
                    _deferred.Add(item.Name);
                    _log?.Info(LogCategory, $"deferred (combat) item={item.Name}");
                }
                else
                {
                    _log?.Info(LogCategory, $"collect item={item.Name}");
                    _gate?.NoteGetSent();
                    Send($"get {item.Name}");
                    (sentThisSurvey ??= new(StringComparer.OrdinalIgnoreCase))[item.Name] =
                        (sentThisSurvey.GetValueOrDefault(item.Name)) + 1;
                }
            }
        }

        // Commit this survey's immediate gets to the floor ledger in one shot so a
        // following re-render of the same floor reconciles against them.
        if (sentThisSurvey is not null)
        {
            foreach ((string name, int n) in sentThisSurvey)
                _floorCollected[name] = _floorCollected.GetValueOrDefault(name) + n;
            _floorCollectedAt = DateTime.UtcNow;
        }

        // Hold the walker while the queued gets wait for combat to finish.
        // Asserted now, before CombatStateTracker clears the Combat gate on
        // the same EntitiesObserved pass, so the walker can't slip out
        // between fight-clear and the loot flush.
        if (deferMode) _gate?.NoteDeferredPending(_deferred.Count);
    }

    private void FlushDeferred()
    {
        ExpireFloorLedgerIfStale();

        // Count the deferred gets per name so we can honour any copies a
        // same-window re-render already grabbed via the immediate path (normally
        // none — the deferred flush is the room's first collection). Preserves the
        // deferred order for the sends that do go out.
        Dictionary<string, int> deferredByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in _deferred)
            deferredByName[name] = deferredByName.GetValueOrDefault(name) + 1;

        Dictionary<string, int> flushSent = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in _deferred)
        {
            int accounted = _floorCollected.GetValueOrDefault(name) + flushSent.GetValueOrDefault(name);
            if (accounted >= deferredByName[name]) continue;   // ledger already covers this floor copy
            _log?.Info(LogCategory, $"collect (post-combat) item={name}");
            _gate?.NoteGetSent();
            Send($"get {name}");
            flushSent[name] = flushSent.GetValueOrDefault(name) + 1;
        }
        foreach ((string name, int n) in flushSent)
            _floorCollected[name] = _floorCollected.GetValueOrDefault(name) + n;
        if (flushSent.Count > 0) _floorCollectedAt = DateTime.UtcNow;

        _deferred.Clear();
        _gate?.NoteDeferredCleared();
    }

    // Drop the dedup ledger once its window has elapsed: by then the sent gets are
    // reflected on the floor, so a fresh survey is authoritative and a later kill's
    // drop must be collectable again.
    private void ExpireFloorLedgerIfStale()
    {
        if (_floorCollected.Count == 0) return;
        if ((DateTime.UtcNow - _floorCollectedAt).TotalMilliseconds > FloorLedgerWindowMs)
            _floorCollected.Clear();
    }

    // The survey prefixes a stacked ground pile with its count ("5 piece of
    // amber", "three torches"). Return that count so the collect loop can send
    // one get per unit; a missing or unrecognized leading token is the article
    // form ("a rusty dagger") — a single item. Mirrors the leading-count strip
    // in ItemNameStore.Normalize so the count parsed here is exactly the token
    // the resolver drops when matching the item name.
    private static int ParseLeadingCount(string entry)
    {
        int sp = entry.IndexOf(' ');
        if (sp <= 0) return 1;
        string first = entry[..sp];
        if (int.TryParse(first, out int n)) return n > 0 ? n : 1;
        return first.ToLowerInvariant() switch
        {
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            _ => 1,   // "one", an article, or a plain noun → single item
        };
    }

    // Split "a, b and c" survey wording into individual entries — commas
    // separate all but the final pair, which uses " and ".
    private static IEnumerable<string> SplitEntries(string list)
    {
        foreach (string comma in list.Split(',', StringSplitOptions.TrimEntries
                                              | StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string piece in comma.Split(" and ",
                         StringSplitOptions.TrimEntries
                         | StringSplitOptions.RemoveEmptyEntries))
            {
                if (piece.Length > 0) yield return piece;
            }
        }
    }

    private void Send(string text)
    {
        if (_wireSender is null) return;
        _wireSender(Encoding.Latin1.GetBytes(text + "\r"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _noticeSub.Dispose();
        if (_lines is not null) _lines.LineEmitted -= OnLine;
        _lines = null;
    }
}

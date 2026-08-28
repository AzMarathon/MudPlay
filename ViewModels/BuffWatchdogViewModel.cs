using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Spells;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.ViewModels;

// Read-only Buff Watchdog window VM. Lists the buffs the character has CONFIGURED
// (self-bless slots + HP/MA-regen + when-full + #item-cast, and party-bless slots),
// each with a live timer bar and a recast-window marker. The row list is (re)built
// from config; each 1-second heartbeat refills the bars from the CastingDirector
// timer snapshot. Never lists the whole learnable spellbook — only what's configured.
public sealed partial class BuffWatchdogViewModel : ObservableObject, IDisposable
{
    private readonly CastingDirector _castDirector;
    private readonly SpellbookState _spellbook;
    private readonly Game.TickEngine _tick;
    private readonly ProfileService _profile;
    private readonly Func<SpellsSettings> _readSpells;
    private readonly Func<PartyBuffSettings?> _readPartyBuffs;
    // Live party roster (non-self), so a single-target party buff can list ONE ROW
    // PER targeted member. Null on the test ctor (no per-member rows there).
    private readonly Game.PartyState? _party;

    private string _configSignature = string.Empty;
    private bool _needsRebuild = true;
    private bool _disposed;

    public ObservableCollection<BuffWatchdogRowViewModel> SelfBuffs { get; } = new();
    public ObservableCollection<BuffWatchdogRowViewModel> PartyBuffs { get; } = new();

    [ObservableProperty] private bool _hasSelfBuffs;
    [ObservableProperty] private bool _hasPartyBuffs;
    [ObservableProperty] private bool _isEmpty;

    // Production ctor — pulls the live services. Settings come through the resolver
    // (4-tier merged; bless slots live at the character tier, which wins).
    public BuffWatchdogViewModel()
        : this(AppServices.Current.CastDirector, AppServices.Current.Spellbook,
               AppServices.Current.Tick, AppServices.Current.Profile,
               () => AppServices.Current.Resolver.Resolve<SpellsSettings>("Spells"),
               () => AppServices.Current.Profile.Current?.PartyBuffs,
               AppServices.Current.PartyState)
    { }

    public BuffWatchdogViewModel(
        CastingDirector castDirector, SpellbookState spellbook,
        Game.TickEngine tick, ProfileService profile,
        Func<SpellsSettings> readSpells, Func<PartyBuffSettings?> readPartyBuffs,
        Game.PartyState? party = null)
    {
        _castDirector = castDirector;
        _spellbook = spellbook;
        _tick = tick;
        _profile = profile;
        _readSpells = readSpells;
        _readPartyBuffs = readPartyBuffs;
        _party = party;

        _spellbook.Changed += OnSpellbookChanged;
        _profile.ProfileLoaded += OnProfileLoaded;
        _tick.HeartbeatElapsed += OnHeartbeat;
        if (_party is not null) _party.Members.CollectionChanged += OnPartyMembersChanged;

        Refresh();
    }

    // The roster changed (a member joined / left) → a single-target buff's per-member
    // rows must be rebuilt so the target columns follow the party.
    private void OnPartyMembersChanged(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs __)
        => MarkRebuildAndRefresh();

    // Current non-self party members as (Display, lower-cased given). Empty when solo.
    private List<(string Display, string Given)> CurrentMembers()
    {
        List<(string, string)> members = new();
        if (_party is null) return members;
        foreach (Game.PartyMember m in _party.Members)
        {
            if (m.IsSelf) continue;
            string name = m.Name;
            string given = (name.Split(' ') is { Length: > 0 } parts ? parts[0] : name).ToLowerInvariant();
            members.Add((name, given));
        }
        return members;
    }

    private void OnSpellbookChanged() => MarkRebuildAndRefresh();
    private void OnProfileLoaded(CharacterProfile _) => MarkRebuildAndRefresh();

    private void MarkRebuildAndRefresh()
    {
        _needsRebuild = true;
        PostRefresh();
    }

    private void OnHeartbeat() => PostRefresh();

    private void PostRefresh()
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        if (_disposed) return;
        SpellsSettings spells = _readSpells();
        PartyBuffSettings? buffs = _readPartyBuffs();

        string sig = BuildSignature(spells, buffs);
        if (_needsRebuild || sig != _configSignature)
        {
            RebuildRows(spells, buffs);
            _configSignature = sig;
            _needsRebuild = false;
        }
        UpdateTimers();
    }

    private void UpdateTimers()
    {
        IReadOnlyList<ActiveBuffTimer> snap = _castDirector.SnapshotActiveBuffs();
        // While a disconnect has the timers paused, freeze the display at the drop instant
        // (the 1s heartbeat is a wall clock that keeps firing offline) — the resume shift
        // then keeps the on-screen remaining continuous across the gap.
        DateTime now = _castDirector.PausedAtUtc ?? DateTime.UtcNow;

        // In a party, a self-buff a configured party-wide buff removes shows "covered by"
        // that buff instead of a timer (the director suppresses self-casting it).
        IReadOnlyDictionary<string, string> coverage = _castDirector.CurrentSelfBuffCoverage();

        foreach (BuffWatchdogRowViewModel row in SelfBuffs)
        {
            ActiveBuffTimer? entry = null;
            foreach (ActiveBuffTimer t in snap)
                if (t.Target.Length == 0 && string.Equals(t.Short, row.CastCode, StringComparison.OrdinalIgnoreCase))
                { entry = t; break; }
            coverage.TryGetValue(row.CastCode, out string? coveredBy);
            row.Update(entry, now, coveredBy: coveredBy);
        }

        foreach (BuffWatchdogRowViewModel row in PartyBuffs)
        {
            // Each row tracks one timer, keyed by (cast code, target): a whole-party
            // (or #item) buff is one cast keyed to self (MemberKey ""); a single-target
            // buff has its own per-member row keyed by that member's given name.
            ActiveBuffTimer? match = null;
            foreach (ActiveBuffTimer t in snap)
                if (string.Equals(t.Short, row.CastCode, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Target, row.MemberKey, StringComparison.OrdinalIgnoreCase))
                {
                    match = t;
                    break;
                }
            row.Update(match, now);
        }
    }

    private void RebuildRows(SpellsSettings spells, PartyBuffSettings? buffs)
    {
        SelfBuffs.Clear();
        PartyBuffs.Clear();

        foreach (int slot in spells.BlessSlots.Keys.OrderBy(k => k))
            AddSelf(spells.BlessSlots[slot]);
        AddSelf(spells.HpRegenSpell);
        AddSelf(spells.MaRegenSpell);
        AddSelf(spells.WhenHpFullSpell);
        AddSelf(spells.WhenMaFullSpell);

        if (buffs is not null)
        {
            List<(string Display, string Given)> members = CurrentMembers();
            foreach (PartyBuffSlot p in buffs.Slots)
            {
                if (string.IsNullOrWhiteSpace(p.Spell)) continue;
                string code = p.Spell.Trim();
                (string name, bool learned) = ResolveName(code);

                if (IsWholePartySlot(code))
                {
                    // One cast blankets the party — a single timer, keyed to self.
                    PartyBuffs.Add(new BuffWatchdogRowViewModel(
                        code, isParty: true, name, "party", learned, isWholeParty: true));
                    continue;
                }

                // Single-target: cast on each targeted member individually, so give
                // each their OWN row + timer. Targets = everyone (AllMembers) or the
                // slot's chosen given names, intersected with the current roster.
                var targets = p.AllMembers
                    ? members
                    : members.Where(m => p.Targets.Contains(m.Given)).ToList();
                if (targets.Count == 0)
                {
                    // Nothing to track yet (no members / none selected) — one placeholder
                    // row so the configured buff still shows, with no timer.
                    PartyBuffs.Add(new BuffWatchdogRowViewModel(
                        code, isParty: true, name, TargetLabel(p, wholeParty: false), learned));
                    continue;
                }
                foreach ((string display, string given) in targets)
                    PartyBuffs.Add(new BuffWatchdogRowViewModel(
                        code, isParty: true, name, display, learned, isWholeParty: false, memberKey: given));
            }
        }

        HasSelfBuffs = SelfBuffs.Count > 0;
        HasPartyBuffs = PartyBuffs.Count > 0;
        IsEmpty = !HasSelfBuffs && !HasPartyBuffs;
    }

    private void AddSelf(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        (string name, bool learned) = ResolveName(code);
        SelfBuffs.Add(new BuffWatchdogRowViewModel(code.Trim(), isParty: false, name, "self", learned));
    }

    // Display label + learned flag. Spells show their 4-letter cast code (not the
    // full name); item-casts show '#' + a short prefix of the item name.
    private (string Name, bool Learned) ResolveName(string code)
    {
        string trimmed = code.Trim();
        if (ItemCastToken.IsToken(trimmed))
        {
            string item = (ItemCastToken.ItemName(trimmed) ?? trimmed).Trim();
            string shortItem = item.Length > 4 ? item[..4] : item;
            return ("#" + shortItem, true);   // a carried buff item counts as available
        }
        return _spellbook.FindByCastCode(trimmed) is { } s
            ? (s.Short, _spellbook.IsObtained(s.Number))
            : (trimmed, false);   // unknown cast code — show it, flagged un-learned
    }

    // The target summary shown on a party-buff row: whole-party buffs read "party";
    // single-target buffs read "all" or the chosen given names.
    private string TargetLabel(PartyBuffSlot p, bool wholeParty)
    {
        if (wholeParty) return "party";
        if (p.AllMembers) return "all";
        return p.Targets.Count > 0 ? string.Join(", ", p.Targets) : "(no targets)";
    }

    // Whether a party-buff slot's cast value is whole-party — a spell with a whole-party
    // Targets scope, or a #item-cast whose item casts a whole-party spell.
    private bool IsWholePartySlot(string? spell)
    {
        if (string.IsNullOrWhiteSpace(spell)) return false;
        string s = spell.Trim();
        if (ItemCastToken.IsToken(s)) return _spellbook.IsTokenWholeParty(s);
        return _spellbook.FindByCastCode(s) is { } ks && PartyBuffClassifier.IsWholeParty(ks.Targets);
    }

    // Cheap fingerprint of the configured buff set — a change (live edit) triggers a
    // row rebuild on the next heartbeat.
    private static string BuildSignature(SpellsSettings spells, PartyBuffSettings? buffs)
    {
        StringBuilder sb = new();
        foreach (int slot in spells.BlessSlots.Keys.OrderBy(k => k))
            sb.Append(slot).Append('=').Append(spells.BlessSlots[slot]).Append('|');
        sb.Append(spells.HpRegenSpell).Append('|').Append(spells.MaRegenSpell).Append('|')
          .Append(spells.WhenHpFullSpell).Append('|').Append(spells.WhenMaFullSpell).Append("||");
        if (buffs is not null)
            foreach (PartyBuffSlot p in buffs.Slots)
                sb.Append(p.Spell).Append(':').Append(p.WholePartyOn ? "W" : "")
                  .Append(p.AllMembers ? "A" : "").Append(string.Join(",", p.Targets)).Append('|');
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _spellbook.Changed -= OnSpellbookChanged;
        _profile.ProfileLoaded -= OnProfileLoaded;
        _tick.HeartbeatElapsed -= OnHeartbeat;
        if (_party is not null) _party.Members.CollectionChanged -= OnPartyMembersChanged;
    }
}

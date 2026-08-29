using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    // Live buff-timer bars, grouped by the player each buff is on: your own name
    // (self + whole-party buffs) first, then one section per party member carrying
    // the buffs cast on them.
    public ObservableCollection<BuffWatchdogPlayerGroup> Groups { get; } = new();

    [ObservableProperty] private bool _isEmpty;

    // The editable buff-config panel (add / edit / remove / target). It lives in this
    // window now — the Buff Watchdog is the single place to both SEE and CONFIGURE
    // buffs. Null on the test ctor (no live services).
    public PartyBuffPanelViewModel? Buffs { get; }

    // Production ctor — pulls the live services. Settings come through the resolver
    // (4-tier merged; bless slots live at the character tier, which wins).
    public BuffWatchdogViewModel()
        : this(AppServices.Current.CastDirector, AppServices.Current.Spellbook,
               AppServices.Current.Tick, AppServices.Current.Profile,
               () => AppServices.Current.Resolver.Resolve<SpellsSettings>("Spells"),
               () => AppServices.Current.Profile.Current?.PartyBuffs,
               AppServices.Current.PartyState)
    {
        Buffs = new PartyBuffPanelViewModel(AppServices.Current.PartyState);
    }

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

    // Manually clear one row's buff timer (the ✕ button) — mark that buff off. A
    // configured, still-due buff recasts on the next evaluation; a phantom timer just
    // drops. Refresh at once so the row updates without waiting for the heartbeat.
    [RelayCommand]
    private void ClearTimer(BuffWatchdogRowViewModel? row)
    {
        if (row is null) return;
        _castDirector.ClearBuffTimer(row.MemberKey, row.CastCode);
        PostRefresh();
    }

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
        IReadOnlyList<ActiveBuffTimer> snap = _castDirector.SnapshotActiveBuffs();

        string sig = BuildSignature(spells, buffs, snap);
        if (_needsRebuild || sig != _configSignature)
        {
            RebuildRows(spells, buffs, snap);
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
        IReadOnlyCollection<string> hidden = _castDirector.HiddenPartyTargets;

        foreach (BuffWatchdogPlayerGroup group in Groups)
        foreach (BuffWatchdogRowViewModel row in group.Rows)
        {
            // Single-target member row (keyed by their given name). A member who's HIDING
            // (a cast came back "You do not see … here!") can't be reached — show that.
            if (row.IsParty && !row.IsWholeParty && row.MemberKey.Length > 0)
            {
                if (hidden.Contains(row.MemberKey)) { row.Update(null, now, hidden: true); continue; }
                ActiveBuffTimer? match = null;
                foreach (ActiveBuffTimer t in snap)
                    if (string.Equals(t.Short, row.CastCode, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(t.Target, row.MemberKey, StringComparison.OrdinalIgnoreCase))
                    { match = t; break; }
                row.Update(match, now);
                continue;
            }

            // Self-cast or whole-party row: one cast keyed to self (""). Self-cast rows
            // may be covered by a configured party-wide buff.
            ActiveBuffTimer? entry = null;
            foreach (ActiveBuffTimer t in snap)
                if (t.Target.Length == 0 && string.Equals(t.Short, row.CastCode, StringComparison.OrdinalIgnoreCase))
                { entry = t; break; }
            string? coveredBy = null;
            if (!row.IsParty) coverage.TryGetValue(row.CastCode, out coveredBy);
            row.Update(entry, now, coveredBy: coveredBy);
        }
    }

    private void RebuildRows(SpellsSettings spells, PartyBuffSettings? buffs, IReadOnlyList<ActiveBuffTimer> snap)
    {
        Groups.Clear();

        // Section order: you first, then each current party member (so a member with no
        // active buff still gets a seeded section, dropped below only if truly empty).
        Dictionary<string, BuffWatchdogPlayerGroup> byName = new(StringComparer.OrdinalIgnoreCase);
        BuffWatchdogPlayerGroup self = GetGroup(byName, SelfName());
        List<(string Display, string Given)> members = CurrentMembers();
        Dictionary<string, string> displayByGiven = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string display, string given) in members)
        {
            displayByGiven[given] = display;
            GetGroup(byName, display);   // seed member section in roster order
        }

        // Mana / HP regen still live on the Spells tab; every other self buff is a
        // CastOnSelf slot in the unified list. All land on you → your section.
        AddSelfRow(self, spells.HpRegenSpell);
        AddSelfRow(self, spells.MaRegenSpell);

        if (buffs is not null)
        {
            foreach (PartyBuffSlot p in buffs.Slots)
            {
                if (string.IsNullOrWhiteSpace(p.Spell)) continue;
                string code = p.Spell.Trim();
                (string name, bool learned) = ResolveName(code);
                bool wholeParty = IsWholePartySlot(code);

                // Self-cast + whole-party buffs land on you → your section (both keyed "").
                if (p.CastOnSelf && !wholeParty)
                    self.Rows.Add(new BuffWatchdogRowViewModel(code, isParty: false, name, "self", learned));
                if (wholeParty)
                {
                    self.Rows.Add(new BuffWatchdogRowViewModel(
                        code, isParty: true, name, "whole party", learned, isWholeParty: true));
                    continue;
                }

                // Single-target: one row per member, under that member's section. Show a
                // row for each CONFIGURED target (AllMembers = the roster, else the chosen
                // names) PLUS any member who already has a LIVE timer for this spell — so a
                // member you unticked (or who left) keeps their countdown until it expires.
                List<string> givens = new();
                void AddGiven(string g)
                {
                    if (!givens.Any(x => x.Equals(g, StringComparison.OrdinalIgnoreCase))) givens.Add(g);
                }
                if (p.AllMembers)
                    foreach ((string _, string given) in members) AddGiven(given);
                else
                    foreach ((string _, string given) in members.Where(m => p.Targets.Contains(m.Given)))
                        AddGiven(given);
                foreach (ActiveBuffTimer t in snap)
                    if (t.Target.Length > 0 && string.Equals(t.Short, code, StringComparison.OrdinalIgnoreCase))
                        AddGiven(t.Target);   // already lower-cased

                if (givens.Count == 0)
                {
                    // Configured single-target buff with nobody targeted yet — a not-up
                    // placeholder under your section (a self-cast slot's self row above
                    // already represents it).
                    if (!p.CastOnSelf)
                        self.Rows.Add(new BuffWatchdogRowViewModel(
                            code, isParty: true, name, TargetLabel(p, wholeParty: false), learned));
                    continue;
                }
                foreach (string given in givens)
                {
                    string display = displayByGiven.TryGetValue(given, out string? d) ? d : Capitalise(given);
                    GetGroup(byName, display).Rows.Add(new BuffWatchdogRowViewModel(
                        code, isParty: true, name, display, learned, isWholeParty: false, memberKey: given));
                }
            }
        }

        // Drop seeded sections that ended up with no buffs.
        for (int i = Groups.Count - 1; i >= 0; i--)
            if (Groups[i].Rows.Count == 0) Groups.RemoveAt(i);

        IsEmpty = Groups.Count == 0;
    }

    // Find or create a player section by header name, appended in first-seen order.
    private BuffWatchdogPlayerGroup GetGroup(Dictionary<string, BuffWatchdogPlayerGroup> byName, string name)
    {
        if (byName.TryGetValue(name, out BuffWatchdogPlayerGroup? g)) return g;
        g = new BuffWatchdogPlayerGroup(name);
        byName[name] = g;
        Groups.Add(g);
        return g;
    }

    // Your own display name for the self section: your given name from the party
    // roster if present, else the loaded profile's character name, else "You".
    private string SelfName()
    {
        if (_party is not null)
            foreach (Game.PartyMember m in _party.Members)
                if (m.IsSelf && !string.IsNullOrWhiteSpace(m.Name))
                    return Capitalise(GivenLower(m.Name));
        string? n = _profile.Current?.Name;
        return string.IsNullOrWhiteSpace(n) ? "You" : Capitalise(GivenLower(n));
    }

    private static string GivenLower(string name) =>
        (name.Split(' ') is { Length: > 0 } parts ? parts[0] : name).ToLowerInvariant();

    private static string Capitalise(string given) =>
        given.Length == 0 ? given : char.ToUpperInvariant(given[0]) + given[1..];

    private void AddSelfRow(BuffWatchdogPlayerGroup group, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return;
        (string name, bool learned) = ResolveName(code);
        group.Rows.Add(new BuffWatchdogRowViewModel(code.Trim(), isParty: false, name, "self", learned));
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

    // Cheap fingerprint of the configured buff set AND the active party-buff timer keys
    // — a config change (live edit) OR a timer arming / expiring triggers a rebuild. The
    // timer keys matter so a member you blessed then unticked keeps a row until the timer
    // actually expires (then the key drops → rebuild → row goes away). Only the KEY set
    // (short@target), never the remaining time, so it doesn't churn every second.
    private static string BuildSignature(
        SpellsSettings spells, PartyBuffSettings? buffs, IReadOnlyList<ActiveBuffTimer> snap)
    {
        StringBuilder sb = new();
        // Mana / HP regen are the only self buffs still on the Spells tab.
        sb.Append(spells.HpRegenSpell).Append('|').Append(spells.MaRegenSpell).Append("||");
        if (buffs is not null)
            foreach (PartyBuffSlot p in buffs.Slots)
                sb.Append(p.Spell).Append(':').Append(p.CastOnSelf ? "S" : "")
                  .Append(p.WholePartyOn ? "W" : "").Append(p.AllMembers ? "A" : "")
                  .Append(p.OnlyWhenHpFull ? "H" : "").Append(p.OnlyWhenMaFull ? "M" : "")
                  .Append(string.Join(",", p.Targets)).Append('|');
        sb.Append("||");
        foreach (string k in snap.Where(t => t.Target.Length > 0)
                                 .Select(t => t.Short + "@" + t.Target)
                                 .OrderBy(k => k, StringComparer.Ordinal))
            sb.Append(k).Append(';');
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
        Buffs?.Dispose();
    }
}

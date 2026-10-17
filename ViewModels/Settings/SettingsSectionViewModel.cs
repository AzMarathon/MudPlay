using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Spells;
using MudPlay.Services;

namespace MudPlay.ViewModels.Settings;

// Base for one section in the SettingsWindowViewModel's sidebar. Each section
// knows where its own data lives — mostly the loaded character profile, but some
// fields are BBS-record or install-global (Global tier) — so sections take no
// runtime scope and the window has no tier picker.
//
// Game-data record overrides use the four-tier (Defaults / Global / BBS / Char)
// hierarchy via SettingsResolver, over in the Game Data Browser. Settings-tab
// data has no tier picker.
public abstract partial class SettingsSectionViewModel : ObservableObject, IDisposable
{
    // Cleanup callbacks (typically event `-=`) registered by sections that hook
    // publishers outliving the Settings window — ProfileService, GameDataCache,
    // PlayerState, the spellbook, keybindings. Run on Dispose when the window
    // closes; without them the singletons keep every section VM (and the whole
    // window graph) alive for the app's lifetime, leaking a fresh copy per reopen.
    private readonly List<Action> _teardown = new();
    private bool _disposed;

    // Stable identifier — sidebar selection persists across reopens against this.
    public abstract string Id { get; }

    // Title shown in the sidebar.
    public abstract string Title { get; }

    // True when this section has unapplied edits.
    public virtual bool IsDirty => false;

    // Extra words the shell's search box finds this section by, beside the text its
    // settings show on screen (labels and tooltips, read from the View). Default:
    // title only. Tabs override to add names a user might search that the screen
    // doesn't spell out, so "rest" finds the Health tab.
    public virtual IEnumerable<string> SearchableLabels => new[] { Title };

    // The section holds the search box's text — highlighted in the sidebar. The
    // search never hides a section.
    [ObservableProperty] private bool _isMatch;

    // Mark this section against the search box's query: a match when one of its
    // SearchableLabels, or the text its settings show (read only when needed — it
    // builds the View), holds the query. A blank query clears the mark.
    public bool ApplySearch(string query, Func<SettingsSectionViewModel, IEnumerable<string>>? shownText)
    {
        IsMatch = !SearchMatch.IsBlank(query)
            && (SearchableLabels.Any(l => SearchMatch.Matches(l, query))
                || (shownText?.Invoke(this).Any(t => SearchMatch.Matches(t, query)) ?? false));
        return IsMatch;
    }

    // The editor UserControl rendered in the shell's content pane. Lazy —
    // constructed on first access, so opening the window doesn't wait on every
    // section; the window builds the rest while idle, for the search to read.
    public abstract Control View { get; }

    // Persist this section's pending edits. Default no-op for placeholders.
    public virtual void Apply() { }

    // Something the user should hear before these edits are saved (null when all is
    // well) — the Settings window asks "Save anyway?" when a dirty section has one.
    public virtual string? SaveWarning => null;

    // Drop pending edits and re-read from the underlying store.
    public virtual void Discard() { }

    // Shared typeahead filter for the spell-picker boxes: matches the typed
    // text against either the 4-letter cast-code or the full spell name, so a
    // slot can be found by code or by name even though the box commits the code.
    // Bound as ItemFilter on each AutoCompleteBox.
    public AutoCompleteFilterPredicate<object?> SpellSuggestionFilter { get; } =
        static (search, item) =>
            item is SpellPick pick &&
            (string.IsNullOrEmpty(search)
             || pick.Short.Contains(search, StringComparison.OrdinalIgnoreCase)
             || pick.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

    // True when code names a spell this class CAN learn but the character hasn't —
    // the picker outlines the box red as a "you haven't learned this" warning.
    // A code that matches no suggestion (a freeform / other-class value) is NOT
    // flagged, and when the obtained set is unknown every pick reports Learned =
    // true, so the whole class never lights up red before the spell list is parsed.
    protected static bool IsSpellUnlearned(IReadOnlyList<SpellPick> picks, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        string c = code.Trim();
        foreach (SpellPick p in picks)
            if (string.Equals(p.Short, c, StringComparison.OrdinalIgnoreCase))
                return !p.Learned;
        return false;
    }

    // Register a cleanup callback (typically an event -=) to run when the
    // Settings window closes. Sections that subscribe to a singleton outliving
    // the window MUST unhook here, or the publisher pins the whole VM graph
    // forever.
    protected void OnDispose(Action teardown)
    {
        ArgumentNullException.ThrowIfNull(teardown);
        _teardown.Add(teardown);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (Action t in _teardown) t();
        _teardown.Clear();
    }
}

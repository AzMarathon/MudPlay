using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Services;

namespace MudPlay.ViewModels.Help;

// One node in the Help window's table-of-contents tree — a section or subsection
// wrapping a HelpTopic. Carries the markdown body shown in the content pane, and
// the IsVisible / IsExpanded state the search filter drives (the TreeViewItem
// style binds IsVisible so a filter collapses non-matching branches without
// rebuilding the tree, preserving the rest).
public sealed partial class HelpNodeViewModel : ObservableObject
{
    public string Title { get; }
    public string Body { get; }
    public IReadOnlyList<HelpNodeViewModel> Children { get; }

    // Bound by the TreeViewItem style — a filter hides non-matching nodes.
    [ObservableProperty] private bool _isVisible = true;

    // Bound TwoWay so the filter can auto-open branches with matches, and the
    // user can still expand/collapse freely when no filter is active.
    [ObservableProperty] private bool _isExpanded;

    public HelpNodeViewModel(HelpTopic topic)
    {
        Title = topic.Title;
        Body = topic.Body;
        Children = topic.Children.Select(c => new HelpNodeViewModel(c)).ToList();
    }

    // The node's own title or body contains the query (case-insensitive).
    public bool SelfMatches(string query) =>
        Title.Contains(query, System.StringComparison.OrdinalIgnoreCase)
        || Body.Contains(query, System.StringComparison.OrdinalIgnoreCase);

    // Apply the search filter to this subtree. Returns true when this node or any
    // descendant matches. Sets IsVisible (kept if self or a descendant matches)
    // and expands branches that hold a match so the hit is revealed. A blank
    // query resets the whole subtree to visible + collapsed. A parent that
    // matches by title does NOT force-show non-matching children — each child's
    // visibility is decided independently, so results stay focused.
    public bool ApplyFilter(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            foreach (HelpNodeViewModel c in Children) c.ApplyFilter(query);
            IsVisible = true;
            IsExpanded = false;
            return true;
        }

        bool anyChild = false;
        foreach (HelpNodeViewModel c in Children)
            anyChild |= c.ApplyFilter(query);

        IsVisible = SelfMatches(query) || anyChild;
        IsExpanded = anyChild;
        return IsVisible;
    }

    // Re-open the branch path down to `target`. Used after a filter clears (which
    // collapses every branch) so a still-selected subsection isn't left hidden
    // inside a collapsed parent — the content pane keeps showing it, and now the
    // tree selection is visible too. Returns true when `target` is this node or
    // lives in its subtree; this node expands only when the target is below it.
    public bool ExpandToReveal(HelpNodeViewModel target)
    {
        if (ReferenceEquals(this, target)) return true;
        bool below = false;
        foreach (HelpNodeViewModel c in Children)
            below |= c.ExpandToReveal(target);
        if (below) IsExpanded = true;
        return below;
    }
}

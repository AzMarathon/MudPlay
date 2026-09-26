using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Services;

namespace MudPlay.ViewModels.Help;

// One node in the Help window's table-of-contents tree — a section or subsection
// wrapping a HelpTopic. Carries the markdown body shown in the content pane, and
// the IsMatch / IsExpanded state the search drives. Searching never hides a node:
// the whole tree stays in place, matching topics are highlighted (IsMatch), and
// the branches that hold a match open so every hit is in view.
public sealed partial class HelpNodeViewModel : ObservableObject
{
    public string Title { get; }
    public string Body { get; }
    public IReadOnlyList<HelpNodeViewModel> Children { get; }

    // The node's own title or body contains the search text — highlighted in the tree.
    [ObservableProperty] private bool _isMatch;

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

    // Apply the search to this subtree. Returns true when this node or any
    // descendant matches. Marks IsMatch on the node's own hit and expands branches
    // that hold a match below them, so every hit is revealed while the rest of the
    // tree stays put. A blank query clears every mark and collapses the subtree.
    public bool ApplySearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            foreach (HelpNodeViewModel c in Children) c.ApplySearch(query);
            IsMatch = false;
            IsExpanded = false;
            return false;
        }

        string q = query.Trim();
        bool anyChild = false;
        foreach (HelpNodeViewModel c in Children)
            anyChild |= c.ApplySearch(q);

        IsMatch = SelfMatches(q);
        IsExpanded = anyChild;
        return IsMatch || anyChild;
    }

    // How many nodes in this subtree (this one included) match the last search.
    public int MatchCount() => (IsMatch ? 1 : 0) + Children.Sum(c => c.MatchCount());

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

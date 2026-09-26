using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Services;

namespace MudPlay.ViewModels.Help;

// View model for the Help window — a searchable compendium of how features work,
// how to use the client, and what each setting means. Loads the bundled markdown
// guide (Assets/Help/guide.md) into a TOC tree; the left tree drives which topic
// the right content pane renders. The search box highlights — never hides — the
// topics whose title or body holds the text, opens the branches they sit in, and
// highlights every occurrence in the open topic's body.
public sealed partial class HelpWindowViewModel : ObservableObject
{
    public IReadOnlyList<HelpNodeViewModel> Topics { get; }

    // The tree's selected node; its Body feeds the content pane.
    [ObservableProperty] private HelpNodeViewModel? _selectedTopic;

    // Live search — highlights matching topics and body text as the user types.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _searchText = string.Empty;

    public string StatusText =>
        Topics.Count == 0 ? "Help content unavailable"
        : string.IsNullOrWhiteSpace(SearchText) ? $"{TopicCount(Topics):N0} topics"
        : MatchSummary(Topics.Sum(t => t.MatchCount()));

    private static string MatchSummary(int n) =>
        n == 0 ? "No topics match" : $"{n:N0} matching topic{(n == 1 ? "" : "s")}";

    // Production ctor — reads the embedded guide.
    public HelpWindowViewModel() : this(HelpBook.LoadBundled()) { }

    // Testable ctor — takes a parsed book directly.
    public HelpWindowViewModel(IReadOnlyList<HelpTopic> topics)
    {
        Topics = topics.Select(t => new HelpNodeViewModel(t)).ToList();
        SelectedTopic = Topics.FirstOrDefault();   // open on the overview
    }

    partial void OnSearchTextChanged(string value)
    {
        foreach (HelpNodeViewModel t in Topics)
            t.ApplySearch(value);

        // Clearing the search resets every branch to collapsed; re-open the path
        // to the selected topic so its tree selection stays visible instead of
        // hiding inside a now-collapsed parent (the content pane already keeps
        // showing it). While searching, the selected topic's branch is opened too,
        // so the topic being read never folds away beside the hits.
        if (SelectedTopic is { } selected)
            foreach (HelpNodeViewModel t in Topics)
                t.ExpandToReveal(selected);
    }

    private static int TopicCount(IReadOnlyList<HelpNodeViewModel> nodes) =>
        nodes.Sum(n => 1 + TopicCount(n.Children));
}

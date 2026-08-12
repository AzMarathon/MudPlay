using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Services;

namespace MudPlay.ViewModels.Help;

// View model for the Help window — a searchable compendium of how features work,
// how to use the client, and what each setting means. Loads the bundled markdown
// guide (Assets/Help/guide.md) into a TOC tree; the left tree drives which topic
// the right content pane renders. The search box filters the tree live by topic
// title AND body text.
public sealed partial class HelpWindowViewModel : ObservableObject
{
    public IReadOnlyList<HelpNodeViewModel> Topics { get; }

    // The tree's selected node; its Body feeds the content pane.
    [ObservableProperty] private HelpNodeViewModel? _selectedTopic;

    // Live search — filters the TOC as the user types.
    [ObservableProperty] private string _searchText = string.Empty;

    public string StatusText =>
        Topics.Count == 0 ? "Help content unavailable" : $"{TopicCount(Topics):N0} topics";

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
            t.ApplyFilter(value);
    }

    private static int TopicCount(IReadOnlyList<HelpNodeViewModel> nodes) =>
        nodes.Sum(n => 1 + TopicCount(n.Children));
}

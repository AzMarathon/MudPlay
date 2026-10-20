using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace MudPlay.Tests;

// A binding in a Conversation row that reaches past the row to the window's view-model
// leaves a weak subscription on the view-model each time the list realizes the row,
// and the view-model raises too few changes for Avalonia to sweep the dead ones. With a
// busy gossip channel that was over a thousand a second, a steady climb in memory for as
// long as the window stayed open. So every binding in the row template reads the row.
public sealed class ConversationRowTemplateTests
{
    [Fact]
    public void EveryRowBinding_ReadsTheRowItself()
    {
        string xaml = Path.Combine(RepoRoot(), "Views", "ConversationWindow.axaml");
        XElement rows = XDocument.Load(xaml).Descendants()
            .Single(e => e.Name.LocalName == "ListBox"
                         && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "RowsList"));
        XElement template = rows.Elements().Single(e => e.Name.LocalName == "ListBox.ItemTemplate");

        List<string> bindings = template.Descendants()
            .SelectMany(e => e.Attributes().Select(a => $"{e.Name.LocalName}.{a.Name.LocalName}={a.Value}"))
            .Where(b => b.Contains("{Binding", StringComparison.Ordinal))
            .ToList();
        List<string> reachingOut = bindings
            .Where(b => b.Contains("ElementName", StringComparison.Ordinal)
                        || b.Contains("RelativeSource", StringComparison.Ordinal)
                        || b.Contains("$parent", StringComparison.Ordinal)
                        || b.Contains("{Binding #", StringComparison.Ordinal)
                        || b.Contains("DataContext.", StringComparison.Ordinal))
            .ToList();

        Assert.True(bindings.Count > 10, $"found only {bindings.Count} bindings in the row template");
        Assert.True(reachingOut.Count == 0,
            "These row bindings read something other than the row:\n  " + string.Join("\n  ", reachingOut));
    }

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(here)!)!;
}

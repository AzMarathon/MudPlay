using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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

        // Every binding, whatever its spelling: the markup-extension forms
        // ({Binding}, {CompiledBinding}, {ReflectionBinding}, prefixed or not) on any
        // attribute, and the element forms (<Binding/>, <MultiBinding>…) with their
        // attributes run together.
        List<string> bindings = new();
        foreach (XElement e in template.Descendants())
        {
            if (BindingElement.IsMatch(e.Name.LocalName))
            {
                bindings.Add($"<{e.Name.LocalName} " + string.Join(" ", e.Attributes().Select(a => $"{a.Name.LocalName}={a.Value}")) + ">");
                continue;
            }
            foreach (XAttribute a in e.Attributes())
                if (BindingExtension.IsMatch(a.Value)) bindings.Add($"{e.Name.LocalName}.{a.Name.LocalName}={a.Value}");
        }
        List<string> reachingOut = bindings.Where(b => ReachesOut.IsMatch(b)).ToList();

        Assert.True(bindings.Count > 10, $"found only {bindings.Count} bindings in the row template");
        Assert.True(reachingOut.Count == 0,
            "These row bindings read something other than the row:\n  " + string.Join("\n  ", reachingOut));
    }

    private static readonly Regex BindingExtension = new(@"\{\s*(\w+:)?(Compiled|Reflection)?Binding\b");
    private static readonly Regex BindingElement = new(@"^(Compiled|Reflection|Multi)?Binding$");
    private static readonly Regex ReachesOut = new(@"ElementName|RelativeSource|\$parent|#\w|DataContext\.");

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(here)!)!;
}

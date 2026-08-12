namespace MudPlay.Services;

// One node in the Help compendium's table of contents: a heading, the markdown
// body under it (up to the next heading), and any nested sub-topics. Produced by
// HelpBook.Parse from the bundled guide; the Help window wraps each in a view
// model for the TOC tree + content pane. A leaf topic has an empty Children list.
public sealed record HelpTopic(string Title, string Body, IReadOnlyList<HelpTopic> Children);

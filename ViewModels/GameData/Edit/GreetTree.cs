using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels.GameData.Edit;

// A monster's greet textblock as the same collapsible tree a room spell's conditional
// effects use: each top-level node is a player keyword, its children the effects that
// fire when it's asked, nested as the textblock chains. Keywords start collapsed so a
// block with dozens of them stays compact on the tab; a keyword that teleports is
// tinted and labelled, since asking it moves you — the thing a player scanning the
// list wants to spot.
public sealed partial class GreetTree
{
    public IReadOnlyList<SpellEffectNode> Keywords { get; }

    public GreetTree(IReadOnlyList<SpellEffectNode> keywords) => Keywords = keywords;

    [RelayCommand] private void ExpandAll() => SpellEffectNode.SetExpanded(Keywords, true);
    [RelayCommand] private void CollapseAll() => SpellEffectNode.SetExpanded(Keywords, false);
}

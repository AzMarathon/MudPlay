using System;
using System.Collections.Generic;

namespace MudPlay.ViewModels.GameData.Edit;

// One node in a room spell's decoded effect tree (see SpellEffectTreeDecoder). A
// branch node is a condition header (level gate + carry check) that tints its border
// and holds child outcome nodes; an outcome node carries a weighted percentage and its
// effect as mixed text/link runs (summon → monster record, teleport → map room, cast →
// spell record). Deep or huge sub-trees collapse to a single summary node. Rendered as
// an expandable tree in the spell record's Game Data tab.
public sealed class SpellEffectNode
{
    // Weighted chance of this outcome under its parent random block; null on a branch
    // header (a condition, not a roll).
    public int? Percent { get; init; }

    // The line content — plain text with any embedded record/room links.
    public IReadOnlyList<MdbInline> Runs { get; init; } = Array.Empty<MdbInline>();

    public IReadOnlyList<SpellEffectNode> Children { get; init; } = Array.Empty<SpellEffectNode>();

    // "danger" (no counter carried), "accent" (safe / countered), or "neutral".
    public string Tone { get; init; } = "neutral";

    // A condition header (bordered + tinted) vs a plain outcome/effect row.
    public bool IsBranch { get; init; }

    // Branches start expanded; deep nested rolls start collapsed so the tree opens tidy.
    public bool StartExpanded { get; init; } = true;

    public bool HasChildren => Children.Count > 0;
    public bool HasPercent => Percent is not null;
    public string PercentText => Percent is { } p ? $"{p}%" : string.Empty;

    // Tone → the CSS-ish brush keys the view maps to (border + percent colour).
    public bool IsDanger => Tone == "danger";
    public bool IsAccent => Tone == "accent";
}

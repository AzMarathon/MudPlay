using System.Collections.Generic;
using System.Globalization;
using Avalonia.Threading;
using FujinTerm.Game.Quests;
using FujinTerm.Services;

namespace FujinTerm.ViewModels.GameData.Tables;

// Game Data Browser → Quest Flags tab. A computed view over QuestFlagIndex: every quest-flag
// reference in the active set's TBInfo — the flag, how the block relates to it (grants / gates
// / advances / clears), and the NPC / room / spell that reaches it, resolved via the block's
// Called-From provenance. Not backed by a JSON table (no such table exists); it recomputes from
// the loaded set and rebuilds on a set swap, like the engine-backed tabs.
public sealed class QuestFlagsSectionViewModel : GameDataTableSectionViewModel
{
    private readonly QuestFlagIndex _index;
    private readonly GameDataCache _cache;
    private readonly Action<string?> _activeSetHandler;

    public QuestFlagsSectionViewModel(QuestFlagIndex index, GameDataCache cache)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(cache);
        _index = index;
        _cache = cache;
        _activeSetHandler = _ => { if (IsLoaded) Reload(); };
        _cache.ActiveSetChanged += _activeSetHandler;
    }

    public override string Id => "questflags";
    public override string Title => "Quest Flags";

    // Derived from the MDB data, so it belongs with the tables; single-tier, so no "Use" badge.
    public override bool ShowInTableGroup => true;
    public override bool ShowUseColumn => false;

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "Flag", "Name", "Relationship", "Kind", "Source", "Location", "Value",
    };

    public override string SearchKeyColumn => "Name";

    public override IEnumerable<string> SearchableLabels => new[]
    {
        Title, "quest", "flag", "ability", "giveability", "checkability", "testability",
    };

    public override string? BannerText =>
        "Every quest-flag reference in this set's TBInfo — what grants / gates / advances / " +
        "clears each flag, resolved to its NPC, room, or spell.";

    // Lazy first-load, deferred a tick so the DataGrid columns exist before rows arrive
    // (mirrors JsonTableSectionViewModel.OnActivated).
    public override void OnActivated()
    {
        if (IsLoaded) return;
        Dispatcher.UIThread.Post(() => _ = LoadAsync());
    }

    protected override void PopulateRows(IList<GameDataRow> rows)
    {
        foreach (QuestFlagRef r in _index.Entries)
        {
            Dictionary<string, string?> cells = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Flag"]         = r.Flag.ToString(CultureInfo.InvariantCulture),
                ["Name"]         = r.FlagName,
                ["Relationship"] = RelationLabel(r.Relation),
                ["Kind"]         = r.SourceKind.ToString(),
                ["Source"]       = r.SourceName,
                ["Location"]     = r.Map > 0 ? $"{r.Map}/{r.Room}" : string.Empty,
                ["Value"]        = r.Value != 0 ? r.Value.ToString(CultureInfo.InvariantCulture) : string.Empty,
            };
            rows.Add(GameDataRow.FromDictionary(cells, Columns));
        }
    }

    private static string RelationLabel(QuestFlagRelation rel) => rel switch
    {
        QuestFlagRelation.Grants   => "Grants",
        QuestFlagRelation.Advances => "Advances",
        QuestFlagRelation.Requires => "Requires",
        QuestFlagRelation.Tests    => "Tests",
        QuestFlagRelation.Gate     => "Gate (must not have)",
        QuestFlagRelation.Clears   => "Clears",
        _                          => rel.ToString(),
    };

    public override void Dispose()
    {
        _cache.ActiveSetChanged -= _activeSetHandler;
        base.Dispose();
    }
}

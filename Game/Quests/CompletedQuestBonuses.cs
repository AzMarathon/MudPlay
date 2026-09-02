using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MudPlay.Models.Profile;
using MudPlay.Services;

namespace MudPlay.Game.Quests;

// The union of every completed quest's class-resolved permanent stat bonuses (quests
// stack, so no dedup). Mirrors QuestSectionViewModel.PublishBonuses but works straight
// off a profile's quest log + a fresh crawl, so surfaces OUTSIDE the Character Workshop
// (Monster Intel) can fold quest rewards into their own stat math without the Quest tab
// being open to publish them. The crawl is skipped entirely when nothing is completed,
// so the common case costs nothing.
public static class CompletedQuestBonuses
{
    // classId is the character's Classes-table Number (null when unresolved). questLog
    // is the per-character quest progress (CharacterProfile.QuestLog).
    public static IReadOnlyList<QuestBonus> Resolve(
        GameDataCache gameData, int? classId, IReadOnlyList<QuestProgress>? questLog)
    {
        if (questLog is null) return System.Array.Empty<QuestBonus>();
        List<QuestProgress> done = questLog.Where(p => p.Complete).ToList();
        if (done.Count == 0) return System.Array.Empty<QuestBonus>();

        var byKey = new Dictionary<(int Flag, int Step), IReadOnlyList<QuestBonus>>();
        foreach (CrawledQuest q in QuestCrawler.Crawl(gameData, classId))
            byKey[(q.Flag, q.Step)] = q.Bonuses;

        var bonuses = new List<QuestBonus>();
        foreach (QuestProgress p in done)
            if (byKey.TryGetValue((p.Flag, p.Step), out IReadOnlyList<QuestBonus>? b))
                bonuses.AddRange(b);
        return bonuses;
    }

    // Resolve a character's class id from the Classes table by name (mirrors
    // QuestEligibility.ResolveId / QuestSectionViewModel.ResolveClassId).
    public static int? ResolveClassId(GameDataCache gameData, string? className)
    {
        if (string.IsNullOrEmpty(className)) return null;
        if (gameData.FindRowByName("Classes", className) is not JsonElement row
            || row.ValueKind != JsonValueKind.Object) return null;
        return row.TryGetProperty("Number", out JsonElement v)
            && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) && n > 0
                ? n : null;
    }
}

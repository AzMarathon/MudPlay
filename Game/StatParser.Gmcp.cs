using System.Text.Json;

namespace MudPlay.Game;

// The server's vitals and stats (Anomaly.Char.Vitals / Anomaly.Char.Stats) written
// to the fields the stat screen's read writes.
public sealed partial class StatParser
{
    private static readonly (string Key, StatSet Flag)[] ServerStats =
    [
        ("strength", StatSet.Strength), ("intellect", StatSet.Intellect), ("willpower", StatSet.Willpower),
        ("agility", StatSet.Agility), ("health", StatSet.Health), ("charm", StatSet.Charm),
    ];

    public void ApplyGmcpVitals(JsonElement value)
    {
        Stats.Hits = value.GetProperty("hp").GetInt32();
        Stats.MaxHits = value.GetProperty("maxhp").GetInt32();
        bool kai = value.GetProperty("manaType").GetString() == "kai";
        int mana = value.GetProperty("mana").GetInt32();
        int max = value.GetProperty("maxmana").GetInt32();
        Stats.Mana = kai ? 0 : mana;
        Stats.MaxMana = kai ? 0 : max;
        Stats.Kai = kai ? mana : 0;
        Stats.MaxKai = kai ? max : 0;
        int level = value.GetProperty("level").GetInt32();
        // A new level's span isn't known until the exp screen gives it.
        if (Stats.Level != level)
        {
            Stats.LevelExpSpan = 0;
            Stats.LevelPercent = 0;
        }
        Stats.Level = level;
        Stats.Exp = value.GetProperty("exp").GetInt64();
        Stats.ExpToNext = Math.Max(0, value.GetProperty("expNeeded").GetInt64() - Stats.Exp);
        HasParsed = true;
    }

    public void ApplyGmcpStats(JsonElement value)
    {
        JsonElement stats = value.GetProperty("stats");
        int Current(string key) => stats.GetProperty(key).GetProperty("current").GetInt32();
        Stats.Strength = Current("strength");
        Stats.Intellect = Current("intellect");
        Stats.Willpower = Current("willpower");
        Stats.Agility = Current("agility");
        Stats.Health = Current("health");
        Stats.Charm = Current("charm");
        // A stat off its base is the one the stat screen would mark modified.
        Stats.ModifiedStats = StatSet.None;
        foreach ((string key, StatSet flag) in ServerStats)
            if (stats.GetProperty(key).GetProperty("base").GetInt32() != Current(key)) Stats.ModifiedStats |= flag;
        Stats.ModifiedMarksRead = true;
        Stats.ArmourClass = value.GetProperty("ac").GetProperty("shown").GetInt32();
        Stats.MaxArmourClass = value.GetProperty("dr").GetProperty("shown").GetInt32();
        Stats.Perception = value.GetProperty("perception").GetInt32();
        Stats.Stealth = value.GetProperty("stealth").GetInt32();
        Stats.Thievery = value.GetProperty("thievery").GetInt32();
        Stats.Traps = value.GetProperty("traps").GetInt32();
        Stats.Picklocks = value.GetProperty("picklocks").GetInt32();
        Stats.Tracking = value.GetProperty("tracking").GetInt32();
        Stats.MartialArts = value.GetProperty("martialArts").GetInt32();
        Stats.MagicRes = value.GetProperty("magicResist").GetInt32();
        Stats.Spellcasting = value.GetProperty("spellcasting").GetInt32();
        HasParsed = true;
    }
}

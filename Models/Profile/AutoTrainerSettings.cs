using System.Collections.Generic;

namespace FujinTerm.Models.Profile;

// Per-character Auto-Trainer settings — the "AutoTrainer" entry in
// CharacterProfile.Settings. Surfaced by the Settings → Auto-Trainer tab.
public sealed class AutoTrainerSettings
{
    // Auto-level toggle: when running a loop / auto-lair and a level-up is
    // available, detour to the appropriate trainer and train the level(s).
    public bool AutoTrain { get; set; }

    // Auto-apply-CP toggle — INDEPENDENT of AutoTrain (decoupled): whenever a train
    // flow runs, drive the train-stats screen to apply the CP plan's row. On with
    // AutoTrain off simply means no auto-trigger fires it, so it then only affects
    // the manual Train-now / @train / "Apply this level" paths. Still requires a
    // saved CP plan to have anything to apply.
    public bool AutoTrainStats { get; set; }

    // Buffer of trainable-but-untrained levels to always keep banked. Auto-train
    // (and the manual Train Now) stops once this many levels are still reachable
    // from banked exp, so the character always carries a reserve. 0 (the
    // default) trains every banked level.
    public int LevelsToKeep { get; set; }

    // Level ceiling: auto-train will train UP TO this level but never above it
    // (reach N, then stop) — a guard against over-levelling / "OP"-ing a character
    // once the exp + level-buffer gates would otherwise keep training. 0 (the
    // default) = no ceiling.
    public int DoNotTrainAbove { get; set; }

    // When on, broadcast "I can now train to level: N" on AnnounceChannel each
    // time a live experience gain makes a new level trainable — i.e. a
    // Level-Projection row's "Exp to level" reaches 0. Banked levels already
    // trainable on login / at the next stat poll are seeded silently, so this
    // only fires on transitions that happen during play.
    public bool AnnounceLevelUps { get; set; }

    // Chat channel the level-up announce is sent on. Defaults to Gangpath
    // (most useful to a party).
    public AnnounceChannel AnnounceChannel { get; set; } = AnnounceChannel.Gangpath;

    // Trainer rows the user has switched OFF for auto-train, keyed by
    // Game.GameData.TrainerShop.RowKey (shop/map/room) so a multi-room shop's
    // rooms toggle independently. Storing the disabled set (rather than the
    // allowed set) keeps the JSON small and means newly-discovered trainers
    // default to allowed. null / empty = every discovered trainer is allowed.
    public List<string>? DisabledTrainers { get; set; }
}

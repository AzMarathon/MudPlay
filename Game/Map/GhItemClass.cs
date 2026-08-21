namespace MudPlay.Game.Map;

// The item-category facets a Roomba GH room label matches against, resolved from
// the MDB Items table. WeaponType / ArmourType / Worn are int? for shape, but in
// practice are never null for a resolved item — ItemNameStore's indices default a
// missing column to 0, so GhItemClassifier.Classify always populates all four.
// Callers still gate on ItemType (Weapon / Armour) before reading a subtype, so a
// subtype is never compared cross-category. See GhItemClassifier.
public readonly record struct GhItemClass(int ItemType, int? WeaponType, int? ArmourType, int? Worn);

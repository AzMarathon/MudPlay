using System.IO;
using MudPlay.Game.Map;
using MudPlay.Models.Profile;
using MudPlay.Services;
using Xunit;

namespace MudPlay.Tests;

public sealed class GhItemClassifierTests : IDisposable
{
    private readonly string _root;

    public GhItemClassifierTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-ghclassifier-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ItemType 1=Weapon (WeaponType 0=1H Blunt, 2=1H Sharp), 0=Armour
    // (ArmourType 7=Chainmail, or 4/6 — both display "Leather" alongside 3),
    // 4=Food (no subtype). Worn 8=Neck, 4/13=Finger (same displayed slot),
    // 14/17=Wrist (same displayed slot), 12=Off-Hand.
    private const string ItemsJson = """
        [
          { "Number": 1, "Name": "war hammer",   "ItemType": 1, "WeaponType": 0 },
          { "Number": 2, "Name": "long sword",   "ItemType": 1, "WeaponType": 2 },
          { "Number": 3, "Name": "chain shirt",  "ItemType": 0, "ArmourType": 7 },
          { "Number": 4, "Name": "ration",       "ItemType": 4 },
          { "Number": 5, "Name": "leather cap",  "ItemType": 0, "ArmourType": 3, "Worn": 2 },
          { "Number": 6, "Name": "hide vest",    "ItemType": 0, "ArmourType": 4, "Worn": 11 },
          { "Number": 7, "Name": "gold ring",    "ItemType": 0, "Worn": 13 },
          { "Number": 8, "Name": "silver band",  "ItemType": 0, "Worn": 4 },
          { "Number": 9, "Name": "iron bracer",  "ItemType": 0, "Worn": 17 },
          { "Number": 10, "Name": "copper bangle", "ItemType": 0, "Worn": 14 },
          { "Number": 11, "Name": "wooden shield", "ItemType": 0, "Worn": 12 },
          { "Number": 12, "Name": "off-hand dagger", "ItemType": 1, "WeaponType": 2, "Worn": 12 }
        ]
        """;

    private ItemNameStore NewStore()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Items.json"), ItemsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        ItemNameStore store = new(cache);
        store.OnActiveSetChanged("alpha");
        return store;
    }

    [Fact]
    public void Classify_ResolvesItemTypeAndWeaponType()
    {
        ItemNameStore s = NewStore();
        GhItemClass? cls = GhItemClassifier.Classify(s, "a war hammer");
        Assert.Equal(new GhItemClass(1, 0, 0, 0), cls);
    }

    [Fact]
    public void Classify_UnknownEntry_ReturnsNull()
    {
        ItemNameStore s = NewStore();
        Assert.Null(GhItemClassifier.Classify(s, "a mysterious orb"));
    }

    private static GhRoomLabel Label(int map, int room, params GhCategoryRule[] rules) =>
        new(map, room) { Rules = new(rules) };

    [Fact]
    public void Matches_BroadWeaponRule_AdmitsAnyWeaponSubtype()
    {
        GhCategoryRule rule = GhCategoryRule.ForItemType(1);   // "Weapons", no subtype
        Assert.True(GhItemClassifier.Matches(rule, new GhItemClass(1, 0, 0, 0)));
        Assert.True(GhItemClassifier.Matches(rule, new GhItemClass(1, 2, 0, 0)));
        Assert.False(GhItemClassifier.Matches(rule, new GhItemClass(0, 0, 7, 0)));
    }

    [Fact]
    public void Matches_NarrowedWeaponRule_RequiresExactSubtype()
    {
        GhCategoryRule rule = GhCategoryRule.ForItemType(1, weaponType: 0);   // "Weapons > 1H Blunt"
        Assert.True(GhItemClassifier.Matches(rule, new GhItemClass(1, 0, 0, 0)));    // war hammer
        Assert.False(GhItemClassifier.Matches(rule, new GhItemClass(1, 2, 0, 0)));   // long sword — wrong subtype
    }

    [Fact]
    public void Matches_ArmourRule_IgnoresWeaponTypeField()
    {
        GhCategoryRule rule = GhCategoryRule.ForItemType(0, armourType: 7);   // "Armour > Chainmail"
        Assert.True(GhItemClassifier.Matches(rule, new GhItemClass(0, null, 7, null)));
        Assert.False(GhItemClassifier.Matches(rule, new GhItemClass(0, null, 1, null)));
    }

    // ArmourType 3/4/5/6 all display as "Leather" (LookupEnums) — a rule
    // pinned to code 3 must still admit an item carrying code 4.
    [Fact]
    public void Matches_ArmourRule_CollapsesLeatherCodeVariants()
    {
        ItemNameStore s = NewStore();
        GhCategoryRule leatherRule = GhCategoryRule.ForItemType(0, armourType: 3);   // "Armour > Leather"

        GhItemClass? cap = GhItemClassifier.Classify(s, "a leather cap");    // ArmourType 3
        GhItemClass? vest = GhItemClassifier.Classify(s, "a hide vest");     // ArmourType 4 — same displayed "Leather"

        Assert.True(GhItemClassifier.Matches(leatherRule, cap!.Value));
        Assert.True(GhItemClassifier.Matches(leatherRule, vest!.Value));
    }

    // Worn 4/13 both display "Finger"; 14/17 both display "Wrist" — a rule
    // pinned to one code must admit an item using the other.
    [Fact]
    public void Matches_WornSlotRule_CollapsesDuplicateCodesForSameSlot()
    {
        ItemNameStore s = NewStore();
        GhCategoryRule fingerRule = GhCategoryRule.ForWornSlot(4);
        GhCategoryRule wristRule = GhCategoryRule.ForWornSlot(14);

        GhItemClass? ring = GhItemClassifier.Classify(s, "a gold ring");       // Worn 13
        GhItemClass? band = GhItemClassifier.Classify(s, "a silver band");    // Worn 4
        GhItemClass? bracer = GhItemClassifier.Classify(s, "an iron bracer"); // Worn 17
        GhItemClass? bangle = GhItemClassifier.Classify(s, "a copper bangle"); // Worn 14

        Assert.True(GhItemClassifier.Matches(fingerRule, ring!.Value));
        Assert.True(GhItemClassifier.Matches(fingerRule, band!.Value));
        Assert.True(GhItemClassifier.Matches(wristRule, bracer!.Value));
        Assert.True(GhItemClassifier.Matches(wristRule, bangle!.Value));
    }

    // A Worn-slot rule matches ANY ItemType at that slot — a shield (armour)
    // and an off-hand dagger (weapon) both worn Off-Hand both match.
    [Fact]
    public void Matches_WornSlotRule_IgnoresItemTypeEntirely()
    {
        ItemNameStore s = NewStore();
        GhCategoryRule offHandRule = GhCategoryRule.ForWornSlot(12);

        GhItemClass? shield = GhItemClassifier.Classify(s, "a wooden shield");     // Armour
        GhItemClass? dagger = GhItemClassifier.Classify(s, "an off-hand dagger");  // Weapon

        Assert.True(GhItemClassifier.Matches(offHandRule, shield!.Value));
        Assert.True(GhItemClassifier.Matches(offHandRule, dagger!.Value));
    }

    [Fact]
    public void MatchesAny_MultipleRulesOnOneRoom_AnyOneAdmits()
    {
        // "Chain Scale" room: Chainmail OR Scalemail.
        GhRoomLabel room = Label(1, 1,
            GhCategoryRule.ForItemType(0, armourType: 7),   // Chainmail
            GhCategoryRule.ForItemType(0, armourType: 8));  // Scalemail

        Assert.True(GhItemClassifier.MatchesAny(room, new GhItemClass(0, null, 7, null)));
        Assert.True(GhItemClassifier.MatchesAny(room, new GhItemClass(0, null, 8, null)));
        Assert.False(GhItemClassifier.MatchesAny(room, new GhItemClass(0, null, 9, null)));   // Platemail
    }

    [Fact]
    public void MatchesAny_MixedWornAndItemTypeRulesOnOneRoom()
    {
        // "Necks Bracers" room: worn at Neck OR worn at Arms.
        GhRoomLabel room = Label(1, 1,
            GhCategoryRule.ForWornSlot(8),   // Neck
            GhCategoryRule.ForWornSlot(6));  // Arms

        Assert.True(GhItemClassifier.MatchesAny(room, new GhItemClass(0, null, null, 8)));
        Assert.True(GhItemClassifier.MatchesAny(room, new GhItemClass(0, null, null, 6)));
        Assert.False(GhItemClassifier.MatchesAny(room, new GhItemClass(0, null, null, 9)));   // Legs
    }
}

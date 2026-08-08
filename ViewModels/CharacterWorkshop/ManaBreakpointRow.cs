namespace FujinTerm.ViewModels.CharacterWorkshop;

// One reachable tick step in the Mana Regen calculator's breakpoint ladder: the
// tick it yields (Tick), the +ManaRgn% it costs (Cost), the roll value that
// reaches it (Roll), where that sits in the spell's range (RangePct), and whether
// it's the recommended reroll target (IsRecommended — highlighted in the UI).
public readonly record struct ManaBreakpointRow(
    string Tick, string Cost, string Roll, string RangePct, bool IsRecommended);

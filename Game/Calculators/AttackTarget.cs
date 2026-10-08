namespace MudPlay.Game.Calculators;

// What an attack is rolled against: the defender's armour class, the damage resist
// taken off each hit, its raw dodge, and the backstab defence only a stab meets.
public readonly record struct AttackTarget(int ArmourClass, int DamageResist, int Dodge, int BsDefense);

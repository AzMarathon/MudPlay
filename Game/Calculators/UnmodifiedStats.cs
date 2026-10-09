using System.Text;

namespace MudPlay.Game.Calculators;

// The trained stats worked back from a `stat` screen, with what was taken off to
// get there. Arrays are STR/INT/WIL/AGL/HEA/CHM. Base is the shown value less
// Equipment and Effects, which hold only what was actually taken off. For a stat
// in Unexplained nothing came off, and the figure is not to be trusted.
public sealed record UnmodifiedStats(
    StatReadingState State,
    int[] Base,
    int[] Equipment,
    int[] Effects,
    StatSet Modified,
    StatSet Unexplained,
    IReadOnlyList<string> EffectNotes)
{
    private static readonly string[] Labels = { "STR", "INT", "WIL", "AGL", "HEA", "CHM" };

    public static readonly UnmodifiedStats None = new(
        StatReadingState.Unverified, new int[6], new int[6], new int[6],
        StatSet.None, StatSet.None, Array.Empty<string>());

    // "STR, AGL" for a set of stats.
    public static string Names(StatSet stats)
    {
        StringBuilder sb = new();
        for (int i = 0; i < Labels.Length; i++)
        {
            if (((int)stats & (1 << i)) == 0) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(Labels[i]);
        }
        return sb.ToString();
    }

    // Whether the screen's marks are all down to worn gear, which is how a
    // character with stat gear always reads on Paradigm. Nothing worth a notice.
    public bool GearAlone
    {
        get
        {
            if (State != StatReadingState.Accounted || Modified == StatSet.None) return false;
            foreach (int e in Effects) if (e != 0) return false;
            return true;
        }
    }

    // One line for the program log, the CP Allocation tab and a bug report: what the
    // screen marked, what came off for it, and what couldn't be explained. Null when
    // the screen marked nothing, or nothing but worn gear.
    public string? Describe()
    {
        if (State == StatReadingState.Unverified || Modified == StatSet.None || GearAlone) return null;
        StringBuilder sb = new();
        sb.Append("`stat` marked ").Append(Names(Modified)).Append(" as modified");
        string gear = Offsets(Equipment, Modified);
        if (gear.Length > 0) sb.Append("; taken off for worn gear: ").Append(gear);
        string taken = Offsets(Effects, Modified);
        if (taken.Length > 0) sb.Append("; taken off for effects: ").Append(taken);
        if (EffectNotes.Count > 0) sb.Append(" (").Append(string.Join("; ", EffectNotes)).Append(')');
        if (Unexplained != StatSet.None)
            sb.Append("; ").Append(Names(Unexplained)).Append(" can't be accounted for");
        return sb.Append('.').ToString();
    }

    private static string Offsets(int[] values, StatSet among)
    {
        StringBuilder sb = new();
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] == 0 || ((int)among & (1 << i)) == 0) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(Labels[i]).Append(' ').Append(values[i] > 0 ? "+" : string.Empty).Append(values[i]);
        }
        return sb.ToString();
    }
}

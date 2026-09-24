using System.Globalization;
using System.Text;

namespace MudPlay.Game.Train;

// Where a party member stands on the next party-train trip, as it reports itself.
//   Waiting — its own auto-train settings don't want a trip yet (not enough levels
//             banked past the reserve).
//   Ready   — its settings would fire a trip now. Paying for it is the trip's
//             funding plan's problem, not the member's readiness.
//   Blocked — nothing it could train in party mode: at its level ceiling, or the
//             only next step is the solo level-11 trainer. Never waited for.
public enum PartyTrainReadiness { Waiting, Ready, Blocked }

// One member's party-train report, sent to the leader as `@ptrain st <payload>`.
// The member computes every figure from its OWN client (its settings, purse, bank
// and earn rate) so the leader never guesses how party exp or money works — it
// only aggregates what each member says about itself.
//
// Copper figures are copper farthings (the unit the game prices training in).
// Spare is what the member can hand to others and still pay for its own train
// and keep its keep-on-hand floor. Bank/BankName is its largest single deposit —
// a withdraw only works at a branch you hold money at, so the leader needs the
// branch, not just a total. EtaSeconds is the projected time until it's Ready at
// its session earn rate, -1 when unknown. Exp is its running exp total and NextExp
// the total that reaches its next not-yet-reached level (0 = unknown) — the leader
// times that gap at its OWN rate, since a party shares the kills.
public readonly record struct PartyTrainStatus(
    PartyTrainReadiness Readiness,
    int Level,
    int ClassNumber,
    int LevelsToTrain,
    long CostCopper,
    long CashCopper,
    long SpareCopper,
    long BankCopper,
    string? BankName,
    int EtaSeconds,
    long Exp = 0,
    long NextExp = 0)
{
    // Compact key=value payload. `bk` goes last because a bank name has spaces in it
    // ("Bank of Godfrey") and is read as the rest of the line.
    public string Encode()
    {
        StringBuilder sb = new();
        sb.Append("s=").Append(Readiness switch
        {
            PartyTrainReadiness.Ready   => 'r',
            PartyTrainReadiness.Blocked => 'b',
            _                           => 'w',
        });
        Append(sb, "l", Level);
        Append(sb, "c", ClassNumber);
        Append(sb, "n", LevelsToTrain);
        Append(sb, "cost", CostCopper);
        Append(sb, "cash", CashCopper);
        Append(sb, "spare", SpareCopper);
        Append(sb, "bank", BankCopper);
        Append(sb, "eta", EtaSeconds);
        Append(sb, "x", Exp);
        Append(sb, "nx", NextExp);
        if (!string.IsNullOrWhiteSpace(BankName)) sb.Append(" bk=").Append(BankName.Trim());
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string key, long value) =>
        sb.Append(' ').Append(key).Append('=').Append(value.ToString(CultureInfo.InvariantCulture));

    // Parses what Encode wrote. Unknown keys are ignored so a newer client's extra
    // field doesn't make an older leader drop the whole report; a missing readiness
    // or level does, since the quorum can't place a member without them.
    public static bool TryDecode(string payload, out PartyTrainStatus status)
    {
        status = default;
        if (string.IsNullOrWhiteSpace(payload)) return false;

        string text = payload.Trim();
        string? bankName = null;
        int bk = text.IndexOf(" bk=", System.StringComparison.Ordinal);
        if (bk >= 0)
        {
            bankName = text[(bk + 4)..].Trim();
            text = text[..bk];
        }

        PartyTrainReadiness? readiness = null;
        int level = 0, cls = 0, levels = 0, eta = -1;
        long cost = 0, cash = 0, spare = 0, bank = 0, exp = 0, nextExp = 0;
        bool haveLevel = false;
        foreach (string token in text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            if (eq <= 0) continue;
            string key = token[..eq];
            string value = token[(eq + 1)..];
            switch (key)
            {
                case "s":
                    readiness = value switch
                    {
                        "r" => PartyTrainReadiness.Ready,
                        "b" => PartyTrainReadiness.Blocked,
                        "w" => PartyTrainReadiness.Waiting,
                        _   => null,
                    };
                    break;
                case "l":     haveLevel = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out level); break;
                case "c":     int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out cls); break;
                case "n":     int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out levels); break;
                case "cost":  long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out cost); break;
                case "cash":  long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out cash); break;
                case "spare": long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out spare); break;
                case "bank":  long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out bank); break;
                case "eta":   int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out eta); break;
                case "x":     long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out exp); break;
                case "nx":    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out nextExp); break;
            }
        }
        if (readiness is not { } r || !haveLevel || level <= 0) return false;

        status = new PartyTrainStatus(r, level, cls, levels, cost, cash, spare, bank,
            string.IsNullOrWhiteSpace(bankName) ? null : bankName, eta, exp, nextExp);
        return true;
    }
}

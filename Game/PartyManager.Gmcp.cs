using System.Text.Json;

namespace MudPlay.Game;

public sealed partial class PartyManager
{
    public Func<bool>? GmcpOwnsGroup { get; set; }

    public void ApplyGmcpGroup(JsonElement value)
    {
        bool wasFollowing = State.IsInParty && !State.SelfIsLeader;
        string? previousLeader = State.LeaderName;
        string? leader = value.GetProperty("leader").GetString();
        JsonElement rows = value.GetProperty("members");
        bool inParty = rows.GetArrayLength() > 0 || leader is not null;
        State.IsInParty = inParty;
        State.LeaderName = leader;
        State.SelfIsLeader = leader is not null && GivenNameOf(leader).Equals(GivenNameOf(LocalCharacterName ?? ""), StringComparison.OrdinalIgnoreCase);
        _parState = ParState.Idle; _parBlockNames.Clear(); _followedUs.Clear();
        SetListedWith(null, "server party snapshot");
        HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
        List<string> confirmed = new();
        foreach (JsonElement row in rows.EnumerateArray())
        {
            string name = row.GetProperty("name").GetString()!; string given = GivenNameOf(name);
            present.Add(given);
            bool invited = row.GetProperty("membership").GetString() == "invited";
            bool self = given.Equals(GivenNameOf(LocalCharacterName ?? ""), StringComparison.OrdinalIgnoreCase);
            PartyMember? previous = State.Members.FirstOrDefault(m => GivenNameOf(m.Name).Equals(given, StringComparison.OrdinalIgnoreCase));
            bool joined = !invited && (previous is null || previous.IsInvited);
            PartyMember member = AddOrTouchMember(name, self, invited);
            member.IsInvited = invited; member.IsLeader = leader is not null && GivenNameOf(leader).Equals(given, StringComparison.OrdinalIgnoreCase);
            member.Class = row.GetProperty("class").GetString()!; member.HpPercent = row.GetProperty("hpPct").GetInt32();
            member.MpPercent = row.GetProperty("manaPct").ValueKind == JsonValueKind.Null ? 0 : row.GetProperty("manaPct").GetInt32();
            member.Here = row.GetProperty("here").GetBoolean();
            member.IsKai = row.GetProperty("manaType").GetString() == "kai";
            member.Resting = row.GetProperty("resting").GetBoolean(); member.Meditating = row.GetProperty("meditating").GetBoolean(); member.Poisoned = row.GetProperty("poisoned").GetBoolean();
            member.Position = member.Resting ? PlayerPosition.Resting : member.Meditating ? PlayerPosition.Meditating : PlayerPosition.Standing;
            member.Rank = row.GetProperty("rank").GetString() switch { "front" => Models.Profile.PartyRank.Front, "back" => Models.Profile.PartyRank.Back, _ => Models.Profile.PartyRank.Mid };
            if (State.SelfIsLeader && !invited && !self) { _followedUs.Add(given); if (joined) confirmed.Add(name); }
            NoteHpReading(member, member.HpPercent, "GMCP");
        }
        foreach (PartyMember old in State.Members.Where(m => !present.Contains(GivenNameOf(m.Name))).ToArray()) State.Members.Remove(old);
        if (!inParty) RefreshSoloSelfRow();
        foreach (string name in confirmed) MemberFollowConfirmed?.Invoke(name);
        if (inParty && !State.SelfIsLeader && leader is not null && (!wasFollowing || previousLeader != leader))
        {
            SendRankPreferenceCommand(leader); FollowingLeader?.Invoke(leader);
        }
        ParReplyRead?.Invoke();
    }
}

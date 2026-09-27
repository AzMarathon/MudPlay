using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.Profile;

// One character row in the Profile Management window's right pane. IsCurrent
// flags the profile the session currently has loaded (bolded in the list); Realm
// is the BBS realm it plays (its assigned realm, or the BBS's first).
public sealed record ProfileManagerRow(ProfileRef Ref, bool IsCurrent, string? Realm = null)
{
    public string Name => Ref.Name;
}

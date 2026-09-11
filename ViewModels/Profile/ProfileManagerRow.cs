using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.Profile;

// One character row in the Profile Management window's right pane. IsCurrent
// flags the profile the session currently has loaded (bolded in the list).
public sealed record ProfileManagerRow(ProfileRef Ref, bool IsCurrent)
{
    public string Name => Ref.Name;
}

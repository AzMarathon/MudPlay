using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Models.Profile;

namespace MudPlay.ViewModels.Settings;

// One settings header's "Include in combat profile" checkbox. Checked (the default)
// = the group swaps with the active combat profile; unchecked = one shared value for
// the whole character. Staged in the Settings window's CombatProfileStagingSession
// and saved with the tab.
public sealed partial class CombatProfileGroupToggle : ObservableObject, IDisposable
{
    private readonly CombatProfileStagingSession _session;
    private readonly CombatProfileGroup _group;
    private readonly Action _markDirty;

    public CombatProfileGroupToggle(CombatProfileStagingSession session, CombatProfileGroup group, Action markDirty)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _group = group;
        _markDirty = markDirty ?? throw new ArgumentNullException(nameof(markDirty));
        _session.SharedGroupsChanged += OnSharedGroupsChanged;
    }

    public bool IsIncluded
    {
        get => !_session.IsShared(_group);
        set
        {
            if (value == IsIncluded) return;
            _session.SetShared(_group, !value);
            _markDirty();
        }
    }

    public string Tooltip => IsIncluded
        ? "Saved with the active combat profile: switching profiles switches these settings too. Uncheck to share one set of values across every combat profile."
        : "Shared by every combat profile: switching profiles leaves these settings as they are. Check to give each combat profile its own copy (each starts from the current values).";

    private void OnSharedGroupsChanged()
    {
        OnPropertyChanged(nameof(IsIncluded));
        OnPropertyChanged(nameof(Tooltip));
    }

    public void Dispose() => _session.SharedGroupsChanged -= OnSharedGroupsChanged;
}

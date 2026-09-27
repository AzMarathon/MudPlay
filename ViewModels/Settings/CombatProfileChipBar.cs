using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels.Settings;

// The numbered combat-profile chips, shown at the top of every Settings tab that
// carries per-profile groups (Combat, Health, Spells, Party), so profiles can be
// switched and compared from whichever tab is open. Clicking a chip stages a switch
// through the window's shared CombatProfileStagingSession; the tabs reload from it.
public sealed class CombatProfileChipBar : IDisposable
{
    private readonly CombatProfileStagingSession _session;

    public ObservableCollection<CombatProfileMenuItem> Chips { get; } = new();

    public CombatProfileChipBar(CombatProfileStagingSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.ChipsChanged += Rebuild;
        Rebuild();
    }

    private void Rebuild()
    {
        Chips.Clear();
        for (int i = 0; i < _session.Profiles.Count; i++)
        {
            int index = i;
            Chips.Add(new CombatProfileMenuItem(
                number: i + 1, name: _session.Profiles[i].Name, isActive: i == _session.ActiveIndex,
                switchCommand: new RelayCommand(() => _session.SwitchTo(index))));
        }
    }

    public void Dispose() => _session.ChipsChanged -= Rebuild;
}

using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace MudPlay.ViewModels.Settings;

// The numbered combat-profile chips plus add / remove, shown at the top of every
// Settings tab that carries per-profile groups (Combat, Health, Spells, Party), so
// profiles can be switched, compared, added and removed from whichever tab is open.
// Every action stages through the window's shared CombatProfileStagingSession; the
// tabs reload from it.
public sealed class CombatProfileChipBar : IDisposable
{
    private readonly CombatProfileStagingSession _session;

    public ObservableCollection<CombatProfileMenuItem> Chips { get; } = new();

    // Add a new empty profile and switch to it / remove the active one (keeps ≥1).
    public IRelayCommand AddProfileCommand { get; }
    public IRelayCommand RemoveActiveProfileCommand { get; }

    public CombatProfileChipBar(CombatProfileStagingSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        AddProfileCommand = new RelayCommand(_session.AddNew);
        RemoveActiveProfileCommand = new RelayCommand(_session.RemoveActive);
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

using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MudPlay.ViewModels.CharacterWorkshop;

// Base for one tab of the Player Workshop: a section of its own (Character Info,
// Quest Status), or a WorkshopGroupSectionViewModel holding several as sub-tabs.
public abstract class WorkshopSectionViewModel : ObservableObject, IDisposable
{
    // Stable identifier — tab selection persists across reopens against this.
    public abstract string Id { get; }

    // Display title on the tab.
    public abstract string Title { get; }

    // The content UserControl rendered under the tab. Constructed lazily on first access.
    public abstract Control View { get; }

    // The section actually on screen under this tab: itself, or a group's selected
    // sub-tab.
    public virtual WorkshopSectionViewModel Leaf => this;

    // The window size this section asks for while it's showing. Null leaves the
    // window to fit the section's content, which works for a form but not for a
    // wide grid whose natural width is every column it has.
    public virtual Size? PreferredSize => null;

    // What's on screen under this tab, or the size it asks for, changed.
    public event Action<WorkshopSectionViewModel>? LayoutChanged;
    protected void RaiseLayoutChanged() => LayoutChanged?.Invoke(this);

    // Detach from any long-lived service events the section subscribed to. The Workshop
    // window is recreated on every open, so its sections must unsubscribe on close or
    // each open/close cycle leaks one set of them. Base is a no-op; sections that
    // subscribe override this. Called by CharacterWorkshopViewModel.Dispose when the
    // window closes.
    public virtual void Dispose() { }
}

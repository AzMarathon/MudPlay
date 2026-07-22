using Avalonia.Input;
using FujinTerm.Models.Profile;
using FujinTerm.Services;
using Xunit;

namespace FujinTerm.Tests;

public sealed class KeybindingStoreTests
{
    [Fact]
    public void Defaults_AreSeeded_OnFreshStore()
    {
        KeybindingStore store = new();
        Assert.Equal(new KeyChord(Key.C, Alt: true), store.Get(BuiltInAction.OpenConversation));
        Assert.Equal(new KeyChord(Key.F3), store.Get(BuiltInAction.OpenGameDataBrowser));
        Assert.Equal(new KeyChord(Key.S, Ctrl: true, Shift: true), store.Get(BuiltInAction.SaveProfileAs));
        // OpenParty ships unbound — F3 now opens the Game Data Browser.
        Assert.Equal(KeyChord.Empty, store.Get(BuiltInAction.OpenParty));
    }

    [Fact]
    public void Rebind_ReplacesChord_AndFiresChangeEvent()
    {
        KeybindingStore store = new();
        BuiltInAction? notified = null;
        store.BindingChanged += a => notified = a;

        store.Rebind(BuiltInAction.OpenConversation, new KeyChord(Key.F6, Ctrl: true));

        Assert.Equal(new KeyChord(Key.F6, Ctrl: true), store.Get(BuiltInAction.OpenConversation));
        Assert.Equal(BuiltInAction.OpenConversation, notified);
    }

    [Fact]
    public void IsConflict_FlagsCollidingChord_ReportsCollidingAction()
    {
        KeybindingStore store = new();
        // F2 is bound to OpenSpellBook by default; rebinding OpenParty
        // to F2 must flag a conflict.
        bool collision = store.IsConflict(new KeyChord(Key.F2), excluding: BuiltInAction.OpenParty,
                                           out BuiltInAction? culprit);
        Assert.True(collision);
        Assert.Equal(BuiltInAction.OpenSpellBook, culprit);
    }

    [Fact]
    public void IsConflict_ExcludesSpecifiedAction_DuringSelfEdit()
    {
        KeybindingStore store = new();
        // F2 → OpenSpellBook by default. The OpenSpellBook editor
        // should NOT flag its own current chord as a collision.
        Assert.False(store.IsConflict(new KeyChord(Key.F2), excluding: BuiltInAction.OpenSpellBook, out _));
    }

    [Fact]
    public void FindAction_ReturnsBoundAction_ForKnownChord()
    {
        KeybindingStore store = new();
        Assert.Equal(BuiltInAction.OpenWorkshop, store.FindAction(new KeyChord(Key.F1)));
        Assert.Null(store.FindAction(new KeyChord(Key.F8)));  // unbound by default
    }

    [Fact]
    public void Empty_ChordNeverConflicts()
    {
        KeybindingStore store = new();
        Assert.False(store.IsConflict(KeyChord.Empty, excluding: null, out _));
        Assert.Null(store.FindAction(KeyChord.Empty));
    }
}

using System;
using System.Windows.Input;
using Avalonia.Controls;

namespace MudPlay.Controls;

// Shared command backing the ✕ clear button the TextBox.clearable style injects
// into filter / search boxes (see Themes/Controls.axaml). Clears the TextBox
// passed as the command parameter and returns focus to it so the user can keep
// typing. One stateless instance serves every clearable box — each binds to it
// and passes its own TextBox as the parameter.
public sealed class ClearTextBoxCommand : ICommand
{
    public static ClearTextBoxCommand Instance { get; } = new();

    // The button's visibility already gates on non-empty text, so CanExecute never
    // has to change; the event is required by the interface but never raised.
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => parameter is TextBox;

    public void Execute(object? parameter)
    {
        if (parameter is not TextBox box) return;
        box.Clear();
        box.Focus();
    }
}

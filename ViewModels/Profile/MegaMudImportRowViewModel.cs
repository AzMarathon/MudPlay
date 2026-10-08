using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Services;

namespace MudPlay.ViewModels.Profile;

// One carried-over setting in the import review, with the editor its kind of value
// takes. What is left in the editor at Import replaces the file's value.
public sealed partial class MegaMudImportRowViewModel : ObservableObject
{
    private readonly MegaMudImportLine _line;

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private decimal? _number;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AdviceText), nameof(HasAdvice), nameof(IsStrongAdvice), nameof(IsMildAdvice))]
    private string _text;

    public MegaMudImportRowViewModel(MegaMudImportLine line)
    {
        _line = line ?? throw new ArgumentNullException(nameof(line));
        _isOn = line.EditValue == "1";
        _number = decimal.TryParse(line.EditValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out decimal n) ? n : 0;
        _text = line.EditValue;
    }

    public string Group => _line.Group;
    public string Setting => _line.Setting;
    public string Unit => _line.Unit;
    public string FixedText => _line.Value;
    public string EditKey => _line.EditKey;

    public bool IsFlag => _line.Edit == MegaMudImportEdit.Flag;
    public bool IsNumber => _line.Edit == MegaMudImportEdit.Number;
    public bool IsText => _line.Edit == MegaMudImportEdit.Text;
    public bool IsFixed => _line.Edit == MegaMudImportEdit.None;

    // A percentage can't pass 100; anything else is held to its own range when the
    // character is made, and the review of that character's settings shows the result.
    public decimal Maximum => _line.Unit == "%" ? 100 : 999999;

    public string CurrentValue => _line.Edit switch
    {
        MegaMudImportEdit.Flag => IsOn ? "1" : "0",
        MegaMudImportEdit.Number => ((int)(Number ?? 0)).ToString(CultureInfo.InvariantCulture),
        MegaMudImportEdit.Text => (Text ?? string.Empty).Trim(),
        _ => _line.EditValue,
    };

    public bool IsEdited => !IsFixed && !string.Equals(CurrentValue, _line.EditValue, StringComparison.Ordinal);

    private MegaMudImportAdvice? Advice => IsText ? _line.Advise?.Invoke(Text ?? string.Empty) : null;

    public string AdviceText => Advice?.Text ?? string.Empty;
    public bool HasAdvice => Advice is not null;
    public bool IsStrongAdvice => Advice is { Strong: true };
    public bool IsMildAdvice => Advice is { Strong: false };
}

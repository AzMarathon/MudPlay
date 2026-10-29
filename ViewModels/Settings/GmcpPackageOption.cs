using CommunityToolkit.Mvvm.ComponentModel;
using MudPlay.Game.Gmcp;

namespace MudPlay.ViewModels.Settings;

public sealed class GmcpPackageOption(string package) : ObservableObject
{
    private GmcpMode _mode = GmcpMode.Shadow;
    public string Package { get; } = package;
    public string Label { get; } = package.Replace("Anomaly.", "", StringComparison.Ordinal);
    public IReadOnlyList<GmcpMode> Modes { get; } = Enum.GetValues<GmcpMode>();
    public GmcpMode Mode { get => _mode; set => SetProperty(ref _mode, value); }
}

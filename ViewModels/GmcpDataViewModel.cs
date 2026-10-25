using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MudPlay.Game.Gmcp;

namespace MudPlay.ViewModels;

public sealed partial class GmcpDataViewModel : ObservableObject, IDisposable
{
    private readonly GmcpIntegration _gmcp;
    public IReadOnlyList<string> Packages { get; } = GmcpIntegration.Packages.Where(p => p is not ("Anomaly.Mob" or "Anomaly.Comm.Line")).ToArray();
    [ObservableProperty] private string _selectedPackage = "Anomaly.Items.List";
    [ObservableProperty] private string _itemNumber = "";
    [ObservableProperty] private string _body = "";
    [ObservableProperty] private string _status = "";

    public GmcpDataViewModel(GmcpIntegration gmcp)
    {
        _gmcp = gmcp; gmcp.State.Changed += OnChanged; Update();
    }

    partial void OnSelectedPackageChanged(string value) => Update();
    partial void OnItemNumberChanged(string value) => Update();
    private void OnChanged(string package) => Update();

    private void Update()
    {
        GmcpState state = _gmcp.State;
        Status = state.Compatible && state.InSession ? $"Connected · contract {state.ServerContract} · {state.Mode(SelectedPackage)}" : "No compatible server session";
        if (SelectedPackage == "Anomaly.Items.Usable") Status += " · last requested response; query again after character changes";
        JsonElement? data = state.Snapshot(SelectedPackage);
        if (int.TryParse(ItemNumber, out int id))
        {
            if (SelectedPackage == "Anomaly.Items.Detail") data = state.ItemCards.TryGetValue(id, out JsonElement card) ? card : null;
            if (SelectedPackage == "Anomaly.Items.Usable") data = state.ItemUsability.TryGetValue(id, out JsonElement usable) ? usable : null;
        }
        Body = data is { } value ? JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }) : "No current data. Item cards and usability can be requested by item number.";
    }

    [RelayCommand] private async Task ItemCard() => await Request(true);
    [RelayCommand] private async Task ItemUsability() => await Request(false);
    [RelayCommand] private async Task Refresh()
    {
        try { await _gmcp.RefreshAsync(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { Status = "Refresh failed: " + ex.Message; }
    }
    private async Task Request(bool card)
    {
        if (!int.TryParse(ItemNumber, out int num) || num <= 0) { Status = "Enter a positive item number."; return; }
        if (!_gmcp.State.Compatible || !_gmcp.State.InSession) { Status = "Connect to a compatible server first."; return; }
        SelectedPackage = card ? "Anomaly.Items.Detail" : "Anomaly.Items.Usable";
        try
        {
            if (card) await _gmcp.RequestItemDetailAsync(num); else await _gmcp.RequestItemUsabilityAsync(num);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { Status = "Request failed: " + ex.Message; }
    }

    public void Dispose() => _gmcp.State.Changed -= OnChanged;
}

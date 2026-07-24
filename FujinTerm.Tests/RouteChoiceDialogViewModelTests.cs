using System.Collections.Generic;
using FujinTerm.Game.Map;
using FujinTerm.Services;
using FujinTerm.ViewModels.Navigation;
using Xunit;

namespace FujinTerm.Tests;

// The picker's requirement line promises a source tail — "(ask <giver>)", else
// "(buy at <shop>)", else "(dropped by <monster>)" — only for the single-item
// gate kinds a walk actually auto-sources (Item, Ticket, single-counter
// hazard). Keys and any-of hazard counters never post a single auto-obtain
// path-item need, so they must not carry a tail even when a resolver would name
// one. These pin that kind-gating, the give-over-shop-over-drop precedence, and
// the no-resolver fallback, plus the select-to-preview / Go interaction.
public sealed class RouteChoiceDialogViewModelTests
{
    private static readonly IReadOnlyList<RoomKey> FreeLine =
        new[] { new RoomKey(1, 1), new RoomKey(1, 2), new RoomKey(1, 9) };
    private static readonly IReadOnlyList<RoomKey> GatedLine =
        new[] { new RoomKey(1, 1), new RoomKey(1, 9) };

    private static RouteChoice Choice(params RouteRequirement[] reqs) =>
        new(FreeStepCount: 5, GatedStepCount: 2, reqs, FreeLine, GatedLine);

    // Sole-route variant: no gate-free detour (empty FreePath), so the picker
    // collapses the send-it/acquire split to the single acquire card.
    private static RouteChoice SoleChoice(params RouteRequirement[] reqs) =>
        new(FreeStepCount: 0, GatedStepCount: 2, reqs, System.Array.Empty<RoomKey>(), GatedLine);

    [Fact]
    public void CarryItemGate_WithShop_GetsBuyTail()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => id == 5 ? "a raft" : null,
            shopNameForItem: id => id == 5 ? "General Store" : null);

        Assert.Equal("Requires a raft (buy at General Store)", vm.RequirementSummary);
    }

    [Fact]
    public void TicketGate_WithShop_GetsBuyTail()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.Ticket, new[] { 7 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Docks (1/9)", id => id == 7 ? "a ferry ticket" : null,
            shopNameForItem: id => id == 7 ? "Ticket Booth" : null);

        Assert.Equal("Requires a ferry ticket (buy at Ticket Booth)", vm.RequirementSummary);
    }

    [Fact]
    public void DoorKeyGate_NeverGetsBuyTail_EvenIfShopResolves()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.DoorKey, new[] { 9 }));

        // A key is never sourced on a path detour, so a resolver that names a
        // giver or shop must be ignored for the DoorKey kind.
        var vm = new RouteChoiceDialogViewModel(
            choice, "Vault (1/9)", id => "the iron key",
            giveNameForItem: id => "a gatekeeper", shopNameForItem: id => "Locksmith");

        Assert.Equal("Requires the iron key", vm.RequirementSummary);
    }

    [Fact]
    public void HazardGate_NeverGetsBuyTail()
    {
        var choice = Choice(new RouteRequirement(
            RouteRequirementKind.HazardProtection, new[] { 11, 12 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Flooded hall (1/9)",
            id => id == 11 ? "a fish-helm" : "a waterskin",
            id => "General Store");

        Assert.Equal("Requires a fish-helm or a waterskin", vm.RequirementSummary);
    }

    [Fact]
    public void HazardGate_NoShop_GetsDropTail()
    {
        // A single-counter hazard whose item no shop sells but a monster drops:
        // the picker previews the hunt the walk would reroute to run.
        var choice = Choice(new RouteRequirement(
            RouteRequirementKind.HazardProtection, new[] { 42 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Sunbaked dune (1/9)",
            id => id == 42 ? "a waterskin" : null,
            shopNameForItem: id => null,          // no shop stocks it
            dropNameForItem: id => id == 42 ? "a sand nomad" : null);

        Assert.Equal("Requires a waterskin (dropped by a sand nomad)", vm.RequirementSummary);
    }

    [Fact]
    public void CarryItemGate_ShopWinsOverDrop_WhenBothResolve()
    {
        // Shop and drop both name a source — the buy tail wins (cheap,
        // deterministic; the routers are shop-first mutually exclusive).
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => "a raft",
            shopNameForItem: id => "General Store",
            dropNameForItem: id => "a river troll");

        Assert.Equal("Requires a raft (buy at General Store)", vm.RequirementSummary);
    }

    [Fact]
    public void CarryItemGate_WithGive_GetsAskTail()
    {
        // A deterministic textblock giver hands the gate item over for free — the
        // picker names the giver the run will ask.
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => "a bloodstone orb",
            giveNameForItem: id => id == 5 ? "Gnome Commander" : null);

        Assert.Equal("Requires a bloodstone orb (ask Gnome Commander)", vm.RequirementSummary);
    }

    [Fact]
    public void CarryItemGate_GiveWinsOverShopAndDrop_WhenAllResolve()
    {
        // Give, shop, and drop all name a source — the free give tail wins,
        // mirroring the routers' give-first precedence (the give stands the shop
        // and drop routers down, so the picker must promise the give).
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => "a bloodstone orb",
            giveNameForItem: id => "Gnome Commander",
            shopNameForItem: id => "General Store",
            dropNameForItem: id => "a river troll");

        Assert.Equal("Requires a bloodstone orb (ask Gnome Commander)", vm.RequirementSummary);
    }

    [Fact]
    public void HazardGate_AnyOf_NeverGetsDropTail()
    {
        // Two-item hazard group: an any-of counter posts no single auto-obtain
        // need, so no drop tail even when a resolver would name a dropper.
        var choice = Choice(new RouteRequirement(
            RouteRequirementKind.HazardProtection, new[] { 11, 12 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Flooded hall (1/9)",
            id => id == 11 ? "a fish-helm" : "a waterskin",
            shopNameForItem: id => null,
            dropNameForItem: id => "a deep one");

        Assert.Equal("Requires a fish-helm or a waterskin", vm.RequirementSummary);
    }

    [Fact]
    public void NoShopResolver_LeavesRequirementsPlain()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));

        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => "a raft");

        Assert.Equal("Requires a raft", vm.RequirementSummary);
    }

    [Fact]
    public void CarryItemGate_ShopResolvesNull_StaysPlain()
    {
        var choice = Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 }));

        // Item flagged/looked-up but no reachable shop stocks it → no tail.
        var vm = new RouteChoiceDialogViewModel(
            choice, "Bank (1/9)", id => "a raft", id => null);

        Assert.Equal("Requires a raft", vm.RequirementSummary);
    }

    // ----- Select-to-preview / Go interaction -----------------------------

    private static RouteChoiceDialogViewModel PickerVm() =>
        new(Choice(new RouteRequirement(RouteRequirementKind.CarryItem, new[] { 5 })),
            "Bank (1/9)", id => "a raft");

    [Fact]
    public void NoSelection_GoDisabled_NothingPreviewed()
    {
        var vm = PickerVm();
        bool previewed = false;
        vm.PreviewRequested += _ => previewed = true;

        Assert.Null(vm.SelectedRoute);
        Assert.False(vm.GoCommand.CanExecute(null));
        Assert.False(vm.IsFreeSelected);
        Assert.False(vm.IsGatedSelected);
        Assert.False(previewed);
    }

    [Fact]
    public void SelectFree_PreviewsFreeLine_EnablesGo_NoWalkYet()
    {
        var vm = PickerVm();
        RouteChoiceResult? previewed = null;
        int previewCount = 0;
        RouteChoiceResult? closed = null;
        bool closeFired = false;
        vm.PreviewRequested += r => { previewed = r; previewCount++; };
        vm.CloseRequested += r => { closed = r; closeFired = true; };

        vm.SelectFreeCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.Free, vm.SelectedRoute);
        Assert.True(vm.IsFreeSelected);
        Assert.False(vm.IsGatedSelected);
        Assert.Equal(RouteChoiceResult.Free, previewed);
        Assert.Equal(1, previewCount);
        Assert.True(vm.GoCommand.CanExecute(null));
        // Selecting previews only — the dialog stays open until Go.
        Assert.False(closeFired);
        Assert.Null(closed);
    }

    [Fact]
    public void SwitchSelection_RepreviewsAndFlipsHighlight()
    {
        var vm = PickerVm();
        var previews = new List<RouteChoiceResult?>();
        vm.PreviewRequested += r => previews.Add(r);

        vm.SelectFreeCommand.Execute(null);
        vm.SelectGatedCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.Gated, vm.SelectedRoute);
        Assert.False(vm.IsFreeSelected);
        Assert.True(vm.IsGatedSelected);
        Assert.Equal(
            new RouteChoiceResult?[] { RouteChoiceResult.Free, RouteChoiceResult.Gated },
            previews);
    }

    [Fact]
    public void Go_ClosesWithSelectedRoute()
    {
        var vm = PickerVm();
        RouteChoiceResult? closed = null;
        int closeCount = 0;
        vm.CloseRequested += r => { closed = r; closeCount++; };

        vm.SelectGatedCommand.Execute(null);
        vm.GoCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.Gated, closed);
        Assert.Equal(1, closeCount);
    }

    [Fact]
    public void Cancel_ClosesWithNull_EvenAfterASelection()
    {
        var vm = PickerVm();
        RouteChoiceResult? closed = RouteChoiceResult.Free;
        bool closeFired = false;
        vm.CloseRequested += r => { closed = r; closeFired = true; };

        vm.SelectFreeCommand.Execute(null);
        vm.CancelCommand.Execute(null);

        Assert.True(closeFired);
        Assert.Null(closed);
    }

    // ----- Send-it (direct — no acquire) third option ----------------------

    [Fact]
    public void SendItCard_ShownWhenFreeRouteExists()
    {
        var vm = PickerVm();
        Assert.True(vm.HasFreeRoute);
        Assert.True(vm.ShowSendItCard);
    }

    [Fact]
    public void SendItCard_HiddenForSoleRoute()
    {
        // No gate-free detour: the acquire/send-it split collapses, so only the
        // single acquire card is offered (chunk-4 flag logic owns the sole case).
        var vm = new RouteChoiceDialogViewModel(
            SoleChoice(new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 42 })),
            "Sunbaked dune (1/9)", id => "a waterskin");

        Assert.False(vm.HasFreeRoute);
        Assert.False(vm.ShowSendItCard);
    }

    [Fact]
    public void SelectSendIt_PreviewsGatedLine_EnablesGo_NoWalkYet()
    {
        var vm = PickerVm();
        RouteChoiceResult? previewed = null;
        bool closeFired = false;
        vm.PreviewRequested += r => previewed = r;
        vm.CloseRequested += _ => closeFired = true;

        vm.SelectSendItCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.GatedNoAcquire, vm.SelectedRoute);
        Assert.True(vm.IsSendItSelected);
        Assert.False(vm.IsGatedSelected);
        Assert.False(vm.IsFreeSelected);
        Assert.Equal(RouteChoiceResult.GatedNoAcquire, previewed);
        Assert.True(vm.GoCommand.CanExecute(null));
        Assert.False(closeFired);
    }

    [Fact]
    public void SelectSendIt_NoOp_WhenNoFreeRoute()
    {
        var vm = new RouteChoiceDialogViewModel(
            SoleChoice(new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 42 })),
            "Sunbaked dune (1/9)", id => "a waterskin");
        bool previewed = false;
        vm.PreviewRequested += _ => previewed = true;

        vm.SelectSendItCommand.Execute(null);

        Assert.Null(vm.SelectedRoute);
        Assert.False(vm.IsSendItSelected);
        Assert.False(previewed);
    }

    [Fact]
    public void Go_ClosesWithGatedNoAcquire_AfterSendIt()
    {
        var vm = PickerVm();
        RouteChoiceResult? closed = null;
        vm.CloseRequested += r => closed = r;

        vm.SelectSendItCommand.Execute(null);
        vm.GoCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.GatedNoAcquire, closed);
    }

    // ----- Sole-route wording (no gate-free detour) ------------------------

    [Fact]
    public void SoleDoorKeyRoute_UsesGateGenericWording_NotHazard()
    {
        // A locked-door sole route reaches the picker (a key is never auto-sourced).
        // The wording must not call it a "hazard" — it's a gate cleared by hand.
        var vm = new RouteChoiceDialogViewModel(
            SoleChoice(new RouteRequirement(RouteRequirementKind.DoorKey, new[] { 9 })),
            "Vault (1/9)", id => "the iron key");

        Assert.False(vm.HasFreeRoute);
        Assert.False(vm.ShowSendItCard);
        Assert.Contains("gated", vm.Heading, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hazard", vm.Heading, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hazard", vm.FreeSummary, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gate", vm.FreeSummary, System.StringComparison.OrdinalIgnoreCase);
        // The key names itself with no source tail (keys aren't sourced).
        Assert.Equal("Requires the iron key", vm.RequirementSummary);
    }

    [Fact]
    public void SoleHazardRoute_KeepsHazardWording()
    {
        var vm = new RouteChoiceDialogViewModel(
            SoleChoice(new RouteRequirement(RouteRequirementKind.HazardProtection, new[] { 42 })),
            "Sunbaked dune (1/9)", id => "a waterskin");

        Assert.False(vm.HasFreeRoute);
        Assert.Contains("hazard", vm.Heading, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hazard", vm.FreeSummary, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SwitchAcrossAllThree_RepreviewsEachSelection()
    {
        var vm = PickerVm();
        var previews = new List<RouteChoiceResult?>();
        vm.PreviewRequested += r => previews.Add(r);

        vm.SelectFreeCommand.Execute(null);
        vm.SelectGatedCommand.Execute(null);
        vm.SelectSendItCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.GatedNoAcquire, vm.SelectedRoute);
        Assert.True(vm.IsSendItSelected);
        Assert.Equal(
            new RouteChoiceResult?[]
            {
                RouteChoiceResult.Free,
                RouteChoiceResult.Gated,
                RouteChoiceResult.GatedNoAcquire,
            },
            previews);
    }

    [Fact]
    public void Summaries_DistinguishAcquireFromSendIt()
    {
        var vm = PickerVm();
        Assert.Contains("acquire", vm.GatedSummary, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("send it", vm.SendItSummary, System.StringComparison.OrdinalIgnoreCase);
    }

    // ----- Teleport fork: walk vs teleport --------------------------------

    private static RouteChoice TeleportChoice() =>
        new(FreeStepCount: 8, GatedStepCount: 2,
            System.Array.Empty<RouteRequirement>(), FreeLine, GatedLine,
            RouteChoiceKind.Teleport, "Silver River (12/34)");

    [Fact]
    public void TeleportChoice_WordsCardsAsWalkVsTeleport_HidesSendIt()
    {
        var vm = new RouteChoiceDialogViewModel(TeleportChoice(), "Silver River (12/34)", id => null);

        Assert.True(vm.IsTeleportChoice);
        Assert.True(vm.HasFreeRoute);
        Assert.False(vm.ShowSendItCard);            // no acquisition to skip
        Assert.Contains("teleport", vm.Heading, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Walk it", vm.FreeSummary);
        Assert.Contains("Teleport", vm.GatedSummary);
        Assert.Empty(vm.RequirementSummary);

        // The gated card's detail line names the landing and warns of the danger.
        Assert.Equal(vm.TeleportCaveat, vm.GatedDetail);
        Assert.Contains("Silver River", vm.GatedDetail);
        Assert.Contains("deadly", vm.GatedDetail, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TeleportChoice_FreeIsWalk_GatedIsTeleport_GoCommits()
    {
        var vm = new RouteChoiceDialogViewModel(TeleportChoice(), "Silver River (12/34)", id => null);
        var previews = new List<RouteChoiceResult?>();
        RouteChoiceResult? closed = null;
        vm.PreviewRequested += r => previews.Add(r);
        vm.CloseRequested += r => closed = r;

        vm.SelectFreeCommand.Execute(null);         // "Walk it"
        vm.SelectGatedCommand.Execute(null);        // "Teleport"
        vm.GoCommand.Execute(null);

        Assert.Equal(RouteChoiceResult.Gated, closed);
        Assert.Equal(
            new RouteChoiceResult?[] { RouteChoiceResult.Free, RouteChoiceResult.Gated },
            previews);
    }
}

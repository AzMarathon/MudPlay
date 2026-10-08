using System;
using System.IO;
using System.Linq;
using MudPlay.Game.Map;
using MudPlay.Models.GameData;
using MudPlay.Models.Profile;
using MudPlay.Services;
using MudPlay.ViewModels.Navigation;
using MudPlay.ViewModels.Settings;
using Xunit;

namespace MudPlay.Tests;

// The Event editor's Walk to boxes suggest known rooms as the user types, so a room
// is picked off the map instead of typed from memory, and the box names the room it
// will walk to.
public sealed class EventWalkToRoomSearchTests : IDisposable
{
    private readonly string _root;

    public EventWalkToRoomSearchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mudplay-event-walkto-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort */ }
    }

    private const string RoomsJson = """
        [
          { "Map Number": 1, "Room Number": 1810, "Name": "Gigantic Cave",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "1/1811", "S": "0", "E": "0", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 1811, "Name": "Cave Mouth",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "1/1810", "E": "1/297", "W": "0",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" },
          { "Map Number": 1, "Room Number": 297, "Name": "Bank of Godfrey",
            "Light": 0, "Shop": 0, "Lair": "", "Delay": 5,
            "N": "0", "S": "0", "E": "0", "W": "1/1811",
            "NE": "0", "NW": "0", "SE": "0", "SW": "0", "U": "0", "D": "0" }
        ]
        """;

    private RoomSearchService NewSearch()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));
        File.WriteAllText(Path.Combine(_root, "alpha", "Rooms.json"), RoomsJson);
        GameDataCache cache = new(_root);
        cache.SwitchSet("alpha");
        RoomGraphManager graph = new(cache);
        graph.OnActiveSetChanged("alpha");
        return new RoomSearchService(graph, cache, new BfsMapper(graph), new RoomBlacklistStore());
    }

    // 1/1810 is Thrag's room and marked "stop before entering"; no other room is.
    private EventEditDialogViewModel Editor(ScheduledEvent? existing = null) =>
        new(existing ?? new ScheduledEvent(), isNew: existing is null, search: NewSearch(),
            stopBeforeBoss: room => room.Equals(new RoomKey(1, 1810)) ? "Thrag" : null);

    private static ScheduledEvent Saved(EventEditDialogViewModel vm)
    {
        ScheduledEvent? saved = null;
        vm.CloseRequested += e => saved = e;
        vm.SaveCommand.Execute(null);
        return saved!;
    }

    [Fact]
    public void TypingARoomName_SuggestsTheRoomsItCouldMean()
    {
        EventEditDialogViewModel vm = Editor();

        Assert.Equal(new[] { "1/1810 - Gigantic Cave" },
            vm.SuggestRooms("gigantic").Select(r => r.DisplayName).ToArray());
        Assert.Equal(new[] { "1/1810 - Gigantic Cave", "1/1811 - Cave Mouth" },
            vm.SuggestRooms("cave").Select(r => r.DisplayName).OrderBy(n => n).ToArray());
        Assert.Empty(vm.SuggestRooms("nowhere at all"));
        Assert.Empty(vm.SuggestRooms("   "));
    }

    // A typed coordinate is answered with the room it is, so the user sees the
    // client knows the number they entered.
    [Fact]
    public void TypingACoordinate_SuggestsThatRoomByName()
    {
        RoomSearchResult only = Assert.Single(Editor().SuggestRooms("1/1810"));
        Assert.Equal("1/1810 - Gigantic Cave", only.DisplayName);
    }

    // The dropdown is fed through the shape the box's populator takes.
    [Fact]
    public async System.Threading.Tasks.Task ThePopulator_HandsBackTheSameSuggestions()
    {
        EventEditDialogViewModel vm = Editor();
        object[] rows = (await vm.RoomSuggestions("bank", default)).ToArray();
        Assert.Equal("1/297 - Bank of Godfrey", Assert.IsType<RoomSearchResult>(Assert.Single(rows)).DisplayName);
    }

    // What a picked suggestion leaves in the box saves as that room.
    [Fact]
    public void APickedSuggestion_SavesAsItsRoom()
    {
        EventEditDialogViewModel vm = Editor();
        vm.IsTriggerLogon = true;
        vm.IsActionWalkTo = true;
        vm.WalkToText = vm.SuggestRooms("gigantic")[0].DisplayName;
        vm.IsThenWalkTo = true;
        vm.ThenWalkToText = "1/297 - Bank of Godfrey";

        Assert.Null(vm.TryGetMissingTargetMessage());
        ScheduledEvent saved = Saved(vm);

        Assert.Equal((1, 1810), (saved.WalkToTarget!.Map, saved.WalkToTarget.Room));
        Assert.Equal((1, 297), (saved.ThenWalkTo!.Map, saved.ThenWalkTo.Room));
    }

    // A reopened event names its room, not just its number.
    [Fact]
    public void ASavedRoom_ReopensWithItsName()
    {
        EventEditDialogViewModel vm = Editor(new ScheduledEvent
        {
            ActionType = EventActionType.WalkTo,
            WalkToTarget = new RoomRef(1, 1810),
            ThenWalkTo = new RoomRef(1, 297),
        });

        Assert.Equal("1/1810 - Gigantic Cave", vm.WalkToText);
        Assert.Equal("1/297 - Bank of Godfrey", vm.ThenWalkToText);
    }

    // A room the map doesn't have still shows, as the bare number it was saved as.
    [Fact]
    public void ASavedRoomNotOnTheMap_ReopensAsItsNumber()
    {
        EventEditDialogViewModel vm = Editor(new ScheduledEvent
        {
            ActionType = EventActionType.WalkTo,
            WalkToTarget = new RoomRef(9, 9999),
        });
        Assert.Equal("9/9999", vm.WalkToText);
    }

    // A Walk to box holding a stop-before boss room says so and offers the choice;
    // any other room doesn't.
    [Fact]
    public void AStopBeforeBossRoom_IsPointedOut_AsTheBoxIsFilled()
    {
        EventEditDialogViewModel vm = Editor();
        Assert.False(vm.HasWalkToBossNotice);

        vm.WalkToText = "1/1810 - Gigantic Cave";
        Assert.True(vm.HasWalkToBossNotice);
        Assert.Contains("Thrag's room is marked Stop before entering", vm.WalkToBossNotice);

        vm.WalkToText = "1/1810";
        Assert.True(vm.HasWalkToBossNotice);

        vm.WalkToText = "1/297 - Bank of Godfrey";
        Assert.False(vm.HasWalkToBossNotice);

        vm.ThenWalkToText = "1/1810";
        Assert.True(vm.HasThenWalkToBossNotice);
    }

    // The note under the box says what the walk will do as the box stands.
    [Fact]
    public void TheNote_SaysWhetherTheWalkGoesIn()
    {
        EventEditDialogViewModel vm = Editor();
        vm.IsActionWalkTo = true;
        vm.WalkToText = "1/1810";
        Assert.Contains("ends in the room next to it and does NOT go in", vm.WalkToBossNotice);

        vm.WalkToEntersBossRoom = true;
        Assert.Contains("ignores that and goes into the room", vm.WalkToBossNotice);
    }

    // Keeping the stop leaves the character outside the boss room with only Then
    // to follow, so the Then block spells out what the event does from there.
    [Fact]
    public void KeepingTheStop_IsExplainedInTheThenBlock_ByWhatThenIsSetTo()
    {
        EventEditDialogViewModel vm = Editor();
        vm.IsActionWalkTo = true;
        Assert.False(vm.HasStopsOutsideHint);               // no boss room in the box yet

        vm.WalkToText = "1/1810";
        vm.IsThenResume = true;
        Assert.Contains("stops in the room next to Thrag's room and does not go in", vm.StopsOutsideHint);
        Assert.Contains("goes back to what was running, without entering", vm.StopsOutsideHint);
        Assert.Contains("To go in, set Then to Walk to this same room and tick its box", vm.StopsOutsideHint);

        vm.IsThenNothing = true;
        Assert.Contains("stays outside the room", vm.StopsOutsideHint);

        // A follow-on event is a fair way to use it: walk up, then let that decide.
        vm.IsThenEvent = true;
        vm.ThenEventName = "check thrag";
        Assert.Contains("fires \"check thrag\" from outside the room", vm.StopsOutsideHint);

        // Then walking to the same room, stop kept: it never goes in.
        vm.IsThenWalkTo = true;
        vm.ThenWalkToText = "1/1810 - Gigantic Cave";
        Assert.Contains("stops short of it again, so the event never goes in", vm.StopsOutsideHint);

        vm.ThenWalkToEntersBossRoom = true;
        Assert.Contains("As set, Then walks into the room.", vm.StopsOutsideHint);

        vm.ThenWalkToText = "1/297";
        Assert.Contains("walks on to a different room, without entering", vm.StopsOutsideHint);
    }

    // Nothing to explain once the walk goes in, isn't to a boss room, or isn't a walk.
    [Fact]
    public void TheThenBlock_SaysNothing_WhenTheEventDoesNotStopOutside()
    {
        EventEditDialogViewModel vm = Editor();
        vm.IsActionWalkTo = true;
        vm.WalkToText = "1/1810";
        Assert.True(vm.HasStopsOutsideHint);

        vm.WalkToEntersBossRoom = true;
        Assert.False(vm.HasStopsOutsideHint);

        vm.WalkToEntersBossRoom = false;
        vm.WalkToText = "1/297";
        Assert.False(vm.HasStopsOutsideHint);

        vm.WalkToText = "1/1810";
        vm.IsActionCommand = true;
        Assert.False(vm.HasStopsOutsideHint);
    }

    [Fact]
    public void TheChoice_IsSavedWithTheEvent_AndComesBackWhenReopened()
    {
        EventEditDialogViewModel vm = Editor();
        vm.IsTriggerLogon = true;
        vm.IsActionWalkTo = true;
        vm.WalkToText = "1/1810";
        Assert.False(vm.WalkToEntersBossRoom);              // respects the stop unless told otherwise
        Assert.Null(Saved(vm).WalkToEntersBossRoom);

        vm = Editor();
        vm.IsTriggerLogon = true;
        vm.IsActionWalkTo = true;
        vm.WalkToText = "1/1810";
        vm.WalkToEntersBossRoom = true;
        vm.IsThenWalkTo = true;
        vm.ThenWalkToText = "1/1810";
        vm.ThenWalkToEntersBossRoom = true;
        ScheduledEvent saved = Saved(vm);
        Assert.True(saved.WalkToEntersBossRoom);
        Assert.True(saved.ThenWalkToEntersBossRoom);

        EventEditDialogViewModel reopened = Editor(saved);
        Assert.True(reopened.WalkToEntersBossRoom);
        Assert.True(reopened.ThenWalkToEntersBossRoom);
    }

    // The tick only means something for a stop-before boss room, so it isn't kept
    // for any other.
    [Fact]
    public void TheChoice_IsNotSaved_ForARoomWithNoStop()
    {
        EventEditDialogViewModel vm = Editor();
        vm.IsTriggerLogon = true;
        vm.IsActionWalkTo = true;
        vm.WalkToText = "1/1810";
        vm.WalkToEntersBossRoom = true;
        vm.WalkToText = "1/297";

        Assert.Null(Saved(vm).WalkToEntersBossRoom);
    }

    [Fact]
    public void WithNoMapLoaded_NothingIsSuggested()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true);
        Assert.Empty(vm.SuggestRooms("cave"));
    }
}

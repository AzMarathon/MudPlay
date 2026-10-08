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

    private EventEditDialogViewModel Editor(ScheduledEvent? existing = null) =>
        new(existing ?? new ScheduledEvent(), isNew: existing is null, search: NewSearch());

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
        ScheduledEvent? saved = null;
        vm.CloseRequested += e => saved = e;
        vm.SaveCommand.Execute(null);

        Assert.Equal((1, 1810), (saved!.WalkToTarget!.Map, saved.WalkToTarget.Room));
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

    [Fact]
    public void WithNoMapLoaded_NothingIsSuggested()
    {
        EventEditDialogViewModel vm = new(new ScheduledEvent(), isNew: true);
        Assert.Empty(vm.SuggestRooms("cave"));
    }
}

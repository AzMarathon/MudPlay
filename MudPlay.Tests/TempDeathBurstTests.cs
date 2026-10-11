using MudPlay.Game.Combat;
using Xunit;

namespace MudPlay.Tests;

// A room holds one monster with a temp death spell at a time (user, 2026-10-10), so
// the kills of one round of our room spell get one response between them, whether
// the room listed one kind or several.
public sealed class TempDeathBurstTests
{
    private const int Lich = 501;        // has a temp death spell
    private const int Zombie = 502;      // has none
    private const int Skeleton = 503;    // has none

    private static TempDeathBurst Burst() => new(number => number == Lich);

    private static MonsterDeathEvent RoomSpellKill(params int[] listedKinds)
        => new(Array.Empty<MonsterDeathIdentity>(), ExperienceGained: 100, DateTimeOffset.Now, IsFallback: true,
            RoomSpellRoster: listedKinds.Select(k => new MonsterDeathIdentity(k, $"monster {k}")).ToArray());

    private static MonsterDeathEvent NamedKill()
        => new(Array.Empty<MonsterDeathIdentity>(), ExperienceGained: 100, DateTimeOffset.Now, IsFallback: true);

    private static RoomEntitiesObservation Room(RoomObservationSource source, params int[] kinds)
        => new("Also here: …",
            kinds.Select(k => new RoomEntity($"monster {k}", $"monster {k}", EntityKind.Monster, k)).ToArray(),
            DateTimeOffset.Now, source);

    [Fact]
    public void SeveralKillsOfOneKindWithTheSpell_GetOneResponse()
    {
        TempDeathBurst burst = Burst();
        MonsterDeathEvent kill = RoomSpellKill(Lich);

        Assert.Equal(Lich, burst.KindToAnswer(kill, new[] { Lich }));
        Assert.Null(burst.KindToAnswer(kill, new[] { Lich }));
        Assert.Null(burst.KindToAnswer(kill, new[] { Lich }));
    }

    [Fact]
    public void MixedRoomHoldingThatKind_GetsOneResponse()
    {
        TempDeathBurst burst = Burst();
        MonsterDeathEvent kill = RoomSpellKill(Zombie, Lich, Skeleton);
        int[] listed = { Zombie, Lich, Skeleton };

        Assert.Equal(Lich, burst.KindToAnswer(kill, listed));
        Assert.Null(burst.KindToAnswer(kill, listed));
        Assert.Null(burst.KindToAnswer(kill, listed));
    }

    [Fact]
    public void MixedRoomWithoutThatKind_GetsNone()
    {
        TempDeathBurst burst = Burst();
        MonsterDeathEvent kill = RoomSpellKill(Zombie, Skeleton);

        Assert.Null(burst.KindToAnswer(kill, new[] { Zombie, Skeleton }));
        Assert.Null(burst.KindToAnswer(kill, new[] { Zombie, Skeleton }));
    }

    // A death that names its monster is one stall each time, as it always was.
    [Fact]
    public void SingleTargetKill_IsAnsweredEveryTime()
    {
        TempDeathBurst burst = Burst();

        Assert.Equal(Lich, burst.KindToAnswer(NamedKill(), new[] { Lich }));
        Assert.Equal(Lich, burst.KindToAnswer(NamedKill(), new[] { Lich }));
        Assert.Null(burst.KindToAnswer(NamedKill(), new[] { Zombie }));
    }

    [Fact]
    public void SingleTargetKill_IsAnswered_AfterARoomSpellKillWas()
    {
        TempDeathBurst burst = Burst();
        burst.KindToAnswer(RoomSpellKill(Lich, Zombie), new[] { Lich, Zombie });

        Assert.Equal(Lich, burst.KindToAnswer(NamedKill(), new[] { Lich }));
    }

    // The next round's kills are a new occasion only once the room was read again
    // and lists the kind still: the round before killed something else.
    [Fact]
    public void NextRoundsKills_AreAnswered_OnlyAfterARereadListsTheKindAgain()
    {
        TempDeathBurst burst = Burst();
        int[] listed = { Zombie, Lich };
        Assert.Equal(Lich, burst.KindToAnswer(RoomSpellKill(listed), listed));

        Assert.Null(burst.KindToAnswer(RoomSpellKill(listed), listed));

        burst.NoteRoomListed(Room(RoomObservationSource.AlsoHere, Lich));
        Assert.Equal(Lich, burst.KindToAnswer(RoomSpellKill(Lich), new[] { Lich }));
    }

    // A monster walking in mid-round re-lists the room without reading it: the
    // kills that follow belong to the round already answered.
    [Fact]
    public void AnArrivalWhileTheKindIsStillListed_IsNoNewOccasion()
    {
        TempDeathBurst burst = Burst();
        int[] listed = { Zombie, Lich };
        burst.KindToAnswer(RoomSpellKill(listed), listed);

        burst.NoteRoomListed(Room(RoomObservationSource.Arrival, Zombie, Lich, Skeleton));

        Assert.Null(burst.KindToAnswer(RoomSpellKill(listed), listed));
    }

    // An emptied room is drawn with no "Also here:" line. The kind walking back in
    // is listed by its arrival alone, and is another monster.
    [Fact]
    public void TheKindArrivingAfterTheRoomWasEmptied_IsANewOccasion()
    {
        TempDeathBurst burst = Burst();
        burst.KindToAnswer(RoomSpellKill(Lich), new[] { Lich });

        burst.NoteRoomListed(Room(RoomObservationSource.Death));
        burst.NoteRoomListed(Room(RoomObservationSource.Arrival, Lich));

        Assert.Equal(Lich, burst.KindToAnswer(RoomSpellKill(Lich), new[] { Lich }));
    }
}

using MudPlay.Models.GameData;
using MudPlay.ViewModels.GameData.Batch;
using Xunit;

namespace MudPlay.Tests;

public sealed class BatchEditTests
{
    // ----- Monsters -----

    [Fact]
    public void Monster_OnlyChosenFieldsApplied_OthersPreserved()
    {
        var existing = new MonsterOverlay
        {
            Relationship = MonsterRelationship.Enemy,
            Priority = MonsterAttackPriority.High,
            DontBackstab = true,
        };
        var changes = new MonsterBatchChanges
        {
            ChangeRelationship = true,
            Relationship = MonsterRelationship.Flee,
            // Priority NOT chosen; DontBackstab left.
        };
        MonsterOverlay r = changes.ApplyTo(existing);
        Assert.Equal(MonsterRelationship.Flee, r.Relationship);   // changed
        Assert.Equal(MonsterAttackPriority.High, r.Priority);     // preserved
        Assert.Equal(true, r.DontBackstab);                       // preserved (Leave)
    }

    [Fact]
    public void Monster_TriStateOffSetsFalse_OnSetsTrue()
    {
        var on = new MonsterBatchChanges { KillOnSight = BatchToggle.On }.ApplyTo(new MonsterOverlay());
        Assert.Equal(true, on.KillOnSight);
        var off = new MonsterBatchChanges { DontBackstab = BatchToggle.Off }
            .ApplyTo(new MonsterOverlay { DontBackstab = true });
        Assert.Equal(false, off.DontBackstab);
    }

    [Fact]
    public void Monster_SpellRung_SetAndClear()
    {
        var set = new MonsterBatchChanges
        {
            ChangeNormalAttack = true, NormalSpellId = 42, NormalCount = 3, NormalMinMana = 25,
        }.ApplyTo(new MonsterOverlay());
        Assert.Equal(42, set.OverrideAttackSpellId);
        Assert.Equal(3, set.OverrideAttackCount);
        Assert.Equal(25, set.OverrideAttackMinMana);

        // Blank cast-code (null id) clears the rung's cap + mana too.
        var cleared = new MonsterBatchChanges { ChangeNormalAttack = true, NormalSpellId = null, NormalCount = 5, NormalMinMana = 9 }
            .ApplyTo(new MonsterOverlay { OverrideAttackSpellId = 7, OverrideAttackCount = 2 });
        Assert.Null(cleared.OverrideAttackSpellId);
        Assert.Null(cleared.OverrideAttackCount);
        Assert.Null(cleared.OverrideAttackMinMana);
    }

    [Fact]
    public void Monster_NothingChosen_IsNoOp()
    {
        Assert.False(new MonsterBatchChanges().AnyFieldChosen);
    }

    // ----- Items -----

    [Fact]
    public void Item_TriStateFlags_PreserveUntouched()
    {
        var existing = new ItemOverlay { AutoCollect = true, AutoStash = true };
        var changes = new ItemBatchChanges
        {
            AutoDiscard = BatchToggle.On,   // set
            AutoStash = BatchToggle.Off,    // clear
            // AutoCollect left → preserved
        };
        ItemOverlay r = changes.ApplyTo(existing);
        Assert.Equal(true, r.AutoCollect);   // preserved
        Assert.Equal(true, r.AutoDiscard);   // set
        Assert.Equal(false, r.AutoStash);    // cleared
    }

    [Fact]
    public void Item_MinMax_ChangeOptIn()
    {
        var r = new ItemBatchChanges { ChangeMaxToGet = true, MaxToGet = "10" }
            .ApplyTo(new ItemOverlay { MinToKeep = "2" });
        Assert.Equal("2", r.MinToKeep);   // preserved (not chosen)
        Assert.Equal("10", r.MaxToGet);   // set
    }

    // ----- Players -----

    [Fact]
    public void Player_GrantAndRevoke_LeaveOthersIntact()
    {
        var existing = new PlayerCustomization
        {
            RemoteControls = PlayerRemoteControls.MovePlayer | PlayerRemoteControls.QueryLocation,
        };
        var changes = new PlayerBatchChanges
        {
            Permissions = new (PlayerRemoteControls, BatchToggle)[]
            {
                (PlayerRemoteControls.QueryItemLocation, BatchToggle.On),   // grant
                (PlayerRemoteControls.MovePlayer, BatchToggle.Off),         // revoke
                (PlayerRemoteControls.QueryLocation, BatchToggle.Leave),    // keep
            },
        };
        PlayerCustomization r = changes.ApplyTo(existing);
        Assert.True(r.RemoteControls.HasFlag(PlayerRemoteControls.QueryItemLocation));   // granted
        Assert.False(r.RemoteControls.HasFlag(PlayerRemoteControls.MovePlayer));         // revoked
        Assert.True(r.RemoteControls.HasFlag(PlayerRemoteControls.QueryLocation));       // preserved
    }

    [Fact]
    public void Player_BehaviourTriState()
    {
        var r = new PlayerBatchChanges { DontAutoDelete = BatchToggle.On }
            .ApplyTo(new PlayerCustomization());
        Assert.True(r.DontAutoDelete);
    }
}

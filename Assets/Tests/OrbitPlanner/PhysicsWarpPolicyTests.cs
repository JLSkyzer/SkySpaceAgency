using K2D2.Node;
using NUnit.Framework;

public class PhysicsWarpPolicyTests
{
    // A long aligned burn in physics mode, on a RigidBody vessel, with a high cap: x4 is wanted.
    static WarpInputs Burning(int index, bool physics = true)
    {
        return new WarpInputs
        {
            CurrentIndex = index,
            PhysicsModeOn = physics,
            VesselRigidBody = true,
            CapRate = 10,
            Aligned = true,
            BurnLeftGameSeconds = 300,
            AllowRaise = true,
            PlayerModeWasPhysics = false,
        };
    }

    [Test]
    public void RailsIndexWhileThrusting_ForcedToZero()
    {
        // x10 is rails warp whatever the mode says: down to 1x at once.
        WarpStep step = PhysicsWarpPolicy.Decide(Burning(3));
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        Assert.That(step.ForcedDrop, Is.True);
        Assert.That(step.Reason, Does.Contain("rails"));

        // x2 or x4 without physics mode is rails warp too.
        step = PhysicsWarpPolicy.Decide(Burning(2, physics: false));
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        Assert.That(step.ForcedDrop, Is.True);
        Assert.That(step.Reason, Does.Contain("physics mode off"));
    }

    [Test]
    public void EnterOrder_ModeAtZeroThenRaise()
    {
        // At 1x without physics mode: switch the mode on, stay at index 0 this frame.
        WarpStep step = PhysicsWarpPolicy.Decide(Burning(0, physics: false));
        Assert.That(step.SetPhysicsMode, Is.EqualTo((bool?)true));
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        Assert.That(step.ForcedDrop, Is.False);

        // Next frame, the mode reads back on: raise, no further mode change.
        step = PhysicsWarpPolicy.Decide(Burning(0, physics: true));
        Assert.That(step.SetPhysicsMode, Is.Null);
        Assert.That(step.WantedIndex, Is.EqualTo(2));
    }

    [Test]
    public void LeaveOrder_ZeroThenModeOff()
    {
        // Still warped: index 0 first, the mode is left alone.
        WarpStep step = PhysicsWarpPolicy.DecideLeave(Burning(2));
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        Assert.That(step.SetPhysicsMode, Is.Null);

        // At index 0 with the mode still on (the player had it off): mode off.
        step = PhysicsWarpPolicy.DecideLeave(Burning(0));
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        Assert.That(step.SetPhysicsMode, Is.EqualTo((bool?)false));

        // The player had physics mode on before the burn: it is restored.
        WarpInputs player = Burning(0, physics: false);
        player.PlayerModeWasPhysics = true;
        step = PhysicsWarpPolicy.DecideLeave(player);
        Assert.That(step.SetPhysicsMode, Is.EqualTo((bool?)true));

        // Already as the player had it: nothing to do.
        step = PhysicsWarpPolicy.DecideLeave(Burning(0, physics: false));
        Assert.That(step.SetPhysicsMode, Is.Null);
    }

    [Test]
    public void PhysicsRefused_StaysAtOne()
    {
        // The game ignored the toggle: the mode still reads off at index 0. Never raise.
        for (int frame = 0; frame < 5; frame++)
        {
            WarpStep step = PhysicsWarpPolicy.Decide(Burning(0, physics: false));
            Assert.That(step.WantedIndex, Is.EqualTo(0));
        }
    }

    [Test]
    public void NotRigidBody_ForcedDrop()
    {
        WarpInputs i = Burning(1);
        i.VesselRigidBody = false;
        WarpStep step = PhysicsWarpPolicy.Decide(i);
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        Assert.That(step.ForcedDrop, Is.True);
        Assert.That(step.Reason, Does.Contain("RigidBody"));

        // At 1x already: nothing to drop, and no raise.
        i.CurrentIndex = 0;
        step = PhysicsWarpPolicy.Decide(i);
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        Assert.That(step.ForcedDrop, Is.False);
        Assert.That(step.SetPhysicsMode, Is.Null);
    }

    [Test]
    public void CapRespected_MaxIndex2()
    {
        // A cap of x1000 still gives x4 (index 2) at most.
        WarpInputs i = Burning(0);
        i.CapRate = 1000;
        Assert.That(PhysicsWarpPolicy.Decide(i).WantedIndex, Is.EqualTo(PhysicsWarpPolicy.MaxPhysicsIndex));
        Assert.That(PhysicsWarpPolicy.MaxPhysicsIndex, Is.EqualTo(2));

        // A cap of x3 (BurnWarp 5 % / 10 s rules) gives x2.
        i.CapRate = 3;
        Assert.That(PhysicsWarpPolicy.Decide(i).WantedIndex, Is.EqualTo(1));

        // A cap of x1 lowers a x4 warp to 1x, instantly but not as a forced drop.
        i = Burning(2);
        i.CapRate = 1;
        WarpStep step = PhysicsWarpPolicy.Decide(i);
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        Assert.That(step.ForcedDrop, Is.False);
    }

    [Test]
    public void UnderThirtySeconds_X2Max()
    {
        WarpInputs i = Burning(2);
        i.BurnLeftGameSeconds = 25;
        Assert.That(PhysicsWarpPolicy.Decide(i).WantedIndex, Is.EqualTo(1));

        i.BurnLeftGameSeconds = PhysicsWarpPolicy.X4MinBurnLeft;
        Assert.That(PhysicsWarpPolicy.Decide(i).WantedIndex, Is.EqualTo(2));
    }

    [Test]
    public void NotAligned_One()
    {
        WarpInputs i = Burning(2);
        i.Aligned = false;
        WarpStep step = PhysicsWarpPolicy.Decide(i);
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        Assert.That(step.Reason, Does.Contain("not aligned"));

        // And no physics mode is switched on for it.
        i = Burning(0, physics: false);
        i.Aligned = false;
        Assert.That(PhysicsWarpPolicy.Decide(i).SetPhysicsMode, Is.Null);
    }

    [Test]
    public void LeaveAtOne_OurToggleAlwaysOff()
    {
        // The burn ended between "mode on" and the raise, engines still pushing: our toggle is
        // switched off at 1x (the game re-derives any physics it forces on its next SetRateIndex).
        WarpInputs i = Burning(0, physics: true);
        WarpStep step = PhysicsWarpPolicy.DecideLeave(i);
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        Assert.That(step.SetPhysicsMode, Is.EqualTo((bool?)false));

        // Unless the player had toggled it on before the burn.
        i.PlayerModeWasPhysics = true;
        Assert.That(PhysicsWarpPolicy.DecideLeave(i).SetPhysicsMode, Is.Null);
    }

    [Test]
    public void PlayerMode_OnlyTheToggleAtOne()
    {
        // At 1x the mode is the player's own toggle.
        Assert.That(PhysicsWarpPolicy.PlayerToggledPhysics(0, true), Is.True);
        Assert.That(PhysicsWarpPolicy.PlayerToggledPhysics(0, false), Is.False);
        // Above 1x it may be physics the game forced (atmosphere): never recorded as the player's.
        Assert.That(PhysicsWarpPolicy.PlayerToggledPhysics(1, true), Is.False);
        Assert.That(PhysicsWarpPolicy.PlayerToggledPhysics(2, true), Is.False);
    }

    [Test]
    public void AllowRaiseFalse_OnlyLowers()
    {
        // The player's 1x stays 1x, and the mode is not touched.
        WarpInputs i = Burning(0, physics: false);
        i.AllowRaise = false;
        WarpStep step = PhysicsWarpPolicy.Decide(i);
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        Assert.That(step.SetPhysicsMode, Is.Null);

        // The player's physics x2 is kept under a x4 cap...
        i = Burning(1);
        i.AllowRaise = false;
        Assert.That(PhysicsWarpPolicy.Decide(i).WantedIndex, Is.EqualTo(1));

        // ...and lowered under 30 s from x4.
        i = Burning(2);
        i.AllowRaise = false;
        i.BurnLeftGameSeconds = 20;
        Assert.That(PhysicsWarpPolicy.Decide(i).WantedIndex, Is.EqualTo(1));

        // The player's rails warp is still forced down.
        i = Burning(4, physics: false);
        i.AllowRaise = false;
        step = PhysicsWarpPolicy.Decide(i);
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        Assert.That(step.ForcedDrop, Is.True);
    }
}

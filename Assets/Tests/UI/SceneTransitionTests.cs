using K2D2;
using KSP.Game;
using NUnit.Framework;

public class SceneTransitionTests
{
    static SceneStep Next(bool wasInFlight, GameState? state, bool windowOpen = false, bool hidden = false, bool enabled = true) =>
        SceneTransition.Next(wasInFlight, enabled, state, windowOpen, hidden);

    [Test]
    public void StayingInFlight_NoReset()
    {
        var step = Next(true, GameState.FlightView, windowOpen: true);
        Assert.IsTrue(step.InFlight);
        Assert.IsTrue(step.WindowAllowed);
        Assert.IsNull(step.Reset);
        Assert.IsFalse(step.CloseWindow);
        Assert.IsFalse(step.OpenWindow);
    }

    [Test]
    public void LeavingFlight_ResetsOnce()
    {
        var first = Next(true, GameState.KerbalSpaceCenter);
        Assert.AreEqual(ResetCause.SceneChange, first.Reset);
        Assert.IsFalse(first.InFlight);
        // The frames after: already out of flight, no reset again.
        var second = Next(first.InFlight, GameState.KerbalSpaceCenter);
        Assert.IsNull(second.Reset);
    }

    [Test]
    public void PhotoMode_PilotsKeepFlying_WindowHiddenThenReopened()
    {
        var photo = Next(true, GameState.PhotoMode, windowOpen: true);
        Assert.IsTrue(photo.InFlight);
        Assert.IsNull(photo.Reset);
        Assert.IsFalse(photo.WindowAllowed);
        Assert.IsTrue(photo.CloseWindow);
        Assert.IsTrue(photo.HiddenByScene);

        var back = Next(photo.InFlight, GameState.FlightView, windowOpen: false, hidden: photo.HiddenByScene);
        Assert.IsNull(back.Reset);
        Assert.IsTrue(back.OpenWindow);
        Assert.IsFalse(back.HiddenByScene);
    }

    [Test]
    public void ClosedWindow_IsNotReopened()
    {
        var photo = Next(true, GameState.PhotoMode, windowOpen: false);
        Assert.IsFalse(photo.CloseWindow);
        Assert.IsFalse(photo.HiddenByScene);
        var back = Next(true, GameState.FlightView, windowOpen: false, hidden: photo.HiddenByScene);
        Assert.IsFalse(back.OpenWindow);
    }

    [Test]
    public void MapView_IsFlightWithTheWindow()
    {
        var step = Next(true, GameState.Map3DView, windowOpen: true);
        Assert.IsTrue(step.InFlight);
        Assert.IsTrue(step.WindowAllowed);
        Assert.IsFalse(step.CloseWindow);
    }

    [Test]
    public void GameNotSetUp_NotInFlight_ResetIfWeWere()
    {
        Assert.IsNull(Next(false, null).Reset);
        Assert.AreEqual(ResetCause.SceneChange, Next(true, null).Reset);
        Assert.IsFalse(Next(false, null).InFlight);
    }

    [Test]
    public void ModOff_NothingRuns_WindowClosedAndNotReopenedLater()
    {
        var off = Next(true, GameState.FlightView, windowOpen: true, hidden: true, enabled: false);
        Assert.AreEqual(ResetCause.ModDisabled, off.Reset);
        Assert.IsFalse(off.InFlight);
        Assert.IsFalse(off.WindowAllowed);
        Assert.IsTrue(off.CloseWindow);
        Assert.IsFalse(off.HiddenByScene);

        // Still off on the next frame: no second reset.
        Assert.IsNull(Next(off.InFlight, GameState.FlightView, enabled: false).Reset);
    }

    [Test]
    public void EnteringFlight_NoReset()
    {
        var step = Next(false, GameState.FlightView);
        Assert.IsTrue(step.InFlight);
        Assert.IsNull(step.Reset);
    }
}

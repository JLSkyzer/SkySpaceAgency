using K2D2;
using K2D2.Transfer;
using NUnit.Framework;

// Never rails warp while an engine still pushes (2026-10-25: +8.5 m/s nobody commanded).
public class WarpGateTests
{
    [Test]
    public void Timer_HoldsWhileAnEngineStillPushes()
    {
        var t = new EngineQuietTimer();
        for (int i = 0; i < 100; i++)
            Assert.IsFalse(t.Update(quiet: false, realTime: i * 0.02), "force or throttle: closed");
    }

    [Test]
    public void Timer_OpensAfterOneSecondAndTenFrames()
    {
        var t = new EngineQuietTimer();
        Assert.IsFalse(t.Update(true, 0));
        // Slow frames: 1 s reached after 4 frames, but 10 frames are needed.
        Assert.IsFalse(t.Update(true, 0.4));
        Assert.IsFalse(t.Update(true, 1.2));
        for (int i = 3; i < 9; i++)
            Assert.IsFalse(t.Update(true, 1.2 + i * 0.1), $"frame {i + 1}");
        Assert.IsTrue(t.Update(true, 2.2), "10 frames and more than 1 s");

        // Fast frames: 10 frames reached early, 1 s is needed.
        var f = new EngineQuietTimer();
        for (int i = 0; i < 40; i++)
            Assert.IsFalse(f.Update(true, i * 0.02), $"fast frame {i}");
        Assert.IsTrue(f.Update(true, 1.0));
    }

    [Test]
    public void Timer_AnyPushRestartsTheWait()
    {
        var t = new EngineQuietTimer();
        for (int i = 0; i <= 60; i++)
            t.Update(true, i * 0.02);
        Assert.IsTrue(t.Quiet(1.2));
        Assert.IsFalse(t.Update(false, 1.22), "a spool-down push");
        Assert.IsFalse(t.Update(true, 1.24), "the wait starts again");
    }

    [Test]
    public void Gate_RailsOnlyWithQuietEngines()
    {
        Assert.AreEqual(0, WarpGatePolicy.AllowedIndex(7, 0, physicsMode: false, enginesQuiet: false), "rails held at 1x");
        Assert.AreEqual(7, WarpGatePolicy.AllowedIndex(7, 0, false, true));
        Assert.AreEqual(2, WarpGatePolicy.AllowedIndex(2, 0, physicsMode: true, enginesQuiet: false), "physics warp is allowed in a burn");
        Assert.AreEqual(0, WarpGatePolicy.AllowedIndex(0, 7, false, false), "lowering always");
        Assert.AreEqual(3, WarpGatePolicy.AllowedIndex(3, 7, false, false), "lowering always");
    }

    [Test]
    public void Watchdog_DropsAGameBurnWeDidNotAskFor()
    {
        Assert.IsTrue(WarpGatePolicy.DropUnaskedRailsBurn(7, physicsMode: false, underThrustOnRails: true, railsBurnAsked: false));
        Assert.IsFalse(WarpGatePolicy.DropUnaskedRailsBurn(7, false, true, railsBurnAsked: true), "our own rails burn");
        Assert.IsFalse(WarpGatePolicy.DropUnaskedRailsBurn(7, false, false, false), "coasting on rails");
        Assert.IsFalse(WarpGatePolicy.DropUnaskedRailsBurn(2, physicsMode: true, true, false), "physics warp");
        Assert.IsFalse(WarpGatePolicy.DropUnaskedRailsBurn(0, false, true, false), "1x");
    }

    [Test]
    public void DeltaVAccounting_TheLoggedCaseIsAbout8Point5()
    {
        // Player-2026-10-25b.log: energy -33384260.976 -> -33329178.602 at about 6497 m/s.
        double dv = TransferFlight.EquivalentDeltaV(-33384260.976, -33329178.602, 6497);
        Assert.AreEqual(8.48, dv, 0.05);
        Assert.Greater(dv, TransferFlight.UncommandedDeltaV);
        Assert.Less(TransferFlight.EquivalentDeltaV(-33384260.976, -33384256.367, 6493), TransferFlight.UncommandedDeltaV, "the first switch: 0.0007 m/s");
    }
}

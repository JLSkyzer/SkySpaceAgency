using System;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using KSP.Sim;
using NUnit.Framework;

public class TerminalGuidanceTests
{
    const double MunG = 1.63;
    const double TouchDown = 2;
    const double SafeContact = TouchDown + 0.5;
    const double MaxContactDrift = 1.5;

    // 1500 m up, the target 800 m ahead, 40 m/s toward it (it would overshoot by ~500 m
    // without guidance), 60 m/s down, max accel 6 m/s².
    static readonly Vector3d NominalOffset = new Vector3d(800, 0, 1500);
    static readonly Vector3d NominalVelocity = new Vector3d(-40, 0, -60);

    static void AssertSafe(PointMassSim.Result r)
    {
        Assert.IsFalse(r.HasNaN, "NaN command");
        Assert.IsTrue(r.Landed, "landed: " + r);
        Assert.LessOrEqual(-r.FinalVerticalSpeed, SafeContact, "vertical speed at contact: " + r);
        // Hand-over: at most TerminalGuidance.GateSpeed of drift at FinalHeight.
        Assert.IsFalse(double.IsNaN(r.GateHorizontalSpeed), "crossed FinalHeight: " + r);
        Assert.LessOrEqual(r.GateHorizontalSpeed, TerminalGuidance.GateSpeed, "horizontal speed at FinalHeight: " + r);
        // 1.5 m/s at contact: half the gate speed, below the 2 m/s touch-down speed (legs take
        // the vertical part; a sideways slide on top of it is what tips a lander over).
        Assert.LessOrEqual(r.FinalHorizontalSpeed, MaxContactDrift, "horizontal speed at contact: " + r);
    }

    [Test]
    public void Nominal_LandsWithin20mOfTarget()
    {
        var r = PointMassSim.Run(NominalOffset, NominalVelocity, MunG, 6, TouchDown);
        TestContext.WriteLine("nominal: " + r);
        AssertSafe(r);
        Assert.LessOrEqual(r.FinalOffsetM, 20, r.ToString());
        Assert.LessOrEqual(r.MaxCommandTiltDeg, TerminalGuidance.DefaultMaxTiltDegrees + 1e-9, r.ToString());
        Assert.LessOrEqual(r.MaxCommandAccel, 6 + 1e-9, r.ToString());
        Assert.IsFalse(r.DroppedAboveGate, r.ToString());
        // Clean handover to FinalDescent at FinalHeight.
        Assert.LessOrEqual(r.GateOffsetM, 5, r.ToString());
        Assert.LessOrEqual(r.GateHorizontalSpeed, 3, r.ToString());
        Assert.LessOrEqual(-r.GateVerticalSpeed,
            DescentEnvelope.MaxSpeed(FinalDescent.FinalHeight, 6, MunG, TouchDown, double.MaxValue), r.ToString());
    }

    [Test]
    public void Nominal_WithAttitudeLag_LandsWithin50mOfTarget()
    {
        var r = PointMassSim.Run(NominalOffset, NominalVelocity, MunG, 6, TouchDown, attitudeLag: 1.5);
        TestContext.WriteLine("lag 1.5 s: " + r);
        AssertSafe(r);
        Assert.LessOrEqual(r.FinalOffsetM, 50, r.ToString());
        Assert.LessOrEqual(r.MaxCommandTiltDeg, TerminalGuidance.DefaultMaxTiltDegrees + 1e-9, r.ToString());
    }

    [Test]
    public void SmallerTiltLimit_IsNeverExceeded()
    {
        var r = PointMassSim.Run(NominalOffset, NominalVelocity, MunG, 6, TouchDown, maxTiltDeg: 20);
        TestContext.WriteLine("tilt 20°: " + r);
        AssertSafe(r);
        Assert.LessOrEqual(r.MaxCommandTiltDeg, 20 + 1e-9, r.ToString());
    }

    [Test]
    public void UnreachableOffset_DropsTargetAndLandsSafely()
    {
        var r = PointMassSim.Run(new Vector3d(3000, 0, 300), new Vector3d(0, 0, -20), MunG, 3, TouchDown);
        TestContext.WriteLine("3 km: " + r);
        AssertSafe(r);
        Assert.IsTrue(r.EverDropped, r.ToString());
        StringAssert.Contains("m from it", r.FirstDropReason);
        Assert.Less(r.FirstDropMissM, 3000, "reported residual distance");
        Assert.Greater(r.FirstDropMissM, 2800, "reported residual distance");
        Assert.LessOrEqual(r.MaxCommandTiltDeg, TerminalGuidance.DefaultMaxTiltDegrees + 1e-9, r.ToString());
        // Lands as close as it safely can. Bound: lateral room next to the braking thrust is
        // sqrt(3² − 2.59²) ≈ 1.51 m/s² for ~18 s before FinalHeight; rest to rest, bang-bang
        // covers at most 1.51·18²/4 ≈ 122 m. Half of it is kept for braking: expect 30–130 m.
        Assert.LessOrEqual(r.FinalOffsetM, 2970, r.ToString());
        Assert.GreaterOrEqual(r.FinalOffsetM, 3000 - 130, r.ToString());
    }

    [Test]
    public void UnreachableOffset_WithAttitudeLag_MovesCloserAndLandsSafely()
    {
        var r = PointMassSim.Run(new Vector3d(3000, 0, 300), new Vector3d(0, 0, -20), MunG, 3, TouchDown, attitudeLag: 1.5);
        TestContext.WriteLine("3 km, lag 1.5 s: " + r);
        AssertSafe(r);
        Assert.IsTrue(r.EverDropped, r.ToString());
        Assert.LessOrEqual(r.FinalOffsetM, 2970, r.ToString());
    }

    [Test]
    public void UnreachableOffset_Moving_WithAttitudeLag_StillLandsSafely()
    {
        // 30 m/s toward a target 3 km away, 600 m up. (From 300 m no law can hand over at
        // 3 m/s: max accel 3 leaves at most 2.12 m/s² lateral, 12.7 s to cancel 27 m/s, while
        // the descent from 25 m/s with at most 0.49 m/s² net braking covers 250 m in ~11 s.)
        var r = PointMassSim.Run(new Vector3d(3000, 0, 600), new Vector3d(-30, 0, -25), MunG, 3, TouchDown, attitudeLag: 1.5);
        TestContext.WriteLine("3 km, moving, lag 1.5 s: " + r);
        AssertSafe(r);
        Assert.IsTrue(r.EverDropped, r.ToString());
    }

    [Test]
    public void NoOffset_DescendsStraight()
    {
        var c = TerminalGuidance.Compute(new Vector3d(0, 0, 1000), new Vector3d(0, 0, -60), MunG, 6, TouchDown);
        Assert.AreEqual(0, c.Accel.x, 1e-12);
        Assert.AreEqual(0, c.Accel.y, 1e-12);
        Assert.IsFalse(c.TargetDropped);
        var r = PointMassSim.Run(new Vector3d(0, 0, 1500), new Vector3d(0, 0, -60), MunG, 6, TouchDown);
        TestContext.WriteLine("vertical only: " + r);
        AssertSafe(r);
        Assert.AreEqual(0, r.FinalOffsetM, 1e-9);
    }

    [Test]
    public void ThrustBelowGravity_DropsTargetWithFullThrustUp()
    {
        var c = TerminalGuidance.Compute(new Vector3d(500, 0, 1000), new Vector3d(10, 0, -30), MunG, 1.5, TouchDown);
        Assert.IsTrue(c.TargetDropped);
        Assert.IsNotNull(c.Reason);
        Assert.AreEqual(0, c.Accel.x, 0);
        Assert.AreEqual(0, c.Accel.y, 0);
        Assert.AreEqual(1.5, c.Accel.z, 1e-12);
    }

    [Test]
    public void CannotStopEvenWithFullThrust_BrakesStraightUp()
    {
        // 44 m/s down at 200 m: full thrust (6) stops from 41.8 m/s + 2 touch-down slack only.
        var c = TerminalGuidance.Compute(new Vector3d(300, 0, 200), new Vector3d(-20, 0, -44), MunG, 6, TouchDown);
        Assert.IsTrue(c.TargetDropped);
        StringAssert.Contains("Cannot stop before the ground", c.Reason);
        Assert.AreEqual(0, c.Accel.x, 0);
        Assert.AreEqual(0, c.Accel.y, 0);
        Assert.AreEqual(6, c.Accel.z, 1e-12);
    }

    [Test]
    public void BelowFinalHeight_OnlyNullsTheDrift()
    {
        // Target 30 m east, drifting north: lean against the drift, not toward the target.
        var c = TerminalGuidance.Compute(new Vector3d(-30, 0, 40), new Vector3d(0, 2, -10), MunG, 6, TouchDown);
        Assert.IsFalse(c.TargetDropped);
        Assert.AreEqual(0, c.Accel.x, 1e-12);
        Assert.Less(c.Accel.y, 0);
    }

    [Test]
    public void BadInputs_NeverGiveNaN()
    {
        double[] values = { double.NaN, double.PositiveInfinity, -1, 0, 1e-9, 1e9 };
        foreach (double x in values)
        {
            Check(TerminalGuidance.Compute(new Vector3d(x, 0, 1000), new Vector3d(0, 0, -30), MunG, 6, TouchDown));
            Check(TerminalGuidance.Compute(new Vector3d(100, 0, x), new Vector3d(0, 0, -30), MunG, 6, TouchDown));
            Check(TerminalGuidance.Compute(new Vector3d(100, 0, 1000), new Vector3d(0, x, -30), MunG, 6, TouchDown));
            Check(TerminalGuidance.Compute(new Vector3d(100, 0, 1000), new Vector3d(0, 0, x), MunG, 6, TouchDown));
            Check(TerminalGuidance.Compute(new Vector3d(100, 0, 1000), new Vector3d(0, 0, -30), x, 6, TouchDown));
            Check(TerminalGuidance.Compute(new Vector3d(100, 0, 1000), new Vector3d(0, 0, -30), MunG, x, TouchDown));
            Check(TerminalGuidance.Compute(new Vector3d(100, 0, 1000), new Vector3d(0, 0, -30), MunG, 6, x));
            Check(TerminalGuidance.Compute(new Vector3d(100, 0, 1000), new Vector3d(0, 0, -30), MunG, 6, TouchDown, x));
        }
    }

    static void Check(Command c)
    {
        Assert.IsFalse(double.IsNaN(c.Accel.x) || double.IsNaN(c.Accel.y) || double.IsNaN(c.Accel.z), "NaN accel");
        Assert.IsFalse(double.IsInfinity(c.Accel.x) || double.IsInfinity(c.Accel.y) || double.IsInfinity(c.Accel.z), "infinite accel");
        Assert.GreaterOrEqual(c.Accel.z, 0, "thrust pointing down");
    }
}

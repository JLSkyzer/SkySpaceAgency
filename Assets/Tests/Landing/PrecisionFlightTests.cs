using System;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using KSP.Sim;
using NUnit.Framework;

// The pure decisions LandingPilot makes while flying a precision landing: the local frame of the
// terminal guidance, the measured rotation sign, the phase decisions and the drop debounce.
public class PrecisionFlightTests
{
    const double Mu = 6.5138398e10;
    const double R = 200000;
    const double RotationPeriod = 138984.38;
    const double Deg = Math.PI / 180;
    static double W => BodyRotation.AngularSpeed(RotationPeriod);

    static SurfaceGeometry.Reference Ref(int lonSign) => new SurfaceGeometry.Reference
    {
        Position = new Vector3d(R, 0, 0), Latitude = 0, Longitude = 10, LonSign = lonSign,
    };

    // ---- TargetFrame ----

    [TestCase(1)]
    [TestCase(-1)]
    public void TargetFrame_IsOrthonormalAndEastIsGrowingLongitude(int lonSign)
    {
        var reference = Ref(lonSign);
        Vector3d dir = SurfaceGeometry.Direction(reference, 8, 120);
        TargetFrame f = TargetFrame.At(dir, R + 300, lonSign);

        Assert.AreEqual(1, f.East.magnitude, 1e-12);
        Assert.AreEqual(1, f.North.magnitude, 1e-12);
        Assert.AreEqual(0, Vector3d.Dot(f.East, f.North), 1e-12);
        Assert.AreEqual(0, Vector3d.Dot(f.East, f.Up), 1e-12);
        Vector3d towardEast = SurfaceGeometry.Direction(reference, 8, 120.001) - dir;
        Vector3d towardNorth = SurfaceGeometry.Direction(reference, 8.001, 120) - dir;
        Assert.Greater(Vector3d.Dot(towardEast.normalized, f.East), 0.9999);
        Assert.Greater(Vector3d.Dot(towardNorth.normalized, f.North), 0.9999);
    }

    [Test]
    public void TargetFrame_OffsetOfAPointEastAndAbove_AndRoundTrip()
    {
        Vector3d dir = SurfaceGeometry.Direction(Ref(1), -20, 45);
        TargetFrame f = TargetFrame.At(dir, R, 1);
        Vector3d p = f.Origin + f.East * 100 + f.Up * 500;

        Vector3d o = f.Offset(p);
        Assert.AreEqual(100, o.x, 1e-6);
        Assert.AreEqual(0, o.y, 1e-6);
        Assert.AreEqual(500, o.z, 1e-6);
        Vector3d v = new Vector3d(3, -4, 12);
        Vector3d back = f.FromLocal(f.ToLocal(v));
        Assert.AreEqual(0, (back - v).magnitude, 1e-9);
    }

    [Test]
    public void TargetFrame_AtThePole_StillOrthonormal()
    {
        TargetFrame f = TargetFrame.At(new Vector3d(0, 0, 1), R, 1);
        Assert.AreEqual(1, f.East.magnitude, 1e-12);
        Assert.AreEqual(1, f.North.magnitude, 1e-12);
        Assert.AreEqual(0, Vector3d.Dot(f.East, f.Up), 1e-12);
        Assert.AreEqual(0, Vector3d.Dot(f.North, f.Up), 1e-12);
    }

    // ---- RotationProbe ----

    // The game's longitude of the fixed inertial position p, dt seconds after the reference time,
    // on a body turning at omegaZ: the body-fixed point under p then sat at AtTime(p, ω, −dt).
    static double GameLon(SurfaceGeometry.Reference reference, Vector3d p, double omegaZ, double dt)
    {
        SurfaceGeometry.LatLon(reference, SurfaceGeometry.AtTime(p, new Vector3d(0, 0, omegaZ), -dt), out _, out double lon);
        return lon;
    }

    [TestCase(1, 1)]
    [TestCase(1, -1)]
    [TestCase(-1, 1)]
    [TestCase(-1, -1)]
    public void RotationProbe_FindsTheTrueSign(int trueSign, int lonSign)
    {
        var reference = Ref(lonSign);
        Vector3d p = new Vector3d(R * 0.6, R * 0.7, R * 0.2);
        double before = GameLon(reference, p, trueSign * W, 0);
        double after = GameLon(reference, p, trueSign * W, 2.5);

        int sign = RotationProbe.Decide(before, after, 2.5, lonSign, W, R, out string reason);

        Assert.AreEqual(trueSign, sign, reason);
    }

    [Test]
    public void RotationProbe_AcrossTheAntimeridian()
    {
        // p sits at lon 179.999 and drifts east by ~0.008° in 3 s: the second reading wraps to −179.99.
        var reference = new SurfaceGeometry.Reference { Position = new Vector3d(R, 0, 0), Latitude = 0, Longitude = 179.999, LonSign = 1 };
        Vector3d p = new Vector3d(R, 0, 0);
        double before = GameLon(reference, p, -W, 0);
        double after = GameLon(reference, p, -W, 3);
        Assert.Greater(Math.Abs(after - before), 300, "the readings straddle ±180");

        Assert.AreEqual(-1, RotationProbe.Decide(before, after, 3, 1, W, R, out _));
    }

    [Test]
    public void RotationProbe_NoDrift_IsNotMeasurable()
    {
        int sign = RotationProbe.Decide(12.5, 12.5, 2.5, 1, W, R, out string reason);
        Assert.AreEqual(0, sign);
        Assert.That(reason, Does.Contain("could not be measured"));
        Assert.AreEqual(0, RotationProbe.Decide(12.5, 13.5, 2.5, 1, W, R, out _), "far more drift than the rotation");
        Assert.AreEqual(0, RotationProbe.Decide(12.5, 12.6, 1, 1, W, R, out _), "readings too close in time");
    }

    [Test]
    public void RotationProbe_NegligibleRotation_KeepsPlusOne()
    {
        Assert.AreEqual(1, RotationProbe.Decide(1, 1, 2, 1, 1e-9, R, out string reason));
        Assert.That(reason, Does.Contain("negligible"));
    }

    // ---- PrecisionPhases ----

    [Test]
    public void Correction_FlownFrom5cmPerSecond()
    {
        Correction small = new Correction { Ok = true, DeltaV = new Vector3d(0.04, 0, 0) };
        Correction big = new Correction { Ok = true, DeltaV = new Vector3d(0, 0.06, 0) };
        Correction refused = new Correction { Ok = false, DeltaV = new Vector3d(0, 1, 0) };
        Assert.IsFalse(PrecisionPhases.ShouldFly(small));
        Assert.IsTrue(PrecisionPhases.ShouldFly(big));
        Assert.IsFalse(PrecisionPhases.ShouldFly(refused));
    }

    [Test]
    public void CorrectionPlan_HalfwayToTheBrake_MinusTheLead()
    {
        Assert.AreEqual(1000 + 500 - PrecisionPhases.PlanLead, PrecisionPhases.CorrectionPlanUT(1000, 2000), 1e-9);
        Assert.AreEqual(1000, PrecisionPhases.CorrectionPlanUT(1000, 1040), 1e-9);
        Assert.AreEqual(1000, PrecisionPhases.CorrectionPlanUT(1000, double.NaN), 1e-9);
    }

    [Test]
    public void PlanLead_CoversTheLongestAlignmentLeadPlusAMargin()
    {
        Assert.GreaterOrEqual(PrecisionPhases.PlanLead, K2D2.Node.BurnWarp.MaxLead + 10);
    }

    [Test]
    public void CorrectionBurn_EndsAtTheMinimumCorrection()
    {
        Assert.AreEqual(PrecisionPhases.MinCorrection, PrecisionPhases.CorrectionDoneDeltaV);
    }

    [Test]
    public void EarliestDeorbit_LeavesTheLeadAndHalfTheBurn_AfterThePlaneChange()
    {
        Assert.AreEqual(1000 + PrecisionPhases.PlanLead + 5, PrecisionPhases.EarliestDeorbitUT(1000, double.NaN, 10), 1e-9);
        Assert.AreEqual(1500, PrecisionPhases.EarliestDeorbitUT(1000, 1500, 10), 1e-9);
        Assert.AreEqual(1000 + PrecisionPhases.PlanLead, PrecisionPhases.EarliestDeorbitUT(1000, 900, double.NaN), 1e-9);
    }

    [Test]
    public void BrakeThrottle_PlannedFraction_FullWhenTheSimulationCannotStop()
    {
        Assert.AreEqual(DescentEnvelope.ThrustFraction, PrecisionPhases.BrakeThrottle(null), 1e-6);
        Assert.AreEqual(DescentEnvelope.ThrustFraction,
            PrecisionPhases.BrakeThrottle(new BrakeResult { Status = BrakeStatus.Ok, StopAltitude = 1500 }), 1e-6);
        Assert.AreEqual(DescentEnvelope.ThrustFraction,
            PrecisionPhases.BrakeThrottle(new BrakeResult { Status = BrakeStatus.TooLate, StopAltitude = 900 }), 1e-6,
            "below the hand-over but safe: keep the plan");
        Assert.AreEqual(1, PrecisionPhases.BrakeThrottle(new BrakeResult { Status = BrakeStatus.TooLate, StopAltitude = 20 }), 1e-6);
        Assert.AreEqual(1, PrecisionPhases.BrakeThrottle(new BrakeResult { Status = BrakeStatus.Impossible }), 1e-6);
    }

    [Test]
    public void BrakeHandOver_WhenStoppedOrFarBelowTheHandOverHeight()
    {
        // accel 5 m/s²: stop test 0.85 × 5 × 0.5 = 2.125 m/s.
        Assert.IsTrue(PrecisionPhases.BrakeHandOver(2.1, 5, 1500, 1500));
        Assert.IsFalse(PrecisionPhases.BrakeHandOver(2.2, 5, 1500, 1500));
        Assert.IsTrue(PrecisionPhases.BrakeHandOver(1.9, 0.1, 1500, 1500), "floor at MinStopSpeed");
        Assert.IsTrue(PrecisionPhases.BrakeHandOver(80, 5, 700, 1500), "forced below half the hand-over height");
        Assert.IsTrue(PrecisionPhases.IsLateHandOver(1100, 1500));
        Assert.IsFalse(PrecisionPhases.IsLateHandOver(1400, 1500));
    }

    [Test]
    public void DeltaVShort_WithTheMargin_UnknownNeverShort()
    {
        Assert.IsTrue(PrecisionPhases.DeltaVShort(100, 95));
        Assert.IsFalse(PrecisionPhases.DeltaVShort(110, 95));
        Assert.IsFalse(PrecisionPhases.DeltaVShort(double.NaN, 95));
        Assert.IsFalse(PrecisionPhases.DeltaVShort(0, 95));
    }

    [Test]
    public void GiveUp_AimsAtTheDriftStopPoint()
    {
        Vector3d offset = new Vector3d(800, -300, 1200);
        Vector3d velocity = new Vector3d(-10, 5, -3);
        Vector3d o = PrecisionPhases.GiveUpOffset(offset, velocity);
        double t = TerminalGuidance.DriftNullTime;
        Assert.AreEqual(10 * t, o.x, 1e-12);
        Assert.AreEqual(-5 * t, o.y, 1e-12);
        Assert.AreEqual(1200, o.z, 1e-12);
        Assert.AreEqual(Math.Sqrt(Math.Pow(800 - 10 * t, 2) + Math.Pow(-300 + 5 * t, 2)),
            PrecisionPhases.DriftStopDistance(offset, velocity), 1e-9);
    }

    [Test]
    public void Messages_HaveTheBriefsText()
    {
        Assert.AreEqual("Target out of reach: landing as close as possible (412 m away).", PrecisionPhases.OutOfReachMessage(411.6));
        Assert.AreEqual("Landed 37 m from the target.", PrecisionPhases.LandedMessage(37.2));
        Assert.AreEqual("Target on a 18° slope.", PrecisionPhases.SlopeMessage(17.8));
        Assert.AreEqual("Periapsis above 100 km: lower the orbit first (Orbit tab).", PrecisionStart.PeriapsisTooHighMessage);
    }

    // ---- DropDebounce ----

    [Test]
    public void DropDebounce_ShowsOnlyAfterTwoSecondsOfDrops_NeverLatched()
    {
        var d = new DropDebounce();
        Assert.IsFalse(d.Update(true, 10));
        Assert.IsFalse(d.Update(true, 11.9));
        Assert.IsTrue(d.Update(true, 12.0));
        Assert.IsFalse(d.Update(false, 12.1), "one call keeping the target resets it");
        Assert.IsFalse(d.Update(true, 12.2));
        Assert.IsFalse(d.Update(true, 14.1));
        Assert.IsTrue(d.Update(true, 14.3));
    }
}

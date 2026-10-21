using System;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// Final review fixes of the vacuum precision landing: what a failed burn does once the vessel may
// be on an impact course (PrecisionFailurePolicy), the terrain clearance of the circular orbit
// the approach starts from (PrecisionStart.Clearance), and the rotation sign cross-check.
public class PrecisionSafetyTests
{
    const double Mu = 6.5138398e10;
    const double R = 200000;
    const double RotationPeriod = 138984.38;
    const double UT = 1000;
    const double Deg = Math.PI / 180;
    static double G => Mu / (R * R);
    static Vector3d Omega => BodyRotation.AngularVelocity(BodyRotation.AngularSpeed(RotationPeriod), 1);
    static Body Flat => new Body { Mu = Mu, Radius = R, Omega = Omega, TerrainHeight = p => 0 };

    static StartInput Input(Vector3d r, Vector3d v, Body body) => new StartInput
    {
        R = r, V = v, UT = UT, Body = body,
        Vehicle = new Vehicle { ThrustKN = 3 * 10 * G, MassT = 10, Isp = 320, StopHeight = 1500 },
        TargetDirNow = new Vector3d(0, 1, 0), TouchDownSpeed = 2, MaxTiltDeg = 40,
    };

    static Vector3d Circular(double r0, out Vector3d v)
    {
        v = new Vector3d(0, Math.Sqrt(Mu / r0), 0);
        return new Vector3d(r0, 0, 0);
    }

    // ---- PrecisionFailurePolicy ----

    [TestCase(PrecisionStage.OrbitPrep, false, FailureAction.Stop)]
    [TestCase(PrecisionStage.PlaneChange, false, FailureAction.Stop)]
    [TestCase(PrecisionStage.Deorbit, false, FailureAction.Stop)]
    [TestCase(PrecisionStage.Deorbit, true, FailureAction.FallBackNoTarget)]
    [TestCase(PrecisionStage.MidCourse, true, FailureAction.Skip)]
    [TestCase(PrecisionStage.MidCourse, false, FailureAction.Skip)]
    [TestCase(PrecisionStage.OrbitPrep, true, FailureAction.FallBackNoTarget)]
    [TestCase(PrecisionStage.PlaneChange, true, FailureAction.FallBackNoTarget)]
    public void FailurePolicy_NeverStopsOnAnImpactCourse(PrecisionStage stage, bool hitsGround, FailureAction expected)
    {
        Assert.AreEqual(expected, PrecisionFailurePolicy.Decide(stage, hitsGround));
    }

    [Test]
    public void FailurePolicy_HitsGroundFromTheCollisionOrThePeriapsis()
    {
        Assert.IsTrue(PrecisionFailurePolicy.OrbitHitsGround(R + 30000, R, collisionPredicted: true));
        Assert.IsTrue(PrecisionFailurePolicy.OrbitHitsGround(R - 1, R, collisionPredicted: false));
        Assert.IsFalse(PrecisionFailurePolicy.OrbitHitsGround(R + 30000, R, collisionPredicted: false));
        Assert.IsFalse(PrecisionFailurePolicy.OrbitHitsGround(double.NaN, R, collisionPredicted: false));
    }

    [Test]
    public void FailurePolicy_Messages()
    {
        Assert.AreEqual("Precision landing aborted: deorbit: Burn diverging. Landing without target.",
            PrecisionFailurePolicy.FallBackMessage("deorbit: Burn diverging."));
        Assert.AreEqual("Course correction skipped: mid-course correction: Could not align.",
            PrecisionFailurePolicy.SkipMessage("mid-course correction: Could not align."));
    }

    // ---- Terrain clearance ----

    [Test]
    public void Clearance_FlatTerrain_IsTheFloorFromAtLeast72Samples()
    {
        int calls = 0;
        Body body = Flat;
        body.TerrainHeight = p => { calls++; return 0; };
        Vector3d r = Circular(R + 30000, out Vector3d v);
        OrbitClearance c = PrecisionStart.Clearance(r, Vector3d.Cross(r, v), R + 30000, 0, body);
        Assert.IsTrue(c.TerrainRead, c.Note);
        Assert.AreEqual(PrecisionStart.MinClearance, c.RequiredM, 1e-9);
        Assert.AreEqual(0, c.HighestTerrainM, 1e-9);
        Assert.GreaterOrEqual(c.Samples, 72);
        Assert.AreEqual(c.Samples, calls);
    }

    [Test]
    public void Clearance_AMountainOnTheTrack_RaisesIt()
    {
        // A 12 km mountain cap around −x: half an orbit ahead on the equatorial track.
        Body body = Flat;
        body.TerrainHeight = p => p.x < -0.99 * p.magnitude ? 12000 : 0;
        Vector3d r = Circular(R + 30000, out Vector3d v);
        OrbitClearance c = PrecisionStart.Clearance(r, Vector3d.Cross(r, v), R + 30000, 0, body);
        Assert.IsTrue(c.TerrainRead, c.Note);
        Assert.AreEqual(12000, c.HighestTerrainM, 1e-9);
        Assert.AreEqual(12000 + PrecisionStart.TerrainMargin, c.RequiredM, 1e-9);

        // A mountain off the track (the pole) changes nothing.
        body.TerrainHeight = p => p.z > 0.9 * p.magnitude ? 12000 : 0;
        Assert.AreEqual(PrecisionStart.MinClearance, PrecisionStart.Clearance(r, Vector3d.Cross(r, v), R + 30000, 0, body).RequiredM, 1e-9);
    }

    [Test]
    public void Clearance_SamplesTheGroundTrackOfTheTurningBody()
    {
        // Polar orbit through +x and +z. In the orientation at UT its plane never passes +y, but
        // a body turning at half the orbital rate brings the body-fixed point +y under the
        // vessel half an orbit later (n·t = 180°, ω·t = 90°).
        double r0 = R + 30000;
        double n = Math.Sqrt(Mu / (r0 * r0 * r0));
        Body body = Flat;
        body.Omega = new Vector3d(0, 0, n / 2);
        body.TerrainHeight = p => p.y > 0.995 * p.magnitude ? 9000 : 0;
        Vector3d r = new Vector3d(r0, 0, 0);
        Vector3d v = new Vector3d(0, 0, Math.Sqrt(Mu / r0));
        OrbitClearance c = PrecisionStart.Clearance(r, Vector3d.Cross(r, v), r0, 0, body);
        Assert.IsTrue(c.TerrainRead, c.Note);
        Assert.AreEqual(9000, c.HighestTerrainM, 1e-9);

        // Without the rotation the track misses it.
        body.Omega = new Vector3d(0, 0, 0);
        Assert.AreEqual(0, PrecisionStart.Clearance(r, Vector3d.Cross(r, v), r0, 0, body).HighestTerrainM, 1e-9);
    }

    [Test]
    public void Clearance_UnreadableTerrain_KeepsTheFloorAlone()
    {
        Body body = Flat;
        body.TerrainHeight = p => throw new InvalidOperationException("far from the vessel");
        Vector3d r = Circular(R + 30000, out Vector3d v);
        OrbitClearance c = PrecisionStart.Clearance(r, Vector3d.Cross(r, v), R + 30000, 0, body);
        Assert.IsFalse(c.TerrainRead);
        Assert.AreEqual(PrecisionStart.MinClearance, c.RequiredM, 1e-9);
        StringAssert.Contains("far from the vessel", c.Note);

        body.TerrainHeight = p => double.NaN;
        c = PrecisionStart.Clearance(r, Vector3d.Cross(r, v), R + 30000, 0, body);
        Assert.IsFalse(c.TerrainRead);
        Assert.AreEqual(PrecisionStart.MinClearance, c.RequiredM, 1e-9);
    }

    [Test]
    public void Plan_CircularOrbitAt4Km_IsRefused()
    {
        Vector3d r = Circular(R + 4000, out Vector3d v);
        StartPlan plan = PrecisionStart.Plan(Input(r, v, Flat));
        Assert.IsFalse(plan.Ok);
        Assert.AreEqual("Orbit too low for a safe precision approach (Pe 4.0 km, terrain up to 0.0 km): raise it with the Orbit tab.",
            plan.Reason);
    }

    [Test]
    public void Plan_EccentricOrbit30x4Km_IsRefusedBeforeThePreparation()
    {
        double rp = R + 4000, ra = R + 30000, a = (ra + rp) / 2;
        StartPlan plan = PrecisionStart.Plan(Input(new Vector3d(ra, 0, 0),
            new Vector3d(0, Math.Sqrt(Mu * (2 / ra - 1 / a)), 0), Flat));
        Assert.IsFalse(plan.Ok);
        StringAssert.StartsWith("Orbit too low for a safe precision approach (Pe 4.0 km", plan.Reason);
    }

    [Test]
    public void Plan_MountainAboveTheOrbitMargin_IsRefused()
    {
        Body body = Flat;
        body.TerrainHeight = p => p.x < -0.99 * p.magnitude ? 14000 : 0;
        Vector3d r = Circular(R + 15000, out Vector3d v);
        StartPlan plan = PrecisionStart.Plan(Input(r, v, body));
        Assert.IsFalse(plan.Ok);
        Assert.AreEqual("Orbit too low for a safe precision approach (Pe 15.0 km, terrain up to 14.0 km): raise it with the Orbit tab.",
            plan.Reason);
        Assert.AreEqual(16000, plan.Clearance.RequiredM, 1e-9);
    }

    [Test]
    public void Plan_ClearOrbitPlans_UnreadableTerrainKeepsTheFloorAlone()
    {
        Vector3d r = Circular(R + 30000, out Vector3d v);
        StartPlan ok = PrecisionStart.Plan(Input(r, v, Flat));
        Assert.IsTrue(ok.Ok, ok.Reason);
        Assert.IsTrue(ok.Clearance.TerrainRead);
        Assert.AreEqual(PrecisionStart.MinClearance, ok.Clearance.RequiredM, 1e-9);

        Body body = Flat;
        body.TerrainHeight = p => double.NaN;
        Vector3d lowR = Circular(R + 8000, out Vector3d lowV);
        StartPlan low = PrecisionStart.Plan(Input(lowR, lowV, body));
        Assert.IsFalse(low.Ok);
        Assert.AreEqual("Orbit too low for a safe precision approach (Pe 8.0 km, terrain not read, 10.0 km minimum): " +
            "raise it with the Orbit tab.", low.Reason);
    }

    // ---- Rotation sign cross-check, brake throttle, plane-change lead ----

    [Test]
    public void RotationMeasurements_DisagreeOnlyWhenBothExistAndDiffer()
    {
        Assert.IsTrue(RotationProbe.Disagree(1, -1));
        Assert.IsTrue(RotationProbe.Disagree(-1, 1));
        Assert.IsFalse(RotationProbe.Disagree(1, 1));
        Assert.IsFalse(RotationProbe.Disagree(-1, -1));
        Assert.IsFalse(RotationProbe.Disagree(0, -1), "no surface-speed measurement");
        Assert.IsFalse(RotationProbe.Disagree(1, 0), "no probe measurement");
        Assert.AreEqual("Rotation direction measurements disagree: precision landing refused.", RotationProbe.DisagreeMessage);
    }

    [Test]
    public void BrakeThrottle_TooLateAboveTheSafeAltitude_IsFullThrottle()
    {
        Assert.AreEqual(1, PrecisionPhases.BrakeThrottle(new BrakeResult { Status = BrakeStatus.TooLate, StopAltitude = 900 }), 1e-6);
    }

    [Test]
    public void PlaneChange_LeadIsThePlanLead()
    {
        Assert.AreEqual(PrecisionPhases.PlanLead, PlaneChange.MinLead, 1e-12);
        Assert.GreaterOrEqual(PlaneChange.MinLead, K2D2.Node.BurnWarp.MaxLead);
    }
}

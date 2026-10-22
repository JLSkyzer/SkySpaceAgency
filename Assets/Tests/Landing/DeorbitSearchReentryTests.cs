using System;
using System.Diagnostics;
using K2D2.Landing.Precision;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// The precision searches with ReentryPredictor as the stop predictor, on the synthetic Kerbin of
// ReentryPredictorTests.
public class DeorbitSearchReentryTests
{
    const double Mu = ReentryPredictorTests.Mu;
    const double R = ReentryPredictorTests.R;
    const double UT = ReentryPredictorTests.UT;
    const double Deg = Math.PI / 180;

    static Body Kerbin => ReentryPredictorTests.Kerbin(true);
    static readonly Vehicle ServiceModule = new Vehicle { MassT = 5, ThrustKN = 50, Isp = 320 };

    // Circular 100 km equatorial prograde orbit, at +x moving +y.
    static readonly double R0 = R + 100000;
    static Vector3d R0Vec => new Vector3d(R0, 0, 0);
    static Vector3d V0Vec => new Vector3d(0, Math.Sqrt(Mu / R0), 0);
    static double Period => 2 * Math.PI * Math.Sqrt(R0 * R0 * R0 / Mu);

    static double DescentRp => DeorbitSearch.ReentryPeriapsis(Kerbin, ReentryPredictorTests.Depth);

    static Vector3d Target(double aheadDeg, double latDeg = 0) => new Vector3d(
        Math.Cos(latDeg * Deg) * Math.Cos(aheadDeg * Deg),
        Math.Cos(latDeg * Deg) * Math.Sin(aheadDeg * Deg),
        Math.Sin(latDeg * Deg));

    static int calls;
    static double predictMs;

    static StopPredictorFn Reentry(ReentryVehicle capsule = default, Func<double, bool> refuse = null)
    {
        if (capsule.MassT <= 0)
            capsule = ReentryPredictorTests.Capsule();
        Atmosphere air = ReentryPredictorTests.Air();
        return (r, v, ut, impulse) =>
        {
            calls++;
            if (refuse != null && impulse.HasValue && refuse(impulse.Value.UT))
                return new StopPrediction { Ok = false, Reason = "refused by the test" };
            var watch = Stopwatch.StartNew();
            StopPrediction p = ReentryPredictor.Predict(r, v, ut, Kerbin, air, capsule, impulse, ServiceModule.Isp);
            predictMs += watch.Elapsed.TotalMilliseconds;
            return p;
        };
    }

    static DeorbitPlan Find(Vector3d target, double earliest, StopPredictorFn predict)
    {
        calls = 0; predictMs = 0;
        var watch = Stopwatch.StartNew();
        DeorbitPlan plan = DeorbitSearch.Find(R0Vec, V0Vec, UT, Kerbin, target, earliest, predict, DescentRp);
        TestContext.Out.WriteLine($"[reentry search] {calls} predict calls, {predictMs:F0} ms predicting ({predictMs / Math.Max(calls, 1):F1} ms each), {watch.Elapsed.TotalMilliseconds:F0} ms total");
        return plan;
    }

    [Test]
    public void ReentryPeriapsis_IsAFractionOfTheAtmosphereDepth()
    {
        Assert.AreEqual(R + 31500, DescentRp, 1e-6);
    }

    [TestCase(0)]
    [TestCase(90)]
    [TestCase(180)]
    [TestCase(270)]
    public void EquatorialTarget_ReentryLandsOnIt(double aheadDeg)
    {
        DeorbitPlan plan = Find(Target(aheadDeg), UT + 30, Reentry());
        Assert.IsTrue(plan.Ok, plan.Reason);
        Assert.Less(Math.Abs(plan.AlongMissM), 500, "along");
        Assert.IsTrue(plan.Stop.Ok);
        Assert.Greater(plan.DeltaV.magnitude, 0);
    }

    [Test]
    public void Baseline_ReentryUsesTheGivenPeriapsis()
    {
        DeorbitPlan plan = DeorbitSearch.Baseline(R0Vec, V0Vec, UT, Kerbin, UT + 30, Reentry(), DescentRp);
        Assert.IsTrue(plan.Ok, plan.Reason);
        Assert.IsTrue(double.IsNaN(plan.AlongMissM));
        // Periapsis after the burn is DescentRp (radius): vis-viva at the burn point.
        KeplerPropagator.Propagate(R0Vec, V0Vec, Mu, 30, out Vector3d rb, out Vector3d vb);
        Vector3d v2 = vb + plan.DeltaV;
        double h = Vector3d.Cross(rb, v2).magnitude;
        double energy = 0.5 * v2.sqrMagnitude - Mu / rb.magnitude;
        double e = Math.Sqrt(1 + 2 * energy * h * h / (Mu * Mu));
        Assert.AreEqual(DescentRp, h * h / (Mu * (1 + e)), 1);
    }

    // A refused sample far from the crossing leaves the earliest crossing, and its refinement, as is.
    [TestCase(90)]
    [TestCase(270)]
    public void RefusedSampleAwayFromTheCrossing_DoesNotChangeTheResult(double aheadDeg)
    {
        Vector3d target = Target(aheadDeg);
        double earliest = UT + 30;
        DeorbitPlan clean = Find(target, earliest, Reentry());
        Assert.IsTrue(clean.Ok, clean.Reason);

        double window = DeorbitSearch.SearchWindow(Period, Kerbin);
        double spacing = window / DeorbitSearch.Samples;
        int crossing = (int)Math.Floor((clean.BurnUT - earliest) / spacing) + 1;   // the sample after the crossing
        int refused = (crossing + 6) % (DeorbitSearch.Samples + 1);
        double refusedUT = earliest + spacing * refused;

        DeorbitPlan plan = Find(target, earliest, Reentry(refuse: t => Math.Abs(t - refusedUT) < 1));
        Assert.IsTrue(plan.Ok, plan.Reason);
        Assert.AreEqual(clean.BurnUT, plan.BurnUT, 0.5);
    }

    // The refused sample sits between the two that bracket the crossing: no bracket, no crash.
    [Test]
    public void RefusedSampleAtTheCrossing_IsRefusedCleanly()
    {
        Vector3d target = Target(90);
        double earliest = UT + 30;
        DeorbitPlan clean = Find(target, earliest, Reentry());
        double spacing = DeorbitSearch.SearchWindow(Period, Kerbin) / DeorbitSearch.Samples;
        int before = (int)Math.Floor((clean.BurnUT - earliest) / spacing);
        double refusedUT = earliest + spacing * before;

        DeorbitPlan plan = Find(target, earliest, Reentry(refuse: t => Math.Abs(t - refusedUT) < 1));
        Assert.IsFalse(plan.Ok);
        Assert.IsNotEmpty(plan.Reason);
    }

    // Chutes that cannot open at any burn time: every candidate is refused, with the predictor's reason.
    [Test]
    public void ChutesThatNeverOpen_NoSolutionAndTheReason()
    {
        DeorbitPlan plan = Find(Target(90), UT + 30, Reentry(ReentryPredictorTests.Capsule(maxSpeed: 1)));
        Assert.IsFalse(plan.Ok);
        StringAssert.Contains("chutes", plan.Reason);
    }

    // Burns well before and well after the crossing are refused ("chutes never open"): the Ok samples
    // around the crossing still bracket it.
    [Test]
    public void CandidatesRefusedOnBothSidesOfTheCrossing_TheBracketStillConverges()
    {
        Vector3d target = Target(90);
        double earliest = UT + 30;
        DeorbitPlan clean = Find(target, earliest, Reentry());
        Assert.IsTrue(clean.Ok, clean.Reason);
        double spacing = DeorbitSearch.SearchWindow(Period, Kerbin) / DeorbitSearch.Samples;
        double lo = clean.BurnUT - 1.5 * spacing, hi = clean.BurnUT + 1.5 * spacing;
        StopPredictorFn inner = Reentry();
        StopPredictorFn predict = (r, v, ut, impulse) =>
            impulse.HasValue && (impulse.Value.UT < lo || impulse.Value.UT > hi)
                ? new StopPrediction { Ok = false, Reason = "The chutes never open" }
                : inner(r, v, ut, impulse);
        DeorbitPlan plan = Find(target, earliest, predict);
        Assert.IsTrue(plan.Ok, plan.Reason);
        Assert.Less(Math.Abs(plan.AlongMissM), 500);
        Assert.AreEqual(clean.BurnUT, plan.BurnUT, 0.5);
    }

    [Test]
    public void PlaneChange_ReentryBringsTheTargetUnderTheTrack()
    {
        // 4° of latitude is 70 km across the track: a plane change is needed.
        calls = 0; predictMs = 0;
        var watch = Stopwatch.StartNew();
        PlaneChangePlan pc = PlaneChange.Plan(R0Vec, V0Vec, UT, Kerbin, ServiceModule, Target(100, 4), Reentry(), DescentRp);
        TestContext.Out.WriteLine($"[reentry plane change] {calls} predict calls, {predictMs:F0} ms predicting, {watch.Elapsed.TotalMilliseconds:F0} ms total, {pc.Iterations} iterations, dv {pc.DeltaV:F1}");
        Assert.IsTrue(pc.Needed);
        Assert.IsTrue(pc.Ok, pc.Reason);
        Assert.IsTrue(pc.Deorbit.Ok, pc.Deorbit.Reason);
        Assert.Less(Math.Abs(pc.Deorbit.CrossMissM), PlaneChange.CrossPrecision);
        Assert.Less(Math.Abs(pc.Deorbit.AlongMissM), 500);
    }

    [Test]
    public void CourseCorrection_ReentryPullsTheStopBackToTheTarget()
    {
        Vector3d target = Target(150);
        DeorbitPlan plan = Find(target, UT + 30, Reentry());
        Assert.IsTrue(plan.Ok, plan.Reason);

        // At the burn, 1 % too much: the stop moves; the correction brings it back.
        KeplerPropagator.Propagate(R0Vec, V0Vec, Mu, plan.BurnUT - UT, out Vector3d rb, out Vector3d vb);
        Vector3d v1 = vb + 1.02 * plan.DeltaV;
        Vector3d targetNow = SurfaceGeometry.AtTime(target, Kerbin.Omega, plan.BurnUT - UT);

        calls = 0; predictMs = 0;
        var watch = Stopwatch.StartNew();
        Correction c = CourseCorrection.Solve(rb, v1, plan.BurnUT, Kerbin, targetNow, 50, Reentry());
        TestContext.Out.WriteLine($"[reentry correction] {calls} predict calls, {predictMs:F0} ms predicting, {watch.Elapsed.TotalMilliseconds:F0} ms total, miss {c.MissBeforeM:F0} -> {c.MissAfterM:F0} m");
        Assert.IsTrue(c.Ok, c.Reason);
        Assert.Greater(c.MissBeforeM, 1000);
        Assert.Less(c.MissAfterM, 500);
    }

    // The miss is split along the track where the capsule arrives (StopPrediction.TrackDirNow), not
    // along the interface velocity projected at the stop: that projection shrinks to nothing when the
    // arc from the interface to the stop nears 90° (along and cross then mix) and points backwards
    // beyond. Equatorial prograde descent, so the arrival track is east along the equator; the 0.75
    // periapsis is a shallow entry with a long arc in the air.
    [TestCase(0.45)]
    [TestCase(0.75)]
    public void ReentryTrack_IsTheArrivalDirection(double fraction)
    {
        double rp = R + fraction * ReentryPredictorTests.Depth;
        DeorbitPlan plan = DeorbitSearch.Baseline(R0Vec, V0Vec, UT, Kerbin, UT + 30, Reentry(), rp);
        Assert.IsTrue(plan.Ok, plan.Reason);
        Vector3d n = plan.Stop.StopDirNow;
        Vector3d track = plan.Stop.TrackDirNow;
        Vector3d east = Vector3d.Cross(new Vector3d(0, 0, 1), n).normalized;
        // The interface state, the point body-fixed in the orientation at UT; the old track from its velocity.
        var burn = new Impulse { UT = plan.BurnUT, DeltaV = plan.DeltaV };
        ReentryPredictor.Predict(R0Vec, V0Vec, UT, Kerbin, ReentryPredictorTests.Air(), ReentryPredictorTests.Capsule(), burn, out ReentryTrace trace);
        KeplerPropagator.Propagate(R0Vec, V0Vec, Mu, plan.BurnUT - UT, out Vector3d rb, out Vector3d vb);
        KeplerPropagator.Propagate(rb, vb + plan.DeltaV, Mu, trace.InterfaceUT - plan.BurnUT, out Vector3d ri, out Vector3d vi);
        Vector3d interfaceNow = SurfaceGeometry.AtTime(ri, Kerbin.Omega, -(trace.InterfaceUT - UT)).normalized;
        Vector3d old = SurfaceGeometry.AtTime(vi - Vector3d.Cross(Kerbin.Omega, ri), Kerbin.Omega, -(trace.InterfaceUT - UT));
        old = old - Vector3d.Dot(old, n) * n;
        TestContext.Out.WriteLine($"[reentry track {fraction:F2}] arc interface->stop {SurfaceGeometry.GreatCircle(interfaceNow, n, R) / 1000:F0} km, "
            + $"track·east {Vector3d.Dot(track, east):F6}, track·stop {Vector3d.Dot(track, n):E2}; interface velocity projected at the stop: "
            + $"{old.magnitude:F0} m/s, ·east {Vector3d.Dot(old.normalized, east):F3}");
        Assert.AreEqual(1, track.magnitude, 1e-9);
        Assert.Greater(Vector3d.Dot(track, east), 0.9999);

        // A target 2 km east of the stop is reached, nothing across.
        Vector3d target = (n * Math.Cos(2000 / R) + east * Math.Sin(2000 / R)).normalized;
        calls = 0; predictMs = 0;
        DeorbitPlan aimed = DeorbitSearch.Find(R0Vec, V0Vec, UT, Kerbin, target, UT + 30, Reentry(), rp);
        Assert.IsTrue(aimed.Ok, aimed.Reason);
        Assert.Less(Math.Abs(aimed.CrossMissM), 50);
        Assert.Less(SurfaceGeometry.GreatCircle(aimed.Stop.StopDirNow, target, R), 500);
    }
}

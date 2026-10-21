using System;
using System.Diagnostics;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class CourseCorrectionTests
{
    // Mun.
    const double Mu = 6.5138398e10;
    const double R = 200000;
    const double RotationPeriod = 138984.38;
    const double UT = 1000;
    const double Deg = Math.PI / 180;
    static double SurfaceGravity => Mu / (R * R);

    static Body Mun => new Body
    {
        Mu = Mu,
        Radius = R,
        Omega = BodyRotation.AngularVelocity(BodyRotation.AngularSpeed(RotationPeriod), 1),
        TerrainHeight = p => 0,
    };

    static Vehicle Lander => new Vehicle { MassT = 10, ThrustKN = 3 * 10 * SurfaceGravity, Isp = 320 };

    static readonly double R0 = R + 30000;
    static double CircularSpeed => Math.Sqrt(Mu / R0);

    // A descent on its way to the surface: circular 30 km orbit (inclination incDeg), deorbited
    // by DeorbitSearch onto a point 120° ahead, then coasted halfway to the braking start.
    // The returned stop is the prediction from that state (orientation at t1).
    static void Descent(double incDeg, out Vector3d r1, out Vector3d v1, out double t1, out StopPrediction stop)
    {
        var r0 = new Vector3d(R0, 0, 0);
        var plane = new Vector3d(0, Math.Cos(incDeg * Deg), Math.Sin(incDeg * Deg));
        Vector3d v0 = CircularSpeed * plane;
        Vector3d aim = Math.Cos(120 * Deg) * new Vector3d(1, 0, 0) + Math.Sin(120 * Deg) * plane;

        DeorbitPlan plan = DeorbitSearch.Find(r0, v0, UT, Mun, Lander, aim, UT + 30);
        Assert.IsTrue(plan.Ok, plan.Reason);

        KeplerPropagator.Propagate(r0, v0, Mu, plan.BurnUT - UT, out var rb, out var vb);
        vb = vb + plan.DeltaV;
        t1 = plan.BurnUT + 0.5 * (plan.Stop.BrakeStartUT - plan.BurnUT);
        KeplerPropagator.Propagate(rb, vb, Mu, t1 - plan.BurnUT, out r1, out v1);

        stop = StopPointPredictor.Predict(r1, v1, t1, Mun, Lander, null);
        Assert.IsTrue(stop.Ok, stop.Reason);
    }

    // Unit ground track at the stop point (surface-relative velocity at the braking start,
    // in the orientation at t1).
    static Vector3d Track(Vector3d r1, Vector3d v1, double t1, StopPrediction stop)
    {
        KeplerPropagator.Propagate(r1, v1, Mu, stop.BrakeStartUT - t1, out var rs, out var vs);
        Vector3d surface = vs - Vector3d.Cross(Mun.Omega, rs);
        Vector3d now = SurfaceGeometry.AtTime(surface, Mun.Omega, -(stop.BrakeStartUT - t1));
        Vector3d n = stop.StopDirNow;
        Vector3d t = now - Vector3d.Dot(now, n) * n;
        return t / t.magnitude;
    }

    // The surface point alongM ahead of the stop and crossM to its left.
    static Vector3d Shifted(StopPrediction stop, Vector3d track, double alongM, double crossM)
    {
        Vector3d n = stop.StopDirNow;
        Vector3d left = Vector3d.Cross(n, track);
        double a = alongM / R, c = crossM / R;
        Vector3d p = Math.Cos(a) * n + Math.Sin(a) * track;
        return Math.Cos(c) * p + Math.Sin(c) * left;
    }

    static Correction Solve(Vector3d r1, Vector3d v1, double t1, Vector3d target, double maxDv, out double ms)
    {
        var watch = Stopwatch.StartNew();
        Correction c = CourseCorrection.Solve(r1, v1, t1, Mun, Lander, target, maxDv);
        ms = watch.Elapsed.TotalMilliseconds;
        return c;
    }

    static double CheckedMiss(Vector3d r1, Vector3d v1, double t1, Vector3d target, Correction c)
    {
        StopPrediction after = StopPointPredictor.Predict(r1, v1, t1, Mun, Lander, new Impulse { UT = t1, DeltaV = c.DeltaV });
        Assert.IsTrue(after.Ok, after.Reason);
        return SurfaceGeometry.GreatCircle(after.StopDirNow, target, R);
    }

    // The descent is 2 km long and 500 m to the side of the target (or the opposite).
    [TestCase(0, -2000, 500)]
    [TestCase(0, 2000, -500)]
    [TestCase(30, -2000, 500)]
    public void OffTargetDescent_CorrectionZeroesTheMiss(double incDeg, double alongM, double crossM)
    {
        Descent(incDeg, out var r1, out var v1, out double t1, out var stop);
        Vector3d target = Shifted(stop, Track(r1, v1, t1, stop), alongM, crossM);

        Correction c = Solve(r1, v1, t1, target, 100, out double ms);

        TestContext.Out.WriteLine($"inc {incDeg}, miss ({alongM}, {crossM}): ok {c.Ok} ({c.Reason}), dv {c.DeltaV.magnitude:F3} m/s, miss {c.MissBeforeM:F0} -> {c.MissAfterM:F1} m, clipped {c.Clipped}, {c.PredictCalls} Predict calls, {ms:F0} ms");
        Assert.IsTrue(c.Ok, c.Reason);
        Assert.IsFalse(c.Clipped);
        Assert.That(c.MissBeforeM, Is.EqualTo(Math.Sqrt(alongM * alongM + crossM * crossM)).Within(3).Percent);
        Assert.That(c.MissAfterM, Is.LessThan(30));
        Assert.That(c.DeltaV.magnitude, Is.LessThan(20));
        Assert.That(c.PredictCalls, Is.LessThanOrEqualTo(25));

        // In the plane of prograde and orbit normal: no radial component.
        Vector3d radialIn = Vector3d.Cross(v1, Vector3d.Cross(r1, v1)).normalized;
        Assert.That(Math.Abs(Vector3d.Dot(c.DeltaV, radialIn)), Is.LessThan(1e-6 * Math.Max(1, c.DeltaV.magnitude)));

        // Independent check of the reported miss.
        double miss = CheckedMiss(r1, v1, t1, target, c);
        Assert.That(miss, Is.LessThan(30));
        Assert.That(miss, Is.EqualTo(c.MissAfterM).Within(1));
    }

    [Test]
    public void SmallBudget_ClipsAlongTheFullSolution()
    {
        Descent(0, out var r1, out var v1, out double t1, out var stop);
        Vector3d target = Shifted(stop, Track(r1, v1, t1, stop), -2000, 500);

        Correction full = Solve(r1, v1, t1, target, 100, out _);
        Correction clipped = Solve(r1, v1, t1, target, 1, out double ms);

        TestContext.Out.WriteLine($"clipped: ok {clipped.Ok} ({clipped.Reason}), dv {clipped.DeltaV.magnitude:F3} m/s (full {full.DeltaV.magnitude:F3}), miss {clipped.MissBeforeM:F0} -> {clipped.MissAfterM:F1} m, {clipped.PredictCalls} Predict calls, {ms:F0} ms");
        Assert.IsTrue(full.Ok, full.Reason);
        Assert.That(full.DeltaV.magnitude, Is.GreaterThan(1), "the test needs a full solution over the budget");
        Assert.IsTrue(clipped.Ok, clipped.Reason);
        Assert.IsTrue(clipped.Clipped);
        Assert.That(clipped.DeltaV.magnitude, Is.EqualTo(1).Within(1e-9));
        Assert.That(Vector3d.Dot(clipped.DeltaV.normalized, full.DeltaV.normalized), Is.GreaterThan(0.99999));
        Assert.That(clipped.MissAfterM, Is.LessThan(clipped.MissBeforeM));
        Assert.That(CheckedMiss(r1, v1, t1, target, clipped), Is.EqualTo(clipped.MissAfterM).Within(1));
    }

    [Test]
    public void OnTarget_NeedsNoCorrection()
    {
        Descent(0, out var r1, out var v1, out double t1, out var stop);

        Correction c = Solve(r1, v1, t1, stop.StopDirNow, 100, out double ms);

        TestContext.Out.WriteLine($"on target: ok {c.Ok} ({c.Reason}), dv {c.DeltaV.magnitude:F4} m/s, miss {c.MissBeforeM:F2} -> {c.MissAfterM:F2} m, {c.PredictCalls} Predict calls, {ms:F0} ms");
        Assert.IsTrue(c.Ok, c.Reason);
        Assert.That(c.DeltaV.magnitude, Is.LessThan(0.05));
        Assert.That(c.MissAfterM, Is.LessThan(5));
    }

    [Test]
    public void RefusedDescent_ReturnsNotOkWithAReason()
    {
        Descent(0, out var r1, out var v1, out double t1, out var stop);
        Vehicle dead = Lander;
        dead.ThrustKN = 0;

        Correction c = CourseCorrection.Solve(r1, v1, t1, Mun, dead, stop.StopDirNow, 100);

        Assert.IsFalse(c.Ok);
        Assert.That(c.Reason, Is.Not.Null.And.Not.Empty);
        Assert.That(c.DeltaV.magnitude, Is.EqualTo(0));
    }
}

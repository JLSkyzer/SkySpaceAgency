using System;
using System.Diagnostics;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class DeorbitSearchTests
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

    // Circular 30 km equatorial orbit, prograde with the rotation: at +x, moving +y.
    static readonly double R0 = R + 30000;
    static double CircularSpeed => Math.Sqrt(Mu / R0);
    static double Period => 2 * Math.PI * Math.Sqrt(R0 * R0 * R0 / Mu);
    static Vector3d R0Vec => new Vector3d(R0, 0, 0);
    static Vector3d V0Vec => new Vector3d(0, CircularSpeed, 0);

    // Surface point `aheadDeg` ahead of the vessel along the orbit, at latitude latDeg, now.
    static Vector3d Target(double aheadDeg, double latDeg = 0) => new Vector3d(
        Math.Cos(latDeg * Deg) * Math.Cos(aheadDeg * Deg),
        Math.Cos(latDeg * Deg) * Math.Sin(aheadDeg * Deg),
        Math.Sin(latDeg * Deg));

    static DeorbitPlan Find(Vector3d target, double earliest, out double ms)
    {
        var watch = Stopwatch.StartNew();
        DeorbitPlan plan = DeorbitSearch.Find(R0Vec, V0Vec, UT, Mun, Lander, target, earliest);
        ms = watch.Elapsed.TotalMilliseconds;
        return plan;
    }

    // 120° ahead from UT: the stop point after one orbit is still 4.5 km short of the target,
    // because the Mun turned under the track (the gap a one-orbit window would miss).
    [TestCase(0, 30)]
    [TestCase(90, 30)]
    [TestCase(180, 30)]
    [TestCase(270, 30)]
    [TestCase(120, 0)]
    public void EquatorialTarget_StopPointLandsOnIt(double aheadDeg, double earliestAfter)
    {
        Vector3d target = Target(aheadDeg);
        double earliest = UT + earliestAfter;

        DeorbitPlan plan = Find(target, earliest, out double ms);

        TestContext.Out.WriteLine($"ahead {aheadDeg}: ok {plan.Ok} ({plan.Reason}), burn +{plan.BurnUT - UT:F1} s, dv {plan.DeltaV.magnitude:F2}, along {plan.AlongMissM:F1} m, cross {plan.CrossMissM:F1} m, {plan.PredictCalls} Predict calls, {ms:F0} ms, burn {(plan.BurnUT - earliest) / Period:F3} orbit after earliest");
        Assert.IsTrue(plan.Ok, plan.Reason);
        Assert.That(Math.Abs(plan.AlongMissM), Is.LessThan(50));
        Assert.That(Math.Abs(plan.CrossMissM), Is.LessThan(1000));
        Assert.That(plan.BurnUT, Is.InRange(earliest, earliest + DeorbitSearch.SearchWindow(Period, Mun)));
        Assert.That(DeorbitSearch.SearchWindow(Period, Mun), Is.LessThan(1.03 * Period));
        Assert.That(plan.PredictCalls, Is.LessThan(60));

        // Independent check: the plan's burn, predicted again, stops on the target.
        StopPrediction again = StopPointPredictor.Predict(R0Vec, V0Vec, UT, Mun, Lander,
            new Impulse { UT = plan.BurnUT, DeltaV = plan.DeltaV });
        Assert.IsTrue(again.Ok, again.Reason);
        double miss = SurfaceGeometry.GreatCircle(again.StopDirNow, target, R);
        Assert.That(miss, Is.LessThan(100), $"great-circle miss {miss:F0} m");

        // Retrograde burn lowering the periapsis to R - margin.
        KeplerPropagator.Propagate(R0Vec, V0Vec, Mu, plan.BurnUT - UT, out var rb, out var vb);
        Assert.That(Vector3d.Dot(plan.DeltaV.normalized, vb.normalized), Is.LessThan(-0.9999));
        Vector3d vAfter = vb + plan.DeltaV;
        double rp = K2D2.OrbitPlanning.OrbitMath.Elements(rb, vAfter, Mu).PeriapsisRadius;
        Assert.That(rp, Is.EqualTo(R - DeorbitSearch.DescentPeriapsisMargin).Within(1));
    }

    [Test]
    public void TargetOffThePlane_ReportsTheCrossMiss()
    {
        DeorbitPlan plan = Find(Target(120, 20), UT, out double ms);

        TestContext.Out.WriteLine($"lat 20: ok {plan.Ok} ({plan.Reason}), along {plan.AlongMissM:F1} m, cross {plan.CrossMissM:F0} m, {plan.PredictCalls} calls, {ms:F0} ms");
        Assert.IsTrue(plan.Ok, plan.Reason);
        Assert.That(Math.Abs(plan.AlongMissM), Is.LessThan(50));
        Assert.That(Math.Abs(plan.CrossMissM), Is.GreaterThan(1000));
        // About 20° of latitude: the miss is to the left (north) of an eastward track.
        Assert.That(plan.CrossMissM, Is.EqualTo(20 * Deg * R).Within(5).Percent);
    }

    [Test]
    public void Baseline_BurnsTheSameDeltaVAtTheEarliestTime()
    {
        DeorbitPlan plan = Find(Target(200), UT, out _);
        DeorbitPlan baseline = DeorbitSearch.Baseline(R0Vec, V0Vec, UT, Mun, Lander, UT + 10);

        TestContext.Out.WriteLine($"find dv {plan.DeltaV.magnitude:F3} brake {plan.Stop.BrakeDeltaV:F1}, baseline dv {baseline.DeltaV.magnitude:F3} brake {baseline.Stop.BrakeDeltaV:F1}");
        Assert.IsTrue(plan.Ok, plan.Reason);
        Assert.IsTrue(baseline.Ok, baseline.Reason);
        Assert.AreEqual(UT + 10, baseline.BurnUT, 1e-9);
        Assert.That(baseline.DeltaV.magnitude, Is.EqualTo(plan.DeltaV.magnitude).Within(1).Percent);
        Assert.That(baseline.Stop.Ok, Is.True);
        Assert.That(baseline.Stop.BrakeDeltaV, Is.GreaterThan(0));
    }

    // A 10 km wall just short of the target: descents that would stop on the target hit it, so
    // the stop point jumps from before the wall to past the target (or the candidate is refused)
    // and no burn time zeroes the along-track miss. Find must refuse instead of returning the
    // closest bracket end.
    [Test]
    public void TargetBehindAWall_IsRefusedAsNotConverged()
    {
        Body walled = Mun;
        double wallFrom = 119.2 * Deg, wallTo = 119.8 * Deg;
        walled.TerrainHeight = p =>
        {
            double phi = Math.Atan2(p.y, p.x);
            return phi > wallFrom && phi < wallTo ? 10000 : 0;
        };

        var watch = Stopwatch.StartNew();
        DeorbitPlan plan = DeorbitSearch.Find(R0Vec, V0Vec, UT, walled, Lander, Target(120), UT);
        double ms = watch.Elapsed.TotalMilliseconds;

        TestContext.Out.WriteLine($"wall: ok {plan.Ok} ({plan.Reason}), burn +{plan.BurnUT - UT:F1} s, along {plan.AlongMissM:F1} m, {plan.PredictCalls} calls, {ms:F0} ms");
        Assert.IsFalse(plan.Ok);
        Assert.That(plan.Reason, Does.Contain("converge"));
        Assert.That(plan.PredictCalls, Is.LessThan(60));
    }

    [Test]
    public void NoThrust_IsRefusedWithAReason()
    {
        Vehicle dead = Lander;
        dead.ThrustKN = 0;

        DeorbitPlan plan = DeorbitSearch.Find(R0Vec, V0Vec, UT, Mun, dead, Target(90), UT);

        Assert.IsFalse(plan.Ok);
        Assert.That(plan.Reason, Is.Not.Null.And.Not.Empty);
        Assert.That(plan.PredictCalls, Is.LessThan(60));
    }
}

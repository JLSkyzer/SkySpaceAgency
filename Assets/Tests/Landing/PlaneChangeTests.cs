using System;
using System.Diagnostics;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class PlaneChangeTests
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
    static Vector3d R0Vec => new Vector3d(R0, 0, 0);
    static Vector3d V0Vec => new Vector3d(0, CircularSpeed, 0);

    // Surface point `aheadDeg` ahead of the vessel along the orbit, at latitude latDeg, now.
    static Vector3d Target(double aheadDeg, double latDeg) => new Vector3d(
        Math.Cos(latDeg * Deg) * Math.Cos(aheadDeg * Deg),
        Math.Cos(latDeg * Deg) * Math.Sin(aheadDeg * Deg),
        Math.Sin(latDeg * Deg));

    static PlaneChangePlan Plan(Vector3d target, out double ms)
    {
        var watch = Stopwatch.StartNew();
        PlaneChangePlan plan = PlaneChange.Plan(R0Vec, V0Vec, UT, Mun, Lander, target);
        ms = watch.Elapsed.TotalMilliseconds;
        return plan;
    }

    static string Describe(PlaneChangePlan plan, double ms) =>
        $"needed {plan.Needed} ok {plan.Ok} ({plan.Reason}), burn +{plan.BurnUT - UT:F1} s (start +{plan.BurnStartUT - UT:F1} s), dv {plan.DeltaV:F1} m/s, " +
        $"incl {plan.Target.InclinationRad / Deg:F3}°, {plan.Iterations} iteration(s), {plan.PredictCalls} Predict calls, {ms:F0} ms, " +
        $"deorbit after: cross {plan.Deorbit.CrossMissM:F1} m along {plan.Deorbit.AlongMissM:F1} m";

    // The plan's burn applied as an instant rotation of the velocity about the radius at BurnUT.
    static void ApplyBurn(PlaneChangePlan plan, out Vector3d rb, out Vector3d vb, out Vector3d vNew)
    {
        KeplerPropagator.Propagate(R0Vec, V0Vec, Mu, plan.BurnUT - UT, out rb, out vb);
        Vector3d axis = rb.normalized;
        Vector3d n = Vector3d.Cross(rb, vb).normalized;
        Vector3d n2 = plan.Target.PlaneNormal.normalized;
        double angle = Math.Atan2(Vector3d.Dot(Vector3d.Cross(n, n2), axis), Vector3d.Dot(n, n2));
        vNew = OrbitMath.Rotate(vb, axis, angle);
    }

    [Test]
    public void TargetAt20DegreesLatitude_EndsUnderTheGroundTrack()
    {
        Vector3d target = Target(120, 20);
        PlaneChangePlan plan = Plan(target, out double ms);

        TestContext.Out.WriteLine("lat 20: " + Describe(plan, ms));
        Assert.IsTrue(plan.Needed);
        Assert.IsTrue(plan.Ok, plan.Reason);
        Assert.That(plan.BurnUT, Is.GreaterThanOrEqualTo(UT + PlaneChange.MinLead));

        ApplyBurn(plan, out Vector3d rb, out Vector3d vb, out Vector3d vNew);
        // The burn is at a node: the position is in both planes.
        Assert.That(Math.Abs(Vector3d.Dot(rb.normalized, plan.Target.PlaneNormal.normalized)), Is.LessThan(1e-6));
        // ... and at the earlier of the two nodes that is at least MinLead ahead.
        double lead = plan.BurnUT - UT;
        double otherLead = OrbitMath.TimeToDirection(R0Vec, V0Vec, Mu, -1.0 * rb);
        TestContext.Out.WriteLine($"lead {lead:F1} s, other node {otherLead:F1} s, start {plan.BurnStartUT - UT:F1} s");
        Assert.That(otherLead < PlaneChange.MinLead || lead <= otherLead, $"other node at +{otherLead:F1} s");
        // The finite burn starts half its (Tsiolkovsky) duration before the node.
        double duration = BurnTiming.Duration(plan.DeltaV, Lander.ThrustKN, Lander.MassT, Lander.Isp);
        Assert.That(duration, Is.GreaterThan(10));
        Assert.AreEqual(plan.BurnUT - duration / 2, plan.BurnStartUT, 0.01);
        Assert.That(plan.BurnStartUT, Is.GreaterThanOrEqualTo(UT + PlaneChange.MinLead));
        Assert.That(plan.DeltaV, Is.EqualTo((vNew - vb).magnitude).Within(0.01));
        // Target is the current orbit's shape in the new plane: the closed-loop guidance toward it
        // asks, at the node, for the same velocity change.
        OrbitElements el = OrbitMath.Elements(R0Vec, V0Vec, Mu);
        Assert.AreEqual(el.ApoapsisRadius, plan.Target.Ra, 1e-6);
        Assert.AreEqual(el.PeriapsisRadius, plan.Target.Rp, 1e-6);
        Vector3d guided = OrbitGuidance.DeltaVToOrbit(rb, vb, Mu, plan.Target);
        Assert.That((guided - (vNew - vb)).magnitude, Is.LessThan(0.05));

        // Independent check: the deorbit search from the new orbit puts the target under the track.
        Vector3d targetAtBurn = SurfaceGeometry.AtTime(target, Mun.Omega, plan.BurnUT - UT);
        double earliest = PlaneChange.EarliestDeorbitUT(plan.BurnUT, plan.DeltaV, Lander);
        DeorbitPlan after = DeorbitSearch.Find(rb, vNew, plan.BurnUT, Mun, Lander, targetAtBurn, earliest);
        TestContext.Out.WriteLine($"after: ok {after.Ok} ({after.Reason}), along {after.AlongMissM:F1} m, cross {after.CrossMissM:F1} m");
        Assert.IsTrue(after.Ok, after.Reason);
        Assert.That(Math.Abs(after.CrossMissM), Is.LessThan(300));
        Assert.That(Math.Abs(after.AlongMissM), Is.LessThan(50));
    }

    [Test]
    public void TargetUnderTheTrack_NeedsNoPlaneChange()
    {
        PlaneChangePlan plan = Plan(Target(120, 0), out double ms);

        TestContext.Out.WriteLine("lat 0: " + Describe(plan, ms));
        Assert.IsFalse(plan.Needed);
        Assert.IsTrue(plan.Ok, plan.Reason);
        Assert.AreEqual(0, plan.DeltaV, 1e-9);
    }

    [Test]
    public void TargetNearThePole_CostsTheClassicPlaneChange()
    {
        Vector3d target = Target(120, 85);
        PlaneChangePlan plan = Plan(target, out double ms);

        TestContext.Out.WriteLine("lat 85: " + Describe(plan, ms));
        Assert.IsTrue(plan.Needed);
        Assert.IsTrue(plan.Ok, plan.Reason);
        double di = plan.Target.InclinationRad;
        Assert.That(di / Deg, Is.EqualTo(85).Within(2));
        Assert.That(plan.DeltaV, Is.EqualTo(2 * CircularSpeed * Math.Sin(di / 2)).Within(5).Percent);

        ApplyBurn(plan, out Vector3d rb, out _, out Vector3d vNew);
        Vector3d targetAtBurn = SurfaceGeometry.AtTime(target, Mun.Omega, plan.BurnUT - UT);
        double earliest = PlaneChange.EarliestDeorbitUT(plan.BurnUT, plan.DeltaV, Lander);
        DeorbitPlan after = DeorbitSearch.Find(rb, vNew, plan.BurnUT, Mun, Lander, targetAtBurn, earliest);
        TestContext.Out.WriteLine($"after: ok {after.Ok} ({after.Reason}), along {after.AlongMissM:F1} m, cross {after.CrossMissM:F1} m");
        Assert.IsTrue(after.Ok, after.Reason);
        Assert.That(Math.Abs(after.CrossMissM), Is.LessThan(300));
    }

    [Test]
    public void TargetAtThePoleOfTheOrbit_IsRefused()
    {
        PlaneChangePlan plan = Plan(new Vector3d(0, 0, 1), out double ms);

        TestContext.Out.WriteLine("pole: " + Describe(plan, ms));
        Assert.IsTrue(plan.Needed);
        Assert.IsFalse(plan.Ok);
        Assert.That(plan.Reason, Does.Contain("pole"));
    }

    [Test]
    public void NoThrust_IsRefusedWithAReason()
    {
        Vehicle dead = Lander;
        dead.ThrustKN = 0;

        PlaneChangePlan plan = PlaneChange.Plan(R0Vec, V0Vec, UT, Mun, dead, Target(120, 20));

        Assert.IsFalse(plan.Ok);
        Assert.That(plan.Reason, Is.Not.Null.And.Not.Empty);
    }
}

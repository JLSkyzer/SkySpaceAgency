using System;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class OrbitGuidanceTests
{
    // Kerbin.
    const double Mu = 3.5316e12;
    const double R = 600000;
    const double Deg = Math.PI / 180;
    static readonly Vector3d North = new Vector3d(0, 0, 1);

    // Vessel at the periapsis (on the +x axis) of an orbit with the given apsis radii and inclination.
    static void StateAtPeriapsis(double rp, double ra, double incDeg, out Vector3d r, out Vector3d v)
    {
        double speed = Math.Sqrt(Mu * (2 / rp - 2 / (rp + ra)));
        r = new Vector3d(rp, 0, 0);
        v = new Vector3d(0, speed * Math.Cos(incDeg * Deg), speed * Math.Sin(incDeg * Deg));
    }

    static void AssertVector(Vector3d expected, Vector3d actual, double tolerance, string what)
    {
        Assert.That((actual - expected).magnitude, Is.LessThan(tolerance),
            $"{what}: expected ({expected.x}, {expected.y}, {expected.z}), got ({actual.x}, {actual.y}, {actual.z})");
    }

    [Test]
    public void AlreadyOnTheTargetOrbit_NeedsAboutZeroDeltaV([Values(0.0, 900.0, 3000.0)] double dt)
    {
        double rp = R + 100000, ra = R + 500000;
        StateAtPeriapsis(rp, ra, 20, out var r0, out var v0);
        Vector3d n = Vector3d.Cross(r0, v0).normalized;
        KeplerPropagator.Propagate(r0, v0, Mu, dt, out var r, out var v);

        Vector3d dv = OrbitGuidance.DeltaVToOrbit(r, v, Mu, ra, rp, n);

        Assert.That(dv.magnitude, Is.LessThan(1e-3));
    }

    [Test]
    public void DescendingOnTheTargetOrbit_NeedsAboutZeroDeltaV()
    {
        double rp = R + 100000, ra = R + 500000;
        StateAtPeriapsis(rp, ra, 20, out var r0, out var v0);
        double period = OrbitMath.Elements(r0, v0, Mu).Period;
        KeplerPropagator.Propagate(r0, v0, Mu, 0.7 * period, out var r, out var v);
        Assert.That(Vector3d.Dot(r, v), Is.LessThan(0), "sanity: descending");

        Vector3d dv = OrbitGuidance.DeltaVToOrbit(r, v, Mu, ra, rp, Vector3d.Cross(r0, v0).normalized);

        Assert.That(dv.magnitude, Is.LessThan(1e-3));
    }

    [Test]
    public void CircularToApoapsis500_AtPeriapsis_IsTheHohmannFirstBurn()
    {
        double r0 = R + 100000, ra = R + 500000;
        StateAtPeriapsis(r0, r0, 0, out var r, out var v);

        Vector3d dv = OrbitGuidance.DeltaVToOrbit(r, v, Mu, ra, r0, North);
        OrbitGuidance.Split(dv, r, v, out double radial, out double horizontal, out double normal);

        double expected = Math.Sqrt(Mu * (2 / r0 - 2 / (r0 + ra))) - Math.Sqrt(Mu / r0);
        Assert.That(expected, Is.GreaterThan(100), "sanity");
        Assert.That(radial, Is.EqualTo(0).Within(1e-6));
        Assert.That(normal, Is.EqualTo(0).Within(1e-6));
        Assert.That(horizontal, Is.EqualTo(expected).Within(1e-6));
        AssertVector(new Vector3d(0, expected, 0), dv, 1e-6, "dv");
    }

    [Test]
    public void PlaneChange0To30_AtTheNode_Is2VSin15()
    {
        double r0 = R + 100000;
        StateAtPeriapsis(r0, r0, 0, out var r, out var v);
        double vc = v.magnitude;
        // 30° plane about the node line (+x): the new velocity tilts toward +z.
        Vector3d n = OrbitMath.Rotate(North, new Vector3d(1, 0, 0), 30 * Deg);

        Vector3d dv = OrbitGuidance.DeltaVToOrbit(r, v, Mu, r0, r0, n);
        OrbitGuidance.Split(dv, r, v, out double radial, out double horizontal, out double normal);

        Assert.That(dv.magnitude, Is.EqualTo(2 * vc * Math.Sin(15 * Deg)).Within(1e-6));
        var after = OrbitMath.Elements(r, v + dv, Mu);
        Assert.That(after.InclinationRad / Deg, Is.EqualTo(30).Within(1e-6));
        Assert.That(after.ApoapsisRadius, Is.EqualTo(r0).Within(1e-3));
        Assert.That(after.PeriapsisRadius, Is.EqualTo(r0).Within(1e-3));
        // Components in the current frame: no radial part, along-track loss, normal (r × v) gain.
        Assert.That(radial, Is.EqualTo(0).Within(1e-6));
        Assert.That(horizontal, Is.EqualTo(vc * (Math.Cos(30 * Deg) - 1)).Within(1e-6));
        Assert.That(normal, Is.EqualTo(vc * Math.Sin(30 * Deg)).Within(1e-6));
    }

    [Test]
    public void CircularTarget_FromTheApoapsisOfAnEllipse_IsTheVisVivaCircularization()
    {
        double ra = R + 100000, rp = R - 430000;
        double speed = VisVivaEquation.CalculateVelocity(ra, ra, rp, Mu);
        var r = new Vector3d(ra, 0, 0);
        var v = new Vector3d(0, speed, 0);

        Vector3d dv = OrbitGuidance.DeltaVToOrbit(r, v, Mu, ra, ra, North);

        double expected = VisVivaEquation.CalculateVelocity(ra, ra, ra, Mu) - speed;
        AssertVector(new Vector3d(0, expected, 0), dv, 1e-6, "dv");
        AssertVector(CircularizeGuidance.DeltaVToCircular(r, v, Mu), dv, 1e-6, "same as CircularizeGuidance");
    }

    [Test]
    public void PastTheApoapsisWhileDescending_TheRadialComponentCancelsTheDownwardSpeed()
    {
        double ra = R + 100000, rp = R - 430000;
        double speed = VisVivaEquation.CalculateVelocity(ra, ra, rp, Mu);
        KeplerPropagator.Propagate(new Vector3d(ra, 0, 0), new Vector3d(0, speed, 0), Mu, 60, out var r, out var v);
        double verticalSpeed = Vector3d.Dot(v, r.normalized);
        Assert.That(verticalSpeed, Is.LessThan(-1), "sanity: descending");

        // Circular at the old apoapsis radius (now above the vessel: unreachable, clamped) and
        // circular at the current radius.
        foreach (double target in new[] { ra, r.magnitude })
        {
            Vector3d dv = OrbitGuidance.DeltaVToOrbit(r, v, Mu, target, target, North);
            OrbitGuidance.Split(dv, r, v, out double radial, out double horizontal, out double normal);

            Assert.That(radial, Is.EqualTo(-verticalSpeed).Within(1e-6), $"target {target}");
            Assert.That(horizontal, Is.GreaterThan(0), $"target {target}");
            Assert.That(normal, Is.EqualTo(0).Within(1e-6), $"target {target}");
        }

        AssertVector(CircularizeGuidance.DeltaVToCircular(r, v, Mu),
            OrbitGuidance.DeltaVToOrbit(r, v, Mu, r.magnitude, r.magnitude, North), 1e-6, "same as CircularizeGuidance");
    }

    [Test]
    public void TargetNormalAgainstTheMotion_KeepsTheDirectionOfMotion()
    {
        double r0 = R + 100000;
        StateAtPeriapsis(r0, r0, 0, out var r, out var v);

        Vector3d dv = OrbitGuidance.DeltaVToOrbit(r, v, Mu, r0, r0, -1.0 * North);

        Assert.That(dv.magnitude, Is.LessThan(1e-6));
    }

    [Test]
    public void NoRadialSpeed_GoesUpNearThePeriapsis_AndDownNearTheApoapsis()
    {
        double rp = R + 100000, ra = R + 500000;
        foreach (double radius in new[] { rp + 50000, ra - 50000 })
        {
            var r = new Vector3d(radius, 0, 0);
            var v = new Vector3d(0, 2000, 0);

            Vector3d after = v + OrbitGuidance.DeltaVToOrbit(r, v, Mu, ra, rp, North);

            double radialSpeed = Vector3d.Dot(after, r.normalized);
            if (radius < (rp + ra) / 2)
                Assert.That(radialSpeed, Is.GreaterThan(1), "near the periapsis: climbing");
            else
                Assert.That(radialSpeed, Is.LessThan(-1), "near the apoapsis: falling");
            var el = OrbitMath.Elements(r, after, Mu);
            Assert.That(el.ApoapsisRadius, Is.EqualTo(ra).Within(1e-3));
            Assert.That(el.PeriapsisRadius, Is.EqualTo(rp).Within(1e-3));
        }
    }

    [Test]
    public void AboveTheTargetApoapsis_ClampsToTheApoapsis()
    {
        double target = R + 100000;
        var r = new Vector3d(R + 200000, 0, 0);
        var v = new Vector3d(-50, 2000, 0);

        Vector3d after = v + OrbitGuidance.DeltaVToOrbit(r, v, Mu, target, target, North);

        Assert.That(Vector3d.Dot(after, r.normalized), Is.EqualTo(0).Within(1e-6));
        Assert.That(after.magnitude, Is.EqualTo(Math.Sqrt(Mu * target) / target).Within(1e-6));
    }

    [Test]
    public void IsReachable_WithinTheIntervalAndItsTolerance()
    {
        double rp = R + 100000, ra = R + 500000;
        Assert.IsTrue(OrbitGuidance.IsReachable(rp, ra, rp));
        Assert.IsTrue(OrbitGuidance.IsReachable((rp + ra) / 2, ra, rp));
        Assert.IsTrue(OrbitGuidance.IsReachable(ra + 500, ra, rp), "0.1 % tolerance");
        Assert.IsTrue(OrbitGuidance.IsReachable(rp - 500, ra, rp), "0.1 % tolerance");
        Assert.IsFalse(OrbitGuidance.IsReachable(ra + 5000, ra, rp));
        Assert.IsFalse(OrbitGuidance.IsReachable(rp - 5000, ra, rp));
    }

    [Test]
    public void ApsisRadiiInReverseOrder_GiveTheSameResult()
    {
        double r0 = R + 100000, ra = R + 500000;
        StateAtPeriapsis(r0, r0, 0, out var r, out var v);

        AssertVector(OrbitGuidance.DeltaVToOrbit(r, v, Mu, ra, r0, North),
            OrbitGuidance.DeltaVToOrbit(r, v, Mu, r0, ra, North), 1e-9, "dv");
    }

    [Test]
    public void SplitComponents_RebuildTheDeltaV()
    {
        double r0 = R + 100000;
        StateAtPeriapsis(r0, R + 300000, 5, out var r1, out var v1);
        KeplerPropagator.Propagate(r1, v1, Mu, 700, out var r, out var v);
        Vector3d n = OrbitMath.Rotate(North, new Vector3d(0, 1, 0), 12 * Deg);

        Vector3d dv = OrbitGuidance.DeltaVToOrbit(r, v, Mu, R + 400000, r0, n);
        OrbitGuidance.Split(dv, r, v, out double radial, out double horizontal, out double normal);

        Assert.That(Math.Sqrt(radial * radial + horizontal * horizontal + normal * normal),
            Is.EqualTo(dv.magnitude).Within(1e-6));
    }

    [Test]
    public void InvalidInputs_GiveNaN()
    {
        var v = new Vector3d(0, 2000, 0);
        Vector3d atCenter = OrbitGuidance.DeltaVToOrbit(Vector3d.zero, v, Mu, R + 100000, R + 100000, North);
        Vector3d noPlane = OrbitGuidance.DeltaVToOrbit(new Vector3d(R + 100000, 0, 0), v, Mu, R + 100000, R + 100000, Vector3d.zero);

        Assert.That(double.IsNaN(atCenter.x) && double.IsNaN(atCenter.y) && double.IsNaN(atCenter.z));
        Assert.That(double.IsNaN(noPlane.x) && double.IsNaN(noPlane.y) && double.IsNaN(noPlane.z));
    }

    // The game's frames are the Zup physics frame with Y and Z swapped (API doc of the *Zup
    // methods: "with Y and Z axes swapped"), then rotated. The swap is a reflection, so the
    // physics normal r × v maps to forward × up in the game frame, not up × forward.
    [Test]
    public void AimInFrame_MatchesTheZupDeltaV_AfterTheYZSwapAndARotation()
    {
        StateAtPeriapsis(R + 100000, R + 300000, 25, out var r1, out var v1);
        KeplerPropagator.Propagate(r1, v1, Mu, 400, out var r, out var v);
        Vector3d n = OrbitMath.Rotate(North, new Vector3d(1, 0, 0), 40 * Deg);
        Vector3d dv = OrbitGuidance.DeltaVToOrbit(r, v, Mu, R + 350000, R + 120000, n);
        OrbitGuidance.Split(dv, r, v, out double radial, out double horizontal, out double normal);
        Assert.That(Math.Abs(normal), Is.GreaterThan(100), "sanity: a real normal component");

        Vector3d axis = new Vector3d(1, 2, 3).normalized;
        Func<Vector3d, Vector3d> toGame = zup => OrbitMath.Rotate(new Vector3d(zup.x, zup.z, zup.y), axis, 0.7);
        Vector3d up = toGame(r.normalized);
        Vector3d horizontalVelocity = v - Vector3d.Dot(v, r.normalized) * r.normalized;
        Vector3d forward = toGame(horizontalVelocity.normalized);

        Vector3d aim = OrbitGuidance.AimInFrame(radial, horizontal, normal, up, forward);

        AssertVector(toGame(dv).normalized, aim, 1e-9, "aim");
    }

    [Test]
    public void TargetOrbit_FromState_GivesTheApsidesAndThePlaneNormal()
    {
        double rp = R + 100000, ra = R + 500000;
        StateAtPeriapsis(rp, ra, 20, out var r, out var v);

        var target = TargetOrbit.FromState(r, v, Mu);

        Assert.That(target.Ra, Is.EqualTo(ra).Within(1e-3));
        Assert.That(target.Rp, Is.EqualTo(rp).Within(1e-3));
        AssertVector(Vector3d.Cross(r, v).normalized, target.PlaneNormal, 1e-12, "normal");
        Assert.That(target.PlaneNormal.magnitude, Is.EqualTo(1).Within(1e-12));
    }
}

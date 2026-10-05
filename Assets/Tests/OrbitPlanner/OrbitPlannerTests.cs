using System;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class OrbitPlannerTests
{
    // Kerbin.
    const double Mu = 3.5316e12;
    const double R = 600000;
    const double Atm = 70000;
    const double Deg = Math.PI / 180;

    // Vessel at the periapsis (on the +x axis) of an orbit with the given apsis altitudes and inclination.
    static void StateAtPeriapsis(double peAlt, double apAlt, double incDeg, out Vector3d r, out Vector3d v)
    {
        double rp = R + peAlt, ra = R + apAlt;
        double speed = Math.Sqrt(Mu * (2 / rp - 2 / (rp + ra)));
        r = new Vector3d(rp, 0, 0);
        v = new Vector3d(0, speed * Math.Cos(incDeg * Deg), speed * Math.Sin(incDeg * Deg));
    }

    static void AssertRelative(double expected, double actual, double tolerance, string what)
    {
        Assert.That(Math.Abs(actual - expected) / Math.Abs(expected), Is.LessThan(tolerance),
            $"{what}: expected {expected}, got {actual}");
    }

    [Test]
    public void Elements_CircularInclinedOrbit()
    {
        StateAtPeriapsis(100000, 100000, 30, out var r, out var v);
        var el = OrbitMath.Elements(r, v, Mu);
        Assert.IsTrue(el.IsClosed);
        AssertRelative(R + 100000, el.ApoapsisRadius, 1e-6, "ra");
        AssertRelative(R + 100000, el.PeriapsisRadius, 1e-6, "rp");
        Assert.AreEqual(30.0, el.InclinationRad / Deg, 1e-6);
    }

    [Test]
    public void Elements_EscapeTrajectoryIsNotClosed()
    {
        double rMag = R + 100000;
        var r = new Vector3d(rMag, 0, 0);
        var v = new Vector3d(0, 1.5 * Math.Sqrt(2 * Mu / rMag), 0);
        Assert.IsFalse(OrbitMath.Elements(r, v, Mu).IsClosed);
    }

    [Test]
    public void TimeToTrueAnomaly_ApoapsisIsHalfAPeriodAway()
    {
        StateAtPeriapsis(100000, 500000, 0, out var r, out var v);
        var el = OrbitMath.Elements(r, v, Mu);
        double t = OrbitMath.TimeToTrueAnomaly(r, v, Mu, Math.PI);
        AssertRelative(el.Period / 2, t, 1e-6, "time to apoapsis");
        KeplerPropagator.Propagate(r, v, Mu, t, out var r1, out _);
        AssertRelative(R + 500000, r1.magnitude, 1e-5, "radius at apoapsis");
    }

    [Test]
    public void TimeToDirection_QuarterTurnOnCircularOrbit()
    {
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        var el = OrbitMath.Elements(r, v, Mu);
        double t = OrbitMath.TimeToDirection(r, v, Mu, new Vector3d(0, 1, 0));
        AssertRelative(el.Period / 4, t, 1e-6, "time to +y");
    }
}

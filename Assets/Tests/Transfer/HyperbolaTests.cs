using System;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class HyperbolaTests
{
    // EXTERNAL DATA (KSP wiki, stock values): Kerbin and Duna.
    const double MuKerbin = 3.5316e12;
    const double SoiKerbin = 84159286;
    const double MuDuna = 3.0136321e11;
    const double RadiusDuna = 320000;

    const double Deg = Math.PI / 180;

    static double AngleDeg(Vector3d a, Vector3d b) =>
        Math.Atan2(Vector3d.Cross(a, b).magnitude, Vector3d.Dot(a, b)) / Deg;

    // Time from (r, v) until the radius reaches 'radius' on an outgoing hyperbola, by bisection on
    // the propagator: independent of the closed forms under test.
    static double TimeToRadius(Vector3d r, Vector3d v, double mu, double radius)
    {
        double hi = 1;
        for (int i = 0; i < 200; i++)
        {
            KeplerPropagator.Propagate(r, v, mu, hi, out var rh, out _);
            if (rh.magnitude >= radius)
                break;
            hi *= 2;
        }
        double lo = 0;
        for (int i = 0; i < 200; i++)
        {
            double mid = 0.5 * (lo + hi);
            KeplerPropagator.Propagate(r, v, mu, mid, out var rm, out _);
            if (rm.magnitude < radius)
                lo = mid;
            else
                hi = mid;
        }
        return 0.5 * (lo + hi);
    }

    [TestCase(1000.0, 0.0)]
    [TestCase(1000.0, 35.0)]
    [TestCase(2500.0, -70.0)]
    [TestCase(400.0, 120.0)]
    public void VelocityForAsymptote_PropagatedToSoi_MatchesDirection(double vInfMag, double elevationDeg)
    {
        var r = new Vector3d(700000, 0, 0);
        var vInf = vInfMag * new Vector3d(0.3 * Math.Cos(elevationDeg * Deg), Math.Cos(elevationDeg * Deg), Math.Sin(elevationDeg * Deg)).normalized;

        Assert.That(Hyperbola.VelocityForAsymptote(r, vInf, MuKerbin, out var vA, out var vB), Is.True);

        foreach (var v in new[] { vA, vB })
        {
            // Energy: v² = v∞² + 2μ/r.
            Assert.That(v.magnitude, Is.EqualTo(Math.Sqrt(vInfMag * vInfMag + 2 * MuKerbin / r.magnitude)).Within(1e-6).Percent);

            double t = TimeToRadius(r, v, MuKerbin, SoiKerbin);
            KeplerPropagator.Propagate(r, v, MuKerbin, t, out var rSoi, out var vSoi);
            Assert.That(rSoi.magnitude, Is.EqualTo(SoiKerbin).Within(0.01).Percent);

            // At the SOI the excess velocity (the asymptote of the state reached) is the one asked for.
            Vector3d asymptote = Hyperbola.Asymptote(rSoi, vSoi, MuKerbin);
            Assert.That(AngleDeg(asymptote, vInf), Is.LessThan(0.2), "asymptote direction at the SOI");
            Assert.That(asymptote.magnitude, Is.EqualTo(vInfMag).Within(1).Percent, "asymptote magnitude at the SOI");

            // Far out (1e10 m), the raw velocity itself tends to v∞: checks Asymptote against plain
            // physics. Chained 1e4 s steps: KeplerPropagator does not converge on one hyperbolic
            // step of ~1e6 s (see the Task 3 report).
            Vector3d rFar = rSoi, vFar = vSoi;
            for (int i = 0; i < 100000 && rFar.magnitude < 1e10; i++)
                KeplerPropagator.Propagate(rFar, vFar, MuKerbin, 1e4, out rFar, out vFar);
            Assert.That(rFar.magnitude, Is.GreaterThanOrEqualTo(1e10));
            Assert.That(AngleDeg(vFar, vInf), Is.LessThan(0.2), "raw velocity direction far out");
            Assert.That(vFar.magnitude, Is.EqualTo(vInfMag).Within(1).Percent, "raw velocity magnitude far out");
        }

        // The two solutions turn in opposite directions about r × v∞.
        Vector3d n = Vector3d.Cross(r, vInf);
        Assert.That(Vector3d.Dot(Vector3d.Cross(r, vA), n), Is.GreaterThan(0));
        Assert.That(Vector3d.Dot(Vector3d.Cross(r, vB), n), Is.LessThan(0));
    }

    [Test]
    public void VelocityForAsymptote_OnTheAsymptoteLine_ReturnsFalse()
    {
        var r = new Vector3d(700000, 0, 0);
        Assert.That(Hyperbola.VelocityForAsymptote(r, new Vector3d(1000, 0, 0), MuKerbin, out _, out _), Is.False);
        Assert.That(Hyperbola.VelocityForAsymptote(r, new Vector3d(-1000, 0, 0), MuKerbin, out _, out _), Is.False);
        Assert.That(Hyperbola.VelocityForAsymptote(r, Vector3d.zero, MuKerbin, out _, out _), Is.False);
    }

    [Test]
    public void Asymptote_ClosedOrbit_IsZero()
    {
        var r = new Vector3d(700000, 0, 0);
        var v = new Vector3d(0, Math.Sqrt(MuKerbin / 700000), 0);
        Assert.That(Hyperbola.Asymptote(r, v, MuKerbin).magnitude, Is.EqualTo(0));
    }

    [Test]
    public void CaptureDeltaV_KnownValue()
    {
        // Duna, v∞ = 1000 m/s, periapsis at 420 km altitude (radius 740 km), circular target.
        // By hand: vp = sqrt(1000² + 2μ/rp) = 1347.032 m/s, vcirc = sqrt(μ/rp) = 638.160 m/s,
        // Δv = 708.872 m/s.
        double rp = RadiusDuna + 420000;
        Assert.That(Hyperbola.CaptureDeltaV(1000, rp, rp, MuDuna), Is.EqualTo(708.872).Within(0.01));
    }
}

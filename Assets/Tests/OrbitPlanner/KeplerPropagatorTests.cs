using System;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class KeplerPropagatorTests
{
    const double Mu = 398600e9; // m³/s²

    // Found by the Lambert round-trip test: on very eccentric orbits, Newton started from the
    // mean-motion guess could cycle near periapsis and return a position 1.6e9 m off.
    [Test]
    public void OneShot_MatchesSmallSteps_OnEccentricOrbits([Values(0.6, 0.8, 0.95)] double e)
    {
        double rp = 6.5e6;
        double a = rp / (1 - e);
        double period = 2 * Math.PI * Math.Sqrt(a * a * a / Mu);
        var r0 = new Vector3d(rp, 0, 0);
        var v0 = new Vector3d(0, Math.Sqrt(Mu * (1 + e) / rp), 0);

        for (int start = 0; start < 12; start++)
        {
            KeplerPropagator.Propagate(r0, v0, Mu, period * start / 12.0, out var rs, out var vs);
            for (int k = 1; k < 20; k++)
            {
                double dt = (k / 20.0 + 0.013) * period;
                KeplerPropagator.Propagate(rs, vs, Mu, dt, out var rOne, out _);

                Vector3d r = rs, v = vs;
                for (int step = 0; step < 400; step++)
                    KeplerPropagator.Propagate(r, v, Mu, dt / 400, out r, out v);

                Assert.That((rOne - r).magnitude, Is.LessThan(1),
                    $"e {e}, start {start}/12, dt {k}/20 of a period: one step and small steps differ by {(rOne - r).magnitude} m");
            }
        }
    }

    // The exact state from the Lambert round trip (a ≈ 12 287 km, near apoapsis, passing periapsis):
    // before the bracketed fallback, one step landed 1.6e9 m away.
    [Test]
    public void OneShot_ChaoticNewtonCase_MatchesSmallSteps()
    {
        var r0 = new Vector3d(-16355501.955459528, 10281516.225944694, 4116484.0259645809);
        var v0 = new Vector3d(2233.1977783779816, -1669.9540008710173, -376.93476127446706);
        double dt = 6552.3565791091623;

        KeplerPropagator.Propagate(r0, v0, Mu, dt, out var rOne, out var vOne);
        Vector3d r = r0, v = v0;
        for (int step = 0; step < 400; step++)
            KeplerPropagator.Propagate(r, v, Mu, dt / 400, out r, out v);

        Assert.That((rOne - r).magnitude, Is.LessThan(1), $"position differs by {(rOne - r).magnitude} m");
        Assert.That((vOne - v).magnitude, Is.LessThan(1e-3), $"velocity differs by {(vOne - v).magnitude} m/s");
    }

    [Test]
    public void Backwards_UndoesForwards_OnEccentricOrbit()
    {
        double rp = 6.5e6, e = 0.9;
        var r0 = new Vector3d(rp, 0, 0);
        var v0 = new Vector3d(0, Math.Sqrt(Mu * (1 + e) / rp), 0);
        for (int k = 1; k < 10; k++)
        {
            double dt = 3000.0 * k * k;
            KeplerPropagator.Propagate(r0, v0, Mu, dt, out var r1, out var v1);
            KeplerPropagator.Propagate(r1, v1, Mu, -dt, out var rBack, out _);
            Assert.That((rBack - r0).magnitude, Is.LessThan(1), $"dt {dt}");
        }
    }
}

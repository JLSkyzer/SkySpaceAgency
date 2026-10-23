using System;
using System.Collections.Generic;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class PatchedPropagatorTests
{
    const double Deg = Math.PI / 180;
    static readonly Impulse[] None = new Impulse[0];

    BodySystem _s;
    int _kerbol, _kerbin, _mun;

    [SetUp]
    public void SetUp()
    {
        _s = KerbolFixture.Build();
        _kerbol = _s["Kerbol"].Index;
        _kerbin = _s["Kerbin"].Index;
        _mun = _s["Mun"].Index;
    }

    static double AngleDeg(Vector3d a, Vector3d b) =>
        Math.Atan2(Vector3d.Cross(a, b).magnitude, Vector3d.Dot(a, b)) / Deg;

    static Vector3d RotateZ(Vector3d p, double angle) =>
        new Vector3d(p.x * Math.Cos(angle) - p.y * Math.Sin(angle), p.x * Math.Sin(angle) + p.y * Math.Cos(angle), p.z);

    // Circular equatorial orbit around Kerbin, at 'angle' from +x.
    void Circular(double radius, double angle, out Vector3d r, out Vector3d v)
    {
        double speed = Math.Sqrt(_s[_kerbin].Mu / radius);
        r = RotateZ(new Vector3d(radius, 0, 0), angle);
        v = RotateZ(new Vector3d(0, speed, 0), angle);
    }

    // Equatorial prograde trajectory from periapsis rp at speed vp, rotated so the vessel reaches the
    // Mun's orbit radius where the Mun is at that moment, 'offsetDeg' ahead of it.
    void AimAtMun(double rp, double vp, double offsetDeg, out Vector3d r, out Vector3d v)
    {
        double mu = _s[_kerbin].Mu;
        var rRef = new Vector3d(rp, 0, 0);
        var vRef = new Vector3d(0, vp, 0);
        _s.StateRelativeToParent(_mun, 0, out var munR, out _);
        double munRadius = munR.magnitude;

        double lo = 0, hi = 1;
        for (int i = 0; i < 100; i++)
        {
            KeplerPropagator.Propagate(rRef, vRef, mu, hi, out var rh, out _);
            if (rh.magnitude >= munRadius)
                break;
            lo = hi;
            hi *= 2;
        }
        for (int i = 0; i < 100; i++)
        {
            double mid = 0.5 * (lo + hi);
            KeplerPropagator.Propagate(rRef, vRef, mu, mid, out var rm, out _);
            if (rm.magnitude < munRadius)
                lo = mid;
            else
                hi = mid;
        }
        double tc = 0.5 * (lo + hi);
        KeplerPropagator.Propagate(rRef, vRef, mu, tc, out var rc, out _);
        _s.StateRelativeToParent(_mun, tc, out var munAtTc, out _);
        double phi = Math.Atan2(munAtTc.y, munAtTc.x) - Math.Atan2(rc.y, rc.x) + offsetDeg * Deg;
        r = RotateZ(rRef, phi);
        v = RotateZ(vRef, phi);
    }

    // First time the vessel (in Kerbin's frame) is inside the Mun's SOI, by plain dense sampling.
    double DenseFirstMunEntry(Vector3d r0, Vector3d v0, double until, double step)
    {
        double mu = _s[_kerbin].Mu;
        for (double t = step; t <= until; t += step)
        {
            KeplerPropagator.Propagate(r0, v0, mu, t, out var r, out _);
            _s.StateRelativeToParent(_mun, t, out var munR, out _);
            if ((r - munR).magnitude < _s[_mun].Soi)
                return t;
        }
        return double.NaN;
    }

    static void AssertFinite(Patch p)
    {
        foreach (double x in new[] { p.StartUT, p.EndUT, p.StartR.magnitude, p.StartV.magnitude, p.EndR.magnitude, p.EndV.magnitude, p.PeriapsisRadius })
            Assert.That(double.IsNaN(x) || double.IsInfinity(x), Is.False, $"non-finite value in a patch around body {p.Body}");
    }

    [Test]
    public void Escape_FromKerbin_ExitsAtSoiWithAsymptote()
    {
        var r = new Vector3d(700000, 0, 0);
        // Well out of the equator, so neither the Mun nor Minmus is on the way.
        var vInf = 1000 * new Vector3d(0.3, Math.Cos(60 * Deg), Math.Sin(60 * Deg)).normalized;
        Assert.That(Hyperbola.VelocityForAsymptote(r, vInf, _s[_kerbin].Mu, out var v, out _), Is.True);

        var patches = PatchedPropagator.Propagate(_s, _kerbin, r, v, 0, 2e6, None);
        foreach (var p in patches)
            AssertFinite(p);

        Assert.That(patches.Count, Is.EqualTo(2));
        var k = patches[0];
        Assert.That(k.Body, Is.EqualTo(_kerbin));
        Assert.That(k.End, Is.EqualTo(PatchEnd.Escape));
        Assert.That(k.NextBody, Is.EqualTo(_kerbol));
        Assert.That(k.EndR.magnitude, Is.EqualTo(_s[_kerbin].Soi).Within(1e-6).Percent);
        // Already outgoing at the start: the hyperbola's periapsis is behind, and it is a real one.
        Assert.That(Vector3d.Dot(r, v), Is.GreaterThan(0));
        Assert.That(k.PeriapsisUT, Is.LessThan(0));
        KeplerPropagator.Propagate(r, v, _s[_kerbin].Mu, k.PeriapsisUT, out var rPe, out var vPe);
        Assert.That(rPe.magnitude, Is.EqualTo(k.PeriapsisRadius).Within(1.0));
        Assert.That(Math.Abs(Vector3d.Dot(rPe.normalized, vPe)), Is.LessThan(1e-3));

        // The state reached at the SOI leaves along the asymptote asked for.
        Vector3d asymptote = Hyperbola.Asymptote(k.EndR, k.EndV, _s[_kerbin].Mu);
        Assert.That(AngleDeg(asymptote, vInf), Is.LessThan(0.2));
        Assert.That(asymptote.magnitude, Is.EqualTo(1000).Within(1).Percent);

        // The Kerbol patch starts where the Kerbin patch ended, in Kerbol's frame.
        var sun = patches[1];
        Assert.That(sun.Body, Is.EqualTo(_kerbol));
        Assert.That(sun.StartUT, Is.EqualTo(k.EndUT));
        _s.StateRelativeToParent(_kerbin, k.EndUT, out var kr, out var kv);
        Assert.That((sun.StartR - (k.EndR + kr)).magnitude, Is.LessThan(1e-3));
        Assert.That((sun.StartV - (k.EndV + kv)).magnitude, Is.LessThan(1e-9));
        Assert.That(sun.End, Is.EqualTo(PatchEnd.HorizonReached));
        Assert.That(sun.EndUT, Is.EqualTo(2e6));
    }

    [Test]
    public void HohmannToMun_ReportsEncounter_ThenPeriapsis()
    {
        double mu = _s[_kerbin].Mu;
        double rp = 700000;
        double ra = 12_300_000; // just beyond the Mun's orbit, so the vessel crosses it
        double vp = Math.Sqrt(mu * (2 / rp - 2 / (rp + ra)));
        AimAtMun(rp, vp, -9, out var r, out var v); // trailing the Mun by 9°: periapsis ≈ 450 km

        var patches = PatchedPropagator.Propagate(_s, _kerbin, r, v, 0, 3e5, None);
        foreach (var p in patches)
            AssertFinite(p);

        Assert.That(patches.Count, Is.GreaterThanOrEqualTo(2));
        var k = patches[0];
        Assert.That(k.End, Is.EqualTo(PatchEnd.Encounter));
        Assert.That(k.NextBody, Is.EqualTo(_mun));

        // Entry time against plain 1 s sampling, independent of the event search.
        double dense = DenseFirstMunEntry(r, v, 3e5, 1);
        Assert.That(k.EndUT, Is.EqualTo(dense).Within(1.0));

        var m = patches[1];
        Assert.That(m.Body, Is.EqualTo(_mun));
        Assert.That(m.StartUT, Is.EqualTo(k.EndUT));
        Assert.That(m.StartR.magnitude, Is.EqualTo(_s[_mun].Soi).Within(1e-6).Percent);
        Assert.That(m.StartR.magnitude, Is.LessThan(_s[_mun].Soi), "the Mun patch starts inside its SOI");

        // The periapsis lies inside the patch, above the surface, and is a real periapsis of that conic.
        Assert.That(m.PeriapsisRadius, Is.GreaterThan(_s[_mun].Radius));
        Assert.That(m.PeriapsisRadius, Is.LessThan(_s[_mun].Soi));
        Assert.That(m.PeriapsisUT, Is.GreaterThan(m.StartUT));
        Assert.That(m.PeriapsisUT, Is.LessThan(m.EndUT));
        KeplerPropagator.Propagate(m.StartR, m.StartV, _s[_mun].Mu, m.PeriapsisUT - m.StartUT, out var rPe, out var vPe);
        Assert.That(rPe.magnitude, Is.EqualTo(m.PeriapsisRadius).Within(1.0));
        Assert.That(Math.Abs(Vector3d.Dot(rPe.normalized, vPe)), Is.LessThan(1e-3), "no radial speed at the periapsis");

        // A flyby: it leaves the Mun again, back into Kerbin's frame.
        Assert.That(m.End, Is.EqualTo(PatchEnd.Escape));
        Assert.That(m.NextBody, Is.EqualTo(_kerbin));
        Assert.That(patches[2].Body, Is.EqualTo(_kerbin));
        TestContext.WriteLine($"Mun periapsis {m.PeriapsisRadius:F0} m at UT {m.PeriapsisUT:F0}, entry at {k.EndUT:F1} (dense {dense})");
    }

    // Review Focus 3: an escape from Kerbin that happens to cross the Mun's SOI. The propagator must
    // report the encounter, even though nothing planned it.
    [Test]
    public void MunCrossesEscape_ReportsUnplannedEncounter()
    {
        double mu = _s[_kerbin].Mu;
        double rp = 700000;
        double vp = Math.Sqrt(1000 * 1000 + 2 * mu / rp); // v∞ = 1000 m/s: an escape
        AimAtMun(rp, vp, 3, out var r, out var v);

        var patches = PatchedPropagator.Propagate(_s, _kerbin, r, v, 0, 1e6, None);
        foreach (var p in patches)
            AssertFinite(p);

        Assert.That(patches[0].End, Is.EqualTo(PatchEnd.Encounter));
        Assert.That(patches[0].NextBody, Is.EqualTo(_mun));
        Assert.That(patches[1].Body, Is.EqualTo(_mun));
        double dense = DenseFirstMunEntry(r, v, 1e6, 1);
        Assert.That(patches[0].EndUT, Is.EqualTo(dense).Within(1.0));
        Assert.That(patches[0].EndR.magnitude, Is.LessThan(_s[_kerbin].Soi));
    }

    // A grazing pass that dips into the SOI between two samples must still be caught.
    [Test]
    public void GrazingPass_BetweenSamples_IsCaught()
    {
        double mu = _s[_kerbin].Mu;
        double rp = 700000;
        double vp = Math.Sqrt(1000 * 1000 + 2 * mu / rp);
        // About 12° ahead: the closest approach is near the edge of the Mun's SOI (radius ≈ 11.6°).
        for (double offset = 9; offset <= 14; offset += 0.25)
        {
            AimAtMun(rp, vp, offset, out var r, out var v);
            var patches = PatchedPropagator.Propagate(_s, _kerbin, r, v, 0, 1e6, None);
            double dense = DenseFirstMunEntry(r, v, 2e5, 1);
            bool found = patches[0].End == PatchEnd.Encounter;
            Assert.That(found, Is.EqualTo(!double.IsNaN(dense)), $"offset {offset}°: dense sampling entry {dense}");
            if (found)
                Assert.That(patches[0].EndUT, Is.EqualTo(dense).Within(1.0), $"offset {offset}°");
        }
    }

    [Test]
    public void Impulse_AppliedAtUT()
    {
        double mu = _s[_kerbin].Mu;
        Circular(700000, 0, out var r, out var v);
        var impulses = new List<Impulse>
        {
            new Impulse(1200, new Vector3d(0, 0, 50)), // given out of order: sorted by the propagator
            new Impulse(500, new Vector3d(-30, 20, 0)),
        };

        var patches = PatchedPropagator.Propagate(_s, _kerbin, r, v, 0, 3000, impulses);
        Assert.That(patches.Count, Is.EqualTo(3));

        Assert.That(patches[0].End, Is.EqualTo(PatchEnd.Impulse));
        Assert.That(patches[0].EndUT, Is.EqualTo(500));
        Assert.That(patches[0].NextBody, Is.EqualTo(_kerbin));
        KeplerPropagator.Propagate(r, v, mu, 500, out var r1, out var v1);
        Assert.That((patches[1].StartR - r1).magnitude, Is.LessThan(1e-6));
        Assert.That((patches[1].StartV - (v1 + new Vector3d(-30, 20, 0))).magnitude, Is.LessThan(1e-9));

        Assert.That(patches[1].End, Is.EqualTo(PatchEnd.Impulse));
        Assert.That(patches[1].EndUT, Is.EqualTo(1200));
        KeplerPropagator.Propagate(patches[1].StartR, patches[1].StartV, mu, 700, out var r2, out var v2);
        Assert.That((patches[2].StartV - (v2 + new Vector3d(0, 0, 50))).magnitude, Is.LessThan(1e-9));

        Assert.That(patches[2].End, Is.EqualTo(PatchEnd.HorizonReached));
        Assert.That(patches[2].EndUT, Is.EqualTo(3000));
    }

    [Test]
    public void Impulse_AtStartUT_AppliedToStartState_PastOnesIgnored()
    {
        Circular(700000, 0, out var r, out var v);
        var impulses = new[]
        {
            new Impulse(-10, new Vector3d(0, 500, 0)), // already past: ignored
            new Impulse(100, new Vector3d(0, 25, 0)),  // at the start
        };
        var patches = PatchedPropagator.Propagate(_s, _kerbin, r, v, 100, 400, impulses);
        Assert.That(patches.Count, Is.EqualTo(1));
        Assert.That((patches[0].StartV - (v + new Vector3d(0, 25, 0))).magnitude, Is.LessThan(1e-12));
        Assert.That(patches[0].End, Is.EqualTo(PatchEnd.HorizonReached));
    }

    [Test]
    public void NoEvent_EndsAtHorizon()
    {
        double mu = _s[_kerbin].Mu;
        Circular(700000, 0.3, out var r, out var v);
        double period = 2 * Math.PI * Math.Sqrt(Math.Pow(700000, 3) / mu);

        var patches = PatchedPropagator.Propagate(_s, _kerbin, r, v, 1000, 1000 + 3.5 * period, None);
        Assert.That(patches.Count, Is.EqualTo(1));
        var p = patches[0];
        AssertFinite(p);
        Assert.That(p.End, Is.EqualTo(PatchEnd.HorizonReached));
        Assert.That(p.EndUT, Is.EqualTo(1000 + 3.5 * period));
        Assert.That(p.NextBody, Is.EqualTo(-1));
        Assert.That(p.EndR.magnitude, Is.EqualTo(700000).Within(1e-6).Percent);
        Assert.That(p.PeriapsisRadius, Is.EqualTo(700000).Within(1e-6).Percent);
    }

    [Test]
    public void Impact_EndsAtAtmosphere()
    {
        double mu = _s[_kerbin].Mu;
        double limit = _s[_kerbin].Radius + _s[_kerbin].AtmosphereDepth;
        // Apoapsis 700 km, periapsis 30 km: it enters the atmosphere on the way down.
        double ra = 700000, rp = 630000;
        var r = new Vector3d(ra, 0, 0);
        var v = new Vector3d(0, Math.Sqrt(mu * (2 / ra - 2 / (ra + rp))), 0);

        var patches = PatchedPropagator.Propagate(_s, _kerbin, r, v, 0, 1e5, None);
        Assert.That(patches.Count, Is.EqualTo(1));
        var p = patches[0];
        Assert.That(p.End, Is.EqualTo(PatchEnd.Impact));
        Assert.That(p.EndR.magnitude, Is.EqualTo(limit).Within(1.0));
        Assert.That(Vector3d.Dot(p.EndR, p.EndV), Is.LessThan(0), "on the way down");
        Assert.That(p.EndUT, Is.LessThan(p.PeriapsisUT));
        Assert.That(p.PeriapsisRadius, Is.EqualTo(rp).Within(1e-6).Percent);
    }

    [Test]
    public void MaxPatches_Respected()
    {
        Circular(700000, 0, out var r, out var v);
        var impulses = new List<Impulse>();
        for (int i = 1; i <= 10; i++)
            impulses.Add(new Impulse(100 * i, new Vector3d(0, 0, 1)));
        var patches = PatchedPropagator.Propagate(_s, _kerbin, r, v, 0, 5000, impulses, maxPatches: 4);
        Assert.That(patches.Count, Is.EqualTo(4));
        Assert.That(patches[3].End, Is.EqualTo(PatchEnd.Impulse));
    }
}

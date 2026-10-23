using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        Assert.That(patches[3].NextBody, Is.Not.EqualTo(-1), "cut by maxPatches: the trajectory goes on");
    }

    [Test]
    public void UntilBeforeStart_SinglePatchAtHorizon()
    {
        Circular(700000, 0, out var r, out var v);
        foreach (double until in new[] { 100.0, 50.0 })
        {
            var patches = PatchedPropagator.Propagate(_s, _kerbin, r, v, 100, until, new[] { new Impulse(100, new Vector3d(0, 10, 0)) });
            Assert.That(patches.Count, Is.EqualTo(1));
            var p = patches[0];
            Assert.That(p.End, Is.EqualTo(PatchEnd.HorizonReached));
            Assert.That(p.NextBody, Is.EqualTo(-1));
            Assert.That(p.StartUT, Is.EqualTo(100));
            Assert.That(p.EndUT, Is.EqualTo(100));
            Assert.That((p.EndR - r).magnitude, Is.EqualTo(0));
            Assert.That((p.EndV - v).magnitude, Is.EqualTo(0), "no impulse at or after the horizon");
        }
    }

    // A closed orbit entirely inside the atmosphere: it is already "impacted" at the start.
    [Test]
    public void Impact_ClosedOrbitBelowLimit_AtStart()
    {
        double mu = _s[_kerbin].Mu;
        double rp = 610000, ra = 650000; // both below the 670 km atmosphere top
        double a = 0.5 * (rp + ra);
        double vp = Math.Sqrt(mu * (2 / rp - 1 / a));
        KeplerPropagator.Propagate(new Vector3d(rp, 0, 0), new Vector3d(0, vp, 0), mu, 300, out var r, out var v);
        Assert.That(Vector3d.Dot(r, v), Is.GreaterThan(0), "climbing");

        var patches = PatchedPropagator.Propagate(_s, _kerbin, r, v, 1000, 1e5, None);
        Assert.That(patches.Count, Is.EqualTo(1));
        Assert.That(patches[0].End, Is.EqualTo(PatchEnd.Impact));
        Assert.That(patches[0].EndUT, Is.EqualTo(1000));
    }

    [Test]
    public void Impact_OnAirlessBody_AtSurface()
    {
        double mu = _s[_mun].Mu;
        double radius = _s[_mun].Radius;
        Assert.That(_s[_mun].AtmosphereDepth, Is.EqualTo(0));
        double ra = 250000, rp = 150000; // the periapsis is below the surface
        var r = new Vector3d(ra, 0, 0);
        var v = new Vector3d(0, Math.Sqrt(mu * (2 / ra - 2 / (ra + rp))), 0);

        var patches = PatchedPropagator.Propagate(_s, _mun, r, v, 0, 1e5, None);
        Assert.That(patches.Count, Is.EqualTo(1));
        var p = patches[0];
        AssertFinite(p);
        Assert.That(p.End, Is.EqualTo(PatchEnd.Impact));
        Assert.That(p.EndR.magnitude, Is.EqualTo(radius).Within(1.0));
        Assert.That(Vector3d.Dot(p.EndR, p.EndV), Is.LessThan(0));
    }

    // A planet with two moons on the same orbit, close together: the vessel arrives between them
    // and the earlier SOI entry, per dense sampling, wins.
    [Test]
    public void TwoChildrenNearAtOnce_EarlierEntryWins()
    {
        double mu = 3.5316e12, orbit = 12_000_000, soi = 2_400_000, separation = 1_000_000; // the SOIs overlap
        double moonSpeed = Math.Sqrt(mu / orbit), half = 0.5 * separation / orbit;
        var nodes = new List<BodyNode>
        {
            new BodyNode { Name = "Planet", Index = 0, Mu = mu, Radius = 600000, Soi = double.PositiveInfinity },
        };
        foreach (double angle in new[] { -half, half })
            nodes.Add(new BodyNode
            {
                Name = angle < 0 ? "Behind" : "Ahead", Index = nodes.Count, ParentIndex = 0, Mu = 6.5e10, Radius = 200000, Soi = soi,
                EpochPosition = RotateZ(new Vector3d(orbit, 0, 0), angle),
                EpochVelocity = RotateZ(new Vector3d(0, moonSpeed, 0), angle),
            });
        var s = new BodySystem(nodes);

        // Hohmann-like arc from 700 km, apoapsis just past the moons' orbit, rotated so that it
        // crosses their orbit at the midpoint between them.
        double rp = 700000, ra = 12_300_000;
        double vp = Math.Sqrt(mu * (2 / rp - 2 / (rp + ra)));
        var rRef = new Vector3d(rp, 0, 0);
        var vRef = new Vector3d(0, vp, 0);
        double lo = 0, hi = Math.PI * Math.Sqrt(Math.Pow(0.5 * (rp + ra), 3) / mu); // to the apoapsis
        for (int i = 0; i < 100; i++)
        {
            double mid = 0.5 * (lo + hi);
            KeplerPropagator.Propagate(rRef, vRef, mu, mid, out var rm, out _);
            if (rm.magnitude < orbit) lo = mid; else hi = mid;
        }
        KeplerPropagator.Propagate(rRef, vRef, mu, lo, out var rc, out _);
        double phi = moonSpeed / orbit * lo - Math.Atan2(rc.y, rc.x);
        var r0 = RotateZ(rRef, phi);
        var v0 = RotateZ(vRef, phi);

        double Dense(int moon)
        {
            for (double t = 1; t <= 4e4; t += 1)
            {
                KeplerPropagator.Propagate(r0, v0, mu, t, out var r, out _);
                s.StateRelativeToParent(moon, t, out var mr, out _);
                if ((r - mr).magnitude < soi)
                    return t;
            }
            return double.NaN;
        }
        double behind = Dense(1), ahead = Dense(2);
        TestContext.WriteLine($"dense entries: behind {behind} s, ahead {ahead} s");
        Assert.That(double.IsNaN(behind) || double.IsNaN(ahead), Is.False, "the arc enters both SOIs");
        Assert.That(Math.Abs(behind - ahead), Is.LessThan(3600), "both entries close together");

        var patches = PatchedPropagator.Propagate(s, 0, r0, v0, 0, 4e4, None);
        Assert.That(patches[0].End, Is.EqualTo(PatchEnd.Encounter));
        Assert.That(patches[0].NextBody, Is.EqualTo(behind < ahead ? 1 : 2));
        Assert.That(patches[0].EndUT, Is.EqualTo(Math.Min(behind, ahead)).Within(1.0));
    }

    // A miss: the patch reports the closest approach, checked against plain dense sampling.
    [Test]
    public void Miss_ReportsClosestApproach()
    {
        double mu = _s[_kerbin].Mu;
        double rp = 700000;
        double vp = Math.Sqrt(1000 * 1000 + 2 * mu / rp);
        AimAtMun(rp, vp, 16, out var r, out var v); // the closest approach is outside the SOI

        var patches = PatchedPropagator.Propagate(_s, _kerbin, r, v, 0, 1e6, None);
        var k = patches[0];
        Assert.That(k.End, Is.EqualTo(PatchEnd.Escape));

        double best = double.PositiveInfinity, bestT = double.NaN;
        for (double t = 0; t <= k.EndUT; t += 1)
        {
            KeplerPropagator.Propagate(r, v, mu, t, out var rt, out _);
            _s.StateRelativeToParent(_mun, t, out var mr, out _);
            double d = (rt - mr).magnitude;
            if (d < best)
            {
                best = d;
                bestT = t;
            }
        }
        TestContext.WriteLine($"closest: body {k.ClosestBody}, {k.ClosestDistance:F0} m at {k.ClosestUT:F1}; dense {best:F0} m at {bestT}");
        Assert.That(best, Is.GreaterThan(_s[_mun].Soi));
        Assert.That(k.ClosestBody, Is.EqualTo(_mun));
        Assert.That(k.ClosestDistance, Is.EqualTo(best).Within(10.0));
        Assert.That(k.ClosestDistance, Is.LessThanOrEqualTo(best + 1e-3));
        Assert.That(k.ClosestUT, Is.EqualTo(bestT).Within(1.0));
    }

    [Test]
    public void NoChildReachable_NoClosestApproach()
    {
        Circular(700000, 0, out var r, out var v);
        var p = PatchedPropagator.Propagate(_s, _kerbin, r, v, 0, 1e4, None)[0];
        Assert.That(p.ClosestBody, Is.EqualTo(-1));
        Assert.That(double.IsPositiveInfinity(p.ClosestDistance), Is.True);
    }

    // Timings: a low orbit over a long horizon reaches no moon, so it samples nothing; a fast
    // hyperbola samples only where it crosses the moons' orbit bands.
    [Test]
    public void Timing_LowOrbitAndFastHyperbola()
    {
        Circular(700000, 0, out var r, out var v);
        PatchedPropagator.Propagate(_s, _kerbin, r, v, 0, 1e3, None); // warm-up
        var watch = Stopwatch.StartNew();
        var low = PatchedPropagator.Propagate(_s, _kerbin, r, v, 0, 1e7, None);
        double lowMs = watch.Elapsed.TotalMilliseconds;
        Assert.That(low[0].End, Is.EqualTo(PatchEnd.HorizonReached));

        double mu = _s[_kerbin].Mu;
        double vp = Math.Sqrt(2500 * 2500 + 2 * mu / 700000);
        var vFast = new Vector3d(0, vp * Math.Cos(60 * Deg), vp * Math.Sin(60 * Deg)); // through the Mun's and Minmus's bands
        watch.Restart();
        var fast = PatchedPropagator.Propagate(_s, _kerbin, r, vFast, 0, 1e6, None);
        double fastMs = watch.Elapsed.TotalMilliseconds;
        TestContext.WriteLine($"low orbit, 1e7 s: {lowMs:F2} ms; 2.5 km/s hyperbola: {fastMs:F2} ms, first patch ends {fast[0].End} at {fast[0].EndUT:F0} s");
        Assert.That(lowMs, Is.LessThan(20));
        Assert.That(fastMs, Is.LessThan(50));
    }
}

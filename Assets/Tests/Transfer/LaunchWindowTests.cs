using System;
using K2D2.Transfer;
using KSP.Sim;
using NUnit.Framework;

public class LaunchWindowTests
{
    const double Deg = Math.PI / 180;
    const double R = 600000;          // Kerbin (KerbolFixture, external data)
    const double Mu = 3.5316e12;
    const double Orbit = 680000;      // 80 km
    const double Day = KerbolFixture.KerbinSiderealRotationSeconds;

    static Vector3d Omega(bool inverse = false) => KerbolFixture.KerbinAngularVelocity(inverse);
    static Vector3d Ksc(double lonDeg = 0) => KerbolFixture.SurfacePoint(R, KerbolFixture.KscLatitudeDeg, lonDeg);

    // Normal of a prograde plane of inclination incDeg whose ascending node is at longitude lanDeg.
    static Vector3d Normal(double incDeg, double lanDeg)
    {
        double i = incDeg * Deg, l = lanDeg * Deg;
        return new Vector3d(Math.Sin(i) * Math.Sin(l), -Math.Sin(i) * Math.Cos(l), Math.Cos(i));
    }

    static double AngleDeg(Vector3d a, Vector3d b) =>
        Math.Atan2(Vector3d.Cross(a, b).magnitude, Vector3d.Dot(a, b)) / Deg;

    static Vector3d Rotate(Vector3d p, Vector3d omega, double dt)
    {
        double a = omega.z * dt, c = Math.Cos(a), s = Math.Sin(a);
        return new Vector3d(c * p.x - s * p.y, s * p.x + c * p.y, p.z);
    }

    // 'site' is the site's position at UT 0.
    static LaunchWindowResult Find(Vector3d site, Vector3d normal, double ut0 = 0, bool inverse = false,
        double lead = 0, double downrange = 0, double horizon = 2 * Day)
    {
        // 'site' is where the site is at UT 0: turn it to ut0.
        var w = LaunchWindow.Find(R, Mu, Omega(inverse), Rotate(site, Omega(inverse), ut0), ut0, normal, Orbit,
            lead, downrange, horizon);
        TestContext.Out.WriteLine($"ok {w.Ok} ({w.Refusal}) launch {w.LaunchUT - ut0:F1} s, azimuth {w.AzimuthDeg:F3}°, " +
                                  $"{w.Pass}, plane error {w.PlaneErrorDeg:F4}° ({w.PlaneChangeDeltaV:F1} m/s)");
        return w;
    }

    [Test]
    public void EquatorialSite_EquatorialPlane_LaunchNow_Azimuth90()
    {
        var w = Find(new Vector3d(R, 0, 0), new Vector3d(0, 0, 1), ut0: 1000);
        Assert.IsTrue(w.Ok, w.Refusal);
        Assert.AreEqual(1000, w.LaunchUT, 1e-6);
        Assert.AreEqual(90, w.AzimuthDeg, 1e-6);
        Assert.AreEqual(PlanePass.Any, w.Pass);
        Assert.AreEqual(0, w.PlaneErrorDeg, 1e-6);

        // A nearly equatorial plane degenerates the same way: now, due east, never half a day later.
        var near = Find(new Vector3d(R, 0, 0), Normal(0.001, 40), ut0: 1000);
        Assert.IsTrue(near.Ok, near.Refusal);
        Assert.AreEqual(1000, near.LaunchUT, 1e-6);
        Assert.AreEqual(90, near.AzimuthDeg, 0.01);
        Assert.Less(near.PlaneErrorDeg, 0.01);
    }

    [Test]
    public void Kerbin_KscLatitude_PlaneOf6Deg_TwoPassesPerDay_EarliestChosen()
    {
        var n = Normal(6, 123);
        var site = Ksc(-74.5577);
        var first = Find(site, n);
        Assert.IsTrue(first.Ok, first.Refusal);
        Assert.Less(first.LaunchUT, Day);
        Assert.AreNotEqual(PlanePass.Any, first.Pass);
        Assert.Less(first.PlaneErrorDeg, 0.01);
        Assert.AreEqual(0, AngleDeg(first.ReachedPlaneNormal, n), 0.01);

        // The site really is in the plane at the launch time, and was on one side of it until then.
        var atLaunch = Rotate(site, Omega(), first.LaunchUT);
        Assert.AreEqual(0, Vector3d.Dot(atLaunch.normalized, n), 1e-6);
        double side = Math.Sign(Vector3d.Dot(site, n));
        for (double t = 0; t < first.LaunchUT - 1; t += 10)
            Assert.AreEqual(side, Math.Sign(Vector3d.Dot(Rotate(site, Omega(), t), n)), $"crossing missed at t = {t}");

        // Next: the other pass, then the first one again a sidereal day later.
        var second = Find(site, n, ut0: first.LaunchUT + 60);
        Assert.IsTrue(second.Ok, second.Refusal);
        Assert.AreNotEqual(first.Pass, second.Pass);
        Assert.Greater(second.LaunchUT, first.LaunchUT);
        var third = Find(site, n, ut0: second.LaunchUT + 60);
        Assert.AreEqual(first.Pass, third.Pass);
        Assert.AreEqual(first.LaunchUT + Day, third.LaunchUT, 1e-3);

        // Northeast on the ascending pass, southeast on the descending one.
        var asc = first.Pass == PlanePass.Ascending ? first : second;
        var desc = first.Pass == PlanePass.Ascending ? second : first;
        Assert.That(asc.AzimuthDeg, Is.InRange(0.0, 90.0));
        Assert.That(desc.AzimuthDeg, Is.InRange(90.0, 180.0));
        Assert.AreEqual(180, asc.AzimuthDeg + desc.AzimuthDeg, 0.1); // symmetric about east at the equator
    }

    [Test]
    public void LeadAndDownrange_PutTheInsertionPointInThePlane()
    {
        var n = Normal(28.5, 10);
        var site = Ksc(0);
        double lead = 240, downrange = 8 * Deg;
        var w = Find(site, n, lead: lead, downrange: downrange);
        Assert.IsTrue(w.Ok, w.Refusal);
        Assert.GreaterOrEqual(w.LaunchUT, 0);

        // Rotating frame: the ground track goes downrange along the surface azimuth, while the body
        // turns for the ascent lead.
        double a = w.AzimuthDeg * Deg;
        var up = site.normalized;
        var east = Vector3d.Cross(new Vector3d(0, 0, 1), up).normalized;
        var north = Vector3d.Cross(up, east);
        var track = Math.Sin(a) * east + Math.Cos(a) * north;
        var insertion = Rotate(Math.Cos(downrange) * up + Math.Sin(downrange) * track, Omega(), w.LaunchUT + lead);
        Assert.AreEqual(0, Vector3d.Dot(insertion, n), 1e-6);
        Assert.Less(w.PlaneErrorDeg, 0.05);
    }

    [Test]
    public void SiteAbovePlaneInclination_UsesClosestPlane_ReportsErrorAndDv()
    {
        // 30° north, plane of 20°: the best is to launch due east under the plane's northern vertex.
        var n = Normal(20, 0);
        var site = KerbolFixture.SurfacePoint(R, 30, 0);
        var w = Find(site, n);
        Assert.IsTrue(w.Ok, w.Refusal);
        Assert.AreEqual(PlanePass.Any, w.Pass);
        Assert.AreEqual(10, w.PlaneErrorDeg, 0.01);
        double vo = Math.Sqrt(Mu / Orbit);
        Assert.AreEqual(2 * vo * Math.Sin(5 * Deg), w.PlaneChangeDeltaV, 0.5);
        Assert.AreEqual(90, w.AzimuthDeg, 1e-6);
        Assert.AreEqual(30, Math.Acos(w.ReachedPlaneNormal.z) / Deg, 0.01);

        // The launch is under the vertex: the site's longitude, plus 90° from the ascending node.
        var at = Rotate(site, Omega(), w.LaunchUT);
        var vertex = Vector3d.Cross(n, Vector3d.Cross(new Vector3d(0, 0, 1), n)).normalized;
        Assert.AreEqual(0, AngleDeg(new Vector3d(at.x, at.y, 0), new Vector3d(vertex.x, vertex.y, 0)), 0.01);

        // Deterministic just above the inclination too, where the two passes merge.
        var edge1 = Find(KerbolFixture.SurfacePoint(R, 20.0001, 0), n);
        var edge2 = Find(KerbolFixture.SurfacePoint(R, 20.0001, 0), n);
        Assert.AreEqual(edge1.LaunchUT, edge2.LaunchUT);
        Assert.AreEqual(edge1.AzimuthDeg, edge2.AzimuthDeg);
        Assert.Less(edge1.PlaneErrorDeg, 0.01);
    }

    [Test]
    public void RetrogradePlane_AzimuthWestward()
    {
        var n = Normal(150, 40);
        var first = Find(Ksc(), n);
        var second = Find(Ksc(), n, ut0: first.LaunchUT + 60);
        foreach (var w in new[] { first, second })
        {
            Assert.IsTrue(w.Ok, w.Refusal);
            Assert.That(w.AzimuthDeg, Is.InRange(180.0, 360.0), "westward");
            Assert.Less(w.PlaneErrorDeg, 0.01);
            Assert.AreEqual(0, AngleDeg(w.ReachedPlaneNormal, n), 0.01);
        }
        Assert.AreNotEqual(first.Pass, second.Pass);
    }

    [Test]
    public void InverseRotation_SignHandled()
    {
        var n = Normal(28.5, 70);
        var site = Ksc();
        var normal = Find(site, n);
        var inverse = Find(site, n, inverse: true);
        Assert.IsTrue(inverse.Ok, inverse.Refusal);

        // The site really is in the plane at the launch time, turning the other way.
        Assert.AreEqual(0, Vector3d.Dot(Rotate(site, Omega(true), inverse.LaunchUT).normalized, n), 1e-6);

        // The ground now moves west: the surface heading leans further east than the inertial one.
        double inertial = Math.Asin(Math.Cos(28.5 * Deg) / Math.Cos(KerbolFixture.KscLatitudeDeg * Deg)) / Deg;
        var asc = inverse.Pass == PlanePass.Ascending ? inverse : Find(site, n, ut0: inverse.LaunchUT + 60, inverse: true);
        var ascNormal = normal.Pass == PlanePass.Ascending ? normal : Find(site, n, ut0: normal.LaunchUT + 60);
        Assert.Greater(asc.AzimuthDeg, inertial);
        Assert.Less(ascNormal.AzimuthDeg, inertial);
    }

    [Test]
    public void AzimuthCorrection_MatchesHandComputedValue()
    {
        // KSC, 28.5°, 80 km. By hand: v_o = sqrt(3.5316e12 / 680000) = 2278.9 m/s; ground speed
        // ω R cos φ = 2π / 21549.425 × 600000 = 174.9 m/s; sin β_i = cos 28.5° / cos 0.0972°, so
        // β_i = 61.50°; β = atan2(2278.9 sin β_i − 174.9, 2278.9 cos β_i) = atan2(1827.8, 1087.4).
        double vo = Math.Sqrt(Mu / Orbit);
        double ground = 2 * Math.PI / Day * R * Math.Cos(KerbolFixture.KscLatitudeDeg * Deg);
        double bi = Math.Asin(Math.Cos(28.5 * Deg) / Math.Cos(KerbolFixture.KscLatitudeDeg * Deg));
        double expected = Math.Atan2(vo * Math.Sin(bi) - ground, vo * Math.Cos(bi)) / Deg;
        Assert.AreEqual(2278.9, vo, 0.1);
        Assert.AreEqual(174.9, ground, 0.1);
        Assert.AreEqual(59.25, expected, 0.02);

        var n = Normal(28.5, 200);
        var w = Find(Ksc(), n);
        var asc = w.Pass == PlanePass.Ascending ? w : Find(Ksc(), n, ut0: w.LaunchUT + 60);
        Assert.AreEqual(PlanePass.Ascending, asc.Pass);
        Assert.AreEqual(expected, asc.AzimuthDeg, 1e-6);
    }

    [Test]
    public void NeverNaN_OnDegenerateInputs()
    {
        var site = Ksc(90); // far from the plane's nodes: the next pass is hours away
        var n = Normal(10, 0);
        var cases = new[]
        {
            LaunchWindow.Find(R, Mu, Vector3d.zero, site, 0, n, Orbit, 0, 0, Day),                       // no rotation
            LaunchWindow.Find(R, Mu, Omega(), site, 0, Vector3d.zero, Orbit, 0, 0, Day),                 // no plane
            LaunchWindow.Find(R, Mu, Omega(), Vector3d.zero, 0, n, Orbit, 0, 0, Day),                    // no site
            LaunchWindow.Find(R, Mu, Omega(), new Vector3d(0, 0, R), 0, n, Orbit, 0, 0, Day),            // site at a pole
            LaunchWindow.Find(R, Mu, Omega(), site, double.NaN, n, Orbit, 0, 0, Day),
            LaunchWindow.Find(R, 0, Omega(), site, 0, n, Orbit, 0, 0, Day),
            LaunchWindow.Find(R, Mu, Omega(), site, 0, n, double.NaN, 0, 0, Day),
            LaunchWindow.Find(R, Mu, Omega(), site, 0, n, Orbit, double.NaN, double.NaN, double.NaN),
            LaunchWindow.Find(R, Mu, Omega(), site, 0, n, Orbit, 0, 0, 1),                                // horizon too short
            LaunchWindow.Find(R, Mu, Omega(), site, 0, new Vector3d(0, 0, -1), Orbit, 0, 0, Day),        // retrograde equatorial
            LaunchWindow.Find(R, Mu, new Vector3d(0, 0, 1), site, 0, n, Orbit, 0, 0, Day),               // ground faster than orbit
        };
        foreach (var w in cases)
        {
            TestContext.Out.WriteLine($"ok {w.Ok} ({w.Refusal}) launch {w.LaunchUT}, azimuth {w.AzimuthDeg}, {w.Pass}, error {w.PlaneErrorDeg}, dv {w.PlaneChangeDeltaV}");
            foreach (double x in new[] { w.LaunchUT, w.AzimuthDeg, w.PlaneErrorDeg, w.PlaneChangeDeltaV,
                         w.ReachedPlaneNormal.x, w.ReachedPlaneNormal.y, w.ReachedPlaneNormal.z })
                Assert.IsFalse(double.IsNaN(x) || double.IsInfinity(x));
            Assert.IsTrue(w.Ok || !string.IsNullOrEmpty(w.Refusal));
        }
        Assert.IsTrue(cases[0].Ok, "a body that does not rotate launches now");
        Assert.IsFalse(cases[1].Ok);
        Assert.IsFalse(cases[2].Ok);
        Assert.IsFalse(cases[3].Ok);
        Assert.IsFalse(cases[8].Ok, "no pass within one second");
        Assert.IsTrue(cases[9].Ok);
        Assert.AreEqual(270, cases[9].AzimuthDeg, 1e-6);
    }
}

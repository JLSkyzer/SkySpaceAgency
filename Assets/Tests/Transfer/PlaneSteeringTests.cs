using System;
using K2D2.Lift;
using K2D2.Transfer;
using KSP.Sim;
using NUnit.Framework;

public class PlaneSteeringTests
{
    const double Deg = Math.PI / 180;
    const double R = 600000;      // Kerbin (KerbolFixture, external data)
    const double Mu = 3.5316e12;
    const double Orbit = 680000;
    static readonly double Vo = Math.Sqrt(Mu / Orbit);

    static Vector3d Normal(double incDeg, double lanDeg)
    {
        double i = incDeg * Deg, l = lanDeg * Deg;
        return new Vector3d(Math.Sin(i) * Math.Sin(l), -Math.Sin(i) * Math.Cos(l), Math.Cos(i));
    }

    static void Frame(Vector3d r, out Vector3d up, out Vector3d east, out Vector3d north)
    {
        up = r.normalized;
        east = new Vector3d(-up.y, up.x, 0).normalized;
        north = Vector3d.Cross(up, east);
    }

    static double Compass(Vector3d r, Vector3d d)
    {
        Frame(r, out _, out var east, out var north);
        double h = Math.Atan2(Vector3d.Dot(d, east), Vector3d.Dot(d, north)) / Deg;
        return h < 0 ? h + 360 : h;
    }

    [Test]
    public void InPlane_ZeroCorrection()
    {
        var n = Normal(28.5, 30);
        var node = new Vector3d(Math.Cos(30 * Deg), Math.Sin(30 * Deg), 0);
        // A point 20° past the ascending node, moving along the plane at half the orbital speed.
        var along = Vector3d.Cross(n, node);
        var r = R * (Math.Cos(20 * Deg) * node + Math.Sin(20 * Deg) * along);
        var t = Vector3d.Cross(n, r.normalized).normalized;
        var v = 0.5 * Vo * t;
        double azimuth = Compass(r, t);
        Assert.AreEqual(0, PlaneSteering.HeadingCorrectionDeg(r, v, n, azimuth, Vo), 1e-6);
        Assert.AreEqual(0, PlaneSteering.PlaneErrorDeg(r, v, n), 1e-6);
        // At orbital speed there is nothing left to gain: no correction either.
        Assert.AreEqual(0, PlaneSteering.HeadingCorrectionDeg(r, Vo * t, n, azimuth, Vo), 1e-6);
    }

    [Test]
    public void NorthOfPlane_TurnsSouth()
    {
        // Equatorial plane, vessel 1° north of it, going east: turn right (heading above 90°).
        var n = new Vector3d(0, 0, 1);
        var r = R * new Vector3d(Math.Cos(1 * Deg), 0, Math.Sin(1 * Deg));
        Frame(r, out _, out var east, out _);
        var v = 800 * east;
        double c = PlaneSteering.HeadingCorrectionDeg(r, v, n, 90, Vo);
        Assert.Greater(c, 0.1);
        Assert.AreEqual(1, PlaneSteering.PlaneErrorDeg(r, v, n), 1e-6);

        // South of it: turn left. Velocity already heading north of the plane: turn right too.
        var rSouth = R * new Vector3d(Math.Cos(1 * Deg), 0, -Math.Sin(1 * Deg));
        Frame(rSouth, out _, out var eastS, out _);
        Assert.Less(PlaneSteering.HeadingCorrectionDeg(rSouth, 800 * eastS, n, 90, Vo), -0.1);
        var rOn = new Vector3d(R, 0, 0);
        Frame(rOn, out _, out var eastOn, out var northOn);
        Assert.Greater(PlaneSteering.HeadingCorrectionDeg(rOn, 800 * eastOn + 50 * northOn, n, 90, Vo), 0.1);
    }

    [Test]
    public void Clamped()
    {
        var n = new Vector3d(0, 0, 1);
        var r = R * new Vector3d(Math.Cos(5 * Deg), 0, Math.Sin(5 * Deg));
        Frame(r, out _, out var east, out var north);
        var v = 1500 * east + 600 * north;
        Assert.AreEqual(10, PlaneSteering.HeadingCorrectionDeg(r, v, n, 90, Vo), 1e-9);
        Assert.AreEqual(3, PlaneSteering.HeadingCorrectionDeg(r, v, n, 90, Vo, 3), 1e-9);
        var rS = R * new Vector3d(Math.Cos(5 * Deg), 0, -Math.Sin(5 * Deg));
        Frame(rS, out _, out var eastS, out var northS);
        Assert.AreEqual(-10, PlaneSteering.HeadingCorrectionDeg(rS, 1500 * eastS - 600 * northS, n, 90, Vo), 1e-9);

        // Degenerate inputs give no correction, never NaN.
        Assert.AreEqual(0, PlaneSteering.HeadingCorrectionDeg(r, v, Vector3d.zero, 90, Vo));
        Assert.AreEqual(0, PlaneSteering.HeadingCorrectionDeg(Vector3d.zero, v, n, 90, Vo));
        Assert.AreEqual(0, PlaneSteering.HeadingCorrectionDeg(r, v, n, double.NaN, Vo));
        Assert.AreEqual(0, PlaneSteering.HeadingCorrectionDeg(r, v, n, 90, double.NaN));
        Assert.AreEqual(0, PlaneSteering.PlaneErrorDeg(r, Vector3d.zero, n));
    }

    // A crude ascent on the sphere (constant radius): the ground's speed at liftoff, then a constant
    // horizontal thrust on heading azimuth + correction until the horizontal speed reaches v_o. The
    // liftoff time comes from LaunchWindow with the ascent's length as the lead.
    static double Ascent(double incDeg, double lanDeg, bool steer, bool inverse, out double maxCorrection)
    {
        const double accel = 15, dt = 0.5, lead = 140;
        var omega = KerbolFixture.KerbinAngularVelocity(inverse);
        var n = Normal(incDeg, lanDeg);
        var site = KerbolFixture.SurfacePoint(R, KerbolFixture.KscLatitudeDeg, 0);
        var w = LaunchWindow.Find(R, Mu, omega, site, 0, n, Orbit, lead, 0, 2 * KerbolFixture.KerbinSiderealRotationSeconds);
        Assert.IsTrue(w.Ok, w.Refusal);

        double a = omega.z * w.LaunchUT, c = Math.Cos(a), s = Math.Sin(a);
        var r = new Vector3d(c * site.x - s * site.y, s * site.x + c * site.y, site.z);
        var v = Vector3d.Cross(omega, r);
        maxCorrection = 0;
        for (int i = 0; i < 100000; i++)
        {
            Frame(r, out var up, out var east, out var north);
            var vh = v - Vector3d.Dot(v, up) * up;
            if (vh.magnitude >= Vo)
                break;
            double corr = steer ? PlaneSteering.HeadingCorrectionDeg(r, v, n, w.AzimuthDeg, Vo) : 0;
            maxCorrection = Math.Max(maxCorrection, Math.Abs(corr));
            double h = (w.AzimuthDeg + corr) * Deg;
            v += accel * dt * (Math.Sin(h) * east + Math.Cos(h) * north);
            r += dt * v;
            r = R * r.normalized;
            up = r.normalized;
            double speed = v.magnitude;
            v = v - Vector3d.Dot(v, up) * up;
            v = speed * v.normalized;
        }
        return PlaneSteering.PlaneErrorDeg(r, v, n);
    }

    [Test]
    public void SimulatedAscent_ConvergesUnder0_5Deg()
    {
        var cases = new[] { (28.5, 0.0, false), (28.5, 140.0, false), (6.0, 250.0, false), (150.0, 40.0, false),
                            (60.0, 10.0, false), (28.5, 75.0, true) };
        foreach (var (inc, lan, inverse) in cases)
        {
            double steered = Ascent(inc, lan, true, inverse, out double maxCorrection);
            double free = Ascent(inc, lan, false, inverse, out _);
            TestContext.Out.WriteLine($"i {inc}°, node {lan}°, inverse {inverse}: plane error {steered:F3}° steered, " +
                                      $"{free:F3}° without, max correction {maxCorrection:F2}°");
            Assert.Less(steered, 0.5, $"i {inc}, node {lan}");
            Assert.LessOrEqual(steered, free + 0.02, $"i {inc}, node {lan}: steering must not make it worse");
            Assert.LessOrEqual(maxCorrection, 10);
        }
    }
}

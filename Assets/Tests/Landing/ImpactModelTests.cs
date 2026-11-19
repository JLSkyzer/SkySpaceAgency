using System;
using K2D2.Landing;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class ImpactModelTests
{
    const double Mu = 3.5316e12;       // Kerbin
    const double R = 600000;

    // A spherical body turning about `pole` (any direction: the frame may be turned), with its
    // prime meridian along `meridian`. Latitude and longitude are measured from those two axes,
    // so a turned copy of the same body gives the same answers for the turned points.
    class FakeBody : IImpactBody
    {
        readonly Vector3d pole, meridian, east;
        readonly double spin_rate;
        public double Terrain = R;

        public FakeBody(Vector3d pole, Vector3d meridian, double spin_rate)
        {
            this.pole = pole.normalized;
            this.meridian = meridian.normalized;
            east = Vector3d.Cross(this.pole, this.meridian);
            this.spin_rate = spin_rate;
        }

        public double Mu => ImpactModelTests.Mu;
        public double Radius => R;
        public double AtmosphereDepth => 70000;
        public Vector3d Spin => pole * spin_rate;

        public void LatLon(Vector3d body_fixed, out double latitude, out double longitude)
        {
            Vector3d d = body_fixed.normalized;
            latitude = Math.Asin(Vector3d.Dot(d, pole)) * 180 / Math.PI;
            longitude = Math.Atan2(Vector3d.Dot(d, east), Vector3d.Dot(d, meridian)) * 180 / Math.PI;
        }

        public Vector3d SurfaceUp(double latitude, double longitude)
        {
            double la = latitude * Math.PI / 180, lo = longitude * Math.PI / 180;
            return (meridian * Math.Cos(lo) + east * Math.Sin(lo)) * Math.Cos(la) + pole * Math.Sin(la);
        }

        public double TerrainRadius(double latitude, double longitude) => Terrain;
    }

    // Rodrigues' rotation of v about a unit axis by angle (rad).
    static Vector3d Rotate(Vector3d v, Vector3d axis, double angle)
    {
        axis = axis.normalized;
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return v * c + Vector3d.Cross(axis, v) * s + axis * (Vector3d.Dot(axis, v) * (1 - c));
    }

    // An arbitrary turn of the whole frame.
    static readonly Vector3d TurnAxis = new Vector3d(0.3, -0.8, 0.52);
    const double TurnAngle = 2.1;
    static Vector3d Turn(Vector3d v) => Rotate(v, TurnAxis, TurnAngle);

    static FakeBody Kerbin(double spin = 2 * Math.PI / 21549.425) =>
        new FakeBody(new Vector3d(0, 0, 1), new Vector3d(1, 0, 0), spin);

    // A suborbital coast: 80 km up, 1800 m/s, 10° above the horizon.
    static void Suborbital(out Vector3d r, out Vector3d v)
    {
        r = new Vector3d(R + 80000, 0, 0);
        double a = 10 * Math.PI / 180;
        v = new Vector3d(1800 * Math.Sin(a), 1800 * Math.Cos(a) * 0.8, 1800 * Math.Cos(a) * 0.6);
    }

    [Test]
    public void AirVelocity_RemovesTheSurfaceSpeed()
    {
        double w = 1e-3;
        var model = new ImpactModel(Kerbin(w), 0);
        var r = new Vector3d(R, 0, 0);
        var v = new Vector3d(0, w * R + 100, 0);
        Vector3d air = model.AirVelocity(r, v);
        Assert.AreEqual(0, air.x, 1e-9);
        Assert.AreEqual(100, air.y, 1e-9);
        Assert.AreEqual(0, air.z, 1e-9);
    }

    [Test]
    public void BodyFixed_UndoesTheTurnSinceTheEpoch_OnATiltedAxis()
    {
        var pole = new Vector3d(0.2, 0.5, 0.84);
        double w = 2 * Math.PI / 20000;
        var model = new ImpactModel(new FakeBody(pole, Vector3d.Cross(pole, new Vector3d(1, 0, 0)), w), 1000);
        var ground = new Vector3d(R, 30000, -20000);
        double t = 3333;
        // The same surface point, t seconds later, seen from the inertial axes.
        Vector3d inertial = Rotate(ground, pole, w * t);
        Vector3d back = model.BodyFixed(inertial, 1000 + t);
        Assert.Less((back - ground).magnitude, 1e-6);
        // A point on the axis does not move.
        Assert.Less((model.BodyFixed(pole.normalized * R, 5000) - pole.normalized * R).magnitude, 1e-6);
    }

    [Test]
    public void Period_IsKeplersThirdLaw()
    {
        var model = new ImpactModel(Kerbin(), 0);
        var r = new Vector3d(R + 100000, 0, 0);
        var v = new Vector3d(0, Math.Sqrt(Mu / r.magnitude), 0);
        double a = r.magnitude;
        Assert.AreEqual(2 * Math.PI * Math.Sqrt(a * a * a / Mu), model.Period(r, v), 1e-6);
    }

    [Test]
    public void Impact_OnASphere_MatchesTheConicAnalytically()
    {
        var model = new ImpactModel(Kerbin(), 0);
        Suborbital(out Vector3d r, out Vector3d v);

        Assert.IsTrue(model.Impact(r, v, 50, R, out double impact_UT, out Vector3d hit));
        Assert.AreEqual(R, hit.magnitude, 1.0, "on the sphere");

        // True anomaly from the conic equation, and the angle from the eccentricity vector.
        Vector3d h = Vector3d.Cross(r, v);
        Vector3d e = Vector3d.Cross(v, h) / Mu - r.normalized;
        double p = h.sqrMagnitude / Mu;
        double nu = Math.Acos((p / R - 1) / e.magnitude);
        double angle = Math.Atan2(Vector3d.Dot(Vector3d.Cross(e, hit), h.normalized), Vector3d.Dot(e, hit));
        // Coming down, before periapsis: a descending crossing has its true anomaly in (-pi, 0).
        Assert.AreEqual(-nu, angle, 1e-5, "descending crossing");

        // The first crossing: a second earlier the vessel was still above the ground, and the
        // state agrees with KeplerPropagator.
        KeplerPropagator.Propagate(r, v, Mu, impact_UT - 50 - 1, out Vector3d before, out _);
        Assert.Greater(before.magnitude, R);
        KeplerPropagator.Propagate(r, v, Mu, impact_UT - 50, out Vector3d again, out _);
        Assert.Less((again - hit).magnitude, 1.0);
    }

    [Test]
    public void Impact_NoneWhenThePeriapsisIsAboveTheGround()
    {
        var model = new ImpactModel(Kerbin(), 0);
        var r = new Vector3d(R + 100000, 0, 0);
        var v = new Vector3d(0, Math.Sqrt(Mu / r.magnitude), 0);
        Assert.IsFalse(model.Impact(r, v, 0, R, out _, out _));
    }

    [Test]
    public void EntryState_IsOnTheAtmosphereEdge_OrTheStateItselfInside()
    {
        var model = new ImpactModel(Kerbin(), 0);
        Suborbital(out Vector3d r, out Vector3d v);
        Assert.IsTrue(model.EntryState(r, v, 0, out Vector3d er, out Vector3d ev));
        Assert.AreEqual(R + 70000, er.magnitude, 1.0);
        Assert.Less(Vector3d.Dot(er, ev), 0, "coming down");

        var inside = new Vector3d(R + 20000, 0, 0);
        Assert.IsTrue(model.EntryState(inside, v, 0, out Vector3d ir, out _));
        Assert.AreEqual(0, (ir - inside).magnitude, 1e-9);
    }

    // Lesson 2026-11-18: an identity frame proves nothing. The same flight, with the body, its
    // axis and the vessel all turned, lands on the same latitude and longitude at the same time.
    [Test]
    public void TurnedBodyFrame_GivesTheSameImpact()
    {
        double w = 2 * Math.PI / 21549.425;
        var tilted_pole = new Vector3d(0.1, 0.3, 0.95);
        var meridian = Vector3d.Cross(tilted_pole, new Vector3d(0, 1, 0));
        var plain = new FakeBody(tilted_pole, meridian, w);
        var turned = new FakeBody(Turn(tilted_pole), Turn(meridian), w);

        Suborbital(out Vector3d r, out Vector3d v);
        Fly(new ImpactModel(plain, 100), r, v, out double ut1, out double lat1, out double lon1, out double clear1);
        Fly(new ImpactModel(turned, 100), Turn(r), Turn(v), out double ut2, out double lat2, out double lon2, out double clear2);

        Assert.AreEqual(ut1, ut2, 1e-6);
        Assert.AreEqual(lat1, lat2, 1e-7);
        Assert.AreEqual(lon1, lon2, 1e-7);
        Assert.AreEqual(clear1, clear2, 1e-3);

        // And the spin matters: without it the same coast lands elsewhere.
        Fly(new ImpactModel(new FakeBody(tilted_pole, meridian, 0), 100), r, v, out _, out _, out double lon0, out _);
        Assert.Greater(Math.Abs(lon0 - lon1), 0.1);
    }

    static void Fly(ImpactModel model, Vector3d r, Vector3d v, out double impact_UT, out double lat, out double lon, out double clearance)
    {
        Assert.IsTrue(model.Impact(r, v, 100, R, out impact_UT, out Vector3d hit));
        Vector3d ground = model.BodyFixed(hit, impact_UT);
        model.LatLon(ground, out lat, out lon);
        clearance = model.MinTerrainClearance(r, v, 100, impact_UT, ground, 1000, out _);
    }

    [Test]
    public void SurfaceDistance_IsTheArcOnTheBody()
    {
        var model = new ImpactModel(Kerbin(), 0);
        double d = model.SurfaceDistance(new Vector3d(R, 0, 0), new Vector3d(0, 2 * R, 0));
        Assert.AreEqual(Math.PI / 2 * R, d, 1e-6);
    }
}

using System;
using System.Threading;
using K2D2.Transfer;
using KSP.Sim;
using NUnit.Framework;

public class ParkingPlaneTests
{
    const double Deg = Math.PI / 180;
    const double Orbit = 680000; // Kerbin LowOrbitRadius in KerbolFixture: 600 + 70 + 10 km

    BodySystem _s;

    [SetUp]
    public void SetUp() => _s = KerbolFixture.Build();

    int B(string name) => _s[name].Index;

    ParkingPlan Plan(string to, double siteLatDeg = KerbolFixture.KscLatitudeDeg, double ut0 = 0)
    {
        var p = ParkingPlane.Plan(_s, B("Kerbin"), B(to), siteLatDeg * Deg, ut0, Orbit, new TransferRequest(), CancellationToken.None);
        TestContext.Out.WriteLine($"{to}: ok {p.Ok} ({p.Refusal}) inclination {InclinationDeg(p):F3}°, window {p.WindowUT:F0}, " +
                                  $"Δv {p.TransferDeltaV:F1} m/s. {p.Explanation}");
        return p;
    }

    static double InclinationDeg(ParkingPlan p) => Math.Acos(Math.Max(-1, Math.Min(1, p.PlaneNormal.normalized.z))) / Deg;
    static double DeclinationDeg(Vector3d d) => Math.Asin(d.normalized.z) / Deg;
    static double OutOfPlaneDeg(Vector3d d, Vector3d n) => Math.Asin(Math.Abs(Vector3d.Dot(d.normalized, n.normalized))) / Deg;
    static double AngleDeg(Vector3d a, Vector3d b) => Math.Atan2(Vector3d.Cross(a, b).magnitude, Vector3d.Dot(a, b)) / Deg;

    TransferPlan Equatorial(string to)
    {
        double speed = Math.Sqrt(_s["Kerbin"].Mu / Orbit);
        return TransferSearch.Find(_s, new TransferRequest
        {
            FromBody = B("Kerbin"), ToBody = B(to),
            R0 = new Vector3d(Orbit, 0, 0), V0 = new Vector3d(0, speed, 0), UT0 = 0,
        }, CancellationToken.None);
    }

    [Test]
    public void KerbinToDuna_PlaneContainsDepartureDirection_Within0_1Deg()
    {
        var p = Plan("Duna");
        Assert.IsTrue(p.Ok, p.Refusal);
        Assert.AreEqual(Orbit, p.OrbitRadius);
        Assert.IsTrue(p.Transfer.Ok, p.Transfer.Refusal);
        Assert.AreEqual(B("Kerbin"), p.Transfer.ExcessBody);
        Assert.Less(OutOfPlaneDeg(p.Transfer.RequiredExcess, p.PlaneNormal), 0.1);
        Assert.AreEqual(p.Transfer.DepartureUT, p.WindowUT);
        Assert.AreEqual(p.Transfer.TotalDeltaV, p.TransferDeltaV);
        Assert.Greater(p.PlaneNormal.z, 0, "prograde");
        StringAssert.Contains("inclination", p.Explanation);
    }

    [Test]
    public void KerbinToMun_PlaneIsMunOrbitPlane()
    {
        foreach (var moon in new[] { "Mun", "Minmus" })
        {
            var p = Plan(moon);
            Assert.IsTrue(p.Ok, p.Refusal);
            _s.StateRelativeToParent(B(moon), 0, out var r, out var v);
            Assert.AreEqual(0, AngleDeg(p.PlaneNormal, Vector3d.Cross(r, v)), 0.01, moon);
            Assert.IsTrue(p.Transfer.Ok, p.Transfer.Refusal);
        }
    }

    [Test]
    public void HighDeclination_InclinationEqualsDeclination()
    {
        // From the equator, the lowest plane that holds the departure asymptote is inclined by its
        // declination.
        var p = Plan("Moho", siteLatDeg: 0);
        Assert.IsTrue(p.Ok, p.Refusal);
        double dec = Math.Abs(DeclinationDeg(p.Transfer.RequiredExcess));
        Assert.Greater(dec, 0.1, "Moho's 7° orbit gives a real declination");
        Assert.AreEqual(dec, InclinationDeg(p), 0.1);
        Assert.Less(OutOfPlaneDeg(p.Transfer.RequiredExcess, p.PlaneNormal), 0.1);
    }

    [Test]
    public void LowDeclination_InclinationEqualsSiteLatitude()
    {
        var p = Plan("Duna", siteLatDeg: 30);
        Assert.IsTrue(p.Ok, p.Refusal);
        Assert.Less(Math.Abs(DeclinationDeg(p.Transfer.RequiredExcess)), 30);
        Assert.AreEqual(30, InclinationDeg(p), 0.01);
        Assert.Less(OutOfPlaneDeg(p.Transfer.RequiredExcess, p.PlaneNormal), 0.1);
    }

    [Test]
    public void TransferFromChosenPlane_CheaperThanFromEquatorial_OrEqual()
    {
        foreach (var to in new[] { "Moho", "Duna" })
        {
            var p = Plan(to, siteLatDeg: 0);
            var eq = Equatorial(to);
            Assert.IsTrue(p.Ok && eq.Ok, p.Refusal ?? eq.Refusal);
            TestContext.Out.WriteLine($"{to}: chosen plane {p.TransferDeltaV:F1} m/s, equatorial {eq.TotalDeltaV:F1} m/s");
            Assert.LessOrEqual(p.TransferDeltaV, eq.TotalDeltaV + 1, to);
        }
    }

    [Test]
    public void TargetIsLaunchBody_Refused()
    {
        var p = Plan("Kerbin");
        Assert.IsFalse(p.Ok);
        Assert.AreEqual(TransferSearch.SameBody, p.Refusal);
    }
}

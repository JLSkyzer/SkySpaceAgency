using System;
using K2D2.OrbitPlanning;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class DeparturePlannerTests
{
    // EXTERNAL DATA (KSP wiki, stock values): Kerbin.
    const double Mu = 3.5316e12;
    const double Soi = 84159286;
    const double Lko = 700000; // 100 km altitude

    const double Deg = Math.PI / 180;
    const double Ut0 = 1000;

    static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
    static bool IsFinite(Vector3d v) => IsFinite(v.x) && IsFinite(v.y) && IsFinite(v.z);

    static double AngleDeg(Vector3d a, Vector3d b) =>
        Math.Atan2(Vector3d.Cross(a, b).magnitude, Vector3d.Dot(a, b)) / Deg;

    // Circular orbit of radius r, inclination i, starting on the +x axis (the ascending node).
    static void Circular(double r, double incDeg, out Vector3d r0, out Vector3d v0)
    {
        r0 = new Vector3d(r, 0, 0);
        double vc = Math.Sqrt(Mu / r);
        v0 = vc * new Vector3d(0, Math.Cos(incDeg * Deg), Math.Sin(incDeg * Deg));
    }

    // The cheapest single burn over one orbit by a fine brute-force scan: shares only
    // VelocityForAsymptote with the planner, none of its search.
    static double BruteForceSingleBurn(Vector3d r0, Vector3d v0, Vector3d vInf, int samples)
    {
        double period = OrbitMath.Elements(r0, v0, Mu).Period;
        double best = double.PositiveInfinity;
        for (int i = 0; i < samples; i++)
        {
            KeplerPropagator.Propagate(r0, v0, Mu, period * i / samples, out var r, out var v);
            if (!Hyperbola.VelocityForAsymptote(r, vInf, Mu, out var a, out var b))
                continue;
            best = Math.Min(best, Math.Min((a - v).magnitude, (b - v).magnitude));
        }
        return best;
    }

    // Replays the plan with the propagator and checks that it ends on the requested asymptote.
    static void AssertPlanReachesAsymptote(Vector3d r0, Vector3d v0, Vector3d vInf, DeparturePlan plan)
    {
        Vector3d r = r0, v = v0;
        double ut = Ut0;
        if (plan.PlaneChangeFirst)
        {
            Assert.That(plan.PlaneChangeUT, Is.GreaterThanOrEqualTo(Ut0));
            Assert.That(plan.BurnUT, Is.GreaterThanOrEqualTo(plan.PlaneChangeUT));
            KeplerPropagator.Propagate(r, v, Mu, plan.PlaneChangeUT - ut, out r, out v);
            v = v + plan.PlaneChangeDeltaV;
            ut = plan.PlaneChangeUT;
            var after = OrbitMath.Elements(r, v, Mu);
            Assert.That(after.IsClosed, Is.True, "still a closed orbit after the plane change");
            Assert.That(Math.Abs(90 - AngleDeg(after.AngularMomentum, vInf)), Is.LessThan(0.1),
                "the new orbit plane contains the departure direction");
        }
        Assert.That(plan.BurnUT, Is.GreaterThanOrEqualTo(ut));
        KeplerPropagator.Propagate(r, v, Mu, plan.BurnUT - ut, out r, out v);
        Vector3d asymptote = Hyperbola.Asymptote(r, v + plan.DeltaV, Mu);
        Assert.That(AngleDeg(asymptote, vInf), Is.LessThan(0.2), "asymptote direction after the burn");
        Assert.That(asymptote.magnitude, Is.EqualTo(vInf.magnitude).Within(0.5).Percent, "asymptote magnitude");
        Assert.That(plan.DeltaVMagnitude, Is.EqualTo(plan.DeltaV.magnitude).Within(1e-6));
    }

    static void AssertSane(DeparturePlan plan)
    {
        if (!plan.Ok)
        {
            Assert.That(plan.Refusal, Is.Not.Null.And.Not.Empty);
            return;
        }
        Assert.That(IsFinite(plan.BurnUT), Is.True, "BurnUT");
        Assert.That(IsFinite(plan.DeltaV), Is.True, "DeltaV");
        Assert.That(IsFinite(plan.DeltaVMagnitude), Is.True, "DeltaVMagnitude");
        Assert.That(IsFinite(plan.TotalDeltaV), Is.True, "TotalDeltaV");
        Assert.That(IsFinite(plan.OutOfPlaneDeg), Is.True, "OutOfPlaneDeg");
        Assert.That(IsFinite(plan.PlaneChangeDeltaV), Is.True, "PlaneChangeDeltaV");
        if (plan.PlaneChangeFirst)
            Assert.That(IsFinite(plan.PlaneChangeUT), Is.True, "PlaneChangeUT");
    }

    [TestCase(500.0)]
    [TestCase(1000.0)]
    [TestCase(3000.0)]
    public void Departure_CoplanarCircular_EqualsOberthFormula(double vInfMag)
    {
        Circular(Lko, 0, out var r0, out var v0);
        var vInf = vInfMag * new Vector3d(-0.6, 0.8, 0);

        var plan = DeparturePlanner.Plan(r0, v0, Ut0, Mu, Soi, vInf);

        Assert.That(plan.Ok, Is.True, plan.Refusal);
        double oberth = Math.Sqrt(vInfMag * vInfMag + 2 * Mu / Lko) - Math.Sqrt(Mu / Lko);
        Assert.That(plan.DeltaVMagnitude, Is.EqualTo(oberth).Within(0.5).Percent);
        Assert.That(plan.PlaneChangeFirst, Is.False);
        Assert.That(plan.Explanation, Is.Empty);
        Assert.That(plan.OutOfPlaneDeg, Is.EqualTo(0).Within(0.01));
        double period = OrbitMath.Elements(r0, v0, Mu).Period;
        Assert.That(plan.BurnUT, Is.InRange(Ut0, Ut0 + period));
        AssertPlanReachesAsymptote(r0, v0, vInf, plan);
    }

    [Test]
    public void Departure_InclinedVinf_PrefersPlaneChangeWhenCheaper()
    {
        // Very eccentric orbit, periapsis 700 km on +x, apoapsis 60 000 km on -x. The departure
        // direction is 60° out of the orbit plane, about the apsis line, so the plane change falls
        // at apoapsis where it is cheap (~40 m/s), then the departure is made near periapsis.
        double rp = Lko, ra = 60e6;
        double a = 0.5 * (rp + ra);
        var r0 = new Vector3d(rp, 0, 0);
        var v0 = new Vector3d(0, Math.Sqrt(Mu * (2 / rp - 1 / a)), 0);
        var vInf = 1000 * new Vector3d(0, Math.Cos(60 * Deg), Math.Sin(60 * Deg));

        var plan = DeparturePlanner.Plan(r0, v0, Ut0, Mu, Soi, vInf);

        AssertSane(plan);
        Assert.That(plan.Ok, Is.True, plan.Refusal);
        Assert.That(plan.PlaneChangeFirst, Is.True);
        Assert.That(plan.OutOfPlaneDeg, Is.EqualTo(60).Within(0.01));
        double single = BruteForceSingleBurn(r0, v0, vInf, 7200);
        double saving = single - plan.TotalDeltaV;
        Assert.That(saving, Is.GreaterThan(1), $"single burn {single:F1} m/s, plan {plan.TotalDeltaV:F1} m/s");
        Assert.That(plan.TotalDeltaV, Is.EqualTo(plan.DeltaVMagnitude + plan.PlaneChangeDeltaV.magnitude).Within(1e-6));
        // The plane change happens at apoapsis (60 000 km), the slow point.
        KeplerPropagator.Propagate(r0, v0, Mu, plan.PlaneChangeUT - Ut0, out var rNode, out _);
        Assert.That(rNode.magnitude, Is.EqualTo(ra).Within(0.1).Percent);
        Assert.That(plan.Explanation, Does.StartWith("Orbit plane change first: the departure direction is 60° out of your orbit plane, which saves "));
        Assert.That(plan.Explanation, Does.EndWith(" m/s"));
        // The saving quoted is the planner's own (a) − (b), close to the brute-force one.
        string quoted = plan.Explanation.Substring(plan.Explanation.LastIndexOf("saves ", StringComparison.Ordinal) + 6).Replace(" m/s", "");
        double quotedSaving = double.Parse(quoted, System.Globalization.CultureInfo.InvariantCulture);
        Assert.That(Math.Abs(quotedSaving - saving), Is.LessThan(Math.Max(2, 0.005 * saving)), $"quoted {quoted} m/s");
        AssertPlanReachesAsymptote(r0, v0, vInf, plan);
    }

    [Test]
    public void Departure_SmallOutOfPlane_SingleBurn()
    {
        Circular(Lko, 0, out var r0, out var v0);
        var vInf = 1000 * new Vector3d(-0.6 * Math.Cos(5 * Deg), 0.8 * Math.Cos(5 * Deg), Math.Sin(5 * Deg));

        var plan = DeparturePlanner.Plan(r0, v0, Ut0, Mu, Soi, vInf);

        AssertSane(plan);
        Assert.That(plan.Ok, Is.True, plan.Refusal);
        Assert.That(plan.PlaneChangeFirst, Is.False);
        Assert.That(plan.Explanation, Is.Empty);
        Assert.That(plan.OutOfPlaneDeg, Is.EqualTo(5).Within(0.01));
        Assert.That(plan.PlaneChangeDeltaV.magnitude, Is.EqualTo(0));
        Assert.That(plan.TotalDeltaV, Is.EqualTo(plan.DeltaVMagnitude));
        Assert.That(plan.DeltaVMagnitude, Is.LessThanOrEqualTo(BruteForceSingleBurn(r0, v0, vInf, 3600) + 0.1));
        AssertPlanReachesAsymptote(r0, v0, vInf, plan);
    }

    [TestCase(180.0)]
    [TestCase(150.0)]
    [TestCase(90.0)]
    public void Departure_RetrogradeOrbit_FindsBurnOrRefuses_NeverNaN(double incDeg)
    {
        Circular(Lko, incDeg, out var r0, out var v0);
        var h = Vector3d.Cross(r0, v0).normalized;
        var directions = new[]
        {
            new Vector3d(-0.6, 0.8, 0),
            new Vector3d(0, 0, 1),     // along the rotation axis
            h,                          // along the orbit normal: no unique node line
            -1.0 * h,
            new Vector3d(1, 0, 0),      // along the starting radius
            new Vector3d(0.3, -0.5, 0.8),
        };
        foreach (var d in directions)
        {
            var vInf = 1200 * d.normalized;
            var plan = DeparturePlanner.Plan(r0, v0, Ut0, Mu, Soi, vInf);
            AssertSane(plan);
            Assert.That(plan.Ok, Is.True, $"direction ({d.x}, {d.y}, {d.z}): {plan.Refusal}");
            Assert.That(plan.TotalDeltaV, Is.LessThanOrEqualTo(BruteForceSingleBurn(r0, v0, vInf, 3600) + 0.1));
            AssertPlanReachesAsymptote(r0, v0, vInf, plan);
        }
    }

    [Test]
    public void Departure_EccentricOrbit_Works()
    {
        // e = 0.6, periapsis 700 km, inclined 30°, the departure direction partly out of plane.
        double rp = Lko, e = 0.6;
        double a = rp / (1 - e);
        double vp = Math.Sqrt(Mu * (2 / rp - 1 / a));
        var r0 = new Vector3d(0, rp, 0);
        var v0 = vp * new Vector3d(-Math.Cos(30 * Deg), 0, Math.Sin(30 * Deg));
        Assert.That(OrbitMath.Elements(r0, v0, Mu).Eccentricity, Is.EqualTo(0.6).Within(1e-9));

        foreach (var d in new[] { new Vector3d(-1, -0.5, 0.2), new Vector3d(0.4, 0.4, 0.8), new Vector3d(1, 0, 0) })
        {
            var vInf = 900 * d.normalized;
            var plan = DeparturePlanner.Plan(r0, v0, Ut0, Mu, Soi, vInf);
            AssertSane(plan);
            Assert.That(plan.Ok, Is.True, plan.Refusal);
            Assert.That(plan.TotalDeltaV, Is.LessThanOrEqualTo(BruteForceSingleBurn(r0, v0, vInf, 7200) + 0.1));
            AssertPlanReachesAsymptote(r0, v0, vInf, plan);
        }
    }

    [Test]
    public void Departure_Hyperbolic_Refused()
    {
        var r0 = new Vector3d(Lko, 0, 0);
        var v0 = new Vector3d(0, 1.1 * Math.Sqrt(2 * Mu / Lko), 0);

        var plan = DeparturePlanner.Plan(r0, v0, Ut0, Mu, Soi, new Vector3d(0, 1000, 0));

        Assert.That(plan.Ok, Is.False);
        Assert.That(plan.Refusal, Is.EqualTo("Not in a closed orbit."));
    }

    [Test]
    public void Departure_ApoapsisBeyondSoi_Refused()
    {
        double rp = Lko, ra = 2 * Soi;
        double a = 0.5 * (rp + ra);
        var r0 = new Vector3d(rp, 0, 0);
        var v0 = new Vector3d(0, Math.Sqrt(Mu * (2 / rp - 1 / a)), 0);

        var plan = DeparturePlanner.Plan(r0, v0, Ut0, Mu, Soi, new Vector3d(0, 1000, 0));

        Assert.That(plan.Ok, Is.False);
        Assert.That(plan.Refusal, Is.EqualTo("Not in a closed orbit."));
    }

    [Test]
    public void Departure_NoExcessVelocity_Refused()
    {
        Circular(Lko, 0, out var r0, out var v0);
        var plan = DeparturePlanner.Plan(r0, v0, Ut0, Mu, Soi, Vector3d.zero);
        Assert.That(plan.Ok, Is.False);
        Assert.That(plan.Refusal, Is.Not.Null.And.Not.Empty);
    }
}

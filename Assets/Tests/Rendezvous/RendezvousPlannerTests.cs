using System;
using K2D2.OrbitPlanning;
using K2D2.Rendezvous;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class RendezvousPlannerTests
{
    const double Mu = 3.5316e12;       // Kerbin, m³/s²
    const double LowOrbit = 670e3;     // 70 km above Kerbin's 600 km radius
    const double R = 700e3;            // 100 km circular orbit
    const double Ut0 = 1000;
    const double Deg = Math.PI / 180;

    // A circular orbit of radius r: position at 'angle' along the orbit from its ascending node,
    // which lies at longitude 'lan', on a plane inclined by 'incl' (all in degrees).
    static void Circular(double r, double angle, double incl, double lan, out Vector3d pos, out Vector3d vel)
    {
        var node = new Vector3d(Math.Cos(lan * Deg), Math.Sin(lan * Deg), 0);
        var normal = OrbitMath.Rotate(OrbitMath.North, node, -incl * Deg); // tilt +z about the node line
        Vector3d inPlane = Vector3d.Cross(normal, node);
        Vector3d u = Math.Cos(angle * Deg) * node + Math.Sin(angle * Deg) * inPlane;
        Vector3d w = Vector3d.Cross(normal, u);
        pos = r * u;
        vel = Math.Sqrt(Mu / r) * w;
    }

    static RvPlan PlanSameOrbit(double phaseDeg, double low = LowOrbit, int maxRevolutions = 10)
    {
        Circular(R, 0, 0, 0, out var rV, out var vV);
        Circular(R, phaseDeg, 0, 0, out var rT, out var vT);
        return RendezvousPlanner.Plan(rV, vV, rT, vT, Mu, Ut0, low, 200, maxRevolutions);
    }

    // Flies the plan with impulses from the vessel's start state; returns the state at ArrivalUT.
    static void Fly(RvPlan plan, Vector3d r, Vector3d v, out Vector3d rEnd, out Vector3d vEnd)
    {
        double ut = Ut0;
        foreach (var step in plan.Steps)
        {
            KeplerPropagator.Propagate(r, v, Mu, step.UT - ut, out r, out v);
            v += step.DeltaV;
            ut = step.UT;
        }
        KeplerPropagator.Propagate(r, v, Mu, plan.ArrivalUT - ut, out rEnd, out vEnd);
    }

    // Semi-major axis right after the given step, flown from the vessel's start.
    static OrbitElements ElementsAfter(RvPlan plan, int index, Vector3d r, Vector3d v)
    {
        double ut = Ut0;
        for (int i = 0; i <= index; i++)
        {
            KeplerPropagator.Propagate(r, v, Mu, plan.Steps[i].UT - ut, out r, out v);
            v += plan.Steps[i].DeltaV;
            ut = plan.Steps[i].UT;
        }
        return OrbitMath.Elements(r, v, Mu);
    }

    // Two tangential burns into an orbit whose period is 'periodRatio' times the circular one,
    // or ∞ when that orbit dips below 'low'.
    static double PhasingCost(double periodRatio, double low)
    {
        double a = R * Math.Pow(periodRatio, 2.0 / 3.0);
        double otherApsis = 2 * a - R;
        if (Math.Min(R, otherApsis) < low)
            return double.PositiveInfinity;
        double vApsis = Math.Sqrt(Mu * (2 / R - 1 / a));
        return 2 * Math.Abs(vApsis - Math.Sqrt(Mu / R));
    }

    // The cheapest same-orbit phasing within 'revolutions': catch up (lower) or fall back (higher).
    static double CheapestPhasing(double phaseDeg, double low, int revolutions, out bool lower)
    {
        double f = phaseDeg / 360;
        double down = double.PositiveInfinity, up = double.PositiveInfinity;
        for (int k = 1; k <= revolutions; k++)
        {
            down = Math.Min(down, PhasingCost((k - f) / k, low));
            up = Math.Min(up, PhasingCost((k + 1 - f) / k, low));
        }
        lower = down < up;
        return Math.Min(down, up);
    }

    static bool IsFinite(Vector3d v) =>
        !double.IsNaN(v.x) && !double.IsNaN(v.y) && !double.IsNaN(v.z)
        && !double.IsInfinity(v.x) && !double.IsInfinity(v.y) && !double.IsInfinity(v.z);

    static void AssertUtOrder(RvPlan plan)
    {
        for (int i = 1; i < plan.Steps.Count; i++)
            Assert.That(plan.Steps[i].UT, Is.GreaterThanOrEqualTo(plan.Steps[i - 1].UT), $"step {i} out of UT order");
    }

    static void AssertPhasing(RvPlan plan, double phaseDeg, bool expectLower, double low = LowOrbit, int revolutions = 10)
    {
        Assert.That(plan.Ok, Is.True, plan.Refusal);
        AssertUtOrder(plan);
        Assert.That(plan.Steps[0].Kind, Is.EqualTo(RvStepKind.Phasing));
        Assert.That(plan.Steps[plan.Steps.Count - 1].Kind, Is.EqualTo(RvStepKind.Brake));
        Assert.That(plan.Steps.Exists(s => s.Kind == RvStepKind.Correction), Is.True, "no correction");

        Circular(R, 0, 0, 0, out var rV, out var vV);
        var el = ElementsAfter(plan, 0, rV, vV);
        if (expectLower)
            Assert.That(el.SemiMajorAxis, Is.LessThan(R), "should go lower to catch up");
        else
            Assert.That(el.SemiMajorAxis, Is.GreaterThan(R), "should go higher to fall back");
        Assert.That(el.PeriapsisRadius, Is.GreaterThanOrEqualTo(low));

        double analytic = CheapestPhasing(phaseDeg, low, revolutions, out bool lower);
        Assert.That(lower, Is.EqualTo(expectLower), "test setup: analytic direction");
        Assert.That(plan.TotalDeltaV, Is.LessThan(analytic + 0.5), $"analytic cheapest {analytic:F2} m/s");
        TestContext.WriteLine($"phase {phaseDeg}°: Δv {plan.TotalDeltaV:F2} m/s (analytic {analytic:F2}), "
            + $"{plan.Revolutions} rev, arrival in {(plan.ArrivalUT - Ut0) / 3600:F2} h");
    }

    [Test]
    public void TargetAheadSameOrbit_PhasesCheapest()
    {
        AssertPhasing(PlanSameOrbit(30), 30, expectLower: true);
    }

    [Test]
    public void TargetBehind_PhasesOtherWay()
    {
        AssertPhasing(PlanSameOrbit(-30), 330, expectLower: false);
    }

    [Test]
    public void PhaseNear0_NoDivideByZero()
    {
        var plan = PlanSameOrbit(0.3); // 3.7 km ahead: beyond the close range
        Assert.That(plan.Ok, Is.True, plan.Refusal);
        Assert.That(plan.Steps.Exists(s => s.Kind == RvStepKind.Brake), Is.True);
        foreach (var s in plan.Steps)
        {
            Assert.That(IsFinite(s.DeltaV), Is.True, $"{s.Kind} Δv");
            Assert.That(double.IsNaN(s.UT) || double.IsInfinity(s.UT), Is.False, $"{s.Kind} UT");
        }
        double analytic = CheapestPhasing(0.3, LowOrbit, 10, out _);
        Assert.That(plan.TotalDeltaV, Is.LessThan(analytic + 0.6), $"analytic cheapest {analytic:F2} m/s");
        TestContext.WriteLine($"phase 0.3°: Δv {plan.TotalDeltaV:F2} m/s, {plan.Revolutions} rev, "
            + $"arrival in {(plan.ArrivalUT - Ut0) / 3600:F2} h");
    }

    [Test]
    public void PhaseNear180_PicksCheaper()
    {
        // Lowering by half an orbit over 10 revolutions dips below 670 km: only going higher is left.
        CheapestPhasing(179.9, LowOrbit, 10, out bool lower);
        AssertPhasing(PlanSameOrbit(179.9), 179.9, lower);
        // Without the low-orbit limit, both ways cost about the same: the cheaper one is still picked.
        CheapestPhasing(179.9, 0, 10, out lower);
        AssertPhasing(PlanSameOrbit(179.9, low: 0), 179.9, lower, low: 0);
    }

    [Test]
    public void Inclined_AddsPlaneMatch_WithExplanation()
    {
        Circular(R, 0, 0, 0, out var rV, out var vV);
        Circular(720e3, 100, 2, 40, out var rT, out var vT);
        var plan = RendezvousPlanner.Plan(rV, vV, rT, vT, Mu, Ut0, LowOrbit);

        Assert.That(plan.Ok, Is.True, plan.Refusal);
        AssertUtOrder(plan);
        var pm = plan.Steps[0];
        Assert.That(pm.Kind, Is.EqualTo(RvStepKind.PlaneMatch));
        Assert.That(pm.Explanation, Does.Contain("2.00°"));
        Assert.That(pm.DeltaVMagnitude, Is.EqualTo(2 * Math.Sqrt(Mu / R) * Math.Sin(1 * Deg)).Within(1));

        var el = ElementsAfter(plan, 0, rV, vV);
        var elT = OrbitMath.Elements(rT, vT, Mu);
        double residual = Math.Acos(Math.Min(1, Vector3d.Dot(el.AngularMomentum.normalized, elT.AngularMomentum.normalized)));
        Assert.That(residual / Deg, Is.LessThan(1e-3), "planes after the match");
        TestContext.WriteLine($"inclined: plane match {pm.DeltaVMagnitude:F2} m/s, total {plan.TotalDeltaV:F2} m/s, "
            + $"{plan.Revolutions} rev, arrival in {(plan.ArrivalUT - Ut0) / 3600:F2} h");

        // Below the 0.05° threshold: no plane match.
        Circular(720e3, 100, 0.03, 40, out rT, out vT);
        plan = RendezvousPlanner.Plan(rV, vV, rT, vT, Mu, Ut0, LowOrbit);
        Assert.That(plan.Ok, Is.True, plan.Refusal);
        Assert.That(plan.Steps.Exists(s => s.Kind == RvStepKind.PlaneMatch), Is.False);
    }

    [Test]
    public void CloseStart_FastRelative_BrakesFirst()
    {
        Circular(R, 0, 0, 0, out var rV, out var vV);
        Circular(R, 1500 / R / Deg, 0, 0, out var rT, out var vT); // 1.5 km ahead
        vV += 50 * rV.normalized;                                 // 50 m/s radial
        var plan = RendezvousPlanner.Plan(rV, vV, rT, vT, Mu, Ut0, LowOrbit);

        Assert.That(plan.Ok, Is.True, plan.Refusal);
        Assert.That(plan.Steps.Count, Is.EqualTo(1));
        var brake = plan.Steps[0];
        Assert.That(brake.Kind, Is.EqualTo(RvStepKind.Brake));
        Assert.That(brake.UT, Is.EqualTo(Ut0));
        Assert.That((brake.DeltaV - (vT - vV)).magnitude, Is.LessThan(1e-9));
        // The 50 m/s radial plus the turn of the orbital velocity over 1.5 km (~4.8 m/s, also radial).
        Assert.That(brake.DeltaVMagnitude, Is.EqualTo((vT - vV).magnitude).Within(1e-9));
        Assert.That(brake.DeltaVMagnitude, Is.GreaterThan(50));
        Assert.That(plan.TotalDeltaV, Is.EqualTo(brake.DeltaVMagnitude).Within(1e-9));
    }

    [TestCase(70, 0.0, 760e3)]
    [TestCase(200, 0.5, 760e3)]
    [TestCase(30, 0.0, R)]
    [TestCase(250, 1.0, 650e3 + 100e3)]
    public void IntercepSimulated_EndsWithin200mAndUnder1mps(double phaseDeg, double inclDeg, double targetRadius)
    {
        Circular(R, 0, 0, 0, out var rV, out var vV);
        Circular(targetRadius, phaseDeg, inclDeg, 15, out var rT, out var vT);
        var plan = RendezvousPlanner.Plan(rV, vV, rT, vT, Mu, Ut0, LowOrbit);
        Assert.That(plan.Ok, Is.True, plan.Refusal);
        AssertUtOrder(plan);

        Fly(plan, rV, vV, out var rEnd, out var vEnd);
        KeplerPropagator.Propagate(rT, vT, Mu, plan.ArrivalUT - Ut0, out var rTEnd, out var vTEnd);
        double miss = (rEnd - rTEnd).magnitude;
        double speed = (vEnd - vTEnd).magnitude;
        TestContext.WriteLine($"phase {phaseDeg}°, incl {inclDeg}°, target {targetRadius / 1e3:F0} km: "
            + $"Δv {plan.TotalDeltaV:F2} m/s, {plan.Revolutions} rev, arrival in {(plan.ArrivalUT - Ut0) / 3600:F2} h, "
            + $"miss {miss:F2} m, relative {speed:F4} m/s, steps {string.Join(", ", plan.Steps.ConvertAll(s => s.Kind + " " + s.DeltaVMagnitude.ToString("F1")))}");
        Assert.That(miss, Is.LessThanOrEqualTo(201), "distance at arrival");
        Assert.That(miss, Is.GreaterThan(150), "the stand-off is kept");
        Assert.That(speed, Is.LessThan(1), "relative speed after the brake");
    }

    [Test]
    public void FarFrom700km_InterceptSimulated()
    {
        // 3,000 km and 3,400 km around Kerbin: the arrival check scales with the orbit.
        Circular(3000e3, 0, 0, 0, out var rV, out var vV);
        Circular(3400e3, 120, 0.5, 15, out var rT, out var vT);
        var plan = RendezvousPlanner.Plan(rV, vV, rT, vT, Mu, Ut0, LowOrbit);
        Assert.That(plan.Ok, Is.True, plan.Refusal);
        AssertUtOrder(plan);
        Fly(plan, rV, vV, out var rEnd, out var vEnd);
        KeplerPropagator.Propagate(rT, vT, Mu, plan.ArrivalUT - Ut0, out var rTEnd, out var vTEnd);
        double miss = (rEnd - rTEnd).magnitude;
        TestContext.WriteLine($"3000 -> 3400 km: Δv {plan.TotalDeltaV:F2} m/s, {plan.Revolutions} rev, miss {miss:F2} m, "
            + $"relative {(vEnd - vTEnd).magnitude:F4} m/s");
        Assert.That(miss, Is.InRange(150, 205));
        Assert.That((vEnd - vTEnd).magnitude, Is.LessThan(1));
    }

    [Test]
    public void RetrogradeTarget_RefusedWithoutNaN()
    {
        Circular(R, 0, 0, 0, out var rV, out var vV);
        Circular(R, 90, 180, 0, out var rT, out var vT);
        var plan = RendezvousPlanner.Plan(rV, vV, rT, vT, Mu, Ut0, LowOrbit);
        Assert.That(plan.Ok, Is.False);
        Assert.That(plan.Refusal, Does.Contain("other way round"));
        Assert.That(plan.Refusal, Does.Not.Contain("NaN"));
    }

    [Test]
    public void PhasingExplanation_CountsTheOrbitsFlown()
    {
        var plan = PlanSameOrbit(30);
        Assert.That(plan.Ok, Is.True, plan.Refusal);
        var step = plan.Steps[0];
        // The arc flies its full revolutions plus a fraction: "10.x orbits", never "10 revolution(s)".
        Assert.That(step.Explanation, Does.Match(@"Phasing: \d+\.\d orbits"));
        Assert.That(step.Explanation, Does.Not.Contain("revolution"));
    }

    [Test]
    public void LowestRadius_OnlyCountsThePeriapsisOnTheArc()
    {
        // An ellipse from 700 km (just past its periapsis at 690 km) up toward apoapsis: no periapsis on the arc.
        double rp = 690e3, ra = 900e3, a = (rp + ra) / 2, e = (ra - rp) / (ra + rp);
        double p = a * (1 - e * e);
        Vector3d At(double nu) => p / (1 + e * Math.Cos(nu)) * new Vector3d(Math.Cos(nu), Math.Sin(nu), 0);
        Vector3d VelAt(double nu) => Math.Sqrt(Mu / p) * new Vector3d(-Math.Sin(nu), e + Math.Cos(nu), 0);
        var el = OrbitMath.Elements(At(0.3), VelAt(0.3), Mu);
        Assert.That(RendezvousPlanner.LowestRadius(At(0.3), At(2.0), el, 0), Is.EqualTo(At(0.3).magnitude).Within(1));
        // Through the periapsis (from before it to after it): the periapsis counts.
        Assert.That(RendezvousPlanner.LowestRadius(At(-0.5), At(0.5), el, 0), Is.EqualTo(rp).Within(1));
        // A full revolution always passes it.
        Assert.That(RendezvousPlanner.LowestRadius(At(0.3), At(2.0), el, 1), Is.EqualTo(rp).Within(1));
    }

    [Test]
    public void PeriapsisBelowLowOrbit_CandidateRejected()
    {
        // Catching up 30° means dropping to ~692 km: forbidden above 699 km, so it must go higher.
        AssertPhasing(PlanSameOrbit(30, low: 699e3), 30, expectLower: false, low: 699e3);

        // Nothing clears a limit above the orbit itself.
        var plan = PlanSameOrbit(30, low: 701e3);
        Assert.That(plan.Ok, Is.False);
        Assert.That(plan.Refusal, Is.Not.Empty);
    }

    [Test]
    public void MaxRevolutions_Capped()
    {
        var plan = PlanSameOrbit(30, maxRevolutions: 3);
        Assert.That(plan.Ok, Is.True, plan.Refusal);
        Assert.That(plan.Revolutions, Is.EqualTo(3), "Δv first: the most revolutions allowed");

        plan = PlanSameOrbit(30, maxRevolutions: 50);
        Assert.That(plan.Ok, Is.True, plan.Refusal);
        Assert.That(plan.Revolutions, Is.EqualTo(10), "capped at 10");
    }
}

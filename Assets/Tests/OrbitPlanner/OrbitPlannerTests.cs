using System;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class OrbitPlannerTests
{
    // Kerbin.
    const double Mu = 3.5316e12;
    const double R = 600000;
    const double Atm = 70000;
    const double Deg = Math.PI / 180;

    // Vessel at the periapsis (on the +x axis) of an orbit with the given apsis altitudes and inclination.
    static void StateAtPeriapsis(double peAlt, double apAlt, double incDeg, out Vector3d r, out Vector3d v)
    {
        double rp = R + peAlt, ra = R + apAlt;
        double speed = Math.Sqrt(Mu * (2 / rp - 2 / (rp + ra)));
        r = new Vector3d(rp, 0, 0);
        v = new Vector3d(0, speed * Math.Cos(incDeg * Deg), speed * Math.Sin(incDeg * Deg));
    }

    static void AssertRelative(double expected, double actual, double tolerance, string what)
    {
        Assert.That(Math.Abs(actual - expected) / Math.Abs(expected), Is.LessThan(tolerance),
            $"{what}: expected {expected}, got {actual}");
    }

    [Test]
    public void Elements_CircularInclinedOrbit()
    {
        StateAtPeriapsis(100000, 100000, 30, out var r, out var v);
        var el = OrbitMath.Elements(r, v, Mu);
        Assert.IsTrue(el.IsClosed);
        AssertRelative(R + 100000, el.ApoapsisRadius, 1e-6, "ra");
        AssertRelative(R + 100000, el.PeriapsisRadius, 1e-6, "rp");
        Assert.AreEqual(30.0, el.InclinationRad / Deg, 1e-6);
    }

    [Test]
    public void Elements_EscapeTrajectoryIsNotClosed()
    {
        double rMag = R + 100000;
        var r = new Vector3d(rMag, 0, 0);
        var v = new Vector3d(0, 1.5 * Math.Sqrt(2 * Mu / rMag), 0);
        Assert.IsFalse(OrbitMath.Elements(r, v, Mu).IsClosed);
    }

    [Test]
    public void TimeToTrueAnomaly_ApoapsisIsHalfAPeriodAway()
    {
        StateAtPeriapsis(100000, 500000, 0, out var r, out var v);
        var el = OrbitMath.Elements(r, v, Mu);
        double t = OrbitMath.TimeToTrueAnomaly(r, v, Mu, Math.PI);
        AssertRelative(el.Period / 2, t, 1e-6, "time to apoapsis");
        KeplerPropagator.Propagate(r, v, Mu, t, out var r1, out _);
        AssertRelative(R + 500000, r1.magnitude, 1e-5, "radius at apoapsis");
    }

    [Test]
    public void TimeToDirection_QuarterTurnOnCircularOrbit()
    {
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        var el = OrbitMath.Elements(r, v, Mu);
        double t = OrbitMath.TimeToDirection(r, v, Mu, new Vector3d(0, 1, 0));
        AssertRelative(el.Period / 4, t, 1e-6, "time to +y");
    }

    static readonly BodyInfo Kerbin = new BodyInfo { Mu = Mu, Radius = R, AtmosphereDepth = Atm };

    // Applies the plan's burns with the propagator and returns the resulting orbit.
    static OrbitElements Simulate(Vector3d r, Vector3d v, double ut0, OrbitPlan plan)
    {
        double ut = ut0;
        foreach (var burn in plan.Burns)
        {
            KeplerPropagator.Propagate(r, v, Mu, burn.UT - ut, out var r1, out var v1);
            r = r1;
            v = v1 + burn.DeltaV;
            ut = burn.UT;
        }
        return OrbitMath.Elements(r, v, Mu);
    }

    [Test]
    public void Plan_RaiseApOnly_OneHohmannBurn()
    {
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Kerbin, new OrbitTargets { ApAltitude = 500000 });
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.AreEqual(1, plan.Burns.Count);
        double r1 = R + 100000, r2 = R + 500000;
        double hohmann = Math.Sqrt(Mu / r1) * (Math.Sqrt(2 * r2 / (r1 + r2)) - 1);
        AssertRelative(hohmann, plan.Burns[0].DeltaV.magnitude, 1e-2, "Hohmann dv");
        var final = Simulate(r, v, 1000, plan);
        AssertRelative(r2, final.ApoapsisRadius, 1e-3, "final ra");
        AssertRelative(r1, final.PeriapsisRadius, 1e-3, "final rp");
    }

    [Test]
    public void Plan_LowerPeOnly_OneBurnAtApoapsis()
    {
        StateAtPeriapsis(100000, 500000, 0, out var r, out var v);
        var el = OrbitMath.Elements(r, v, Mu);
        var plan = OrbitPlanner.Plan(r, v, 1000, Kerbin, new OrbitTargets { PeAltitude = 300000 });
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.AreEqual(1, plan.Burns.Count);
        AssertRelative(1000 + el.Period / 2, plan.Burns[0].UT, 1e-3, "burn at apoapsis");
        var final = Simulate(r, v, 1000, plan);
        AssertRelative(R + 500000, final.ApoapsisRadius, 1e-3, "final ra");
        AssertRelative(R + 300000, final.PeriapsisRadius, 1e-3, "final rp");
    }

    [Test]
    public void Plan_ApAndPe_TwoBurns()
    {
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Kerbin, new OrbitTargets { ApAltitude = 500000, PeAltitude = 300000 });
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.AreEqual(2, plan.Burns.Count);
        Assert.Less(plan.Burns[0].UT, plan.Burns[1].UT);
        var final = Simulate(r, v, 1000, plan);
        AssertRelative(R + 500000, final.ApoapsisRadius, 1e-3, "final ra");
        AssertRelative(R + 300000, final.PeriapsisRadius, 1e-3, "final rp");
    }

    [Test]
    public void Plan_AlreadyOnTarget_NoBurn()
    {
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Kerbin, new OrbitTargets { ApAltitude = 100000, PeAltitude = 100000 });
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.AreEqual(0, plan.Burns.Count);
    }

    [TestCase(-10000.0, 400000.0, OrbitPlanError.PeBelowSurface)]
    [TestCase(50000.0, 400000.0, OrbitPlanError.PeInAtmosphere)]
    [TestCase(300000.0, 200000.0, OrbitPlanError.PeAboveAp)]
    public void Plan_RefusesBadTargets(double peAlt, double apAlt, OrbitPlanError expected)
    {
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Kerbin, new OrbitTargets { ApAltitude = apAlt, PeAltitude = peAlt });
        Assert.AreEqual(expected, plan.Error);
        Assert.AreEqual(0, plan.Burns.Count);
    }

    [Test]
    public void Plan_RefusesEscapeTrajectory()
    {
        double rMag = R + 100000;
        var r = new Vector3d(rMag, 0, 0);
        var v = new Vector3d(0, 1.5 * Math.Sqrt(2 * Mu / rMag), 0);
        var plan = OrbitPlanner.Plan(r, v, 1000, Kerbin, new OrbitTargets { ApAltitude = 500000 });
        Assert.AreEqual(OrbitPlanError.EscapeTrajectory, plan.Error);
    }

    [Test]
    public void Plan_RefusesUnstableCurrentOrbit()
    {
        StateAtPeriapsis(50000, 50000, 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Kerbin, new OrbitTargets { ApAltitude = 500000 });
        Assert.AreEqual(OrbitPlanError.UnstableCurrentOrbit, plan.Error);
    }

    [Test]
    public void Plan_RaiseAp_FromBetweenApsides_BurnsAtNextPeriapsis()
    {
        StateAtPeriapsis(100000, 500000, 0, out var r, out var v);
        var el = OrbitMath.Elements(r, v, Mu);
        KeplerPropagator.Propagate(r, v, Mu, el.Period / 4, out var rq, out var vq);
        var plan = OrbitPlanner.Plan(rq, vq, 1000, Kerbin, new OrbitTargets { ApAltitude = 800000 });
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.AreEqual(1, plan.Burns.Count);
        AssertRelative(el.Period * 3 / 4, plan.Burns[0].UT - 1000, 1e-3, "time to next periapsis");
        var final = Simulate(rq, vq, 1000, plan);
        AssertRelative(R + 800000, final.ApoapsisRadius, 1e-3, "final ra");
        AssertRelative(R + 100000, final.PeriapsisRadius, 1e-3, "final rp");
    }

    [Test]
    public void Plan_RaiseAp_AtPeriapsis_WaitsOnePeriod()
    {
        StateAtPeriapsis(100000, 500000, 0, out var r, out var v);
        var el = OrbitMath.Elements(r, v, Mu);
        var plan = OrbitPlanner.Plan(r, v, 1000, Kerbin, new OrbitTargets { ApAltitude = 800000 });
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.AreEqual(1, plan.Burns.Count);
        AssertRelative(el.Period, plan.Burns[0].UT - 1000, 1e-3, "one period lead");
        var final = Simulate(r, v, 1000, plan);
        AssertRelative(R + 800000, final.ApoapsisRadius, 1e-3, "final ra");
        AssertRelative(R + 100000, final.PeriapsisRadius, 1e-3, "final rp");
    }

    [Test]
    public void Plan_LowerBoth_TwoBurns()
    {
        StateAtPeriapsis(400000, 400000, 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Kerbin, new OrbitTargets { ApAltitude = 300000, PeAltitude = 200000 });
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.AreEqual(2, plan.Burns.Count);
        Assert.Less(plan.Burns[0].UT, plan.Burns[1].UT);
        var final = Simulate(r, v, 1000, plan);
        AssertRelative(R + 300000, final.ApoapsisRadius, 1e-3, "final ra");
        AssertRelative(R + 200000, final.PeriapsisRadius, 1e-3, "final rp");
    }

    [Test]
    public void Plan_RaiseAp_BurnIsPurePrograde()
    {
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Kerbin, new OrbitTargets { ApAltitude = 500000 });
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        var burn = plan.Burns[0];
        double dv = burn.DeltaV.magnitude;
        Assert.Less(Math.Abs(burn.Radial), 1e-6 * dv);
        Assert.Less(Math.Abs(burn.Normal), 1e-6 * dv);
        Assert.Greater(burn.Prograde, 0);
        AssertRelative(dv, burn.Prograde, 1e-9, "prograde");
        AssertRelative(plan.Burns[0].DeltaV.magnitude, plan.TotalDeltaV, 1e-12, "total");
        AssertRelative(R + 500000, plan.Final.ApoapsisRadius, 1e-3, "final ra");
    }

    [Test]
    public void Plan_InclinationOnly_OnePlaneChange()
    {
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Kerbin, new OrbitTargets { InclinationDeg = 30 });
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.AreEqual(1, plan.Burns.Count);
        double speed = Math.Sqrt(Mu / (R + 100000));
        AssertRelative(2 * speed * Math.Sin(15 * Deg), plan.Burns[0].DeltaV.magnitude, 1e-3, "plane change dv");
        var final = Simulate(r, v, 1000, plan);
        Assert.AreEqual(30.0, final.InclinationRad / Deg, 0.05);
        AssertRelative(R + 100000, final.ApoapsisRadius, 1e-3, "final ra");
        AssertRelative(R + 100000, final.PeriapsisRadius, 1e-3, "final rp");

        var burn = plan.Burns[0];
        KeplerPropagator.Propagate(r, v, Mu, burn.UT - 1000, out var rb, out var vb);
        Vector3d pro = vb.normalized;
        Vector3d nor = Vector3d.Cross(rb, vb).normalized;
        Vector3d rad = Vector3d.Cross(pro, nor);
        Vector3d rebuilt = burn.Prograde * pro + burn.Normal * nor + burn.Radial * rad;
        Assert.Less((rebuilt - burn.DeltaV).magnitude, 1e-6 * burn.DeltaV.magnitude, "decomposition rebuilds DeltaV");
        Assert.Greater(Math.Abs(burn.Normal), 0.5 * burn.DeltaV.magnitude, "plane change is mostly normal");
    }

    [Test]
    public void Plan_Combined_ThreeBurnsInOrder()
    {
        StateAtPeriapsis(100000, 500000, 10, out var r, out var v);
        var targets = new OrbitTargets { ApAltitude = 800000, PeAltitude = 200000, InclinationDeg = 45 };
        var plan = OrbitPlanner.Plan(r, v, 1000, Kerbin, targets);
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.AreEqual(3, plan.Burns.Count);
        Assert.Less(plan.Burns[0].UT, plan.Burns[1].UT);
        Assert.Less(plan.Burns[1].UT, plan.Burns[2].UT);
        var final = Simulate(r, v, 1000, plan);
        AssertRelative(R + 800000, final.ApoapsisRadius, 1e-3, "final ra");
        AssertRelative(R + 200000, final.PeriapsisRadius, 1e-3, "final rp");
        Assert.AreEqual(45.0, final.InclinationRad / Deg, 0.05);
        foreach (var burn in plan.Burns)
        {
            double parts = Math.Sqrt(burn.Radial * burn.Radial + burn.Normal * burn.Normal + burn.Prograde * burn.Prograde);
            AssertRelative(burn.DeltaV.magnitude, parts, 1e-9, "burn components");
        }
    }

    [Test]
    public void Plan_RefusesInclinationOutOfRange()
    {
        StateAtPeriapsis(100000, 100000, 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Kerbin, new OrbitTargets { InclinationDeg = 200 });
        Assert.AreEqual(OrbitPlanError.InclinationOutOfRange, plan.Error);
    }
}

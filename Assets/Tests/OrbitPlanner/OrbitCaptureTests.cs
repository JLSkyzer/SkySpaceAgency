using System;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// The Orbit tab from an open trajectory inside a body's SOI: a capture burn first, then the
// usual plan (OrbitPlanner.PlanCapture).
public class OrbitCaptureTests
{
    // The Mun.
    const double Mu = 6.5138398e10;
    const double R = 200000;
    const double Soi = 2429559;
    const double Deg = Math.PI / 180;

    static readonly BodyInfo Mun = new BodyInfo { Mu = Mu, Radius = R, AtmosphereDepth = 0, SoiRadius = Soi };

    // State on the hyperbola of periapsis radius rp and excess speed vInf, at true anomaly nuDeg
    // (negative = inbound), in a plane of inclination incDeg (node line on +x).
    static void Hyperbola(double rp, double vInf, double nuDeg, double incDeg, out Vector3d r, out Vector3d v)
    {
        double e = 1 + rp * vInf * vInf / Mu;
        double p = rp * (1 + e);
        double nu = nuDeg * Deg;
        double rMag = p / (1 + e * Math.Cos(nu));
        double k = Math.Sqrt(Mu / p);
        var rPlane = new Vector3d(rMag * Math.Cos(nu), rMag * Math.Sin(nu), 0);
        var vPlane = new Vector3d(-k * Math.Sin(nu), k * (e + Math.Cos(nu)), 0);
        double c = Math.Cos(incDeg * Deg), s = Math.Sin(incDeg * Deg);
        r = new Vector3d(rPlane.x, rPlane.y * c, rPlane.y * s);
        v = new Vector3d(vPlane.x, vPlane.y * c, vPlane.y * s);
    }

    // True anomaly (degrees, inbound) where the hyperbola crosses radius rMag.
    static double InboundAnomalyAt(double rp, double vInf, double rMag)
    {
        double e = 1 + rp * vInf * vInf / Mu;
        double p = rp * (1 + e);
        return -Math.Acos((p / rMag - 1) / e) / Deg;
    }

    static OrbitElements Simulate(Vector3d r, Vector3d v, double ut, OrbitPlan plan)
    {
        foreach (var burn in plan.Burns)
        {
            KeplerPropagator.Propagate(r, v, Mu, burn.UT - ut, out var rb, out var vb);
            r = rb;
            v = vb + burn.DeltaV;
            ut = burn.UT;
        }
        return OrbitMath.Elements(r, v, Mu);
    }

    static void AssertRelative(double expected, double actual, double tolerance, string what)
    {
        Assert.That(Math.Abs(actual - expected) / Math.Abs(expected), Is.LessThan(tolerance),
            $"{what}: expected {expected}, got {actual}");
    }

    [Test]
    public void Flyby_PeriapsisAhead_CapturesThereIntoACircularOrbit()
    {
        double rp = R + 170000, vInf = 500;
        Hyperbola(rp, vInf, -90, 12, out var r, out var v);
        Assert.IsTrue(OrbitPlanner.NeedsCapture(r, v, Mun));

        var plan = OrbitPlanner.Plan(r, v, 1000, Mun, default);
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.IsNotNull(plan.Capture);
        Assert.IsTrue(plan.Capture.AtPeriapsis);
        Assert.AreEqual(1, plan.Burns.Count, "unticked targets: the capture alone");
        StringAssert.StartsWith("Capture at periapsis", plan.Burns[0].Label);

        double tPe = OrbitPlanner.TimeToPeriapsis(r, v, Mu);
        Assert.AreEqual(1000 + tPe, plan.Burns[0].UT, 1e-6);
        KeplerPropagator.Propagate(r, v, Mu, tPe, out var rAtPe, out _);
        AssertRelative(rp, rAtPe.magnitude, 1e-6, "radius at the time to periapsis");
        AssertRelative(rp, plan.Capture.Radius, 1e-6, "capture radius");

        AssertRelative(K2D2.Transfer.Hyperbola.CaptureDeltaV(vInf, rp, rp, Mu), plan.Capture.DeltaV, 1e-4, "capture dv");
        Assert.AreEqual(plan.Capture.DeltaV, plan.Burns[0].DeltaV.magnitude, 1e-9);
        Assert.Less(plan.Burns[0].Prograde, 0, "a retrograde burn");
        Assert.AreEqual(0, plan.Burns[0].Radial, 1e-3 * plan.Capture.DeltaV, "tangential at the periapsis");

        var final = Simulate(r, v, 1000, plan);
        AssertRelative(rp, final.ApoapsisRadius, 1e-4, "final ra");
        AssertRelative(rp, final.PeriapsisRadius, 1e-4, "final rp");
        Assert.AreEqual(12, final.InclinationRad / Deg, 1e-3, "the plane is kept");
    }

    [Test]
    public void Flyby_PastPeriapsis_CapturesAsSoonAsPossible()
    {
        double rp = R + 170000;
        Hyperbola(rp, 500, 60, 0, out var r, out var v);

        var plan = OrbitPlanner.Plan(r, v, 1000, Mun, default);
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.IsFalse(plan.Capture.AtPeriapsis);
        Assert.AreEqual(1000 + OrbitPlanner.MinLeadSeconds, plan.Burns[0].UT, 1e-9);
        StringAssert.StartsWith("Capture in 1 min", plan.Burns[0].Label);

        KeplerPropagator.Propagate(r, v, Mu, OrbitPlanner.MinLeadSeconds, out var rb, out _);
        AssertRelative(rb.magnitude, plan.Capture.Radius, 1e-9, "capture radius");
        Assert.Greater(plan.Capture.Radius, r.magnitude, "outbound: higher than now");

        var final = Simulate(r, v, 1000, plan);
        AssertRelative(plan.Capture.Radius, final.ApoapsisRadius, 1e-4, "final ra");
        AssertRelative(plan.Capture.Radius, final.PeriapsisRadius, 1e-4, "final rp");
    }

    [Test]
    public void Flyby_PastPeriapsis_LeavingTheSoiFirst_IsRefused()
    {
        double rp = R + 170000;
        double nu = -InboundAnomalyAt(rp, 500, Soi - 1000);
        Hyperbola(rp, 500, nu, 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Mun, default);
        Assert.AreEqual(OrbitPlanError.CaptureLeavesSoi, plan.Error);
        Assert.AreEqual(0, plan.Burns.Count);
        Assert.IsNull(plan.Capture);
    }

    [Test]
    public void PeriapsisBelowSurface_TooLowToCapture_IsRefused()
    {
        double rp = R - 50000, vInf = 500;
        Hyperbola(rp, vInf, InboundAnomalyAt(rp, vInf, R + 20000), 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Mun, default);
        Assert.AreEqual(OrbitPlanError.CaptureBelowSurface, plan.Error);
        Assert.AreEqual(0, plan.Burns.Count);
        Assert.IsNull(plan.Capture);
    }

    [Test]
    public void PeriapsisBelowSurface_StillHigh_CapturesAsSoonAsPossibleAboveTheSurface()
    {
        double rp = R - 50000, vInf = 500;
        Hyperbola(rp, vInf, InboundAnomalyAt(rp, vInf, 1500000), 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Mun, default);
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.IsFalse(plan.Capture.AtPeriapsis);
        var final = Simulate(r, v, 1000, plan);
        Assert.IsTrue(final.IsClosed);
        Assert.Greater(final.PeriapsisRadius, R + 1000, "the captured orbit clears the surface");
    }

    [Test]
    public void TickedTargets_CaptureAtPeriapsisThenTheUsualPlan()
    {
        double rp = R + 170000;
        Hyperbola(rp, 500, -90, 0, out var r, out var v);
        var targets = new OrbitTargets { ApAltitude = 500000, PeAltitude = 100000 };
        var plan = OrbitPlanner.Plan(r, v, 1000, Mun, targets);
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.IsTrue(plan.Capture.AtPeriapsis);
        Assert.AreEqual(2, plan.Burns.Count, "capture into Ap 500 km, then Pe 100 km at the apoapsis");
        Assert.AreEqual(R + 500000, plan.Burns[0].Target.Ra, 1);
        Assert.AreEqual(rp, plan.Burns[0].Target.Rp, 1);
        Assert.Less(plan.Burns[0].UT, plan.Burns[1].UT);

        var final = Simulate(r, v, 1000, plan);
        AssertRelative(R + 500000, final.ApoapsisRadius, 1e-3, "final ra");
        AssertRelative(R + 100000, final.PeriapsisRadius, 1e-3, "final rp");
    }

    [Test]
    public void TickedInclination_IsChangedAfterTheCapture()
    {
        Hyperbola(R + 170000, 500, -90, 5, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Mun, new OrbitTargets { InclinationDeg = 30 });
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.AreEqual(2, plan.Burns.Count);
        Assert.AreEqual(5, plan.Burns[0].Target.InclinationRad / Deg, 1e-3, "the capture keeps the plane");
        Assert.AreEqual(30, Simulate(r, v, 1000, plan).InclinationRad / Deg, 0.05);
    }

    [Test]
    public void TargetApBeyondTheSoi_IsRefused()
    {
        Hyperbola(R + 170000, 500, -90, 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Mun, new OrbitTargets { ApAltitude = Soi });
        Assert.AreEqual(OrbitPlanError.CaptureLeavesSoi, plan.Error);
    }

    [Test]
    public void EscapingEllipse_IsCapturedToo()
    {
        // Elliptic, apoapsis beyond the SOI, inbound.
        double rp = R + 170000, ra = 2 * Soi;
        double a = (ra + rp) / 2, e = (ra - rp) / (ra + rp), p = a * (1 - e * e);
        double nu = -120 * Deg, k = Math.Sqrt(Mu / p);
        double rMag = p / (1 + e * Math.Cos(nu));
        var r = new Vector3d(rMag * Math.Cos(nu), rMag * Math.Sin(nu), 0);
        var v = new Vector3d(-k * Math.Sin(nu), k * (e + Math.Cos(nu)), 0);
        Assert.IsTrue(OrbitMath.Elements(r, v, Mu).IsClosed);
        Assert.IsTrue(OrbitPlanner.NeedsCapture(r, v, Mun));

        var plan = OrbitPlanner.Plan(r, v, 1000, Mun, default);
        Assert.IsTrue(plan.Ok, plan.Error.ToString());
        Assert.IsTrue(plan.Capture.AtPeriapsis);
        AssertRelative(rp, Simulate(r, v, 1000, plan).ApoapsisRadius, 1e-4, "circular at the periapsis");
    }

    [Test]
    public void UnknownSoi_KeepsRefusingOpenTrajectories()
    {
        Hyperbola(R + 170000, 500, -90, 0, out var r, out var v);
        var body = new BodyInfo { Mu = Mu, Radius = R };
        Assert.IsFalse(OrbitPlanner.NeedsCapture(r, v, body));
        Assert.AreEqual(OrbitPlanError.EscapeTrajectory, OrbitPlanner.Plan(r, v, 1000, body, default).Error);
    }

    [Test]
    public void ClosedOrbitInsideTheSoi_NeedsNoCapture()
    {
        double rMag = R + 100000;
        var r = new Vector3d(rMag, 0, 0);
        var v = new Vector3d(0, Math.Sqrt(Mu / rMag), 0);
        Assert.IsFalse(OrbitPlanner.NeedsCapture(r, v, Mun));
        var plan = OrbitPlanner.Plan(r, v, 1000, Mun, new OrbitTargets { ApAltitude = 300000 });
        Assert.IsTrue(plan.Ok);
        Assert.IsNull(plan.Capture);
        Assert.AreEqual(1, plan.Burns.Count);
    }

    [Test]
    public void CaptureBudget_RefusesMoreThanTheVesselHas()
    {
        Hyperbola(R + 170000, 500, -90, 0, out var r, out var v);
        var plan = OrbitPlanner.Plan(r, v, 1000, Mun, default);
        double dv = plan.Capture.DeltaV;
        Assert.AreEqual(OrbitPlanError.CaptureBeyondDeltaV, OrbitPlanner.CheckCaptureBudget(plan, dv - 1));
        Assert.AreEqual(OrbitPlanError.None, OrbitPlanner.CheckCaptureBudget(plan, dv + 1));
        Assert.AreEqual(OrbitPlanError.None, OrbitPlanner.CheckCaptureBudget(plan, double.NaN), "unknown Δv: no check");
    }

    [Test]
    public void Sequence_ReplansFromTheCapturedOrbit_UntilDone()
    {
        Hyperbola(R + 170000, 500, -90, 0, out var r, out var v);
        double ut = 1000;
        var targets = new OrbitTargets { ApAltitude = 500000, PeAltitude = 100000 };
        var first = OrbitPlanner.Plan(r, v, ut, Mun, targets);
        var sequence = new OrbitPlanSequence(targets, Mun, first.Burns.Count);

        var step = sequence.Next(r, v, ut, Mun);
        Assert.AreEqual(OrbitPlanStepOutcome.Burn, step.Outcome);
        StringAssert.StartsWith("Capture", step.Burn.Label);
        Assert.AreEqual(2, step.RemainingBurns);

        // An imprecise capture (2 % short): the next step is planned from the orbit reached.
        KeplerPropagator.Propagate(r, v, Mu, step.Burn.UT - ut, out var rb, out var vb);
        r = rb;
        v = vb + 0.98 * step.Burn.DeltaV;
        ut = step.Burn.UT;
        Assert.IsFalse(OrbitPlanner.NeedsCapture(r, v, Mun), "captured");
        Assert.IsNull(OrbitPlanner.Plan(r, v, ut, Mun, targets).Capture, "no second capture");

        for (int i = 0; i < OrbitPlanSequence.ExtraSteps + 2; i++)
        {
            step = sequence.Next(r, v, ut, Mun);
            if (step.Outcome != OrbitPlanStepOutcome.Burn)
                break;
            KeplerPropagator.Propagate(r, v, Mu, step.Burn.UT - ut, out rb, out vb);
            r = rb;
            v = vb + step.Burn.DeltaV;
            ut = step.Burn.UT;
        }
        Assert.AreEqual(OrbitPlanStepOutcome.Done, step.Outcome);
        var final = OrbitMath.Elements(r, v, Mu);
        Assert.That(OrbitPlanner.SameRadius(R + 500000, final.ApoapsisRadius), $"ra {final.ApoapsisRadius}");
        Assert.That(OrbitPlanner.SameRadius(R + 100000, final.PeriapsisRadius), $"rp {final.PeriapsisRadius}");
    }
}

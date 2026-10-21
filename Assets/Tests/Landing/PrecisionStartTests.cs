using System;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// The plans a precision landing makes before it flies (PrecisionStart.Plan), on a rotating Mun.
public class PrecisionStartTests
{
    const double Mu = 6.5138398e10;
    const double R = 200000;
    const double RotationPeriod = 138984.38;
    const double UT = 1000;
    const double Deg = Math.PI / 180;
    const double Isp = 320;
    static double G => Mu / (R * R);
    static Vector3d Omega => BodyRotation.AngularVelocity(BodyRotation.AngularSpeed(RotationPeriod), 1);
    static Body Mun => new Body { Mu = Mu, Radius = R, Omega = Omega, TerrainHeight = p => 0 };
    static readonly double ThrustKN = 3 * 10 * G;

    static StartInput Input(Vector3d r, Vector3d v, Vector3d target) => new StartInput
    {
        R = r, V = v, UT = UT, Body = Mun,
        Vehicle = new Vehicle { ThrustKN = ThrustKN, MassT = 10, Isp = Isp, StopHeight = 1500 },
        TargetDirNow = target, TouchDownSpeed = 2, MaxTiltDeg = 40,
    };

    static Vector3d FarSide8N => new Vector3d(-Math.Cos(8 * Deg), 0, Math.Sin(8 * Deg));

    [Test]
    public void CircularOrbit_FarSideTarget_PlansThePlaneChangeAndStaysInTheBudget()
    {
        double r0 = R + 30000;
        StartPlan plan = PrecisionStart.Plan(Input(new Vector3d(r0, 0, 0), new Vector3d(0, Math.Sqrt(Mu / r0), 0), FarSide8N));

        TestContext.Out.WriteLine($"{plan.Budget.Summary} | total {plan.TotalDeltaV:F1} m/s, terminal {plan.TerminalReserve:F1} " +
            $"+ lateral {plan.LateralReserve:F1}, baseline final {plan.BaselineTerminalReserve:F1}, calls {plan.PredictCalls}");
        Assert.IsTrue(plan.Ok, plan.Reason);
        Assert.IsFalse(plan.NeedsPreparation);
        Assert.IsTrue(plan.PlaneChange.Needed);
        Assert.Greater(plan.PlaneChange.DeltaV, 50);
        Assert.IsTrue(plan.Deorbit.Ok);
        Assert.IsTrue(plan.Baseline.Ok, plan.Baseline.Reason);
        Assert.IsTrue(plan.Budget.WithinLimit, plan.Budget.Reason);
        Assert.Greater(plan.TerminalReserve, 0);
        Assert.AreEqual(plan.PlaneChange.DeltaV + plan.Budget.PrecisionDeltaV, plan.TotalDeltaV, 1e-9);
        // Baseline from the new plane, braking to 50 m: same brake within a few m/s.
        Assert.AreEqual(plan.Deorbit.DeltaV.magnitude, plan.Baseline.DeltaV.magnitude, 1);
    }

    [Test]
    public void EccentricOrbit_CircularizesAtThePeriapsisFirst()
    {
        double rp = R + 30000, ra = R + 60000;
        double a = (ra + rp) / 2;
        double vp = Math.Sqrt(Mu * (2 / rp - 1 / a));
        // 100 s past the periapsis; target under the equatorial track: no plane change.
        KeplerPropagator.Propagate(new Vector3d(rp, 0, 0), new Vector3d(0, vp, 0), Mu, 100, out Vector3d r, out Vector3d v);
        StartPlan plan = PrecisionStart.Plan(Input(r, v, new Vector3d(0, 1, 0)));

        TestContext.Out.WriteLine($"{plan.Budget.Summary} | prep at +{plan.PreparationUT - UT:F1} s");
        Assert.IsTrue(plan.Ok, plan.Reason);
        Assert.IsTrue(plan.NeedsPreparation);
        Assert.AreEqual(vp - Math.Sqrt(Mu / rp), plan.PreparationDeltaV, 0.05);
        // The next periapsis passage: one period after the last one.
        Assert.AreEqual(2 * Math.PI * Math.Sqrt(a * a * a / Mu) - 100, plan.PreparationUT - UT, 1);
        Assert.AreEqual(rp, plan.PlanR.magnitude, 1);
        Assert.AreEqual(Math.Sqrt(Mu / plan.PlanR.magnitude), plan.PlanV.magnitude, 1e-6);
        Assert.IsFalse(plan.PlaneChange.Needed);
        Assert.AreEqual(plan.PreparationDeltaV, plan.Budget.PreparationDeltaV, 1e-12);
    }

    [Test]
    public void HighPeriapsis_IsRefusedWithTheMessage()
    {
        double r0 = R + 110000;
        StartPlan plan = PrecisionStart.Plan(Input(new Vector3d(r0, 0, 0), new Vector3d(0, Math.Sqrt(Mu / r0), 0), FarSide8N));
        Assert.IsFalse(plan.Ok);
        Assert.AreEqual("Periapsis above 100 km: lower the orbit first (Orbit tab).", plan.Reason);
    }

    [Test]
    public void HighApoapsisLowPeriapsis_IsAccepted()
    {
        double rp = R + 30000, ra = R + 400000;
        double a = (ra + rp) / 2;
        StartPlan plan = PrecisionStart.Plan(Input(new Vector3d(rp, 0, 0),
            new Vector3d(0, Math.Sqrt(Mu * (2 / rp - 1 / a)), 0), new Vector3d(0, 1, 0)));
        Assert.IsTrue(plan.Ok, plan.Reason);
        Assert.IsTrue(plan.NeedsPreparation);
    }

    [Test]
    public void OrbitIntoTheGround_AndEscape_AreRefused()
    {
        double r0 = R + 30000;
        StartPlan ground = PrecisionStart.Plan(Input(new Vector3d(r0, 0, 0), new Vector3d(0, 0.8 * Math.Sqrt(Mu / r0), 0), FarSide8N));
        StartPlan escape = PrecisionStart.Plan(Input(new Vector3d(r0, 0, 0), new Vector3d(0, 1.5 * Math.Sqrt(Mu / r0), 0), FarSide8N));
        Assert.AreEqual(PrecisionStart.HitsGroundMessage, ground.Reason);
        Assert.AreEqual(PrecisionStart.NotClosedMessage, escape.Reason);
    }

    [Test]
    public void LateralReserve_ZemZevOverTheDescent_MoreWhenTheTiltIsTooSmall()
    {
        double t = PrecisionStart.DescentTime(1500, G, 2, 3 * G);
        TestContext.Out.WriteLine($"descent {t:F1} s; reserve 40° {PrecisionStart.LateralReserve(100, G, 40, t):F2}, " +
            $"3° {PrecisionStart.LateralReserve(100, G, 3, t):F2}, 1° {PrecisionStart.LateralReserve(100, G, 1, t):F2} m/s");
        // TWR 3 on the Mun from 1500 m: ~55 s, so 100 m costs 3·100/55 ≈ 5.5 m/s at 40°.
        Assert.AreEqual(55, t, 3);
        Assert.AreEqual(300 / t, PrecisionStart.LateralReserve(100, G, 40, t), 1e-9);
        Assert.AreEqual(300 / t, PrecisionStart.LateralReserve(100, G, 20, t), 1e-9);
        Assert.Greater(PrecisionStart.LateralReserve(100, G, 1, t), PrecisionStart.LateralReserve(100, G, 3, t));
        Assert.Greater(PrecisionStart.LateralReserve(100, G, 1, t), 300 / t);
        Assert.IsNaN(PrecisionStart.LateralReserve(100, G, 0, t));
        Assert.AreEqual(0, PrecisionStart.LateralReserve(0, G, 40, t));
    }

    [Test]
    public void DescentTime_MatchesAFreeFallThenTheBrake()
    {
        // From rest at h: fall to the profile speed sb, then brake at d to vf. Integrated by hand.
        double a = 3 * G, d = TerminalGuidance.PlanFraction * (a - G), vf = 2;
        double h = 1500, hb = (2 * G * h - vf * vf) / (2 * (G + d));
        double sb = Math.Sqrt(vf * vf + 2 * d * hb);
        Assert.AreEqual(h - hb, sb * sb / (2 * G), 1e-6, "the fall ends on the profile");
        Assert.AreEqual(sb / G + (sb - vf) / d, PrecisionStart.DescentTime(h, G, vf, a), 1e-9);
        Assert.IsNaN(PrecisionStart.DescentTime(h, G, vf, 0.5 * G));
    }

    [Test]
    public void AtLaterTime_TurnsTheTargetAndKeepsTheTerrainBodyFixed()
    {
        Vector3d hill = new Vector3d(0, R, 0);
        Body body = Mun;
        body.TerrainHeight = p => (p.normalized - hill.normalized).magnitude < 1e-6 ? 100 : 0;
        PrecisionStart.AtLaterTime(body, hill.normalized, 600, out Body later, out Vector3d targetLater);

        Assert.AreEqual(0, (targetLater - SurfaceGeometry.AtTime(hill.normalized, Omega, 600)).magnitude, 1e-12);
        Assert.AreEqual(100, later.TerrainHeight(targetLater * R), 1e-9, "the hill is found where it turned to");
        Assert.AreEqual(0, later.TerrainHeight(hill), 1e-9);
    }

    [Test]
    public void Predict_ReadsTheMissOfADescentAndRefusesAnOrbit()
    {
        double r0 = R + 30000;
        var vehicle = new Vehicle { ThrustKN = ThrustKN, MassT = 10, Isp = Isp, StopHeight = 1500 };
        Vector3d r = new Vector3d(r0, 0, 0), v = new Vector3d(0, Math.Sqrt(Mu / r0), 0);

        Miss orbit = PrecisionPhases.Predict(r, v, UT, Mun, vehicle, FarSide8N);
        Assert.IsFalse(orbit.Ok);
        Assert.IsNotNull(orbit.Reason);

        DeorbitPlan d = DeorbitSearch.Baseline(r, v, UT, Mun, vehicle, UT);
        Vehicle after = vehicle;  // the impulse's propellant is gone
        after.MassT *= Math.Exp(-d.DeltaV.magnitude / (Isp * BrakeSimulator.G0));
        Miss descent = PrecisionPhases.Predict(r, v + d.DeltaV, UT, Mun, after, d.Stop.StopDirNow);
        Assert.IsTrue(descent.Ok, descent.Reason);
        Assert.AreEqual(0, descent.DistanceM, 1, "the stop of the same descent");
        Assert.AreEqual(0, descent.AlongM, 1);
    }
}

using System;
using K2D2.Landing.Braking;
using K2D2.Landing.Heating;
using K2D2.Landing.Precision;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class ThermalModelTests
{
    static ThermalConstants C => ThermalConstants.Defaults;

    [Test]
    public void ReentryFlux_IsTheGameFormula()
    {
        double expected = 500 * Math.Pow(3000 / 300.0, 3) * 1.2 * Math.Pow(0.01, 0.53) * 1 * Math.Pow(0.8, 0.3) * 1.5;
        Assert.That(ThermalModel.ReentryFlux(C, 3000, 0.01, 1.2, 0.8, 1.5), Is.EqualTo(expected).Within(1e-6 * expected));
    }

    [Test]
    public void ReentryFlux_IsZeroWithoutSpeedOrAir()
    {
        Assert.That(ThermalModel.ReentryFlux(C, 0, 0.01, 1, 1, 1), Is.EqualTo(0));
        Assert.That(ThermalModel.ReentryFlux(C, 2000, 0, 1, 1, 1), Is.EqualTo(0));
    }

    [Test]
    public void GatedFlux_IsZeroBelowMach1()
    {
        Assert.That(ThermalModel.GatedFlux(C, 330, 340, 1, 1, 1, 1), Is.EqualTo(0));
        Assert.That(ThermalModel.GatedFlux(C, 350, 340, 1, 1, 1, 1), Is.GreaterThan(0));
        Assert.That(ThermalModel.GatedFlux(C, 3000, 340, 0.01, 1, 0, 1), Is.EqualTo(0), "no drag coefficient, no flux");
    }

    [Test]
    public void Conductance_FadesAtTheTopOfTheAtmosphere()
    {
        double deep = ThermalModel.Conductance(C, 10, 0, 70000);
        Assert.That(deep, Is.EqualTo(10 * 50 * 0.026).Within(1e-6), "full exchange deep in the air");
        Assert.That(ThermalModel.Conductance(C, 10, 69000, 70000), Is.EqualTo(0.5 * deep).Within(1e-9), "half at 1 km under the top");
        Assert.That(ThermalModel.Conductance(C, 10, 71000, 70000), Is.LessThan(0.001 * deep));
    }

    [Test]
    public void Step_ReachesTheEquilibriumWithALongStep()
    {
        // No radiation: T = S / G at equilibrium; backward Euler gets there for dt -> infinity.
        double t = ThermalModel.Step(C, 300, 100, 1e7, 1000, 2, 0, 0);
        Assert.That(t, Is.EqualTo(500).Within(0.01));
    }

    [Test]
    public void Step_CoolsFiveTimesFaster()
    {
        // P < 0: s = 5, k = C / (dt·s) = 20; T' = k·T / (k + G).
        double t = ThermalModel.Step(C, 1000, 100, 1, 0, 1, 0, 0);
        Assert.That(t, Is.EqualTo(20.0 * 1000 / 21).Within(1e-6));
        // P > 0: s = 1, k = 100; T' = (k·T + S) / (k + G).
        double heated = ThermalModel.Step(C, 0, 100, 1, 1000, 1, 0, 0);
        Assert.That(heated, Is.EqualTo(1000.0 / 101).Within(1e-6));
    }

    [Test]
    public void Step_WithRadiationStaysBetweenTheBounds()
    {
        double rad = ThermalModel.RadiationCoefficient(C, 0.4, 5);
        double t = ThermalModel.Step(C, 300, 50, 0.5, 20000, 0, 0, rad);
        Assert.That(t, Is.GreaterThan(300));
        Assert.That(t, Is.LessThan(300 + 20000 * 0.5 / 50 + 1e-9));
    }
}

public class HeatingPredictorTests
{
    internal const double Mu = ReentryPredictorTests.Mu;
    internal const double R = ReentryPredictorTests.R;
    internal const double Depth = ReentryPredictorTests.Depth;
    internal const double UT = 1000;

    internal static Atmosphere Air()
    {
        Atmosphere air = ReentryPredictorTests.Air();
        air.Temperature = new double[air.Density.Length];
        for (int i = 0; i < air.Temperature.Length; i++)
            air.Temperature[i] = 250;
        return air;
    }

    internal static ThermalPart Engine(double maxTemp = 2000, double mass = 1.5, double area = 1.2) => new ThermalPart
    {
        Name = "engine",
        ThermalMass = mass * 500,
        MaxTemp = maxTemp,
        Temperature = 290,
        AirstreamArea = new DragTable { Mach = new double[] { 0 }, CdA = new double[] { area } },
        DragCoefficient = new DragTable { Mach = new double[] { 0 }, CdA = new double[] { 1 } },
        WettedArea = 6,
        Emissivity = 0.4,
    };

    internal static DescentVehicle Stage(params ThermalPart[] parts) => new DescentVehicle
    {
        MassT = 8,
        Drag = new DragTable { Mach = new double[] { 0 }, CdA = new double[] { 2 } },
        DragScale = 1,
        Parts = parts,
        Constants = ThermalConstants.Defaults,
        Harshness = 1,
    };

    internal static void Circular(double altitude, out Vector3d r, out Vector3d v)
    {
        double rr = R + altitude;
        r = new Vector3d(rr, 0, 0);
        v = new Vector3d(0, Math.Sqrt(Mu / rr), 0);
    }

    // 100 km apoapsis, periapsis at the given altitude: start at the apoapsis.
    internal static void Entry(double peAltitude, out Vector3d r, out Vector3d v)
    {
        double ra = R + 100000, rp = R + peAltitude;
        r = new Vector3d(ra, 0, 0);
        v = new Vector3d(0, Math.Sqrt(2 * Mu * rp / (ra * (ra + rp))), 0);
    }

    [Test]
    public void Predict_WithoutParts_ReachesTheGround()
    {
        Entry(30000, out Vector3d r, out Vector3d v);
        HeatPrediction p = HeatingPredictor.Predict(r, v, UT, ReentryPredictorTests.Kerbin(true), Air(), Stage(), null);
        Assert.That(p.Ok, Is.True, p.Reason);
        Assert.That(p.PeakRatio, Is.EqualTo(0));
        Assert.That(p.TouchdownSpeed, Is.LessThan(400), "slowed by the drag");
        Assert.That(p.Samples.Count, Is.GreaterThan(10));
        Assert.That(p.Samples[p.Samples.Count - 1].Altitude, Is.EqualTo(0));
        Assert.That(p.InterfaceUT, Is.GreaterThan(UT));
    }

    [Test]
    public void Predict_HypersonicEntryHeatsTheEngine()
    {
        Entry(30000, out Vector3d r, out Vector3d v);
        HeatPrediction p = HeatingPredictor.Predict(r, v, UT, ReentryPredictorTests.Kerbin(true), Air(), Stage(Engine()), null);
        Assert.That(p.Ok, Is.True, p.Reason);
        Assert.That(p.PeakRatio, Is.GreaterThan(290 / 2000.0 + 0.05));
        Assert.That(p.PeakPart, Is.EqualTo("engine"));
        Assert.That(p.PeakFlux, Is.GreaterThan(0));
        Assert.That(p.PeakAltitude, Is.LessThan(Depth));
    }

    [Test]
    public void Predict_ABiggerAreaHeatsMore()
    {
        Entry(30000, out Vector3d r, out Vector3d v);
        HeatPrediction small = HeatingPredictor.Predict(r, v, UT, ReentryPredictorTests.Kerbin(true), Air(), Stage(Engine(area: 0.5)), null);
        HeatPrediction big = HeatingPredictor.Predict(r, v, UT, ReentryPredictorTests.Kerbin(true), Air(), Stage(Engine(area: 2)), null);
        Assert.That(big.PeakRatio, Is.GreaterThan(small.PeakRatio));
    }

    [Test]
    public void Predict_SubsonicDescentDoesNotHeat()
    {
        // 10 km up, 150 m/s down: no reentry flux below Mach 1, the part only cools toward the air.
        var r = new Vector3d(R + 10000, 0, 0);
        Vector3d omega = ReentryPredictorTests.Kerbin(true).Omega;
        Vector3d v = Vector3d.Cross(omega, r) + new Vector3d(-150, 0, 0);
        ThermalPart hot = Engine();
        hot.Temperature = 900;
        HeatPrediction p = HeatingPredictor.Predict(r, v, UT, ReentryPredictorTests.Kerbin(true), Air(), Stage(hot), null);
        Assert.That(p.Ok, Is.True, p.Reason);
        Assert.That(p.StartsInAir, Is.True);
        Assert.That(p.PeakRatio, Is.EqualTo(900 / 2000.0).Within(1e-9), "the start is the peak");
        Assert.That(p.PeakFlux, Is.EqualTo(0));
    }

    [Test]
    public void Predict_AnOrbitAboveTheAtmosphereIsRefused()
    {
        Circular(100000, out Vector3d r, out Vector3d v);
        HeatPrediction p = HeatingPredictor.Predict(r, v, UT, ReentryPredictorTests.Kerbin(true), Air(), Stage(), null);
        Assert.That(p.Ok, Is.False);
    }
}

public class AeroBrakeTests
{
    static Vehicle Engines(double thrustKN, double massT = 8) => new Vehicle { ThrustKN = thrustKN, MassT = massT, Isp = 300 };

    [Test]
    public void Find_BrakesLowAndCheaperThanFromOrbit()
    {
        HeatingPredictorTests.Entry(30000, out Vector3d r, out Vector3d v);
        Body kerbin = ReentryPredictorTests.Kerbin(true);
        HeatPrediction p = HeatingPredictor.Predict(r, v, HeatingPredictorTests.UT, kerbin, HeatingPredictorTests.Air(), HeatingPredictorTests.Stage(), null);
        Assert.That(p.Ok, Is.True, p.Reason);
        AeroBrakeResult b = AeroBrake.Find(p.Samples, kerbin, HeatingPredictorTests.UT, Engines(200));
        Assert.That(b.Ok, Is.True, b.Reason);
        Assert.That(b.DeltaV, Is.GreaterThan(0));
        Assert.That(b.DeltaV, Is.LessThan(0.8 * v.magnitude), "the air did part of the work");
        Assert.That(b.SampleAltitude, Is.LessThan(30000));
        Assert.That(b.StartUT, Is.GreaterThanOrEqualTo(p.Samples[b.SampleIndex].UT - 1e-6));
    }

    [Test]
    public void Find_TooWeakAnEngineCannotStop()
    {
        HeatingPredictorTests.Entry(30000, out Vector3d r, out Vector3d v);
        Body kerbin = ReentryPredictorTests.Kerbin(true);
        HeatPrediction p = HeatingPredictor.Predict(r, v, HeatingPredictorTests.UT, kerbin, HeatingPredictorTests.Air(), HeatingPredictorTests.Stage(), null);
        AeroBrakeResult b = AeroBrake.Find(p.Samples, kerbin, HeatingPredictorTests.UT, Engines(20));
        Assert.That(b.Ok, Is.False);
    }
}

public class DescentPlannerTests
{
    const double MunMu = 6.5138398e10;
    const double MunR = 200000;
    const double UT = 1000;

    static Body Mun => new Body
    {
        Mu = MunMu,
        Radius = MunR,
        Omega = BodyRotation.AngularVelocity(BodyRotation.AngularSpeed(138984.38), 1),
        TerrainHeight = p => 0,
    };

    static Vehicle Lander(double thrust = 60, double mass = 4) => new Vehicle { ThrustKN = thrust, MassT = mass, Isp = 320 };

    static DescentInput Airless(Vector3d r, Vector3d v) => new DescentInput
    {
        R = r, V = v, UT = UT, Body = Mun, Vehicle = Lander(), EarliestBurnUT = UT + 60,
    };

    [Test]
    public void Airless_FromOrbit_DeorbitsThenBrakes()
    {
        double rr = MunR + 30000;
        var r = new Vector3d(rr, 0, 0);
        var v = new Vector3d(0, Math.Sqrt(MunMu / rr), 0);
        DescentPlan plan = DescentPlanner.Plan(Airless(r, v));
        Assert.That(plan.Ok, Is.True, plan.Reason);
        Assert.That(plan.NeedsDeorbit, Is.True);
        Assert.That(plan.DeorbitDeltaV, Is.InRange(5, 40), "a periapsis 2 km under the datum from 30 km");
        Assert.That(plan.Deorbit.UT, Is.EqualTo(UT + 60).Within(1e-6));
        Assert.That(plan.BrakeDeltaV, Is.InRange(400, 800));
        Assert.That(plan.TotalDeltaV, Is.EqualTo(plan.DeorbitDeltaV + plan.BrakeDeltaV).Within(1e-9));
        Assert.That(plan.NeedsPreBrake, Is.False);
    }

    [Test]
    public void Airless_OnAnImpactCourse_NeedsNoDeorbit()
    {
        double ra = MunR + 30000, rp = MunR - 10000;
        var r = new Vector3d(ra, 0, 0);
        var v = new Vector3d(0, Math.Sqrt(2 * MunMu * rp / (ra * (ra + rp))), 0);
        DescentPlan plan = DescentPlanner.Plan(Airless(r, v));
        Assert.That(plan.Ok, Is.True, plan.Reason);
        Assert.That(plan.NeedsDeorbit, Is.False);
        Assert.That(plan.DeorbitDeltaV, Is.EqualTo(0));
    }

    [Test]
    public void Airless_EscapeIsRefused()
    {
        var r = new Vector3d(MunR + 30000, 0, 0);
        var v = new Vector3d(0, 2 * Math.Sqrt(MunMu / (MunR + 30000)), 0);
        DescentPlan plan = DescentPlanner.Plan(Airless(r, v));
        Assert.That(plan.Ok, Is.False);
    }

    static DescentInput Kerbin(DescentVehicle aero, double thrust = 200)
    {
        HeatingPredictorTests.Circular(100000, out Vector3d r, out Vector3d v);
        return new DescentInput
        {
            R = r, V = v, UT = UT, Body = ReentryPredictorTests.Kerbin(true),
            Vehicle = new Vehicle { ThrustKN = thrust, MassT = aero.MassT, Isp = 300 },
            Atmospheric = true, Air = HeatingPredictorTests.Air(), Aero = aero, EarliestBurnUT = UT + 60,
            FixedPeriapsisRadius = double.NaN,
        };
    }

    [Test]
    public void Atmospheric_ChoosesAnEntryAndCountsEveryBurn()
    {
        DescentPlan plan = DescentPlanner.Plan(Kerbin(HeatingPredictorTests.Stage(HeatingPredictorTests.Engine())));
        Assert.That(plan.Ok, Is.True, plan.Reason + " | " + plan.Candidates);
        Assert.That(plan.NeedsDeorbit, Is.True);
        Assert.That(plan.DeorbitPeriapsisRadius - ReentryPredictorTests.R, Is.InRange(0, 0.6 * HeatingPredictorTests.Depth + 1));
        Assert.That(plan.TotalDeltaV, Is.EqualTo(plan.DeorbitDeltaV + plan.PreBrakeDeltaV + plan.BrakeDeltaV).Within(1e-9));
        Assert.That(plan.HeatPeak, Is.LessThanOrEqualTo(DescentPlanner.HeatLimit));
        Assert.That(plan.HeatPeak, Is.LessThanOrEqualTo(plan.Heat.PeakRatio), "judged up to the brake start");
        Assert.That(plan.Candidates, Does.Contain("pe 42.0 km"));
        Assert.That(plan.BrakeDeltaV, Is.LessThan(Math.Sqrt(ReentryPredictorTests.Mu / (ReentryPredictorTests.R + 100000))), "the air brakes part of the orbital speed");
        Assert.That(plan.Predictions, Is.GreaterThan(0));
    }

    [Test]
    public void Atmospheric_AFragilePartGetsAPreBrake()
    {
        DescentPlan plan = DescentPlanner.Plan(Kerbin(HeatingPredictorTests.Stage(HeatingPredictorTests.Engine(maxTemp: 1000))));
        Assert.That(plan.Ok, Is.True, plan.Reason + " | " + plan.Candidates);
        Assert.That(plan.NeedsPreBrake, Is.True, plan.Candidates);
        Assert.That(plan.PreBrakeDeltaV, Is.GreaterThan(100));
        Assert.That(plan.HeatPeak, Is.LessThanOrEqualTo(DescentPlanner.HeatLimit));
        DescentPlan sturdy = DescentPlanner.Plan(Kerbin(HeatingPredictorTests.Stage(HeatingPredictorTests.Engine(maxTemp: 2000))));
        Assert.That(sturdy.PreBrakeDeltaV, Is.LessThan(plan.PreBrakeDeltaV), "a sturdier engine needs less");
        Assert.That(plan.PreBrake.UT, Is.LessThan(plan.InterfaceUT));
        Assert.That(plan.PreBrake.UT, Is.GreaterThanOrEqualTo(plan.Deorbit.UT));
    }

    [Test]
    public void Atmospheric_TooHotIsRefused()
    {
        // Already at 97 % of its max temperature: no pre-brake can cool it under 70 %.
        ThermalPart part = HeatingPredictorTests.Engine(maxTemp: 300);
        DescentPlan plan = DescentPlanner.Plan(Kerbin(HeatingPredictorTests.Stage(part)));
        Assert.That(plan.Ok, Is.False);
        Assert.That(plan.Reason, Does.Contain("too hot"));
    }

    [Test]
    public void Atmospheric_FixedPeriapsisIsKept()
    {
        DescentInput input = Kerbin(HeatingPredictorTests.Stage());
        input.FixedPeriapsisRadius = ReentryPredictorTests.R + 30000;
        DescentPlan plan = DescentPlanner.Plan(input);
        Assert.That(plan.Ok, Is.True, plan.Reason);
        Assert.That(plan.DeorbitPeriapsisRadius, Is.EqualTo(input.FixedPeriapsisRadius));
        Assert.That(plan.HeatModelled, Is.False);
    }

    [Test]
    public void Feasibility_RefusesWithTheBreakdown()
    {
        var plan = new DescentPlan { Ok = true, DeorbitDeltaV = 120, PreBrakeDeltaV = 400, BrakeDeltaV = 500, TotalDeltaV = 1020 };
        FeasibilityResult r = DescentFeasibility.Check(plan, 20, 9.81, 850, Lander());
        Assert.That(r.Ok, Is.False);
        Assert.That(r.Error, Is.EqualTo("Δv 850 m/s for ~1122 m/s needed (deorbit 120, pre-brake 400, brake 500, +10 %)."));
        Assert.That(DescentFeasibility.Check(plan, 20, 9.81, 1200, Lander()).Ok, Is.True);
        FeasibilityResult unknown = DescentFeasibility.Check(plan, 20, 9.81, double.NaN, Lander());
        Assert.That(unknown.Ok, Is.True);
        Assert.That(unknown.Warning, Is.EqualTo(LandingFeasibility.DeltaVUnknownMessage));
    }

    [Test]
    public void Feasibility_KeepsTheEngineChecksFirst()
    {
        var plan = new DescentPlan { Ok = true, TotalDeltaV = 10 };
        Assert.That(DescentFeasibility.Check(plan, 0, 1.63, 1000, Lander()).Error, Is.EqualTo(LandingFeasibility.NoEngineMessage));
        var refused = new DescentPlan { Ok = false, Reason = "nope" };
        Assert.That(DescentFeasibility.Check(refused, 20, 1.63, 1000, Lander()).Error, Is.EqualTo("nope"));
    }

    [Test]
    public void Feasibility_StagedChecksTheActiveStageForTheDeorbit()
    {
        Vehicle staged = Lander();
        staged.Stages = new[]
        {
            new BrakeStage { Stage = 2, ThrustKN = 100, MassT = 6, Isp = 300, DeltaV = 30 },
            new BrakeStage { Stage = 1, ThrustKN = 60, MassT = 4, Isp = 320, DeltaV = 900 },
        };
        var plan = new DescentPlan { Ok = true, DeorbitDeltaV = 40, BrakeDeltaV = 600, TotalDeltaV = 640, Stop = new StopPrediction { Ok = true, LandingStageDeltaV = 500 } };
        FeasibilityResult r = DescentFeasibility.Check(plan, 15, 1.63, double.NaN, staged);
        Assert.That(r.Ok, Is.False);
        Assert.That(r.Error, Does.StartWith("Stage 1 has 30 m/s"));
        staged.Stages[0].DeltaV = 300;
        Assert.That(DescentFeasibility.Check(plan, 15, 1.63, double.NaN, staged).Ok, Is.True);
        plan.Stop.LandingStageDeltaV = 800;
        Assert.That(DescentFeasibility.Check(plan, 15, 1.63, double.NaN, staged).Ok, Is.False, "the landing stage lacks its 20 %");
    }
}

public class HeatGuardTests
{
    [Test]
    public void StartsAt75PercentAboveMach1Only()
    {
        var g = new HeatGuard();
        Assert.That(g.Update(0.74, 5, 0, true), Is.EqualTo(HeatGuardEvent.None));
        Assert.That(g.Update(0.80, 0.9, 0, true), Is.EqualTo(HeatGuardEvent.None), "no flux below Mach 1");
        Assert.That(g.Active, Is.False);
        Assert.That(g.Update(0.75, 3, 0, true), Is.EqualTo(HeatGuardEvent.Started));
        Assert.That(g.Active, Is.True);
        Assert.That(g.Throttle, Is.EqualTo(HeatGuard.MinThrottle).Within(1e-6));
    }

    [Test]
    public void StopsUnder70PercentOrBelowMach1()
    {
        var g = new HeatGuard();
        g.Update(0.8, 3, 0, true);
        Assert.That(g.Update(0.72, 3, 0, true), Is.EqualTo(HeatGuardEvent.None), "hysteresis");
        Assert.That(g.Update(0.69, 3, 0, true), Is.EqualTo(HeatGuardEvent.Stopped));
        Assert.That(g.Throttle, Is.EqualTo(0));
        g.Update(0.8, 3, 0, true);
        Assert.That(g.Update(0.8, 0.95, 0, true), Is.EqualTo(HeatGuardEvent.Stopped));
    }

    [Test]
    public void ThrottleRisesToFullAt85Percent()
    {
        Assert.That(HeatGuard.ThrottleFor(0.75), Is.EqualTo(0.25).Within(1e-9));
        Assert.That(HeatGuard.ThrottleFor(0.80), Is.EqualTo(0.625).Within(1e-9));
        Assert.That(HeatGuard.ThrottleFor(0.85), Is.EqualTo(1));
        Assert.That(HeatGuard.ThrottleFor(0.95), Is.EqualTo(1));
    }

    [Test]
    public void NoThrustUntilAligned()
    {
        var g = new HeatGuard();
        g.Update(0.8, 3, 40, true);
        Assert.That(g.Active, Is.True);
        Assert.That(g.Throttle, Is.EqualTo(0));
        g.Update(0.8, 3, 10, true);
        Assert.That(g.Throttle, Is.GreaterThan(0));
    }

    [Test]
    public void AlarmAbove90PercentAtFullThrottleOrWithoutThrust()
    {
        var g = new HeatGuard();
        g.Update(0.88, 3, 0, true);
        Assert.That(g.Overheating, Is.False);
        g.Update(0.92, 3, 0, true);
        Assert.That(g.Overheating, Is.True);
        Assert.That(g.PeakRatio, Is.EqualTo(0.92));
        var dry = new HeatGuard();
        dry.Update(0.8, 3, 0, false);
        Assert.That(dry.NoThrust, Is.True);
        Assert.That(dry.Throttle, Is.EqualTo(0));
        Assert.That(HeatGuard.OverheatMessage("engine", 0.92), Is.EqualTo("Overheating: engine at 92 % of its max temperature, cannot slow down enough."));
    }
}

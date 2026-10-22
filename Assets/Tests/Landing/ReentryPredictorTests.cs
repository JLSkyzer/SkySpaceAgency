using System;
using System.Diagnostics;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

public class ReentryPredictorTests
{
    // Synthetic Kerbin: exponential atmosphere, constant sound speed, ideal-gas pressure.
    const double Mu = 3.5316e12;
    const double R = 600000;
    const double RotationPeriod = 21549.425;
    const double Rho0 = 1.225;
    const double ScaleHeight = 5600;
    const double Depth = 70000;
    const double UT = 1000;
    static double SurfaceGravity => Mu / (R * R);

    static Vector3d Zero => new Vector3d(0, 0, 0);
    static Vector3d OmegaOf(bool rotating) =>
        rotating ? BodyRotation.AngularVelocity(BodyRotation.AngularSpeed(RotationPeriod), 1) : Zero;

    static Body Kerbin(bool rotating) => new Body
    {
        Mu = Mu,
        Radius = R,
        Omega = OmegaOf(rotating),
        TerrainHeight = p => 0,
    };

    static Atmosphere Air(double rho0 = Rho0, double depth = Depth)
    {
        const double step = 100;
        int n = (int)(depth / step) + 1;
        var air = new Atmosphere
        {
            Depth = depth,
            SampleStep = step,
            Density = new double[n],
            Pressure = new double[n],
            SoundSpeed = new double[n],
        };
        for (int i = 0; i < n; i++)
        {
            double rho = rho0 * Math.Exp(-i * step / ScaleHeight);
            air.Density[i] = rho;
            air.Pressure[i] = rho * 287 * 288;
            air.SoundSpeed[i] = 340;
        }
        return air;
    }

    const double ChuteArea = 25;

    // 1 t capsule, CdA 1.5 m², one chute of 25 m² (semi-deployed: 10 %).
    static ReentryVehicle Capsule(int chutes = 1, double maxSpeed = 250, double minSpeed = 0, double deployAltitude = 1000) => new ReentryVehicle
    {
        MassT = 1,
        Drag = new DragTable { Mach = new double[] { 0 }, CdA = new double[] { 1.5 } },
        DragScale = 1,
        Chutes = new Chutes
        {
            Count = chutes,
            AreaDeployed = ChuteArea,
            SemiAreaFraction = 0.1,
            MinPressureToOpen = 1000,   // Pa, as the synthetic Atmosphere.Pressure
            DeployAltitude = deployAltitude,
            MinDeploySpeed = minSpeed,
            MaxDeploySpeed = maxSpeed,
            SemiToFullDelay = 4,
        },
    };

    // 100 × 30 km equatorial prograde (eastward) descent, starting at the apoapsis on +x.
    static readonly double Ra = R + 100000;
    static readonly double Rp = R + 30000;
    static Vector3d R0 => new Vector3d(Ra, 0, 0);
    static Vector3d V0 => new Vector3d(0, Math.Sqrt(2 * Mu * Rp / (Ra * (Ra + Rp))), 0);

    static double Longitude(Vector3d p) => Math.Atan2(p.y, p.x);

    static StopPrediction Predict(bool rotating, Atmosphere air, ReentryVehicle vehicle, out ReentryTrace trace, out double ms)
    {
        var watch = Stopwatch.StartNew();
        StopPrediction p = ReentryPredictor.Predict(R0, V0, UT, Kerbin(rotating), air, vehicle, null, out trace);
        ms = watch.Elapsed.TotalMilliseconds;
        return p;
    }

    static void AssertFinite(StopPrediction p)
    {
        Assert.IsFalse(double.IsNaN(p.StopUT) || double.IsInfinity(p.StopUT), "StopUT " + p.StopUT);
        Assert.IsFalse(double.IsNaN(p.StopDirNow.x) || double.IsNaN(p.StopDirNow.y) || double.IsNaN(p.StopDirNow.z), "StopDirNow NaN");
        Assert.AreEqual(1, p.StopDirNow.magnitude, 1e-9);
    }

    [Test]
    public void UnderCanopy_TouchesDownAtTheCanopyTerminalSpeed()
    {
        StopPrediction p = Predict(true, Air(), Capsule(), out ReentryTrace trace, out double ms);
        double terminal = Math.Sqrt(2 * 1000 * SurfaceGravity / (Rho0 * ChuteArea));
        TestContext.Out.WriteLine($"canopy: ok {p.Ok} ({p.Reason}), {trace}, terminal {terminal:F1} m/s, {ms:F1} ms");
        Assert.IsTrue(p.Ok, p.Reason);
        AssertFinite(p);
        Assert.IsFalse(double.IsNaN(trace.SemiUT));
        Assert.IsFalse(double.IsNaN(trace.FullUT));
        Assert.Less(trace.SemiUT, trace.FullUT);
        Assert.Less(trace.FullUT, p.StopUT);
        Assert.AreEqual(terminal, -trace.TouchdownVerticalSpeed, 0.1 * terminal);
    }

    [Test]
    public void WithoutChutes_LandsFasterAndFarther()
    {
        StopPrediction chute = Predict(true, Air(), Capsule(), out ReentryTrace withChute, out _);
        StopPrediction none = Predict(true, Air(), Capsule(chutes: 0), out ReentryTrace noChute, out _);
        Vector3d start = R0 / R0.magnitude;
        double chuteRange = SurfaceGeometry.GreatCircle(start, chute.StopDirNow, R);
        double noneRange = SurfaceGeometry.GreatCircle(start, none.StopDirNow, R);
        TestContext.Out.WriteLine($"chute: range {chuteRange / 1000:F2} km, {withChute}");
        TestContext.Out.WriteLine($"none: ok {none.Ok} ({none.Reason}), range {noneRange / 1000:F2} km, {noChute}");
        Assert.IsTrue(chute.Ok, chute.Reason);
        Assert.IsTrue(none.Ok, none.Reason);
        AssertFinite(none);
        Assert.IsTrue(double.IsNaN(noChute.SemiUT));
        Assert.Greater(noChute.TouchdownSpeed, 2 * withChute.TouchdownSpeed);
        Assert.Greater(-noChute.TouchdownVerticalSpeed, 2 * -withChute.TouchdownVerticalSpeed);
        Assert.Greater(noneRange, chuteRange);
        Assert.Less(none.StopUT, chute.StopUT);
    }

    [Test]
    public void Rotation_ShiftsTheStopPointEastByTheCoRotationDrift()
    {
        StopPrediction still = Predict(false, Air(), Capsule(), out _, out _);
        StopPrediction turning = Predict(true, Air(), Capsule(), out ReentryTrace trace, out _);
        Assert.IsTrue(still.Ok, still.Reason);
        Assert.IsTrue(turning.Ok, turning.Reason);

        // Inertial stop point: the body-fixed StopDirNow carried to the stop UT.
        Vector3d inertial = SurfaceGeometry.AtTime(turning.StopDirNow, OmegaOf(true), turning.StopUT - UT);
        double eastShift = R * (Longitude(inertial) - Longitude(still.StopDirNow));
        double drift = OmegaOf(true).z * R * (turning.StopUT - trace.InterfaceUT);
        double bodyFixedShift = R * (Longitude(turning.StopDirNow) - Longitude(still.StopDirNow));
        TestContext.Out.WriteLine($"rotation: inertial east shift {eastShift / 1000:F1} km, co-rotation drift {drift / 1000:F1} km, body-fixed shift {bodyFixedShift / 1000:F1} km, ratio {eastShift / drift:F2}");
        Assert.Greater(eastShift, 0.25 * drift);
        Assert.Less(eastShift, 1.5 * drift);
        // The surface turns east under the capsule: in the body-fixed frame it lands west.
        Assert.Less(bodyFixedShift, 0);
    }

    // Independent ballistic impact on the flat body: first point of the coast below R.
    static void BallisticImpact(Vector3d r, Vector3d v, out Vector3d impact, out double impactUT)
    {
        for (double dt = 0.05; dt < 20000; dt += 0.05)
        {
            KeplerPropagator.Propagate(r, v, Mu, dt, out var ri, out _);
            if (ri.magnitude <= R)
            {
                impact = ri;
                impactUT = UT + dt;
                return;
            }
        }
        throw new Exception("no impact");
    }

    [Test]
    public void NoAir_BehavesAsABallisticImpact()
    {
        // Without air the 100 × 30 km orbit would not land: periapsis 100 km under the surface.
        double rp = R - 100000;
        var v0 = new Vector3d(0, Math.Sqrt(2 * Mu * rp / (Ra * (Ra + rp))), 0);
        var watch = Stopwatch.StartNew();
        StopPrediction p = ReentryPredictor.Predict(R0, v0, UT, Kerbin(true), Air(rho0: 0), Capsule(chutes: 0), null, out ReentryTrace trace);
        double ms = watch.Elapsed.TotalMilliseconds;
        BallisticImpact(R0, v0, out Vector3d impact, out double impactUT);
        Vector3d expected = SurfaceGeometry.AtTime(impact, OmegaOf(true), -(impactUT - UT));
        double miss = SurfaceGeometry.GreatCircle(expected / expected.magnitude, p.StopDirNow, R);
        TestContext.Out.WriteLine($"no air: ok {p.Ok} ({p.Reason}), stop {p.StopUT - impactUT:F2} s vs Kepler, miss {miss:F1} m, {trace}, {ms:F1} ms");
        Assert.IsTrue(p.Ok, p.Reason);
        AssertFinite(p);
        Assert.AreEqual(impactUT, p.StopUT, 0.5);
        Assert.Less(miss, 100);

        // With chutes in a vacuum: they never open, said so, and still no NaN.
        StopPrediction chutes = ReentryPredictor.Predict(R0, v0, UT, Kerbin(true), Air(rho0: 0), Capsule(), null);
        Assert.IsFalse(chutes.Ok);
        StringAssert.Contains("never open", chutes.Reason);
        AssertFinite(chutes);
    }

    [Test]
    public void ChuteThatNeverOpens_ReasonSaysSo()
    {
        StopPrediction p = Predict(true, Air(), Capsule(maxSpeed: 1), out ReentryTrace trace, out _);
        TestContext.Out.WriteLine($"never opens (max 1 m/s): ok {p.Ok} ({p.Reason}), {trace}");
        Assert.IsFalse(p.Ok);
        StringAssert.Contains("never open", p.Reason);
        Assert.IsTrue(double.IsNaN(trace.SemiUT));
        AssertFinite(p);
    }

    [Test]
    public void OrbitInsideAnEmptyAtmosphere_HitsTheTimeCap()
    {
        // A circular 100 km orbit inside a 200 km "atmosphere" of zero density never lands.
        double r = R + 100000;
        var air = Air(rho0: 0, depth: 200000);
        var watch = Stopwatch.StartNew();
        StopPrediction p = ReentryPredictor.Predict(new Vector3d(r, 0, 0), new Vector3d(0, Math.Sqrt(Mu / r), 0), UT,
            Kerbin(true), air, Capsule(chutes: 0), null, out ReentryTrace trace);
        double ms = watch.Elapsed.TotalMilliseconds;
        TestContext.Out.WriteLine($"cap: ok {p.Ok} ({p.Reason}), {trace.Steps} steps, {ms:F1} ms");
        Assert.IsFalse(p.Ok);
        StringAssert.Contains("4 h", p.Reason);
    }

    [Test]
    public void Impulse_MatchesTheManualBurnAndLightensTheCapsule()
    {
        // Circular 100 km orbit, retrograde burn 600 s later down to a 30 km periapsis.
        double r = R + 100000;
        var r0 = new Vector3d(r, 0, 0);
        var v0 = new Vector3d(0, Math.Sqrt(Mu / r), 0);
        double burnUT = UT + 600;
        KeplerPropagator.Propagate(r0, v0, Mu, 600, out var rb, out var vb);
        double va = Math.Sqrt(2 * Mu * Rp / (r * (r + Rp)));
        Vector3d dv = vb / vb.magnitude * (va - vb.magnitude);
        var burn = new Impulse { UT = burnUT, DeltaV = dv };

        ReentryVehicle heavy = Capsule();
        heavy.MassT = 2;
        StopPrediction viaImpulse = ReentryPredictor.Predict(r0, v0, UT, Kerbin(true), Air(), heavy, burn, out ReentryTrace t1);
        StopPrediction manual = ReentryPredictor.Predict(rb, vb + dv, burnUT, Kerbin(true), Air(), heavy, null, out _);
        // Same stop, expressed at different prediction UTs: carry manual's to UT.
        Vector3d manualAtUT = SurfaceGeometry.AtTime(manual.StopDirNow, OmegaOf(true), -(burnUT - UT));
        double gap = SurfaceGeometry.GreatCircle(viaImpulse.StopDirNow, manualAtUT, R);

        // Isp 100 s: the 2 t capsule burns some of its mass, so it comes down slower.
        StopPrediction lighter = ReentryPredictor.Predict(r0, v0, UT, Kerbin(true), Air(), heavy, burn, out ReentryTrace t2, 100);
        TestContext.Out.WriteLine($"impulse: gap {gap:F2} m, |dv| {dv.magnitude:F1} m/s, touchdown {t1.TouchdownVerticalSpeed:F2} -> {t2.TouchdownVerticalSpeed:F2} m/s with Isp 100");
        Assert.IsTrue(viaImpulse.Ok, viaImpulse.Reason);
        Assert.IsTrue(manual.Ok, manual.Reason);
        Assert.Less(gap, 1);
        Assert.AreEqual(viaImpulse.StopUT, manual.StopUT, 1e-6);
        Assert.Less(-t2.TouchdownVerticalSpeed, -t1.TouchdownVerticalSpeed - 0.1);

        Assert.IsFalse(ReentryPredictor.Predict(r0, v0, UT, Kerbin(true), Air(), heavy, new Impulse { UT = UT - 10, DeltaV = dv }).Ok);
    }

    [Test]
    public void OnePredict_StaysUnder50ms()
    {
        Predict(true, Air(), Capsule(), out _, out _);   // JIT warm-up
        double worst = 0, total = 0;
        const int runs = 10;
        for (int i = 0; i < runs; i++)
        {
            Predict(true, Air(), Capsule(), out ReentryTrace trace, out double ms);
            worst = Math.Max(worst, ms);
            total += ms;
            if (i == 0)
                TestContext.Out.WriteLine($"timing: {trace.Steps} steps");
        }
        TestContext.Out.WriteLine($"timing: mean {total / runs:F2} ms, worst {worst:F2} ms");
        Assert.Less(worst, 50);
    }

    [Test]
    public void MinDeploySpeedAboveTheSpeedInTheAir_NeverOpens()
    {
        // Above any air speed of this reentry (about 2.3 km/s at the interface), no upper bound.
        StopPrediction p = Predict(true, Air(), Capsule(maxSpeed: 0, minSpeed: 3000), out ReentryTrace trace, out _);
        TestContext.Out.WriteLine($"never opens (min 3000 m/s): ok {p.Ok} ({p.Reason}), {trace}");
        Assert.IsFalse(p.Ok);
        StringAssert.Contains("never open", p.Reason);
        Assert.IsTrue(double.IsNaN(trace.SemiUT));
        AssertFinite(p);

        // A lower bound under the opening speed changes nothing.
        StopPrediction ok = Predict(true, Air(), Capsule(minSpeed: 50), out ReentryTrace okTrace, out _);
        Assert.IsTrue(ok.Ok, ok.Reason);
        Assert.Greater(okTrace.SemiAirSpeed, 50);
    }

    [Test]
    public void DeployAltitudeTooLowForTheDelay_NeverFullyOpens()
    {
        // Semi at about 63 m/s: the 4 s delay needs ~250 m, more than the 100 m deploy altitude.
        StopPrediction p = Predict(true, Air(), Capsule(deployAltitude: 100), out ReentryTrace trace, out _);
        TestContext.Out.WriteLine($"never full: ok {p.Ok} ({p.Reason}), {trace}");
        Assert.IsFalse(p.Ok);
        StringAssert.Contains("never fully open", p.Reason);
        Assert.IsFalse(double.IsNaN(trace.SemiUT));
        Assert.IsTrue(double.IsNaN(trace.FullUT));
        AssertFinite(p);
    }

    [Test]
    public void DragTable_InterpolatesMachAndMoreSupersonicDragLandsShorter()
    {
        var table = new DragTable { Mach = new double[] { 0, 1, 3 }, CdA = new double[] { 1.5, 1.5, 3.0 } };
        Assert.AreEqual(1.5, table.At(-1), 1e-12);
        Assert.AreEqual(1.5, table.At(0.5), 1e-12);
        Assert.AreEqual(2.25, table.At(2), 1e-12);
        Assert.AreEqual(3.0, table.At(5), 1e-12);

        StopPrediction flat = Predict(true, Air(), Capsule(chutes: 0), out _, out _);
        ReentryVehicle draggy = Capsule(chutes: 0);
        draggy.Drag = table;
        StopPrediction mach = Predict(true, Air(), draggy, out ReentryTrace trace, out _);
        Vector3d start = R0 / R0.magnitude;
        double flatRange = SurfaceGeometry.GreatCircle(start, flat.StopDirNow, R);
        double machRange = SurfaceGeometry.GreatCircle(start, mach.StopDirNow, R);
        TestContext.Out.WriteLine($"mach table: range {flatRange / 1000:F2} -> {machRange / 1000:F2} km, peak {trace.PeakDecel / 9.80665:F1} g");
        Assert.IsTrue(mach.Ok, mach.Reason);
        Assert.Less(machRange, flatRange - 1000);
    }
}

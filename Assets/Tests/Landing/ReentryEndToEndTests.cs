using System;
using System.Diagnostics;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// The parachute precision return on the synthetic Kerbin of ReentryPredictorTests, burns
// impulsive: plane change (if needed) → deorbit re-planned from the new orbit → coast → one
// correction halfway to the atmosphere interface → service module jettison above the interface →
// reentry under the chute to the ground.
//
// The pre-jettison vessel (service module + capsule) only flies above the atmosphere, where the
// coast is Kepler whatever the vehicle: the predictor used by the searches (WithJettison) predicts
// the capsule alone (mass and drag) from the jettison on, and refuses a trajectory that leaves no
// JettisonLead between the last burn and the interface. The capsule's mass does not change with
// the burns (the propellant is in the service module), so the impulses carry no Isp.
//
// The "truth" applies each burn impulsively at its planned time and propagates with
// KeplerPropagator between them, switches to the capsule at the planned jettison UT, and then
// integrates the reentry with ReentryPredictor itself (there is no second atmosphere
// integrator). It differs from the plan by a deliberate deorbit execution error (DeorbitError),
// which the correction has to remove.
public class ReentryEndToEndTests
{
    const double Mu = ReentryPredictorTests.Mu;
    const double R = ReentryPredictorTests.R;
    const double UT = ReentryPredictorTests.UT;
    const double Deg = Math.PI / 180;
    const double G0 = 9.80665;

    // Vehicle: 4 t service module + 1.2 t capsule, 60 kN, Isp 300.
    const double ServiceModuleT = 4, CapsuleT = 1.2, ThrustKN = 60, Isp = 300;
    const double MaxCorrection = 30;       // m/s
    const double DeorbitError = 1.01;      // the deorbit burn ends 1 % long
    const double JettisonLead = 120;       // s between the jettison and the interface
    const double MaxDistance = 5000;       // m

    static Vehicle Stack(double massT) => new Vehicle { MassT = massT, ThrustKN = ThrustKN, Isp = Isp };
    static double AfterBurn(double massT, double dv) => massT * Math.Exp(-dv / (Isp * G0));

    static ReentryVehicle Capsule => new ReentryVehicle
    {
        MassT = CapsuleT,
        Drag = new DragTable { Mach = new double[] { 0 }, CdA = new double[] { 1.8 } },
        DragScale = 1,
        Chutes = new Chutes
        {
            Count = 1,
            AreaDeployed = 30,
            SemiAreaFraction = 0.1,
            MinPressureToOpen = 1000,   // Pa, as the synthetic Atmosphere.Pressure
            DeployAltitude = 1000,
            MaxDeploySpeed = 250,
            SemiToFullDelay = 4,
        },
    };

    static Body Kerbin(int sign) => new Body
    {
        Mu = Mu,
        Radius = R,
        Omega = BodyRotation.AngularVelocity(BodyRotation.AngularSpeed(ReentryPredictorTests.RotationPeriod), sign),
        TerrainHeight = p => 0,
    };

    /// <summary>The searches' predictor: the capsule from the jettison (JettisonLead before the
    /// interface) on; the Kepler coast before it is the same for the whole stack.</summary>
    static StopPredictorFn WithJettison(Body body, Atmosphere air, ReentryVehicle capsule) =>
        (r, v, ut, impulse) =>
        {
            StopPrediction p = ReentryPredictor.Predict(r, v, ut, body, air, capsule, impulse, out ReentryTrace trace);
            if (!p.Ok)
                return p;
            double lastBurn = impulse.HasValue ? Math.Max(impulse.Value.UT, ut) : ut;
            if (!(trace.InterfaceUT - JettisonLead >= lastBurn))
                return new StopPrediction
                {
                    Ok = false,
                    Reason = $"No time to jettison the service module: the interface is {trace.InterfaceUT - lastBurn:F0} s after the burn",
                };
            return p;
        };

    [TestCase(1)]
    [TestCase(-1)]
    public void InclinedOrbitToTheKsc_LandsUnderTheChuteWithin5km(int sign)
    {
        var watch = Stopwatch.StartNew();
        var log = new System.Text.StringBuilder();
        log.AppendLine($"rotation sign {sign}");
        Body body = Kerbin(sign);
        Atmosphere air = ReentryPredictorTests.Air();
        StopPredictorFn predict = WithJettison(body, air, Capsule);
        double descentRp = DeorbitSearch.ReentryPeriapsis(body, air.Depth);

        // 1. Circular 100 km orbit inclined 3°, the vessel at the ascending node (lat 0, lon 0);
        //    the KSC-like target at lat −0.10, lon −74.56.
        double r0m = R + 100000;
        var r0 = new Vector3d(r0m, 0, 0);
        var v0 = Math.Sqrt(Mu / r0m) * new Vector3d(0, Math.Cos(3 * Deg), Math.Sin(3 * Deg));
        var reference = new SurfaceGeometry.Reference { Position = r0, Latitude = 0, Longitude = 0, LonSign = 1 };
        Vector3d target0 = SurfaceGeometry.Direction(reference, -0.10, -74.56);
        Vector3d TargetAt(double t) => SurfaceGeometry.AtTime(target0, body.Omega, t - UT);
        double mass = ServiceModuleT + CapsuleT;

        // 2. Plane change, applied as an instant rotation of the velocity at its node.
        PlaneChangePlan pc = PlaneChange.Plan(r0, v0, UT, body, Stack(mass), target0, predict, descentRp);
        log.AppendLine($"plane change: needed {pc.Needed} ok {pc.Ok} ({pc.Reason}), node +{pc.BurnUT - UT:F1} s, Δv {pc.DeltaV:F2} m/s, "
            + $"incl {pc.Target.InclinationRad / Deg:F3}°, predicted deorbit along {pc.Deorbit.AlongMissM:F1} m cross {pc.Deorbit.CrossMissM:F1} m, "
            + $"{pc.Iterations} iteration(s), {pc.PredictCalls} Predict calls");
        Assert.IsTrue(pc.Ok, log.ToString());
        Vector3d rb = r0, vb = v0;
        double tb = UT, earliest = UT, planeChangeDv = 0;
        if (pc.Needed)
        {
            KeplerPropagator.Propagate(r0, v0, Mu, pc.BurnUT - UT, out rb, out Vector3d vOld);
            Vector3d axis = rb.normalized;
            Vector3d n1 = Vector3d.Cross(rb, vOld).normalized;
            Vector3d n2 = pc.Target.PlaneNormal.normalized;
            vb = OrbitMath.Rotate(vOld, axis, Math.Atan2(Vector3d.Dot(Vector3d.Cross(n1, n2), axis), Vector3d.Dot(n1, n2)));
            tb = pc.BurnUT;
            planeChangeDv = (vb - vOld).magnitude;
            earliest = PlaneChange.EarliestDeorbitUT(pc.BurnUT, pc.DeltaV, Stack(mass));
            mass = AfterBurn(mass, planeChangeDv);
        }

        // 3. Deorbit re-planned from the orbit reached, then flown 1 % long.
        DeorbitPlan deorbit = DeorbitSearch.Find(rb, vb, tb, body, TargetAt(tb), earliest, predict, descentRp);
        log.AppendLine($"deorbit: ok {deorbit.Ok} ({deorbit.Reason}), +{deorbit.BurnUT - tb:F1} s after the plane change, Δv {deorbit.DeltaV.magnitude:F2} m/s, "
            + $"predicted along {deorbit.AlongMissM:F1} m cross {deorbit.CrossMissM:F1} m, interface +{deorbit.Stop.BrakeStartUT - deorbit.BurnUT:F0} s, "
            + $"{deorbit.PredictCalls} Predict calls");
        Assert.IsTrue(deorbit.Ok, log.ToString());
        KeplerPropagator.Propagate(rb, vb, Mu, deorbit.BurnUT - tb, out Vector3d rd, out Vector3d vd);
        Vector3d flown = DeorbitError * deorbit.DeltaV;
        vd = vd + flown;
        mass = AfterBurn(mass, flown.magnitude);

        // 4. Correction halfway to the interface (the predicted one, from the flown burn).
        StopPrediction flownStop = predict(rd, vd, deorbit.BurnUT, null);
        Assert.IsTrue(flownStop.Ok, flownStop.Reason + " / " + log);
        double t1 = deorbit.BurnUT + 0.5 * (flownStop.BrakeStartUT - deorbit.BurnUT);
        KeplerPropagator.Propagate(rd, vd, Mu, t1 - deorbit.BurnUT, out Vector3d r1, out Vector3d v1);
        Correction c = CourseCorrection.Solve(r1, v1, t1, body, TargetAt(t1), MaxCorrection, predict);
        log.AppendLine($"correction at +{t1 - deorbit.BurnUT:F0} s: ok {c.Ok} ({c.Reason}), Δv {c.DeltaV.magnitude:F3} m/s, "
            + $"predicted miss {c.MissBeforeM:F1} -> {c.MissAfterM:F1} m, clipped {c.Clipped}, {c.PredictCalls} Predict calls");
        Assert.IsTrue(c.Ok, log.ToString());
        Assert.IsFalse(c.Clipped, log.ToString());
        v1 = v1 + c.DeltaV;
        mass = AfterBurn(mass, c.DeltaV.magnitude);
        StopPrediction planned = ReentryPredictor.Predict(r1, v1, t1, body, air, Capsule, null, out ReentryTrace plannedTrace);
        Assert.IsTrue(planned.Ok, planned.Reason + " / " + log);
        double plannedMiss = SurfaceGeometry.GreatCircle(planned.StopDirNow, TargetAt(t1), R);

        // 5. Jettison JettisonLead before the interface, after the correction.
        double jettisonUT = plannedTrace.InterfaceUT - JettisonLead;
        Assert.Greater(jettisonUT, t1, log.ToString());
        KeplerPropagator.Propagate(r1, v1, Mu, jettisonUT - t1, out Vector3d rj, out Vector3d vj);
        log.AppendLine($"jettison at +{jettisonUT - t1:F0} s after the correction, {(rj.magnitude - R) / 1000:F1} km up; "
            + $"stack {mass:F3} t -> capsule {CapsuleT:F1} t (service module {mass - CapsuleT:F3} t left)");
        Assert.Greater(rj.magnitude - R, air.Depth, log.ToString());

        // 6. Reentry of the capsule alone.
        StopPrediction truth = ReentryPredictor.Predict(rj, vj, jettisonUT, body, air, Capsule, null, out ReentryTrace trace);
        Assert.IsTrue(truth.Ok, truth.Reason + " / " + log);
        double distance = SurfaceGeometry.GreatCircle(truth.StopDirNow, TargetAt(jettisonUT), R);
        log.AppendLine("reentry: " + trace);
        log.AppendLine($"chute open (semi) at {trace.SemiAltitude:F0} m ({trace.SemiAirSpeed:F0} m/s), full at {trace.FullAltitude:F0} m; "
            + $"touchdown {trace.TouchdownSpeed:F1} m/s (vertical {trace.TouchdownVerticalSpeed:F1}), "
            + $"{(trace.InterfaceUT - deorbit.BurnUT) / 60:F1} min from the deorbit to the interface, "
            + $"{(truth.StopUT - trace.InterfaceUT) / 60:F1} min from the interface to the ground");
        log.AppendLine($"Δv: plane change {planeChangeDv:F2} + deorbit {flown.magnitude:F2} (planned {deorbit.DeltaV.magnitude:F2}) "
            + $"+ correction {c.DeltaV.magnitude:F3} = {planeChangeDv + flown.magnitude + c.DeltaV.magnitude:F2} m/s");
        log.AppendLine($"final distance {distance:F1} m (planned after the correction {plannedMiss:F1} m); runtime {watch.Elapsed.TotalMilliseconds:F0} ms");
        TestContext.Out.WriteLine(log.ToString());

        Assert.IsFalse(double.IsNaN(trace.FullUT), log.ToString());
        // Under the full canopy at its terminal speed √(2mg / (ρ0 (CdA + chute))) (24.6 m/s for this synthetic capsule).
        double terminal = Math.Sqrt(2 * CapsuleT * 1000 * ReentryPredictorTests.SurfaceGravity / (1.225 * (1.8 + 30)));
        Assert.AreEqual(terminal, -trace.TouchdownVerticalSpeed, 0.05 * terminal, log.ToString());
        Assert.Greater(c.MissBeforeM, 1000, "the execution error moves the stop / " + log);
        Assert.Less(distance, MaxDistance, log.ToString());
    }
}

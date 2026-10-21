using System;
using System.Diagnostics;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// The whole pure chain on a rotating Mun, burns impulsive: plane change → deorbit re-planned
// from the new orbit → coast → one mid-course correction halfway to the brake start → finite
// retrograde brake to a stop at start_touchdown_altitude (Vehicle.StopHeight = 1500 m) →
// terminal guidance from there (PointMassSim, flat local frame below 1500 m).
//
// The brake stops at 1500 m, not at BrakeSimulator.SafeAltitude: a brake planned to stop at
// 50 m still flies ~70° from vertical at 1500 m (152 m/s horizontal, 53 m/s down, 2.7 km short
// in this scenario), and TerminalGuidance (45° tilt limit) dropped the target from every
// hand-over altitude along that path (806 m miss from 1500 m).
//
// The brake is integrated by a test-side RK4 that follows BrakeSimulator's law exactly (0.85 of
// the thrust against the surface velocity, mass flow, Step 0.5 s): BrakeSimulator.Simulate only
// returns the stop position, and the chain needs the whole state (velocity, mass) at the stop.
// Running the same law also checks the prediction against a separate integration.
public class PrecisionEndToEndTests
{
    // Mun.
    const double Mu = 6.5138398e10;
    const double R = 200000;
    const double RotationPeriod = 138984.38;
    const double UT = 1000;
    const double Deg = Math.PI / 180;
    const double G0 = 9.80665;
    const double Isp = 320;
    const double StartTouchdownAltitude = 1500;  // m, the pilot's start_touchdown_altitude
    const double TouchDown = 2;                  // m/s
    const double MaxCorrection = 20;             // m/s
    const double AttitudeLag = 1.5;              // s, as the robust TerminalGuidance tests
    static double SurfaceGravity => Mu / (R * R);
    static Vector3d Omega => BodyRotation.AngularVelocity(BodyRotation.AngularSpeed(RotationPeriod), 1);
    static readonly Vector3d Z = new Vector3d(0, 0, 1);

    static Body Mun => new Body { Mu = Mu, Radius = R, Omega = Omega, TerrainHeight = p => 0 };

    // TWR 3 on the Mun at the start mass, the same engines all along.
    static readonly double ThrustKN = 3 * 10 * SurfaceGravity;
    static Vehicle Lander(double massT) => new Vehicle
    {
        MassT = massT, ThrustKN = ThrustKN, Isp = Isp, StopHeight = StartTouchdownAltitude,
    };
    // The landing without target as LandingPilot flies it: brake to BrakeSimulator.SafeAltitude.
    static Vehicle NoTarget(double massT) => new Vehicle { MassT = massT, ThrustKN = ThrustKN, Isp = Isp };
    static double AfterBurn(double massT, double dv) => massT * Math.Exp(-dv / (Isp * G0));

    static readonly double R0 = R + 30000;

    // A powered-descent state.
    struct State
    {
        public Vector3d R, V;
        public double UT, Mass, BrakeDeltaV;
        public bool Stopped;
    }

    static Vector3d Acceleration(Vector3d r, Vector3d v, double m, double thrust)
    {
        double rm = r.magnitude;
        Vector3d a = r * (-Mu / (rm * rm * rm));
        Vector3d vs = BodyRotation.SurfaceVelocity(r, v, Omega);
        double speed = vs.magnitude;
        if (speed > 1e-9)
            a = a + vs * (-thrust / (m * speed));
        return a;
    }

    static void Rk4(ref Vector3d r, ref Vector3d v, ref double m, double h, double thrust, double flow)
    {
        Vector3d a1 = Acceleration(r, v, m, thrust);
        Vector3d v1 = v;
        Vector3d v2 = v + a1 * (h / 2);
        Vector3d a2 = Acceleration(r + v1 * (h / 2), v2, m - flow * h / 2, thrust);
        Vector3d v3 = v + a2 * (h / 2);
        Vector3d a3 = Acceleration(r + v2 * (h / 2), v3, m - flow * h / 2, thrust);
        Vector3d v4 = v + a3 * h;
        Vector3d a4 = Acceleration(r + v3 * h, v4, m - flow * h, thrust);
        r = r + (v1 + v2 * 2 + v3 * 2 + v4) * (h / 6);
        v = v + (a1 + a2 * 2 + a3 * 2 + a4) * (h / 6);
        m -= flow * h;
    }

    // Retrograde brake from (r, v, ut) until it stops (BrakeSimulator's stop test) or hits the
    // (flat) ground, in steps of BrakeSimulator.Step.
    static State Brake(Vector3d r, Vector3d v, double ut, double mass)
    {
        double thrust = DescentEnvelope.ThrustFraction * ThrustKN;
        double flow = thrust / (Isp * G0);
        double m = mass, t = ut;
        while (true)
        {
            double speed = BodyRotation.SurfaceVelocity(r, v, Omega).magnitude;
            bool stopped = speed <= Math.Max(BrakeSimulator.StopSpeed, thrust / m * BrakeSimulator.Step);
            if (stopped || r.magnitude <= R || t - ut > BrakeSimulator.MaxBurnTime)
                return new State
                {
                    R = r, V = v, UT = t, Mass = m, BrakeDeltaV = Isp * G0 * Math.Log(mass / m),
                    Stopped = stopped && r.magnitude > R,
                };
            Rk4(ref r, ref v, ref m, BrakeSimulator.Step, thrust, flow);
            t += BrakeSimulator.Step;
        }
    }

    // State in the flat local frame at the surface point `groundNow` (unit, inertial at the
    // state's UT): offset x east, y north (great-circle arcs), z altitude; velocity
    // surface-relative in the vessel's own east/north/up.
    static void Local(State s, Vector3d groundNow, out Vector3d offset, out Vector3d velocity)
    {
        Vector3d up = groundNow.normalized;
        Vector3d east = Vector3d.Cross(Z, up).normalized;
        Vector3d north = Vector3d.Cross(up, east);
        Vector3d rh = s.R.normalized;
        offset = new Vector3d(R * Math.Atan2(Vector3d.Dot(rh, east), Vector3d.Dot(rh, up)),
                              R * Math.Atan2(Vector3d.Dot(rh, north), Vector3d.Dot(rh, up)),
                              s.R.magnitude - R);
        Vector3d vs = BodyRotation.SurfaceVelocity(s.R, s.V, Omega);
        Vector3d eastV = Vector3d.Cross(Z, rh).normalized;
        Vector3d northV = Vector3d.Cross(rh, eastV);
        velocity = new Vector3d(Vector3d.Dot(vs, eastV), Vector3d.Dot(vs, northV), Vector3d.Dot(vs, rh));
    }

    static string Describe(Vector3d offset, Vector3d velocity) =>
        $"offset ({offset.x:F1}, {offset.y:F1}) m at {offset.z:F0} m up ({Math.Sqrt(offset.x * offset.x + offset.y * offset.y):F1} m), "
        + $"velocity ({velocity.x:F2}, {velocity.y:F2}, {velocity.z:F2}) m/s";

    static void AssertSafe(PointMassSim.Result r, string what)
    {
        Assert.IsFalse(r.HasNaN, what + ": NaN command");
        Assert.IsTrue(r.Landed, what + ": " + r);
        Assert.LessOrEqual(-r.FinalVerticalSpeed, TouchDown + 0.5, what + " vertical speed at contact: " + r);
        Assert.IsFalse(double.IsNaN(r.GateHorizontalSpeed), what + " crossed FinalHeight: " + r);
        Assert.LessOrEqual(r.GateHorizontalSpeed, TerminalGuidance.GateSpeed, what + " drift at FinalHeight: " + r);
        Assert.LessOrEqual(r.FinalHorizontalSpeed, 1.5, what + " drift at contact: " + r);
    }

    [Test]
    public void MunFarSideAtLatitude8_LandsWithin100mFor120PercentOfTheBaseline()
    {
        var watch = Stopwatch.StartNew();
        var log = new System.Text.StringBuilder();

        // 1. Circular 30 km equatorial orbit (prograde with the rotation), target on the far side at 8° N.
        var r0 = new Vector3d(R0, 0, 0);
        var v0 = new Vector3d(0, Math.Sqrt(Mu / R0), 0);
        var target0 = new Vector3d(-Math.Cos(8 * Deg), 0, Math.Sin(8 * Deg));
        double mass = 10;
        Vector3d TargetAt(double t) => SurfaceGeometry.AtTime(target0, Omega, t - UT);

        // 2. Plane change, applied as an instant rotation of the velocity at its node.
        PlaneChangePlan pc = PlaneChange.Plan(r0, v0, UT, Mun, Lander(mass), target0);
        log.AppendLine($"plane change: needed {pc.Needed} ok {pc.Ok} ({pc.Reason}), node +{pc.BurnUT - UT:F1} s, Δv {pc.DeltaV:F2} m/s, "
            + $"incl {pc.Target.InclinationRad / Deg:F3}°, predicted deorbit along {pc.Deorbit.AlongMissM:F1} m cross {pc.Deorbit.CrossMissM:F1} m, "
            + $"{pc.Iterations} iteration(s), {pc.PredictCalls} Predict calls");
        Assert.IsTrue(pc.Needed, log.ToString());
        Assert.IsTrue(pc.Ok, log.ToString());
        KeplerPropagator.Propagate(r0, v0, Mu, pc.BurnUT - UT, out Vector3d rb, out Vector3d vOld);
        Vector3d axis = rb.normalized;
        Vector3d n1 = Vector3d.Cross(rb, vOld).normalized;
        Vector3d n2 = pc.Target.PlaneNormal.normalized;
        Vector3d vb = OrbitMath.Rotate(vOld, axis, Math.Atan2(Vector3d.Dot(Vector3d.Cross(n1, n2), axis), Vector3d.Dot(n1, n2)));
        double tb = pc.BurnUT;
        double planeChangeDv = (vb - vOld).magnitude;
        double earliest = PlaneChange.EarliestDeorbitUT(pc.BurnUT, pc.DeltaV, Lander(mass));
        mass = AfterBurn(mass, planeChangeDv);

        // Re-plan from the orbit actually reached; the baseline from the same orbit, no target.
        DeorbitPlan deorbit = DeorbitSearch.Find(rb, vb, tb, Mun, Lander(mass), TargetAt(tb), earliest);
        DeorbitPlan baseline = DeorbitSearch.Baseline(rb, vb, tb, Mun, NoTarget(mass), earliest);
        log.AppendLine($"deorbit: ok {deorbit.Ok} ({deorbit.Reason}), +{deorbit.BurnUT - tb:F1} s after the plane change, Δv {deorbit.DeltaV.magnitude:F2} m/s, "
            + $"predicted along {deorbit.AlongMissM:F1} m cross {deorbit.CrossMissM:F1} m, brake {deorbit.Stop.BrakeDeltaV:F1} m/s, {deorbit.PredictCalls} Predict calls");
        log.AppendLine($"baseline: ok {baseline.Ok} ({baseline.Reason}), +{baseline.BurnUT - tb:F1} s, Δv {baseline.DeltaV.magnitude:F2} m/s, "
            + $"brake {baseline.Stop.BrakeDeltaV:F1} m/s");
        Assert.IsTrue(deorbit.Ok, log.ToString());
        Assert.IsTrue(baseline.Ok, log.ToString());

        // 3. Precision: deorbit impulse, coast halfway to the brake start, one correction.
        KeplerPropagator.Propagate(rb, vb, Mu, deorbit.BurnUT - tb, out Vector3d rd, out Vector3d vd);
        vd = vd + deorbit.DeltaV;
        double massP = AfterBurn(mass, deorbit.DeltaV.magnitude);
        double t1 = deorbit.BurnUT + 0.5 * (deorbit.Stop.BrakeStartUT - deorbit.BurnUT);
        KeplerPropagator.Propagate(rd, vd, Mu, t1 - deorbit.BurnUT, out Vector3d r1, out Vector3d v1);
        Correction c = CourseCorrection.Solve(r1, v1, t1, Mun, Lander(massP), TargetAt(t1), MaxCorrection);
        log.AppendLine($"correction at +{t1 - deorbit.BurnUT:F0} s: ok {c.Ok} ({c.Reason}), Δv {c.DeltaV.magnitude:F3} m/s, "
            + $"predicted miss {c.MissBeforeM:F1} -> {c.MissAfterM:F1} m, clipped {c.Clipped}, {c.PredictCalls} Predict calls");
        Assert.IsTrue(c.Ok, log.ToString());
        Assert.IsFalse(c.Clipped, log.ToString());
        v1 = v1 + c.DeltaV;
        massP = AfterBurn(massP, c.DeltaV.magnitude);
        StopPrediction after = StopPointPredictor.Predict(r1, v1, t1, Mun, Lander(massP), null);
        Assert.IsTrue(after.Ok, after.Reason + " / " + log);
        double predictedMiss = SurfaceGeometry.GreatCircle(after.StopDirNow, TargetAt(t1), R);

        // 4. Brake from the predicted start to its stop at start_touchdown_altitude.
        KeplerPropagator.Propagate(r1, v1, Mu, after.BrakeStartUT - t1, out Vector3d rs, out Vector3d vs);
        State gate = Brake(rs, vs, after.BrakeStartUT, massP);
        double simulatedStopMiss = SurfaceGeometry.GreatCircle(gate.R.normalized, TargetAt(gate.UT), R);
        log.AppendLine($"brake: start +{after.BrakeStartUT - t1:F1} s after the correction, predicted stop {after.StopAltitude:F1} m up "
            + $"{predictedMiss:F1} m from the target, simulated stop {gate.R.magnitude - R:F1} m up {simulatedStopMiss:F1} m from it "
            + $"(brake {gate.BrakeDeltaV:F1} m/s, predicted {after.BrakeDeltaV:F1} m/s)");
        Assert.IsTrue(gate.Stopped, "the brake does not stop / " + log);
        Local(gate, TargetAt(gate.UT), out Vector3d offset, out Vector3d velocity);
        log.AppendLine($"state at the hand-over: +{gate.UT - after.BrakeStartUT:F1} s of brake, " + Describe(offset, velocity)
            + $", mass {gate.Mass:F3} t");

        // 5. Terminal guidance from the hand-over, flat local frame.
        double maxAccel = ThrustKN / gate.Mass;
        PointMassSim.Result landing = PointMassSim.Run(offset, velocity, SurfaceGravity, maxAccel, AttitudeLag,
            0.02, 600, (p, v) => TerminalGuidance.Compute(p, v, SurfaceGravity, maxAccel, TouchDown));
        log.AppendLine("terminal: " + landing);

        // Baseline: the same orbit, deorbit at the natural point, brake to ~50 m, then the final
        // descent onto the point under its stop (no target).
        KeplerPropagator.Propagate(rb, vb, Mu, baseline.BurnUT - tb, out Vector3d rbd, out Vector3d vbd);
        vbd = vbd + baseline.DeltaV;
        double massB = AfterBurn(mass, baseline.DeltaV.magnitude);
        KeplerPropagator.Propagate(rbd, vbd, Mu, baseline.Stop.BrakeStartUT - baseline.BurnUT, out Vector3d rbs, out Vector3d vbs);
        State gateB = Brake(rbs, vbs, baseline.Stop.BrakeStartUT, massB);
        Assert.IsTrue(gateB.Stopped, "the baseline brake does not stop / " + log);
        Assert.That(gateB.R.magnitude - R, Is.InRange(BrakeSimulator.SafeAltitude, BrakeSimulator.SafeAltitude + 100), log.ToString());
        Local(gateB, gateB.R.normalized, out Vector3d offsetB, out Vector3d velocityB);
        double maxAccelB = ThrustKN / gateB.Mass;
        PointMassSim.Result landingB = PointMassSim.Run(offsetB, velocityB, SurfaceGravity, maxAccelB, AttitudeLag,
            0.02, 600, (p, v) => TerminalGuidance.Compute(p, v, SurfaceGravity, maxAccelB, TouchDown));
        log.AppendLine($"baseline hand-over: brake {gateB.BrakeDeltaV:F1} m/s, " + Describe(offsetB, velocityB));
        log.AppendLine("baseline terminal: " + landingB);

        // 6. Budget, plane change excluded from both.
        double correctionDv = c.DeltaV.magnitude;
        double precisionDv = deorbit.DeltaV.magnitude + correctionDv + gate.BrakeDeltaV + landing.DeltaV;
        double baselineDv = baseline.DeltaV.magnitude + gateB.BrakeDeltaV + landingB.DeltaV;
        double ratio = precisionDv / baselineDv;
        double terminalReserve = PrecisionBudget.TerminalReserve(StartTouchdownAltitude, SurfaceGravity, TouchDown, ThrustKN / AfterBurn(mass, deorbit.DeltaV.magnitude + deorbit.Stop.BrakeDeltaV));
        double baselineReserve = PrecisionBudget.TerminalReserve(BrakeSimulator.SafeAltitude, SurfaceGravity, TouchDown, ThrustKN / AfterBurn(mass, baseline.DeltaV.magnitude + baseline.Stop.BrakeDeltaV));
        PrecisionBudget.Budget predicted = PrecisionBudget.Evaluate(baseline, deorbit, correctionDv, terminalReserve, 0, planeChangeDv, baselineReserve);
        log.AppendLine($"simulated Δv: precision {precisionDv:F1} m/s (deorbit {deorbit.DeltaV.magnitude:F2} + correction {correctionDv:F3} "
            + $"+ brake {gate.BrakeDeltaV:F1} + terminal {landing.DeltaV:F1}), baseline {baselineDv:F1} m/s (deorbit {baseline.DeltaV.magnitude:F2} "
            + $"+ brake {gateB.BrakeDeltaV:F1} + terminal {landingB.DeltaV:F1}), ratio {ratio:F4}; plane change {planeChangeDv:F2} m/s apart");
        log.AppendLine($"predicted at start (terminal reserve {terminalReserve:F1} m/s, baseline final descent reserve {baselineReserve:F1} m/s): "
            + predicted.Summary + $" ratio {predicted.PrecisionDeltaV / predicted.BaselineDeltaV:F4} ({predicted.Reason})");
        log.AppendLine($"final distance {landing.FinalOffsetM:F1} m, gate drift {landing.GateHorizontalSpeed:F2} m/s at {FinalDescent.FinalHeight:F0} m, "
            + $"touchdown drift {landing.FinalHorizontalSpeed:F2} m/s, vz {landing.FinalVerticalSpeed:F2} m/s; runtime {watch.Elapsed.TotalMilliseconds:F0} ms");
        TestContext.Out.WriteLine(log.ToString());

        // The hand-over is where the planned brake ends: just above start_touchdown_altitude, near rest.
        Assert.That(gate.R.magnitude - R, Is.InRange(StartTouchdownAltitude, StartTouchdownAltitude + 100), log.ToString());
        Assert.Less(velocity.magnitude, 5, log.ToString());
        Assert.Less(Math.Abs(simulatedStopMiss - predictedMiss), 10, log.ToString());
        AssertSafe(landing, "precision");
        AssertSafe(landingB, "baseline");
        Assert.IsFalse(landing.EverDropped, log.ToString());
        Assert.Less(landing.FinalOffsetM, 100, log.ToString());
        Assert.LessOrEqual(ratio, PrecisionBudget.MaxRatio, log.ToString());
        Assert.IsTrue(predicted.WithinLimit, log.ToString());
        Assert.IsFalse(after.BelowHandOver, log.ToString());
    }
}

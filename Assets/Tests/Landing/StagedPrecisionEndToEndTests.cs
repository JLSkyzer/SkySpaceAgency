using System;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// A precision landing with a chosen stage, the whole pure chain on a rotating Mun as in
// PrecisionEndToEndTests: a booster under a lander. The burns before the brake fly on the
// booster; the brake drops it when dry or at the drop height, the lander stops at
// start_touchdown_altitude and the terminal guidance (PointMassSim) lands on the lander.
//
// The brake is flown by BrakeSimulator.Simulate itself (its last sample is the hand-over state);
// the prediction comes from StopPointPredictor, as in flight.
public class StagedPrecisionEndToEndTests
{
    // Mun.
    const double Mu = 6.5138398e10;
    const double R = 200000;
    const double RotationPeriod = 138984.38;
    const double UT = 1000;
    const double Deg = Math.PI / 180;
    const double G0 = 9.80665;
    const double StartTouchdownAltitude = 1500;
    const double TouchDown = 2;
    const double MaxCorrection = 20;
    const double AttitudeLag = 1.5;
    const double MaxTilt = 40;
    static double SurfaceGravity => Mu / (R * R);
    static Vector3d Omega => BodyRotation.AngularVelocity(BodyRotation.AngularSpeed(RotationPeriod), 1);
    static readonly Vector3d Z = new Vector3d(0, 0, 1);
    static Body Mun => new Body { Mu = Mu, Radius = R, Omega = Omega, TerrainHeight = p => 0 };
    static readonly double R0 = R + 30000;

    // Booster: 60 kN on 16 t. Lander: TWR 3 on the Mun at 6 t, 1500 m/s.
    static Vehicle Staged(double boosterDv, double stopHeight)
    {
        var stages = new[]
        {
            new BrakeStage { Stage = 3, ThrustKN = 60, MassT = 16, Isp = 300, DeltaV = boosterDv },
            new BrakeStage { Stage = 2, ThrustKN = 3 * 6 * SurfaceGravity, MassT = 6, Isp = 320, DeltaV = 1500 },
        };
        return new Vehicle
        {
            ThrustKN = 60, MassT = 16, Isp = 300, StopHeight = stopHeight, Stages = stages,
            DropHeight = StagedBrakePlan.Floor(0, StartTouchdownAltitude),
        };
    }

    struct Gate
    {
        public Vector3d R, V;
        public double UT, Mass;
        public BrakeRun Run;
    }

    // The brake flown from (r, v) at ut with the vehicle's stages.
    static Gate Brake(Vector3d r, Vector3d v, double ut, Vehicle vehicle, double dropHeight)
    {
        var input = new BrakeInput
        {
            Position = r, Velocity = v, UT = ut, Mu = Mu, BodyRadius = R, AngularVelocity = Omega,
            ThrustKN = vehicle.ThrustKN, MassT = vehicle.MassT, Isp = vehicle.Isp, TerrainHeight = p => 0,
            Stages = vehicle.Stages, DropHeight = dropHeight,
        };
        BrakeSample end = default;
        BrakeRun run = BrakeSimulator.Simulate(input, ut, s => end = s);
        BrakeStage landing = vehicle.Stages[vehicle.Stages.Length - 1];
        return new Gate
        {
            R = end.Position, V = end.Velocity, UT = end.UT, Run = run,
            Mass = landing.MassT * Math.Exp(-run.FinalStageDeltaV / (landing.Isp * G0)),
        };
    }

    static void Local(Gate s, Vector3d groundNow, out Vector3d offset, out Vector3d velocity)
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

    static void AssertDrop(BrakeRun run, double dropHeight, bool expectDry, string log)
    {
        Assert.AreEqual(1, run.Drops.Count, log);
        Assert.AreEqual(1, run.FinalStageIndex, log);
        BrakeDrop d = run.Drops[0];
        Assert.AreEqual(expectDry, d.Dry, log);
        if (d.Dry)
            Assert.That(d.Altitude, Is.GreaterThanOrEqualTo(dropHeight), log);
        else
        {
            Assert.That(d.Altitude, Is.LessThanOrEqualTo(dropHeight), log);
            Assert.That(d.Altitude, Is.GreaterThan(dropHeight - Math.Abs(d.SurfaceSpeed) * BrakeSimulator.Step - 1), log);
        }
    }

    // 2000 m/s on the booster: dropped at the drop height. 400 m/s: dry during the brake.
    [TestCase(2000, false)]
    [TestCase(400, true)]
    public void TwoStageMunLanding_Within100mFor120PercentOfTheBaseline(double boosterDv, bool expectDry)
    {
        var log = new System.Text.StringBuilder();
        var r0 = new Vector3d(R0, 0, 0);
        var v0 = new Vector3d(0, Math.Sqrt(Mu / R0), 0);
        var target0 = new Vector3d(-Math.Cos(8 * Deg), 0, Math.Sin(8 * Deg));
        Vector3d TargetAt(double t) => SurfaceGeometry.AtTime(target0, Omega, t - UT);
        Vehicle lander = Staged(boosterDv, StartTouchdownAltitude);

        // The start plan, as the pilot checks it: budget and the staged checks.
        StartPlan start = PrecisionStart.Plan(new StartInput
        {
            R = r0, V = v0, UT = UT, Body = Mun, Vehicle = lander, TargetDirNow = target0,
            TouchDownSpeed = TouchDown, MaxTiltDeg = MaxTilt,
        });
        log.AppendLine($"start plan: ok {start.Ok} ({start.Reason}), {start.Budget.Summary}, before brake {start.BeforeBrakeDeltaV:F0} m/s, " +
            $"landing stage {start.LandingStageDeltaV:F0} m/s, predicted drops {start.Deorbit.Stop.Drops?.Count}");
        Assert.IsTrue(start.Ok, log.ToString());
        Assert.IsTrue(start.Budget.WithinLimit, log.ToString());

        // Plane change at its node, then the deorbit re-planned from the orbit reached.
        PlaneChangePlan pc = PlaneChange.Plan(r0, v0, UT, Mun, lander, target0);
        Assert.IsTrue(pc.Ok && pc.Needed, pc.Reason);
        KeplerPropagator.Propagate(r0, v0, Mu, pc.BurnUT - UT, out Vector3d rb, out Vector3d vOld);
        Vector3d axis = rb.normalized;
        Vector3d n1 = Vector3d.Cross(rb, vOld).normalized;
        Vector3d n2 = pc.Target.PlaneNormal.normalized;
        Vector3d vb = OrbitMath.Rotate(vOld, axis, Math.Atan2(Vector3d.Dot(Vector3d.Cross(n1, n2), axis), Vector3d.Dot(n1, n2)));
        double tb = pc.BurnUT;
        double earliest = PlaneChange.EarliestDeorbitUT(pc.BurnUT, pc.DeltaV, lander);
        lander = lander.AfterImpulse((vb - vOld).magnitude);
        Vehicle noTarget = lander;
        noTarget.StopHeight = 0;

        DeorbitPlan deorbit = DeorbitSearch.Find(rb, vb, tb, Mun, lander, TargetAt(tb), earliest);
        DeorbitPlan baseline = DeorbitSearch.Baseline(rb, vb, tb, Mun, noTarget, earliest);
        log.AppendLine($"deorbit ok {deorbit.Ok} ({deorbit.Reason}) Δv {deorbit.DeltaV.magnitude:F2}, brake {deorbit.Stop.BrakeDeltaV:F0}; " +
            $"baseline ok {baseline.Ok} Δv {baseline.DeltaV.magnitude:F2}, brake {baseline.Stop.BrakeDeltaV:F0}");
        Assert.IsTrue(deorbit.Ok, log.ToString());
        Assert.IsTrue(baseline.Ok, log.ToString());

        // Deorbit, coast halfway to the brake, one correction.
        KeplerPropagator.Propagate(rb, vb, Mu, deorbit.BurnUT - tb, out Vector3d rd, out Vector3d vd);
        vd = vd + deorbit.DeltaV;
        Vehicle afterDeorbit = lander.AfterImpulse(deorbit.DeltaV.magnitude);
        double t1 = deorbit.BurnUT + 0.5 * (deorbit.Stop.BrakeStartUT - deorbit.BurnUT);
        KeplerPropagator.Propagate(rd, vd, Mu, t1 - deorbit.BurnUT, out Vector3d r1, out Vector3d v1);
        Correction c = CourseCorrection.Solve(r1, v1, t1, Mun, afterDeorbit, TargetAt(t1), MaxCorrection);
        Assert.IsTrue(c.Ok, c.Reason + " / " + log);
        v1 = v1 + c.DeltaV;
        Vehicle braking = afterDeorbit.AfterImpulse(c.DeltaV.magnitude);
        StopPrediction after = StopPointPredictor.Predict(r1, v1, t1, Mun, braking, null);
        Assert.IsTrue(after.Ok, after.Reason + " / " + log);

        // The brake: booster first, dropped, the lander stops at the hand-over.
        KeplerPropagator.Propagate(r1, v1, Mu, after.BrakeStartUT - t1, out Vector3d rs, out Vector3d vs);
        // Flown with the drop height of the prediction, as the flight keeps it.
        Assert.That(after.DropHeight, Is.GreaterThanOrEqualTo(braking.DropHeight), log.ToString());
        Gate gate = Brake(rs, vs, after.BrakeStartUT, braking, after.DropHeight);
        BrakeDrop drop = gate.Run.Drops.Count > 0 ? gate.Run.Drops[0] : default;
        log.AppendLine($"brake: stopped {gate.Run.Stopped} at {gate.R.magnitude - R:F0} m, Δv {gate.Run.DeltaV:F0} (predicted {after.BrakeDeltaV:F0}), " +
            $"lander {gate.Run.FinalStageDeltaV:F0} m/s; drop {(drop.Dry ? "dry" : "forced")} at {drop.Altitude:F0} m, {drop.SurfaceSpeed:F0} m/s " +
            $"(drop height {after.DropHeight:F0} m, safe {after.SafeDropHeight:F0} m)");
        Assert.IsTrue(gate.Run.Stopped, log.ToString());
        AssertDrop(gate.Run, after.DropHeight, expectDry, log.ToString());
        Assert.That(gate.R.magnitude - R, Is.InRange(StartTouchdownAltitude, StartTouchdownAltitude + 100), log.ToString());

        // Terminal guidance on the lander.
        Local(gate, TargetAt(gate.UT), out Vector3d offset, out Vector3d velocity);
        double maxAccel = braking.Stages[1].ThrustKN / gate.Mass;
        PointMassSim.Result landing = PointMassSim.Run(offset, velocity, SurfaceGravity, maxAccel, AttitudeLag,
            0.02, 600, (p, v) => TerminalGuidance.Compute(p, v, SurfaceGravity, maxAccel, TouchDown));
        log.AppendLine("terminal: " + landing);

        // Baseline: same orbit, natural deorbit, brake to ~50 m with the same drops, final descent.
        KeplerPropagator.Propagate(rb, vb, Mu, baseline.BurnUT - tb, out Vector3d rbd, out Vector3d vbd);
        vbd = vbd + baseline.DeltaV;
        Vehicle noTargetBraking = noTarget.AfterImpulse(baseline.DeltaV.magnitude);
        KeplerPropagator.Propagate(rbd, vbd, Mu, baseline.Stop.BrakeStartUT - baseline.BurnUT, out Vector3d rbs, out Vector3d vbs);
        Gate gateB = Brake(rbs, vbs, baseline.Stop.BrakeStartUT, noTargetBraking, baseline.Stop.DropHeight);
        Assert.IsTrue(gateB.Run.Stopped, log.ToString());
        Local(gateB, gateB.R.normalized, out Vector3d offsetB, out Vector3d velocityB);
        double maxAccelB = noTargetBraking.Stages[1].ThrustKN / gateB.Mass;
        PointMassSim.Result landingB = PointMassSim.Run(offsetB, velocityB, SurfaceGravity, maxAccelB, AttitudeLag,
            0.02, 600, (p, v) => TerminalGuidance.Compute(p, v, SurfaceGravity, maxAccelB, TouchDown));

        double precisionDv = deorbit.DeltaV.magnitude + c.DeltaV.magnitude + gate.Run.DeltaV + landing.DeltaV;
        double baselineDv = baseline.DeltaV.magnitude + gateB.Run.DeltaV + landingB.DeltaV;
        double ratio = precisionDv / baselineDv;
        double landerDv = gate.Run.FinalStageDeltaV + landing.DeltaV;
        log.AppendLine($"Δv precision {precisionDv:F0}, baseline {baselineDv:F0}, ratio {ratio:F3}; lander spent {landerDv:F0} of 1500 m/s; " +
            $"final distance {landing.FinalOffsetM:F1} m");
        TestContext.Out.WriteLine(log.ToString());

        Assert.IsTrue(landing.Landed, log.ToString());
        Assert.IsFalse(landing.HasNaN, log.ToString());
        Assert.LessOrEqual(-landing.FinalVerticalSpeed, TouchDown + 0.5, log.ToString());
        Assert.Less(landing.FinalOffsetM, 100, log.ToString());
        Assert.LessOrEqual(ratio, PrecisionBudget.MaxRatio, log.ToString());
        Assert.LessOrEqual(StagedBrakePlan.DeltaVMargin * landerDv, 1500, log.ToString());
    }

    [Test]
    public void BoosterTooSmallForTheBurnsBeforeTheBrake_Refused()
    {
        var r0 = new Vector3d(R0, 0, 0);
        var v0 = new Vector3d(0, Math.Sqrt(Mu / R0), 0);
        var target0 = new Vector3d(-Math.Cos(8 * Deg), 0, Math.Sin(8 * Deg));
        StartPlan start = PrecisionStart.Plan(new StartInput
        {
            R = r0, V = v0, UT = UT, Body = Mun, Vehicle = Staged(5, StartTouchdownAltitude), TargetDirNow = target0,
            TouchDownSpeed = TouchDown, MaxTiltDeg = MaxTilt,
        });
        Assert.IsFalse(start.Ok);
        StringAssert.StartsWith("Stage 1 has 5 m/s for the ", start.Reason);
    }

    [Test]
    public void AfterImpulse_SpendsTheActiveStageAndCopiesTheList()
    {
        Vehicle v = Staged(400, StartTouchdownAltitude);
        Vehicle after = v.AfterImpulse(100);
        Assert.AreEqual(400, v.Stages[0].DeltaV);       // not changed in place
        Assert.AreEqual(300, after.Stages[0].DeltaV, 1e-9);
        Assert.AreEqual(16 * Math.Exp(-100 / (300 * G0)), after.Stages[0].MassT, 1e-9);
        Assert.AreEqual(after.Stages[0].MassT, after.MassT, 1e-9);
        Assert.AreEqual(6, after.Stages[1].MassT);
        Assert.AreEqual(v.Stages[1].ThrustKN / 6, v.LandingAccel, 1e-9);
    }
}

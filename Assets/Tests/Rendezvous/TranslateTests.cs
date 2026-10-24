using System;
using System.Collections.Generic;
using K2D2.Rendezvous;
using KSP.Sim;
using NUnit.Framework;

// The translation controller's pure part (TranslateLogic.cs): the RCS allocation through a
// measured RcsMapping, the RCS and main-engine probes, the main-engine sequence (turn, burn, turn
// back), the mode choice and the attitude math. The vessels here are point masses: a velocity,
// an attitude turned at a fixed rate, and thrust applied per input.
public class TranslateTests
{
    const double Deg = Math.PI / 180;
    const double Dt = 0.02;

    // A port frame turned 30° about z then 50° about its new x (as RcsProbeTests).
    static PortFrame Frame()
    {
        double a = 30 * Deg, b = 50 * Deg;
        var right = new Vector3d(Math.Cos(a), Math.Sin(a), 0);
        var y = new Vector3d(-Math.Sin(a), Math.Cos(a), 0);
        var z = new Vector3d(0, 0, 1);
        var up = y * Math.Cos(b) + z * Math.Sin(b);
        var fwd = Vector3d.Cross(right, up);
        return PortFrame.From(fwd, up);
    }

    // X moves the vessel down at 0.4 m/s², Y forward at 0.3, Z right at 0.2 (all per full input).
    static readonly int[] SimAxis = { RcsMapping.Up, RcsMapping.Forward, RcsMapping.Right };
    static readonly int[] SimSign = { -1, 1, 1 };
    static readonly double[] SimAccel = { 0.4, 0.3, 0.2 };

    static Vector3d RcsAccel(PortFrame f, float[] inputs, bool zDead = false)
    {
        var acc = Vector3d.zero;
        for (int i = 0; i < 3; i++)
        {
            if (zDead && i == 2) continue;
            acc += f.Axis(SimAxis[i]) * (SimSign[i] * SimAccel[i] * Math.Max(-1, Math.Min(1, inputs[i])));
        }
        return acc;
    }

    static RcsMapping Probe(PortFrame f, out double[] accel, bool zDead = false)
    {
        var probe = new RcsProbeSequence();
        var vel = Vector3d.zero;
        double t = 0;
        for (int k = 0; k < 10000 && !probe.Done; k++)
        {
            float[] inputs = probe.Step(t, vel, f);
            vel += RcsAccel(f, inputs, zDead) * Dt;
            t += Dt;
        }
        Assert.IsTrue(probe.Done, "the probe never ended");
        accel = probe.Accel;
        Assert.Less(vel.magnitude, 0.002, "the cancel pulses leave the vessel at rest");
        return probe.Mapping;
    }

    // ---- RCS probe ----

    [Test]
    public void RcsProbe_ThreePulses_MapsAxesAndAccelerations()
    {
        var f = Frame();
        var m = Probe(f, out var accel);
        Assert.IsTrue(m.Ok, m.Refusal);
        CollectionAssert.AreEqual(SimAxis, m.AxisOf);
        CollectionAssert.AreEqual(SimSign, m.Sign);
        for (int i = 0; i < 3; i++)
            Assert.AreEqual(SimAccel[i], accel[i], 0.1 * SimAccel[i], $"input {i}");
    }

    [Test]
    public void RcsProbe_WaitsForTheTurnToSettle()
    {
        var f = Frame();
        var probe = new RcsProbeSequence();
        var vel = Vector3d.zero;
        double t = 0;
        for (int k = 0; k < 10000 && !probe.Done; k++)
        {
            double rate = t < 3 ? 2.0 : 0.1;   // °/s: still turning for 3 s
            float[] u = probe.Step(t, vel, f, rate);
            if (t < 3) CollectionAssert.AreEqual(new[] { 0f, 0f, 0f }, u, "no pulse while turning");
            vel += RcsAccel(f, u) * Dt;
            t += Dt;
        }
        Assert.IsTrue(probe.Mapping.Ok, probe.Mapping.Refusal);
        Assert.IsFalse(probe.StartedUnsettled);
        CollectionAssert.AreEqual(SimAxis, probe.Mapping.AxisOf);
    }

    [Test]
    public void RcsProbe_FrameTurningDuringTheProbe_EachResponseInItsOwnFrame()
    {
        // The port frame turns 10°/s about its forward axis: 50° between X's pulse and the end.
        // X moves the vessel along up, so read in the last frame it would be 50° off its axis.
        var f0 = Frame();
        var probe = new RcsProbeSequence();
        var vel = Vector3d.zero;
        double t = 0;
        PortFrame f = f0;
        for (int k = 0; k < 10000 && !probe.Done; k++)
        {
            double a = 10 * Deg * t;
            f = PortFrame.From(f0.Forward, EngineSim.Rot(f0.Up, f0.Forward, a));
            float[] u = probe.Step(t, vel, f);
            vel += RcsAccel(f, u) * Dt;
            t += Dt;
        }
        Assert.IsTrue(probe.Mapping.Ok, probe.Mapping.Refusal);
        CollectionAssert.AreEqual(SimAxis, probe.Mapping.AxisOf);
        CollectionAssert.AreEqual(SimSign, probe.Mapping.Sign);
    }

    [Test]
    public void RcsProbe_DeadInput_MarkedDead()
    {
        var m = Probe(Frame(), out _, zDead: true);
        Assert.IsTrue(m.Ok, m.Refusal);
        CollectionAssert.AreEqual(new[] { false, false, true }, m.Dead);
    }

    // ---- RCS allocation ----

    [Test]
    public void Allocate_PermutedMapping_InputsOnTheMappedAxes()
    {
        var f = Frame();
        var m = Probe(f, out var accel);
        // 0.1 m/s right, 0.05 up, −0.02 forward
        var err = 0.1 * f.Right + 0.05 * f.Up - 0.02 * f.Forward;
        RcsCommand c = RcsAllocation.Allocate(m, accel, err, f);
        // X: up, sign −1, 0.4 m/s² → −0.05 / 0.4; Y: forward → −0.02 / 0.3; Z: right → 0.1 / 0.2
        Assert.AreEqual(-0.05 / 0.4, c.X, 0.015);
        Assert.AreEqual(-0.02 / 0.3, c.Y, 0.01);
        Assert.AreEqual(0.1 / 0.2, c.Z, 0.05);
        Assert.IsFalse(c.Saturated);
        Assert.Less(c.Uncovered.magnitude, 1e-9);
    }

    [Test]
    public void Allocate_LargeError_Saturates()
    {
        var f = Frame();
        var m = Probe(f, out var accel);
        RcsCommand c = RcsAllocation.Allocate(m, accel, 2.0 * f.Right, f);
        Assert.AreEqual(1f, c.Z, 1e-6);
        Assert.IsTrue(c.Saturated);
    }

    [Test]
    public void Allocate_InsideDeadband_NoInput()
    {
        var f = Frame();
        var m = Probe(f, out var accel);
        RcsCommand c = RcsAllocation.Allocate(m, accel, 0.004 * f.Right + 0.003 * f.Up, f);
        Assert.AreEqual(0f, c.X);
        Assert.AreEqual(0f, c.Y);
        Assert.AreEqual(0f, c.Z);
    }

    [Test]
    public void Allocate_DeadAxis_LeftUncovered()
    {
        var f = Frame();
        var m = Probe(f, out var accel, zDead: true);
        var err = 0.1 * f.Right + 0.05 * f.Up;
        RcsCommand c = RcsAllocation.Allocate(m, accel, err, f);
        Assert.AreEqual(0f, c.Z);
        Assert.AreEqual(-0.05 / 0.4, c.X, 0.015);
        Assert.AreEqual(0.1, Vector3d.Dot(c.Uncovered, f.Right), 1e-9);
        Assert.AreEqual(0, Vector3d.Dot(c.Uncovered, f.Up), 1e-9);
    }

    [Test]
    public void Allocate_ClosedLoop_ReachesTheWantedVelocity()
    {
        var f = Frame();
        var m = Probe(f, out var accel);
        var vel = new Vector3d(0.3, -0.2, 0.1);
        var wanted = 0.25 * f.Forward;
        for (int k = 0; k < (int)(30 / Dt); k++)
        {
            RcsCommand c = RcsAllocation.Allocate(m, accel, wanted - vel, f);
            vel += RcsAccel(f, new[] { c.X, c.Y, c.Z }) * Dt;
        }
        Assert.Less((wanted - vel).magnitude, 2 * RcsAllocation.Deadband);
    }

    // ---- main engine ----

    // Our vessel with a main engine: the port frame turns at TurnRate toward the aim, thrust is
    // ThrustLocal in the port frame. MinThrottle > 0 models an engine that cannot throttle lower.
    class EngineSim
    {
        public PortFrame Port = Frame();
        public Vector3d ThrustLocal = new Vector3d(0, 0, 1);
        public double MaxAccel = 2.0;
        public double MinThrottle;
        public double TurnRateDeg = 5;
        public Vector3d Vel;
        public double T;
        public double RateDeg;
        public Vector3d Omega;   // rad/s, the last step's turn

        public Vector3d ThrustWorld => Port.ToWorld(ThrustLocal);

        public void Apply(double throttle, Vector3d aimAxis, Vector3d aimRoll)
        {
            if (throttle > 0) throttle = Math.Max(throttle, MinThrottle);
            Vel += ThrustWorld * (MaxAccel * Math.Min(1, throttle) * Dt);
            RateDeg = 0;
            Omega = Vector3d.zero;
            if (aimAxis.sqrMagnitude > 0)
            {
                var want = PortFrame.From(aimAxis, aimRoll.sqrMagnitude > 0 ? aimRoll : Port.Up);
                // turn the whole frame toward want, at most TurnRateDeg per second
                Vector3d k = Vector3d.Cross(Port.Forward, want.Forward);
                double angle = Math.Acos(Math.Max(-1, Math.Min(1, Vector3d.Dot(Port.Forward, want.Forward))));
                double step = Math.Min(angle, TurnRateDeg * Deg * Dt);
                if (angle > 1e-9)
                {
                    if (k.sqrMagnitude < 1e-12) k = Port.Up;
                    k = k.normalized;
                    Port = PortFrame.From(Rot(Port.Forward, k, step), Rot(Port.Up, k, step));
                    RateDeg = step / Dt / Deg;
                    Omega = k * (step / Dt);
                }
            }
            T += Dt;
        }

        public static Vector3d Rot(Vector3d v, Vector3d k, double a) =>
            v * Math.Cos(a) + Vector3d.Cross(k, v) * Math.Sin(a) + k * (Vector3d.Dot(k, v) * (1 - Math.Cos(a)));
    }

    // Runs the sequence toward a fixed wanted velocity with the port wanted along wantedForward.
    static List<EnginePhase> Fly(EngineSim sim, MainEngineSequence seq, Vector3d wanted, Vector3d wantedForward, double seconds)
    {
        var phases = new List<EnginePhase>();
        for (int k = 0; k < (int)(seconds / Dt) && seq.Failure == null; k++)
        {
            EngineStep s = seq.Step(sim.T, sim.Vel, wanted, sim.ThrustWorld, sim.Port.Forward, wantedForward, sim.RateDeg, sim.MaxAccel);
            if (phases.Count == 0 || phases[phases.Count - 1] != seq.Phase) phases.Add(seq.Phase);
            Vector3d aimAxis = wantedForward, aimRoll = Vector3d.zero;
            if (s.AimBurn)
                AttitudeAim.BurnPortAxes(sim.Port, sim.ThrustLocal, s.BurnDir, out aimAxis, out aimRoll);
            Assert.IsTrue(s.Throttle >= 0 && s.Throttle <= 1);
            if (seq.Phase == EnginePhase.Turning || seq.Phase == EnginePhase.TurningBack)
                Assert.AreEqual(0, s.Throttle, "no thrust while turning");
            sim.Apply(s.Throttle, aimAxis, aimRoll);
        }
        return phases;
    }

    [Test]
    public void Engine_AlongTheThrust_BurnsWithoutTurning()
    {
        var sim = new EngineSim();
        var seq = new MainEngineSequence();
        var wanted = 0.2 * sim.Port.Forward;
        var phases = Fly(sim, seq, wanted, sim.Port.Forward, 20);
        Assert.IsNull(seq.Failure);
        CollectionAssert.DoesNotContain(phases, EnginePhase.Turning);
        Assert.AreEqual(1, seq.Burns);
        Assert.Less((wanted - sim.Vel).magnitude, MainEngineSequence.AssumedDeadband);
        Assert.Less(seq.LastResidual, MainEngineSequence.AssumedDeadband);
    }

    [Test]
    public void Engine_Sideways_TurnsBurnsAndTurnsBack()
    {
        var sim = new EngineSim();
        var seq = new MainEngineSequence();
        var forward = sim.Port.Forward;
        var wanted = 0.1 * sim.Port.Right;
        var phases = Fly(sim, seq, wanted, forward, 120);
        Assert.IsNull(seq.Failure);
        CollectionAssert.AreEqual(new[] { EnginePhase.Turning, EnginePhase.Burning, EnginePhase.TurningBack, EnginePhase.Idle }, phases);
        Assert.Less((wanted - sim.Vel).magnitude, MainEngineSequence.AssumedDeadband);
        Assert.Greater(Vector3d.Dot(sim.Port.Forward, forward), Math.Cos(2 * Deg), "turned back to the port attitude");
    }

    [Test]
    public void Engine_ThrustBehindThePort_BrakesWithoutTurning()
    {
        // The engine pushes along −forward: a brake along −forward needs no turn.
        var sim = new EngineSim { ThrustLocal = new Vector3d(0, 0, -1) };
        sim.Vel = 0.3 * sim.Port.Forward;
        var seq = new MainEngineSequence();
        var phases = Fly(sim, seq, Vector3d.zero, sim.Port.Forward, 20);
        CollectionAssert.DoesNotContain(phases, EnginePhase.Turning);
        Assert.Less(sim.Vel.magnitude, MainEngineSequence.AssumedDeadband);
    }

    [Test]
    public void Engine_InsideTheDeadband_NoBurn()
    {
        var sim = new EngineSim();
        var seq = new MainEngineSequence();
        Fly(sim, seq, 0.004 * sim.Port.Right, sim.Port.Forward, 10);
        Assert.AreEqual(0, seq.Burns);
        Assert.AreEqual(EnginePhase.Idle, seq.Phase);
    }

    [Test]
    public void Engine_NoThrust_Fails()
    {
        var sim = new EngineSim { MaxAccel = 0 };
        var seq = new MainEngineSequence();
        Fly(sim, seq, 0.1 * sim.Port.Forward, sim.Port.Forward, 5);
        Assert.IsNotNull(seq.Failure);
    }

    [Test]
    public void Engine_CoarseThrottle_ResidualReportedAndLimitsWidened()
    {
        // An engine that cannot throttle (on or off): at 2 m/s² a frame is 40 mm/s, so 0.21 m/s
        // ends 30 mm/s past the target.
        var sim = new EngineSim { MinThrottle = 1 };
        var seq = new MainEngineSequence();
        var wanted = 0.21 * sim.Port.Forward;
        Fly(sim, seq, wanted, sim.Port.Forward, 2);
        Assert.AreEqual(1, seq.Burns);
        Assert.Greater(seq.WorstResidual, MainEngineSequence.AssumedDeadband, "the residual says how coarse the engine is");

        var limits = DockLimits.MainEngine(0.9, 0.5, 0, 5, 1.25);
        DockLimits wide = MainEngineSequence.ForDeadband(limits, seq.WorstResidual, out string note);
        Assert.AreEqual(limits.ContactLateral, wide.ContactLateral, "the contact gate never widens");
        Assert.Greater(wide.HoldTolerance, limits.HoldTolerance);
        Assert.GreaterOrEqual(wide.HoldSpeed, 3 * seq.WorstResidual - 1e-12);
        Assert.IsNotNull(note);
        // 30 mm/s: a 10 m coast at 0.3 m/s drifts far more than the 0.25 m the port captures.
        Assert.IsNotNull(MainEngineSequence.DeadbandRefusal(limits, seq.WorstResidual));
    }

    [Test]
    public void ForDeadband_SmallPort_ContactGateKeptAndRefusalAtTheRightPrecision()
    {
        // A 0.625 m port captures within 0.125 m: a 33 s coast allows 2 × 0.125 / 33 = 7.5 mm/s.
        var limits = DockLimits.MainEngine(0.9, 0.5, 0, 5, 0.625);
        Assert.AreEqual(DockLimits.ContactLateralFor(0.625), limits.ContactLateral);
        DockLimits wide = MainEngineSequence.ForDeadband(limits, 0.007, out _);
        Assert.AreEqual(DockLimits.ContactLateralFor(0.625), wide.ContactLateral);
        Assert.Greater(wide.HoldTolerance, limits.HoldTolerance);
        Assert.IsNull(MainEngineSequence.DeadbandRefusal(limits, 0.007));
        Assert.IsNotNull(MainEngineSequence.DeadbandRefusal(limits, 0.008));
        // The assumed precision passes the strictest gate (unknown port size, 0.1 m).
        Assert.IsNull(MainEngineSequence.DeadbandRefusal(DockLimits.MainEngine(0.9, 0.5, 0, 5), MainEngineSequence.AssumedDeadband));
        Assert.IsNull(MainEngineSequence.DeadbandRefusal(DockLimits.Rcs(0.9, 0.5, 0, 5, 0.625), 1.0), "RCS limits are not the engine's");
    }

    // The port attitude follows a target turning at 1°/s (the most the docking accepts): our
    // inertial rate never drops under 0.5°/s, the rate relative to the port attitude does.
    static MainEngineSequence FlyTurningTarget(out EngineSim sim, out Vector3d wantedForward)
    {
        sim = new EngineSim();
        var seq = new MainEngineSequence();
        Vector3d f0 = sim.Port.Forward, spinAxis = sim.Port.Up;
        Vector3d omegaTarget = spinAxis * (1.0 * Deg);
        var wanted = 0.1 * sim.Port.Right;
        wantedForward = f0;
        for (int k = 0; k < (int)(200 / Dt) && seq.Failure == null; k++)
        {
            wantedForward = EngineSim.Rot(f0, spinAxis, 1.0 * Deg * sim.T);
            Vector3d targetAxis = -wantedForward;
            double rateToPort = MainEngineSequence.RelativeRateDeg(sim.Omega, targetAxis, Vector3d.Cross(omegaTarget, targetAxis));
            EngineStep s = seq.Step(sim.T, sim.Vel, wanted, sim.ThrustWorld, sim.Port.Forward, wantedForward,
                sim.RateDeg, sim.MaxAccel, rateToPort);
            Vector3d aimAxis = wantedForward, aimRoll = Vector3d.zero;
            if (s.AimBurn)
                AttitudeAim.BurnPortAxes(sim.Port, sim.ThrustLocal, s.BurnDir, out aimAxis, out aimRoll);
            sim.Apply(s.Throttle, aimAxis, aimRoll);
        }
        return seq;
    }

    [Test]
    public void Engine_TargetTurning1DegPerSec_TurnsBackAndSettles()
    {
        var seq = FlyTurningTarget(out var sim, out var wantedForward);
        Assert.IsNull(seq.Failure);
        Assert.AreEqual(1, seq.Burns);
        Assert.AreEqual(EnginePhase.Idle, seq.Phase);
        Assert.Greater(Vector3d.Dot(sim.Port.Forward, wantedForward), Math.Cos(2 * Deg), "following the port attitude");

        // Following the target, our inertial rate stays at 1°/s: over the 0.5°/s an inertial
        // check needs, which is why the turn back is judged on the relative rate.
        Assert.AreEqual(1.0, sim.RateDeg, 0.05);
    }

    [Test]
    public void RelativeRate_FollowingTheTargetIsZero()
    {
        var a = new Vector3d(0, 0, 1);
        var w = new Vector3d(0.01, 0.02, 0);   // rad/s, perpendicular to a
        Assert.Less(MainEngineSequence.RelativeRateDeg(w, a, Vector3d.Cross(w, a)), 1e-9);
        Assert.AreEqual(w.magnitude * 180 / Math.PI, MainEngineSequence.RelativeRateDeg(w, a, Vector3d.zero), 1e-9);
    }

    [Test]
    public void ForDeadband_AsAssumed_Unchanged()
    {
        var limits = DockLimits.MainEngine(0.9, 0.5, 0, 5, 1.25);
        DockLimits same = MainEngineSequence.ForDeadband(limits, 0.004, out string note);
        Assert.AreEqual(limits.ContactLateral, same.ContactLateral);
        Assert.AreEqual(limits.HoldTolerance, same.HoldTolerance);
        Assert.IsNull(note);
        var rcs = DockLimits.Rcs(0.9, 0.5, 0, 5, 1.25);
        Assert.AreEqual(rcs.ContactLateral, MainEngineSequence.ForDeadband(rcs, 0.05, out _).ContactLateral, "RCS limits are not the engine's");
    }

    [Test]
    public void EngineProbe_MeasuresTheThrustAxisAndPrecision()
    {
        var sim = new EngineSim { ThrustLocal = new Vector3d(0, 0, -1), MaxAccel = 5 };
        var probe = new EngineProbe();
        for (int k = 0; k < 2000 && !probe.Done; k++)
            sim.Apply(probe.Step(sim.T, sim.Vel, sim.Port, sim.MaxAccel), Vector3d.zero, Vector3d.zero);
        Assert.IsTrue(probe.Done);
        Assert.IsTrue(probe.Ok, probe.Refusal);
        Assert.Less((probe.ThrustLocal - new Vector3d(0, 0, -1)).magnitude, 0.01);
        Assert.Less(probe.Residual, MainEngineSequence.AssumedDeadband);
        Assert.AreEqual(EngineProbe.ProbeDv, sim.Vel.magnitude, 0.003);
    }

    [Test]
    public void EngineProbe_NoResponse_Refused()
    {
        var sim = new EngineSim { MaxAccel = 0 };
        var probe = new EngineProbe();
        for (int k = 0; k < 2000 && !probe.Done; k++)
            sim.Apply(probe.Step(sim.T, sim.Vel, sim.Port, 1.0), Vector3d.zero, Vector3d.zero);
        Assert.IsTrue(probe.Done);
        Assert.IsFalse(probe.Ok);
        Assert.IsNotNull(probe.Refusal);
    }

    // ---- mode choice ----

    [Test]
    public void Mode_Choice()
    {
        var f = Frame();
        var full = Probe(f, out _);
        var dead = Probe(f, out _, zDead: true);
        var refused = RcsMapping.FromResponses(Vector3d.zero, Vector3d.zero, Vector3d.zero, f.Right, f.Up, f.Forward, 0.002);

        Assert.AreEqual(TranslateMode.Rcs, TranslateModes.Choose(true, full, false, out string n1));
        Assert.IsNull(n1);
        Assert.AreEqual(TranslateMode.RcsWithEngine, TranslateModes.Choose(true, dead, true, out string n2));
        StringAssert.Contains("Z", n2);
        Assert.AreEqual(TranslateMode.Refused, TranslateModes.Choose(true, dead, false, out string n3));
        Assert.IsNotNull(n3);
        Assert.AreEqual(TranslateMode.MainEngine, TranslateModes.Choose(false, null, true, out string n4));
        Assert.AreEqual(TranslateModes.NoRcsNote, n4);
        Assert.AreEqual(TranslateMode.MainEngine, TranslateModes.Choose(true, refused, true, out string n5));
        StringAssert.Contains("no RCS response", n5);
        Assert.AreEqual(TranslateMode.Refused, TranslateModes.Choose(false, null, false, out string n6));
        Assert.IsNotNull(n6);
    }

    // ---- attitude ----

    [Test]
    public void VesselAxes_KeepThePortToVesselOffset()
    {
        // The port is on the vessel's side: its axis is the vessel's dorsal, its roll the nose.
        var port = Frame();
        Vector3d nose = port.Up, dorsal = port.Forward;
        var wantAxis = new Vector3d(1, 0, 0);
        var wantRoll = new Vector3d(0, 0, 1);
        Assert.IsTrue(AttitudeAim.VesselAxes(nose, dorsal, port, wantAxis, wantRoll, out var wn, out var wd));
        Assert.Less((wd - wantAxis).magnitude, 1e-9);
        Assert.Less((wn - wantRoll).magnitude, 1e-9);

        // Any roll: the current roll is kept as far as the new axis allows.
        Assert.IsTrue(AttitudeAim.VesselAxes(nose, dorsal, port, port.Forward, Vector3d.zero, out wn, out wd));
        Assert.Less((wn - nose).magnitude, 1e-9);
    }

    [Test]
    public void BurnPortAxes_PutTheThrustOnTheBurn()
    {
        var port = Frame();
        var thrustLocal = new Vector3d(0, 0, -1);
        var burn = new Vector3d(0.3, -0.4, 0.5).normalized;
        AttitudeAim.BurnPortAxes(port, thrustLocal, burn, out var axis, out var roll);
        var after = PortFrame.From(axis, roll);
        Assert.Less((after.ToWorld(thrustLocal) - burn).magnitude, 1e-9);

        // Opposite: a half turn, still well defined.
        AttitudeAim.BurnPortAxes(port, thrustLocal, port.Forward, out axis, out roll);
        after = PortFrame.From(axis, roll);
        Assert.IsTrue(after.Ok);
        Assert.Less((after.ToWorld(thrustLocal) - port.Forward).magnitude, 1e-9);
    }

    [Test]
    public void LockRotation_MapsLocalAxesToNoseAndDorsal()
    {
        var nose = new Vector3d(0.2, 0.9, -0.3).normalized;
        var dorsal = Vector3d.Cross(nose, new Vector3d(1, 0, 0)).normalized;
        QuaternionD q = AttitudeAim.RotationFromNoseAndDorsal(nose, dorsal);
        Assert.Less((q * new Vector3d(0, 1, 0) - nose).magnitude, 1e-9);
        Assert.Less((q * new Vector3d(0, 0, 1) - dorsal).magnitude, 1e-9);
    }
}

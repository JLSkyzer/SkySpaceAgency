using System;
using System.Collections.Generic;
using K2D2.Rendezvous;
using KSP.Sim;
using NUnit.Framework;

// The guidance flown in DockSim (see its header for what the simulation models and leaves out).
// The capture values are test values, not read from a game part.
public class DockingGuidanceTests
{
    const double Deg = Math.PI / 180;
    const double FwdDot = 0.995;   // ~5.7°
    const double MaxRvel = 0.5;

    static DockLimits Rcs(double radius = 3) => DockLimits.Rcs(FwdDot, MaxRvel, 0, radius);
    static DockLimits Main(double radius = 3) => DockLimits.MainEngine(0.98, MaxRvel, 0, radius);

    class Trace
    {
        public bool Docked, Aborted;
        public int Steps;
        public double ContactLateral = double.NaN, ContactClosing = double.NaN, ContactFwdDot = double.NaN;
        public double MaxConeExcess = double.NegativeInfinity;   // lateral - allowed, in Final
        public double MinKeepOutDistance = double.PositiveInfinity;
        public readonly HashSet<DockPhase> Phases = new HashSet<DockPhase>();
        public string Reason;
        public DockPhase Last;
        public double LastAxial, LastLateral;
    }

    static Trace Fly(DockSim sim, DockingGuidance g, double maxSeconds, Action<DockSim, DockCommand> each = null)
    {
        var tr = new Trace();
        double tanCone = Math.Tan(g.Limits.ConeHalfAngleDeg * Deg);
        double keepOut = g.Limits.TargetRadius + 10;
        while (sim.T < maxSeconds)
        {
            var c = g.Step(sim.State(), sim.Dt);
            tr.Steps++;
            tr.Phases.Add(c.Phase);
            tr.Last = c.Phase;
            AssertFinite(c);
            sim.Truth(out double ax, out double lat, out double closing);
            tr.LastAxial = ax;
            tr.LastLateral = lat;
            if (c.Phase == DockPhase.Final)
                tr.MaxConeExcess = Math.Max(tr.MaxConeExcess, lat - (tanCone * Math.Max(ax, 0) + g.Limits.LateralMargin));
            // Outside the approach corridor (in front of the port, within the cone), stay out of the
            // keep-out sphere around the target's centre.
            bool inCorridor = ax > 0 && lat <= tanCone * ax + g.Limits.LateralMargin + 1;
            if (!inCorridor)
                tr.MinKeepOutDistance = Math.Min(tr.MinKeepOutDistance, sim.Pos.magnitude);
            if (c.Phase == DockPhase.Docked)
            {
                tr.Docked = true;
                tr.ContactLateral = lat;
                tr.ContactClosing = closing;
                tr.ContactFwdDot = sim.FwdDot;
                return tr;
            }
            if (c.Phase == DockPhase.Aborted)
            {
                tr.Aborted = true;
                g.AbortNeeded(out tr.Reason);
                return tr;
            }
            each?.Invoke(sim, c);
            sim.Apply(c);
        }
        return tr;
    }

    static void AssertFinite(DockCommand c)
    {
        foreach (var v in new[] { c.WantedRelVel, c.Forward, c.Up, c.AimPoint })
            Assert.IsTrue(Finite(v), $"non-finite command {v} in {c.Phase}");
    }

    static bool Finite(Vector3d v) => !double.IsNaN(v.x + v.y + v.z) && !double.IsInfinity(v.x + v.y + v.z);

    static void AssertCaptured(Trace tr, DockLimits l, double fwdDot)
    {
        Assert.IsTrue(tr.Docked, $"not docked: {tr.Last} at {tr.LastAxial:F1} m, lateral {tr.LastLateral:F1} m, {tr.Reason}");
        Assert.LessOrEqual(tr.ContactLateral, l.LateralMargin);
        Assert.LessOrEqual(tr.ContactClosing, MaxRvel);
        Assert.Greater(tr.ContactClosing, 0.05);
        Assert.GreaterOrEqual(tr.ContactFwdDot, fwdDot);
        Assert.LessOrEqual(tr.MaxConeExcess, 0, "left the cone in the final approach");
    }

    [Test]
    public void Rcs_FromOffAxis50m_DocksInsideCone()
    {
        // 50 m from the port face, 37° off its axis, drifting sideways at 0.2 m/s.
        var sim = new DockSim { OurAxis = new Vector3d(1, 0, 0) };
        sim.Pos = sim.Face + new Vector3d(30, 0, 40);
        sim.Vel = new Vector3d(0, 0.2, 0);
        var g = new DockingGuidance(Rcs());
        var tr = Fly(sim, g, 3000);
        AssertCaptured(tr, g.Limits, FwdDot);
        Assert.AreEqual(0, g.Aborts);
        foreach (var p in new[] { DockPhase.Align, DockPhase.Hold50, DockPhase.Approach10, DockPhase.Hold10, DockPhase.Final })
            Assert.IsTrue(tr.Phases.Contains(p), $"never in {p}");
        Assert.IsFalse(g.AbortNeeded(out _));
    }

    [Test]
    public void Rcs_TargetRotating_TracksAxis()
    {
        // The target port axis turns at 1°/s (the refusal limit), the port 3 m from the centre.
        var sim = new DockSim { SpinDegPerSec = 1, OurAxis = new Vector3d(0, 0, -1) };
        sim.Pos = sim.Face + new Vector3d(20, 5, 45);
        var g = new DockingGuidance(Rcs());
        var tr = Fly(sim, g, 3000);
        AssertCaptured(tr, g.Limits, FwdDot);
        Assert.AreEqual(0, g.Aborts);
        // The axis really turned during the approach: several tens of degrees at least.
        Assert.Greater(sim.T, 60);
    }

    [Test]
    public void PortOnFarSide_RoutesAroundKeepOut()
    {
        // We start 40 m behind the target's centre, on the port axis: the straight line to the hold
        // point goes through the target. Target radius 5 m: keep-out radius 15 m.
        var sim = new DockSim { PortOffset = 5, OurAxis = new Vector3d(0, 0, 1) };
        sim.Pos = new Vector3d(0, 0, -40);
        var g = new DockingGuidance(Rcs(5));
        var tr = Fly(sim, g, 4000);
        AssertCaptured(tr, g.Limits, FwdDot);
        Assert.GreaterOrEqual(tr.MinKeepOutDistance, 15 - 0.5, "went inside the keep-out sphere");

        // The same from closer: about 20 m behind the centre, slightly off the axis.
        var sim2 = new DockSim { PortOffset = 5, OurAxis = new Vector3d(0, 0, 1) };
        sim2.Pos = new Vector3d(3, -2, -20);
        var g2 = new DockingGuidance(Rcs(5));
        var tr2 = Fly(sim2, g2, 4000);
        AssertCaptured(tr2, g2.Limits, FwdDot);
        Assert.GreaterOrEqual(tr2.MinKeepOutDistance, 15 - 0.5);
    }

    static void KickWhenFinal(DockSim sim, DockCommand c, ref int kicks, int maxKicks)
    {
        sim.Truth(out double ax, out _, out _);
        if (kicks < maxKicks && c.Phase == DockPhase.Final && ax < 8)
        {
            // A sudden 1 m/s push toward the port (a stuck thruster, say).
            sim.PendingKick = -1.0 * sim.Axis;
            kicks++;
        }
    }

    [Test]
    public void TooFastClosing_Aborts_BacksOff_RetriesOnce()
    {
        var sim = new DockSim { OurAxis = new Vector3d(0, 0, -1) };
        sim.Pos = sim.Face + new Vector3d(0, 0, 50);
        var g = new DockingGuidance(Rcs());
        int kicks = 0;
        double minAxialAfterAbort = double.PositiveInfinity, maxBackoff = 0;
        var tr = Fly(sim, g, 4000, (s, c) =>
        {
            KickWhenFinal(s, c, ref kicks, 1);
            s.Truth(out double ax, out _, out _);
            if (c.Phase == DockPhase.Backoff)
            {
                minAxialAfterAbort = Math.Min(minAxialAfterAbort, ax);
                maxBackoff = Math.Max(maxBackoff, ax);
            }
        });
        Assert.AreEqual(1, kicks);
        Assert.AreEqual(1, g.Aborts);
        StringAssert.Contains("closing", g.LastAbortReason);
        Assert.IsTrue(tr.Phases.Contains(DockPhase.Backoff));
        Assert.Greater(minAxialAfterAbort, 0.5, "hit the port while backing off");
        Assert.AreEqual(20, maxBackoff, 2.0, "backs off to 20 m");
        AssertCaptured(tr, g.Limits, FwdDot);
    }

    [Test]
    public void SecondAbort_Fails()
    {
        var sim = new DockSim { OurAxis = new Vector3d(0, 0, -1) };
        sim.Pos = sim.Face + new Vector3d(0, 0, 50);
        var g = new DockingGuidance(Rcs());
        int kicks = 0;
        var tr = Fly(sim, g, 6000, (s, c) => KickWhenFinal(s, c, ref kicks, 2));
        Assert.AreEqual(2, kicks);
        Assert.IsTrue(tr.Aborted);
        Assert.IsFalse(tr.Docked);
        Assert.IsTrue(g.AbortNeeded(out string reason));
        StringAssert.Contains("closing", reason);
        // Once aborted, it stays aborted and keeps station.
        var c2 = g.Step(sim.State(), sim.Dt);
        Assert.AreEqual(DockPhase.Aborted, c2.Phase);
    }

    [Test]
    public void LateralOutsideCone_Aborts()
    {
        var g = new DockingGuidance(Rcs());
        var sim = new DockSim { OurAxis = new Vector3d(0, 0, -1) };
        sim.Pos = sim.Face + new Vector3d(0, 0, 50);
        // Fly until the approach starts, then jump sideways (as after a bad state read).
        int jumped = 0;
        var tr = Fly(sim, g, 3000, (s, c) =>
        {
            if (jumped == 0 && c.Phase == DockPhase.Approach10)
            {
                s.Pos += new Vector3d(12, 0, 0);
                jumped++;
            }
        });
        Assert.AreEqual(1, g.Aborts);
        StringAssert.Contains("cone", g.LastAbortReason);
        AssertCaptured(tr, g.Limits, FwdDot);
    }

    [Test]
    public void MainEngine_FromOffAxis_DocksWithWiderTolerance()
    {
        var rcs = Rcs();
        var main = Main();
        Assert.AreEqual(100, main.HoldFar);
        Assert.AreEqual(20, main.HoldNear);
        Assert.AreEqual(0.3, main.FinalSpeedMax, 1e-9);
        Assert.Greater(main.ConeHalfAngleDeg, rcs.ConeHalfAngleDeg);
        Assert.Greater(main.LateralMargin, rcs.LateralMargin);
        Assert.IsTrue(main.MainEngineMode);

        var sim = new DockSim { MainEngine = true, OurAxis = new Vector3d(-1, 0, 0) };
        sim.Pos = sim.Face + new Vector3d(-60, 10, 80);
        var g = new DockingGuidance(main);
        var tr = Fly(sim, g, 8000);
        AssertCaptured(tr, main, 0.98);
        Assert.AreEqual(0, g.Aborts, g.LastAbortReason);
        Assert.LessOrEqual(tr.ContactClosing, 0.3 + 0.1);
    }

    [Test]
    public void Roll_RequiredOnlyWhenMinRollDot()
    {
        var a = new Vector3d(0, 0, 1);
        var roll = new Vector3d(0, 1, 0);
        var s = new DockState { RelPos = 50 * a, TargetAxis = a, TargetRoll = roll, OurAxis = -a, TargetCenter = -3 * a };
        var free = new DockingGuidance(Rcs()).Step(s, 0.1);
        Assert.AreEqual(0, free.Up.magnitude, 1e-12);
        Assert.AreEqual(-1, Vector3d.Dot(free.Forward, a), 1e-12);
        var keyed = new DockingGuidance(DockLimits.Rcs(FwdDot, MaxRvel, 0.9, 3)).Step(s, 0.1);
        Assert.AreEqual(1, Vector3d.Dot(keyed.Up, roll), 1e-12);
        // Roll not matched: the hold at 50 m does not end.
        var g = new DockingGuidance(DockLimits.Rcs(FwdDot, MaxRvel, 0.9, 3));
        s.OurRoll = new Vector3d(1, 0, 0);
        DockCommand c = default;
        for (int i = 0; i < 1000; i++) c = g.Step(s, 0.1);
        Assert.AreEqual(DockPhase.Hold50, c.Phase);
        s.OurRoll = roll;
        for (int i = 0; i < 100; i++) c = g.Step(s, 0.1);
        Assert.AreEqual(DockPhase.Approach10, c.Phase);
    }

    [Test]
    public void NeverCommandsNaN()
    {
        var rnd = new Random(42);
        double R(double scale) => (rnd.NextDouble() * 2 - 1) * scale;
        Vector3d V(double scale) => new Vector3d(R(scale), R(scale), R(scale));
        var special = new[]
        {
            Vector3d.zero, new Vector3d(double.NaN, 0, 0), new Vector3d(double.PositiveInfinity, 1, 0),
            new Vector3d(0, 0, 1), new Vector3d(0, 0, -1), new Vector3d(1e-300, 0, 0), new Vector3d(1e12, 0, 0),
        };
        Vector3d Pick(double scale) => rnd.Next(4) == 0 ? special[rnd.Next(special.Length)] : V(scale);

        foreach (var limits in new[] { Rcs(), Main(), DockLimits.Rcs(0.99, 0.3, 0.9, 0), default })
        {
            var g = new DockingGuidance(limits);
            for (int i = 0; i < 5000; i++)
            {
                if (rnd.Next(500) == 0) g = new DockingGuidance(limits);
                var a = Pick(1);
                var s = new DockState
                {
                    RelPos = Pick(60),
                    RelVel = Pick(3),
                    TargetAxis = a,
                    TargetAxisRate = Pick(0.05),
                    TargetCenter = rnd.Next(3) == 0 ? Vector3d.zero : Pick(10),
                    OurAxis = Pick(1),
                    OurRoll = Pick(1),
                    TargetRoll = Pick(1),
                    Time = i * 0.1,
                };
                // The degenerate geometry of the routing: exactly behind the centre, on the axis.
                if (i % 97 == 0) { s.TargetAxis = new Vector3d(0, 0, 1); s.TargetCenter = new Vector3d(0, 0, -3); s.RelPos = new Vector3d(0, 0, -30); }
                double dt = rnd.Next(10) == 0 ? (rnd.Next(2) == 0 ? 0 : double.NaN) : 0.1;
                AssertFinite(g.Step(s, dt));
                g.AbortNeeded(out _);
            }
        }
    }
}

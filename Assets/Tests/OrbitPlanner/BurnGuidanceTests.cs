using System;
using K2D2.Node;
using K2D2.OrbitPlanning;
using K2D2.Transfer;
using KSP.Sim;
using NUnit.Framework;

public class BurnGuidanceTests
{
    // EXTERNAL DATA (KSP wiki, stock values): Kerbin.
    const double Mu = 3.5316e12;
    const double R = 600000;
    const double Deg = Math.PI / 180;
    static readonly Vector3d North = new Vector3d(0, 0, 1);

    static void AssertVector(Vector3d expected, Vector3d actual, double tolerance, string what)
    {
        Assert.That((actual - expected).magnitude, Is.LessThan(tolerance),
            $"{what}: expected ({expected.x}, {expected.y}, {expected.z}), got ({actual.x}, {actual.y}, {actual.z})");
    }

    // A state on an outgoing hyperbola past the periapsis (rp = R + 100 km, v∞ = 1000 m/s).
    static void HyperbolicState(out Vector3d r, out Vector3d v)
    {
        double rp = R + 100000;
        double vInf = 1000;
        double vp = Math.Sqrt(vInf * vInf + 2 * Mu / rp);
        Vector3d r0 = new Vector3d(rp, 0, 0);
        Vector3d v0 = new Vector3d(0, vp * Math.Cos(20 * Deg), vp * Math.Sin(20 * Deg));
        KSP2FlightAssistant.MathLibrary.KeplerPropagator.Propagate(r0, v0, Mu, 600, out r, out v);
    }

    [Test]
    public void AsymptoteGuidance_ZeroWhenOnTheHyperbola()
    {
        HyperbolicState(out Vector3d r, out Vector3d v);
        Vector3d vInf = Hyperbola.Asymptote(r, v, Mu);
        Assert.That(vInf.magnitude, Is.EqualTo(1000).Within(1e-6 * 1000));

        var guidance = new AsymptoteGuidance(vInf);
        Vector3d dv = guidance.DeltaVToGo(r, v, 0, Mu);
        Assert.That(dv.magnitude, Is.LessThan(1e-3), $"dv {dv.magnitude} m/s on the hyperbola");
        StringAssert.Contains("1000.0", guidance.Describe());
    }

    [Test]
    public void AsymptoteGuidance_TakesTheBranchClosestToTheVelocity()
    {
        HyperbolicState(out Vector3d r, out Vector3d v);
        Vector3d vInf = Hyperbola.Asymptote(r, v, Mu);
        Assert.IsTrue(Hyperbola.VelocityForAsymptote(r, vInf, Mu, out Vector3d vA, out Vector3d vB));

        var guidance = new AsymptoteGuidance(vInf);
        // Slightly off each branch: the guidance goes to the nearer one.
        Vector3d offset = new Vector3d(3, -2, 1);
        AssertVector(vA - (vA + offset), guidance.DeltaVToGo(r, vA + offset, 0, Mu), 1e-6, "near vA");
        AssertVector(vB - (vB + offset), guidance.DeltaVToGo(r, vB + offset, 0, Mu), 1e-6, "near vB");
    }

    [Test]
    public void AsymptoteGuidance_NaNWhenNoHyperbolaPasses()
    {
        var r = new Vector3d(R + 100000, 0, 0);
        var guidance = new AsymptoteGuidance(new Vector3d(500, 0, 0));   // along r: no hyperbola
        Vector3d dv = guidance.DeltaVToGo(r, new Vector3d(0, 2300, 0), 0, Mu);
        Assert.IsTrue(double.IsNaN(dv.magnitude));
    }

    [Test]
    public void FixedImpulse_DecreasesAsVelocityChanges()
    {
        var v0 = new Vector3d(0, 2246, 0);
        var deltaV = new Vector3d(12, 850, -30);
        var r = new Vector3d(R + 100000, 0, 0);
        var guidance = new FixedImpulseGuidance(deltaV, v0);

        AssertVector(deltaV, guidance.DeltaVToGo(r, v0, 0, Mu), 1e-9, "at ignition");
        double last = double.PositiveInfinity;
        for (int i = 1; i <= 10; i++)
        {
            Vector3d v = v0 + (i / 10.0) * deltaV;
            Vector3d dv = guidance.DeltaVToGo(r, v, i, Mu);
            Assert.That(dv.magnitude, Is.LessThan(last), $"step {i}");
            AssertVector((v0 + deltaV) - v, dv, 1e-9, $"step {i}");
            last = dv.magnitude;
        }
        Assert.That(last, Is.LessThan(1e-9));
    }

    [Test]
    public void FixedImpulse_FixedInInertialSpaceAtIgnition()
    {
        var deltaV = new Vector3d(0, 100, 0);
        var r = new Vector3d(R + 100000, 0, 0);
        // Without a known velocity: the impulse itself until ignition, whatever the velocity.
        var guidance = new FixedImpulseGuidance(deltaV);
        AssertVector(deltaV, guidance.DeltaVToGo(r, new Vector3d(0, 2246, 0), 0, Mu), 1e-9, "before ignition");
        AssertVector(deltaV, guidance.DeltaVToGo(r, new Vector3d(-50, 2240, 0), 10, Mu), 1e-9, "before ignition, later");

        // At ignition the velocity is fixed: the target velocity no longer moves.
        var vIgnition = new Vector3d(-50, 2240, 0);
        guidance.FixAtIgnition(r, vIgnition, 10, Mu);
        AssertVector(deltaV, guidance.DeltaVToGo(r, vIgnition, 10, Mu), 1e-9, "at ignition");
        // During the burn: 40 m/s applied by the engines on top of the 2 s coast.
        KSP2FlightAssistant.MathLibrary.KeplerPropagator.Propagate(r, vIgnition, Mu, 2, out Vector3d rLater, out Vector3d vCoast);
        var later = vCoast + new Vector3d(0, 40, 0);
        AssertVector(new Vector3d(0, 60, 0), guidance.DeltaVToGo(rLater, later, 12, Mu), 1e-6, "during the burn");

        // A known velocity is replaced by the one measured at the real ignition.
        var known = new FixedImpulseGuidance(deltaV, new Vector3d(0, 2246, 0));
        known.FixAtIgnition(r, vIgnition, 10, Mu);
        AssertVector(deltaV, known.DeltaVToGo(r, vIgnition, 10, Mu), 1e-9, "re-fixed at ignition");
    }

    [Test]
    public void FixedImpulse_GravityDuringTheBurnIsNotMissingDeltaV()
    {
        // The in-game case of 2026-10-30: a 6 m/s correction 885 km from Kerbin's centre, near the
        // periapsis of an e = 0.89 orbit. Gravity there is ~4.5 m/s²; a burn that stalls (vessel
        // re-pointing, no thrust) must keep the same Δv to go, not grow with gravity.
        var r0 = new Vector3d(885520, 0, 0);
        var v0 = new Vector3d(0, 2723.8, 0);
        var deltaV = new Vector3d(5.6, 2.3, 0);
        var guidance = new FixedImpulseGuidance(deltaV);
        guidance.FixAtIgnition(r0, v0, 100, Mu);

        for (int s = 1; s <= 21; s += 5)
        {
            KSP2FlightAssistant.MathLibrary.KeplerPropagator.Propagate(r0, v0, Mu, s, out Vector3d r, out Vector3d v);
            AssertVector(deltaV, guidance.DeltaVToGo(r, v, 100 + s, Mu), 1e-6, $"coasting {s} s, no thrust");
        }

        // Half the impulse applied by the engines after 3 s: half left.
        KSP2FlightAssistant.MathLibrary.KeplerPropagator.Propagate(r0, v0, Mu, 3, out Vector3d r3, out Vector3d v3);
        AssertVector(0.5 * deltaV, guidance.DeltaVToGo(r3, v3 + 0.5 * deltaV, 103, Mu), 1e-6, "half applied");
    }

    [Test]
    public void OrbitTargetGuidance_SameAsBefore()
    {
        var target = new TargetOrbit { Ra = R + 300000, Rp = R + 80000, PlaneNormal = new Vector3d(0, -Math.Sin(6 * Deg), Math.Cos(6 * Deg)) };
        var states = new[]
        {
            (new Vector3d(R + 80000, 0, 0), new Vector3d(0, 2280, 0)),
            (new Vector3d(R + 150000, 20000, -3000), new Vector3d(-150, 2100, 40)),
            (new Vector3d(0, -(R + 250000), 0), new Vector3d(1900, 30, -12)),
        };
        foreach (var (r, v) in states)
        {
            // Before ignition: the radial sign from the current state, as before (radial_sign 0).
            var guidance = new OrbitTargetGuidance(target);
            AssertVector(OrbitGuidance.DeltaVToOrbit(r, v, Mu, target, 0), guidance.DeltaVToGo(r, v, 0, Mu), 1e-9, "before ignition");

            // From ignition: the sign fixed from the ignition state, as TargetOrbitBurn did.
            guidance.FixAtIgnition(r, v, 0, Mu);
            int sign = OrbitGuidance.RadialSign(r, v, target.Ra, target.Rp);
            Assert.AreEqual(sign, guidance.RadialSign);
            Vector3d rLater = r + 5.0 * v;
            Vector3d vLater = v + new Vector3d(0, 0, 3) - (sign > 0 ? 400.0 : -400.0) * r.normalized; // radial speed flips
            AssertVector(OrbitGuidance.DeltaVToOrbit(rLater, vLater, Mu, target, sign), guidance.DeltaVToGo(rLater, vLater, 5, Mu),
                1e-9, "after ignition");
        }
        StringAssert.Contains("ra", new OrbitTargetGuidance(target).Describe());
    }

    [Test]
    public void OrbitTargetGuidance_OldValues()
    {
        // Values of OrbitGuidance.DeltaVToOrbit before the refactor: circular 100 km from an
        // equatorial 80 × 100 km orbit at its periapsis, Kerbin.
        double rp = R + 80000, ra = R + 100000;
        double vp = Math.Sqrt(Mu * (2 / rp - 2 / (rp + ra)));
        var r = new Vector3d(rp, 0, 0);
        var v = new Vector3d(0, vp, 0);
        var target = new TargetOrbit { Ra = R + 100000, Rp = R + 100000, PlaneNormal = North };
        var guidance = new OrbitTargetGuidance(target);
        // At the periapsis below the target: the target orbit is widened to rp' = |r|, ra' = ra,
        // the same orbit as now: about zero.
        Assert.That(guidance.DeltaVToGo(r, v, 0, Mu).magnitude, Is.LessThan(1e-6));
    }

    // Staging during the burn: thrust gone for 0.5 s, at most one staging per second.

    [Test]
    public void DryTimer_StagesAfterHalfASecondOfNoThrust()
    {
        var timer = new DryStageTimer();
        Assert.IsFalse(timer.ShouldStage(100.0, dry: true, deltaVLeft: true));
        Assert.IsFalse(timer.ShouldStage(100.3, dry: true, deltaVLeft: true));
        Assert.IsTrue(timer.ShouldStage(100.5, dry: true, deltaVLeft: true));
    }

    [Test]
    public void DryTimer_ThrustBackOrNothingLeft_Resets()
    {
        var timer = new DryStageTimer();
        timer.ShouldStage(100.0, true, true);
        Assert.IsFalse(timer.ShouldStage(100.4, dry: false, deltaVLeft: true));
        Assert.IsFalse(timer.ShouldStage(100.6, true, true), "the timer restarted at 100.6");
        Assert.IsFalse(timer.ShouldStage(101.0, true, true));
        Assert.IsTrue(timer.ShouldStage(101.1, true, true));
        Assert.IsFalse(timer.ShouldStage(101.2, dry: true, deltaVLeft: false));
        Assert.IsFalse(timer.ShouldStage(101.3, true, true));
        // Time going back (a quickload) restarts the wait.
        var back = new DryStageTimer();
        back.ShouldStage(200.0, true, true);
        Assert.IsFalse(back.ShouldStage(150.0, true, true));
        Assert.IsFalse(back.ShouldStage(150.4, true, true));
        Assert.IsTrue(back.ShouldStage(150.5, true, true));
    }

    [Test]
    public void DryTimer_NeverStagesTwiceWithinOneSecond()
    {
        var timer = new DryStageTimer();
        timer.ShouldStage(100.0, true, true);
        Assert.IsTrue(timer.ShouldStage(100.5, true, true));
        timer.Staged(100.5);
        Assert.IsFalse(timer.ShouldStage(100.6, true, true));
        Assert.IsFalse(timer.ShouldStage(101.1, true, true), "dry for 0.5 s again, but only 0.6 s since the staging");
        Assert.IsFalse(timer.ShouldStage(101.4, true, true));
        Assert.IsTrue(timer.ShouldStage(101.5, true, true));
    }

    // The burn not updated for 1 s (the game's auto-staging holds the pilots' updates while it
    // stages): the dry time before the gap does not count, so the mod does not stage right after.
    [Test]
    public void DryTimer_GapInTheUpdates_RestartsTheWait()
    {
        var timer = new DryStageTimer();
        Assert.IsFalse(timer.ShouldStage(100.0, true, true));
        Assert.IsFalse(timer.ShouldStage(100.2, true, true));
        Assert.IsFalse(timer.ShouldStage(101.2, true, true), "1 s without an update: the wait restarts at 101.2");
        Assert.IsFalse(timer.ShouldStage(101.5, true, true));
        Assert.IsTrue(timer.ShouldStage(101.7, true, true));
    }

    [Test]
    public void IsDry_ThrustCommandedButNoneMeasured()
    {
        Assert.IsTrue(DryStageTimer.IsDry(throttle: 1, expectedAccel: 5, measuredAccel: 0));
        Assert.IsFalse(DryStageTimer.IsDry(1, 5, 4.9));
        // A low throttle on a weak vessel still pushes: not dry.
        Assert.IsFalse(DryStageTimer.IsDry(0.05, 1, 0.05));
        Assert.IsTrue(DryStageTimer.IsDry(0.05, 1, 0));
        // No throttle commanded or no engine at all: nothing to say.
        Assert.IsFalse(DryStageTimer.IsDry(0, 5, 0));
        Assert.IsFalse(DryStageTimer.IsDry(1, 0, 0));
        Assert.IsFalse(DryStageTimer.IsDry(1, double.NaN, 0));
    }
}

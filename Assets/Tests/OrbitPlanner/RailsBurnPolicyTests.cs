using System;
using K2D2.Node;
using KSP.Sim;
using NUnit.Framework;

public class RailsBurnPolicyTests
{
    // The game's first warp levels (1x, x2, x4, x10, x50, ...).
    static readonly float[] Rates = { 1, 2, 4, 10, 50, 100, 1000 };

    // A long, steady, aligned burn in physics warp, nothing in the way: eligible for rails warp.
    static RailsInputs Eligible()
    {
        return new RailsInputs
        {
            BurnLeftGameSeconds = 1000,
            StageBurnLeftSeconds = 2000,
            AlignedSteadyRealSeconds = 2,
            AimDriftDegAtExit = 1,
            RailsAllowed = true,
            InAtmosphereOrEnvelope = false,
            NearTarget = false,
            CurrentlyOnRails = false,
            GuardOk = null,
            FramesSinceRailsEntry = 0,
            Rates = Rates,
            RealFrameSeconds = 0.02,
            CurrentIndex = 2,
            RefusedBefore = false,
        };
    }

    // On rails at index (x10 or x50), guard confirmed, far from every exit.
    static RailsInputs OnRails(int index)
    {
        RailsInputs i = Eligible();
        i.CurrentlyOnRails = true;
        i.CurrentIndex = index;
        i.GuardOk = true;
        i.FramesSinceRailsEntry = 100;
        return i;
    }

    [Test]
    public void ShortBurn_StaysPhysics()
    {
        RailsInputs i = Eligible();
        i.BurnLeftGameSeconds = 120;   // not more than 120 s
        RailsStep step = RailsBurnPolicy.Decide(i);
        Assert.That(step.Action, Is.EqualTo(RailsAction.Stay));
        Assert.That(step.Reason, Does.Contain("120"));
    }

    [Test]
    public void LongSteadyBurn_EntersRailsAtHighestSafeRate()
    {
        // 1000 s left, 15 s margin: 985 s to the planned exit, 19.7 s real at x50: x50.
        RailsStep step = RailsBurnPolicy.Decide(Eligible());
        Assert.That(step.Action, Is.EqualTo(RailsAction.EnterRails));
        Assert.That(step.WantedIndex, Is.EqualTo(4));

        // Also from 1x.
        RailsInputs i = Eligible();
        i.CurrentIndex = 0;
        Assert.That(RailsBurnPolicy.Decide(i).Action, Is.EqualTo(RailsAction.EnterRails));
    }

    [Test]
    public void NotSteadyYet_Waits()
    {
        RailsInputs i = Eligible();
        i.AlignedSteadyRealSeconds = 0.5;
        RailsStep step = RailsBurnPolicy.Decide(i);
        Assert.That(step.Action, Is.EqualTo(RailsAction.Stay));
        Assert.That(step.Reason, Does.Contain("steady"));

        i.AlignedSteadyRealSeconds = 1.0;
        Assert.That(RailsBurnPolicy.Decide(i).Action, Is.EqualTo(RailsAction.EnterRails));
    }

    [Test]
    public void AimDriftTooLarge_StaysPhysics()
    {
        RailsInputs i = Eligible();
        i.AimDriftDegAtExit = 5;
        RailsStep step = RailsBurnPolicy.Decide(i);
        Assert.That(step.Action, Is.EqualTo(RailsAction.Stay));
        Assert.That(step.Reason, Does.Contain("drift"));

        i.AimDriftDegAtExit = double.NaN;   // unknown: no rails
        Assert.That(RailsBurnPolicy.Decide(i).Action, Is.EqualTo(RailsAction.Stay));

        // On rails, the aim moving 5° away from the fixed burn direction ends the rails stretch.
        RailsInputs r = OnRails(4);
        r.AimDriftDegAtExit = 6;
        Assert.That(RailsBurnPolicy.Decide(r).Action, Is.EqualTo(RailsAction.LeaveRails));
    }

    [Test]
    public void LandingFlag_NeverRails()
    {
        RailsInputs i = Eligible();
        i.RailsAllowed = false;
        Assert.That(RailsBurnPolicy.Decide(i).Action, Is.EqualTo(RailsAction.Stay));

        RailsInputs r = OnRails(3);
        r.RailsAllowed = false;
        Assert.That(RailsBurnPolicy.Decide(r).Action, Is.EqualTo(RailsAction.LeaveRails));
    }

    [Test]
    public void Atmosphere_NeverRails()
    {
        RailsInputs i = Eligible();
        i.InAtmosphereOrEnvelope = true;
        RailsStep step = RailsBurnPolicy.Decide(i);
        Assert.That(step.Action, Is.EqualTo(RailsAction.Stay));
        Assert.That(step.Reason, Does.Contain("atmosphere"));

        RailsInputs r = OnRails(4);
        r.InAtmosphereOrEnvelope = true;
        Assert.That(RailsBurnPolicy.Decide(r).Action, Is.EqualTo(RailsAction.LeaveRails));

        // The game's envelope: max(atmosphere if any, highest terrain) + 500 m.
        Assert.That(RailsBurnPolicy.EnvelopeAltitude(true, 70000, 6700), Is.EqualTo(70500));
        Assert.That(RailsBurnPolicy.EnvelopeAltitude(false, 70000, 6700), Is.EqualTo(7200));
    }

    [Test]
    public void NearTarget_NeverRails()
    {
        RailsInputs i = Eligible();
        i.NearTarget = true;
        RailsStep step = RailsBurnPolicy.Decide(i);
        Assert.That(step.Action, Is.EqualTo(RailsAction.Stay));
        Assert.That(step.Reason, Does.Contain("target"));

        RailsInputs r = OnRails(4);
        r.NearTarget = true;
        Assert.That(RailsBurnPolicy.Decide(r).Action, Is.EqualTo(RailsAction.LeaveRails));
    }

    [Test]
    public void GuardFailsWithin2Frames_DropsAndNeverRetries()
    {
        // Frame 1 after entering: the game may not have switched yet, a failed check waits.
        RailsInputs r = OnRails(4);
        r.GuardOk = false;
        r.FramesSinceRailsEntry = 1;
        RailsStep step = RailsBurnPolicy.Decide(r);
        Assert.That(step.Action, Is.EqualTo(RailsAction.Stay));
        Assert.That(step.WantedIndex, Is.EqualTo(4));

        // Frame 2: still not confirmed (failed or never read): drop to 1x.
        r.FramesSinceRailsEntry = 2;
        step = RailsBurnPolicy.Decide(r);
        Assert.That(step.Action, Is.EqualTo(RailsAction.DropGuard));
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        r.GuardOk = null;
        Assert.That(RailsBurnPolicy.Decide(r).Action, Is.EqualTo(RailsAction.DropGuard));

        // A guard confirmed then lost (the game ended its burn) drops too.
        RailsInputs lost = OnRails(4);
        lost.GuardOk = false;
        Assert.That(RailsBurnPolicy.Decide(lost).Action, Is.EqualTo(RailsAction.DropGuard));

        // Never again for this burn, even when everything else allows it.
        RailsInputs again = Eligible();
        again.RefusedBefore = true;
        step = RailsBurnPolicy.Decide(again);
        Assert.That(step.Action, Is.EqualTo(RailsAction.Stay));
        Assert.That(step.Reason, Does.Contain("refused"));
    }

    [Test]
    public void ExitBeforeFifteenSecondsOfBurn()
    {
        // x50 at 0.02 s frames: 1 s of game time per frame. 16.5 s left: the next frame keeps
        // more than 15 s, stay.
        RailsInputs r = OnRails(4);
        r.BurnLeftGameSeconds = 16.5;
        Assert.That(RailsBurnPolicy.Decide(r).Action, Is.EqualTo(RailsAction.Stay));

        // 15.9 s left: the next frame would go under 15 s, leave now.
        r.BurnLeftGameSeconds = 15.9;
        RailsStep step = RailsBurnPolicy.Decide(r);
        Assert.That(step.Action, Is.EqualTo(RailsAction.LeaveRails));
        Assert.That(step.WantedIndex, Is.EqualTo(0));
        Assert.That(step.Reason, Does.Contain("burn left"));
    }

    [Test]
    public void ExitBeforeStageBurnout()
    {
        RailsInputs r = OnRails(4);
        r.StageBurnLeftSeconds = 10.9;   // under 10 s + one frame (1 s)
        RailsStep step = RailsBurnPolicy.Decide(r);
        Assert.That(step.Action, Is.EqualTo(RailsAction.LeaveRails));
        Assert.That(step.Reason, Does.Contain("stage"));

        r.StageBurnLeftSeconds = 11.5;
        Assert.That(RailsBurnPolicy.Decide(r).Action, Is.EqualTo(RailsAction.Stay));

        // Not entered when the stage ends too soon for any rails stretch.
        RailsInputs i = Eligible();
        i.StageBurnLeftSeconds = 100;   // 90 s to the exit: 9 s real at x10
        Assert.That(RailsBurnPolicy.Decide(i).Action, Is.EqualTo(RailsAction.Stay));
    }

    [Test]
    public void RateLeavesFifteenRealSeconds()
    {
        // 600 s left: 585 s to the exit, 11.7 s real at x50 (too short), 58.5 s at x10: x10.
        RailsInputs i = Eligible();
        i.BurnLeftGameSeconds = 600;
        RailsStep step = RailsBurnPolicy.Decide(i);
        Assert.That(step.Action, Is.EqualTo(RailsAction.EnterRails));
        Assert.That(step.WantedIndex, Is.EqualTo(3));

        // 160 s left: 145 s to the exit, 14.5 s real at x10: no rails.
        i.BurnLeftGameSeconds = 160;
        Assert.That(RailsBurnPolicy.Decide(i).Action, Is.EqualTo(RailsAction.Stay));

        // The stage limits the stretch too: 300 s of stage, 290 s to the exit, x10 only.
        i = Eligible();
        i.StageBurnLeftSeconds = 300;
        Assert.That(RailsBurnPolicy.Decide(i).WantedIndex, Is.EqualTo(3));
        Assert.That(RailsBurnPolicy.PlannedRailsSeconds(1000, 300), Is.EqualTo(290));
    }

    [Test]
    public void AimDrift_FromTheGuidanceAlongThePath()
    {
        // No gravity, a fixed target velocity: thrusting along the first Δv keeps the Δv-to-go
        // on the same line, no drift.
        var vt = new Vector3d(100, 50, 0);
        Func<Vector3d, Vector3d, double, Vector3d> toVelocity = (r, v, ut) => vt - v;
        double drift = RailsBurnPolicy.PredictAimDrift(toVelocity, new Vector3d(1e6, 0, 0), Vector3d.zero, 0, 0, 0.5, 100,
            out double minR);
        Assert.That(drift, Is.LessThan(1e-3));   // acos noise near 0°
        Assert.That(minR, Is.EqualTo(1e6).Within(1));

        // Circular orbit at 700 km around Kerbin (period ~1958 s), the ideal direction is the
        // prograde: after 100 s it has turned ~18.4° away from the fixed direction.
        const double mu = 3.5316e12;
        var r0 = new Vector3d(700000, 0, 0);
        var v0 = new Vector3d(0, Math.Sqrt(mu / 700000), 0);
        Func<Vector3d, Vector3d, double, Vector3d> prograde = (r, v, ut) => v.normalized * 100;
        drift = RailsBurnPolicy.PredictAimDrift(prograde, r0, v0, 0, mu, 0.001, 100, out minR);
        Assert.That(drift, Is.EqualTo(18.4).Within(0.5));
        Assert.That(minR, Is.EqualTo(700000).Within(100));

        // No answer from the guidance: unknown.
        Func<Vector3d, Vector3d, double, Vector3d> none = (r, v, ut) => new Vector3d(double.NaN, 0, 0);
        Assert.That(double.IsNaN(RailsBurnPolicy.PredictAimDrift(none, r0, v0, 0, mu, 1, 100, out _)), Is.True);
    }

    [Test]
    public void AccelAfter_GrowsAsMassDrops()
    {
        // 100 kN on 10 t burning 30 kg/s: 10 m/s² now, 100 kN / 7 t after 100 s.
        Assert.That(RailsBurnPolicy.AccelAfter(100000, 10000, 30, 0), Is.EqualTo(10).Within(1e-9));
        Assert.That(RailsBurnPolicy.AccelAfter(100000, 10000, 30, 100), Is.EqualTo(100000.0 / 7000).Within(1e-9));
        Assert.That(double.IsNaN(RailsBurnPolicy.AccelAfter(100000, 10000, 30, 400)), Is.True);
    }
}

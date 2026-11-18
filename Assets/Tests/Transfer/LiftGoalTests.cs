using System;
using K2D2.Lift;
using K2D2.Transfer;
using KSP.Sim;
using NUnit.Framework;

// The pure part of Lift's transfer goal (sub-project 4.2, Task 4): the pad decisions (warp, hold,
// launch, re-solve), the parking altitude, the trim node and the end message.
public class LiftGoalTests
{
    const double Deg = Math.PI / 180;
    const double Mu = 3.5316e12;  // Kerbin (KerbolFixture, external data)
    const double Orbit = 680000;

    // ---------------------------------------------------------------- pad decisions

    // 2026-11-04 in game: "throttle 1, 0 force(s)" on the pad, the warp gate held 1x for the whole wait.
    [Test]
    public void TheThrottleIsHeldAtZero_WhileWaitingOnly()
    {
        Assert.IsTrue(LiftGoalRules.HoldsThrottleAtZero(PadAction.Warp));
        Assert.IsTrue(LiftGoalRules.HoldsThrottleAtZero(PadAction.Hold));
        Assert.IsFalse(LiftGoalRules.HoldsThrottleAtZero(PadAction.Launch), "the launch sets it");
        Assert.IsFalse(LiftGoalRules.HoldsThrottleAtZero(PadAction.LiftedOff));
        Assert.IsFalse(LiftGoalRules.HoldsThrottleAtZero(PadAction.Resolve));
        // With the throttle at 0 and no engine force, the gate lets the rails warp through after 1 s.
        var quiet = new K2D2.EngineQuietTimer();
        for (int i = 0; i < 11; i++)
            quiet.Update(true, i * 0.11);
        Assert.AreEqual(6, K2D2.WarpGatePolicy.AllowedIndex(6, 0, false, quiet.Quiet(1.1)));
        Assert.AreEqual(0, K2D2.WarpGatePolicy.AllowedIndex(6, 0, false, false), "held at 1x at full throttle");
    }

    [Test]
    public void LongBeforeTheLaunch_Warps()
    {
        Assert.AreEqual(PadAction.Warp, LiftGoalRules.Decide(1000, 5000, landed: true));
        Assert.AreEqual(PadAction.Warp, LiftGoalRules.Decide(5000 - LiftGoalRules.HoldSeconds - 0.1, 5000, true));
    }

    [Test]
    public void InTheLast30Seconds_HoldsAt1x()
    {
        Assert.AreEqual(PadAction.Hold, LiftGoalRules.Decide(5000 - LiftGoalRules.HoldSeconds, 5000, true));
        Assert.AreEqual(PadAction.Hold, LiftGoalRules.Decide(4999.9, 5000, true));
    }

    [Test]
    public void AtTheLaunchTime_AndUpTo5SecondsLate_Launches()
    {
        Assert.AreEqual(PadAction.Launch, LiftGoalRules.Decide(5000, 5000, true));
        Assert.AreEqual(PadAction.Launch, LiftGoalRules.Decide(5000 + LiftGoalRules.MissedSeconds, 5000, true));
    }

    [Test]
    public void MoreThan5SecondsLate_OnThePad_Resolves()
    {
        // The player warped past the launch time, or the launch did not lift off: never launch late
        // onto the wrong plane (Review Focus 4).
        Assert.AreEqual(PadAction.Resolve, LiftGoalRules.Decide(5005.1, 5000, true));
        Assert.AreEqual(PadAction.Resolve, LiftGoalRules.Decide(90000, 5000, true));
        Assert.AreEqual(PadAction.Resolve, LiftGoalRules.Decide(1000, double.NaN, true));
    }

    [Test]
    public void OffThePad_IsLiftedOff_WhateverTheClock()
    {
        Assert.AreEqual(PadAction.LiftedOff, LiftGoalRules.Decide(1000, 5000, landed: false));
        Assert.AreEqual(PadAction.LiftedOff, LiftGoalRules.Decide(5002, 5000, false));
        Assert.AreEqual(PadAction.LiftedOff, LiftGoalRules.Decide(9000, 5000, false));
    }

    [Test]
    public void WarpEnd_Is30SecondsBeforeTheLaunch()
    {
        // LaunchUT already includes the ascent lead (LaunchWindow): no second lead here.
        Assert.AreEqual(4970, LiftGoalRules.WarpEndUT(5000), 1e-9);
    }

    // ---------------------------------------------------------------- staging at T-0

    [Test]
    public void UnstagedVessel_NoEngineLit_IsStaged()
    {
        // 3 stages, the last (StageCount-1, the first to fire) not active yet.
        Assert.AreEqual(LaunchStaging.Stage, LiftGoalRules.LaunchStagingFor(false, 3, false, 0));
    }

    [Test]
    public void PlayerStaged_EnginesLit_LiftsOffOnTheThrottleOnly()
    {
        Assert.AreEqual(LaunchStaging.ThrottleOnly, LiftGoalRules.LaunchStagingFor(false, 3, true, 2));
        // Engines lit by hand (action group) with nothing staged: still no stage fired.
        Assert.AreEqual(LaunchStaging.ThrottleOnly, LiftGoalRules.LaunchStagingFor(false, 3, false, 1));
        // The mod staged already (a re-solved pass): never a second stage.
        Assert.AreEqual(LaunchStaging.ThrottleOnly, LiftGoalRules.LaunchStagingFor(true, 2, true, 1));
    }

    [Test]
    public void PlayerStaged_NoEngineLit_Refuses()
    {
        // Clamps released or a decoupler fired by hand, engines off: the next stage could drop the boosters.
        Assert.AreEqual(LaunchStaging.Refuse, LiftGoalRules.LaunchStagingFor(false, 3, true, 0));
        Assert.AreEqual(LaunchStaging.Refuse, LiftGoalRules.LaunchStagingFor(true, 2, true, 0));
        Assert.AreEqual(LaunchStaging.Refuse, LiftGoalRules.LaunchStagingFor(false, 0, false, 0));
    }

    [Test]
    public void StageFired_OnlyWhenTheStagingChanged()
    {
        Assert.IsTrue(LiftGoalRules.StageFired(3, false, 3, true));   // first staging: stage L activated
        Assert.IsTrue(LiftGoalRules.StageFired(3, true, 2, true));    // later: stage L removed
        Assert.IsFalse(LiftGoalRules.StageFired(3, false, 3, false)); // paused or on rails: nothing happened
    }

    // ---------------------------------------------------------------- stored ascent lead

    [Test]
    public void UpdatedLead_FirstMeasurement_IsClamped()
    {
        Assert.IsTrue(LiftGoalRules.UpdatedLead(-1, -1, 170, 6, out double s, out double d));
        Assert.AreEqual(170, s, 1e-9);
        Assert.AreEqual(6, d, 1e-9);
        Assert.IsTrue(LiftGoalRules.UpdatedLead(-1, -1, 2000, 0.2, out s, out d));
        Assert.AreEqual(LiftGoalRules.MaxLeadSeconds, s, 1e-9);
        Assert.AreEqual(LiftGoalRules.MinDownrangeDeg, d, 1e-9);
    }

    [Test]
    public void UpdatedLead_IsSmoothedWithThePreviousValue()
    {
        Assert.IsTrue(LiftGoalRules.UpdatedLead(150, 5, 190, 7, out double s, out double d));
        Assert.AreEqual(170, s, 1e-9);
        Assert.AreEqual(6, d, 1e-9);
    }

    [Test]
    public void UpdatedLead_UnusableMeasurement_KeepsThePrevious()
    {
        Assert.IsFalse(LiftGoalRules.UpdatedLead(150, 5, double.NaN, 7, out double s, out double d));
        Assert.AreEqual(150, s, 1e-9);
        Assert.AreEqual(5, d, 1e-9);
        Assert.IsFalse(LiftGoalRules.UpdatedLead(150, 5, -3, 7, out _, out _));
    }

    [Test]
    public void AscentLead_OutOfTheRange_UsesTheEstimate()
    {
        LiftGoalRules.AscentLead(20, 4, out double lead, out _, out bool measured);
        Assert.IsFalse(measured);
        Assert.AreEqual(LiftGoalRules.EstimatedLeadSeconds, lead, 1e-9);
        LiftGoalRules.AscentLead(160, 45, out _, out _, out measured);
        Assert.IsFalse(measured);
    }

    // ---------------------------------------------------------------- parking altitude

    [Test]
    public void ParkingAp_IsTheHigherOfTheLowOrbitAndTheApSetting()
    {
        // Kerbin: atmosphere 70 km + 10 km.
        Assert.AreEqual(100, LiftGoalRules.ParkingApKm(680000, 600000, 100));
        Assert.AreEqual(80, LiftGoalRules.ParkingApKm(680000, 600000, 75));
        // A low orbit that is not a whole number of km rounds up: never below the low orbit.
        Assert.AreEqual(81, LiftGoalRules.ParkingApKm(680400, 600000, 20));
    }

    // ---------------------------------------------------------------- ascent lead

    [Test]
    public void AscentLead_UsesTheMeasuredValues_ElseTheEstimate()
    {
        LiftGoalRules.AscentLead(160, 4, out double lead, out double downrange, out bool measured);
        Assert.IsTrue(measured);
        Assert.AreEqual(160, lead, 1e-9);
        Assert.AreEqual(4 * Deg, downrange, 1e-12);

        LiftGoalRules.AscentLead(-1, -1, out lead, out downrange, out measured);
        Assert.IsFalse(measured);
        Assert.AreEqual(LiftGoalRules.EstimatedLeadSeconds, lead, 1e-9);
        Assert.AreEqual(LiftGoalRules.EstimatedDownrangeDeg * Deg, downrange, 1e-12);

        LiftGoalRules.AscentLead(float.NaN, 3, out lead, out _, out measured);
        Assert.IsFalse(measured);
        Assert.AreEqual(LiftGoalRules.EstimatedLeadSeconds, lead, 1e-9);
    }

    [Test]
    public void Downrange_IsMeasuredOnTheTurningGround()
    {
        var omega = KerbolFixture.KerbinAngularVelocity();
        var site = KerbolFixture.SurfacePoint(600000, 0, 0);
        double dt = 150;
        // The insertion point: 5° east of the site on the ground, which itself turned by ω·dt.
        double turn = omega.z * dt;
        double a = turn + 5 * Deg;
        var insertion = 680000 * new Vector3d(Math.Cos(a), Math.Sin(a), 0);
        Assert.AreEqual(5 * Deg, LiftGoalRules.DownrangeRad(site, insertion, omega, dt), 1e-9);
        // Without the rotation it would read 5° + ω·dt.
        Assert.AreEqual(5 * Deg + turn, LiftGoalRules.DownrangeRad(site, insertion, Vector3d.zero, dt), 1e-9);
    }

    // ---------------------------------------------------------------- trim

    [Test]
    public void Trim_OnlyAbove0Point1Degree()
    {
        Assert.IsFalse(LiftGoalRules.TrimNeeded(0.1));
        Assert.IsFalse(LiftGoalRules.TrimNeeded(0.05));
        Assert.IsTrue(LiftGoalRules.TrimNeeded(0.11));
        Assert.IsFalse(LiftGoalRules.TrimNeeded(double.NaN));
    }

    [Test]
    public void TrimNode_IsTheNextCrossingOfTheTargetPlane_AtLeastTheLeadAhead()
    {
        double vo = Math.Sqrt(Mu / Orbit);
        double period = 2 * Math.PI * Math.Sqrt(Orbit * Orbit * Orbit / Mu);
        // An equatorial orbit; the target plane is inclined 1° with its ascending node on +x.
        var target = new Vector3d(0, -Math.Sin(1 * Deg), Math.Cos(1 * Deg));
        // 30° before the +x node: the next crossing is the +x node, 30° ahead.
        double a = -30 * Deg;
        var r = Orbit * new Vector3d(Math.Cos(a), Math.Sin(a), 0);
        var v = vo * new Vector3d(-Math.Sin(a), Math.Cos(a), 0);
        double t = LiftGoalRules.TimeToTrimNode(r, v, Mu, target, 130, out var node);
        Assert.AreEqual(period / 12, t, 1e-3 * period);
        Assert.AreEqual(1, node.x, 1e-9);

        // 1° before it: too close for the alignment, so the other node, half an orbit later.
        a = -1 * Deg;
        r = Orbit * new Vector3d(Math.Cos(a), Math.Sin(a), 0);
        v = vo * new Vector3d(-Math.Sin(a), Math.Cos(a), 0);
        t = LiftGoalRules.TimeToTrimNode(r, v, Mu, target, 130, out node);
        Assert.AreEqual(period * 181.0 / 360, t, 1e-3 * period);
        Assert.AreEqual(-1, node.x, 1e-9);
    }

    [Test]
    public void TrimNode_SamePlane_IsNaN()
    {
        var r = new Vector3d(Orbit, 0, 0);
        var v = new Vector3d(0, Math.Sqrt(Mu / Orbit), 0);
        Assert.IsNaN(LiftGoalRules.TimeToTrimNode(r, v, Mu, new Vector3d(0, 0, 1), 130, out _));
    }

    [Test]
    public void PlaneChangeDeltaV_IsTheChordOfTheSpeed()
    {
        Assert.AreEqual(2 * 2280 * Math.Sin(0.5 * Deg), LiftGoalRules.PlaneChangeDeltaV(2280, 1), 1e-9);
        Assert.AreEqual(0, LiftGoalRules.PlaneChangeDeltaV(2280, 0), 1e-12);
    }

    // ---------------------------------------------------------------- end message

    [Test]
    public void EndMessage_WithinTheTolerance_IsReadyForTheTransfer()
    {
        Assert.IsTrue(LiftGoalRules.EndMessage("Duna", 0.04, out string text));
        Assert.AreEqual("Ready for the Duna transfer: open the Transfer tab", text);
    }

    [Test]
    public void EndMessage_OutOfTolerance_SaysByHowMuch()
    {
        Assert.IsFalse(LiftGoalRules.EndMessage("Duna", 0.35, out string text));
        Assert.AreEqual("In the parking orbit, but its plane is still 0.35° off the Duna transfer's (more than 0.1°): the Transfer tab will plan from this orbit", text);
    }

    // ---------------------------------------------------------------- readout

    [Test]
    public void Readout_ShowsTheLaunchThePlaneAndTheWindow()
    {
        var p = new LiftTransferPlan
        {
            TargetName = "Duna",
            Parking = new ParkingPlan { Ok = true, PlaneNormal = new Vector3d(0, -Math.Sin(4.28 * Deg), Math.Cos(4.28 * Deg)),
                WindowUT = 3 * KerbinCalendar.DaySeconds, TransferDeltaV = 1696 },
            Window = new LaunchWindowResult { Ok = true, LaunchUT = 1000 + 3600 + 120, AzimuthDeg = 85.7, PlaneErrorDeg = 0.2, PlaneChangeDeltaV = 8 },
        };
        var rows = LiftGoalRules.Readout(p, 1000);
        Assert.AreEqual(("Launch in", "T-1h 02m"), rows[0]);
        Assert.AreEqual(("Azimuth", "85.7°"), rows[1]);
        Assert.AreEqual(("Plane inclination", "4.28°"), rows[2]);
        Assert.AreEqual(("Plane change left", "0.20° (8 m/s)"), rows[3]);
        Assert.AreEqual(("Transfer window", "Y1, D4, 0h 00m (1696 m/s)"), rows[4]);
    }

    [Test]
    public void Readout_BeforeThePlan_SaysPlanning()
    {
        var rows = LiftGoalRules.Readout(new LiftTransferPlan { TargetName = "Duna" }, 0);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(("Transfer plan", "planning for Duna..."), rows[0]);
    }
    // ---------------------------------------------------------------- rendezvous goal (4.4)

    static readonly LiftPilot.LiftStatus[] OldOrder =
    {
        LiftPilot.LiftStatus.Off, LiftPilot.LiftStatus.WaitLaunch, LiftPilot.LiftStatus.Ascent, LiftPilot.LiftStatus.Coasting,
        LiftPilot.LiftStatus.Adjust, LiftPilot.LiftStatus.Circularize, LiftPilot.LiftStatus.PlaneTrim,
    };

    [Test]
    public void NextStatus_OrbitAndTransfer_Unchanged()
    {
        // The order before Handoff existed: each status goes to the next, PlaneTrim back to Off.
        foreach (var goal in new[] { LiftGoal.Orbit, LiftGoal.PrepareTransfer })
            for (int k = 0; k < OldOrder.Length; k++)
                Assert.AreEqual(OldOrder[(k + 1) % OldOrder.Length], LiftGoalRules.NextStatus(OldOrder[k], goal), $"{goal} {OldOrder[k]}");
    }

    [Test]
    public void NextStatus_RendezvousAfterCircularize_Handoff()
    {
        Assert.AreEqual(LiftPilot.LiftStatus.Handoff, LiftGoalRules.NextStatus(LiftPilot.LiftStatus.Circularize, LiftGoal.Rendezvous));
        Assert.AreEqual(LiftPilot.LiftStatus.Coasting, LiftGoalRules.NextStatus(LiftPilot.LiftStatus.Ascent, LiftGoal.Rendezvous));
        Assert.AreEqual(LiftPilot.LiftStatus.Off, LiftGoalRules.NextStatus(LiftPilot.LiftStatus.Handoff, LiftGoal.Rendezvous));
        Assert.AreEqual(LiftPilot.LiftStatus.PlaneTrim, LiftGoalRules.NextStatus(LiftPilot.LiftStatus.Circularize, LiftGoal.PrepareTransfer));
    }

    [Test]
    public void MissedLaunch_RendezvousReplansAll_TransferNextPass()
    {
        Assert.AreEqual(LiftGoalRules.ResolveMode.ReplanAll, LiftGoalRules.OnMissedLaunch(LiftGoal.Rendezvous));
        Assert.AreEqual(LiftGoalRules.ResolveMode.NextPass, LiftGoalRules.OnMissedLaunch(LiftGoal.PrepareTransfer));
    }

    [Test]
    public void ChainStopped_TextPerStep()
    {
        Assert.AreEqual("Rendezvous with Station stopped during the pad wait: aborted. Left to do: press Start again on the pad: the launch is planned again",
            LiftGoalRules.ChainStopped(LiftPilot.LiftStatus.WaitLaunch, "Station", "aborted"));
        foreach (var s in new[] { LiftPilot.LiftStatus.Ascent, LiftPilot.LiftStatus.Coasting, LiftPilot.LiftStatus.Adjust })
            StringAssert.EndsWith("Left to do: reach orbit by hand, then start the rendezvous from the Rendezvous tab (the target is set)",
                LiftGoalRules.ChainStopped(s, "Station", "x"));
        StringAssert.Contains("during the ascent: x.", LiftGoalRules.ChainStopped(LiftPilot.LiftStatus.Ascent, "Station", "x"));
        StringAssert.EndsWith("Left to do: circularize, then start the rendezvous from the Rendezvous tab (the target is set)",
            LiftGoalRules.ChainStopped(LiftPilot.LiftStatus.Circularize, "Station", "x"));
        // A reason that already ends with a full stop does not get a second one.
        Assert.AreEqual("Rendezvous with Station stopped during the hand-over: No target vessel. Left to do: start the rendezvous from the Rendezvous tab (the target is set)",
            LiftGoalRules.ChainStopped(LiftPilot.LiftStatus.Handoff, "Station", "No target vessel."));
    }

    [Test]
    public void Handoff_Started_And_Refused_Texts()
    {
        Assert.IsTrue(LiftGoalRules.HandoffMessage(true, "Station", true, "", out var text));
        Assert.AreEqual("In the parking orbit: the Rendezvous pilot takes over (rendezvous + docking with Station), see the Rendezvous tab", text);
        Assert.IsTrue(LiftGoalRules.HandoffMessage(true, "Station", false, "", out text));
        StringAssert.Contains("(rendezvous only with Station)", text);
        Assert.IsFalse(LiftGoalRules.HandoffMessage(false, "Station", true, "No target vessel.", out text));
        Assert.AreEqual(LiftGoalRules.ChainStopped(LiftPilot.LiftStatus.Handoff, "Station", "No target vessel."), text);
    }

    static RendezvousLaunchPlan RvPlanFor(bool ok)
    {
        return new RendezvousLaunchPlan
        {
            Ok = ok,
            Refusal = ok ? "" : "No rendezvous from any of the 8 launch passes: nope",
            TargetPlaneNormal = new Vector3d(0, -Math.Sin(6 * Deg), Math.Cos(6 * Deg)),
            ParkingRadius = 710000,
            ParkingReason = "10 km under the target's periapsis",
            Window = new LaunchWindowResult { Ok = true, LaunchUT = 1000 + 3600 + 120, AzimuthDeg = 84.2, PlaneErrorDeg = 0.1, PlaneChangeDeltaV = 4 },
            Rendezvous = new K2D2.Rendezvous.RvPlan { Ok = true, TotalDeltaV = 18.6, ArrivalUT = 3 * KerbinCalendar.DaySeconds },
            Passes = new System.Collections.Generic.List<RendezvousPass>
            {
                new RendezvousPass { DeltaV = 67.3 }, new RendezvousPass { DeltaV = 18.6 }, new RendezvousPass { DeltaV = 40 },
            },
            Chosen = 1,
        };
    }

    [Test]
    public void PlaneNormal_And_Ready_PerGoal()
    {
        var transfer = new LiftTransferPlan
        {
            Parking = new ParkingPlan { Ok = true, PlaneNormal = new Vector3d(1, 0, 0) },
            Window = new LaunchWindowResult { Ok = true },
        };
        Assert.AreEqual(LiftGoal.PrepareTransfer, transfer.Goal);
        Assert.AreEqual(1, transfer.PlaneNormal.x);
        Assert.IsTrue(transfer.Ready);

        var rv = new LiftTransferPlan { Goal = LiftGoal.Rendezvous, Rendezvous = RvPlanFor(true) };
        Assert.IsFalse(rv.Ready, "no window yet");
        rv.Window = rv.Rendezvous.Window;
        Assert.IsTrue(rv.Ready);
        Assert.AreEqual(Math.Cos(6 * Deg), rv.PlaneNormal.z, 1e-12);
        rv.Rendezvous = RvPlanFor(false);
        Assert.IsFalse(rv.Ready);
        rv.Rendezvous = null;
        Assert.IsFalse(rv.Ready);
    }

    [Test]
    public void Readout_RendezvousRows()
    {
        var p = new LiftTransferPlan { Goal = LiftGoal.Rendezvous, TargetName = "Station", BodyRadius = 600000 };
        var rows = LiftGoalRules.Readout(p, 1000);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(("Rendezvous plan", "planning with Station..."), rows[0]);

        p.Rendezvous = RvPlanFor(false);
        rows = LiftGoalRules.Readout(p, 1000);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(("Rendezvous plan", "No rendezvous from any of the 8 launch passes: nope"), rows[0]);

        p.Rendezvous = RvPlanFor(true);
        p.Window = p.Rendezvous.Window;
        rows = LiftGoalRules.Readout(p, 1000);
        Assert.AreEqual(7, rows.Count);
        Assert.AreEqual(("Launch in", "T-1h 02m"), rows[0]);
        Assert.AreEqual(("Azimuth", "84.2°"), rows[1]);
        Assert.AreEqual(("Target plane", "6.00°"), rows[2]);
        Assert.AreEqual(("Plane change left", "0.10° (4 m/s)"), rows[3]);
        Assert.AreEqual(("Parking orbit", "110 km (10 km under the target's periapsis)"), rows[4]);
        Assert.AreEqual(("Rendezvous", "19 m/s, arrival Y1, D4, 0h 00m"), rows[5]);
        Assert.AreEqual(("Launch pass", "2 of 3, first pass 67 m/s"), rows[6]);
    }

    [Test]
    public void Readout_TransferRows_Unchanged()
    {
        var p = new LiftTransferPlan
        {
            TargetName = "Duna",
            Parking = new ParkingPlan { Ok = true, PlaneNormal = new Vector3d(0, -Math.Sin(4.28 * Deg), Math.Cos(4.28 * Deg)),
                WindowUT = 3 * KerbinCalendar.DaySeconds, TransferDeltaV = 1696 },
            Window = new LaunchWindowResult { Ok = true, LaunchUT = 1000 + 3600 + 120, AzimuthDeg = 85.7, PlaneErrorDeg = 0.2, PlaneChangeDeltaV = 8 },
            Rendezvous = RvPlanFor(true), // ignored for the transfer goal
        };
        var rows = LiftGoalRules.Readout(p, 1000);
        Assert.AreEqual(5, rows.Count);
        Assert.AreEqual(("Plane inclination", "4.28°"), rows[2]);
        Assert.AreEqual(("Transfer window", "Y1, D4, 0h 00m (1696 m/s)"), rows[4]);
    }
    // Review finding: a target recovered, destroyed, docked elsewhere (new GUID) or landed during the
    // ascent: the hand-over stops, and never advises a target that is not set any more.
    [Test]
    public void HandoffRefusal_TargetGoneLandedOrElsewhere()
    {
        Assert.IsNull(LiftGoalRules.HandoffRefusal(true, false, true));
        Assert.AreEqual("the target vessel no longer exists (recovered, destroyed, or docked elsewhere)", LiftGoalRules.HandoffRefusal(false, false, false));
        Assert.AreEqual("the target is landed", LiftGoalRules.HandoffRefusal(true, true, true));
        Assert.AreEqual("the target no longer orbits the launch body", LiftGoalRules.HandoffRefusal(true, false, false));
        Assert.AreEqual("Rendezvous with Station stopped during the hand-over: the target is landed. Left to do: choose a target in the Rendezvous tab, then start the rendezvous there",
            LiftGoalRules.ChainStopped(LiftPilot.LiftStatus.Handoff, "Station", "the target is landed", targetLost: true));
    }
}

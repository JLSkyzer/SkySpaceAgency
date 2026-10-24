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
}

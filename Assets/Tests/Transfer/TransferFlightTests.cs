using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using K2D2.Node;
using K2D2.OrbitPlanning;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using NUnit.Framework;

// The transfer pilot's pure decisions (TransferFlight): the step table, legs, the correction rules,
// the plan update after a correction, the resume decision, and one whole flight driven by the table.
public class TransferFlightTests
{
    const double KerbinDay = 21600;

    BodySystem _s;
    int _kerbin, _kerbol, _duna, _mun;

    [SetUp]
    public void SetUp()
    {
        _s = KerbolFixture.Build();
        _kerbin = _s["Kerbin"].Index;
        _kerbol = _s["Kerbol"].Index;
        _duna = _s["Duna"].Index;
        _mun = _s["Mun"].Index;
    }

    // Kerbin -> Duna by hand: injection, SOI exit, three corrections, the capture.
    TransferPlan HandPlan()
    {
        var q = new TransferRequest { FromBody = _kerbin, ToBody = _duna, R0 = new Vector3d(700_000, 0, 0), V0 = new Vector3d(0, 2300, 0) };
        double pe = _s[_duna].LowOrbitRadius;
        PlanStep Aim(StepKind kind, int body, double ut, double dv = 0) => new PlanStep
        {
            Kind = kind, Body = body, UT = ut, DeltaVMagnitude = dv, TargetBody = _duna, TargetPeriapsisRadius = pe, TargetApoapsisRadius = pe,
        };
        var injection = Aim(StepKind.Injection, _kerbin, 1000, 1050);
        injection.DeltaV = new Vector3d(0, 1050, 0);
        return new TransferPlan
        {
            Ok = true,
            Request = q,
            DepartureUT = 1000,
            ArrivalUT = 1_000_000,
            ReserveDeltaV = 34,
            TotalDeltaV = 1700,
            CaseName = TransferSearch.Siblings,
            Steps =
            {
                injection,
                Aim(StepKind.Escape, _kerbin, 20_000),
                Aim(StepKind.Correction, _kerbol, 30_000),
                Aim(StepKind.Correction, _kerbol, 500_000),
                Aim(StepKind.Correction, _kerbol, 900_000),
                Aim(StepKind.Capture, _duna, 1_000_000, 600),
            },
        };
    }

    // ---------------------------------------------------------------- the step table

    [Test]
    public void Next_WaitsForTheWindow_ThenBurns()
    {
        var plan = HandPlan();
        double lead = TransferFlight.BurnLead(0);
        var d = TransferFlight.Next(plan, 0, _kerbin, _duna, 0, lead, double.NaN);
        Assert.AreEqual(FlightAction.Warp, d.Action);
        Assert.AreEqual(TransferPhase.WaitWindow, d.Phase);
        Assert.AreEqual(1000 - lead, d.WarpUntilUT, 1e-9);
        Assert.Greater(lead, BurnWarp.MinLead + TransferFlight.BurnWarpMargin - 1e-9, "the alignment lead plus 30 s");

        d = TransferFlight.Next(plan, 0, _kerbin, _duna, 1000 - lead, lead, double.NaN);
        Assert.AreEqual(FlightAction.Burn, d.Action);
        Assert.AreEqual(TransferPhase.Injection, d.Phase);
    }

    [Test]
    public void BurnLead_CentresTheBurn()
    {
        Assert.AreEqual(TransferFlight.BurnLead(0) + 50, TransferFlight.BurnLead(100), 1e-9);
        Assert.AreEqual(TransferFlight.BurnLead(0), TransferFlight.BurnLead(double.NaN), 1e-9);
    }

    [Test]
    public void Next_StopsBeforeTheSoiChange_ThenWaits_ThenPasses()
    {
        var plan = HandPlan();
        var d = TransferFlight.Next(plan, 1, _kerbin, _duna, 5000, 100, 19_000);
        Assert.AreEqual(FlightAction.Warp, d.Action);
        Assert.AreEqual(TransferPhase.Escape, d.Phase);
        Assert.AreEqual(19_000 - TransferFlight.SoiWarpMargin, d.WarpUntilUT, 1e-9, "the propagator's SOI change, not the planned estimate");

        d = TransferFlight.Next(plan, 1, _kerbin, _duna, 18_990, 100, 19_000);
        Assert.AreEqual(FlightAction.WaitSoi, d.Action);
        Assert.AreEqual(19_000, d.WarpUntilUT, 1e-9);

        d = TransferFlight.Next(plan, 1, _kerbol, _duna, 19_001, 100, double.NaN);
        Assert.AreEqual(FlightAction.StepPassed, d.Action);
    }

    [Test]
    public void Next_CorrectionSolvedWhenDue_CaptureInTheTargetSoi()
    {
        var plan = HandPlan();
        var d = TransferFlight.Next(plan, 2, _kerbol, _duna, 25_000, 100, double.NaN);
        Assert.AreEqual(FlightAction.Warp, d.Action);
        Assert.AreEqual(TransferPhase.Coast, d.Phase);
        d = TransferFlight.Next(plan, 2, _kerbol, _duna, 29_950, 100, double.NaN);
        Assert.AreEqual(FlightAction.Solve, d.Action);
        Assert.AreEqual(TransferPhase.Correction, d.Phase);

        // The capture is flown in Duna: from Kerbol, coast to the SOI change first.
        d = TransferFlight.Next(plan, 5, _kerbol, _duna, 950_000, 100, 990_000);
        Assert.AreEqual(FlightAction.Warp, d.Action);
        Assert.AreEqual(990_000 - TransferFlight.SoiWarpMargin, d.WarpUntilUT, 1e-9);
        d = TransferFlight.Next(plan, 5, _duna, _duna, 999_950, 100, double.NaN);
        Assert.AreEqual(FlightAction.Capture, d.Action);
        Assert.AreEqual(TransferPhase.Capture, d.Phase);

        d = TransferFlight.Next(plan, 6, _duna, _duna, 1_000_100, 100, double.NaN);
        Assert.AreEqual(FlightAction.Done, d.Action);
        Assert.AreEqual(TransferPhase.Done, d.Phase);
    }

    [Test]
    public void Next_UnplannedSoiBeforeABurn_StopsTheWarpBeforeIt()
    {
        var plan = HandPlan();
        // A correction in Kerbol at 500 000, an SOI change predicted at 400 000 (an unplanned body).
        var d = TransferFlight.Next(plan, 3, _kerbol, _duna, 300_000, 100, 400_000);
        Assert.AreEqual(FlightAction.Warp, d.Action);
        Assert.AreEqual(400_000 - TransferFlight.SoiWarpMargin, d.WarpUntilUT, 1e-9);
        d = TransferFlight.Next(plan, 3, _kerbol, _duna, 399_950, 100, 400_000);
        Assert.AreEqual(FlightAction.WaitSoi, d.Action);
    }

    [Test]
    public void Next_EncounterEndsInTheTargetSoi()
    {
        var plan = HandPlan();
        plan.Steps.RemoveAt(5); // no capture
        var d = TransferFlight.Next(plan, 5, _kerbol, _duna, 950_000, 100, 990_000);
        Assert.AreEqual(FlightAction.Warp, d.Action);
        Assert.AreEqual(TransferPhase.Coast, d.Phase);
        d = TransferFlight.Next(plan, 5, _duna, _duna, 990_001, 100, double.NaN);
        Assert.AreEqual(FlightAction.Done, d.Action);
    }

    // No SOI change predicted where the plan needs one: warp to the planned time plus the late margin,
    // then the change is missed (never a warp or a 1x wait without end).
    [Test]
    public void Next_SoiChangeThatNeverComes_IsMissed()
    {
        var plan = HandPlan();
        double late = TransferFlight.SoiLateSeconds(plan, plan.Steps[5].UT);
        Assert.AreEqual(Math.Max(3600, 0.02 * (1_000_000 - 1000)), late, 1e-6);

        // The capture in Duna, the vessel still in Kerbol, no encounter predicted (a missed encounter).
        var d = TransferFlight.Next(plan, 5, _kerbol, _duna, 950_000, 100, double.NaN);
        Assert.AreEqual(FlightAction.Warp, d.Action);
        Assert.AreEqual(1_000_000 + late, d.WarpUntilUT, 1e-6);
        d = TransferFlight.Next(plan, 5, _kerbol, _duna, 1_000_000 + late, 100, double.NaN);
        Assert.AreEqual(FlightAction.SoiMissed, d.Action);
        Assert.AreEqual(_duna, d.MissedBody);
        Assert.IsFalse(d.MissedExit);

        // An escape that does not come.
        d = TransferFlight.Next(plan, 1, _kerbin, _duna, 20_000, 100, double.NaN);
        Assert.AreEqual(FlightAction.Warp, d.Action);
        d = TransferFlight.Next(plan, 1, _kerbin, _duna, 20_000 + TransferFlight.SoiLateSeconds(plan, 20_000), 100, double.NaN);
        Assert.AreEqual(FlightAction.SoiMissed, d.Action);
        Assert.AreEqual(_kerbin, d.MissedBody);
        Assert.IsTrue(d.MissedExit);

        // An encounter plan past its last step, the target never entered.
        plan.Steps.RemoveAt(5);
        d = TransferFlight.Next(plan, 5, _kerbol, _duna, plan.ArrivalUT + late, 100, double.NaN);
        Assert.AreEqual(FlightAction.SoiMissed, d.Action);
        Assert.AreEqual(_duna, d.MissedBody);
    }

    [Test]
    public void InOrbit_ClosedInsideTheSoi()
    {
        double mu = _s[_duna].Mu, soi = _s[_duna].Soi, rp = _s[_duna].LowOrbitRadius;
        var r = new Vector3d(rp, 0, 0);
        double vc = Math.Sqrt(mu / rp);
        Assert.IsTrue(TransferFlight.InOrbit(r, new Vector3d(0, vc, 0), mu, soi));
        Assert.IsFalse(TransferFlight.InOrbit(r, new Vector3d(0, 1.5 * vc, 0), mu, soi), "an escape");
        Assert.IsFalse(TransferFlight.InOrbit(r, new Vector3d(0, 1.41 * vc, 0), mu, soi), "closed, but its apoapsis is beyond the SOI");
    }

    [Test]
    public void BurnPhase_EveryKind()
    {
        Assert.AreEqual(TransferPhase.PlaneChange, TransferFlight.BurnPhase(StepKind.PlaneChange));
        Assert.AreEqual(TransferPhase.OrbitChange, TransferFlight.BurnPhase(StepKind.OrbitChange));
        Assert.AreEqual(TransferPhase.Injection, TransferFlight.BurnPhase(StepKind.Injection));
        Assert.AreEqual(TransferPhase.Escape, TransferFlight.BurnPhase(StepKind.Escape));
        Assert.AreEqual(TransferPhase.Correction, TransferFlight.BurnPhase(StepKind.Correction));
        Assert.AreEqual(TransferPhase.Capture, TransferFlight.BurnPhase(StepKind.Capture));
        var plan = HandPlan();
        Assert.AreEqual(TransferPhase.WaitWindow, TransferFlight.WaitPhase(plan, 0));
        Assert.AreEqual(TransferPhase.Coast, TransferFlight.WaitPhase(plan, 2));
    }

    // ---------------------------------------------------------------- legs

    [Test]
    public void LegStart_HowEachLegIsPlannedAgain()
    {
        var plan = HandPlan();
        Assert.AreEqual(LegReplan.None, TransferFlight.LegStart(plan, 2, _kerbol, false), "leg 0 is flown as planned");

        plan.Steps[5].LegIndex = 1;
        plan.Steps[5].ReplanAtLegStart = true;
        Assert.AreEqual(LegReplan.None, TransferFlight.LegStart(plan, 5, _kerbol, false), "not in the leg's body yet");
        Assert.AreEqual(LegReplan.Search, TransferFlight.LegStart(plan, 5, _duna, true), "a closed orbit: search again");

        plan.Steps[5].Kind = StepKind.Correction; // a direct descent's entry correction
        Assert.AreEqual(LegReplan.SolveCorrection, TransferFlight.LegStart(plan, 5, _duna, false));

        plan.Steps[5].DeltaV = new Vector3d(5, 0, 0); // a straight-through re-aim burn
        Assert.AreEqual(LegReplan.PlannedImpulse, TransferFlight.LegStart(plan, 5, _duna, false));
        plan.ExcessBody = _duna;
        plan.RequiredExcess = new Vector3d(900, 0, 0);
        Assert.AreEqual(LegReplan.AimExcess, TransferFlight.LegStart(plan, 5, _duna, false));

        TransferFlight.MarkLegStarted(plan, 5);
        Assert.IsFalse(plan.Steps[5].ReplanAtLegStart);
        Assert.AreEqual(LegReplan.None, TransferFlight.LegStart(plan, 5, _duna, false), "started once only");
    }

    [Test]
    public void MergeLeg_ReplacesTheRestWithTheNewLeg()
    {
        var plan = HandPlan();
        foreach (var st in plan.Steps.Skip(3))
        {
            st.LegIndex = 1;
            st.ReplanAtLegStart = true;
        }
        var replanned = new TransferPlan
        {
            Ok = true,
            ArrivalUT = 2_000_000,
            Warnings = { "new warning" },
            Steps =
            {
                new PlanStep { Kind = StepKind.Injection, Body = _duna, UT = 1_200_000, DeltaV = new Vector3d(1, 0, 0), DeltaVMagnitude = 1 },
                new PlanStep { Kind = StepKind.Capture, Body = _duna, UT = 2_000_000, LegIndex = 1, ReplanAtLegStart = true },
            },
        };
        TransferFlight.MergeLeg(plan, 3, replanned);
        Assert.AreEqual(5, plan.Steps.Count);
        Assert.AreEqual(StepKind.Injection, plan.Steps[3].Kind);
        Assert.AreEqual(1, plan.Steps[3].LegIndex, "the new plan's leg 0 is the current leg");
        Assert.AreEqual(2, plan.Steps[4].LegIndex);
        Assert.AreEqual(2_000_000, plan.ArrivalUT);
        CollectionAssert.Contains(plan.Warnings, "new warning");
    }

    [Test]
    public void LegArrival_AndCaptureOfTheLeg()
    {
        var plan = HandPlan();
        Assert.AreEqual(plan.ArrivalUT, TransferFlight.LegArrivalUT(plan, 2));
        Assert.AreEqual(5, TransferFlight.LegCapture(plan, 2));
        Assert.AreEqual(_s[_duna].LowOrbitRadius, TransferFlight.CaptureApoapsis(plan, 2), 1e-6);
        plan.Steps[5].LegIndex = 1;
        Assert.AreEqual(plan.Steps[5].UT, TransferFlight.LegArrivalUT(plan, 2), "the next leg's first step");
        Assert.AreEqual(-1, TransferFlight.LegCapture(plan, 2));
        Assert.IsTrue(double.IsNaN(TransferFlight.CaptureApoapsis(plan, 2)), "a body only passed: no capture");
    }

    // ---------------------------------------------------------------- corrections

    [Test]
    public void JudgeCorrection_SkipOnlyWhenThePathAlreadyMeetsThePeriapsis()
    {
        const double wanted = 700_000, floor = 690_000;
        Assert.AreEqual(CorrectionVerdict.Skip, TransferFlight.JudgeCorrection(0.4, true, wanted, wanted, wanted + 800, floor, out _));
        Assert.AreEqual(CorrectionVerdict.Fly, TransferFlight.JudgeCorrection(0.4, true, wanted, wanted, wanted + 5000, floor, out _),
            "tiny but needed: several skipped once led to an impact");
        Assert.AreEqual(CorrectionVerdict.Fly, TransferFlight.JudgeCorrection(0.4, true, wanted, wanted, double.NaN, floor, out _),
            "no encounter without it");
        Assert.AreEqual(CorrectionVerdict.Fly, TransferFlight.JudgeCorrection(12, true, wanted, wanted, wanted, floor, out _));
        Assert.AreEqual(CorrectionVerdict.Skip, TransferFlight.JudgeCorrection(0.4, true, double.NaN, double.NaN, double.NaN, floor, out _),
            "no periapsis wanted: the threshold alone");
    }

    [Test]
    public void JudgeCorrection_NotConverged_RefusedWhenTooFarOrTooLow()
    {
        const double wanted = 700_000, floor = 690_000;
        Assert.AreEqual(CorrectionVerdict.Fly, TransferFlight.JudgeCorrection(20, false, wanted + 8000, wanted, double.NaN, floor, out _));
        Assert.AreEqual(CorrectionVerdict.Refuse, TransferFlight.JudgeCorrection(20, false, wanted + 90_000, wanted, double.NaN, floor, out string why));
        StringAssert.Contains("misses", why);
        Assert.AreEqual(CorrectionVerdict.Refuse, TransferFlight.JudgeCorrection(20, false, 600_000, wanted, double.NaN, floor, out why));
        StringAssert.Contains("below", why);
        Assert.AreEqual(CorrectionVerdict.Refuse, TransferFlight.JudgeCorrection(20, false, double.NaN, wanted, double.NaN, floor, out _));
    }

    [Test]
    public void ApplySolve_SamePass_CaptureAtTheAimedPeriapsis()
    {
        var plan = HandPlan();
        double shift = TransferFlight.ApplySolve(plan, 2, 1_000_000.4, 612.5, 30_000);
        Assert.AreEqual(0.4, shift, 1e-6);
        Assert.AreEqual(1_000_000.4, plan.Steps[5].UT, 1e-9);
        Assert.AreEqual(612.5, plan.Steps[5].DeltaVMagnitude);
        Assert.AreEqual(1_000_000.4, plan.ArrivalUT, 1e-9);
        Assert.AreEqual(500_000, plan.Steps[3].UT, "under a second: the corrections keep their times");
    }

    [Test]
    public void ApplySolve_AnotherPass_MovesTheArrivalAndRetimesTheCorrections()
    {
        var plan = HandPlan();
        double newArrival = 1_400_000;
        double shift = TransferFlight.ApplySolve(plan, 2, newArrival, 640, 30_000);
        Assert.AreEqual(400_000, shift, 1e-6);
        Assert.AreEqual(newArrival, plan.ArrivalUT);
        var capture = plan.Steps.Single(st => st.Kind == StepKind.Capture);
        Assert.AreEqual(newArrival, capture.UT);
        Assert.AreEqual(640, capture.DeltaVMagnitude);
        var times = TransferSearch.CorrectionTimes(20_000, 1000, newArrival, newArrival);
        var corrections = plan.Steps.Where(st => st.Kind == StepKind.Correction).ToList();
        Assert.AreEqual(30_000, corrections[0].UT, "the one just solved stays");
        Assert.AreEqual(times[1], corrections[1].UT, 1e-6);
        Assert.AreEqual(times[2], corrections[2].UT, 1e-6);
        for (int k = 1; k < plan.Steps.Count; k++)
            Assert.LessOrEqual(plan.Steps[k - 1].UT, plan.Steps[k].UT, "still in UT order");
    }

    [Test]
    public void ApplySolve_LaterLegsMoveWithTheArrival()
    {
        var plan = HandPlan();
        plan.Steps[5].LegIndex = 1; // the capture is the next leg's start
        plan.Steps.Add(new PlanStep { Kind = StepKind.Injection, Body = _duna, UT = 1_100_000, LegIndex = 1 });
        plan.ArrivalUT = 1_500_000;
        double shift = TransferFlight.ApplySolve(plan, 2, 1_000_000 + 7200, double.NaN, 30_000);
        Assert.AreEqual(7200, shift, 1e-6);
        Assert.AreEqual(1_100_000 + 7200, plan.Steps.Last().UT, 1e-6);
        Assert.AreEqual(1_500_000 + 7200, plan.ArrivalUT, 1e-6);
    }

    [Test]
    public void ApplySolve_RetimedCorrectionsStayAfterNow()
    {
        var plan = HandPlan();
        // A much earlier arrival: the new times would fall before the correction just flown.
        TransferFlight.ApplySolve(plan, 3, 520_000, double.NaN, 500_000);
        var c = plan.Steps.Where(st => st.Kind == StepKind.Correction).ToList();
        Assert.GreaterOrEqual(c[2].UT, 500_000 + TransferFlight.CorrectionSpacing - 1e-6);
    }

    // Corrections re-timed to an arrival so early that no room is left are dropped, never put after it.
    [Test]
    public void ApplySolve_NoRoomBeforeTheArrival_CorrectionDropped()
    {
        var plan = HandPlan();
        TransferFlight.ApplySolve(plan, 3, 500_100, double.NaN, 500_000);
        var c = plan.Steps.Where(st => st.Kind == StepKind.Correction).ToList();
        Assert.AreEqual(2, c.Count, "the approach correction had no room left");
        foreach (var st in plan.Steps.Where(st => st.Kind != StepKind.Capture))
            Assert.Less(st.UT, 500_100);
    }

    [Test]
    public void PreferPass_ConvergedWinsWhenClose()
    {
        Assert.IsTrue(ArrivalTargeting.PreferPass(101, true, 100, false), "converged, 1 m/s dearer");
        Assert.IsFalse(ArrivalTargeting.PreferPass(99, false, 100, true), "not converged, 1 m/s cheaper");
        Assert.IsTrue(ArrivalTargeting.PreferPass(90, false, 100, true), "not converged, but 10 m/s cheaper");
        Assert.IsFalse(ArrivalTargeting.PreferPass(110, true, 100, false), "converged, but 10 m/s dearer");
        Assert.IsTrue(ArrivalTargeting.PreferPass(99, true, 100, true));
        Assert.IsFalse(ArrivalTargeting.PreferPass(double.NaN, true, 100, false));
        Assert.IsTrue(ArrivalTargeting.PreferPass(1000, false, double.PositiveInfinity, false));
    }

    // ---------------------------------------------------------------- burns

    [Test]
    public void GuidanceFor_EachKindOfBurn()
    {
        double mu = _s[_kerbin].Mu, soi = _s[_kerbin].Soi, r0 = 700_000;
        var r = new Vector3d(r0, 0, 0);
        var v = new Vector3d(0, Math.Sqrt(mu / r0), 0);

        // An injection onto an escape: aim at the asymptote, which the planned vector reaches.
        var injection = new PlanStep { Kind = StepKind.Injection, Body = _kerbin, DeltaV = new Vector3d(0, 1100, 0) };
        var g = TransferFlight.GuidanceFor(injection, r, v, mu, soi);
        Assert.IsInstanceOf<AsymptoteGuidance>(g);
        Assert.AreEqual(0, (g.DeltaVToGo(r, v, 0, mu) - injection.DeltaV).magnitude, 0.01);

        // A burn inside the SOI (Kerbin -> Mun): the transfer orbit.
        var toMun = new PlanStep { Kind = StepKind.Injection, Body = _kerbin, DeltaV = new Vector3d(0, 840, 0) };
        g = TransferFlight.GuidanceFor(toMun, r, v, mu, soi);
        Assert.IsInstanceOf<OrbitTargetGuidance>(g);
        Assert.AreEqual(0, (g.DeltaVToGo(r, v, 0, mu) - toMun.DeltaV).magnitude, 0.5);

        // A capture: the step's orbit, in the arrival plane.
        var capture = new PlanStep { Kind = StepKind.Capture, Body = _kerbin, TargetPeriapsisRadius = r0, TargetApoapsisRadius = double.NaN };
        g = TransferFlight.GuidanceFor(capture, r, 1.4 * v, mu, soi);
        var target = ((OrbitTargetGuidance)g).Target;
        Assert.AreEqual(r0, target.Rp);
        Assert.AreEqual(r0, target.Ra);
        Assert.AreEqual(0, (g.DeltaVToGo(r, 1.4 * v, 0, mu) + 0.4 * v).magnitude, 0.5, "back to circular");

        // The re-aim burn entering the top climb level: the plan's required excess.
        var plan = HandPlan();
        plan.ExcessBody = _kerbin;
        plan.RequiredExcess = new Vector3d(0, 900, 0);
        plan.Steps[2] = new PlanStep { Kind = StepKind.Correction, Body = _kerbin, DeltaV = new Vector3d(3, 0, 0) };
        g = TransferFlight.GuidanceFor(plan, 2, r, v, mu, soi);
        Assert.AreEqual(plan.RequiredExcess, ((AsymptoteGuidance)g).VInf);
    }

    [Test]
    public void LongBurn_MoreThanATenthOfThePeriod()
    {
        Assert.IsTrue(TransferFlight.IsLongBurn(400, 3000));
        Assert.IsFalse(TransferFlight.IsLongBurn(200, 3000));
        Assert.IsFalse(TransferFlight.IsLongBurn(400, double.PositiveInfinity), "no period on an escape");
        Assert.IsFalse(TransferFlight.IsLongBurn(double.NaN, 3000));
    }

    [Test]
    public void NodeComponents_ProgradeNormalRadial()
    {
        var r = new Vector3d(1, 0, 0);
        var v = new Vector3d(0, 1, 0);
        TransferFlight.NodeComponents(new Vector3d(2, 3, 5), r, v, out double prograde, out double normal, out double radial);
        Assert.AreEqual(3, prograde, 1e-12);
        Assert.AreEqual(5, normal, 1e-12);
        Assert.AreEqual(2, radial, 1e-12);
    }

    [Test]
    public void NodeFlown_GoneAndOrbitChanged()
    {
        var v = new Vector3d(0, 2000, 0);
        var moved = v + new Vector3d(0, 50, 0);
        Assert.AreEqual(NodeState.Waiting, TransferFlight.NodeFlown(true, false, 60, 50, false, v, moved), "still before its time");
        Assert.AreEqual(NodeState.Flown, TransferFlight.NodeFlown(false, false, 0, 50, false, v, moved), "removed after the burn (the Node tab)");
        Assert.AreEqual(NodeState.Flown, TransferFlight.NodeFlown(false, false, 0, 50, true, v, v));
        Assert.AreEqual(NodeState.RemovedUnflown, TransferFlight.NodeFlown(false, false, 0, 50, false, v, v + new Vector3d(0.1, 0, 0)));
        Assert.AreEqual(NodeState.Waiting, TransferFlight.NodeFlown(true, true, 60, 50, false, v, v + new Vector3d(0.1, 0, 0)), "time passed, not flown yet");
    }

    // A node left on the plan: the player cuts the throttle halfway (to stage or re-aim), or another
    // pilot burns a little after the node's time. Neither is the node flown.
    [Test]
    public void NodeFlown_ThrottleCutMidBurn_NotFlown()
    {
        var v = new Vector3d(0, 2000, 0);
        var half = v + new Vector3d(0, 25, 0); // 25 of 50 m/s
        Assert.AreEqual(NodeState.Waiting, TransferFlight.NodeFlown(true, true, 60, 50, false, v, half), "half the node's Δv");
        Assert.AreEqual(NodeState.Waiting, TransferFlight.NodeFlown(true, true, 60, 50, false, v, v + new Vector3d(0, 1, 0)), "another pilot's small burn");
    }

    [Test]
    public void NodeFlown_FullBurnLeftOnThePlan_FlownOnceTheEnginesStayOff()
    {
        var v = new Vector3d(0, 2000, 0);
        var full = v + new Vector3d(0, 48, 0); // 96 % of 50 m/s
        Assert.AreEqual(NodeState.Waiting, TransferFlight.NodeFlown(true, true, 2, 50, false, v, full), "engines off for 2 s only");
        Assert.AreEqual(NodeState.Flown, TransferFlight.NodeFlown(true, true, TransferFlight.NodeEnginesOffSeconds, 50, false, v, full));
        Assert.AreEqual(NodeState.Flown, TransferFlight.NodeFlown(true, true, 10, 50, true, v, v), "left the SOI");
    }

    // A null read (no vessel for a frame, a new piece without its control owner yet) is waited out
    // for 30 frames and 1 s, the longer of the two; then the vessel counts as changed.
    [Test]
    public void VesselWatch_WaitsOutTransientReads()
    {
        var w = new VesselWatch();
        const string ours = "A", part = "P";
        Assert.AreEqual(VesselVerdict.Same, w.Decide(ours, part, ours, part, 0));
        Assert.AreEqual(VesselVerdict.Wait, w.Decide(null, null, ours, part, 0.01), "one null frame");
        Assert.AreEqual(VesselVerdict.Same, w.Decide(ours, null, ours, part, 0.02), "back, even without the control part");

        // A new piece whose control owner arrives a few frames later: it takes the plan over.
        Assert.AreEqual(VesselVerdict.Wait, w.Decide("B", null, ours, part, 1.0));
        Assert.AreEqual(VesselVerdict.TakeOver, w.Decide("B", part, ours, part, 1.1));

        // Another vessel for good: Changed only after 30 frames and 1 s.
        w.Reset();
        double t = 10;
        for (int i = 0; i < VesselWatch.MinFrames - 1; i++, t += 0.1)
            Assert.AreEqual(VesselVerdict.Wait, w.Decide("C", "Q", ours, part, t), $"frame {i}");
        Assert.AreEqual(VesselVerdict.Changed, w.Decide("C", "Q", ours, part, t));

        // 30 fast frames are not enough before 1 s.
        w.Reset();
        for (int i = 0; i < 40; i++)
            Assert.AreEqual(VesselVerdict.Wait, w.Decide(null, null, ours, part, 20 + i * 0.01));
        Assert.AreEqual(VesselVerdict.Changed, w.Decide(null, null, ours, part, 21.0));
    }

    [Test]
    public void Next_NoPlannedTime_MissedAtOnce()
    {
        var plan = HandPlan();
        plan.Steps.RemoveAt(5);
        plan.ArrivalUT = double.NaN;
        var d = TransferFlight.Next(plan, 5, _kerbol, _duna, 10, 100, double.NaN);
        Assert.AreEqual(FlightAction.SoiMissed, d.Action, "no limit that moves with now");
        Assert.IsTrue(double.IsNaN(d.WarpUntilUT));
    }

    // ---------------------------------------------------------------- resume and saving

    SavedPlan Saved(TransferPlan plan, TransferPhase phase, int step) =>
        TransferFlight.ToSaved(plan, _s, "0b7c1f6e-2a7e-4c55-9a39-3d1f4a7d9e10", phase, step, 500, false);

    [Test]
    public void DecideResume_ReplansAPlannedVectorCutMidBurn()
    {
        var plan = HandPlan();
        var resume = ResumeCheck.Resume();
        Assert.AreEqual(ResumeAction.Continue, TransferFlight.DecideResume(Saved(plan, TransferPhase.WaitWindow, 0), resume));
        Assert.AreEqual(ResumeAction.Replan, TransferFlight.DecideResume(Saved(plan, TransferPhase.Injection, 0), resume),
            "the injection's planned vector from a state already part burned would replay it");
        Assert.AreEqual(ResumeAction.Continue, TransferFlight.DecideResume(Saved(plan, TransferPhase.Correction, 3), resume),
            "a correction is solved again from the actual state");
        Assert.AreEqual(ResumeAction.Continue, TransferFlight.DecideResume(Saved(plan, TransferPhase.Capture, 5), resume),
            "the capture aims at its orbit from the actual state");
        Assert.AreEqual(ResumeAction.Continue, TransferFlight.DecideResume(Saved(plan, TransferPhase.Failed, 3), resume));
        Assert.AreEqual(ResumeAction.Replan, TransferFlight.DecideResume(Saved(plan, TransferPhase.Coast, 3), ResumeCheck.Replan("burned by hand")));
        Assert.AreEqual(ResumeAction.Refuse, TransferFlight.DecideResume(Saved(plan, TransferPhase.Coast, 3), ResumeCheck.Mismatch("another body")));

        // A failure in the middle of the injection: the phase is Failed, BurnStarted remembers the cut.
        var failed = TransferFlight.ToSaved(plan, _s, "id", TransferPhase.Failed, 0, 500, false, burnStarted: true);
        Assert.IsTrue(PlanStore.TryFromJson(PlanStore.ToJson(failed), out var back, out string error), error);
        Assert.IsTrue(back.BurnStarted);
        Assert.AreEqual(ResumeAction.Replan, TransferFlight.DecideResume(back, resume));
        Assert.AreEqual(ResumeAction.Continue, TransferFlight.DecideResume(Saved(plan, TransferPhase.Failed, 0), resume), "failed before ignition");
        var capture = TransferFlight.ToSaved(plan, _s, "id", TransferPhase.Failed, 5, 500, false, burnStarted: true);
        Assert.AreEqual(ResumeAction.Continue, TransferFlight.DecideResume(capture, resume), "a capture aims at its orbit from the actual state");
    }

    [Test]
    public void BurnStarted_OptionalInTheFile()
    {
        var sp = Saved(HandPlan(), TransferPhase.Coast, 2);
        var o = Newtonsoft.Json.Linq.JObject.Parse(PlanStore.ToJson(sp));
        o.Remove("burnStarted");
        Assert.IsTrue(PlanStore.TryFromJson(o.ToString(), out var back, out string error), error);
        Assert.IsFalse(back.BurnStarted);
    }

    [Test]
    public void ToSaved_NullStringsStoredEmpty_RoundTrips()
    {
        var plan = HandPlan();
        plan.CaseName = null;
        var sp = TransferFlight.ToSaved(plan, _s, null, TransferPhase.Coast, 2, 123, true);
        Assert.AreEqual("", sp.VesselId);
        Assert.AreEqual("transfer", sp.Kind);
        Assert.AreEqual("Kerbin", sp.FromBodyName);
        Assert.AreEqual("Duna", sp.ToBodyName);
        Assert.AreEqual(_s.Count, sp.BodyNames.Count);
        Assert.IsTrue(PlanStore.TryFromJson(PlanStore.ToJson(sp), out var back, out string error), error);
        Assert.AreEqual(TransferPhase.Coast, back.Phase);
        Assert.AreEqual(2, back.StepIndex);
        Assert.IsTrue(back.PrepareOnly);
    }

    [Test]
    public void RemapBodies_FollowsTheNames()
    {
        var plan = HandPlan();
        var sp = Saved(plan, TransferPhase.Coast, 2);
        // Saved when Kerbin and Eve had swapped indices.
        int eve = _s["Eve"].Index;
        sp.BodyNames[_kerbin] = "Eve";
        sp.BodyNames[eve] = "Kerbin";
        Assert.IsTrue(TransferFlight.RemapBodies(sp, _s));
        Assert.AreEqual(eve, sp.Plan.Steps[0].Body, "saved index of 'Kerbin' was Eve's");
        Assert.AreEqual(eve, sp.Plan.Request.FromBody);
        Assert.AreEqual("Kerbin", sp.BodyNames[_kerbin]);

        var missing = Saved(HandPlan(), TransferPhase.Coast, 2);
        missing.BodyNames[_duna] = "Vulcan";
        Assert.IsFalse(TransferFlight.RemapBodies(missing, _s));
        Assert.AreEqual(_duna, missing.Plan.Steps[5].Body, "nothing changed");
    }

    [Test]
    public void RemainingBudget_UsesTheMagnitudesFromTheStep()
    {
        var plan = HandPlan();
        var stages = new List<StageDv> { new StageDv { Stage = 2, DeltaVVac = 1200 }, new StageDv { Stage = 1, DeltaVVac = 700 } };
        var all = TransferFlight.RemainingBudget(plan, 0, stages);
        Assert.IsTrue(all.Ok, all.Refusal);
        Assert.AreEqual(1050 + 600 + 34, all.Needed, 1e-9, "the capture's magnitude counts though its vector is zero");
        var tight = new List<StageDv> { new StageDv { Stage = 1, DeltaVVac = 640 } };
        Assert.IsTrue(TransferFlight.RemainingBudget(plan, 2, tight).Ok, "from the corrections on, the capture and its reserve");
        var short_ = TransferFlight.RemainingBudget(plan, 2, new List<StageDv> { new StageDv { Stage = 1, DeltaVVac = 610 } });
        Assert.IsFalse(short_.Ok);
        StringAssert.Contains("capture", short_.Refusal);
    }

    [Test]
    public void Texts_ResumeOfferAndFinalOrbit()
    {
        var plan = HandPlan();
        var sp = Saved(plan, TransferPhase.Coast, 3);
        string offer = TransferFlight.ResumeOffer(sp, ResumeCheck.Resume(), 500_000 - 42 * KerbinDay, s => $"{s / KerbinDay:F0} d");
        Assert.AreEqual("Resume plan to Duna (next: mid-course correction in 42 d)", offer);
        StringAssert.Contains("planned again", TransferFlight.ResumeOffer(sp, ResumeCheck.Replan("burned by hand"), 0, s => "x"));
        StringAssert.StartsWith(PlanStore.MismatchMessage, TransferFlight.ResumeOffer(sp, ResumeCheck.Mismatch("orbits Mun"), 0, s => "x"));
        Assert.AreEqual("approach correction", TransferFlight.StepName(plan, 4));
        Assert.AreEqual("correction after the escape", TransferFlight.StepName(plan, 2));
        Assert.AreEqual("In orbit of Duna: 112 × 108 km, 0.4°", TransferFlight.FinalOrbit("Duna", 108_000, 112_000, 0.4));
        Assert.AreEqual("Capture failed: you will fly past Duna", TransferFlight.CaptureFailed("Duna"));
        StringAssert.Contains("search 12 ms", TransferFlight.Describe(plan, _s, 12));
    }

    // A multi-level plan: at the start of its second leg the vessel is in a low orbit of Duna, and that
    // leg is searched again from there (Duna -> Ike) and merged in place.
    [Test]
    public void MergeLeg_SecondLegSearchedAgainFromTheOrbitReached()
    {
        double radius = 700_000;
        var q = new TransferRequest { FromBody = _kerbin, ToBody = _s["Ike"].Index, R0 = new Vector3d(radius, 0, 0),
            V0 = new Vector3d(0, Math.Sqrt(_s[_kerbin].Mu / radius), 0) };
        var plan = TransferSearch.Find(_s, q, CancellationToken.None);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        int i = plan.Steps.FindIndex(st => st.ReplanAtLegStart);
        Assert.GreaterOrEqual(i, 1, "a later leg to re-plan");
        var before = plan.Steps.Take(i).ToList();
        int leg = plan.Steps[i].LegIndex;

        // From a low circular orbit of Duna, as after a staged capture.
        double rd = _s[_duna].LowOrbitRadius;
        double ut = plan.Steps[i].UT;
        Assert.AreEqual(LegReplan.Search, TransferFlight.LegStart(plan, i, plan.Steps[i].Body, true));
        var q2 = new TransferRequest { FromBody = _duna, ToBody = q.ToBody, R0 = new Vector3d(rd, 0, 0),
            V0 = new Vector3d(0, Math.Sqrt(_s[_duna].Mu / rd), 0), UT0 = ut };
        var replanned = TransferSearch.Find(_s, q2, CancellationToken.None);
        Assert.IsTrue(replanned.Ok, replanned.Refusal);

        TransferFlight.MergeLeg(plan, i, replanned);
        TransferFlight.MarkLegStarted(plan, i);
        CollectionAssert.AreEqual(before, plan.Steps.Take(i).ToList(), "the steps flown are kept");
        Assert.AreEqual(i + replanned.Steps.Count, plan.Steps.Count);
        Assert.IsTrue(plan.Steps.Skip(i).All(st => st.LegIndex >= leg));
        Assert.AreEqual(replanned.ArrivalUT, plan.ArrivalUT);
        Assert.AreEqual(LegReplan.None, TransferFlight.LegStart(plan, i, plan.Steps[i].Body, true), "the merged leg is not searched again");
        Assert.AreEqual(StepKind.Capture, plan.Steps.Last().Kind);
        Assert.AreEqual(q.ToBody, plan.Steps.Last().Body);
        Assert.IsTrue(TransferFlight.RemainingBudget(plan, i, new List<StageDv> { new StageDv { Stage = 1, DeltaVVac = 5000 } }).Ok);
    }

    // ---------------------------------------------------------------- one whole flight

    class Flight
    {
        public int Body;
        public Vector3d R, V;
        public double UT;
    }

    void CoastTo(Flight f, double ut)
    {
        if (!(ut > f.UT))
            return;
        var patches = PatchedPropagator.Propagate(_s, f.Body, f.R, f.V, f.UT, ut, null, 32);
        var last = patches[patches.Count - 1];
        Assert.AreEqual(PatchEnd.HorizonReached, last.End, $"coast to {ut} ended early ({last.End} at {last.EndUT})");
        f.Body = last.Body;
        f.R = last.EndR;
        f.V = last.EndV;
        f.UT = ut;
    }

    double NextSoi(Flight f, double horizon)
    {
        var patches = PatchedPropagator.Propagate(_s, f.Body, f.R, f.V, f.UT, horizon, null, 4);
        var p = patches.FirstOrDefault(x => x.End == PatchEnd.Escape || x.End == PatchEnd.Encounter);
        return p == null ? double.NaN : p.EndUT;
    }

    // Kerbin -> Duna driven by the step table only, every burn an impulse from the guidance the pilot
    // builds: the phases follow spec 4.1, the corrections are solved, skipped or flown by the rules,
    // and the capture ends in the wanted orbit.
    [Test]
    public void WholeFlight_KerbinToDuna_ByTheStepTable()
    {
        double radius = 700_000, speed = Math.Sqrt(_s[_kerbin].Mu / radius);
        var q = new TransferRequest { FromBody = _kerbin, ToBody = _duna, R0 = new Vector3d(radius, 0, 0), V0 = new Vector3d(0, speed, 0) };
        var plan = TransferSearch.Find(_s, q, CancellationToken.None);
        Assert.IsTrue(plan.Ok, plan.Refusal);
        TestContext.Out.WriteLine(TransferFlight.Describe(plan, _s, 0));
        double horizon = plan.ArrivalUT + 0.25 * plan.TripSeconds;

        var f = new Flight { Body = _kerbin, R = q.R0, V = q.V0, UT = 0 };
        var phases = new List<TransferPhase>();
        var log = new List<string>();
        double flown = 0;
        int i = 0, solves = 0;
        bool done = false;
        for (int guard = 0; guard < 400 && !done; guard++)
        {
            var d = TransferFlight.Next(plan, i, f.Body, _duna, f.UT, TransferFlight.BurnLead(0), NextSoi(f, horizon));
            if (phases.Count == 0 || phases[phases.Count - 1] != d.Phase)
                phases.Add(d.Phase);
            double mu = _s[f.Body].Mu;
            switch (d.Action)
            {
                case FlightAction.Done:
                    done = true;
                    break;
                case FlightAction.StepPassed:
                    i++;
                    break;
                case FlightAction.Warp:
                    CoastTo(f, d.WarpUntilUT);
                    break;
                case FlightAction.WaitSoi:
                    CoastTo(f, double.IsNaN(d.WarpUntilUT) ? f.UT + 600 : d.WarpUntilUT + 1);
                    break;
                case FlightAction.Burn:
                {
                    var st = plan.Steps[i];
                    CoastTo(f, st.UT);
                    var g = TransferFlight.GuidanceFor(plan, i, f.R, f.V, mu, _s[f.Body].Soi);
                    (g as IFixedAtIgnition)?.FixAtIgnition(f.R, f.V, f.UT, mu);
                    var dv = g.DeltaVToGo(f.R, f.V, f.UT, mu);
                    f.V += dv;
                    flown += dv.magnitude;
                    log.Add($"{st.Kind} {dv.magnitude:F1} m/s (planned {st.DeltaVMagnitude:F1}) by {g.GetType().Name}");
                    i++;
                    break;
                }
                case FlightAction.Solve:
                {
                    var st = plan.Steps[i];
                    CoastTo(f, Math.Max(st.UT, f.UT));
                    int n = TransferFlight.CorrectionNumber(plan, i);
                    double without = TransferFlight.PeriapsisWithout(_s, f.Body, f.R, f.V, f.UT, st.TargetBody, horizon);
                    Assert.IsTrue(ArrivalTargeting.Solve(_s, f.Body, f.R, f.V, f.UT, st.TargetBody, st.TargetPeriapsisRadius, st.TargetInclinationDeg,
                        horizon, TransferFlight.LegArrivalUT(plan, i), TransferFlight.CaptureApoapsis(plan, i),
                        out var dv, out double pe, out _, out double arrival, out double capture, out bool converged), $"correction {n}");
                    var verdict = TransferFlight.JudgeCorrection(dv.magnitude, converged, pe, st.TargetPeriapsisRadius, without,
                        _s[st.TargetBody].LowOrbitRadius - 10_000, out string why);
                    Assert.AreNotEqual(CorrectionVerdict.Refuse, verdict, why);
                    TransferFlight.ApplySolve(plan, i, arrival, capture, f.UT);
                    if (verdict == CorrectionVerdict.Fly)
                    {
                        f.V += dv;
                        flown += dv.magnitude;
                    }
                    log.Add($"correction {n}: {dv.magnitude:F3} m/s {verdict}, Pe {(pe - _s[_duna].Radius) / 1000:F1} km, without {(without - _s[_duna].Radius) / 1000:F1} km");
                    solves++;
                    i++;
                    break;
                }
                case FlightAction.Capture:
                {
                    var st = plan.Steps[i];
                    var first = PatchedPropagator.Propagate(_s, f.Body, f.R, f.V, f.UT, f.UT + 30 * KerbinDay, null, 2)[0];
                    Assert.GreaterOrEqual(first.PeriapsisUT, f.UT, "the periapsis is ahead");
                    CoastTo(f, first.PeriapsisUT);
                    var g = TransferFlight.GuidanceFor(plan, i, f.R, f.V, mu, _s[f.Body].Soi);
                    (g as IFixedAtIgnition)?.FixAtIgnition(f.R, f.V, f.UT, mu);
                    var dv = g.DeltaVToGo(f.R, f.V, f.UT, mu);
                    f.V += dv;
                    flown += dv.magnitude;
                    log.Add($"capture {dv.magnitude:F1} m/s (planned {st.DeltaVMagnitude:F1})");
                    i++;
                    break;
                }
            }
        }
        TestContext.Out.WriteLine(string.Join("; ", log));
        TestContext.Out.WriteLine("phases: " + string.Join(" -> ", phases));
        Assert.IsTrue(done, "the flight ends");
        Assert.AreEqual(_duna, f.Body);
        Assert.AreEqual(3, solves);
        CollectionAssert.IsSubsetOf(new[] { TransferPhase.WaitWindow, TransferPhase.Injection, TransferPhase.Escape, TransferPhase.Correction,
            TransferPhase.Coast, TransferPhase.Capture, TransferPhase.Done }, phases);
        Assert.Less(phases.IndexOf(TransferPhase.Injection), phases.IndexOf(TransferPhase.Escape));
        Assert.Less(phases.IndexOf(TransferPhase.Escape), phases.IndexOf(TransferPhase.Correction));
        Assert.Less(phases.IndexOf(TransferPhase.Correction), phases.IndexOf(TransferPhase.Capture));

        var el = OrbitMath.Elements(f.R, f.V, _s[_duna].Mu);
        double wanted = _s[_duna].LowOrbitRadius;
        TestContext.Out.WriteLine(TransferFlight.FinalOrbit("Duna", el.PeriapsisRadius - _s[_duna].Radius, el.ApoapsisRadius - _s[_duna].Radius,
            el.InclinationRad * 180 / Math.PI) + $"; flown {flown:F1} m/s vs plan {plan.TotalDeltaV:F1}");
        Assert.IsTrue(el.IsClosed);
        Assert.AreEqual(wanted, el.PeriapsisRadius, 2000);
        Assert.AreEqual(wanted, el.ApoapsisRadius, 2000);
        Assert.AreEqual(plan.TotalDeltaV, flown, 0.05 * plan.TotalDeltaV);
    }
}

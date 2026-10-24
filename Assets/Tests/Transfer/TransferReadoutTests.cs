using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.Transfer;
using KSP.Sim;
using NUnit.Framework;

public class TransferReadoutTests
{
    BodySystem _s;
    int _kerbol, _kerbin, _duna;

    [SetUp]
    public void SetUp()
    {
        _s = KerbolFixture.Build();
        _kerbol = _s["Kerbol"].Index;
        _kerbin = _s["Kerbin"].Index;
        _duna = _s["Duna"].Index;
    }

    // ---------------------------------------------------------------- calendar

    [Test]
    public void Date_KerbinCalendar_SixHourDays_426DayYears_FromY1D1()
    {
        Assert.AreEqual("Y1, D1, 0h 00m", KerbinCalendar.Date(0));
        Assert.AreEqual("Y1, D1, 3h 05m", KerbinCalendar.Date(3 * 3600 + 5 * 60 + 59));
        Assert.AreEqual("Y1, D2, 0h 00m", KerbinCalendar.Date(21600));
        Assert.AreEqual("Y1, D426, 5h 59m", KerbinCalendar.Date(426 * 21600 - 1));
        Assert.AreEqual("Y2, D1, 0h 00m", KerbinCalendar.Date(426 * 21600));
        Assert.AreEqual("-", KerbinCalendar.Date(double.NaN));
    }

    [Test]
    public void Duration_LargestUnitsFirst()
    {
        Assert.AreEqual("35 s", KerbinCalendar.Duration(35.7));
        Assert.AreEqual("4m 10s", KerbinCalendar.Duration(250));
        Assert.AreEqual("3h 05m", KerbinCalendar.Duration(3 * 3600 + 5 * 60));
        Assert.AreEqual("12d 4h", KerbinCalendar.Duration(12 * 21600 + 4 * 3600 + 100));
        Assert.AreEqual("2y 10d 3h", KerbinCalendar.Duration(2 * KerbinCalendar.YearSeconds + 10 * 21600 + 3 * 3600));
        Assert.AreEqual("-4m 10s", KerbinCalendar.Duration(-250));
        Assert.AreEqual("no limit", KerbinCalendar.Duration(double.PositiveInfinity));
    }

    // ---------------------------------------------------------------- inputs

    [Test]
    public void ParseDays_EmptyIsNoCap_CommaIsTheDecimalPoint_ZeroOrTextRefused()
    {
        Assert.IsTrue(TransferReadout.TryParseDays("", out double s, out _));
        Assert.IsTrue(double.IsPositiveInfinity(s));
        Assert.IsTrue(TransferReadout.TryParseDays("  ", out s, out _));
        Assert.IsTrue(double.IsPositiveInfinity(s));
        Assert.IsTrue(TransferReadout.TryParseDays("12", out s, out _));
        Assert.AreEqual(12 * 21600, s, 1e-9);
        Assert.IsTrue(TransferReadout.TryParseDays("1,5", out s, out _));
        Assert.AreEqual(1.5 * 21600, s, 1e-9);
        Assert.IsFalse(TransferReadout.TryParseDays("abc", out _, out string error));
        StringAssert.Contains("not a number of days", error);
        Assert.IsFalse(TransferReadout.TryParseDays("0", out _, out _));
        Assert.IsFalse(TransferReadout.TryParseDays("-3", out _, out _));
    }

    // The tab's input, then the pilot's resolution against its own snapshot of the bodies.
    bool Req(string target, ArrivalKind arrival, bool low, double pe, double ap, bool setInc, double inc, string days,
        out TransferRequest q, out string error)
    {
        q = null;
        return TransferReadout.TryInput(target, arrival, low, pe, ap, setInc, inc, days, out var input, out error)
            && input.Resolve(_s, out q, out error);
    }

    [Test]
    public void Request_LowCircular_LeavesPeAndApToTheSearch_KeepsTheChosenPlane()
    {
        Assert.IsTrue(Req("Duna", ArrivalKind.Orbit, true, 5, 7, true, 30, "", out var q, out string error), error);
        Assert.AreEqual(_duna, q.ToBody);
        Assert.AreEqual(ArrivalKind.Orbit, q.Arrival);
        Assert.IsNaN(q.ArrivalPeriapsisRadius);
        Assert.IsNaN(q.ArrivalApoapsisRadius);
        Assert.AreEqual(30, q.ArrivalInclinationDeg, 1e-9, "a low circular orbit in a chosen plane");
        Assert.IsTrue(double.IsPositiveInfinity(q.MaxTripSeconds));
        Assert.IsTrue(Req("Duna", ArrivalKind.Orbit, true, 5, 7, false, 30, "", out q, out error), error);
        Assert.IsNaN(q.ArrivalInclinationDeg, "inclination unticked: free");
    }

    [Test]
    public void Request_CustomOrbit_AltitudesToRadii_OfTheResolvingSnapshot()
    {
        Assert.IsTrue(Req("Duna", ArrivalKind.Orbit, false, 60, 120, true, 45, "300", out var q, out string error), error);
        Assert.AreEqual(320000 + 60000, q.ArrivalPeriapsisRadius, 1e-6);
        Assert.AreEqual(320000 + 120000, q.ArrivalApoapsisRadius, 1e-6);
        Assert.AreEqual(45, q.ArrivalInclinationDeg, 1e-9);
        Assert.AreEqual(300 * 21600, q.MaxTripSeconds, 1e-6);

        Assert.IsTrue(Req("Duna", ArrivalKind.Encounter, false, 60, 10, false, 45, "", out q, out error), error);
        Assert.AreEqual(380000, q.ArrivalPeriapsisRadius, 1e-6);
        Assert.IsNaN(q.ArrivalApoapsisRadius, "an encounter has no Ap");
        Assert.IsNaN(q.ArrivalInclinationDeg, "inclination unticked: free");

        // The altitudes stay altitudes until the resolution: another snapshot, another radius.
        Assert.IsTrue(TransferReadout.TryInput("Duna", ArrivalKind.Orbit, false, 60, 120, false, 0, "", out var input, out error), error);
        Assert.AreEqual(60000, input.PeriapsisAltitude, 1e-9);
        var other = KerbolFixture.Build();
        other["Duna"].Radius = 400000;
        Assert.IsTrue(input.Resolve(other, out q, out error), error);
        Assert.AreEqual(460000, q.ArrivalPeriapsisRadius, 1e-6);
    }

    // The target is found by name in the resolving snapshot, whatever its index there.
    [Test]
    public void Request_TargetByName()
    {
        Assert.IsTrue(TransferReadout.TryInput("Ike", ArrivalKind.Orbit, true, 0, 0, false, 0, "", out var input, out string error), error);
        Assert.IsTrue(input.Resolve(_s, out var q, out error), error);
        Assert.AreEqual(_s["Ike"].Index, q.ToBody);
        input.TargetName = "Eeloo";
        Assert.IsFalse(input.Resolve(_s, out _, out error));
        Assert.AreEqual("The target Eeloo is not among the game's bodies: choose it again.", error);
        Assert.IsFalse(input.Resolve(null, out _, out _));
    }

    [Test]
    public void Request_Refusals()
    {
        Assert.IsFalse(Req("", ArrivalKind.Orbit, true, 0, 0, false, 0, "", out _, out string error));
        Assert.AreEqual("Choose a target body.", error);
        Assert.IsFalse(Req("Duna", ArrivalKind.Orbit, false, 100, 50, false, 0, "", out _, out error));
        StringAssert.Contains("Ap cannot be lower", error);
        Assert.IsFalse(Req("Duna", ArrivalKind.Orbit, false, -1, 50, false, 0, "", out _, out error));
        StringAssert.Contains("above the surface", error);
        Assert.IsFalse(Req("Duna", ArrivalKind.Orbit, true, 60, 60, true, 200, "", out _, out error));
        StringAssert.Contains("inclination", error);
        Assert.IsFalse(Req("Duna", ArrivalKind.Orbit, true, 60, 60, false, 0, "x", out _, out error));
        StringAssert.Contains("Max trip", error);
    }

    // ---------------------------------------------------------------- plan

    TransferPlan KerbinToDuna()
    {
        double mu = _s[_kerbin].Mu, r = 700_000;
        var plan = new TransferPlan
        {
            Ok = true,
            CaseName = TransferSearch.Siblings,
            DepartureUT = 10 * 21600,
            ArrivalUT = 10 * 21600 + 200 * 21600,
            ReserveDeltaV = 33,
            TotalDeltaV = 1050 + 586 + 33,
            Request = new TransferRequest
            {
                FromBody = _kerbin, ToBody = _duna,
                R0 = new Vector3d(r, 0, 0), V0 = new Vector3d(0, Math.Sqrt(mu / r), 0),
            },
        };
        plan.Steps.Add(new PlanStep { Kind = StepKind.Injection, Body = _kerbin, UT = plan.DepartureUT, DeltaVMagnitude = 1050.44, Explanation = "Oberth" });
        plan.Steps.Add(new PlanStep { Kind = StepKind.Escape, Body = _kerbol, UT = plan.DepartureUT + 2 * 21600 });
        plan.Steps.Add(new PlanStep { Kind = StepKind.Correction, Body = _kerbol, UT = plan.DepartureUT + 3 * 21600 });
        plan.Steps.Add(new PlanStep
        {
            Kind = StepKind.Capture, Body = _duna, UT = plan.ArrivalUT, DeltaVMagnitude = 586,
            TargetBody = _duna, TargetPeriapsisRadius = 380_000,
        });
        return plan;
    }

    [Test]
    public void Headlines_DatesAndWaits()
    {
        var plan = KerbinToDuna();
        Assert.AreEqual("Departure: Y1, D11, 0h 00m, in 9d 0h", TransferReadout.Departure(plan, 21600));
        Assert.AreEqual("Departure: Y1, D11, 0h 00m, now", TransferReadout.Departure(plan, plan.DepartureUT + 5));
        Assert.AreEqual("Trip: 200d 0h, arrival Y1, D211, 0h 00m", TransferReadout.Trip(plan));
        var budget = new BudgetResult { Ok = true, Available = 2500, Needed = 1669 };
        Assert.AreEqual("Total 1669 m/s, reserve included (33 m/s); the vessel has 2500 m/s", TransferReadout.Totals(plan, budget));
        Assert.AreEqual("Margin: 831 m/s left at the arrival", TransferReadout.Margin(budget));
        Assert.AreEqual("Margin: short by 69 m/s", TransferReadout.Margin(new BudgetResult { Available = 1600, Needed = 1669 }));
    }

    [Test]
    public void StepTable_KindWhenDvExplanation()
    {
        var plan = KerbinToDuna();
        var rows = TransferReadout.Steps(plan, _s, plan.DepartureUT + 21600, 1);
        Assert.AreEqual(4, rows.Count);
        Assert.AreEqual("Injection burn (Kerbin)", rows[0].Kind);
        Assert.AreEqual("1050.4 m/s", rows[0].DeltaV);
        Assert.AreEqual("Oberth", rows[0].Explanation);
        Assert.IsTrue(rows[0].Done);
        StringAssert.EndsWith(", done", rows[0].When);
        Assert.AreEqual("SOI exit (Kerbol)", rows[1].Kind);
        Assert.AreEqual("-", rows[1].DeltaV);
        Assert.AreEqual("Y1, D13, 0h 00m, in 1d 0h", rows[1].When);
        Assert.IsFalse(rows[1].Done);
        Assert.AreEqual("Correction after the escape (Kerbol)", rows[2].Kind);
        Assert.AreEqual("reserve", rows[2].DeltaV);
        Assert.AreEqual("Capture burn (Duna)", rows[3].Kind);
        Assert.AreEqual("586.0 m/s", rows[3].DeltaV);
    }

    static StageDv S(int stage, double dv, bool port = false, bool chute = false, bool command = false) =>
        new StageDv { Stage = stage, DeltaVVac = dv, HasDockingPort = port, HasParachute = chute, HasCommandPart = command };

    [Test]
    public void StageTable_WhatEachStagePays_AndTheStagesToFireByHand()
    {
        var plan = KerbinToDuna();
        var stages = new List<StageDv> { S(5, 1200, command: true), S(4, 800, chute: true), S(3, 500, command: true) };
        var budget = TransferFlight.RemainingBudget(plan, 0, stages);
        Assert.IsTrue(budget.Ok, budget.Refusal);
        var rows = TransferReadout.Stages(plan, 0, stages, budget, 4, out string note);
        Assert.AreEqual(3, rows.Count);
        Assert.AreEqual("Stage 5: 1200 m/s, pays the injection burn, capture burn", rows[0].Text);
        Assert.IsFalse(rows[0].ByHand, "stage 5 is staged already (the next to fire is 4)");
        Assert.AreEqual("Stage 4: 800 m/s, pays the capture burn. Stage it by hand: it drops a parachute", rows[1].Text);
        Assert.IsTrue(rows[1].ByHand);
        Assert.IsTrue(rows[2].ByHand);
        Assert.AreEqual("", rows[2].Pays);
        StringAssert.Contains("not needed", rows[2].Text);
        Assert.AreEqual("The Δv from stage 4 on needs you to stage by hand: the mod does not fire a stage that drops a parachute.", note);
    }

    // During the flight the budget starts at the step reached: its indices are offset.
    [Test]
    public void StageTable_FromALaterStep_MapsTheStepsBack()
    {
        var plan = KerbinToDuna();
        var stages = new List<StageDv> { S(5, 1200), S(4, 800, chute: true) };
        var budget = TransferFlight.RemainingBudget(plan, 3, stages);
        var rows = TransferReadout.Stages(plan, 3, stages, budget, null, out string note);
        Assert.AreEqual("capture burn", rows[0].Pays);
        Assert.AreEqual("", rows[1].Pays);
        Assert.IsNull(note, "the guarded stage is not needed");
    }

    [Test]
    public void StageTable_NotEnough_SaysNoStageIsLeft()
    {
        var plan = KerbinToDuna();
        var stages = new List<StageDv> { S(5, 1200) };
        var budget = TransferFlight.RemainingBudget(plan, 0, stages);
        Assert.IsFalse(budget.Ok);
        TransferReadout.Stages(plan, 0, stages, budget, null, out string note);
        Assert.AreEqual("No stage is left for the last step(s).", note);
    }

    // An empty stage that drops a parachute between two paying stages: the mod stops there, so
    // the Δv of stage 3 needs the player to stage by hand, though stage 4 itself pays nothing.
    [Test]
    public void StageTable_EmptyGuardedStageBetweenPayingStages_GetsTheNote()
    {
        var plan = KerbinToDuna();
        var stages = new List<StageDv> { S(5, 1200), S(4, 0, chute: true), S(3, 800) };
        var budget = TransferFlight.RemainingBudget(plan, 0, stages);
        Assert.IsTrue(budget.Ok, budget.Refusal);
        var rows = TransferReadout.Stages(plan, 0, stages, budget, 4, out string note);
        Assert.AreEqual("", rows[1].Pays);
        Assert.IsTrue(rows[1].ByHand);
        Assert.AreEqual("capture burn", rows[2].Pays);
        Assert.AreEqual("The Δv from stage 4 on needs you to stage by hand: the mod does not fire a stage that drops a parachute.", note);
    }

    // On the pad (nothing staged yet), the first stage is still to fire and is judged too; once
    // every stage is staged, none is.
    [Test]
    public void StageTable_FirstStage_FlaggedUnlessStagedAlready()
    {
        var plan = KerbinToDuna();
        var stages = new List<StageDv> { S(5, 1200, command: true), S(4, 800) };
        var budget = TransferFlight.RemainingBudget(plan, 0, stages);
        var notStaged = TransferReadout.Stages(plan, 0, stages, budget, 5, out string note);
        Assert.IsTrue(notStaged[0].ByHand);
        Assert.AreEqual("The Δv from stage 5 on needs you to stage by hand: the mod does not fire a stage that drops the command part.", note);
        var staged = TransferReadout.Stages(plan, 0, stages, budget, 4, out note);
        Assert.IsFalse(staged[0].ByHand);
        Assert.IsNull(note);
        var unknown = TransferReadout.Stages(plan, 0, stages, budget, null, out note);
        Assert.IsFalse(unknown[0].ByHand, "unknown: the first stage is taken as the active one");
    }

    // A 100 km Kerbin orbit lasts ~1960 s and the 380 km Duna orbit ~2680 s: 10 % of them is the limit.
    [Test]
    public void LongBurns_DepartureAndCaptureOrbits()
    {
        var plan = KerbinToDuna();
        var slow = TransferReadout.LongBurnWarnings(plan, _s, dv => dv / 2);
        Assert.AreEqual(2, slow.Count, string.Join("\n", slow));
        StringAssert.StartsWith("The injection burn should last about 8m 45s, more than 10 % of the orbit period (32m ", slow[0]);
        StringAssert.StartsWith("The capture burn should last about 4m 53s", slow[1]);
        Assert.AreEqual(0, TransferReadout.LongBurnWarnings(plan, _s, dv => dv / 10).Count);
        Assert.AreEqual(0, TransferReadout.LongBurnWarnings(plan, _s, dv => double.NaN).Count);
    }
}

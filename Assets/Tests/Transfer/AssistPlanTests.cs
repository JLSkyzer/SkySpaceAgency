using System;
using System.Linq;
using SkySpaceAgency.Node;
using SkySpaceAgency.Transfer;
using KSP.Sim;
using NUnit.Framework;

// The flyby step of a gravity-assist plan (4.6): the step table, the legs, the guidance, the resume
// routes, the plan file and the readout.
public class AssistPlanTests
{
    BodySystem _s;
    int _kerbin, _kerbol, _eve, _moho, _mun;

    const double FlybyUT = 4_000_000;

    [SetUp]
    public void SetUp()
    {
        _s = KerbolFixture.Build();
        _kerbin = _s["Kerbin"].Index;
        _kerbol = _s["Kerbol"].Index;
        _eve = _s["Eve"].Index;
        _moho = _s["Moho"].Index;
        _mun = _s["Mun"].Index;
    }

    static readonly Vector3d WantedOut = new Vector3d(1200, -3400, 150);

    // Kerbin -> Moho via Eve by hand, in the leg layout of the plan's decision 7.
    TransferPlan SiblingPlan(double flybyDv)
    {
        var q = new TransferRequest { FromBody = _kerbin, ToBody = _moho, R0 = new Vector3d(700_000, 0, 0), V0 = new Vector3d(0, 2246, 0) };
        double evePe = _s[_eve].LowOrbitRadius + 50_000, mohoPe = _s[_moho].LowOrbitRadius;
        PlanStep S(StepKind kind, int body, double ut, int leg, int target, double pe, double dv = 0) => new PlanStep
        {
            Kind = kind, Body = body, UT = ut, DeltaVMagnitude = dv, LegIndex = leg, ReplanAtLegStart = leg > 0,
            TargetBody = target, TargetPeriapsisRadius = pe,
        };
        var injection = S(StepKind.Injection, _kerbin, 1000, 0, _eve, evePe, 1100);
        injection.DeltaV = new Vector3d(0, 1100, 0);
        var flyby = S(StepKind.Flyby, _eve, FlybyUT, 1, _moho, mohoPe, flybyDv);
        flyby.TargetExcess = WantedOut;
        return new TransferPlan
        {
            Ok = true, Request = q, DepartureUT = 1000, ArrivalUT = 7_000_000, ReserveDeltaV = 60, TotalDeltaV = 3100,
            CaseName = TransferSearch.Siblings + " via Eve", Assist = AssistKind.Sibling, FlybyBody = _eve,
            ExcessBody = _kerbin, RequiredExcess = new Vector3d(-900, 300, 0),
            Steps =
            {
                injection,
                S(StepKind.Escape, _kerbin, 20_000, 0, _eve, evePe),
                S(StepKind.Correction, _kerbol, 40_000, 0, _eve, evePe),
                S(StepKind.Correction, _kerbol, 2_000_000, 0, _eve, evePe),
                S(StepKind.Correction, _kerbol, 3_800_000, 0, _eve, evePe),
                flyby,
                S(StepKind.Escape, _eve, FlybyUT + 300_000, 1, _moho, mohoPe),
                S(StepKind.Correction, _kerbol, 4_400_000, 2, _moho, mohoPe),
                S(StepKind.Correction, _kerbol, 5_500_000, 2, _moho, mohoPe),
                S(StepKind.Correction, _kerbol, 6_800_000, 2, _moho, mohoPe),
                S(StepKind.Capture, _moho, 7_000_000, 2, _moho, mohoPe, 1500),
            },
        };
    }

    // Kerbin -> Duna with a departure flyby of the Mun: leg 2 starts with the re-aim burn in Kerbin.
    TransferPlan DeparturePlan()
    {
        int duna = _s["Duna"].Index;
        var plan = new TransferPlan
        {
            Ok = true, Assist = AssistKind.Departure, FlybyBody = _mun, ExcessBody = _kerbin, RequiredExcess = new Vector3d(950, 100, 0),
            Request = new TransferRequest { FromBody = _kerbin, ToBody = duna },
        };
        var inj = new PlanStep { Kind = StepKind.Injection, Body = _kerbin, UT = 100, DeltaV = new Vector3d(0, 860, 0), DeltaVMagnitude = 860, LegIndex = 0 };
        var fly = new PlanStep { Kind = StepKind.Flyby, Body = _mun, UT = 20_000, LegIndex = 1, ReplanAtLegStart = true, TargetExcess = new Vector3d(400, 500, 0) };
        var reaim = new PlanStep { Kind = StepKind.Correction, Body = _kerbin, UT = 25_000, DeltaV = new Vector3d(0.3, 0, 0), DeltaVMagnitude = 0.3, LegIndex = 2, ReplanAtLegStart = true };
        plan.Steps.Add(inj);
        plan.Steps.Add(fly);
        plan.Steps.Add(new PlanStep { Kind = StepKind.Escape, Body = _mun, UT = 22_000, LegIndex = 1, ReplanAtLegStart = true });
        plan.Steps.Add(reaim);
        plan.Steps.Add(new PlanStep { Kind = StepKind.Escape, Body = _kerbin, UT = 200_000, LegIndex = 2, ReplanAtLegStart = true });
        return plan;
    }

    // ---------------------------------------------------------------- the step table

    [Test]
    public void Next_UnpoweredFlyby_PassesAfterItsTime()
    {
        var plan = SiblingPlan(0.5);
        var d = TransferFlight.Next(plan, 5, _eve, _moho, FlybyUT - 5000, 100, FlybyUT + 300_000);
        Assert.AreEqual(FlightAction.Warp, d.Action);
        Assert.AreEqual(TransferPhase.Flyby, d.Phase);
        Assert.AreEqual(FlybyUT, d.WarpUntilUT, 1e-9);
        d = TransferFlight.Next(plan, 5, _eve, _moho, FlybyUT + 1, 100, FlybyUT + 300_000);
        Assert.AreEqual(FlightAction.StepPassed, d.Action);
        Assert.IsFalse(TransferFlight.IsBurn(plan.Steps[5]));
    }

    [Test]
    public void Next_PoweredFlyby_FliesAtItsLead()
    {
        var plan = SiblingPlan(300);
        Assert.IsTrue(TransferFlight.IsBurn(plan.Steps[5]));
        var d = TransferFlight.Next(plan, 5, _eve, _moho, FlybyUT - 5000, 100, FlybyUT + 300_000);
        Assert.AreEqual(FlightAction.Warp, d.Action);
        Assert.AreEqual(FlybyUT - 100, d.WarpUntilUT, 1e-9);
        d = TransferFlight.Next(plan, 5, _eve, _moho, FlybyUT - 100, 100, FlybyUT + 300_000);
        Assert.AreEqual(FlightAction.Flyby, d.Action);
        Assert.AreEqual(TransferPhase.Flyby, d.Phase);
    }

    [Test]
    public void Next_Flyby_PassedWhenTheSoiIsLeft()
    {
        var plan = SiblingPlan(300);
        // Not entered yet: coast to the SOI entry.
        var d = TransferFlight.Next(plan, 5, _kerbol, _moho, FlybyUT - 200_000, 100, FlybyUT - 100_000);
        Assert.AreEqual(FlightAction.Warp, d.Action);
        // Entered (its leg started), then left before the step was flown: passed.
        TransferFlight.MarkLegStarted(plan, 5);
        d = TransferFlight.Next(plan, 5, _kerbol, _moho, FlybyUT + 10, 100, double.NaN);
        Assert.AreEqual(FlightAction.StepPassed, d.Action);
    }

    // ---------------------------------------------------------------- legs and guidance

    [Test]
    public void LegKind_FlybyLeg_IsFlyby()
    {
        var plan = SiblingPlan(300);
        Assert.AreEqual(LegReplan.Flyby, TransferFlight.LegKind(plan, 5));
        Assert.AreEqual(LegReplan.Flyby, TransferFlight.LegStart(plan, 5, _eve));
        Assert.AreEqual(LegReplan.SolveCorrection, TransferFlight.LegKind(plan, 7));
    }

    [Test]
    public void LegKind_AfterDepartureFlyby_AimsTheExcess()
    {
        var plan = DeparturePlan();
        Assert.AreEqual(LegReplan.Flyby, TransferFlight.LegKind(plan, 1));
        Assert.AreEqual(LegReplan.AimExcess, TransferFlight.LegKind(plan, 3));
    }

    [Test]
    public void GuidanceFor_Flyby_AimsTheTargetExcess()
    {
        var plan = SiblingPlan(300);
        var g = TransferFlight.GuidanceFor(plan, 5, new Vector3d(1e6, 0, 0), new Vector3d(0, 5000, 0), _s[_eve].Mu, _s[_eve].Soi);
        Assert.IsInstanceOf<AsymptoteGuidance>(g);
        // At a point already on a hyperbola with that asymptote, nothing is left to burn.
        Assert.IsTrue(Hyperbola.VelocityForAsymptote(new Vector3d(1e6, 0, 0), WantedOut, _s[_eve].Mu, out var vA, out _));
        Assert.Less(g.DeltaVToGo(new Vector3d(1e6, 0, 0), vA, FlybyUT, _s[_eve].Mu).magnitude, 1e-3);
        Assert.AreEqual("flyby", TransferFlight.StepName(plan, 5));
        Assert.AreEqual("flyby (no burn)", TransferFlight.StepName(SiblingPlan(0), 5));
    }

    // ---------------------------------------------------------------- resume

    [Test]
    public void RouteAssistResume_EachStage()
    {
        var plan = SiblingPlan(300);
        Assert.AreEqual(AssistResumeRoute.FullSearch, TransferFlight.RouteAssistResume(plan, 0, _kerbin, false));
        Assert.AreEqual(AssistResumeRoute.BeforeFlyby, TransferFlight.RouteAssistResume(plan, 3, _kerbol, false));
        Assert.AreEqual(AssistResumeRoute.InFlybySoi, TransferFlight.RouteAssistResume(plan, 5, _eve, true));
        Assert.AreEqual(AssistResumeRoute.AfterFlyby, TransferFlight.RouteAssistResume(plan, 5, _eve, false));
        Assert.AreEqual(AssistResumeRoute.AfterFlyby, TransferFlight.RouteAssistResume(plan, 7, _kerbol, false));
    }

    // Final review I1: burns flown but still in a closed orbit of the departure body (a plane change):
    // the whole assist search again.
    [Test]
    public void RouteAssistResume_ClosedInTheDepartureBody_FullSearch()
    {
        var plan = SiblingPlan(300);
        Assert.AreEqual(AssistResumeRoute.FullSearch, TransferFlight.RouteAssistResume(plan, 1, _kerbin, false, closedInDeparture: true));
        Assert.AreEqual(AssistResumeRoute.BeforeFlyby, TransferFlight.RouteAssistResume(plan, 1, _kerbin, false, closedInDeparture: false));
    }

    [Test]
    public void DecideResume_FlybyAhead_Replans()
    {
        var plan = SiblingPlan(0);
        Assert.IsTrue(TransferFlight.SavedVectorsAhead(plan, 3));
        var saved = new SavedPlan { Plan = plan, StepIndex = 3, Phase = TransferPhase.Coast };
        Assert.AreEqual(ResumeAction.Replan, TransferFlight.DecideResume(saved, ResumeCheck.Resume()));
    }

    // ---------------------------------------------------------------- the plan file

    SavedPlan Saved(TransferPlan plan, int stepIndex) => new SavedPlan
    {
        VesselId = "6a1e2c3d-0000-4000-8000-000000000046", Kind = SavedPlan.KindTransfer, Plan = plan, Phase = TransferPhase.Coast,
        StepIndex = stepIndex, SavedUT = 2_000_000, FromBodyName = "Kerbin", ToBodyName = "Moho",
        BodyNames = Enumerable.Range(0, _s.Count).Select(i => _s[i].Name).ToList(),
    };

    [Test]
    public void RoundTrip_KeepsTheFlyby()
    {
        var p = Saved(SiblingPlan(300), 3);
        string json = PlanStore.ToJson(p);
        Assert.IsTrue(PlanStore.TryFromJson(json, out var b, out string error), error);
        Assert.AreEqual(json, PlanStore.ToJson(b));
        Assert.AreEqual(AssistKind.Sibling, b.Plan.Assist);
        Assert.AreEqual(_eve, b.Plan.FlybyBody);
        Assert.AreEqual(StepKind.Flyby, b.Plan.Steps[5].Kind);
        Assert.AreEqual(0, (b.Plan.Steps[5].TargetExcess - WantedOut).magnitude);
        Assert.AreEqual(Vector3d.zero, b.Plan.Steps[4].TargetExcess);
    }

    [Test]
    public void NoAssistKeys_ReadsAsBefore()
    {
        var plan = SiblingPlan(300);
        plan.Assist = AssistKind.None;
        plan.FlybyBody = -1;
        plan.Steps.RemoveAt(6);
        plan.Steps.RemoveAt(5);
        string json = PlanStore.ToJson(Saved(plan, 0));
        StringAssert.DoesNotContain("assist", json);
        StringAssert.DoesNotContain("flybyBody", json);
        StringAssert.DoesNotContain("targetExcess", json);
        Assert.IsTrue(PlanStore.TryFromJson(json, out var b, out string error), error);
        Assert.AreEqual(AssistKind.None, b.Plan.Assist);
        Assert.AreEqual(-1, b.Plan.FlybyBody);
        Assert.IsTrue(b.Plan.Steps.All(st => st.TargetExcess == Vector3d.zero));
    }

    [Test]
    public void Check_PassedFlyby_IsAMilestone()
    {
        var plan = SiblingPlan(0);
        var p = Saved(plan, 5);
        p.SavedUT = FlybyUT + 10_000;
        // In Eve's SOI, past the planned periapsis: no missed burn.
        var c = PlanStore.Check(p, _s, _eve, new Vector3d(5e6, 0, 0), new Vector3d(0, 3000, 0), FlybyUT + 10_000);
        Assert.AreNotEqual(ResumeVerdict.Mismatch, c.Verdict, c.Reason);
        StringAssert.DoesNotContain("Flyby burn was due", c.Reason ?? "");
    }

    // ---------------------------------------------------------------- readout

    [Test]
    public void Rows_Flyby_PoweredAndNot()
    {
        var plan = SiblingPlan(312.34);
        plan.Steps[5].Explanation = "Flyby of Eve";
        var row = TransferReadout.Steps(plan, _s, 0, 0)[5];
        Assert.AreEqual("Flyby (Eve)", row.Kind);
        Assert.AreEqual("312.3 m/s", row.DeltaV);
        Assert.AreEqual("Flyby of Eve", row.Explanation);
        row = TransferReadout.Steps(SiblingPlan(0.4), _s, 0, 0)[5];
        Assert.AreEqual("Flyby (Eve)", row.Kind);
        Assert.AreEqual("no burn", row.DeltaV);
    }
}

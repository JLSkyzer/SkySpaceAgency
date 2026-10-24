using System;
using System.Collections.Generic;
using K2D2.OrbitPlanning;
using K2D2.Rendezvous;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class RendezvousFlightTests
{
    const double Mu = 3.5316e12;       // Kerbin, m³/s²
    const double LowOrbit = 670e3;
    const double R = 700e3;
    const double Ut0 = 1000;
    const double Deg = Math.PI / 180;

    static void Circular(double r, double angle, double incl, double lan, out Vector3d pos, out Vector3d vel)
    {
        var node = new Vector3d(Math.Cos(lan * Deg), Math.Sin(lan * Deg), 0);
        var normal = OrbitMath.Rotate(OrbitMath.North, node, -incl * Deg);
        Vector3d inPlane = Vector3d.Cross(normal, node);
        Vector3d u = Math.Cos(angle * Deg) * node + Math.Sin(angle * Deg) * inPlane;
        Vector3d w = Vector3d.Cross(normal, u);
        pos = r * u;
        vel = Math.Sqrt(Mu / r) * w;
    }

    static RvPlan Plan(params RvStepKind[] kinds)
    {
        var plan = new RvPlan { Ok = true };
        double ut = 2000;
        foreach (var k in kinds)
        {
            plan.Steps.Add(new RvStep { Kind = k, UT = ut, DeltaVMagnitude = 10 });
            ut += 1000;
        }
        plan.ArrivalUT = ut - 1000;
        return plan;
    }

    // ------------------------------------------------------------ phases

    [Test]
    public void PhasingAndIntercept_AreTheInterceptPhase()
    {
        Assert.AreEqual(RvPhase.Intercept, RendezvousFlight.PhaseOf(RvStepKind.Phasing));
        Assert.AreEqual(RvPhase.Intercept, RendezvousFlight.PhaseOf(RvStepKind.Intercept));
        Assert.AreEqual(RvPhase.PlaneMatch, RendezvousFlight.PhaseOf(RvStepKind.PlaneMatch));
        Assert.AreEqual(RvPhase.Correction, RendezvousFlight.PhaseOf(RvStepKind.Correction));
        Assert.AreEqual(RvPhase.Brake, RendezvousFlight.PhaseOf(RvStepKind.Brake));
    }

    [Test]
    public void PhasingWait_IsTheCoastAfterTheInterceptBurn()
    {
        foreach (var burn in new[] { RvStepKind.Phasing, RvStepKind.Intercept })
        {
            var plan = Plan(RvStepKind.PlaneMatch, burn, RvStepKind.Correction, RvStepKind.Brake);
            Assert.AreEqual(RvPhase.PlaneMatch, RendezvousFlight.WaitPhase(plan, 0));
            Assert.AreEqual(RvPhase.Intercept, RendezvousFlight.WaitPhase(plan, 1), "waiting for the intercept burn itself");
            Assert.AreEqual(RvPhase.PhasingWait, RendezvousFlight.WaitPhase(plan, 2), "the coast after it");
            Assert.AreEqual(RvPhase.PhasingWait, RendezvousFlight.WaitPhase(plan, 3));
        }
    }

    [Test]
    public void Next_WarpsFarAway_WaitsWithin2km_BurnsWhenDue()
    {
        var plan = Plan(RvStepKind.Phasing, RvStepKind.Correction, RvStepKind.Brake);
        double lead = 100;

        var d = RendezvousFlight.Next(plan, 0, 1500, lead, 50_000);
        Assert.AreEqual(RvAction.Warp, d.Action);
        Assert.AreEqual(1900, d.WarpUntilUT, 1e-9, "the warp stops one lead before the burn");
        Assert.AreEqual(RvPhase.Intercept, d.Phase);

        d = RendezvousFlight.Next(plan, 0, 1500, lead, 1999);
        Assert.AreEqual(RvAction.Wait, d.Action, "never warp within 2 km");
        Assert.IsFalse(RendezvousFlight.CanWarp(double.NaN), "an unknown distance does not warp");

        d = RendezvousFlight.Next(plan, 0, 1900, lead, 50_000);
        Assert.AreEqual(RvAction.Burn, d.Action);
        Assert.AreEqual(RvPhase.Intercept, d.Phase);

        d = RendezvousFlight.Next(plan, 1, 2500, lead, 50_000);
        Assert.AreEqual(RvAction.Warp, d.Action);
        Assert.AreEqual(RvPhase.PhasingWait, d.Phase);

        d = RendezvousFlight.Next(plan, 1, 2950, lead, 50_000);
        Assert.AreEqual(RvAction.Solve, d.Action, "the correction is solved when it falls due");
        Assert.AreEqual(RvPhase.Correction, d.Phase);

        d = RendezvousFlight.Next(plan, 2, 3950, lead, 300);
        Assert.AreEqual(RvAction.Burn, d.Action);
        Assert.AreEqual(RvPhase.Brake, d.Phase);

        d = RendezvousFlight.Next(plan, 3, 9999, lead, 200);
        Assert.AreEqual(RvAction.Done, d.Action);
    }

    [Test]
    public void Next_MissedBurn_ButALateCorrectionIsStillSolved()
    {
        var plan = Plan(RvStepKind.Phasing, RvStepKind.Correction, RvStepKind.Brake);
        Assert.AreEqual(RvAction.Missed, RendezvousFlight.Next(plan, 0, 2000 + RendezvousFlight.MissedSeconds + 1, 100, 1e5).Action);
        Assert.AreEqual(RvAction.Burn, RendezvousFlight.Next(plan, 0, 2000 + RendezvousFlight.MissedSeconds - 1, 100, 1e5).Action);
        Assert.AreEqual(RvAction.Solve, RendezvousFlight.Next(plan, 1, 3000 + 500, 100, 1e5).Action);
    }

    // ------------------------------------------------------------ burns

    [Test]
    public void PlaneMatchDeltaV_FromTheActualState_MatchesThePlanes()
    {
        Circular(R, 90, 0, 0, out var r, out var v);        // at the node line of the target's plane
        Circular(720e3, 30, 2, 90, out var rT, out var vT);
        var dv = RendezvousFlight.PlaneMatchDeltaV(r, v, Vector3d.Cross(rT, vT));
        var h = Vector3d.Cross(r, v + dv).normalized;
        var hT = Vector3d.Cross(rT, vT).normalized;
        Assert.That(Math.Acos(Math.Min(1, Vector3d.Dot(h, hT))) / Deg, Is.LessThan(1e-6));
        Assert.That(dv.magnitude, Is.EqualTo(2 * v.magnitude * Math.Sin(1 * Deg)).Within(1e-6));
        Assert.That((v + dv).magnitude, Is.EqualTo(v.magnitude).Within(1e-6), "a pure turn");
    }

    [Test]
    public void MatchVelocityGuidance_IsTheTargetVelocityMinusOurs()
    {
        Circular(R, 10, 0, 0, out var rT, out var vT);
        var g = new MatchVelocityGuidance(rT, vT, Ut0, Mu);
        KeplerPropagator.Propagate(rT, vT, Mu, 300, out _, out var vLater);
        var v = vLater + new Vector3d(3, -4, 0);
        Assert.That((g.DeltaVToGo(Vector3d.zero, v, Ut0 + 300, Mu) - new Vector3d(-3, 4, 0)).magnitude, Is.LessThan(1e-6));
    }

    [Test]
    public void Correction_FromAPerturbedIntercept_FindsTheRevolutionsAndArrives()
    {
        Circular(R, 0, 0, 0, out var rV, out var vV);
        Circular(760e3, 70, 0, 15, out var rT, out var vT);
        var plan = RendezvousPlanner.Plan(rV, vV, rT, vT, Mu, Ut0, LowOrbit);
        Assert.That(plan.Ok, Is.True, plan.Refusal);
        var intercept = plan.Steps.Find(s => s.Kind == RvStepKind.Phasing || s.Kind == RvStepKind.Intercept);
        var correction = plan.Steps.Find(s => s.Kind == RvStepKind.Correction);

        // Fly to the intercept, re-solve it from the actual state (a fresh Lambert), then miss it by 0.3 m/s.
        KeplerPropagator.Propagate(rV, vV, Mu, intercept.UT - Ut0, out var r, out var v);
        var fresh = RendezvousFlight.SolveIntercept(r, v, intercept.UT, rT, vT, Ut0, plan.ArrivalUT, Mu, 200, LowOrbit);
        Assert.That(fresh.Ok, Is.True);
        Assert.That(fresh.Revolutions, Is.EqualTo(plan.Revolutions), "the cheapest k is the planned one");
        Assert.That((fresh.DeltaV - intercept.DeltaV).magnitude, Is.LessThan(0.05), "same burn as planned");
        v += fresh.DeltaV + 0.3 * fresh.DeltaV.normalized;

        // At the correction: solved without being told the revolution count.
        KeplerPropagator.Propagate(r, v, Mu, correction.UT - intercept.UT, out r, out v);
        var c = RendezvousFlight.SolveIntercept(r, v, correction.UT, rT, vT, Ut0, plan.ArrivalUT, Mu, 200, LowOrbit);
        Assert.That(c.Ok, Is.True);
        Assert.That(c.MissWithout, Is.GreaterThan(1000), "the error is real");
        var verdict = RendezvousFlight.JudgeCorrection(c, plan.ArrivalUT - correction.UT, out string why);
        Assert.AreEqual(CorrectionVerdict.Fly, verdict, why);

        KeplerPropagator.Propagate(r, v + c.DeltaV, Mu, plan.ArrivalUT - correction.UT, out var rEnd, out var vEnd);
        var aim = RendezvousFlight.AimPoint(rT, vT, Ut0, plan.ArrivalUT, Mu, 200, out var rTEnd, out var vTEnd);
        TestContext.WriteLine($"correction {c.DeltaV.magnitude:F2} m/s (k {c.Revolutions}), miss without {c.MissWithout:F0} m, " +
            $"after {(rEnd - aim).magnitude:F3} m from the aim, brake {c.BrakeDeltaV:F2} m/s");
        Assert.That((rEnd - aim).magnitude, Is.LessThan(2));
        Assert.That((rEnd - rTEnd).magnitude, Is.EqualTo(200).Within(2));
        Assert.That(c.BrakeDeltaV, Is.EqualTo((vTEnd - vEnd).magnitude).Within(1e-3));
    }

    [Test]
    public void JudgeCorrection_SkipsSmallOrLate_RefusesNoSolution()
    {
        var big = new InterceptSolution { Ok = true, DeltaV = new Vector3d(1, 0, 0), MissWithout = 5000 };
        Assert.AreEqual(CorrectionVerdict.Fly, RendezvousFlight.JudgeCorrection(big, 1000, out _));
        Assert.AreEqual(CorrectionVerdict.Skip, RendezvousFlight.JudgeCorrection(big, RendezvousFlight.CorrectionMinLead - 1, out _), "too late");

        var tiny = new InterceptSolution { Ok = true, DeltaV = new Vector3d(0.01, 0, 0), MissWithout = 5000 };
        Assert.AreEqual(CorrectionVerdict.Skip, RendezvousFlight.JudgeCorrection(tiny, 1000, out _));
        var onTrack = new InterceptSolution { Ok = true, DeltaV = new Vector3d(1, 0, 0), MissWithout = 5 };
        Assert.AreEqual(CorrectionVerdict.Skip, RendezvousFlight.JudgeCorrection(onTrack, 1000, out _));

        Assert.AreEqual(CorrectionVerdict.Refuse, RendezvousFlight.JudgeCorrection(new InterceptSolution(), 1000, out string why));
        Assert.That(why, Is.Not.Empty);
    }

    [Test]
    public void AfterRendezvous_DocksWhenClose_ReplansTwiceThenFails()
    {
        Assert.AreEqual(ArrivalOutcome.Dock, RendezvousFlight.AfterRendezvous(210, 0.2, 0, true, out _));
        Assert.AreEqual(ArrivalOutcome.Done, RendezvousFlight.AfterRendezvous(210, 0.2, 0, false, out _));
        Assert.AreEqual(ArrivalOutcome.Replan, RendezvousFlight.AfterRendezvous(5000, 0.2, 0, true, out _));
        Assert.AreEqual(ArrivalOutcome.Replan, RendezvousFlight.AfterRendezvous(210, 3, 1, true, out _), "too fast");
        Assert.AreEqual(ArrivalOutcome.Fail, RendezvousFlight.AfterRendezvous(5000, 0.2, RendezvousFlight.MaxReplans, true, out string why));
        Assert.That(why, Does.Contain("5000 m"));
    }

    // ------------------------------------------------------------ docking

    [Test]
    public void DockedVessel_HoldsOurPortAndATargetPart()
    {
        var target = new HashSet<string> { "t1", "t2", "tport" };
        Assert.IsTrue(RendezvousFlight.IsDockedVessel(new[] { "a", "ourport", "t2" }, "ourport", target));
        Assert.IsFalse(RendezvousFlight.IsDockedVessel(new[] { "a", "ourport" }, "ourport", target), "our vessel alone");
        Assert.IsFalse(RendezvousFlight.IsDockedVessel(new[] { "t1", "t2" }, "ourport", target), "the target alone");
        Assert.IsFalse(RendezvousFlight.IsDockedVessel(null, "ourport", target));
        Assert.IsFalse(RendezvousFlight.IsDockedVessel(new[] { "ourport", "t1" }, null, target));
        Assert.IsFalse(RendezvousFlight.IsDockedVessel(new[] { "ourport", "t1" }, "ourport", new HashSet<string>()));
    }

    [Test]
    public void VesselChangeOnDocking_IsDocked_NotATakeOverOrAStop()
    {
        // The merged vessel has a new id and our port as its control part: VesselWatch would say
        // TakeOver (or Changed once its delay runs out). During the approach it is the docking.
        Assert.AreEqual(RvVesselOutcome.Docked, RendezvousFlight.OnVesselChange(RvPhase.Dock, true, VesselVerdict.TakeOver));
        Assert.AreEqual(RvVesselOutcome.Docked, RendezvousFlight.OnVesselChange(RvPhase.Dock, true, VesselVerdict.Changed));
        Assert.AreEqual(RvVesselOutcome.Docked, RendezvousFlight.OnVesselChange(RvPhase.Dock, true, VesselVerdict.Wait));
        // Otherwise the transfer's rule.
        Assert.AreEqual(RvVesselOutcome.TakeOver, RendezvousFlight.OnVesselChange(RvPhase.Brake, true, VesselVerdict.TakeOver));
        Assert.AreEqual(RvVesselOutcome.Changed, RendezvousFlight.OnVesselChange(RvPhase.Dock, false, VesselVerdict.Changed));
        Assert.AreEqual(RvVesselOutcome.Wait, RendezvousFlight.OnVesselChange(RvPhase.Dock, false, VesselVerdict.Wait));
        Assert.AreEqual(RvVesselOutcome.Same, RendezvousFlight.OnVesselChange(RvPhase.PhasingWait, false, VesselVerdict.Same));
    }

    [Test]
    public void JudgeDock_RetriesOnceThenFails()
    {
        string why;
        Assert.AreEqual(DockOutcome.Continue, RendezvousFlight.JudgeDock(DockPhase.Hold10, 0, 0, false, null, false, false, 0, null, out why));
        Assert.AreEqual(DockOutcome.Retry, RendezvousFlight.JudgeDock(DockPhase.Backoff, 1, 0, false, "cone", false, false, 0, null, out why));
        Assert.That(why, Does.Contain("cone"));
        Assert.AreEqual(DockOutcome.Continue, RendezvousFlight.JudgeDock(DockPhase.Backoff, 1, 1, false, "cone", false, false, 0, null, out why), "the retry is logged once");
        Assert.AreEqual(DockOutcome.Failed, RendezvousFlight.JudgeDock(DockPhase.Aborted, 2, 1, true, "too fast", false, false, 0, null, out why));
        Assert.That(why, Does.Contain("second abort"));
        Assert.AreEqual(DockOutcome.Failed, RendezvousFlight.JudgeDock(DockPhase.Aborted, 0, 0, true, "hold inside keep-out", false, false, 0, null, out why));
        Assert.That(why, Does.Contain("refused"));
    }

    [Test]
    public void JudgeDock_TheGameLatchWins_ContactWithoutLatchFails()
    {
        Assert.AreEqual(DockOutcome.Docked, RendezvousFlight.JudgeDock(DockPhase.Final, 0, 0, false, null, true, false, 0, null, out _));
        Assert.AreEqual(DockOutcome.Docked, RendezvousFlight.JudgeDock(DockPhase.Docked, 0, 0, false, null, false, true, 0, null, out _));
        // Latched in the same frame as a second abort: docked all the same.
        Assert.AreEqual(DockOutcome.Docked, RendezvousFlight.JudgeDock(DockPhase.Aborted, 2, 1, true, "x", true, false, 0, null, out _));
        Assert.AreEqual(DockOutcome.Continue, RendezvousFlight.JudgeDock(DockPhase.Docked, 0, 0, false, null, false, false, RendezvousFlight.LatchSeconds - 1, null, out _));
        Assert.AreEqual(DockOutcome.Failed, RendezvousFlight.JudgeDock(DockPhase.Docked, 0, 0, false, null, false, false, RendezvousFlight.LatchSeconds + 1, null, out _));
        Assert.AreEqual(DockOutcome.Failed, RendezvousFlight.JudgeDock(DockPhase.Final, 0, 0, false, null, false, false, 0, "no response", out string why));
        Assert.That(why, Does.Contain("no response"));
    }

    // ------------------------------------------------------------ start, resume, saving

    [Test]
    public void StartRefusals()
    {
        Assert.That(RendezvousFlight.StartRefusal(RvMode.RendezvousAndDock, false, false, null, 1e5), Does.Contain("target"));
        Assert.That(RendezvousFlight.StartRefusal(RvMode.RendezvousAndDock, true, true, null, 0), Does.Contain("own vessel"));
        Assert.AreEqual("The target orbits Mun, not Kerbin.",
            RendezvousFlight.StartRefusal(RvMode.RendezvousOnly, true, false, "The target orbits Mun, not Kerbin.", double.NaN));
        Assert.That(RendezvousFlight.StartRefusal(RvMode.DockOnly, true, false, null, 2500), Does.Contain("within 2 km"));
        Assert.That(RendezvousFlight.StartRefusal(RvMode.DockOnly, true, false, null, double.NaN), Is.Not.Null);
        Assert.IsNull(RendezvousFlight.StartRefusal(RvMode.DockOnly, true, false, null, 1500));
        Assert.IsNull(RendezvousFlight.StartRefusal(RvMode.RendezvousAndDock, true, false, null, 1e6));
    }

    [Test]
    public void ResumeMode_DocksAgainFromTheDockPhase()
    {
        Assert.AreEqual(RvMode.DockOnly, RendezvousFlight.ResumeMode(RvMode.RendezvousAndDock, RvPhase.Dock, 300));
        Assert.AreEqual(RvMode.RendezvousAndDock, RendezvousFlight.ResumeMode(RvMode.RendezvousAndDock, RvPhase.Dock, 5000), "drifted away: plan again");
        Assert.AreEqual(RvMode.RendezvousAndDock, RendezvousFlight.ResumeMode(RvMode.RendezvousAndDock, RvPhase.PhasingWait, 300));
        Assert.AreEqual(RvMode.RendezvousOnly, RendezvousFlight.ResumeMode(RvMode.RendezvousOnly, RvPhase.Dock, 300));
        Assert.IsFalse(RendezvousFlight.CanResume(RvPhase.Docked));
        Assert.IsFalse(RendezvousFlight.CanResume(RvPhase.Done));
        Assert.IsTrue(RendezvousFlight.CanResume(RvPhase.Failed));
    }

    [Test]
    public void SavedRendezvous_RoundTrips_InItsOwnFile()
    {
        var p = RendezvousFlight.ToSaved("v1", "Kerbin", "t1", "Station", RvMode.RendezvousAndDock, RvPhase.Dock, 12345.5, true, "op", null);
        Assert.IsTrue(PlanStore.TryFromJson(PlanStore.ToJson(p), out var back, out string error), error);
        Assert.AreEqual(SavedPlan.KindRendezvous, back.Kind);
        Assert.IsNull(back.Plan);
        Assert.AreEqual("v1", back.VesselId);
        Assert.AreEqual(12345.5, back.SavedUT);
        Assert.IsTrue(back.PrepareOnly);
        Assert.AreEqual("t1", back.Rendezvous.TargetId);
        Assert.AreEqual("Station", back.Rendezvous.TargetName);
        Assert.IsTrue(RendezvousFlight.TryParse(back.Rendezvous.Mode, out RvMode mode));
        Assert.AreEqual(RvMode.RendezvousAndDock, mode);
        Assert.IsTrue(RendezvousFlight.TryParse(back.Rendezvous.Phase, out RvPhase phase));
        Assert.AreEqual(RvPhase.Dock, phase);
        Assert.AreEqual("op", back.Rendezvous.OurPortId);
        Assert.IsNull(back.Rendezvous.TargetPortId);
        Assert.IsFalse(RendezvousFlight.TryParse("Nonsense", out RvPhase _));

        Assert.AreEqual("rendezvous_v1.json", PlanStore.FileName("v1", SavedPlan.KindRendezvous));
        Assert.AreEqual(PlanStore.FileName("v1"), PlanStore.FileName("v1", SavedPlan.KindTransfer));

        // A rendezvous without its section, or a transfer without a plan, is refused.
        var o = JObject.Parse(PlanStore.ToJson(p));
        o.Remove("rendezvous");
        Assert.IsFalse(PlanStore.TryFromJson(o.ToString(), out _, out error));
        Assert.That(error, Does.Contain("rendezvous"));
        o = JObject.Parse(PlanStore.ToJson(p));
        o["kind"] = SavedPlan.KindTransfer;
        o.Remove("rendezvous");
        Assert.IsFalse(PlanStore.TryFromJson(o.ToString(), out _, out error));
        Assert.That(error, Does.Contain("plan"));
    }

    [Test]
    public void TransferFile_HasNoRendezvousSection()
    {
        var p = new SavedPlan { VesselId = "v", FromBodyName = "Kerbin", ToBodyName = "Duna", Plan = new TransferPlan() };
        Assert.IsFalse(JObject.Parse(PlanStore.ToJson(p)).ContainsKey("rendezvous"));
    }

    [Test]
    public void Budget_KeepsTheDockingReserve()
    {
        var plan = Plan(RvStepKind.Phasing, RvStepKind.Correction, RvStepKind.Brake);   // 30 m/s
        var stages = new List<StageDv> { new StageDv { Stage = 0, DeltaVVac = 35 } };
        Assert.IsTrue(RendezvousFlight.Budget(plan, 0, stages, dock: false).Ok);
        var withDock = RendezvousFlight.Budget(plan, 0, stages, dock: true);
        Assert.IsFalse(withDock.Ok);
        Assert.That(withDock.Refusal, Does.Contain("40"));
        Assert.IsTrue(RendezvousFlight.Budget(plan, 1, stages, dock: true).Ok, "from the correction on");
    }

    [Test]
    public void Relative_DistanceSpeedInclination()
    {
        Circular(R, 0, 0, 0, out var r, out var v);
        Circular(R, 1, 3, 0, out var rT, out var vT);
        RendezvousFlight.Relative(r, v, rT, vT, out double d, out double s, out double i);
        Assert.That(d, Is.EqualTo((rT - r).magnitude).Within(1e-9));
        Assert.That(s, Is.EqualTo((vT - v).magnitude).Within(1e-9));
        Assert.That(i, Is.EqualTo(3).Within(1e-9));
    }
}

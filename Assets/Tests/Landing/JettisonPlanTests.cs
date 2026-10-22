using System.Collections.Generic;
using K2D2.Landing.Precision;
using NUnit.Framework;

public class JettisonPlanTests
{
    // Stage indices as the game's StagingComponent numbers them (index in AvailableStages): the
    // active stage is the last one, the next to fire the one before it. Here 3 stages, stage 2
    // active (the engine), so the next is 1.
    const int Next = 1;

    static JettisonPart P(string id, string parent, int stage = -1, int decoupleStage = -1, bool control = false) =>
        new JettisonPart
        {
            Part = new CapsulePart { Id = id, ParentId = parent, MassT = 1 },
            ActivationStage = stage, DecoupleStage = decoupleStage, IsControl = control,
        };
    static JettisonPart Chute(string id, string parent, int stage) =>
        new JettisonPart { Part = new CapsulePart { Id = id, ParentId = parent, IsParachute = true, MassT = 0.1 }, ActivationStage = stage, DecoupleStage = -1 };
    static JettisonPart Decoupler(string id, string parent, string detachesFrom, int stage, int decoupleStage = -1) =>
        new JettisonPart
        {
            Part = new CapsulePart { Id = id, ParentId = parent, IsDecoupler = true, DetachesFromId = detachesFrom, MassT = 0.05 },
            ActivationStage = stage, DecoupleStage = decoupleStage,
        };
    static JettisonPart Heatshield(string id, string parent) =>
        new JettisonPart { Part = new CapsulePart { Id = id, ParentId = parent, MassT = 0.3 }, ActivationStage = -1, DecoupleStage = -1, IsHeatshield = true };

    // Pod (root, control) with its chute on top and a heat shield; below: decoupler (stays with
    // the pod), tank, engine. Chute in stage 0, decoupler in stage 1, engine in stage 2 (active).
    static List<JettisonPart> Stack(int chuteStage = 0, int decouplerStage = Next) => new List<JettisonPart>
    {
        P("pod", null, control: true), Chute("chute", "pod", chuteStage), Heatshield("shield", "pod"),
        Decoupler("dec", "shield", "tank", decouplerStage), P("tank", "dec", decoupleStage: Next), P("engine", "tank", 2, Next),
    };

    static void AssertRefused(JettisonPlanResult r, string why)
    {
        TestContext.Out.WriteLine("refused: " + r.Reason);
        Assert.IsFalse(r.Ok);
        StringAssert.Contains(why, r.Reason);
    }

    [Test]
    public void NextStage_IsTheOneBeforeTheActiveStage()
    {
        Assert.AreEqual(1, JettisonPlan.NextStage(3, true));
        Assert.AreEqual(2, JettisonPlan.NextStage(3, false), "nothing fired yet: the last stage fires first");
        Assert.AreEqual(-1, JettisonPlan.NextStage(1, true), "the last stage is active: nothing left to fire");
        Assert.AreEqual(0, JettisonPlan.NextStage(1, false));
        Assert.AreEqual(-1, JettisonPlan.NextStage(0, false));
    }

    [Test]
    public void DecouplerAndChuteInDifferentStages_Ok()
    {
        JettisonPlanResult r = JettisonPlan.Plan(Stack(), Next);
        Assert.IsTrue(r.Ok, r.Reason);
        Assert.AreEqual(Next, r.Stage);
        CollectionAssert.AreEquivalent(new[] { "pod", "chute", "shield", "dec" }, r.CapsuleIds);
        CollectionAssert.AreEquivalent(new[] { "tank", "engine" }, r.ServiceIds);
        CollectionAssert.AreEquivalent(new[] { "dec" }, r.Decouplers);
        Assert.AreEqual(1, r.HeatshieldsKept);
        Assert.AreEqual(1, r.Heatshields);
        Assert.AreEqual(2, r.ServiceWithDecoupleStage, "both service parts carry DecoupleStage = the jettison stage");
    }

    [Test]
    public void DecouplerAndChuteInTheSameStage_Refused()
    {
        AssertRefused(JettisonPlan.Plan(Stack(chuteStage: Next), Next), "parachute");
    }

    [Test]
    public void ChuteBelowTheDecoupler_Refused()
    {
        // The only chute is on the tank: the piece holding it is the service module, not the pod.
        var parts = new List<JettisonPart>
        {
            P("pod", null, control: true), Decoupler("dec", "pod", "tank", Next), P("tank", "dec"), Chute("chute", "tank", 0), P("engine", "tank", 2),
        };
        AssertRefused(JettisonPlan.Plan(parts, Next), "controlling part");

        // A chute on each side: they would be separated from each other.
        parts.Add(Chute("chute2", "pod", 0));
        AssertRefused(JettisonPlan.Plan(parts, Next), "parachutes");
    }

    [Test]
    public void NoDecoupler_Refused()
    {
        var parts = new List<JettisonPart>
        {
            P("pod", null, control: true), Chute("chute", "pod", 0), P("tank", "pod"), P("engine", "tank", 2),
        };
        AssertRefused(JettisonPlan.Plan(parts, Next), "no decoupler");

        // A decoupler, but not in the next stage.
        AssertRefused(JettisonPlan.Plan(Stack(decouplerStage: 0), Next), "no decoupler between the capsule and the service module");
    }

    [Test]
    public void NoStageLeft_Refused()
    {
        AssertRefused(JettisonPlan.Plan(Stack(), -1), "no stage");
    }

    // Pod / decoupler A (capsule side) / tank / decoupler B / booster tank / engine.
    static List<JettisonPart> TwoDecouplers(int aStage, int bStage, int chuteStage = 0) => new List<JettisonPart>
    {
        P("pod", null, control: true), Chute("chute", "pod", chuteStage),
        Decoupler("decA", "pod", "tank", aStage), P("tank", "decA"),
        Decoupler("decB", "tank", "booster", bStage), P("booster", "decB"), P("engine", "booster", 2),
    };

    [Test]
    public void TwoDecouplersInTheNextStage_OkOnlyWithTheCapsuleSideOneAndNoChute()
    {
        JettisonPlanResult both = JettisonPlan.Plan(TwoDecouplers(Next, Next), Next);
        Assert.IsTrue(both.Ok, both.Reason);
        CollectionAssert.AreEquivalent(new[] { "decA" }, both.Decouplers);
        CollectionAssert.AreEquivalent(new[] { "tank", "decB", "booster", "engine" }, both.ServiceIds);

        // Only the lower one fires: the capsule stays on the tank.
        AssertRefused(JettisonPlan.Plan(TwoDecouplers(0, Next), Next), "no decoupler between the capsule and the service module");

        // Both, but the chute fires with them.
        AssertRefused(JettisonPlan.Plan(TwoDecouplers(Next, Next, chuteStage: Next), Next), "parachute");
    }

    [Test]
    public void CapsuleHeldOnTwoSides_RefusedUnlessBothCutsFire()
    {
        // A tower above the pod on its own decoupler, the service module below on another.
        var parts = new List<JettisonPart>
        {
            P("pod", null, control: true), Chute("chute", "pod", 0),
            Decoupler("towerDec", "pod", "tower", 0), P("tower", "towerDec"),
            Decoupler("dec", "pod", "tank", Next), P("tank", "dec"), P("engine", "tank", 2),
        };
        AssertRefused(JettisonPlan.Plan(parts, Next), "stay attached");

        parts[2] = Decoupler("towerDec", "pod", "tower", Next);
        JettisonPlanResult r = JettisonPlan.Plan(parts, Next);
        Assert.IsTrue(r.Ok, r.Reason);
        CollectionAssert.AreEquivalent(new[] { "tower", "tank", "engine" }, r.ServiceIds);
    }

    [Test]
    public void EngineRootStack_TheSamePlan()
    {
        // Built from the engine up; the decoupler separates from its parent (the tank) and stays with the pod.
        var parts = new List<JettisonPart>
        {
            P("engine", null, 2), P("tank", "engine"), Decoupler("dec", "tank", "tank", Next), P("pod", "dec", control: true), Chute("chute", "pod", 0),
        };
        JettisonPlanResult r = JettisonPlan.Plan(parts, Next);
        Assert.IsTrue(r.Ok, r.Reason);
        CollectionAssert.AreEquivalent(new[] { "engine", "tank" }, r.ServiceIds);
        Assert.AreEqual(0, r.ServiceWithDecoupleStage);
    }

    [Test]
    public void DroppedHeatshieldWithNoneKept_Refused()
    {
        // The shield sits below the decoupler, on the tank: it would fall with the service module.
        var parts = new List<JettisonPart>
        {
            P("pod", null, control: true), Chute("chute", "pod", 0), Decoupler("dec", "pod", "shield", Next),
            Heatshield("shield", "dec"), P("tank", "shield"), P("engine", "tank", 2),
        };
        JettisonPlanResult r = JettisonPlan.Plan(parts, Next);
        Assert.IsFalse(r.Ok);
        Assert.AreEqual("Cannot jettison the service module safely: the heat shield would be dropped with it.", r.Message);

        // Another shield kept on the capsule: Ok, one of two kept.
        parts.Add(Heatshield("shield2", "pod"));
        r = JettisonPlan.Plan(parts, Next);
        Assert.IsTrue(r.Ok, r.Reason);
        Assert.AreEqual(2, r.Heatshields);
        Assert.AreEqual(1, r.HeatshieldsKept);
    }

    [Test]
    public void StageAlsoFiresAPartOnTheCapsule_Refused()
    {
        List<JettisonPart> parts = Stack();
        parts.Add(P("podEngine", "pod", Next));
        AssertRefused(JettisonPlan.Plan(parts, Next), "on the capsule");

        // Another decoupler inside the capsule, not on its boundary (its node holds nothing).
        parts = Stack();
        parts.Add(Decoupler("spare", "pod", "", Next));
        AssertRefused(JettisonPlan.Plan(parts, Next), "on the capsule");
    }

    [Test]
    public void BoundaryDecouplerWithAnUnknownNode_Refused()
    {
        var parts = new List<JettisonPart>
        {
            P("pod", null, control: true), Chute("chute", "pod", 0), Decoupler("dec", "pod", null, Next), P("tank", "dec"), P("engine", "tank", 2),
        };
        AssertRefused(JettisonPlan.Plan(parts, Next), "unknown explosive node");
    }

    [Test]
    public void RadialDecouplerOnTheServiceSide_Ok()
    {
        // Radial booster on the tank, dropped by the same stage: it leaves with the service module.
        List<JettisonPart> parts = Stack();
        parts.Add(Decoupler("radial", "tank", "booster", Next));
        parts.Add(P("booster", "radial"));
        JettisonPlanResult r = JettisonPlan.Plan(parts, Next);
        Assert.IsTrue(r.Ok, r.Reason);
        CollectionAssert.AreEquivalent(new[] { "dec" }, r.Decouplers);
        CollectionAssert.AreEquivalent(new[] { "radial" }, r.OtherActivated);
        CollectionAssert.AreEquivalent(new[] { "tank", "engine", "radial", "booster" }, r.ServiceIds);
    }

    [Test]
    public void Fairing_OkOnTheServiceSideRefusedOnTheCapsule()
    {
        List<JettisonPart> parts = Stack();
        parts.Add(P("fairing", "tank", Next));
        JettisonPlanResult r = JettisonPlan.Plan(parts, Next);
        Assert.IsTrue(r.Ok, r.Reason);
        CollectionAssert.AreEquivalent(new[] { "fairing" }, r.OtherActivated);

        parts = Stack();
        parts.Add(P("fairing", "pod", Next));
        AssertRefused(JettisonPlan.Plan(parts, Next), "on the capsule");
    }

    [Test]
    public void StagedDockingPort_Refused()
    {
        List<JettisonPart> parts = Stack();
        JettisonPart port = P("port", "tank", Next);
        port.IsDockingPort = true;
        parts.Add(port);
        AssertRefused(JettisonPlan.Plan(parts, Next), "docking port");
    }

    [Test]
    public void NoControlOwner_TheRootMustBeOnTheCapsule()
    {
        // Pod root: the root piece stays the active vessel.
        List<JettisonPart> parts = Stack();
        parts[0] = P("pod", null);
        JettisonPlanResult r = JettisonPlan.Plan(parts, Next);
        Assert.IsTrue(r.Ok, r.Reason);

        // Engine root: the service module would stay active.
        parts = new List<JettisonPart>
        {
            P("engine", null, 2), P("tank", "engine"), Decoupler("dec", "tank", "tank", Next), P("pod", "dec"), Chute("chute", "pod", 0),
        };
        AssertRefused(JettisonPlan.Plan(parts, Next), "root part");
    }
}

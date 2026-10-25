using System;
using System.Collections.Generic;
using K2D2.Landing.Braking;
using KSP.Sim;
using NUnit.Framework;

// The plan of a landing with a chosen stage: when the lower stage leaves, and whether the
// landing stage can finish the braking.
public class StagedBrakePlanTests
{

    static StagePlanInput PlanInput(BrakeStage[] stages, double player = 0)
    {
        StagedBrakeTests.Approach(out Vector3d r, out Vector3d v);
        return new StagePlanInput
        {
            Brake = StagedBrakeTests.Input(r, v, stages),
            PlayerDropHeight = player,
        };
    }

    [Test]
    public void SmallBooster_DroppedDryAboveTheDropHeight()
    {
        // Vertical fall: the booster runs dry high above the drop height.
        BrakeInput brake = StagedBrakeTests.Input(new Vector3d(200000 + 25000, 0, 0), new Vector3d(-300, 0, 0), StagedBrakeTests.TwoStages(150));
        StagePlan plan = StagedBrakePlan.Plan(new StagePlanInput { Brake = brake });

        Assert.IsTrue(plan.Ok, plan.Error);
        Assert.AreEqual(BrakeStatus.Ok, plan.Brake.Status);
        Assert.AreEqual(1, plan.Brake.Drops.Count);
        Assert.IsTrue(plan.Brake.Drops[0].Dry);
        Assert.IsTrue(plan.DroppedDry);
        Assert.That(plan.DropHeight, Is.GreaterThanOrEqualTo(StagedBrakePlan.MinDropHeight));
        Assert.That(plan.Brake.Drops[0].Altitude, Is.GreaterThanOrEqualTo(plan.DropHeight - 1e-6));
        Assert.AreEqual(1, plan.Brake.FinalStageIndex);
        Assert.That(StagedBrakePlan.DeltaVMargin * plan.LandingDeltaVNeeded, Is.LessThanOrEqualTo(plan.LandingDeltaVAvailable));
    }

    [Test]
    public void BigBooster_ForcedDropAtTheComputedHeight_LanderStillStops()
    {
        StagePlanInput input = PlanInput(StagedBrakeTests.TwoStages(2000));
        StagePlan plan = StagedBrakePlan.Plan(input);

        Assert.IsTrue(plan.Ok, plan.Error);
        Assert.AreEqual(1, plan.Brake.Drops.Count);
        BrakeDrop drop = plan.Brake.Drops[0];
        Assert.IsFalse(drop.Dry);
        Assert.That(plan.DropHeight, Is.GreaterThanOrEqualTo(StagedBrakePlan.MinDropHeight));
        Assert.That(drop.Altitude, Is.LessThanOrEqualTo(plan.DropHeight));
        Assert.That(drop.Altitude, Is.GreaterThan(plan.DropHeight - drop.SurfaceSpeed * BrakeSimulator.Step - 1));

        // Flown from the planned start with the planned drop height, the lander stops safely.
        BrakeInput b = input.Brake;
        b.DropHeight = plan.DropHeight;
        BrakeRun run = BrakeSimulator.Simulate(b, plan.Brake.StartUT);
        Assert.IsTrue(run.Stopped);
        Assert.AreEqual(1, run.FinalStageIndex);
        Assert.That(run.StopAltitude, Is.GreaterThanOrEqualTo(BrakeSimulator.SafeAltitude));
        Assert.That(StagedBrakePlan.DeltaVMargin * run.FinalStageDeltaV, Is.LessThanOrEqualTo(900));
    }

    [Test]
    public void WeakBooster_DropsHigherThanTheFloor()
    {
        // A heavy booster barely holds the vessel up (0.85 x 2 m/s² against 1.63 m/s²): the
        // lander must take over high, while it can still stop.
        BrakeStage[] stages = StagedBrakeTests.TwoStages(2000);
        stages[0].MassT = 30;
        BrakeInput brake = StagedBrakeTests.Input(new Vector3d(200000 + 20000, 0, 0), new Vector3d(-250, 0, 0), stages);
        StagePlan plan = StagedBrakePlan.Plan(new StagePlanInput { Brake = brake });

        Assert.IsTrue(plan.Ok, plan.Error);
        Assert.That(plan.SafeDropHeight, Is.GreaterThan(StagedBrakePlan.MinDropHeight));
        Assert.That(plan.DropHeight, Is.GreaterThanOrEqualTo(plan.SafeDropHeight));
        Assert.IsTrue(plan.LandingStageReached);
        Assert.IsFalse(plan.DroppedDry);
        Assert.That(plan.DropAltitude, Is.LessThanOrEqualTo(plan.DropHeight));
        Assert.That(plan.Brake.StopAltitude, Is.GreaterThanOrEqualTo(BrakeSimulator.SafeAltitude));
    }

    [Test]
    public void BigBooster_DropsAtTheFloor()
    {
        StagePlan plan = StagedBrakePlan.Plan(PlanInput(StagedBrakeTests.TwoStages(2000)));
        Assert.IsTrue(plan.Ok, plan.Error);
        Assert.AreEqual(StagedBrakePlan.MinDropHeight, plan.DropHeight, 1e-6);
    }

    [Test]
    public void LanderWithoutEnoughDeltaV_RefusedWithNumbers()
    {
        StagePlan plan = StagedBrakePlan.Plan(PlanInput(StagedBrakeTests.TwoStages(150, landerDv: 100)));

        Assert.IsFalse(plan.Ok);
        StringAssert.StartsWith("Stage 2 alone cannot brake from ", plan.Error);
        StringAssert.Contains("m/s: it has 100 m/s", plan.Error);
    }

    [Test]
    public void PlayerHeight_RaisesTheDropButNeverLowersIt()
    {
        StagePlan high = StagedBrakePlan.Plan(PlanInput(StagedBrakeTests.TwoStages(2000), player: 6000));
        Assert.IsTrue(high.Ok, high.Error);
        Assert.That(high.DropHeight, Is.GreaterThanOrEqualTo(6000));
        Assert.That(high.Brake.Drops[0].Altitude, Is.LessThanOrEqualTo(high.DropHeight));
        // Dropped at 6 km, or at the start of the braking when it starts lower.
        Assert.That(high.Brake.Drops[0].Altitude > 5000 || high.Brake.Drops[0].UT <= high.Brake.StartUT + 1e-9);

        StagePlan low = StagedBrakePlan.Plan(PlanInput(StagedBrakeTests.TwoStages(2000), player: 500));
        StagePlan auto = StagedBrakePlan.Plan(PlanInput(StagedBrakeTests.TwoStages(2000)));
        Assert.AreEqual(auto.DropHeight, low.DropHeight, 1e-6);
    }

    [Test]
    public void ThreeStages_DroppedOneByOne()
    {
        var stages = new[]
        {
            new BrakeStage { Stage = 4, ThrustKN = 90, MassT = 16, Isp = 300, DeltaV = 120 },
            new BrakeStage { Stage = 3, ThrustKN = 60, MassT = 10, Isp = 300, DeltaV = 150 },
            new BrakeStage { Stage = 2, ThrustKN = 20, MassT = 4, Isp = 320, DeltaV = 900 },
        };
        StagePlan plan = StagedBrakePlan.Plan(PlanInput(stages));

        Assert.IsTrue(plan.Ok, plan.Error);
        Assert.AreEqual(2, plan.Brake.Drops.Count);
        Assert.AreEqual(4, plan.Brake.Drops[0].Stage);
        Assert.AreEqual(3, plan.Brake.Drops[1].Stage);
        Assert.That(plan.Brake.Drops[1].UT - plan.Brake.Drops[0].UT, Is.GreaterThanOrEqualTo(BrakeSimulator.StageGap));
        Assert.AreEqual(2, plan.Brake.FinalStageIndex);
    }

    [Test]
    public void DropDecision()
    {
        Assert.AreEqual(DropReason.None, StagedBrakePlan.DropNow(false, true, 100, 2000));
        Assert.AreEqual(DropReason.Dry, StagedBrakePlan.DropNow(true, true, 9000, 2000));
        Assert.AreEqual(DropReason.Altitude, StagedBrakePlan.DropNow(true, false, 1999, 2000));
        Assert.AreEqual(DropReason.None, StagedBrakePlan.DropNow(true, false, 2001, 2000));
        Assert.AreEqual(DropReason.None, StagedBrakePlan.DropNow(true, false, 100, double.NaN));
    }

    // ---- The stage list of the Land tab ----

    static List<LandingStageData> GameStages() => new List<LandingStageData>
    {
        new LandingStageData { Stage = 3, DeltaV = 755, ThrustKN = 120, Isp = 310, StartMassT = 12 },
        new LandingStageData { Stage = 2, DeltaV = 980, ThrustKN = 30, Isp = 320, StartMassT = 4 },
        new LandingStageData { Stage = 1, DeltaV = 0, ThrustKN = 0, Isp = 0, StartMassT = 1.5 },
    };

    [Test]
    public void Build_FromTheActiveStageToTheChosenOne()
    {
        BrakeStage[] stages = LandingStages.Build(GameStages(), 1, currentMassT: 11, activeThrustKN: 115, activeIsp: 305, out string error);

        Assert.IsNull(error);
        Assert.AreEqual(2, stages.Length);
        // The active stage flies with the live mass, thrust and Isp.
        Assert.AreEqual(11, stages[0].MassT);
        Assert.AreEqual(115, stages[0].ThrustKN);
        Assert.AreEqual(305, stages[0].Isp);
        Assert.AreEqual(755, stages[0].DeltaV);
        Assert.AreEqual(4, stages[1].MassT);
        Assert.AreEqual(30, stages[1].ThrustKN);
        Assert.AreEqual(980, stages[1].DeltaV);
        Assert.AreEqual(2, stages[1].Stage);
    }

    [Test]
    public void Build_CurrentStageMeansNoStagedBraking()
    {
        Assert.IsNull(LandingStages.Build(GameStages(), 0, 11, 115, 305, out string error));
        Assert.IsNull(error);
    }

    [Test]
    public void Build_LandingStageWithoutEngine_Refused()
    {
        Assert.IsNull(LandingStages.Build(GameStages(), 2, 11, 115, 305, out string error));
        Assert.AreEqual("Stage 3 has no engine: choose a stage that can brake.", error);
        Assert.IsNull(LandingStages.Build(GameStages(), 5, 11, 115, 305, out error));
        Assert.AreEqual("The chosen stage is not on the vessel any more: choose it again.", error);
    }

    [Test]
    public void ParseDropHeight()
    {
        Assert.AreEqual(0, LandingStages.ParseDropHeight(""));
        Assert.AreEqual(0, LandingStages.ParseDropHeight("  "));
        Assert.AreEqual(0, LandingStages.ParseDropHeight(null));
        Assert.AreEqual(5000, LandingStages.ParseDropHeight("5"), 1e-9);
        Assert.AreEqual(2500, LandingStages.ParseDropHeight("2,5"), 1e-9);
        Assert.AreEqual(2500, LandingStages.ParseDropHeight(" 2.5 "), 1e-9);
        Assert.IsNaN(LandingStages.ParseDropHeight("abc"));
        Assert.IsNaN(LandingStages.ParseDropHeight("-1"));
    }

    [Test]
    public void Labels_InStackOrder()
    {
        List<string> labels = LandingStages.Labels(GameStages());
        CollectionAssert.AreEqual(new[] { "Stage 3: 0 m/s", "Stage 2: 980 m/s", "Stage 1 (current): 755 m/s" }, labels);
        Assert.AreEqual(2, LandingStages.IndexOfLabel(GameStages(), "Stage 3: 0 m/s"));
        Assert.AreEqual(0, LandingStages.IndexOfLabel(GameStages(), "Stage 1 (current): 755 m/s"));
        Assert.AreEqual(-1, LandingStages.IndexOfLabel(GameStages(), "nope"));
    }
}

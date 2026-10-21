using K2D2.Landing.Precision;
using KSP.Sim;
using NUnit.Framework;

public class PrecisionBudgetTests
{
    // A plan of deorbitDv (along -x) and brakeDv.
    static DeorbitPlan Plan(double deorbitDv, double brakeDv) => new DeorbitPlan
    {
        Ok = true,
        DeltaV = new Vector3d(-deorbitDv, 0, 0),
        Stop = new StopPrediction { Ok = true, BrakeDeltaV = brakeDv },
    };

    [Test]
    public void Ratio119_IsWithinTheLimit()
    {
        // Baseline 20 + 580 = 600; precision 21 + 600 + 50 + 43 = 714 = 1.19 × 600.
        PrecisionBudget.Budget b = PrecisionBudget.Evaluate(Plan(20, 580), Plan(21, 600), 50, 43, 0, 0);

        Assert.AreEqual(600, b.BaselineDeltaV, 1e-9);
        Assert.AreEqual(714, b.PrecisionDeltaV, 1e-9);
        Assert.IsTrue(b.WithinLimit);
    }

    [Test]
    public void Ratio121_IsOverTheLimit()
    {
        // 726 = 1.21 × 600.
        PrecisionBudget.Budget b = PrecisionBudget.Evaluate(Plan(20, 580), Plan(21, 600), 50, 55, 0, 0);

        Assert.AreEqual(726, b.PrecisionDeltaV, 1e-9);
        Assert.IsFalse(b.WithinLimit);
    }

    [Test]
    public void OverBudget_IsRefusedWithTheNumbers()
    {
        // 726 = 1.21 × 600.
        PrecisionBudget.Budget b = PrecisionBudget.Evaluate(Plan(20, 580), Plan(21, 600), 50, 55, 0, 0);

        Assert.IsTrue(b.OverBudget);
        Assert.AreEqual("Precision would cost +21 % Δv (limit 20 %): land without a target, or pick a closer target.", b.RefusalMessage);
    }

    [Test]
    public void WithinBudget_HasNoRefusal()
    {
        PrecisionBudget.Budget b = PrecisionBudget.Evaluate(Plan(20, 580), Plan(21, 600), 50, 43, 0, 0);

        Assert.IsFalse(b.OverBudget);
        Assert.IsNull(b.RefusalMessage);
    }

    [Test]
    public void RefusedPlans_AreNotOverBudget()
    {
        DeorbitPlan refused = Plan(21, 500);
        refused.Ok = false;

        Assert.IsFalse(PrecisionBudget.Evaluate(Plan(20, 580), refused, 0, 0, 0, 0).OverBudget);
        Assert.IsFalse(PrecisionBudget.Evaluate(default, Plan(21, 600), 0, 0, 0, 0).OverBudget);
    }

    [Test]
    public void Summary_ShowsLandingBaselinePlaneChangeAndPreparation()
    {
        PrecisionBudget.Budget b = PrecisionBudget.Evaluate(Plan(20, 580), Plan(21, 600), 50, 43, 12.4, 74.6);

        Assert.AreEqual(12.4, b.PreparationDeltaV, 1e-9);
        Assert.AreEqual(74.6, b.PlaneChangeDeltaV, 1e-9);
        Assert.AreEqual("Δv: landing 714 m/s (no target 600, +19 %), plane change 75 m/s, orbit preparation 12 m/s", b.Summary);
    }

    [Test]
    public void NoBaseline_IsNotWithinTheLimit()
    {
        PrecisionBudget.Budget b = PrecisionBudget.Evaluate(default, Plan(21, 600), 0, 0, 0, 0);

        Assert.IsFalse(b.WithinLimit);
    }

    [Test]
    public void RefusedPlans_AreNotWithinTheLimitAndSayWhy()
    {
        DeorbitPlan refused = Plan(21, 500);
        refused.Ok = false;
        refused.Reason = "Cannot stop before the ground!";

        PrecisionBudget.Budget noPrecision = PrecisionBudget.Evaluate(Plan(20, 580), refused, 0, 10, 0, 0);
        DeorbitPlan refusedBaseline = refused;
        PrecisionBudget.Budget noBaseline = PrecisionBudget.Evaluate(refusedBaseline, Plan(21, 600), 0, 10, 0, 0);

        TestContext.Out.WriteLine(noPrecision.Summary + " | " + noBaseline.Summary);
        Assert.IsFalse(noPrecision.WithinLimit);
        Assert.That(noPrecision.Reason, Does.Contain("Cannot stop before the ground!"));
        Assert.That(noPrecision.Summary, Does.Contain("budget not checked"));
        Assert.IsFalse(noBaseline.WithinLimit);
        Assert.That(noBaseline.Reason, Does.Contain("without target"));
    }

    [Test]
    public void OverTheLimit_SaysByHowMuch()
    {
        PrecisionBudget.Budget b = PrecisionBudget.Evaluate(Plan(20, 580), Plan(21, 600), 50, 55, 0, 0);

        Assert.That(b.Reason, Is.EqualTo("precision costs 21 % more than the landing without target (limit 20 %)"));
        Assert.IsNull(PrecisionBudget.Evaluate(Plan(20, 580), Plan(21, 600), 50, 43, 0, 0).Reason);
    }

    [Test]
    public void BaselineTerminalReserve_CountsInTheBaseline()
    {
        // 600 + 30 = 630; 714 / 630 = 1.13.
        PrecisionBudget.Budget b = PrecisionBudget.Evaluate(Plan(20, 580), Plan(21, 600), 50, 43, 0, 0, 30);

        Assert.AreEqual(630, b.BaselineDeltaV, 1e-9);
        Assert.IsTrue(b.WithinLimit);
    }

    // The reserve against the simulated vertical descent of TerminalGuidance from rest.
    [TestCase(1500, 6.0)]
    [TestCase(1500, 4.0)]
    [TestCase(50, 6.0)]
    public void TerminalReserve_MatchesASimulatedVerticalDescent(double height, double maxAccel)
    {
        const double g = 1.63, touchDown = 2;
        double reserve = PrecisionBudget.TerminalReserve(height, g, touchDown, maxAccel);
        PointMassSim.Result r = PointMassSim.Run(new Vector3d(0, 0, height), new Vector3d(0, 0, 0), g, maxAccel, touchDown);

        TestContext.Out.WriteLine($"{height} m, {maxAccel} m/s²: reserve {reserve:F1} m/s, simulated {r.DeltaV:F1} m/s ({r})");
        Assert.IsTrue(r.Landed, r.ToString());
        Assert.That(reserve, Is.GreaterThan(0));
        Assert.That(reserve, Is.EqualTo(r.DeltaV).Within(10).Percent);
    }

    [Test]
    public void TerminalReserve_WithoutEnoughThrust_IsNaN()
    {
        Assert.IsNaN(PrecisionBudget.TerminalReserve(1500, 1.63, 2, 1.5));
    }
}

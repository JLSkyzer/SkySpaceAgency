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
}

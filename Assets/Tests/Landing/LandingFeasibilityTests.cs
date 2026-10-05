using K2D2.Landing.Braking;
using NUnit.Framework;

public class LandingFeasibilityTests
{
    const double G = 1.62845; // Mun surface gravity

    static BrakeResult Brake(BrakeStatus status, double dv = 500)
    {
        return new BrakeResult { Status = status, DeltaVNeeded = dv };
    }

    [Test]
    public void NoEngine_Refused()
    {
        var result = LandingFeasibility.Check(0, G, null, 1000);
        Assert.IsFalse(result.Ok);
        Assert.AreEqual("No active engine: stage or activate your engines first.", result.Error);
        Assert.IsFalse(LandingFeasibility.Check(double.NaN, G, null, 1000).Ok);
    }

    [Test]
    public void LowTwr_Refused()
    {
        // 0.85 * 1.2 g = 1.02 g <= 1.05 g
        var result = LandingFeasibility.Check(1.2 * G, G, null, 1000);
        Assert.IsFalse(result.Ok);
        Assert.AreEqual("Local TWR 1.20: too low to stop safely.", result.Error);
    }

    [Test]
    public void EnoughTwr_NoCollisionYet_Accepted()
    {
        // 0.85 * 1.3 g = 1.105 g > 1.05 g; no simulation (precision landing starting in orbit).
        var result = LandingFeasibility.Check(1.3 * G, G, null, 0);
        Assert.IsTrue(result.Ok);
        Assert.IsTrue(string.IsNullOrEmpty(result.Error));
        Assert.IsTrue(string.IsNullOrEmpty(result.Warning));
    }

    [Test]
    public void SimulationImpossible_Refused()
    {
        var result = LandingFeasibility.Check(3 * G, G, Brake(BrakeStatus.Impossible), 1000);
        Assert.IsFalse(result.Ok);
        Assert.AreEqual("Cannot stop before the ground with the current thrust.", result.Error);
    }

    [Test]
    public void NotEnoughDeltaV_Refused()
    {
        // 1.10 * 500 = 550 > 540
        var result = LandingFeasibility.Check(3 * G, G, Brake(BrakeStatus.Ok, 500), 540);
        Assert.IsFalse(result.Ok);
        Assert.AreEqual("Δv 540 m/s for ~500 m/s needed.", result.Error);
    }

    [Test]
    public void EnoughDeltaV_Accepted()
    {
        var result = LandingFeasibility.Check(3 * G, G, Brake(BrakeStatus.Ok, 500), 560);
        Assert.IsTrue(result.Ok);
        Assert.IsTrue(string.IsNullOrEmpty(result.Warning));
    }

    [Test]
    public void TooLate_IsNotARefusal()
    {
        Assert.IsTrue(LandingFeasibility.Check(3 * G, G, Brake(BrakeStatus.TooLate, 100), 1000).Ok);
    }

    [TestCase(0.0)]
    [TestCase(double.NaN)]
    public void UnknownDeltaV_WarnsWithoutRefusing(double remaining)
    {
        var result = LandingFeasibility.Check(3 * G, G, Brake(BrakeStatus.Ok, 500), remaining);
        Assert.IsTrue(result.Ok);
        Assert.AreEqual("Δv unknown: check your fuel.", result.Warning);
    }
}

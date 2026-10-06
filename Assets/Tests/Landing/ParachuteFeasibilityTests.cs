using K2D2.Landing.Braking;
using NUnit.Framework;

public class ParachuteFeasibilityTests
{
    const double Depth = 70000; // Kerbin's atmosphere

    static void AssertRefused(FeasibilityResult result, string message)
    {
        Assert.IsFalse(result.Ok);
        Assert.AreEqual(message, result.Error);
    }

    [Test]
    public void NoAtmosphere_Refused()
    {
        AssertRefused(ParachuteFeasibility.Check(false, 0, false, 5000, 2),
            "No atmosphere here: parachute landing needs one.");
    }

    [Test]
    public void NoParachute_Refused()
    {
        AssertRefused(ParachuteFeasibility.Check(true, Depth, false, 30000, 0),
            "No parachute on this vessel.");
    }

    [Test]
    public void NoParachute_RefusedEvenInsideTheAtmosphere()
    {
        AssertRefused(ParachuteFeasibility.Check(true, Depth, true, 30000, 0),
            "No parachute on this vessel.");
    }

    [TestCase(80000.0)] // stable orbit above the atmosphere
    [TestCase(70000.0)] // periapsis right at the edge
    [TestCase(2e6)]     // escape trajectory with a high periapsis
    public void PeriapsisAboveTheAtmosphere_Refused(double periapsis)
    {
        AssertRefused(ParachuteFeasibility.Check(true, Depth, false, periapsis, 2),
            "Trajectory stays above the atmosphere: deorbit first.");
    }

    [Test]
    public void UnknownPeriapsis_Refused()
    {
        AssertRefused(ParachuteFeasibility.Check(true, Depth, false, double.NaN, 2),
            "Trajectory stays above the atmosphere: deorbit first.");
    }

    [Test]
    public void PeriapsisInsideTheAtmosphere_Accepted()
    {
        var result = ParachuteFeasibility.Check(true, Depth, false, 30000, 1);
        Assert.IsTrue(result.Ok);
        Assert.IsTrue(string.IsNullOrEmpty(result.Error));
        // Suborbital: the periapsis is below the surface.
        Assert.IsTrue(ParachuteFeasibility.Check(true, Depth, false, -400000, 1).Ok);
    }

    [Test]
    public void AlreadyInTheAtmosphere_AcceptsAHighPeriapsis()
    {
        Assert.IsTrue(ParachuteFeasibility.Check(true, Depth, true, 80000, 1).Ok);
    }

    [Test]
    public void TooFastUnderCanopy_Above10MetresPerSecondBelow500Metres()
    {
        Assert.IsTrue(ParachuteFeasibility.TooFastUnderCanopy(1, 400, 12));
        Assert.IsTrue(ParachuteFeasibility.TooFastUnderCanopy(2, 499, 10.1));
        Assert.IsFalse(ParachuteFeasibility.TooFastUnderCanopy(1, 400, 10));   // not above 10 m/s
        Assert.IsFalse(ParachuteFeasibility.TooFastUnderCanopy(1, 500, 20));   // not below 500 m
        Assert.IsFalse(ParachuteFeasibility.TooFastUnderCanopy(1, 2000, 40));
    }

    [Test]
    public void TooFastUnderCanopy_NeedsAnOpenParachute()
    {
        Assert.IsFalse(ParachuteFeasibility.TooFastUnderCanopy(0, 400, 40));
    }

    [Test]
    public void NoParachuteLeft_OnlyWhileFlying()
    {
        Assert.IsTrue(ParachuteFeasibility.NoParachuteLeft(0, false));
        Assert.IsFalse(ParachuteFeasibility.NoParachuteLeft(0, true));
        Assert.IsFalse(ParachuteFeasibility.NoParachuteLeft(1, false));
    }
}

using SkySpaceAgency.Landing.Braking;
using NUnit.Framework;

public class StandingCheckTests
{
    const double KerbinG = 9.81, MunG = 1.63, MinmusG = 0.491;

    // Runs the check every 0.1 s for the given time with constant readings; returns the first
    // time it fired, or NaN.
    static double FirstFire(StandingCheck check, double seconds, bool powered, double speed, double thrust, double g)
    {
        for (int i = 0; i <= (int)(seconds * 10); i++)
        {
            double t = i * 0.1;
            if (check.Update(t, powered, speed, thrust, g))
                return t;
        }
        return double.NaN;
    }

    [Test]
    public void HoldSeconds_KerbinIsTheMinimum()
    {
        Assert.AreEqual(StandingCheck.MinHoldS, StandingCheck.HoldSeconds(KerbinG), 1e-9);
    }

    [Test]
    public void HoldSeconds_MunIsTwiceTheSpeedLimitOverHalfGravity()
    {
        Assert.AreEqual(2 * 1.5 / (0.5 * MunG), StandingCheck.HoldSeconds(MunG), 1e-9);
        Assert.IsTrue(StandingCheck.Applies(MunG));
    }

    [Test]
    public void Minmus_TooLowGravity_NeverFires()
    {
        Assert.IsFalse(StandingCheck.Applies(MinmusG));
        Assert.IsNaN(FirstFire(new StandingCheck(), 30, true, 0, 0, MinmusG));
    }

    [Test]
    public void Kerbin_StillWithEnginesIdle_FiresAfterTheHold()
    {
        double t = FirstFire(new StandingCheck(), 10, true, 0.2, 0, KerbinG);
        Assert.AreEqual(1.5, t, 0.05);
    }

    [Test]
    public void Mun_StillWithLowThrust_FiresAfterTheHold()
    {
        double t = FirstFire(new StandingCheck(), 10, true, 0.5, 0.4 * MunG, MunG);
        Assert.AreEqual(StandingCheck.HoldSeconds(MunG), t, 0.1);
    }

    [TestCase(KerbinG)]
    [TestCase(MunG)]
    [TestCase(MinmusG)]
    public void HoverAtOneG_NeverFires(double g)
    {
        // The final descent hovering slowly just above the ground: thrust balances gravity.
        Assert.IsNaN(FirstFire(new StandingCheck(), 30, true, 0.3, 1.0 * g, g));
    }

    [Test]
    public void NotPowered_NeverFires()
    {
        Assert.IsNaN(FirstFire(new StandingCheck(), 30, false, 0, 0, KerbinG));
    }

    [Test]
    public void Moving_NeverFires()
    {
        Assert.IsNaN(FirstFire(new StandingCheck(), 30, true, 2.0, 0, KerbinG));
    }

    [Test]
    public void NaNReadings_NeverFire()
    {
        Assert.IsNaN(FirstFire(new StandingCheck(), 30, true, double.NaN, 0, KerbinG));
        Assert.IsNaN(FirstFire(new StandingCheck(), 30, true, 0, double.NaN, KerbinG));
    }

    [Test]
    public void Interruption_RestartsTheHold()
    {
        var check = new StandingCheck();
        Assert.IsFalse(check.Update(0, true, 0, 0, KerbinG));
        Assert.IsFalse(check.Update(1.0, true, 0, 0, KerbinG));
        Assert.IsFalse(check.Update(1.2, true, 3.0, 0, KerbinG)); // moved
        Assert.IsFalse(check.Update(1.4, true, 0, 0, KerbinG));
        Assert.IsFalse(check.Update(2.8, true, 0, 0, KerbinG));
        Assert.IsTrue(check.Update(2.9, true, 0, 0, KerbinG));
    }

    [Test]
    public void Reset_ForgetsTheStart()
    {
        var check = new StandingCheck();
        check.Update(0, true, 0, 0, KerbinG);
        check.Reset();
        Assert.IsFalse(check.Update(1.6, true, 0, 0, KerbinG));
        Assert.IsTrue(check.Update(3.1, true, 0, 0, KerbinG));
    }
}

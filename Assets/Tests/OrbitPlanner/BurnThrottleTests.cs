using K2D2.Node;
using NUnit.Framework;

public class BurnThrottleTests
{
    [Test]
    public void NoActiveEngine_FullThrottle_NoBurnTime()
    {
        // A stage still to fire gives full Δv but nothing lit: the burn time used to come from it.
        var step = BurnThrottle.Compute(100, 0, 1.5f);
        Assert.IsTrue(step.NoThrust);
        Assert.AreEqual(1f, step.Throttle);
        Assert.AreEqual(0f, step.FullBurnTime);
        Assert.IsTrue(BurnThrottle.Compute(100, double.NaN, 1.5f).NoThrust);
    }

    [Test]
    public void ActiveEngine_LongBurn_FullThrottle()
    {
        var step = BurnThrottle.Compute(100, 10, 1.5f);
        Assert.IsFalse(step.NoThrust);
        Assert.AreEqual(1f, step.Throttle);
        Assert.AreEqual(10f, step.FullBurnTime, 1e-6);
    }

    [Test]
    public void LastSecond_ThrottleFollowsTheBurnTime()
    {
        var step = BurnThrottle.Compute(2, 10, 1.5f);
        Assert.AreEqual(0.2f, step.FullBurnTime, 1e-6);
        Assert.AreEqual(0.3f, step.Throttle, 1e-6);
    }

    [Test]
    public void NothingLeft_ZeroThrottle()
    {
        var step = BurnThrottle.Compute(0, 10, 1.5f);
        Assert.AreEqual(0f, step.Throttle);
        Assert.IsFalse(step.NoThrust);
        Assert.AreEqual(0f, BurnThrottle.Compute(-3, 0, 1.5f).Throttle);
    }
}

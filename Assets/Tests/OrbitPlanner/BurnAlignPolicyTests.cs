using K2D2.Node;
using NUnit.Framework;

// Align before warping to a burn: on rails warp the vessel cannot turn.
public class BurnAlignPolicyTests
{
    [Test]
    public void Aligned_UnderTwoDegreesAndHalfADegreePerSecond()
    {
        Assert.IsTrue(BurnAlignPolicy.Aligned(1.5, 0.2));
        Assert.IsFalse(BurnAlignPolicy.Aligned(2.5, 0.2), "angle");
        Assert.IsFalse(BurnAlignPolicy.Aligned(1.5, 0.8), "still turning");
        Assert.IsFalse(BurnAlignPolicy.Aligned(99.4, 0), "the 2026-10-25 screenshot");
    }

    [Test]
    public void Timeout_SixtySecondsOrTwiceTheLead()
    {
        Assert.AreEqual(60, BurnAlignPolicy.TimeoutSeconds(20));
        Assert.AreEqual(240, BurnAlignPolicy.TimeoutSeconds(120));
        Assert.AreEqual(60, BurnAlignPolicy.TimeoutSeconds(double.NaN));
    }

    [Test]
    public void WarpTail_TenSecondsOrFifteenForALongBurn()
    {
        Assert.AreEqual(10, BurnAlignPolicy.WarpTail(5));
        Assert.AreEqual(10, BurnAlignPolicy.WarpTail(double.NaN), "no thrust known");
        Assert.AreEqual(15, BurnAlignPolicy.WarpTail(79));
    }

    [Test]
    public void SecondPreAlign_LongWaitOrTurningFrame()
    {
        Assert.IsFalse(BurnAlignPolicy.SecondPreAlign(600, 2000, 10), "short wait, little turn");
        Assert.IsTrue(BurnAlignPolicy.SecondPreAlign(3000, 2000, 10), "longer than one orbit");
        Assert.IsTrue(BurnAlignPolicy.SecondPreAlign(600, 2000, 108), "a prograde burn in low orbit: the frame turns");
        Assert.IsFalse(BurnAlignPolicy.SecondPreAlign(1e6, double.PositiveInfinity, 5), "an escape: no period");
        Assert.AreEqual(1000 - 60 - 30, BurnAlignPolicy.SecondPreAlignUT(1000, 60));
    }

    [Test]
    public void Realign_WhenTheDriftExceedsTwoDegrees()
    {
        Assert.IsFalse(BurnAlignPolicy.NeedsRealign(1.9));
        Assert.IsTrue(BurnAlignPolicy.NeedsRealign(2.1));
    }
}

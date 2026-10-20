using K2D2.VabTools;
using NUnit.Framework;

// Final review I3: the settle wait scales with the part count, 10 s floor, 60 s cap.
public class VabSettleTests
{
    [Test]
    public void Timeout_HasAFloorOfTenSeconds()
    {
        Assert.AreEqual(10f, VabSettle.TimeoutS(-5), 1e-4);
        Assert.AreEqual(10f, VabSettle.TimeoutS(0), 1e-4);
        Assert.AreEqual(10f, VabSettle.TimeoutS(100), 1e-4);
    }

    [Test]
    public void Timeout_GrowsByATenthOfASecondPerPart()
    {
        Assert.AreEqual(25f, VabSettle.TimeoutS(250), 1e-4);
        Assert.AreEqual(45f, VabSettle.TimeoutS(450), 1e-4);
    }

    [Test]
    public void Timeout_IsCappedAtSixtySeconds()
    {
        Assert.AreEqual(60f, VabSettle.TimeoutS(600), 1e-4);
        Assert.AreEqual(60f, VabSettle.TimeoutS(5000), 1e-4);
    }
}

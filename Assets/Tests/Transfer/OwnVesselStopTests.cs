using K2D2.Transfer;
using NUnit.Framework;

public class OwnVesselStopTests
{
    [Test]
    public void Inputs_CutTheThrottleOnly_ForABurn()
    {
        var i = OwnVesselStop.Inputs(false);
        Assert.AreEqual(0f, i.mainThrottle);
        Assert.IsFalse(i.X.HasValue);
        Assert.IsFalse(i.Y.HasValue);
        Assert.IsFalse(i.Z.HasValue);
        Assert.IsFalse(i.pitch.HasValue, "the stick is left as it is");
        Assert.IsFalse(i.killRot.HasValue);
    }

    [Test]
    public void Inputs_CutTheThrottleAndTheTranslation_ForADocking()
    {
        var i = OwnVesselStop.Inputs(true);
        Assert.AreEqual(0f, i.mainThrottle);
        Assert.AreEqual(0f, i.X);
        Assert.AreEqual(0f, i.Y);
        Assert.AreEqual(0f, i.Z);
        Assert.IsFalse(i.roll.HasValue);
    }

    [Test]
    public void Apply_WithoutAVessel_DoesNothing()
    {
        Assert.IsNull(OwnVesselStop.Find(null));
        Assert.IsFalse(OwnVesselStop.Apply(null, true));
        Assert.IsFalse(OwnVesselStop.Apply("", false));
    }
}

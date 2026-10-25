using K2D2.Node;
using NUnit.Framework;

// The player's burn time warp mode (Node tab, ADVANCED): what each mode lets a burn use.
public class BurnWarpModeTests
{
    [Test]
    public void Off_NeitherPhysicsNorRails()
    {
        Assert.IsFalse(BurnWarpModes.AllowsPhysics(BurnWarpMode.Off));
        Assert.IsFalse(BurnWarpModes.AllowsRails(BurnWarpMode.Off));
        Assert.AreEqual(BurnWarpMode.Off, default(BurnWarpMode), "Off is the first value, the one a missing setting reads as");
    }

    [Test]
    public void Physics_NoRails_PhysicsAndRails_Both()
    {
        Assert.IsTrue(BurnWarpModes.AllowsPhysics(BurnWarpMode.Physics));
        Assert.IsFalse(BurnWarpModes.AllowsRails(BurnWarpMode.Physics));
        Assert.IsTrue(BurnWarpModes.AllowsPhysics(BurnWarpMode.PhysicsAndRails));
        Assert.IsTrue(BurnWarpModes.AllowsRails(BurnWarpMode.PhysicsAndRails));
    }

    [Test]
    public void Labels_OnePerMode()
    {
        Assert.AreEqual(System.Enum.GetValues(typeof(BurnWarpMode)).Length, BurnWarpModes.Labels.Split(';').Length);
        StringAssert.Contains("1x", BurnWarpModes.Describe(BurnWarpMode.Off));
    }
}

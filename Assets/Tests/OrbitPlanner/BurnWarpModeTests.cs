using SkySpaceAgency.Node;
using NUnit.Framework;

// The player's burn time warp mode (Node tab, ADVANCED): what each mode lets a burn use.
public class BurnWarpModeTests
{
    [Test]
    public void Off_NeitherPhysicsNorRails()
    {
        Assert.IsFalse(BurnWarpModes.AllowsPhysics(BurnWarpMode.Off));
        Assert.IsFalse(BurnWarpModes.AllowsRails(BurnWarpMode.Off));
    }

    [Test]
    public void Physics_NoRails_Rails_NoPhysics()
    {
        Assert.IsTrue(BurnWarpModes.AllowsPhysics(BurnWarpMode.Physics));
        Assert.IsFalse(BurnWarpModes.AllowsRails(BurnWarpMode.Physics));
        // Rails (native): 1x around the game's rails warp, no physics warp.
        Assert.IsFalse(BurnWarpModes.AllowsPhysics(BurnWarpMode.Rails));
        Assert.IsTrue(BurnWarpModes.AllowsRails(BurnWarpMode.Rails));
    }

    [Test]
    public void Default_IsRails_AndTheOldPhysicsAndRailsValueReadsAsTheDefault()
    {
        Assert.AreEqual(BurnWarpMode.Rails, BurnWarpModes.Default);
        // A settings file saved before 26w46a may hold "PhysicsAndRails": it no longer parses, so
        // the setting reads its default (Rails). "Off" and "Physics" are kept as the player chose.
        Assert.IsFalse(System.Enum.TryParse<BurnWarpMode>("PhysicsAndRails", out _));
        Assert.IsTrue(System.Enum.TryParse<BurnWarpMode>("Off", out var off));
        Assert.AreEqual(BurnWarpMode.Off, off);
    }

    [Test]
    public void Labels_OnePerMode()
    {
        Assert.AreEqual(System.Enum.GetValues(typeof(BurnWarpMode)).Length, BurnWarpModes.Labels.Split(';').Length);
        StringAssert.Contains("1x", BurnWarpModes.Describe(BurnWarpMode.Off));
        StringAssert.Contains("native", BurnWarpModes.Labels);
    }
}

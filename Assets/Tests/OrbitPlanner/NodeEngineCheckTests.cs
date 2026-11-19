using SkySpaceAgency.Node;
using NUnit.Framework;

public class NodeEngineCheckTests
{
    [Test]
    public void LitEngine_Starts()
    {
        var state = NodeEngineCheck.Evaluate(true, 500);
        Assert.AreEqual(NodeEngineState.Active, state);
        Assert.IsTrue(NodeEngineCheck.CanStart(state));
        Assert.IsFalse(NodeEngineCheck.Disagrees(true, 500));
    }

    [Test]
    public void CoastWithStageDv_StartsAndIsLogged()
    {
        // EngineIgnited may read false at throttle 0: the stage's Δv still lets the run start.
        var state = NodeEngineCheck.Evaluate(false, 800);
        Assert.AreEqual(NodeEngineState.StageOnly, state);
        Assert.IsTrue(NodeEngineCheck.CanStart(state));
        Assert.IsTrue(NodeEngineCheck.Disagrees(false, 800));
    }

    [Test]
    public void NoLitEngineAndNoStageDv_Refused()
    {
        Assert.IsFalse(NodeEngineCheck.CanStart(NodeEngineCheck.Evaluate(false, 0)));
        Assert.IsFalse(NodeEngineCheck.CanStart(NodeEngineCheck.Evaluate(false, double.NaN)));
        Assert.IsFalse(NodeEngineCheck.Disagrees(false, 0));
    }

    [Test]
    public void LitEngineWithoutStageDv_StartsAndIsLogged()
    {
        Assert.IsTrue(NodeEngineCheck.CanStart(NodeEngineCheck.Evaluate(true, 0)));
        Assert.IsTrue(NodeEngineCheck.Disagrees(true, 0));
        Assert.IsFalse(NodeEngineCheck.Disagrees(true, double.NaN));
    }
}

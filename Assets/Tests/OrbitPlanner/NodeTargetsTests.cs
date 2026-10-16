using System;
using K2D2.KSPService;
using K2D2.OrbitPlanning;
using KSP.Sim;
using NUnit.Framework;

public class NodeTargetsTests
{
    static TargetOrbit Target(double ra) => new TargetOrbit { Ra = ra, Rp = 700000, PlaneNormal = new Vector3d(0, 0, 1) };

    [SetUp]
    public void SetUp() => NodeTargets.Clear();

    [TearDown]
    public void TearDown() => NodeTargets.Clear();

    [Test]
    public void Register_ThenTryGet_ReturnsTheTarget()
    {
        var id = Guid.NewGuid();
        NodeTargets.Register(id, Target(900000));

        Assert.IsTrue(NodeTargets.TryGet(id, out var target));
        Assert.AreEqual(900000, target.Ra);
        Assert.IsFalse(NodeTargets.TryGet(Guid.NewGuid(), out _), "unknown node");
    }

    [Test]
    public void Register_TwiceForTheSameNode_KeepsTheLastTarget()
    {
        var id = Guid.NewGuid();
        NodeTargets.Register(id, Target(900000));
        NodeTargets.Register(id, Target(950000));

        Assert.AreEqual(1, NodeTargets.Count);
        Assert.IsTrue(NodeTargets.TryGet(id, out var target));
        Assert.AreEqual(950000, target.Ra);
    }

    [Test]
    public void Remove_ClearsOnlyThatNode()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        NodeTargets.Register(a, Target(900000));
        NodeTargets.Register(b, Target(950000));

        NodeTargets.Remove(a);
        NodeTargets.Remove(Guid.NewGuid());

        Assert.IsFalse(NodeTargets.TryGet(a, out _));
        Assert.IsTrue(NodeTargets.TryGet(b, out _));
    }

    [Test]
    public void Clear_RemovesEverything()
    {
        NodeTargets.Register(Guid.NewGuid(), Target(900000));
        NodeTargets.Register(Guid.NewGuid(), Target(950000));

        NodeTargets.Clear();

        Assert.AreEqual(0, NodeTargets.Count);
    }

    [Test]
    public void Prune_KeepsOnlyTheLiveNodes()
    {
        var live = Guid.NewGuid();
        var gone = Guid.NewGuid();
        NodeTargets.Register(live, Target(900000));
        NodeTargets.Register(gone, Target(950000));

        NodeTargets.Prune(new[] { live, Guid.NewGuid() });

        Assert.IsTrue(NodeTargets.TryGet(live, out _));
        Assert.IsFalse(NodeTargets.TryGet(gone, out _));
        Assert.AreEqual(1, NodeTargets.Count);
    }
}

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
        NodeTargets.Register(id, Target(900000), new Vector3d(1, 2, 300), 5000);

        Assert.IsTrue(NodeTargets.TryGet(id, out var entry));
        Assert.AreEqual(900000, entry.Target.Ra);
        Assert.AreEqual(300, entry.BurnVector.z);
        Assert.AreEqual(5000, entry.Time);
        Assert.IsFalse(NodeTargets.TryGet(Guid.NewGuid(), out _), "unknown node");
    }

    [Test]
    public void Register_TwiceForTheSameNode_KeepsTheLastTarget()
    {
        var id = Guid.NewGuid();
        NodeTargets.Register(id, Target(900000), Vector3d.zero, 0);
        NodeTargets.Register(id, Target(950000), Vector3d.zero, 0);

        Assert.AreEqual(1, NodeTargets.Count);
        Assert.IsTrue(NodeTargets.TryGet(id, out var entry));
        Assert.AreEqual(950000, entry.Target.Ra);
    }

    [Test]
    public void Remove_ClearsOnlyThatNode()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        NodeTargets.Register(a, Target(900000), Vector3d.zero, 0);
        NodeTargets.Register(b, Target(950000), Vector3d.zero, 0);

        NodeTargets.Remove(a);
        NodeTargets.Remove(Guid.NewGuid());

        Assert.IsFalse(NodeTargets.TryGet(a, out _));
        Assert.IsTrue(NodeTargets.TryGet(b, out _));
    }

    [Test]
    public void Clear_RemovesEverything()
    {
        NodeTargets.Register(Guid.NewGuid(), Target(900000), Vector3d.zero, 0);
        NodeTargets.Register(Guid.NewGuid(), Target(950000), Vector3d.zero, 0);

        NodeTargets.Clear();

        Assert.AreEqual(0, NodeTargets.Count);
    }

    [Test]
    public void Prune_KeepsOnlyTheLiveNodes()
    {
        var live = Guid.NewGuid();
        var gone = Guid.NewGuid();
        NodeTargets.Register(live, Target(900000), Vector3d.zero, 0);
        NodeTargets.Register(gone, Target(950000), Vector3d.zero, 0);

        NodeTargets.Prune(new[] { live, Guid.NewGuid() });

        Assert.IsTrue(NodeTargets.TryGet(live, out _));
        Assert.IsFalse(NodeTargets.TryGet(gone, out _));
        Assert.AreEqual(1, NodeTargets.Count);
    }

    [Test]
    public void IsEdited_BeyondHalfAMeterPerSecondOrOneSecond()
    {
        var entry = new NodeTarget { Target = Target(900000), BurnVector = new Vector3d(1, 2, 300), Time = 5000 };

        Assert.IsFalse(entry.IsEdited(new Vector3d(1.4, 1.6, 300.4), 5000.9), "within the tolerances");
        Assert.IsTrue(entry.IsEdited(new Vector3d(1, 2, 300.6), 5000), "prograde changed");
        Assert.IsTrue(entry.IsEdited(new Vector3d(1, 2.6, 300), 5000), "normal changed");
        Assert.IsTrue(entry.IsEdited(new Vector3d(1, 2, 300), 5001.5), "time changed");
        Assert.IsTrue(entry.IsEdited(new Vector3d(1, 2, 300), 4998), "time changed");
    }
}

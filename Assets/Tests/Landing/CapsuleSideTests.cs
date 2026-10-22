using System.Collections.Generic;
using K2D2.Landing.Precision;
using NUnit.Framework;

public class CapsuleSideTests
{
    static CapsulePart P(string id, string parent, double mass = 1) =>
        new CapsulePart { Id = id, ParentId = parent, MassT = mass };
    static CapsulePart Chute(string id, string parent) =>
        new CapsulePart { Id = id, ParentId = parent, IsParachute = true, MassT = 0.1 };
    static CapsulePart Decoupler(string id, string parent, string detachesFrom) =>
        new CapsulePart { Id = id, ParentId = parent, IsDecoupler = true, DetachesFromId = detachesFrom, MassT = 0.05 };

    static void AssertIds(CapsuleSideResult r, params string[] ids)
    {
        Assert.IsTrue(r.Ok, r.Reason);
        CollectionAssert.AreEquivalent(ids, r.Ids);
    }

    // Pod (root) with its chute on top; the decoupler below stays with the pod and drops the tank.
    [Test]
    public void PodRoot_DecouplerStaysWithThePod()
    {
        var r = CapsuleSide.Find(new List<CapsulePart>
        {
            P("pod", null, 2), Chute("chute", "pod"), Decoupler("dec", "pod", "tank"), P("tank", "dec", 4), P("engine", "tank", 1),
        });
        AssertIds(r, "pod", "chute", "dec");
        CollectionAssert.AreEquivalent(new[] { "dec" }, r.BoundaryIds);
        Assert.AreEqual(2.15, r.MassT, 1e-9);
        Assert.AreEqual(1, r.Parachutes);
        Assert.IsFalse(r.WholeVessel);
    }

    // The same stack built from the engine up: the decoupler separates from its parent (the tank).
    [Test]
    public void EngineRoot_TheSameCapsule()
    {
        var r = CapsuleSide.Find(new List<CapsulePart>
        {
            P("engine", null), P("tank", "engine"), Decoupler("dec", "tank", "tank"), P("pod", "dec"), Chute("chute", "pod"),
        });
        AssertIds(r, "pod", "chute", "dec");
        CollectionAssert.AreEquivalent(new[] { "dec" }, r.BoundaryIds);
    }

    // The decoupler separates from the pod: it falls with the tank.
    [Test]
    public void DecouplerStaysWithTheServiceModule()
    {
        var r = CapsuleSide.Find(new List<CapsulePart>
        {
            P("pod", null), Chute("chute", "pod"), Decoupler("dec", "pod", "pod"), P("tank", "dec"),
        });
        AssertIds(r, "pod", "chute");
        CollectionAssert.AreEquivalent(new[] { "pod" }, r.BoundaryIds);
    }

    // Two stack decouplers: the capsule ends at the nearest one.
    [Test]
    public void TwoDecouplers_TheNearestOneBoundsTheCapsule()
    {
        var r = CapsuleSide.Find(new List<CapsulePart>
        {
            P("pod", null), Chute("chute", "pod"), Decoupler("dec1", "pod", "tank1"), P("tank1", "dec1"),
            Decoupler("dec2", "tank1", "tank2"), P("tank2", "dec2"),
        });
        AssertIds(r, "pod", "chute", "dec1");
    }

    // Radial chutes on the pod, and a heat shield (not a decoupler) between the pod and the decoupler.
    [Test]
    public void RadialChutesAndHeatShield_AllInTheCapsule()
    {
        var r = CapsuleSide.Find(new List<CapsulePart>
        {
            P("pod", null), Chute("c1", "pod"), Chute("c2", "pod"), P("shield", "pod"),
            Decoupler("dec", "shield", "tank"), P("tank", "dec"),
        });
        AssertIds(r, "pod", "c1", "c2", "shield", "dec");
        Assert.AreEqual(2, r.Parachutes);
    }

    [Test]
    public void ChutesOnBothSidesOfTheDecoupler_Refused()
    {
        var r = CapsuleSide.Find(new List<CapsulePart>
        {
            P("pod", null), Chute("chute", "pod"), Decoupler("dec", "pod", "tank"), P("tank", "dec"), Chute("booster chute", "tank"),
        });
        Assert.IsFalse(r.Ok);
        StringAssert.StartsWith(CapsuleSide.Prefix, r.Reason);
        StringAssert.Contains("1 of the 2 parachutes", r.Reason);
    }

    [Test]
    public void NoParachute_Refused()
    {
        var r = CapsuleSide.Find(new List<CapsulePart> { P("pod", null), Decoupler("dec", "pod", "tank"), P("tank", "dec") });
        Assert.IsFalse(r.Ok);
        StringAssert.Contains("no parachute", r.Reason);
    }

    [Test]
    public void NoDecoupler_TheWholeVessel()
    {
        var r = CapsuleSide.Find(new List<CapsulePart> { P("pod", null), Chute("chute", "pod"), P("tank", "pod") });
        AssertIds(r, "pod", "chute", "tank");
        Assert.IsTrue(r.WholeVessel);
        Assert.IsEmpty(r.BoundaryIds);
    }

    // Unknown explosive node (or an omni decoupler): it separates from every neighbour and joins neither side.
    [Test]
    public void UnknownExplosiveNode_TheDecouplerBelongsToNeitherSide()
    {
        var r = CapsuleSide.Find(new List<CapsulePart>
        {
            P("pod", null), Chute("chute", "pod"), Decoupler("dec", "pod", null), P("tank", "dec"),
        });
        AssertIds(r, "pod", "chute");
    }

    // A decoupler whose explosive node holds nothing separates nothing; one that names a part that
    // is not its neighbour is treated as unknown.
    [Test]
    public void EmptyOrForeignExplosiveNode()
    {
        var empty = CapsuleSide.Find(new List<CapsulePart>
        {
            P("pod", null), Chute("chute", "pod"), Decoupler("dec", "pod", ""), P("tank", "dec"),
        });
        AssertIds(empty, "pod", "chute", "dec", "tank");
        var foreign = CapsuleSide.Find(new List<CapsulePart>
        {
            P("pod", null), Chute("chute", "pod"), Decoupler("dec", "pod", "engine"), P("tank", "dec"), P("engine", "tank"),
        });
        AssertIds(foreign, "pod", "chute");
    }

    [Test]
    public void DuplicateIds_Refused()
    {
        var r = CapsuleSide.Find(new List<CapsulePart> { P("pod", null), Chute("pod", null) });
        Assert.IsFalse(r.Ok);
    }
}

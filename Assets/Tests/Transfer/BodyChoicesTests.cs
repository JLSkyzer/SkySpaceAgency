using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.Transfer;
using KSP.Sim;
using NUnit.Framework;

public class BodyChoicesTests
{
    [Test]
    public void Kerbol_GroupedByParent_NearestOrbitFirst_Indented()
    {
        var list = BodyChoices.Grouped(KerbolFixture.Build());
        CollectionAssert.AreEqual(
            new[] { "Kerbol", "Moho", "Eve", "Kerbin", "Mun", "Minmus", "Duna", "Ike", "Dres", "Jool", "Laythe", "Vall", "Tylo" },
            list.Select(e => e.Name).ToArray());
        var mun = list[BodyChoices.FindName(list, "Mun")];
        Assert.AreEqual(2, mun.Depth);
        Assert.AreEqual(BodyChoices.Indent + BodyChoices.Indent + "Mun", mun.Label);
        Assert.AreEqual("Kerbol", list[0].Label);
        Assert.AreEqual(1, list[BodyChoices.FindName(list, "Duna")].Depth);
        foreach (var e in list)
            Assert.AreEqual(e.Name, KerbolFixture.Build()[e.Index].Name);
    }

    // The order comes from the orbits, not from the indices.
    [Test]
    public void ChildrenSortedByPeriod_NotByIndex()
    {
        var s = new BodySystem(new List<BodyNode>
        {
            Root("Star"),
            Moon("Far", 1, 0, 9e9),
            Moon("Moon of near", 2, 3, 1e6),
            Moon("Near", 3, 0, 1e9),
        });
        var list = BodyChoices.Grouped(s);
        CollectionAssert.AreEqual(new[] { "Star", "Near", "Moon of near", "Far" }, list.Select(e => e.Name).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 1 }, list.Select(e => e.Depth).ToArray());
        Assert.AreEqual(2, BodyChoices.FindLabel(list, BodyChoices.Indent + BodyChoices.Indent + "Moon of near"));
        Assert.AreEqual(-1, BodyChoices.FindLabel(list, "Moon of near"));
        Assert.AreEqual(4, BodyChoices.Labels(list).Count);
    }

    [Test]
    public void NoSystem_Empty() => Assert.AreEqual(0, BodyChoices.Grouped(null).Count);

    static BodyNode Root(string name) => new BodyNode { Name = name, Index = 0, Mu = 1e18, Radius = 1e8, Soi = double.PositiveInfinity };

    static BodyNode Moon(string name, int index, int parent, double radius)
    {
        double mu = parent == 0 ? 1e18 : 1e10;
        return new BodyNode
        {
            Name = name, Index = index, ParentIndex = parent, Mu = 1e10, Radius = 1e4, Soi = 1e5,
            EpochPosition = new Vector3d(radius, 0, 0), EpochVelocity = new Vector3d(0, Math.Sqrt(mu / radius), 0),
        };
    }
}

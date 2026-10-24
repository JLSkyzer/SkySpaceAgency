using System;
using System.Collections.Generic;
using K2D2.Rendezvous;
using KSP.Sim;
using NUnit.Framework;

// Positions and axes are inertial; the target is at the origin and we are along +x from it.
public class PortChooserTests
{
    static readonly Vector3d FromTargetToUs = new Vector3d(1, 0, 0);
    static readonly Vector3d OurForward = new Vector3d(0, 0, 1);

    static PortInfo Port(string id, Vector3d axis, string type = "size1", bool free = true, bool gendered = false, bool female = false)
    {
        return new PortInfo
        {
            PartId = id, NodeTypes = new[] { type }, Free = free, Gendered = gendered, Female = female,
            Position = axis, Axis = axis.normalized, Size = 1.25,
        };
    }

    static Vector3d Deg(double angleDeg) =>
        new Vector3d(-Math.Cos(angleDeg * Math.PI / 180), Math.Sin(angleDeg * Math.PI / 180), 0);

    [Test]
    public void Ports_NoCompatible_Refused()
    {
        var ours = new List<PortInfo> { Port("o1", Deg(0), "size1"), Port("o2", Deg(0), "size2", free: false) };
        var theirs = new List<PortInfo> { Port("t1", FromTargetToUs, "size0"), Port("t2", FromTargetToUs, "size2") };
        var (o, t, reason) = PortChooser.Choose(ours, theirs, OurForward, FromTargetToUs);
        Assert.IsNotNull(reason);
        Assert.IsNull(o.PartId);
        Assert.IsNull(t.PartId);

        // Empty or missing lists, and missing node types.
        Assert.IsNotNull(PortChooser.Choose(new List<PortInfo>(), theirs, OurForward, FromTargetToUs).reason);
        Assert.IsNotNull(PortChooser.Choose(ours, null, OurForward, FromTargetToUs).reason);
        var untyped = new List<PortInfo> { new PortInfo { PartId = "x", Free = true, Axis = FromTargetToUs } };
        Assert.IsNotNull(PortChooser.Choose(ours, untyped, OurForward, FromTargetToUs).reason);

        // The target's only matching port is taken.
        var taken = new List<PortInfo> { Port("t3", FromTargetToUs, "size1", free: false) };
        StringAssert.Contains("free", PortChooser.Choose(ours, taken, OurForward, FromTargetToUs).reason);
    }

    [Test]
    public void Ports_PrefersFacingAndLeastRotation()
    {
        // t-near faces us, t-far faces away. o-back points away from the target: it mates with
        // t-far without turning, but t-far is on the other side. o-60 needs a 60° turn to t-near.
        var ours = new List<PortInfo> { Port("o-back", new Vector3d(1, 0, 0)), Port("o-60", Deg(60)) };
        var theirs = new List<PortInfo> { Port("t-far", new Vector3d(-1, 0, 0)), Port("t-near", new Vector3d(1, 0, 0)) };
        var (o, t, reason) = PortChooser.Choose(ours, theirs, OurForward, FromTargetToUs);
        Assert.IsNull(reason);
        Assert.AreEqual("o-60", o.PartId);
        Assert.AreEqual("t-near", t.PartId);

        // Among ports facing us, the least rotation wins.
        ours.Add(Port("o-20", Deg(20)));
        (o, t, reason) = PortChooser.Choose(ours, theirs, OurForward, FromTargetToUs);
        Assert.AreEqual("o-20", o.PartId);
        Assert.AreEqual("t-near", t.PartId);

        // Only ports facing away: still chosen, the guidance goes around the target.
        var away = new List<PortInfo> { Port("t-far", new Vector3d(-1, 0, 0)) };
        (o, t, reason) = PortChooser.Choose(ours, away, OurForward, FromTargetToUs);
        Assert.IsNull(reason);
        Assert.AreEqual("o-back", o.PartId);
        Assert.AreEqual("t-far", t.PartId);
    }

    [Test]
    public void Ports_GenderedPairing()
    {
        var theirs = new List<PortInfo> { Port("t-f", FromTargetToUs, "clamp", gendered: true, female: true) };
        var ours = new List<PortInfo>
        {
            Port("o-f", Deg(0), "clamp", gendered: true, female: true),    // same gender: no
            Port("o-n", Deg(0), "clamp"),                                   // ungendered: no
            Port("o-m", Deg(45), "clamp", gendered: true, female: false),  // opposite: yes
        };
        var (o, t, reason) = PortChooser.Choose(ours, theirs, OurForward, FromTargetToUs);
        Assert.IsNull(reason);
        Assert.AreEqual("o-m", o.PartId);
        Assert.AreEqual("t-f", t.PartId);

        // Two ungendered ports mate; a gendered port finds no opposite one.
        var plain = new List<PortInfo> { Port("t-n", FromTargetToUs, "clamp") };
        Assert.AreEqual("o-n", PortChooser.Choose(ours, plain, OurForward, FromTargetToUs).ours.PartId);
        var sameOnly = new List<PortInfo> { ours[0] };
        StringAssert.Contains("gender", PortChooser.Choose(sameOnly, theirs, OurForward, FromTargetToUs).reason);
    }
}

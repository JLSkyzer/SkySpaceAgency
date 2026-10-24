using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.Rendezvous;
using KSP.Modules;
using KSP.OAB;
using KSP.Sim;
using NUnit.Framework;

public class DockingReadsTests
{
    const double Deg = Math.PI / 180;

    static void Near(Vector3d expected, Vector3d actual, double tol = 1e-9)
    {
        Assert.Less((expected - actual).magnitude, tol, $"expected {expected}, got {actual}");
    }

    [Test]
    public void Port_FromRawFields()
    {
        var p = DockingReads.Port("id", new[] { "size1", "", null, "size2" }, true, false, true,
            new Vector3d(1, 2, 3), new Vector3d(0, 0, 5), 1);
        Assert.AreEqual("id", p.PartId);
        CollectionAssert.AreEqual(new[] { "size1", "size2" }, p.NodeTypes);
        Assert.IsTrue(p.Free);
        Assert.IsFalse(p.Female, "female means nothing on an ungendered port");
        Near(new Vector3d(0, 0, 1), p.Axis);
        Near(new Vector3d(1, 2, 3), p.Position);

        var busy = DockingReads.Port("id", null, false, true, true, new Vector3d(double.NaN, 0, 0), Vector3d.zero, 1);
        Assert.IsFalse(busy.Free);
        Assert.IsTrue(busy.Female);
        Assert.AreEqual(0, busy.NodeTypes.Length);
        Assert.AreEqual(Vector3d.zero, busy.Position);
        Assert.AreEqual(Vector3d.zero, busy.Axis, "an unreadable axis is zero: PortChooser counts it as the worst rotation");
        Assert.IsFalse(DockingReads.Port(null, new[] { "a" }, true, false, false, Vector3d.zero, Vector3d.one, 1).Free);
    }

    [Test]
    public void Port_BuiltPairIsChosen()
    {
        var ours = DockingReads.Port("o", new[] { "size1" }, true, false, false, new Vector3d(100, 0, 0), new Vector3d(-1, 0, 0), 1);
        var theirs = DockingReads.Port("t", new[] { "size1" }, true, false, false, new Vector3d(1, 0, 0), new Vector3d(1, 0, 0), 1);
        var (o, t, reason) = PortChooser.Choose(new List<PortInfo> { ours }, new List<PortInfo> { theirs },
            new Vector3d(-1, 0, 0), new Vector3d(1, 0, 0));
        Assert.IsNull(reason);
        Assert.AreEqual("o", o.PartId);
        Assert.AreEqual("t", t.PartId);
    }

    [Test]
    public void SizeMetres_Categories()
    {
        Assert.AreEqual(1.25, DockingReads.SizeMetres(MetaAssemblySizeFilterType.S));
        Assert.AreEqual(2.5, DockingReads.SizeMetres(MetaAssemblySizeFilterType.M));
        Assert.AreEqual(0, DockingReads.SizeMetres(MetaAssemblySizeFilterType.Auto));
    }

    [Test]
    public void Limits_StricterOfBothPorts()
    {
        var a = new PortCapture { AcquireMinFwdDot = 0.9f, CaptureMinFwdDot = 0.95, CaptureMaxRvel = 0.4, MinRollDot = -1, CaptureMinRollDot = -1 };
        var b = new PortCapture { AcquireMinFwdDot = 0.8, CaptureMinFwdDot = 0.97, CaptureMaxRvel = 0.3, MinRollDot = 0.9, CaptureMinRollDot = 0 };
        var rcs = DockingReads.Limits(a, b, true, 12);
        Assert.AreEqual(0.97, rcs.CaptureMinFwdDot, 1e-9);
        Assert.AreEqual(0.3, rcs.CaptureMaxRvel, 1e-9);
        Assert.AreEqual(0.9, rcs.MinRollDot, 1e-9);
        Assert.AreEqual(12, rcs.TargetRadius);
        Assert.IsFalse(rcs.MainEngineMode);
        Assert.AreEqual(50, rcs.HoldFar);

        var main = DockingReads.Limits(a, a, false, 12);
        Assert.IsTrue(main.MainEngineMode);
        Assert.AreEqual(0, main.MinRollDot, "a roll dot ≤ 0 means any roll");

        // Unusable values: no capture speed falls back to 0.5 m/s, a bad radius to a part's size.
        var bad = DockingReads.Limits(new PortCapture { CaptureMaxRvel = double.NaN }, new PortCapture { CaptureMaxRvel = -1 }, true, double.NaN);
        Assert.AreEqual(0.5, bad.CaptureMaxRvel, 1e-9);
        Assert.AreEqual(DockingReads.PartHalfSize, bad.TargetRadius);
    }

    [Test]
    public void BoundingRadius_FarthestPartPlusHalfSize()
    {
        var c = new Vector3d(10, 0, 0);
        var parts = new[] { new Vector3d(10, 3, 0), new Vector3d(4, 0, 0), new Vector3d(double.NaN, 0, 0) };
        Assert.AreEqual(6 + DockingReads.PartHalfSize, DockingReads.BoundingRadius(parts, c), 1e-9);
        Assert.AreEqual(DockingReads.PartHalfSize, DockingReads.BoundingRadius(null, c), 1e-9);
    }

    // v = ω × r in the Yup (left-handed) frame must stay v = ω × r once converted to Zup.
    [Test]
    public void Zup_KeepsRigidBodyVelocity()
    {
        var w = new Vector3d(0.1, -0.3, 0.7);
        var r = new Vector3d(2, 5, -1);
        var v = Vector3d.Cross(w, r);
        Near(DockingReads.ZupFromYup(v), Vector3d.Cross(DockingReads.ZupAngularFromYup(w), DockingReads.ZupFromYup(r)));
        Near(new Vector3d(1, 3, 2), DockingReads.ZupFromYup(new Vector3d(1, 2, 3)));
    }

    [Test]
    public void Spin_AboutAxisOnly()
    {
        var axis = new Vector3d(0, 0, 2);
        // 0.5°/s about the axis, 3°/s across it: only the first counts.
        var w = new Vector3d(3 * Deg, 0, 0.5 * Deg);
        Assert.AreEqual(0.5, DockingReads.SpinAboutAxisDegPerSec(w, axis), 1e-9);
        Assert.IsNull(DockingReads.SpinRefusal(w, axis));
        Assert.AreEqual(1.5, DockingReads.SpinAboutAxisDegPerSec(new Vector3d(0, 0, -1.5 * Deg), axis), 1e-9);
        Assert.IsNotNull(DockingReads.SpinRefusal(new Vector3d(0, 0, -1.5 * Deg), axis));
        Assert.IsNotNull(DockingReads.SpinRefusal(new Vector3d(double.NaN, 0, 0), axis));
        Assert.IsNotNull(DockingReads.SpinRefusal(w, Vector3d.zero));
    }

    [Test]
    public void State_RelativeToTargetPortFace()
    {
        // Target at the origin, spinning about z at 0.01 rad/s; its port 5 m along +x, axis +x.
        var wt = new Vector3d(0, 0, 0.01);
        var s = DockingReads.State(42,
            new Vector3d(60, 1, 0), new Vector3d(-0.2, 0, 0), Vector3d.zero,
            new Vector3d(57, 1, 0), new Vector3d(-1, 0, 0), new Vector3d(0, 0, 1),
            Vector3d.zero, Vector3d.zero, wt,
            new Vector3d(5, 0, 0), new Vector3d(2, 0, 0), new Vector3d(0, 0, 1));
        Assert.AreEqual(42, s.Time);
        Near(new Vector3d(52, 1, 0), s.RelPos);
        // The target face moves at ω × (5,0,0) = (0, 0.05, 0).
        Near(new Vector3d(-0.2, -0.05, 0), s.RelVel);
        Near(new Vector3d(1, 0, 0), s.TargetAxis);
        Near(new Vector3d(0, 0.01, 0), s.TargetAxisRate);
        Near(new Vector3d(-5, 0, 0), s.TargetCenter);
        Near(new Vector3d(-1, 0, 0), s.OurAxis);
        Near(new Vector3d(0, 0, 1), s.OurRoll);
        Near(new Vector3d(0, 0, 1), s.TargetRoll);

        // The axis rate is what the guidance turns back into ω about the face: a × ȧ = ω (across the axis).
        Near(wt, Vector3d.Cross(s.TargetAxis, s.TargetAxisRate));
    }

    [Test]
    public void State_FeedsTheGuidance()
    {
        var s = DockingReads.State(0,
            new Vector3d(80, 0, 0), Vector3d.zero, Vector3d.zero,
            new Vector3d(78, 0, 0), new Vector3d(-1, 0, 0), new Vector3d(0, 0, 1),
            Vector3d.zero, Vector3d.zero, Vector3d.zero,
            new Vector3d(3, 0, 0), new Vector3d(1, 0, 0), new Vector3d(0, 0, 1));
        var g = new DockingGuidance(DockingReads.Limits(new PortCapture { CaptureMinFwdDot = 0.95, CaptureMaxRvel = 0.4 },
            new PortCapture { CaptureMinFwdDot = 0.95, CaptureMaxRvel = 0.4 }, true, 6));
        var cmd = g.Step(s, 0.1);
        Near(new Vector3d(-1, 0, 0), cmd.Forward);
        Assert.Less(cmd.WantedRelVel.x, 0, "toward the hold point at 50 m");
    }

    [Test]
    public void LoadedRefusal_EachCase()
    {
        Assert.IsNull(DockingReads.LoadedRefusal(true, true, true, true));
        StringAssert.Contains("physics range", DockingReads.LoadedRefusal(true, false, true, true));
        Assert.IsNotNull(DockingReads.LoadedRefusal(false, true, true, true));
        Assert.IsNotNull(DockingReads.LoadedRefusal(true, true, false, true));
        Assert.IsNotNull(DockingReads.LoadedRefusal(true, true, true, false));
    }

    [Test]
    public void GroupByBody_OursFirstThenByName()
    {
        var list = new[]
        {
            new VesselEntry { Id = "1", Name = "Zeta", Body = "Kerbin" },
            new VesselEntry { Id = "2", Name = "alpha", Body = "Kerbin" },
            new VesselEntry { Id = "3", Name = "Probe", Body = "Duna" },
            new VesselEntry { Id = "4", Name = "Me", Body = "Kerbin", IsOurs = true },
            new VesselEntry { Id = "5", Name = "Junk", Body = "Kerbin", IsDebris = true },
            new VesselEntry { Id = "6", Name = "Lander", Body = "Mun" },
            new VesselEntry { Id = "", Name = "NoId", Body = "Mun" },
        };
        var groups = DockingReads.GroupByBody(list, "Mun");
        CollectionAssert.AreEqual(new[] { "Mun", "Duna", "Kerbin" }, groups.Select(g => g.body).ToArray());
        CollectionAssert.AreEqual(new[] { "6" }, groups[0].vessels.Select(v => v.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "2", "1" }, groups[2].vessels.Select(v => v.Id).ToArray());
        Assert.AreEqual(0, DockingReads.GroupByBody(null, "Kerbin").Count);
    }

    [Test]
    public void Rcs_UsableIsReadyOrFiring()
    {
        var s = RcsStatus.From(new[]
        {
            Data_RCS.ThrusterStatusState.Ready, Data_RCS.ThrusterStatusState.Operational,
            Data_RCS.ThrusterStatusState.Off, Data_RCS.ThrusterStatusState.Deprived, Data_RCS.ThrusterStatusState.Occluded,
        });
        Assert.AreEqual(5, s.Total);
        Assert.AreEqual(2, s.Usable);
        Assert.AreEqual(0, RcsStatus.From(null).Usable);
        Assert.AreEqual(0, RcsStatus.From(new[] { Data_RCS.ThrusterStatusState.Off }).Usable);
        StringAssert.Contains("no RCS", RcsStatus.From(null).ToString());
    }
}

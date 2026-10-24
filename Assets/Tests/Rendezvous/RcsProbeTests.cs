using System;
using K2D2.Rendezvous;
using KSP.Sim;
using NUnit.Framework;

// The probe pulses each RCS input (X, Y, Z) and measures the velocity change; FromResponses
// turns the three measured Δv into a mapping. Here the Δv are written by hand, in a vessel frame
// that is not aligned with the inertial axes.
public class RcsProbeTests
{
    const int Right = 0, Up = 1, Forward = 2;

    // A vessel frame turned 30° about z then 50° about its new x.
    static void Frame(out Vector3d right, out Vector3d up, out Vector3d forward)
    {
        double a = 30 * Math.PI / 180, b = 50 * Math.PI / 180;
        right = new Vector3d(Math.Cos(a), Math.Sin(a), 0);
        var y = new Vector3d(-Math.Sin(a), Math.Cos(a), 0);
        var z = new Vector3d(0, 0, 1);
        up = y * Math.Cos(b) + z * Math.Sin(b);
        forward = Vector3d.Cross(right, up);
    }

    [Test]
    public void Probe_PermutedAndFlippedAxes_Mapped()
    {
        Frame(out var right, out var up, out var fwd);
        // X moves the vessel down, Y forward, Z right; each with some cross-talk.
        var dvX = -0.30 * up + 0.02 * right;
        var dvY = 0.25 * fwd - 0.03 * up;
        var dvZ = 0.20 * right + 0.01 * fwd;
        var m = RcsMapping.FromResponses(dvX, dvY, dvZ, right, up, fwd, 0.02);
        Assert.IsTrue(m.Ok, m.Refusal);
        Assert.IsNull(m.Refusal);
        CollectionAssert.AreEqual(new[] { Up, Forward, Right }, m.AxisOf);
        CollectionAssert.AreEqual(new[] { -1, 1, 1 }, m.Sign);
        CollectionAssert.AreEqual(new[] { false, false, false }, m.Dead);
    }

    [Test]
    public void Probe_DeadAxis_Marked()
    {
        Frame(out var right, out var up, out var fwd);
        // Y barely moves the vessel (blocks on one side only, mostly cancelling).
        var dvX = 0.2 * right;
        var dvY = 0.004 * fwd;
        var dvZ = -0.2 * up;
        var m = RcsMapping.FromResponses(dvX, dvY, dvZ, right, up, fwd, 0.02);
        Assert.IsTrue(m.Ok, m.Refusal);
        CollectionAssert.AreEqual(new[] { false, true, false }, m.Dead);
        Assert.AreEqual(-1, m.AxisOf[1]);
        Assert.AreEqual(0, m.Sign[1]);
        Assert.AreEqual(Right, m.AxisOf[0]);
        Assert.AreEqual(Up, m.AxisOf[2]);
        Assert.AreEqual(-1, m.Sign[2]);

        // No response at all, or an unreadable one: refused.
        var none = RcsMapping.FromResponses(Vector3d.zero, 0.001 * up, new Vector3d(double.NaN, 0, 0), right, up, fwd, 0.02);
        Assert.IsFalse(none.Ok);
        StringAssert.Contains("no", none.Refusal);
        CollectionAssert.AreEqual(new[] { true, true, true }, none.Dead);
    }

    [Test]
    public void Probe_DeadAxis_JudgedAgainstStrongest()
    {
        Frame(out var right, out var up, out var fwd);
        // Y gives 0.015 m/s, well above the 0.001 noise floor but 7.5 % of the others: dead.
        var m = RcsMapping.FromResponses(0.2 * right, 0.015 * fwd, 0.2 * up, right, up, fwd, 0.001);
        Assert.IsTrue(m.Ok, m.Refusal);
        CollectionAssert.AreEqual(new[] { false, true, false }, m.Dead);
        // At 15 % of the others it is alive.
        var alive = RcsMapping.FromResponses(0.2 * right, 0.03 * fwd, 0.2 * up, right, up, fwd, 0.001);
        CollectionAssert.AreEqual(new[] { false, false, false }, alive.Dead);
        Assert.AreEqual(Forward, alive.AxisOf[1]);
    }

    [Test]
    public void Probe_NoDominantAxis_Refused()
    {
        Frame(out var right, out var up, out var fwd);
        // Z moves the vessel diagonally, 71 % along right and 71 % along up.
        var m = RcsMapping.FromResponses(0.2 * fwd, 0.2 * up, 0.2 * right + 0.2 * up, right, up, fwd, 0.02);
        Assert.IsFalse(m.Ok);
        StringAssert.Contains("Z", m.Refusal);
        StringAssert.Contains("no single axis", m.Refusal);
        // 85 % along one axis is enough.
        var ok = RcsMapping.FromResponses(0.2 * fwd, 0.2 * up, 0.2 * right + 0.124 * fwd, right, up, fwd, 0.02);
        Assert.IsTrue(ok.Ok, ok.Refusal);
    }

    [Test]
    public void Probe_TwoInputsSameAxis_Refused()
    {
        Frame(out var right, out var up, out var fwd);
        var dvX = 0.2 * right + 0.05 * up;
        var dvY = 0.2 * fwd;
        var dvZ = -0.15 * right + 0.06 * up;
        var m = RcsMapping.FromResponses(dvX, dvY, dvZ, right, up, fwd, 0.02);
        Assert.IsFalse(m.Ok);
        StringAssert.Contains("X", m.Refusal);
        StringAssert.Contains("Z", m.Refusal);
        StringAssert.Contains("right", m.Refusal);
    }
}

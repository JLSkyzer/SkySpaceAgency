using System;
using K2D2.Landing.Braking;
using KSP.Sim;
using NUnit.Framework;

public class BodyRotationTests
{
    const double R = 200000;

    [Test]
    public void AngularSpeed_IsTwoPiOverPeriod()
    {
        Assert.AreEqual(2 * Math.PI / 21549.425, BodyRotation.AngularSpeed(21549.425), 1e-15);
        Assert.AreEqual(2 * Math.PI / 21549.425, BodyRotation.AngularSpeed(-21549.425), 1e-15);
    }

    [Test]
    public void AngularSpeed_NoPeriod_IsZero()
    {
        Assert.AreEqual(0.0, BodyRotation.AngularSpeed(0));
        Assert.AreEqual(0.0, BodyRotation.AngularSpeed(double.NaN));
        Assert.AreEqual(0.0, BodyRotation.AngularSpeed(double.PositiveInfinity));
    }

    [Test]
    public void SurfaceVelocity_RemovesTheGroundSpeed()
    {
        var r = new Vector3d(R, 0, 0);
        var v = new Vector3d(0, 500, 0);
        var vs = BodyRotation.SurfaceVelocity(r, v, BodyRotation.AngularVelocity(1e-3, 1));
        // omega x r = (0, 0, 1e-3) x (R, 0, 0) = (0, 200, 0)
        Assert.AreEqual(300.0, vs.y, 1e-9);
        Assert.AreEqual(0.0, vs.x, 1e-9);
    }

    [TestCase(1)]
    [TestCase(-1)]
    public void ChooseSign_MatchesMeasuredSurfaceSpeed(int actualSign)
    {
        var r = new Vector3d(R + 10000, 0, 0);
        var v = new Vector3d(0, 550, 0);
        double omega = 1e-3;
        double measured = BodyRotation.SurfaceVelocity(r, v, BodyRotation.AngularVelocity(omega, actualSign)).magnitude;
        Assert.IsTrue(BodyRotation.IsDecisive(r, v, omega));
        Assert.AreEqual(actualSign, BodyRotation.ChooseSign(r, v, omega, measured));
    }

    [Test]
    public void ChooseSign_SlowRotation_KeepsPlusOne()
    {
        var r = new Vector3d(R + 10000, 0, 0);
        var v = new Vector3d(0, 550, 0);
        double omega = 1e-7; // ground speed ~0.02 m/s: both signs predict the same speed
        Assert.IsFalse(BodyRotation.IsDecisive(r, v, omega));
        Assert.AreEqual(1, BodyRotation.ChooseSign(r, v, omega, 400));
    }

    [Test]
    public void ChooseSign_PolarOrbit_KeepsPlusOne()
    {
        // Velocity along the rotation axis: omega x r is perpendicular to v, so both signs give
        // the same |v - omega x r|.
        var r = new Vector3d(R + 10000, 0, 0);
        var v = new Vector3d(0, 0, 550);
        Assert.IsFalse(BodyRotation.IsDecisive(r, v, 1e-3));
        Assert.AreEqual(1, BodyRotation.ChooseSign(r, v, 1e-3, 100));
    }
}

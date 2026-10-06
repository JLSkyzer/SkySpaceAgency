using System;
using K2D2.Landing.Braking;
using KSP.Sim;
using NUnit.Framework;

public class FinalDescentTests
{
    const double MunG = 1.63; // spec value

    static double AngleDeg(Vector3d a, Vector3d b)
    {
        double c = Vector3d.Dot(a, b) / (a.magnitude * b.magnitude);
        return Math.Acos(Math.Max(-1, Math.Min(1, c))) * 180 / Math.PI;
    }

    static void AssertVector(Vector3d expected, Vector3d actual, double tolerance)
    {
        Assert.AreEqual(expected.x, actual.x, tolerance, "x");
        Assert.AreEqual(expected.y, actual.y, tolerance, "y");
        Assert.AreEqual(expected.z, actual.z, tolerance, "z");
    }

    // Spec test 1, first case.
    [Test]
    public void NoHorizontalSpeed_AimsStraightUp()
    {
        var up = new Vector3d(0, 0, 1);
        AssertVector(up, FinalDescent.AimDirection(up, new Vector3d(0, 0, 0), MunG), 1e-12);
        // Falling straight down: only the horizontal part counts, so still straight up.
        AssertVector(up, FinalDescent.AimDirection(up, new Vector3d(0, 0, -5), MunG), 1e-12);
    }

    // Spec test 1, second case: atan(1 / 1.63) = 31.5°, capped at 15°, against the drift.
    [Test]
    public void OneMeterPerSecondOnTheMun_IsCappedAt15Degrees()
    {
        Assert.AreEqual(31.5, Math.Atan(1 / MunG) * 180 / Math.PI, 0.05); // uncapped value of the spec
        Assert.AreEqual(15.0, FinalDescent.TiltDegrees(1, MunG), 1e-12);

        var up = new Vector3d(0, 0, 1);
        var drift = new Vector3d(1, 0, 0);
        Vector3d aim = FinalDescent.AimDirection(up, drift, MunG);

        Assert.AreEqual(1.0, aim.magnitude, 1e-12);
        Assert.AreEqual(15.0, AngleDeg(aim, up), 1e-9);
        Assert.Less(Vector3d.Dot(aim, drift), 0.0); // leans against the drift
        Assert.AreEqual(0.0, aim.y, 1e-12);          // in the plane of up and the drift
    }

    // Spec test 1, third case: atan(0.1 / 1.63) = 3.5°, not capped.
    [Test]
    public void TenthOfMeterPerSecond_TiltsByAtan()
    {
        double expected = Math.Atan(0.1 / MunG) * 180 / Math.PI;
        Assert.AreEqual(3.5, expected, 0.05);
        Assert.AreEqual(expected, FinalDescent.TiltDegrees(0.1, MunG), 1e-12);

        var up = new Vector3d(0, 0, 1);
        // 0.1 m/s along +y while falling at 3 m/s.
        Vector3d aim = FinalDescent.AimDirection(up, new Vector3d(0, 0.1, -3), MunG);
        Assert.AreEqual(expected, AngleDeg(aim, up), 1e-9);
        Assert.Less(aim.y, 0.0);
        Assert.AreEqual(0.0, aim.x, 1e-12);
    }

    [Test]
    public void Aim_WorksInAnyFrame_AndUpNeedNotBeUnit()
    {
        var up = new Vector3d(3, 4, 0);          // |up| = 5, unit (0.6, 0.8, 0)
        var velocity = new Vector3d(-1.8, -2.4, 2); // 3 m/s down plus 2 m/s along +z
        Vector3d aim = FinalDescent.AimDirection(up, velocity, MunG);

        double c = Math.Cos(15 * Math.PI / 180), s = Math.Sin(15 * Math.PI / 180);
        AssertVector(new Vector3d(0.6 * c, 0.8 * c, -s), aim, 1e-12);
    }

    [Test]
    public void Tilt_BadInputs()
    {
        Assert.AreEqual(0.0, FinalDescent.TiltDegrees(double.NaN, MunG));
        Assert.AreEqual(0.0, FinalDescent.TiltDegrees(1, double.NaN));
        Assert.AreEqual(0.0, FinalDescent.TiltDegrees(-1, MunG));
        Assert.AreEqual(15.0, FinalDescent.TiltDegrees(1, 0), 1e-12);                       // no gravity: cap
        Assert.AreEqual(15.0, FinalDescent.TiltDegrees(double.PositiveInfinity, MunG), 1e-12);
    }

    // Spec test 2, thrust factor.
    [Test]
    public void ThrustFactor_IsCosineOfTheError()
    {
        Assert.AreEqual(1.0, FinalDescent.ThrustFactor(0), 1e-12);
        Assert.AreEqual(Math.Cos(Math.PI / 6), FinalDescent.ThrustFactor(30), 1e-12);
        Assert.AreEqual(0.0, FinalDescent.ThrustFactor(90), 1e-12);
    }

    [Test]
    public void ThrustFactor_IsZeroBeyond90Degrees()
    {
        Assert.AreEqual(0.0, FinalDescent.ThrustFactor(120));
        Assert.AreEqual(0.0, FinalDescent.ThrustFactor(180));
        Assert.AreEqual(0.0, FinalDescent.ThrustFactor(double.NaN));
    }

    // Spec test 2, contact speed floor: max(v_td, 0.5).
    [Test]
    public void ContactSpeed_NeverBelowHalfMeterPerSecond()
    {
        Assert.AreEqual(0.5, FinalDescent.ContactSpeed(0), 1e-12);
        Assert.AreEqual(0.5, FinalDescent.ContactSpeed(0.3), 1e-12);
        Assert.AreEqual(2.5, FinalDescent.ContactSpeed(2.5), 1e-12);
        Assert.AreEqual(0.5, FinalDescent.ContactSpeed(double.NaN), 1e-12);
    }

    [Test]
    public void IsFinal_Below50Metres()
    {
        Assert.IsTrue(FinalDescent.IsFinal(49.9));
        Assert.IsFalse(FinalDescent.IsFinal(50));
        Assert.IsFalse(FinalDescent.IsFinal(double.NaN));
    }
}

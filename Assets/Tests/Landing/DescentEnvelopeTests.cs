using System;
using K2D2.Landing.Braking;
using NUnit.Framework;

public class DescentEnvelopeTests
{
    [Test]
    public void MaxSpeed_IsStoppingSpeedPlusTouchDownSpeed()
    {
        // sqrt(2 * (0.85*5 - 1.6) * 1000) + 2 = sqrt(5300) + 2
        double expected = Math.Sqrt(5300) + 2;
        Assert.AreEqual(expected, DescentEnvelope.MaxSpeed(1000, 5, 1.6, 2, 1000), 1e-9);
    }

    [Test]
    public void MaxSpeed_NeverAbovePlayerProfile()
    {
        Assert.AreEqual(20.0, DescentEnvelope.MaxSpeed(1000, 5, 1.6, 2, 20), 1e-12);
    }

    [Test]
    public void MaxSpeed_WeakThrust_IsTouchDownSpeed()
    {
        // 0.85 * 1.5 < 1.6: the engines cannot decelerate at all.
        Assert.AreEqual(2.0, DescentEnvelope.MaxSpeed(1000, 1.5, 1.6, 2, 1000), 1e-12);
    }

    [Test]
    public void MaxSpeed_BadInputs_IsTouchDownSpeed()
    {
        Assert.AreEqual(2.0, DescentEnvelope.MaxSpeed(-50, 5, 1.6, 2, 1000), 1e-12);
        Assert.AreEqual(2.0, DescentEnvelope.MaxSpeed(1000, double.NaN, 1.6, 2, 1000), 1e-12);
    }

    [Test]
    public void CanStop_UsesFullThrust()
    {
        // full thrust: sqrt(2 * (5 - 1.6) * 1000) + 2 = 84.46
        Assert.IsTrue(DescentEnvelope.CanStop(50, 1000, 5, 1.6, 2));
        Assert.IsTrue(DescentEnvelope.CanStop(84, 1000, 5, 1.6, 2));
        Assert.IsFalse(DescentEnvelope.CanStop(85, 1000, 5, 1.6, 2));
    }

    [Test]
    public void CanStop_NoThrust_OnlyAtTouchDownSpeed()
    {
        Assert.IsTrue(DescentEnvelope.CanStop(1.5, 1000, 0, 1.6, 2));
        Assert.IsFalse(DescentEnvelope.CanStop(3, 1000, 0, 1.6, 2));
        Assert.IsFalse(DescentEnvelope.CanStop(3, 1000, double.NaN, 1.6, 2));
    }
}

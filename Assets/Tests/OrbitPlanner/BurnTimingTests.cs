using System;
using K2D2.OrbitPlanning;
using NUnit.Framework;

public class BurnTimingTests
{
    const double G0 = 9.80665;

    [Test]
    public void Duration_ConstantMass_IsDeltaVTimesMassOverThrust()
    {
        // 100 m/s with 50 kN on 10 t (5 m/s^2) takes 20 s.
        Assert.That(BurnTiming.Duration(100, 50, 10, 0), Is.EqualTo(20).Within(1e-9));
    }

    [Test]
    public void Duration_NegativeDeltaV_UsesMagnitude()
    {
        Assert.That(BurnTiming.Duration(-100, 50, 10, 0), Is.EqualTo(20).Within(1e-9));
    }

    [Test]
    public void Duration_WithIsp_MatchesTsiolkovskyClosedForm()
    {
        double expected = 10.0 * 300 * G0 / 50 * (1 - Math.Exp(-1000 / (300 * G0)));
        double actual = BurnTiming.Duration(1000, 50, 10, 300);
        Assert.That(actual, Is.EqualTo(expected).Within(1e-9));
        // Mass drops during the burn, so it is shorter than the constant-mass estimate (200 s).
        Assert.That(actual, Is.LessThan(BurnTiming.Duration(1000, 50, 10, 0)));
    }

    [Test]
    public void Duration_ZeroOrNegativeThrustOrMass_IsNaN()
    {
        Assert.That(double.IsNaN(BurnTiming.Duration(100, 0, 10, 300)));
        Assert.That(double.IsNaN(BurnTiming.Duration(100, 50, 0, 300)));
        Assert.That(double.IsNaN(BurnTiming.Duration(100, -1, 10, 0)));
        Assert.That(double.IsNaN(BurnTiming.Duration(100, 50, -1, 0)));
    }

    [Test]
    public void Duration_NaNInput_IsNaN()
    {
        Assert.That(double.IsNaN(BurnTiming.Duration(double.NaN, 50, 10, 300)));
        Assert.That(double.IsNaN(BurnTiming.Duration(100, double.NaN, 10, 300)));
        Assert.That(double.IsNaN(BurnTiming.Duration(100, 50, double.NaN, 300)));
        Assert.That(double.IsNaN(BurnTiming.Duration(100, 50, 10, double.NaN)));
    }

    [Test]
    public void CenteredStart_StartsHalfADurationBeforeTheImpulse()
    {
        Assert.That(BurnTiming.CenteredStart(1000, 200, 0), Is.EqualTo(900).Within(1e-9));
    }

    [Test]
    public void CenteredStart_ClampsToEarliest()
    {
        Assert.That(BurnTiming.CenteredStart(1000, 2000, 105), Is.EqualTo(105).Within(1e-9));
    }

    [Test]
    public void CenteredStart_NaNDuration_KeepsTheImpulseTime()
    {
        Assert.That(BurnTiming.CenteredStart(1000, double.NaN, 0), Is.EqualTo(1000).Within(1e-9));
    }
}

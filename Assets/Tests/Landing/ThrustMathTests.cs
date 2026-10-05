using K2D2.Landing.Braking;
using NUnit.Framework;

public class ThrustMathTests
{
    [Test]
    public void Acceleration_IsThrustOverMass()
    {
        Assert.AreEqual(5.0, ThrustMath.Acceleration(50, 10), 1e-12);
    }

    [Test]
    public void Acceleration_IsZeroInsteadOfNaNOrInfinity()
    {
        Assert.AreEqual(0.0, ThrustMath.Acceleration(50, 0));
        Assert.AreEqual(0.0, ThrustMath.Acceleration(0, 10));
        Assert.AreEqual(0.0, ThrustMath.Acceleration(double.NaN, 10));
        Assert.AreEqual(0.0, ThrustMath.Acceleration(50, double.NaN));
        Assert.AreEqual(0.0, ThrustMath.Acceleration(-5, 10));
    }

    [Test]
    public void CombinedIsp_SameEngines_IsTheirIsp()
    {
        Assert.AreEqual(300.0, ThrustMath.CombinedIsp(new[] { 100.0, 100.0 }, new[] { 300.0, 300.0 }), 1e-9);
    }

    [Test]
    public void CombinedIsp_IsThrustOverTotalFlow()
    {
        // 400 / (100/200 + 300/400) = 400 / 1.25 = 320
        Assert.AreEqual(320.0, ThrustMath.CombinedIsp(new[] { 100.0, 300.0 }, new[] { 200.0, 400.0 }), 1e-9);
    }

    [Test]
    public void CombinedIsp_IgnoresEnginesWithoutThrust()
    {
        Assert.AreEqual(300.0, ThrustMath.CombinedIsp(new[] { 100.0, 0.0 }, new[] { 300.0, 0.0 }), 1e-9);
    }

    [Test]
    public void CombinedIsp_UnknownIspOrNoEngine_IsZero()
    {
        Assert.AreEqual(0.0, ThrustMath.CombinedIsp(new[] { 100.0, 100.0 }, new[] { 300.0, 0.0 }));
        Assert.AreEqual(0.0, ThrustMath.CombinedIsp(new[] { 100.0 }, new[] { double.NaN }));
        Assert.AreEqual(0.0, ThrustMath.CombinedIsp(new double[0], new double[0]));
    }
}

using K2D2.Landing;
using NUnit.Framework;

public class TargetPointTests
{
    [Test]
    public void TryNormalize_KeepsAValidPoint()
    {
        Assert.IsTrue(TargetPoint.TryNormalize(10, 20, out double lat, out double lon));
        Assert.AreEqual(10, lat);
        Assert.AreEqual(20, lon);
    }

    [TestCase(-12.5, 190, -170)]
    [TestCase(0, -180, -180)]
    [TestCase(0, 180, -180)]
    [TestCase(0, 540, -180)]
    [TestCase(0, -190, 170)]
    public void TryNormalize_WrapsLongitude(double latitude, double longitude, double expected)
    {
        Assert.IsTrue(TargetPoint.TryNormalize(latitude, longitude, out double lat, out double lon));
        Assert.AreEqual(latitude, lat);
        Assert.AreEqual(expected, lon, 1e-9);
    }

    [Test]
    public void TryNormalize_RejectsBadValues()
    {
        Assert.IsFalse(TargetPoint.TryNormalize(91, 0, out _, out _));
        Assert.IsFalse(TargetPoint.TryNormalize(double.NaN, 0, out _, out _));
        Assert.IsFalse(TargetPoint.TryNormalize(0, double.PositiveInfinity, out _, out _));
    }

    [Test]
    public void Accept_NullWhenSameBody_MessageOtherwise()
    {
        Assert.IsNull(TargetPoint.Accept("Mun", "Mun"));
        Assert.AreEqual("Picked point is on Minmus, not Mun: ignored.", TargetPoint.Accept("Minmus", "Mun"));
    }
}

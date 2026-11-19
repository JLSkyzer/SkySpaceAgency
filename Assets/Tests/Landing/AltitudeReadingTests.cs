using K2D2.KSPService;
using NUnit.Framework;

public class AltitudeReadingTests
{
    [Test]
    public void GroundWithTerrainFallback_IsTheLowerReading()
    {
        Assert.AreEqual(120.0, AltitudeReading.GroundWithTerrainFallback(500, 120, 80), 1e-9);
        Assert.AreEqual(500.0, AltitudeReading.GroundWithTerrainFallback(500, 800, 80), 1e-9);
    }

    [Test]
    public void GroundEqualToSeaLevelLowDown_TakesTheTerrainHeight()
    {
        // On the pad: the ground height fell back to the sea-level one (70 m); the terrain says 2 m.
        Assert.AreEqual(2.0, AltitudeReading.GroundWithTerrainFallback(70, 70.2, 2), 1e-9);
    }

    [Test]
    public void TerrainFallback_OnlyBelow3Km()
    {
        Assert.AreEqual(3500.0, AltitudeReading.GroundWithTerrainFallback(3500, 3500, 10), 1e-9);
    }

    [Test]
    public void TerrainFallback_IgnoresUnbelievableTerrain()
    {
        Assert.AreEqual(70.0, AltitudeReading.GroundWithTerrainFallback(70, 70, double.NaN), 1e-9);
        Assert.AreEqual(70.0, AltitudeReading.GroundWithTerrainFallback(70, 70, -60), 1e-9);
        Assert.AreEqual(70.0, AltitudeReading.GroundWithTerrainFallback(70, 70, 90), 1e-9);
    }

    [Test]
    public void Grounded_IsZeroWhenLandedAndNeverNegative()
    {
        Assert.AreEqual(0.0, AltitudeReading.Grounded(true, 40, 5), 1e-9);
        Assert.AreEqual(0.0, AltitudeReading.Grounded(false, 3, 5), 1e-9);
        Assert.AreEqual(35.0, AltitudeReading.Grounded(false, 40, 5), 1e-9);
    }
}

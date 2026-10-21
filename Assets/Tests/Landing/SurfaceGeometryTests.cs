using System;
using K2D2.Landing.Precision;
using KSP.Sim;
using NUnit.Framework;

public class SurfaceGeometryTests
{
    const double R = 200000;

    static SurfaceGeometry.Reference Ref(double lat, double lon, int lonSign = 1)
    {
        // An arbitrary but consistent reference: the vessel at (lat, lon) with the same convention.
        double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
        return new SurfaceGeometry.Reference
        {
            Position = new Vector3d(R * Math.Cos(la) * Math.Cos(lo), R * Math.Cos(la) * Math.Sin(lo), R * Math.Sin(la)),
            Latitude = lat,
            Longitude = lon,
            LonSign = lonSign,
        };
    }

    static double LonDiff(double a, double b)
    {
        double d = (a - b) % 360;
        if (d >= 180) d -= 360;
        if (d < -180) d += 360;
        return d;
    }

    [TestCase(0, 0)]
    [TestCase(45, 120)]
    [TestCase(-30, -170)]
    [TestCase(89, 10)]
    public void RoundTripLatLon(double lat, double lon)
    {
        var reference = Ref(10, 20);
        Vector3d d = SurfaceGeometry.Direction(reference, lat, lon);
        Assert.AreEqual(1.0, d.magnitude, 1e-12);
        SurfaceGeometry.LatLon(reference, d, out double la, out double lo);
        Assert.AreEqual(lat, la, 1e-9);
        Assert.AreEqual(0, LonDiff(lon, lo), 1e-9);
    }

    [Test]
    public void ReferencePointMapsToItself()
    {
        var reference = Ref(-25, 70);
        Vector3d d = SurfaceGeometry.Direction(reference, -25, 70);
        Vector3d u = reference.Position / reference.Position.magnitude;
        Assert.AreEqual(0, (d - u).magnitude, 1e-12);
    }

    [Test]
    public void NegativeLonSignMirrorsLongitudes()
    {
        var plus = Ref(0, 0, 1);
        var minus = Ref(0, 0, -1);
        Vector3d a = SurfaceGeometry.Direction(plus, 0, 30);
        Vector3d b = SurfaceGeometry.Direction(minus, 0, 30);
        Assert.AreEqual(a.y, -b.y, 1e-12);
        Assert.AreEqual(a.x, b.x, 1e-12);
        SurfaceGeometry.LatLon(minus, b, out _, out double lo);
        Assert.AreEqual(30, lo, 1e-9);
    }

    [Test]
    public void AtTimeQuarterPeriodRotatesNinetyDegrees()
    {
        double period = 1000;
        var omega = new Vector3d(0, 0, 2 * Math.PI / period);
        Vector3d p = SurfaceGeometry.AtTime(new Vector3d(R, 0, 0), omega, period / 4);
        Assert.AreEqual(0, p.x, 1e-6);
        Assert.AreEqual(R, p.y, 1e-6);
        Assert.AreEqual(0, p.z, 1e-6);
    }

    [Test]
    public void GreatCircleOfAntipodesIsHalfCircumference()
    {
        double d = SurfaceGeometry.GreatCircle(new Vector3d(1, 0, 0), new Vector3d(-1, 0, 0), R);
        Assert.AreEqual(Math.PI * R, d, 1e-6);
    }

    [Test]
    public void GreatCircleAcrossAntiMeridianIsShort()
    {
        var reference = Ref(0, 0);
        Vector3d a = SurfaceGeometry.Direction(reference, 0, 179.9);
        Vector3d b = SurfaceGeometry.Direction(reference, 0, -179.9);
        double expected = 0.2 * Math.PI / 180 * R;
        Assert.AreEqual(expected, SurfaceGeometry.GreatCircle(a, b, R), 1e-3);
    }

    [Test]
    public void GreatCircleNearPoleAcrossAntiMeridian()
    {
        var reference = Ref(0, 0);
        Vector3d a = SurfaceGeometry.Direction(reference, 89, 179.9);
        Vector3d b = SurfaceGeometry.Direction(reference, 89, -179.9);
        double expected = 0.2 * Math.PI / 180 * R * Math.Cos(89 * Math.PI / 180);
        Assert.AreEqual(expected, SurfaceGeometry.GreatCircle(a, b, R), 1e-2);
    }

    static void SplitEquator(double dLat, double dLon, out double along, out double cross)
    {
        var reference = Ref(0, 0);
        Vector3d from = SurfaceGeometry.Direction(reference, 0, 0);
        Vector3d to = SurfaceGeometry.Direction(reference, dLat, dLon);
        var east = new Vector3d(0, 1, 0);
        SurfaceGeometry.Split(from, to, east, R, out along, out cross);
    }

    [Test]
    public void SplitEastIsAlongTrack()
    {
        double deg = 1000 / R * 180 / Math.PI;
        SplitEquator(0, deg, out double along, out double cross);
        Assert.AreEqual(1000, along, 1e-3);
        Assert.AreEqual(0, cross, 1e-3);
        SplitEquator(0, -deg, out along, out cross);
        Assert.AreEqual(-1000, along, 1e-3);
    }

    [Test]
    public void SplitNorthIsLeftOfEastwardTrack()
    {
        double deg = 1000 / R * 180 / Math.PI;
        SplitEquator(deg, 0, out double along, out double cross);
        Assert.AreEqual(0, along, 1e-3);
        Assert.AreEqual(1000, cross, 1e-3);
        SplitEquator(-deg, 0, out along, out cross);
        Assert.AreEqual(-1000, cross, 1e-3);
    }

    static SurfaceGeometry.Reference OffsetRef(int lonSign)
    {
        // Inertial azimuth 70 deg, game longitude 20 deg: they differ, as in the game.
        double az = 70 * Math.PI / 180;
        return new SurfaceGeometry.Reference
        {
            Position = new Vector3d(R * Math.Cos(az), R * Math.Sin(az), 0),
            Latitude = 0,
            Longitude = 20,
            LonSign = lonSign,
        };
    }

    [TestCase(1, 100.0)]
    [TestCase(-1, 40.0)]
    public void OffsetReferenceGivesKnownAzimuthAndRoundTrips(int lonSign, double expectedAzimuth)
    {
        var reference = OffsetRef(lonSign);
        Vector3d d = SurfaceGeometry.Direction(reference, 0, 50);
        Assert.AreEqual(expectedAzimuth, Math.Atan2(d.y, d.x) * 180 / Math.PI, 1e-9);
        SurfaceGeometry.LatLon(reference, d, out double la, out double lo);
        Assert.AreEqual(0, la, 1e-9);
        Assert.AreEqual(50, lo, 1e-9);
        d = SurfaceGeometry.Direction(reference, -30, -170);
        SurfaceGeometry.LatLon(reference, d, out la, out lo);
        Assert.AreEqual(-30, la, 1e-9);
        Assert.AreEqual(0, LonDiff(-170, lo), 1e-9);
    }

    [Test]
    public void SplitAtHighLatitudeEastwardTrack()
    {
        var reference = Ref(0, 0);
        Vector3d from = SurfaceGeometry.Direction(reference, 60, 0);
        double phi = Math.Atan2(from.y, from.x);
        double lat = 60 * Math.PI / 180;
        var east = new Vector3d(-Math.Sin(phi), Math.Cos(phi), 0);
        var north = new Vector3d(-Math.Sin(lat) * Math.Cos(phi), -Math.Sin(lat) * Math.Sin(phi), Math.Cos(lat));
        double a = 1000 / R;
        Vector3d toNorth = Math.Cos(a) * from + Math.Sin(a) * north;
        Vector3d toEast = Math.Cos(a) * from + Math.Sin(a) * east;
        SurfaceGeometry.Split(from, toNorth, east, R, out double along, out double cross);
        Assert.AreEqual(0, along, 1);
        Assert.AreEqual(1000, cross, 1);
        SurfaceGeometry.Split(from, toEast, east, R, out along, out cross);
        Assert.AreEqual(1000, along, 1);
        Assert.AreEqual(0, cross, 1);
    }
}

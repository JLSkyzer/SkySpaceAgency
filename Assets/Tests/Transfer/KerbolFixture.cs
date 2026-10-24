using System;
using System.Collections.Generic;
using K2D2.Transfer;
using KSP.Sim;

// A synthetic copy of the stock Kerbol system, for tests only. The game's bodies are read at run
// time (GameBodySystem); nothing outside the tests uses these numbers.
//
// EXTERNAL DATA: the orbital elements, gravitational parameters, radii, SOIs, atmosphere depths
// and highest terrain come from the KSP wiki (stock KSP values, from memory of the wiki pages,
// not read from Redux). The terrain heights are rounded. Angles are in degrees, the mean anomaly
// at epoch in radians, distances in m, mu in m³/s².
public static class KerbolFixture
{
    struct Body
    {
        public string Name, Parent;
        public double Mu, Radius, Soi, Atmosphere, Terrain;
        public double A, E, IncDeg, LanDeg, ArgPeDeg, MeanAnomaly;
    }

    static readonly Body[] Bodies =
    {
        new Body { Name = "Kerbol", Parent = null, Mu = 1.1723328e18, Radius = 261600000, Soi = double.PositiveInfinity, Atmosphere = 600000 },
        new Body { Name = "Moho", Parent = "Kerbol", Mu = 1.6860938e11, Radius = 250000, Soi = 9646663, Terrain = 6817,
            A = 5263138304, E = 0.2, IncDeg = 7, LanDeg = 70, ArgPeDeg = 15, MeanAnomaly = 3.14 },
        new Body { Name = "Eve", Parent = "Kerbol", Mu = 8.1717302e12, Radius = 700000, Soi = 85109365, Atmosphere = 90000, Terrain = 7541,
            A = 9832684544, E = 0.01, IncDeg = 2.1, LanDeg = 15, ArgPeDeg = 0, MeanAnomaly = 3.14 },
        new Body { Name = "Kerbin", Parent = "Kerbol", Mu = 3.5316e12, Radius = 600000, Soi = 84159286, Atmosphere = 70000, Terrain = 6764,
            A = 13599840256, E = 0, IncDeg = 0, LanDeg = 0, ArgPeDeg = 0, MeanAnomaly = 3.14 },
        new Body { Name = "Mun", Parent = "Kerbin", Mu = 6.5138398e10, Radius = 200000, Soi = 2429559, Terrain = 7061,
            A = 12000000, E = 0, IncDeg = 0, LanDeg = 0, ArgPeDeg = 0, MeanAnomaly = 1.7 },
        new Body { Name = "Minmus", Parent = "Kerbin", Mu = 1.7658e9, Radius = 60000, Soi = 2247428, Terrain = 5725,
            A = 47000000, E = 0, IncDeg = 6, LanDeg = 78, ArgPeDeg = 38, MeanAnomaly = 0.9 },
        new Body { Name = "Duna", Parent = "Kerbol", Mu = 3.0136321e11, Radius = 320000, Soi = 47921949, Atmosphere = 50000, Terrain = 8264,
            A = 20726155264, E = 0.051, IncDeg = 0.06, LanDeg = 135.5, ArgPeDeg = 0, MeanAnomaly = 3.14 },
        new Body { Name = "Ike", Parent = "Duna", Mu = 1.8568369e10, Radius = 130000, Soi = 1049599, Terrain = 12725,
            A = 3200000, E = 0.03, IncDeg = 0.2, LanDeg = 0, ArgPeDeg = 0, MeanAnomaly = 1.7 },
        new Body { Name = "Dres", Parent = "Kerbol", Mu = 2.1484489e10, Radius = 138000, Soi = 32832840, Terrain = 5670,
            A = 40839348203, E = 0.145, IncDeg = 5, LanDeg = 280, ArgPeDeg = 90, MeanAnomaly = 3.14 },
        new Body { Name = "Jool", Parent = "Kerbol", Mu = 2.82528e14, Radius = 6000000, Soi = 2455985200, Atmosphere = 200000,
            A = 68773560320, E = 0.05, IncDeg = 1.304, LanDeg = 52, ArgPeDeg = 0, MeanAnomaly = 0.1 },
        new Body { Name = "Laythe", Parent = "Jool", Mu = 1.962e12, Radius = 500000, Soi = 3723646, Atmosphere = 50000, Terrain = 6079,
            A = 27184000, E = 0, IncDeg = 0, LanDeg = 0, ArgPeDeg = 0, MeanAnomaly = 3.14 },
        new Body { Name = "Vall", Parent = "Jool", Mu = 2.074815e11, Radius = 300000, Soi = 2406401, Terrain = 7985,
            A = 43152000, E = 0, IncDeg = 0, LanDeg = 0, ArgPeDeg = 0, MeanAnomaly = 0.9 },
        new Body { Name = "Tylo", Parent = "Jool", Mu = 2.82528e12, Radius = 600000, Soi = 10856518, Terrain = 12904,
            A = 68500000, E = 0, IncDeg = 0.025, LanDeg = 0, ArgPeDeg = 0, MeanAnomaly = 3.14 },
    };

    // EXTERNAL DATA (KSP wiki, stock values from memory, not read from Redux): Kerbin's sidereal
    // rotation period, and the KSC launch pad's latitude and longitude. Kerbin rotates prograde,
    // about +z in this fixture's frame.
    public const double KerbinSiderealRotationSeconds = 21549.425;
    public const double KscLatitudeDeg = -0.0972;
    public const double KscLongitudeDeg = -74.5577;

    // Kerbin's angular velocity, in rad/s about +z (inverse = true flips it, for the sign tests).
    public static Vector3d KerbinAngularVelocity(bool inverse = false) =>
        new Vector3d(0, 0, (inverse ? -1 : 1) * 2 * Math.PI / KerbinSiderealRotationSeconds);

    // A body-relative point on the surface at (latDeg, lonDeg), longitude measured from +x about +z.
    public static Vector3d SurfacePoint(double radius, double latDeg, double lonDeg)
    {
        const double deg = Math.PI / 180;
        double c = Math.Cos(latDeg * deg);
        return radius * new Vector3d(c * Math.Cos(lonDeg * deg), c * Math.Sin(lonDeg * deg), Math.Sin(latDeg * deg));
    }

    public static BodySystem Build(double epochUT = 0)
    {
        var nodes = new List<BodyNode>();
        var index = new Dictionary<string, int>();
        foreach (var b in Bodies)
        {
            var node = new BodyNode
            {
                Name = b.Name,
                Index = nodes.Count,
                ParentIndex = b.Parent == null ? -1 : index[b.Parent],
                Mu = b.Mu,
                Radius = b.Radius,
                Soi = b.Soi,
                AtmosphereDepth = b.Atmosphere,
                MaxTerrainHeight = b.Terrain,
                EpochUT = epochUT,
            };
            if (b.Parent != null)
            {
                StateFromElements(b, nodes[node.ParentIndex].Mu, out var r, out var v);
                node.EpochPosition = r;
                node.EpochVelocity = v;
            }
            index[b.Name] = node.Index;
            nodes.Add(node);
        }
        return new BodySystem(nodes);
    }

    // Classical elements to a state vector in the parent's inertial Zup frame.
    static void StateFromElements(Body b, double mu, out Vector3d r, out Vector3d v)
    {
        const double deg = Math.PI / 180;
        double eAnomaly = b.MeanAnomaly;
        for (int i = 0; i < 50; i++)
            eAnomaly -= (eAnomaly - b.E * Math.Sin(eAnomaly) - b.MeanAnomaly) / (1 - b.E * Math.Cos(eAnomaly));

        double cosE = Math.Cos(eAnomaly), sinE = Math.Sin(eAnomaly);
        double rootOneMinusE2 = Math.Sqrt(1 - b.E * b.E);
        double rMag = b.A * (1 - b.E * cosE);
        double speedFactor = Math.Sqrt(mu * b.A) / rMag;
        var rPerifocal = new Vector3d(b.A * (cosE - b.E), b.A * rootOneMinusE2 * sinE, 0);
        var vPerifocal = new Vector3d(-speedFactor * sinE, speedFactor * rootOneMinusE2 * cosE, 0);

        r = Rotate(rPerifocal, b.LanDeg * deg, b.IncDeg * deg, b.ArgPeDeg * deg);
        v = Rotate(vPerifocal, b.LanDeg * deg, b.IncDeg * deg, b.ArgPeDeg * deg);
    }

    // Rz(lan) · Rx(inc) · Rz(argPe) applied to a perifocal vector.
    static Vector3d Rotate(Vector3d p, double lan, double inc, double argPe)
    {
        double x1 = p.x * Math.Cos(argPe) - p.y * Math.Sin(argPe);
        double y1 = p.x * Math.Sin(argPe) + p.y * Math.Cos(argPe);
        double z1 = p.z;

        double y2 = y1 * Math.Cos(inc) - z1 * Math.Sin(inc);
        double z2 = y1 * Math.Sin(inc) + z1 * Math.Cos(inc);

        double x3 = x1 * Math.Cos(lan) - y2 * Math.Sin(lan);
        double y3 = x1 * Math.Sin(lan) + y2 * Math.Cos(lan);
        return new Vector3d(x3, y3, z2);
    }
}

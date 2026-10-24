using System;
using System.Collections.Generic;
using System.Globalization;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Transfer
{
    // Game glue: the body hierarchy read from the game at one UT, as a fresh BodySystem. Never
    // mutate the nodes of a built system (PatchedPropagator caches its hierarchy per BodySystem):
    // build a new one instead.
    //
    // Facts from the IL of Ksp2Redux.dll (Redux 26w42c), see the Task 9 report:
    // - every GetRelativePositionAtUTZup / GetOrbitalVelocityAtUTZup of a PatchedConicsOrbit goes
    //   through PatchedConicsOrbit.CurrentZup, read from the UniverseRotationState singleton: all
    //   bodies and vessels share one Zup orientation, so the vectors can be summed up the
    //   hierarchy. The Task 9 review found that orientation constant for one universe load (it does
    //   not turn with time). It may still differ after a reload, so the system and the vessel are
    //   read together, from the same body list (Build, then VesselSnapshot.TryRead with that list),
    //   and both are read again at each re-plan.
    // - CelestialBodyComponent.MaxTerrainHeight is the height above the radius that the game itself
    //   uses for collisions (PatchedConics.WillCollideWithParent: Periapsis < radius + MaxTerrainHeight).
    public static class GameBodySystem
    {
        public const int TerrainSamples = 400;
        // Airless body whose terrain height is unknown (the game gives none and the samples found
        // nothing above the radius): assume this share of the radius, high enough for the stock
        // moons (Minmus and Ike are near 10 %, external KSP wiki figures), and warn.
        public const double UnknownTerrainShare = 0.10;

        static readonly HashSet<string> Logged = new HashSet<string>();

        // The sampled fallback, per body name, for the session: 400 terrain queries are not repeated
        // at every build.
        static readonly Dictionary<string, (double height, string source)> TerrainFallback =
            new Dictionary<string, (double, string)>();

        static void LogOnce(string line)
        {
            if (Logged.Add(line))
                K2D2_Plugin.logger.LogInfo(line);
        }

        // The game's bodies; a body's index in a built BodySystem is its index in this list.
        public static List<CelestialBodyComponent> GameBodies() =>
            GeneralTools.Game?.UniverseModel?.GetAllCelestialBodies();

        public static int IndexOf(IReadOnlyList<CelestialBodyComponent> bodies, CelestialBodyComponent body)
        {
            if (bodies == null || body == null)
                return -1;
            for (int i = 0; i < bodies.Count; i++)
                if (ReferenceEquals(bodies[i], body))
                    return i;
            return -1;
        }

        // Null, with the reason in error, when a body cannot be read. Refuses duplicate names.
        public static BodySystem Build(double ut, out string error) => Build(GameBodies(), ut, out error);

        // The same, from a body list read by the caller: pass that list to VesselSnapshot.TryRead
        // too, so the vessel's body index refers to this system.
        public static BodySystem Build(IReadOnlyList<CelestialBodyComponent> bodies, double ut, out string error)
        {
            error = null;
            try
            {
                if (bodies == null || bodies.Count == 0)
                {
                    error = "The game lists no celestial body.";
                    return null;
                }

                var nodes = new List<BodyNode>(bodies.Count);
                var names = new HashSet<string>();
                int roots = 0;
                for (int i = 0; i < bodies.Count; i++)
                {
                    CelestialBodyComponent b = bodies[i];
                    if (b == null)
                    {
                        error = $"The game's body {i} is missing.";
                        return null;
                    }
                    string name = b.bodyName;
                    if (string.IsNullOrEmpty(name))
                    {
                        error = $"The game's body {i} has no name.";
                        return null;
                    }
                    if (!names.Add(name))
                    {
                        error = $"Two bodies are named {name}: transfers cannot tell them apart.";
                        return null;
                    }

                    CelestialBodyComponent parent = b.referenceBody;
                    int parentIndex = parent == null || ReferenceEquals(parent, b) ? -1 : IndexOf(bodies, parent);
                    if (parentIndex < 0 && parent != null && !ReferenceEquals(parent, b))
                    {
                        error = $"The parent of {name} is not in the game's body list.";
                        return null;
                    }

                    var node = new BodyNode
                    {
                        Name = name,
                        Index = i,
                        ParentIndex = parentIndex,
                        Mu = b.gravParameter,
                        Radius = b.radius,
                        Soi = parentIndex < 0 ? double.PositiveInfinity : b.sphereOfInfluence,
                        AtmosphereDepth = b.hasAtmosphere ? b.atmosphereDepth : 0,
                        EpochUT = ut,
                    };
                    if (parentIndex < 0)
                        roots++;
                    else
                    {
                        if (double.IsInfinity(node.Soi))
                        {
                            error = $"The game gives {name} an infinite SOI, but {name} orbits {bodies[parentIndex].bodyName}: only a root body may have one.";
                            return null;
                        }
                        IKeplerPatch orbit = b.Orbit;
                        if (orbit == null)
                        {
                            error = $"The game gives no orbit for {name}.";
                            return null;
                        }
                        node.EpochPosition = orbit.GetRelativePositionAtUTZup(ut);
                        node.EpochVelocity = orbit.GetOrbitalVelocityAtUTZup(ut);
                    }
                    node.MaxTerrainHeight = MaxTerrainHeight(b, parentIndex < 0, out string source);

                    if (!Finite(node.Mu) || node.Mu <= 0 || !Finite(node.Radius) || node.Radius <= 0
                        || double.IsNaN(node.Soi) || node.Soi <= 0 || !Finite(node.AtmosphereDepth)
                        || !Finite(node.EpochPosition.magnitude) || !Finite(node.EpochVelocity.magnitude))
                    {
                        error = $"The game's values for {name} are not usable (mu {F(node.Mu)}, radius {F(node.Radius)}, " +
                                $"SOI {F(node.Soi)}, atmosphere {F(node.AtmosphereDepth)}).";
                        return null;
                    }

                    LogOnce($"[Transfer] body {name}: parent {(parentIndex < 0 ? "none" : bodies[parentIndex].bodyName)}, " +
                            $"mu {node.Mu.ToString("G6", CultureInfo.InvariantCulture)} m³/s², radius {F(node.Radius)} m, " +
                            $"SOI {F(node.Soi)} m, atmosphere {F(node.AtmosphereDepth)} m, " +
                            $"max terrain {F(node.MaxTerrainHeight)} m ({source}), low orbit {F(node.LowOrbitRadius - node.Radius)} m");
                    nodes.Add(node);
                }

                var system = new BodySystem(nodes);
                K2D2_Plugin.logger.LogInfo($"[Transfer] body system at UT {F(ut)}: {nodes.Count} bodies, {roots} root(s)");
                return system;
            }
            catch (Exception e)
            {
                error = $"Cannot read the game's bodies: {e.GetType().Name}: {e.Message}";
                K2D2_Plugin.logger.LogInfo("[Transfer] " + error);
                return null;
            }
        }

        // The game's own value when it gives one. Otherwise 0 under an atmosphere, and for an
        // airless body the highest point of a coarse grid sampled with GetAltitudeFromTerrain
        // (not verified in game far from the vessel: the terrain may not be loaded there).
        static double MaxTerrainHeight(CelestialBodyComponent b, bool root, out string source)
        {
            if (root || b.IsStar)
            {
                source = "star";
                return 0;
            }
            double h = b.MaxTerrainHeight;
            if (Finite(h) && h > 0)
            {
                source = "CelestialBodyComponent.MaxTerrainHeight";
                return h;
            }
            if (b.hasAtmosphere)
            {
                source = $"MaxTerrainHeight is {F(h)}, the atmosphere is the floor";
                return 0;
            }
            string key = b.bodyName ?? "";
            if (TerrainFallback.TryGetValue(key, out var cached))
            {
                source = cached.source + " (cached)";
                return cached.height;
            }
            double sampled = SampleTerrain(b, h, out source);
            if (!(sampled > 0))
            {
                double assumed = UnknownTerrainShare * b.radius;
                source += $"; nothing above the radius, so the terrain height is unknown: {F(assumed)} m assumed ({UnknownTerrainShare * 100:F0} % of the radius)";
                K2D2_Plugin.logger.LogWarning($"[Transfer] {key}: the terrain height is unknown (MaxTerrainHeight {F(h)}, samples found nothing above the radius); " +
                    $"the low orbit assumes {F(assumed)} m. Check the capture orbit by hand.");
                sampled = assumed;
            }
            TerrainFallback[key] = (sampled, source);
            return sampled;
        }

        // The highest point of a coarse grid, with GetAltitudeFromTerrain; 0 when sampling fails.
        static double SampleTerrain(CelestialBodyComponent b, double h, out string source)
        {
            try
            {
                double radius = b.radius;
                double best = double.NegativeInfinity;
                var frame = b.SimulationObject.transform.celestialFrame;
                foreach (Vector3d dir in TerrainGrid.Directions(TerrainSamples))
                {
                    Vector3d zup = dir * radius;
                    var ps = new Position(frame, new Vector3d(zup.x, zup.z, zup.y)); // Zup -> Yup
                    b.GetAltitudeFromTerrain(ps, out double aboveTerrain, out _);
                    double height = -aboveTerrain; // the point sits at the radius: height = |ps| - radius - aboveTerrain
                    if (Finite(height) && height > best)
                        best = height;
                }
                double result = Finite(best) ? Math.Max(0, best) : 0;
                source = $"MaxTerrainHeight is {F(h)}, sampled {TerrainSamples} points with GetAltitudeFromTerrain";
                return result;
            }
            catch (Exception e)
            {
                source = $"MaxTerrainHeight is {F(h)}, sampling failed ({e.GetType().Name}: {e.Message})";
                return 0;
            }
        }

        static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        static string F(double x) => x.ToString("F0", CultureInfo.InvariantCulture);
    }
}

using System;
using System.Globalization;
using KSP.Game;
using KSP.Sim;

namespace K2D2.Landing
{
    // Latitude/longitude of the KSC launch pad, read from the game (nothing is hard-coded).
    //
    // Source: CelestialBodyComponent.TryGetSurfaceObjectPosition(<spawn name>) on the home world, with the
    // spawn name given by OABProvider.LaunchLocationToSpawnName(Launchpad_1) - the same lookup the game's own
    // OABProvider.GetSurfaceLaunchPosition performs (SpaceSimulation.GetSurfaceObjectPosition ->
    // CelestialBodyComponent.TryGetSurfaceObjectPosition). The result goes through
    // CelestialBodyComponent.GetLatLonAltFromRadius(Position, ...), which returns degrees.
    //
    // Why not LaunchPadController: it is a scene MonoBehaviour whose LaunchPadLocation is only an enum
    // (Launchpad_1...), with no coordinates, and it exists only in the KSC scene.
    //
    // Availability (from the IL of CelestialBodyBehavior.TryGetSurfaceObjectPosition, 26w42c): the surface
    // objects are the PQSSurfaceObject children of the body's local-space view, so the lookup answers only
    // while the home world's local space is loaded (IsLocalSpaceLoaded). It does not need the KSC scene. When
    // the local space is not loaded, TryGet returns false and is retried on the next call; the pad never moves,
    // so a success is cached, for as long as UniverseModel.HomeWorld is the same object (another save loads a
    // new universe, whose home world may differ: the cache and the logged-once failure are then dropped).
    public static class KscLocation
    {
        static bool cached;
        static string cached_body;
        static double cached_lat, cached_lon;
        static bool failure_logged;
        // Home world the cache (and failure_logged) belongs to.
        static object cached_home;

        public static bool TryGet(out string bodyName, out double latitude, out double longitude)
        {
            object current_home = null;
            try { current_home = GameManager.Instance?.Game?.UniverseModel?.HomeWorld; }
            catch (Exception) { /* handled below by the full lookup */ }
            if (!ReferenceEquals(current_home, cached_home))
            {
                cached = false;
                failure_logged = false;
                cached_home = current_home;
            }

            if (cached)
            {
                bodyName = cached_body;
                latitude = cached_lat;
                longitude = cached_lon;
                return true;
            }

            bodyName = null;
            latitude = 0;
            longitude = 0;
            try
            {
                var game = GameManager.Instance?.Game;
                var home = game?.UniverseModel?.HomeWorld;
                if (home == null)
                    return Fail("the game has no home world yet");

                var spawn = game.OAB?.LaunchLocationToSpawnName(OABProvider.LaunchLocation.Launchpad_1);
                if (string.IsNullOrEmpty(spawn))
                    return Fail("the game gives no spawn name for Launchpad_1");

                if (!home.TryGetSurfaceObjectPosition(spawn, out Position position, out Rotation _))
                    return Fail($"surface object '{spawn}' not found on {home.Name} " +
                                "(the home world's local space is probably not loaded)");

                home.GetLatLonAltFromRadius(position, out double lat, out double lon, out double _);
                if (double.IsNaN(lat) || double.IsInfinity(lat) || double.IsNaN(lon) || double.IsInfinity(lon)
                    || lat < -90 || lat > 90)
                    return Fail($"unusable coordinates from '{spawn}' on {home.Name} ({lat}, {lon})");

                cached = true;
                cached_body = home.Name;
                cached_lat = lat;
                cached_lon = lon;
                K2D2_Plugin.logger.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "[Landing] target: KSC launch pad at ({0:0.0000}, {1:0.0000}) on {2} from " +
                    "CelestialBodyComponent.TryGetSurfaceObjectPosition('{3}')", lat, lon, cached_body, spawn));

                bodyName = cached_body;
                latitude = lat;
                longitude = lon;
                return true;
            }
            catch (Exception e)
            {
                return Fail("exception " + e.GetType().Name + ": " + e.Message);
            }
        }

        // Logs the first failure only; later calls retry silently (the local space may load later).
        static bool Fail(string reason)
        {
            if (!failure_logged)
            {
                failure_logged = true;
                K2D2_Plugin.logger.LogInfo("[Landing] target: KSC launch pad unavailable, entry omitted: " + reason);
            }
            return false;
        }
    }
}

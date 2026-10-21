using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace K2D2.Landing
{
    // Redux's own player-waypoint system (Redux.UI.Waypoints.PlayerWaypointManager and friends,
    // in Assembly-CSharp) is declared `internal`, so it's invisible to K2D2.dll through a normal
    // reference - same problem, and same fix, as VisualElementFactoryRegistry.RegisterFactory()
    // in KTools/K2UIFactoryRegistration.cs (see NOTICE.md's UI Toolkit section): reflection.
    // Confirmed against the real decompiled PlayerWaypointManager.cs/PlayerWaypointRecord.cs
    // rather than guessed at.
    //
    // Only reads the player's saved waypoints (PlayerWaypointManager.Instance.Records) for now.
    // Redux's own click-to-place flow (BeginPlacement()/PlacementCompleted) isn't hooked up here
    // yet - that would let players pick a landing site by clicking the map instead of typing
    // lat/lon or picking a saved waypoint, reusing Redux's own raycasting instead of us
    // reimplementing it, but it needs reflectively subscribing to an internal event
    // (Action<string, double, double>), which deserves its own pass and in-game test rather than
    // going in alongside everything else here.
    public struct ReduxWaypoint
    {
        public string name;
        public string body_name;
        public double latitude;
        public double longitude;
        // Not one of the player's own waypoints: a waypoint of the game, set by a mission (or a mod).
        public bool from_mission;
    }

    public static class ReduxWaypoints
    {
        // Looked up in the game's own assembly (Ksp2Redux since Redux 26w42, Assembly-CSharp before)
        // instead of by assembly name, so it does not depend on the Assembly-CSharp facade.
        const string manager_type_name = "Redux.UI.Waypoints.PlayerWaypointManager";

        static Type _manager_type;
        static bool _type_lookup_failed;

        internal static Type manager_type
        {
            get
            {
                if (_manager_type == null && !_type_lookup_failed)
                {
                    _manager_type = typeof(KSP.Sim.impl.WaypointComponent).Assembly.GetType(manager_type_name);
                    if (_manager_type == null)
                        _type_lookup_failed = true;
                }
                return _manager_type;
            }
        }

        // True once we've confirmed the type + Instance + Records are all reachable this run -
        // lets callers show "Waypoints unavailable" instead of just an empty list if Redux ever
        // renames/removes this internally between versions.
        public static bool available => GetManagerInstance() != null;

        internal static object GetManagerInstance()
        {
            var type = manager_type;
            if (type == null)
                return null;

            var instance_prop = type.GetProperty("Instance", BindingFlags.NonPublic | BindingFlags.Static);
            return instance_prop?.GetValue(null);
        }

        public static List<ReduxWaypoint> GetWaypointsForBody(string body_name)
        {
            var result = new List<ReduxWaypoint>();

            var manager_instance = GetManagerInstance();
            if (manager_instance == null)
                return result;

            var records_prop = manager_type.GetProperty("Records", BindingFlags.NonPublic | BindingFlags.Instance);
            if (records_prop?.GetValue(manager_instance) is not IEnumerable records)
                return result;

            foreach (var record in records)
            {
                var record_type = record.GetType();
                string this_body = record_type.GetField("BodyName")?.GetValue(record) as string;

                if (!string.IsNullOrEmpty(body_name) && this_body != body_name)
                    continue;

                result.Add(new ReduxWaypoint
                {
                    name = record_type.GetField("Name")?.GetValue(record) as string,
                    body_name = this_body,
                    latitude = (double)(record_type.GetField("Latitude")?.GetValue(record) ?? 0.0),
                    longitude = (double)(record_type.GetField("Longitude")?.GetValue(record) ?? 0.0),
                });
            }

            return result;
        }

        /// <summary>
        /// Every waypoint of the simulation (UniverseModel.GetAllWaypoints): the ones missions create
        /// for a precise destination, those of other mods, and the player's own ones as well (the
        /// caller drops those, see WaypointChoices). Destroyed waypoints are skipped.
        /// </summary>
        public static List<ReduxWaypoint> GetGameWaypoints()
        {
            // The Land tab rebuilds its list every UI tick: read the simulation once a second.
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_game_waypoints != null && now - _game_waypoints_time < 1f)
                return _game_waypoints;

            var result = new List<ReduxWaypoint>();
            _game_waypoints = result;
            _game_waypoints_time = now;
            var universe = KTools.GeneralTools.Game?.UniverseModel;
            if (universe == null)
                return result;

            foreach (var component in universe.GetAllWaypoints())
            {
                if (component == null || component.IsDestroyedOrBeingDestroyed)
                    continue;
                try
                {
                    var handle = new Redux.ApiImpls.ReduxWaypoint(component);
                    string name = string.IsNullOrEmpty(component.DisplayName) ? handle.Name : component.DisplayName;
                    result.Add(new ReduxWaypoint
                    {
                        name = name,
                        body_name = handle.BodyName,
                        latitude = handle.Latitude,
                        longitude = handle.Longitude,
                        from_mission = true,
                    });
                }
                catch (Exception e)
                {
                    LogOnce($"[Landing] could not read waypoint {component.Name}: {e.Message}");
                }
            }
            return result;
        }

        static List<ReduxWaypoint> _game_waypoints;
        static float _game_waypoints_time;

        static readonly HashSet<string> _logged = new HashSet<string>();

        internal static void LogOnce(string message)
        {
            if (_logged.Add(message))
                K2D2_Plugin.logger.LogWarning(message);
        }
    }
}

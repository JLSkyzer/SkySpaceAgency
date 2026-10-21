using System;
using System.Collections.Generic;
using System.Globalization;

namespace K2D2.Landing
{
    public struct WaypointChoice
    {
        public string Label;
        public ReduxWaypoint Waypoint;
    }

    /// <summary>
    /// Entries of the Land tab's waypoint dropdown for one body: the player's own waypoints
    /// (Redux's Waypoints window) first, then every other waypoint of the game on that body,
    /// marked [Mission]. Missions that need a precise point create such a waypoint when they
    /// activate (MissionDestinationPosition), so they show up here by themselves. A player
    /// waypoint also present among the game's waypoints is listed once. Labels are unique,
    /// since the dropdown is keyed by label. Pure logic, tested in the EditMode tests.
    /// </summary>
    public static class WaypointChoices
    {
        public const string MissionPrefix = "[Mission] ";
        // Two waypoints closer than this (degrees) with the same name are the same one.
        const double SameSpotDeg = 1e-3;

        public const string KscName = "KSC";
        public const string PickedName = "Picked point";
        // Entry showing the saved target when no listed entry sits on it.
        public const string CustomName = "Custom";
        // Two points closer than this (degrees, on latitude and on longitude) are the saved target.
        public const double SavedMatchDeg = 0.005;

        /// <summary>
        /// Index of the first entry within SavedMatchDeg of the saved target (latitude and longitude,
        /// longitude compared modulo 360), or -1.
        /// </summary>
        public static int IndexOfSaved(IReadOnlyList<WaypointChoice> choices, double lat, double lon)
        {
            for (int i = 0; i < choices.Count; i++)
            {
                var w = choices[i].Waypoint;
                double dlon = Math.Abs((((w.longitude - lon) % 360) + 540) % 360 - 180);
                if (Math.Abs(w.latitude - lat) <= SavedMatchDeg && dlon <= SavedMatchDeg)
                    return i;
            }
            return -1;
        }

        /// <summary>The "Custom (lat, lon)" entry for a saved target that matches no listed entry.</summary>
        public static WaypointChoice Custom(string bodyName, double lat, double lon)
        {
            var waypoint = new ReduxWaypoint { name = CustomName, body_name = bodyName, latitude = lat, longitude = lon };
            return new WaypointChoice { Label = Label(waypoint, ""), Waypoint = waypoint };
        }

        public static List<WaypointChoice> Build(IEnumerable<ReduxWaypoint> player, IEnumerable<ReduxWaypoint> game,
            string bodyName) => Build(player, game, bodyName, null, null);

        /// <summary>Same, with the launch pad (KSC) and the point picked on the map listed first, when on this body.</summary>
        public static List<WaypointChoice> Build(IEnumerable<ReduxWaypoint> player, IEnumerable<ReduxWaypoint> game,
            string bodyName, ReduxWaypoint? ksc, ReduxWaypoint? picked)
        {
            var result = new List<WaypointChoice>();
            var labels = new HashSet<string>();

            foreach (var special in new[] { ksc, picked })
            {
                if (special == null || special.Value.body_name != bodyName)
                    continue;
                var entry = special.Value;
                entry.from_mission = false;
                Add(result, labels, Label(entry, ""), entry);
            }

            var playerOnBody = new List<ReduxWaypoint>();

            foreach (var waypoint in player)
            {
                if (waypoint.body_name != bodyName)
                    continue;
                var entry = waypoint;
                entry.from_mission = false;
                playerOnBody.Add(entry);
                Add(result, labels, Label(entry, ""), entry);
            }

            foreach (var waypoint in game)
            {
                if (waypoint.body_name != bodyName || IsPlayerWaypoint(waypoint, playerOnBody))
                    continue;
                var entry = waypoint;
                entry.from_mission = true;
                if (string.IsNullOrEmpty(entry.name))
                    entry.name = "Waypoint";
                Add(result, labels, Label(entry, MissionPrefix), entry);
            }

            return result;
        }

        static bool IsPlayerWaypoint(ReduxWaypoint waypoint, List<ReduxWaypoint> playerOnBody)
        {
            foreach (var own in playerOnBody)
                if (own.name == waypoint.name
                    && Math.Abs(own.latitude - waypoint.latitude) < SameSpotDeg
                    && Math.Abs(own.longitude - waypoint.longitude) < SameSpotDeg)
                    return true;
            return false;
        }

        // Invariant culture: with a decimal comma (French Windows) "3,00, 4,00" would be ambiguous.
        static string Label(ReduxWaypoint waypoint, string prefix) =>
            prefix + waypoint.name + string.Format(CultureInfo.InvariantCulture, " ({0:0.00}, {1:0.00})",
                waypoint.latitude, waypoint.longitude);

        static void Add(List<WaypointChoice> result, HashSet<string> labels, string label, ReduxWaypoint waypoint)
        {
            string unique = label;
            for (int n = 2; labels.Contains(unique); n++)
                unique = $"{label} #{n}";
            labels.Add(unique);
            result.Add(new WaypointChoice { Label = unique, Waypoint = waypoint });
        }
    }
}

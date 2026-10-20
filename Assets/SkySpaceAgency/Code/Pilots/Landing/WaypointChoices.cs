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

        public static List<WaypointChoice> Build(IEnumerable<ReduxWaypoint> player, IEnumerable<ReduxWaypoint> game,
            string bodyName)
        {
            var result = new List<WaypointChoice>();
            var labels = new HashSet<string>();
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

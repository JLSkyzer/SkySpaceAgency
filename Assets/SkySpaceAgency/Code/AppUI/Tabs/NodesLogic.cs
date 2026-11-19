using System;
using System.Collections.Generic;
using System.Globalization;
using SkySpaceAgency.Node;
using UnityEngine;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The NODES tab's pure logic (tested in EditMode): node labels and times, the phase of the node
    /// executor on the timeline, the ignition time of each start mode, and the burn view's geometry.
    /// </summary>
    public static class NodesLogic
    {
        // The node executor's sequence (NodeExPilot: Turn, Warp, Burn; the burn waits for its
        // ignition time first). Done is the end marker: the pilot stops when the burn is done.
        public static readonly string[] NodeSteps = { "Align", "Warp to node", "Ignite", "Burn", "Done" };

        // A closed-loop run (Circularize at AP/PE, a node of an Orbit tab plan): TargetOrbitBurn's
        // Warp, Align, Burn. Done also marks an Orbit plan creating its next node (NextStep).
        public static readonly string[] ClosedLoopSteps = { "Warp", "Align", "Burn", "Done" };

        /// <summary>The timeline's steps and current index for the pilot's mode.</summary>
        public static int Phase(NodeExPilot.Mode mode, bool beforeIgnition, TargetOrbitBurn.Mode closedLoop, out string[] steps)
        {
            switch (mode)
            {
                case NodeExPilot.Mode.Turn: steps = NodeSteps; return 0;
                case NodeExPilot.Mode.Warp: steps = NodeSteps; return 1;
                case NodeExPilot.Mode.Burn: steps = NodeSteps; return beforeIgnition ? 2 : 3;
                case NodeExPilot.Mode.Circularize:
                case NodeExPilot.Mode.TargetOrbit:
                    steps = ClosedLoopSteps;
                    return closedLoop == TargetOrbitBurn.Mode.Warp ? 0 : closedLoop == TargetOrbitBurn.Mode.Align ? 1 : 2;
                case NodeExPilot.Mode.NextStep: steps = ClosedLoopSteps; return 3;
                default: steps = NodeSteps; return 0;
            }
        }

        /// <summary>When the burn of a node starts, by the start mode (as NodeExPilot.Update sets it).</summary>
        public static double IgnitionUT(NodeExSettings.StartMode mode, double nodeTime, double burnDuration, double startBefore)
        {
            switch (mode)
            {
                case NodeExSettings.StartMode.half_duration: return nodeTime - burnDuration / 2;
                case NodeExSettings.StartMode.constant: return nodeTime - startBefore;
                default: return nodeTime;
            }
        }

        /// <summary>
        /// The main direction of a node's burn vector, in the game's maneuver frame (x radial out,
        /// y normal, z prograde: ManeuverCreator's Prograde/Normal/RadialOutBurnVector).
        /// </summary>
        public static string Direction(double radial, double normal, double prograde)
        {
            double ar = Math.Abs(radial), an = Math.Abs(normal), ap = Math.Abs(prograde);
            if (ar < 1e-6 && an < 1e-6 && ap < 1e-6) return "empty";
            if (ap >= an && ap >= ar) return prograde > 0 ? "prograde" : "retrograde";
            if (an >= ar) return normal > 0 ? "normal" : "anti-normal";
            return radial > 0 ? "radial out" : "radial in";
        }

        /// <summary>A clock: "0:41", "4:12", "1:02:03"; negative with a minus sign; NaN as a dash.</summary>
        public static string Clock(double secs)
        {
            if (double.IsNaN(secs) || double.IsInfinity(secs)) return "—";
            if (secs < 0) return "-" + Clock(-secs);
            long s = (long)Math.Floor(secs);
            long h = s / 3600, m = s / 60 % 60, sec = s % 60;
            return h > 0
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", h, m, sec)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", m, sec);
        }

        /// <summary>"in 4:12", or "4:12 ago" for a node in the past.</summary>
        public static string When(double secondsToNode) =>
            secondsToNode < 0 ? Clock(-secondsToNode) + " ago" : "in " + Clock(secondsToNode);

        /// <summary>A speed in m/s without decimals ("412"), with one under 10 ("3.4").</summary>
        public static string Speed(double ms)
        {
            if (double.IsNaN(ms)) return "—";
            return Math.Abs(ms) < 10 ? ms.ToString("0.0", CultureInfo.InvariantCulture) : ms.ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>The part of a node's Δv already applied, from 0 to 1 (0 without a node).</summary>
        public static float Applied(double required, double left)
        {
            if (!(required > 0) || double.IsNaN(left)) return 0;
            return Mathf.Clamp01((float)((required - left) / required));
        }

        /// <summary>A burn time scaled by the Δv left (the game gives the node's whole burn).</summary>
        public static double BurnTimeLeft(double burnDuration, double required, double left)
        {
            if (!(required > 0) || double.IsNaN(left)) return burnDuration;
            return burnDuration * Math.Max(0, Math.Min(1, left / required));
        }

        // ---- burn view (a navball seen from the front: its centre is the vessel's nose) ----

        /// <summary>
        /// Where the burn vector sits on the ball, from its angle to the nose and its direction on
        /// screen: orthographic projection of the front half (radius × sin angle), on the rim beyond 90°.
        /// </summary>
        public static Vector2 MarkerOffset(float angleDeg, Vector2 direction, float radius)
        {
            if (float.IsNaN(angleDeg) || direction.sqrMagnitude < 1e-12f) return Vector2.zero;
            float a = Mathf.Clamp(angleDeg, 0, 180);
            float r = a <= 90 ? radius * Mathf.Sin(a * Mathf.Deg2Rad) : radius;
            return direction.normalized * r;
        }

        /// <summary>The burn vector is behind the vessel (more than 90° from the nose).</summary>
        public static bool MarkerBehind(float angleDeg) => angleDeg > 90;

        /// <summary>The horizon's offset below the ball's centre (screen y down) for a pitch: nose up, horizon down.</summary>
        public static float HorizonOffset(float pitchDeg, float radius) =>
            float.IsNaN(pitchDeg) ? 0 : radius * Mathf.Sin(Mathf.Clamp(pitchDeg, -90, 90) * Mathf.Deg2Rad);

        /// <summary>
        /// The ground's arc on the ball (degrees, screen angles: 0 right, 90 down), from start to end
        /// through the bottom; null when no ground shows, (0, 360) when the whole ball is ground.
        /// </summary>
        public static Vector2? GroundArc(float horizonOffset, float radius)
        {
            if (radius <= 0 || horizonOffset >= radius) return null;
            if (horizonOffset <= -radius) return new Vector2(0, 360);
            float a = Mathf.Asin(horizonOffset / radius) * Mathf.Rad2Deg;
            return new Vector2(a, 180 - a);
        }

        /// <summary>The note under the ball.</summary>
        public static string AlignNote(bool hasBurn, float angleDeg, float maxAngle, out StatusKind kind)
        {
            if (!hasBurn || float.IsNaN(angleDeg))
            {
                kind = StatusKind.Neutral;
                return "no burn to point at";
            }
            string a = angleDeg.ToString(angleDeg < 10 ? "0.0" : "0", CultureInfo.InvariantCulture);
            if (angleDeg <= maxAngle)
            {
                kind = StatusKind.Ok;
                return $"aligned to {a}°";
            }
            kind = StatusKind.Info;
            return $"{a}° from the burn";
        }

        /// <summary>The keys of the settings the tab binds (the classic Node and Attitude tabs' settings).</summary>
        public static readonly IReadOnlyList<string> SettingKeys = new[]
        {
            "node_ex.auto_warp", "node_ex.pause_on_end", "node_ex.start_mode", "node_ex.start_before",
            "node_ex.turn.max_angle_maneuver", "node_ex.turn.max_angular_speed",
            "node_ex.warp.speed", "node_ex.warp.safe_duration", "burn.warp_mode",
            "node_ex.burn.burn_adjust", "node_ex.burn.max_dv_error", "node_ex.burn.rotate_during_burn",
            "attitude.elevation", "attitude.heading",
        };
    }
}

using System;
using System.Collections.Generic;
using SkySpaceAgency.Lift;
using UnityEngine;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The pure part of the Lift tab's ascent profile: the planned curve the Guided profile flies (the
    /// classic graph's, LiftAscentPath.updatePath, from the same settings), the plot's ranges and the
    /// downrange of a flown sample. All in km: x is the downrange, y the sea-level altitude.
    /// </summary>
    public static class LiftProfileModel
    {
        /// <summary>The classic LiftAscentPath.compute_elevation, from altitudes in km (degrees above the horizon).</summary>
        public static float Elevation(float altKm, float startKm, float midKm, float endKm)
        {
            if (altKm < startKm)
                return 90;
            if (altKm < midKm)
                return Mathf.Lerp(90, 45, Mathf.InverseLerp(startKm, midKm, altKm));
            return Mathf.Lerp(45, 1, Mathf.InverseLerp(midKm, endKm, altKm));
        }

        /// <summary>
        /// The Guided curve, as the classic graph draws it: steps of target/100 km along the pitch the
        /// profile asks for, from (0, 0) until the target altitude or the right edge of the plot.
        /// </summary>
        public static List<Vector2> PlannedPath(float startKm, float midKm, float endKm, float targetKm, float maxDownrangeKm)
        {
            var points = new List<Vector2> { Vector2.zero };
            if (targetKm <= 0 || maxDownrangeKm <= 0)
                return points;
            float step = targetKm / 100f, alt = 0, x = 0;
            for (int i = 0; i < 4000 && alt < targetKm && x < maxDownrangeKm; i++)
            {
                float e = Elevation(alt, startKm, midKm, endKm) * Mathf.Deg2Rad;
                alt += step * Mathf.Sin(e);
                // cos(90°) rounds to -4e-8 in float: never step backwards.
                x += step * Mathf.Max(0f, Mathf.Cos(e));
                points.Add(new Vector2(x, alt));
            }
            return points;
        }

        /// <summary>
        /// The plotted ranges: y up to 12 % over the highest of the target, the flown path and the
        /// vessel; x at least the classic graph's (y range times the view's aspect), and the whole
        /// flown path.
        /// </summary>
        public static Vector2 Range(float targetKm, IReadOnlyList<Vector2> flown, Vector2? vessel, float aspect)
        {
            float top = Mathf.Max(1, targetKm), right = 0;
            if (flown != null)
                foreach (var p in flown)
                {
                    top = Mathf.Max(top, p.y);
                    right = Mathf.Max(right, p.x);
                }
            if (vessel.HasValue)
            {
                top = Mathf.Max(top, vessel.Value.y);
                right = Mathf.Max(right, vessel.Value.x);
            }
            float yMax = top * 1.12f;
            float xMax = Mathf.Max(yMax * Mathf.Clamp(aspect, 0.5f, 4f), right * 1.05f);
            return new Vector2(xMax, yMax);
        }

        /// <summary>The great-circle distance between two points of a body, in km (latitudes and longitudes in degrees).</summary>
        public static float DownrangeKm(double lat0, double lon0, double lat1, double lon1, double radiusM)
        {
            double d2r = Math.PI / 180;
            double p0 = lat0 * d2r, p1 = lat1 * d2r, dp = p1 - p0, dl = (lon1 - lon0) * d2r;
            double a = Math.Sin(dp / 2) * Math.Sin(dp / 2) + Math.Cos(p0) * Math.Cos(p1) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0, 1 - a)));
            return (float)(c * radiusM / 1000);
        }

        /// <summary>
        /// Tick values for the altitude axis, from the top: the target, the atmosphere and its half when
        /// it is under the target (the mockup's 100 / 70 / 35 / 0), else two thirds and a third.
        /// </summary>
        public static int[] Ticks(float targetKm, float atmosphereKm = -1)
        {
            int t = Mathf.Max(1, Mathf.RoundToInt(targetKm));
            int a = Mathf.RoundToInt(atmosphereKm);
            if (a > 1 && a < t * 0.95f)
                return new[] { t, a, Mathf.RoundToInt(a / 2f), 0 };
            return new[] { t, Mathf.RoundToInt(t * 2 / 3f), Mathf.RoundToInt(t / 3f), 0 };
        }
    }

    /// <summary>
    /// The Lift pilot's phases as the timeline shows them: LiftPilot.LiftStatus in flight order, the
    /// Ascent split into its vertical climb and its turn, the steps the goal and the settings skip left
    /// out, and the end state of the run.
    /// </summary>
    public static class LiftPhases
    {
        public const string LaunchWindow = "Launch window", VerticalClimb = "Vertical climb", GravityTurn = "Gravity turn",
            Coast = "Coast to Ap", FineTuning = "Fine tuning", Circularize = "Circularize", InOrbit = "In orbit",
            ApSet = "Ap set", PlaneTrim = "Plane trim", Handoff = "Hand-over";

        public static List<string> Steps(LiftGoal goal, bool adjust, bool autoCircularize)
        {
            var steps = new List<string>();
            if (goal != LiftGoal.Orbit) steps.Add(LaunchWindow);
            steps.Add(VerticalClimb);
            steps.Add(GravityTurn);
            steps.Add(Coast);
            if (adjust) steps.Add(FineTuning);
            bool circularize = LiftGoalRules.CircularizationForced(goal) || autoCircularize;
            if (circularize) steps.Add(Circularize);
            if (goal == LiftGoal.PrepareTransfer) steps.Add(PlaneTrim);
            else if (goal == LiftGoal.Rendezvous) steps.Add(Handoff);
            else steps.Add(circularize ? InOrbit : ApSet);
            return steps;
        }

        /// <summary>
        /// The lit step. Off: the last one after a run that ended well, the first otherwise. A step the
        /// list does not hold (Adjust with fine tuning off) lights the one before it.
        /// </summary>
        public static int Index(IReadOnlyList<string> steps, LiftPilot.LiftStatus status, bool verticalClimb, bool endedOk)
        {
            if (steps == null || steps.Count == 0) return 0;
            string name;
            switch (status)
            {
                case LiftPilot.LiftStatus.WaitLaunch: name = LaunchWindow; break;
                case LiftPilot.LiftStatus.Ascent: name = verticalClimb ? VerticalClimb : GravityTurn; break;
                case LiftPilot.LiftStatus.Coasting: name = Coast; break;
                case LiftPilot.LiftStatus.Adjust: name = FineTuning; break;
                case LiftPilot.LiftStatus.Circularize: name = Circularize; break;
                case LiftPilot.LiftStatus.PlaneTrim: name = PlaneTrim; break;
                case LiftPilot.LiftStatus.Handoff: name = Handoff; break;
                default: return endedOk ? steps.Count - 1 : 0;
            }
            int i = IndexOf(steps, name);
            if (i < 0 && status == LiftPilot.LiftStatus.Adjust) i = IndexOf(steps, Coast);
            return Mathf.Max(0, i);
        }

        static int IndexOf(IReadOnlyList<string> steps, string name)
        {
            for (int i = 0; i < steps.Count; i++)
                if (steps[i] == name) return i;
            return -1;
        }

        /// <summary>The Guided ascent climbs straight up below its 90° altitude; Gravity Turn says so in its Phase row.</summary>
        public static bool IsVerticalClimb(AscentProfile profile, float altitudeKm, int startAltitudeKm, string gravityTurnPhase) =>
            profile == AscentProfile.GravityTurn
                ? gravityTurnPhase == null || gravityTurnPhase == "VerticalRise"
                : altitudeKm < startAltitudeKm;
    }
}

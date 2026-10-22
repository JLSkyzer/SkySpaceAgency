using System;
using System.Collections.Generic;
using System.Diagnostics;
using K2D2.KSPService;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using KSP.Modules;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Landing
{
    // The parachute precision return after the jettison (LandingPilotReentry.cs has the rest):
    // the descent readouts, the interface, heating and chute opening logs against the reference
    // prediction, the final distance and the suggested drag scale. Every line logged here starts
    // with "[Landing] reentry:".
    public partial class LandingPilot
    {
        void UpdateReentryDescent()
        {
            var vessel = current_vessel.VesselComponent;
            double sea = vessel.AltitudeFromSeaLevel;
            double now = GeneralTools.Current_UT;
            if (!reentry_interface_seen && sea <= reentry_depth)
            {
                reentry_interface_seen = true;
                bool hadReference = reentry_reference;
                if (ReentryState(out Vector3d r, out Vector3d v, out double ut, out Precision.Body body, out Vector3d target))
                {
                    logger.LogInfo($"[Landing] reentry: interface at T{ut:F1} (predicted T{F(reentry_interface_UT, "F1")}, {F(ut - reentry_interface_UT, "n1")} s late), " +
                        $"speed {current_vessel.VesselVehicle.SurfaceSpeed:n0} m/s surface, {v.magnitude:n0} m/s orbital");
                    if (!hadReference)
                    {
                        StopPrediction stop = ReentryPredictorFor(body, jettisoned: true).Predict(r, v, ut, null, out ReentryTrace trace);
                        SetReference("interface", r, v, ut, body, target, stop, trace);
                    }
                }
            }

            // Predicted distance above the interface.
            float now_real = UnityEngine.Time.realtimeSinceStartup;
            if (sea > reentry_depth && now_real >= next_precision_readout)
            {
                next_precision_readout = now_real + (float)ReadoutIntervalReentry;
                if (PredictNow(out _, out _, out _, out Precision.Body b, out Vector3d target, out StopPrediction stop, out _))
                    precision_distance_m = stop.Ok ? SurfaceGeometry.GreatCircle(stop.StopDirNow, target, b.Radius) : double.NaN;
                precision_distance_label = "Predicted Distance";
            }
            else if (sea <= reentry_depth)
                precision_distance_label = "Predicted Distance (at the interface)";

            if (sea <= reentry_depth && !chute_full_logged)
                UpdateHeating(vessel, sea, now_real);
            UpdateChuteOpening(vessel, now);
        }

        // Peak deceleration and the hottest part (temperature / max temperature) in the air, logged
        // every HeatLogInterval s until the chutes are fully open: the 0.35 descent periapsis was
        // chosen without a heating model.
        void UpdateHeating(VesselComponent vessel, double sea, float now_real)
        {
            double g = vessel.geeForce;
            if (g > reentry_peak_g)
            {
                reentry_peak_g = g;
                reentry_peak_g_altitude = sea;
            }
            double hottest = 0;
            string hottest_part = null;
            try
            {
                foreach (PartComponent part in vessel.SimulationObject.PartOwner.Parts)
                {
                    if (part == null || !(part.MaxTemp > 0))
                        continue;
                    double ratio = part.Temperature / part.MaxTemp;
                    if (ratio > hottest)
                    {
                        hottest = ratio;
                        hottest_part = part.PartName;
                    }
                }
            }
            catch (Exception) { /* temperatures not readable: logged as 0 */ }
            if (hottest > reentry_max_temp_ratio)
            {
                reentry_max_temp_ratio = hottest;
                reentry_max_temp_part = hottest_part;
            }
            if (now_real < next_heat_log)
                return;
            next_heat_log = now_real + (float)HeatLogInterval;
            logger.LogInfo($"[Landing] reentry: heating: altitude {sea:n0} m, speed {current_vessel.VesselVehicle.SurfaceSpeed:n0} m/s, Mach {vessel.MachNumber:n2}, " +
                $"{g:n2} g (peak {reentry_peak_g:n2} g at {F(reentry_peak_g_altitude)} m), hottest part {hottest_part ?? "?"} at {hottest * 100:n0} % of its max temperature " +
                $"(peak {reentry_max_temp_ratio * 100:n0} %, {reentry_max_temp_part ?? "?"}) | predicted peak drag {ref_trace.PeakDecel / BrakeSimulator.G0:n2} g");
        }

        // The game opening the chutes (semi, then full), against the prediction.
        void UpdateChuteOpening(VesselComponent vessel, double now)
        {
            if (chute_full_logged)
                return;
            ParachuteCounts counts = Parachutes.Count(vessel);
            bool semi = counts.SemiDeployed + counts.Deployed > 0;
            bool full = counts.Deployed > 0;
            if (semi && !chute_semi_logged)
            {
                chute_semi_logged = true;
                LogChuteOpening("open (semi-deployed)", vessel, now, ref_trace.SemiUT, ref_trace.SemiAltitude, ref_trace.SemiDirNow);
                logger.LogInfo($"[Landing] reentry: heating summary at the chute opening: peak {reentry_peak_g:n2} g at {F(reentry_peak_g_altitude)} m " +
                    $"(predicted {ref_trace.PeakDecel / BrakeSimulator.G0:n2} g), hottest part {reentry_max_temp_part ?? "?"} at {reentry_max_temp_ratio * 100:n0} % of its max temperature");
            }
            if (full && !chute_full_logged)
            {
                chute_full_logged = true;
                LogChuteOpening("fully deployed", vessel, now, ref_trace.FullUT, ref_trace.FullAltitude, new Vector3d(0, 0, 0));
            }
        }

        void LogChuteOpening(string what, VesselComponent vessel, double now, double predictedUT, double predictedAltitude, Vector3d predictedDirAtRef)
        {
            var states = new List<string>();
            try
            {
                foreach (PartComponent part in vessel.SimulationObject.PartOwner.Parts)
                {
                    if (part != null && part.TryGetModuleData<PartComponentModule_Parachute, Data_Parachute>(out Data_Parachute chute) && chute != null)
                        states.Add($"{part.PartName} {chute.deployState.GetValue()} safety {chute.deploymentSafetyState.GetValue()}");
                }
            }
            catch (Exception e)
            {
                states.Add($"not readable ({e.GetType().Name})");
            }
            string position = "";
            if (reentry_reference && ReentryState(out Vector3d r, out _, out _, out Precision.Body body, out _))
            {
                Vector3d here = SurfaceGeometry.AtTime(r, ref_body.Omega, -(now - ref_ut));
                position = predictedDirAtRef.sqrMagnitude > 0
                    ? $", {SurfaceGeometry.GreatCircle(here, predictedDirAtRef, ref_body.Radius):n0} m from the predicted opening point"
                    : "";
                position += $", {SurfaceGeometry.GreatCircle(here, ref_target, ref_body.Radius):n0} m from the target";
            }
            logger.LogInfo($"[Landing] reentry: chutes {what} at T{now:F1}, {vessel.AltitudeFromSurface:n0} m above the ground ({vessel.AltitudeFromSeaLevel:n0} m above sea level), " +
                $"air speed {current_vessel.VesselVehicle.SurfaceSpeed:n1} m/s, Mach {vessel.MachNumber:n2}{position} | predicted from the {reentry_reference_name ?? "?"}: " +
                $"T{F(predictedUT, "F1")} ({F(now - predictedUT, "n1")} s late) at {F(predictedAltitude)} m | {string.Join("; ", states)}");
        }

        // ---- End ----

        void OnReentryFinish()
        {
            try
            {
                var vessel = current_vessel.VesselComponent;
                var cb = vessel.Orbit.referenceBody;
                double now = GeneralTools.Current_UT;
                Vector3d r = vessel.Orbit.GetRelativePositionAtUTZup(now);
                PrecisionContext.GameLatLon(cb, r, out double lat, out double lon);
                double d = HaversineDistanceMeters(lat, lon, precision_target_lat, precision_target_lon, cb.radius);
                precision_final_message = PrecisionPhases.LandedMessage(d);
                precision_distance_label = "Distance to Target";
                precision_distance_m = d;
                string compare = "no reference prediction";
                if (reentry_reference)
                {
                    Vector3d here = SurfaceGeometry.AtTime(r, ref_body.Omega, -(now - ref_ut));
                    double fromPredicted = SurfaceGeometry.GreatCircle(here, ref_stop.StopDirNow, ref_body.Radius);
                    double predictedMiss = SurfaceGeometry.GreatCircle(ref_stop.StopDirNow, ref_target, ref_body.Radius);
                    string split = "";
                    if (ref_stop.TrackDirNow.sqrMagnitude > 0)
                    {
                        SurfaceGeometry.Split(ref_stop.StopDirNow, here, ref_stop.TrackDirNow, ref_body.Radius, out double along, out double cross);
                        split = $" (touchdown {along:n0} m along the predicted track, {cross:n0} m across)";
                    }
                    compare = $"predicted from the {reentry_reference_name}: {predictedMiss:n0} m from the target{AlongCross(ref_stop, ref_target, ref_body.Radius)}, " +
                        $"touchdown T{ref_stop.StopUT:F1} ({now - ref_stop.StopUT:n1} s late); actual touchdown {fromPredicted:n0} m from the predicted one{split}";
                    Calibrate(here);
                }
                logger.LogInfo($"[Landing] reentry: landed {d:n1} m from the target (expected within about {Km(reentry_expected_m)}): at ({lat:0.00000}, {lon:0.00000}), " +
                    $"target ({precision_target_lat:0.00000}, {precision_target_lon:0.00000}) | {compare} | Δv used total {burn_dV.burned_dV:n1} m/s: " +
                    string.Join(", ", phase_log) + $" | drag scale {reentry_capsule.DragScale:0.00}");
            }
            catch (Exception e)
            {
                logger.LogWarning("[Landing] reentry: final distance not available: " + e);
            }
        }

        // The drag scale that would have put the reference prediction on the touchdown point.
        void Calibrate(Vector3d touchdownAtRef)
        {
            ReentryVehicle capsule = reentry_capsule;
            JettisonAwarePredictor p = ReentryPredictorFor(ref_body, jettisoned: true);
            var timer = Stopwatch.StartNew();
            DragCalibrationResult c = DragCalibration.Solve(s =>
            {
                ReentryVehicle scaled = capsule;
                scaled.DragScale = s;
                return p.Predict(ref_r, ref_v, ref_ut, null, scaled, out _);
            }, touchdownAtRef, ref_body.Radius);
            timer.Stop();
            double current = settings_atmo.reentry_drag_scale.V;
            if (c.Ok)
                suggested_drag_scale = c.Scale;
            logger.LogInfo($"[Landing] reentry: calibration from the {reentry_reference_name}: " + (c.Ok
                ? $"{DragCalibration.SuggestionMessage(c.Scale, current)}{(c.AtLimit ? " (at the limit of the range)" : "")}, residual {c.AlongM:n0} m along, " +
                  $"{c.CrossM:n0} m across (not a drag error), {c.Predictions} predictions, {timer.Elapsed.TotalMilliseconds:n0} ms - not applied"
                : $"no suggestion ({c.Reason}), {c.Predictions} predictions"));
        }

        // LandingUI's Apply button: writes the suggestion to the setting, never done on its own.
        internal void ApplySuggestedDragScale()
        {
            if (double.IsNaN(suggested_drag_scale))
                return;
            float before = settings_atmo.reentry_drag_scale.V;
            settings_atmo.reentry_drag_scale.V = (float)suggested_drag_scale;
            logger.LogInfo($"[Landing] reentry: drag scale applied by the player: {before:0.00} -> {settings_atmo.reentry_drag_scale.V:0.00}");
            suggested_drag_scale = double.NaN;
        }
    }
}

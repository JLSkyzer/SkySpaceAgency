using System;
using System.Globalization;
using K2D2.Landing.Braking;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using UnityEngine;

namespace K2D2.Landing
{
    // What Redux itself says about the landing, logged next to ours (K2-D2 1.4.0, IanMealworm):
    // its impact prediction (ReduxImpactPrediction) against compute_real_collision, and the game's
    // own body spin against our rotation sign, and Redux's stage recovery (a recovered stage being
    // flown). Logs only: nothing here flies the vessel.
    public partial class LandingPilot
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Redux is read at most once a second of real time (reflection); a line is written when its
        // state changes, and every ReduxLogInterval s otherwise.
        const float ReduxReadInterval = 1;
        const float ReduxLogInterval = 10;
        // The spin line waits this long for our rotation sign to be measured, then goes anyway.
        const float SpinLogWait = 60;

        float next_redux_read = 0;
        float next_redux_log = 0;
        ReduxImpactState? redux_last_state = null;
        bool redux_read_failed = false;
        bool spin_logged = false;
        float run_start_real = 0;
        string stage_recovery_phase = null;

        // At each start (isRunning).
        void ResetReduxLogs()
        {
            next_redux_read = 0;
            next_redux_log = 0;
            redux_last_state = null;
            spin_logged = false;
            run_start_real = Time.realtimeSinceStartup;
            stage_recovery_phase = null;
            LogStageRecovery("at landing start");
        }

        // Redux's stage recovery (ReduxStageRecovery): at the start, then on each phase change.
        void LogStageRecovery(string when)
        {
            ReduxReplay replay;
            try
            {
                replay = ReduxStageRecovery.Get();
            }
            catch (Exception e)
            {
                if (stage_recovery_phase != "unreadable")
                    logger.LogInfo($"[StageRecovery] {when}: unreadable ({e.GetType().Name}: {e.Message})");
                stage_recovery_phase = "unreadable";
                return;
            }
            if (replay.phase == stage_recovery_phase)
                return;
            string from = stage_recovery_phase;
            stage_recovery_phase = replay.phase;
            logger.LogInfo($"[StageRecovery] {when}: Redux stage recovery " + (from != null ? $"{from} -> " : "") + replay.phase +
                (replay.vessel_name != null ? string.Format(Inv, " ({0}, captured at UT {1:0.0})", replay.vessel_name, replay.captured_UT) : "") +
                (replay.flying_stage ? " - flying a recovered stage" : ""));
        }

        // Every frame of a run, after computeValues.
        void UpdateReduxLogs()
        {
            if (!isRunning)
                return;
            float now_real = Time.realtimeSinceStartup;
            if (now_real < next_redux_read)
                return;
            next_redux_read = now_real + ReduxReadInterval;
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return;
            var body = vessel.Orbit.referenceBody;
            LogStageRecovery($"in {mode}");

            if (!spin_logged && (rotation_sign_body == body.Name || now_real - run_start_real >= SpinLogWait))
            {
                spin_logged = true;
                LogBodySpin(body);
            }

            ReduxImpact redux;
            try
            {
                redux = ReduxImpactPrediction.GetFlown(vessel);
            }
            catch (Exception e)
            {
                if (!redux_read_failed)
                    logger.LogInfo($"[Landing] redux impact: unreadable ({e.GetType().Name}: {e.Message})");
                redux_read_failed = true;
                return;
            }
            bool changed = redux.state != redux_last_state;
            if (!changed && now_real < next_redux_log)
                return;
            redux_last_state = redux.state;
            next_redux_log = now_real + ReduxLogInterval;
            logger.LogInfo(ReduxComparisonLine(redux, body, GeneralTools.Current_UT));
        }

        string ReduxComparisonLine(ReduxImpact redux, CelestialBodyComponent body, double now)
        {
            string ours = collision_detected
                ? string.Format(Inv, "lat {0:0.0000} lon {1:0.0000} in {2:0} s", predicted_landing_lat, predicted_landing_lon, adjusted_collision_UT - now)
                : "no collision";
            if (redux.state != ReduxImpactState.Impact)
                return $"[Landing] redux impact: in {mode}: redux {redux.state} | ours: {ours}";

            string theirs = string.Format(Inv, "lat {0:0.0000} lon {1:0.0000} in {2:0} s, age {3:0.0} s, approximate {4}, usable {5}",
                redux.latitude, redux.longitude, redux.impact_UT - now, redux.age, redux.approximate, redux.IsUsable(now));
            string delta = collision_detected
                ? string.Format(Inv, " | distance {0:0} m, impact time redux - ours {1:0.0} s",
                    HaversineDistanceMeters(redux.latitude, redux.longitude, predicted_landing_lat, predicted_landing_lon, body.radius),
                    redux.impact_UT - adjusted_collision_UT)
                : "";
            return $"[Landing] redux impact: in {mode}: redux {theirs} | ours: {ours}{delta}";
        }

        // The game's spin vector of the body (relativeAngularVelocity), in the body frame (what
        // ImpactModel uses) and in the Zup axes our braking simulation uses, next to the rotation
        // sign we measure (surface speed, RotationProbe). Our Zup vectors are the celestial frame's
        // with y and z swapped (compute_real_collision): a reflection, which turns an axial vector
        // around, so the Zup spin is -(x, z, y) of the celestial one. If the implied sign keeps
        // agreeing in game, it may later replace the calibration (plan L8).
        void LogBodySpin(CelestialBodyComponent body)
        {
            try
            {
                Vector w = body.relativeAngularVelocity;
                Vector3d in_body = body.transform.bodyFrame.ToLocalVector(w);
                Vector celestial = w;
                celestial.Reframe(body.SimulationObject.transform.celestialFrame);
                Vector3d c = celestial.vector;
                Vector3d zup = -new Vector3d(c.x, c.z, c.y);
                int implied = Math.Sign(zup.z);
                double axis_deg = zup.magnitude > 0 ? Vector3d.Angle(zup, new Vector3d(0, 0, 1)) : double.NaN;
                double ours = BodyRotation.AngularSpeed(body.rotationPeriod);
                bool calibrated = rotation_sign_body == body.Name;
                int speed = SpeedSignFor(body.Name), probe = ProbeSignFor(body.Name);
                string verdict = !calibrated || implied == 0 ? "not compared"
                    : implied == rotation_sign ? "agrees" : "DISAGREES";
                logger.LogInfo(string.Format(Inv,
                    "[Landing] spin: {0}: game ω in body frame ({1:e3}, {2:e3}, {3:e3}) rad/s, |ω| {4:e4} (2π/period {5:e4}) | " +
                    "Zup ({6:e3}, {7:e3}, {8:e3}), axis {9:0.00}° from Zup z, implied sign {10} | " +
                    "our rotation sign {11}{12}, surface-speed sign {13}, probe sign {14} -> {15}",
                    body.Name, in_body.x, in_body.y, in_body.z, in_body.magnitude, ours,
                    zup.x, zup.y, zup.z, axis_deg, implied,
                    rotation_sign, calibrated ? "" : " (not measured on this body)",
                    speed == 0 ? "none" : speed.ToString(Inv), probe == 0 ? "none" : probe.ToString(Inv), verdict));
            }
            catch (Exception e)
            {
                logger.LogInfo($"[Landing] spin: unreadable ({e.GetType().Name}: {e.Message})");
            }
        }
    }
}

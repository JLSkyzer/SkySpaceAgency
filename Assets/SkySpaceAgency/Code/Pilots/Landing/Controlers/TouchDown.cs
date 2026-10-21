using System;
using K2D2.KSPService;
using K2UI;
using KSP.Sim;
// using KTools.UI;
using UnityEngine;
using UnityEngine.UIElements;
using K2D2.UI;
using K2D2.Controller;
using K2D2.Landing.Braking;

using KTools;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Landing
{
    /// apply the wanted speed in the good direction
    public class TouchDown : ExecuteController
    {
        public ILogger logger = SpaceWarp2.API.Logging.LogManager.GetLogger("SkySpaceAgency.TouchDown");

        public bool gravity_compensation;
        public float max_speed = 0;

        // True when even full thrust can no longer stop before the ground (DescentEnvelope.CanStop).
        // The landing tab shows it as an alert; braking goes on at full throttle anyway.
        public bool cannot_stop = false;

        // Precision landing (LandingPilot.UpdatePrecisionGuidance), set every frame: in Touch Down
        // above FinalDescent.FinalHeight, the terminal guidance's thrust direction (any frame) and
        // throttle replace the speed regulation; guidance_active is false otherwise. "Cannot
        // stop" overrides it for the frame (retrograde, full throttle).
        public bool guidance_active = false;
        public Vector guidance_aim;
        public float guidance_throttle = 0;
        // Precision brake (Brake mode): the throttle BrakeSimulator planned with
        // (PrecisionPhases.BrakeThrottle), so the predicted stop stays valid. NaN: speed regulation.
        public float precision_brake_throttle = float.NaN;
        // The guidance flies this frame (guidance_active, not in the final descent, can stop).
        bool guiding = false;

        // Atmo/Vacuum profile split (see LandingSettings.cs's class comment) - TouchDown's own 5
        // tunables get the same treatment: two full copies (atmo/vac, each its own independent
        // ClampSetting<float> against its own SettingsFile), selected via the pass-through
        // properties below every existing call site in this class already reads by the same names
        // (touch_down_max_angle.V, steering_max_angle.V, etc.). Only LandingUI.cs's binding
        // (SetupFullUI/SetupBasicUI below) needs to reach into `atmo`/`vac` directly instead of
        // through these properties, since one-time UI binding must not follow the current profile.
        class TouchDownSettings
        {
            public ClampSetting<float> touch_down_max_angle;

            // Largest tilt off vertical the precision landing's terminal guidance may use
            // (LandingPilot.UpdatePrecisionGuidance); it also sizes the lateral Δv reserve of the
            // precision plan. 0 refuses precision landing.
            public ClampSetting<float> steering_max_angle;

            // How fast the ENGINE THROTTLE ITSELF is allowed to change, in fraction/second (5 = 0
            // to full in 0.2s). No player-facing slider in either profile - still split per-profile
            // for consistency, since a future atmospheric descent profile may want a different
            // ramp rate than vacuum's.
            public ClampSetting<float> max_throttle_rate_per_sec;

            public TouchDownSettings(SettingsFile file)
            {
                touch_down_max_angle = new("land.touch_down_max_angle", 30, 0, 45, file);
                steering_max_angle = new("land.steering_max_angle", 40, 0, 80, file);
                max_throttle_rate_per_sec = new("land.max_throttle_rate_per_sec", 5, 1, 20, file);
            }
        }

        TouchDownSettings atmo;
        TouchDownSettings vac;

        public ClampSetting<float> touch_down_max_angle =>
            LandingProfile.IsAtmospheric ? atmo.touch_down_max_angle : vac.touch_down_max_angle;
        public ClampSetting<float> steering_max_angle =>
            LandingProfile.IsAtmospheric ? atmo.steering_max_angle : vac.steering_max_angle;
        public ClampSetting<float> max_throttle_rate_per_sec =>
            LandingProfile.IsAtmospheric ? atmo.max_throttle_rate_per_sec : vac.max_throttle_rate_per_sec;

        // Full binding (Vacuum panel) - touch_down_max_angle plus the PRECISION LANDING tilt limit
        // (steering_max_angle), since that only applies to vacuum precision landing. Called once, ever,
        // from LandingUI.onInit() against the vac_settings_panel root - see LandingSettings.cs's
        // class comment for why this can't just go through the touch_down_max_angle/etc.
        // properties above (those follow the CURRENT profile, which one-time UI binding must not).
        public void SetupFullUI(VisualElement root)
        {
            root.Q<K2Slider>("touch_down_max_angle_vac").Bind(vac.touch_down_max_angle);
            root.Q<K2Slider>("steering_max_angle_vac").Bind(vac.steering_max_angle);
        }

        // Reduced binding (Atmo panel) - just touch_down_max_angle, the one general-purpose
        // tunable of the four that isn't precision-landing-specific. steering_max_angle and
        // max_throttle_rate_per_sec have no control on either panel.
        public void SetupBasicUI(VisualElement root)
        {
            root.Q<K2Slider>("touch_down_max_angle_atmo").Bind(atmo.touch_down_max_angle);
        }

        // compute_Throttle() below recomputes wanted_throttle fresh every frame from delta_speed,
        // and every time the Pause<->Brake cycle (see LandingPilot.Update()) restarts a burn, that
        // jumps straight from 0 to whatever's needed, which reads as the throttle blipping to 100%.
        // max_throttle_rate_per_sec (see TouchDownSettings above) doesn't touch how much total dV
        // gets used, just smooths the ramp - fast enough that it shouldn't cost any real stopping
        // distance in an actual emergency, just the sudden all-or-nothing snap.
        float smoothed_throttle = 0;

        KSPVessel current_vessel;
        BurndV burn_dV = new BurndV();

        // float gravity_inclination = 0;
        float gravity_direction_factor = 0;
        float gravity;

        float wanted_throttle = 0;

        // Needed for the precision landing's guidance state (guidance_active, guidance_aim, ...)
        // and the altitude/mode, which all live on LandingPilot. Null when this TouchDown is used somewhere that doesn't wire a LandingPilot in (there
        // isn't one today, but keeping the null-check cheap insurance rather than assuming).
        LandingPilot landing;

        public TouchDown(LandingPilot landing, SettingsFile atmo_file, SettingsFile vac_file)
        {
            this.landing = landing;
            atmo = new TouchDownSettings(atmo_file);
            vac = new TouchDownSettings(vac_file);
            sub_contollers.Add(burn_dV);
            // logger.LogMessage("LandingController !");
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        // Not currently called anywhere - Brake/TouchDown deliberately never call this, since
        // they're meant to feel like one continuous burn sharing state across that transition (see
        // LandingPilot.Update()'s Mode.Brake comment). Kept as a real, working reset path for any
        // future phase that hands off to TouchDown partway through a descent at a different
        // altitude/velocity each time, needing a real reset rather than carrying over stale
        // smoothed state.
        public override void Start()
        {
            finished = false;
            cannot_stop = false;
            final_descent = false;
            thrust_factor = 1;
            smoothed_throttle = 0;
        }

        public void computeGravityRatio()
        {
            // current_vessel.getInclination();
            Vector up_dir = current_vessel.VesselComponent.gravityForPos;
            Rotation vessel_rotation = current_vessel.GetRotation();

            // convert rotation to maneuver coordinates

            vessel_rotation = Rotation.Reframed(vessel_rotation, up_dir.coordinateSystem);
            Vector3d forward_direction = (vessel_rotation.localRotation * Vector3.down).normalized;

            var gravity_inclination = (float)Vector3d.Angle(up_dir.vector, forward_direction);
            // status_line = $"Waiting for good sas direction\nAngle = {angle:n2}°";

            gravity = (float)current_vessel.VesselComponent.graviticAcceleration.magnitude;
            gravity_direction_factor = Mathf.Cos(gravity_inclination * Mathf.Deg2Rad);
        }

        void compute_Throttle()
        {
            // Active engines (BurndV.active_dv). If the active filter misreads the game's flags,
            // fall back to all engines rather than idling. With no thrust at all, idle instead of
            // dividing by zero: the landing tab shows the "Cannot stop" alert.
            float accel = burn_dV.active_dv > 0 ? burn_dV.active_dv : burn_dV.full_dv;
            delta_speed = RegulatedSpeed() - max_speed;
            if (!(accel > 0))
            {
                wanted_throttle = 0;
                return;
            }

            float min_throttle = 0;
            if (gravity_compensation && gravity_direction_factor != 0)
                min_throttle = gravity_direction_factor * gravity / accel;

            float remaining_full_burn_time = delta_speed / accel;
            wanted_throttle = Mathf.Clamp(remaining_full_burn_time + min_throttle, 0, 1);

            // Precision landing: the guidance's throttle, or the planned brake throttle.
            if (guiding)
                wanted_throttle = Mathf.Clamp01(guidance_throttle);
            else if (!float.IsNaN(precision_brake_throttle))
                wanted_throttle = Mathf.Clamp01(precision_brake_throttle);

            // Cannot stop any more: keep braking at maximum (spec). After the computation above,
            // which would otherwise overwrite it. Below h_final the pinned throttle is still scaled
            // by the alignment factor in Update (spec: thrust x cos(error)).
            if (cannot_stop)
                wanted_throttle = 1;
        }

        float delta_speed = 0;

        public bool NeedBurn => delta_speed > 0;

        public bool checkSpeed()
        {
            if (delta_speed < 0)
            {
                return true;
            }

            return false;
        }

        float retrograde_angle;

        // Final descent (FinalDescent: below FinalHeight, in TouchDown only), set by
        // checkDirection. thrust_factor scales the throttle there; it is 1 everywhere else.
        bool final_descent = false;
        float final_tilt_deg = 0;
        float thrust_factor = 1;

        bool IsFinalDescent() => landing != null && landing.mode == LandingPilot.Mode.TouchDown
            && FinalDescent.IsFinal(landing.altitude);

        // The speed the throttle regulates: the total surface speed, except in the final descent
        // where it is the downward vertical speed (the horizontal drift is the tilt's job).
        float RegulatedSpeed() => final_descent
            ? Mathf.Max(0, landing.current_falling_speed)
            : current_speed;

        public bool checkDirection()
        {

            var telemetry = SASTool.getTelemetry();

            // check that the direction is not over max_angle
            Vector HorizonUp = telemetry.HorizonUp;
            Vector retro_dir = telemetry.SurfaceMovementRetrograde;

            retro_dir.Reframe(HorizonUp.coordinateSystem);

            var speed_vertical_angle = (float)Vector3d.Angle(retro_dir.vector, HorizonUp.vector);

            // Final descent (below FinalHeight): the vertical regulation in Update/compute_Throttle
            // already lowers the throttle when the vessel climbs, so the guard below does not apply.
            final_descent = IsFinalDescent();
            // The guidance also flies a climb or a hover (its vertical channel), so the guard does
            // not apply to it either.
            guiding = guidance_active && !final_descent && !cannot_stop;

            if (speed_vertical_angle > 90 && !final_descent && !guiding)
            {
                status_line = $"Waiting for speed Down\nAngle = {speed_vertical_angle:n2}°\nFree Time Warp";
                return false;
            }

            TimeWarpTools.SetRateIndex(0, false);

            // The precision landing steers with the terminal guidance (LandingPilot.guidance_aim).
            Vector3d aim_dir = retro_dir.vector;

            if (guiding)
            {
                Vector aim = guidance_aim;
                aim.Reframe(HorizonUp.coordinateSystem);
                aim_dir = aim.vector.magnitude > 1e-9 ? aim.vector.normalized : HorizonUp.vector.normalized;
            }

            // Final descent: aim at the local vertical tilted against the horizontal drift
            // (FinalDescent), instead of pure retrograde. Applied through the same SAS
            // target as the terminal guidance below.
            if (final_descent)
            {
                double gravity_now = current_vessel.VesselComponent.graviticAcceleration.magnitude;
                // SurfaceMovementRetrograde is a direction: the surface velocity is minus it, at
                // the current surface speed.
                Vector3d surface_velocity = retro_dir.vector.normalized * (-current_speed);
                aim_dir = FinalDescent.AimDirection(HorizonUp.vector, surface_velocity, gravity_now);
                final_tilt_deg = (float)Vector3d.Angle(aim_dir, HorizonUp.vector);
            }

            if (final_descent || guiding)
            {
                var autopilot = current_vessel.Autopilot;
                autopilot.Enabled = true;
                // Guard the mode switch like SASTool.setAutoPilot does elsewhere - re-calling
                // SetMode every single tick even while already in StabilityAssist risks resetting
                // SAS's own internal state each frame instead of letting it settle onto the target.
                if (autopilot.AutopilotMode != AutopilotMode.StabilityAssist)
                    autopilot.SetMode(AutopilotMode.StabilityAssist);
                autopilot.SAS.lockedMode = false;
                autopilot.SAS.SetTargetOrientation(new Vector(HorizonUp.coordinateSystem, aim_dir), false);
            }
            else
            {
                SASTool.setAutoPilot(AutopilotMode.Retrograde);
            }

            Rotation vessel_rotation = current_vessel.GetRotation();

            // convert rotation to maneuver coordinates
            vessel_rotation = Rotation.Reframed(vessel_rotation, retro_dir.coordinateSystem);
            Vector3d forward_direction = (vessel_rotation.localRotation * Vector3.up).normalized;

            // Compare against aim_dir (the guidance or final-descent direction, plain retrograde
            // otherwise) rather than always retro_dir - otherwise a real tilt would
            // read as "already aligned" against the old retrograde-only check while the vessel is
            // still rotating to catch up with what's actually commanded, and throttle would fire
            // before it's actually pointed the right way.
            retrograde_angle = (float)Vector3d.Angle(aim_dir, forward_direction);

            if (final_descent)
            {
                // Below FinalHeight the thrust is never cut because SAS lags: Update scales it by
                // how well the vessel points instead (FinalDescent.ThrustFactor).
                thrust_factor = (float)FinalDescent.ThrustFactor(retrograde_angle);
                status_line = $"Final descent\nTilt = {final_tilt_deg:n1}°, Angle = {retrograde_angle:n2}°";
                return true;
            }

            if (guiding)
            {
                // Same rule as the final descent: never cut the thrust only because SAS lags (the
                // guidance holds the vessel up), scale it by the alignment instead.
                thrust_factor = (float)FinalDescent.ThrustFactor(retrograde_angle);
                status_line = $"Terminal guidance\nAngle = {retrograde_angle:n2}°";
                return true;
            }

            thrust_factor = 1;
            status_line = $"Waiting for Vessel rotation\nAngle = {retrograde_angle:n2}°";
            return retrograde_angle < touch_down_max_angle.V;
        }

        float current_speed;

        void UpdateCannotStop()
        {
            // The envelope (DescentEnvelope.CanStop) is a vertical-fall formula on total ground
            // speed: only meaningful in the final TouchDown phase. Brake's alert comes from the
            // braking simulation instead (LandingUI).
            if (landing == null || landing.mode != LandingPilot.Mode.TouchDown)
            {
                cannot_stop = false;
                return;
            }
            double gravity_now = current_vessel.VesselComponent.graviticAcceleration.magnitude;
            // Final descent: the throttle regulates the downward speed to the contact speed, so the
            // envelope is checked against those, not the total speed and the player's touch-down speed.
            double speed = final_descent ? RegulatedSpeed() : current_speed;
            double target_speed = final_descent
                ? FinalDescent.ContactSpeed(landing.settings.touch_down_speed.V)
                : landing.settings.touch_down_speed.V;
            bool now = !DescentEnvelope.CanStop(speed, landing.altitude, burn_dV.active_dv,
                gravity_now, target_speed);
            if (now && !cannot_stop)
                logger.LogWarning($"[TouchDown] cannot stop before the ground: speed {speed:n1} m/s, " +
                    $"height {landing.altitude:n0} m, thrust accel {burn_dV.active_dv:n2} m/s², gravity {gravity_now:n2} m/s²");
            cannot_stop = now;
        }

        void LogFinalDescentStart()
        {
            double v_down = landing.current_falling_speed;
            double v_h = Math.Sqrt(Math.Max(0, (double)current_speed * current_speed - v_down * v_down));
            double gravity_now = current_vessel.VesselComponent.graviticAcceleration.magnitude;
            double tilt_deg = FinalDescent.TiltDegrees(v_h, gravity_now);
            logger.LogInfo($"[TouchDown] final descent: height {landing.altitude:n0} m, horizontal {v_h:n1} m/s, " +
                $"fall {v_down:n1} m/s, tilt {tilt_deg:n1}°");
        }

        public override void Update()
        {
            if (current_vessel == null || current_vessel.VesselVehicle == null)
                return;

            current_speed = (float)current_vessel.VesselVehicle.SurfaceSpeed;

            // Before UpdateCannotStop: it reads final_descent.
            bool was_final_descent = final_descent;
            final_descent = IsFinalDescent();
            if (final_descent && !was_final_descent)
                LogFinalDescentStart();

            UpdateCannotStop();

            delta_speed = RegulatedSpeed() - max_speed;

            if (delta_speed > 0) // reset timewarp if it is time to burn
                TimeWarpTools.SetRateIndex(0, false);

            if (gravity_compensation)
                computeGravityRatio();

            current_vessel.SetSpeedMode(KSP.Sim.SpeedDisplayMode.Surface);

            // if (autopilot.AutopilotMode != AutopilotMode.Retrograde)
            //         autopilot.SetMode(AutopilotMode.Retrograde);
            // else


            // if (autopilot.AutopilotMode != AutopilotMode.Retrograde)
            //     autopilot.SetMode(AutopilotMode.Retrograde);
            // else
            //     if (autopilot.AutopilotMode != AutopilotMode.StabilityAssist)
            //         autopilot.SetMode(AutopilotMode.StabilityAssist);

            if (!checkDirection())
            {
                current_vessel.SetThrottle(0);
                // Keep this in sync with the real applied throttle (0) - otherwise the next good
                // frame would ramp starting from whatever smoothed_throttle was before we cut it,
                // not from the actual current 0, and briefly re-apply stale throttle.
                smoothed_throttle = 0;
                // status_line = $"Turning : {retrograde_angle:n2} °";
                return;
            }

            compute_Throttle();
            // 1 outside the final descent; there, the throttle follows how well the vessel points.
            wanted_throttle *= thrust_factor;

            // Rate-limit the actual applied throttle instead of snapping straight to the freshly
            // computed value - see max_throttle_rate_per_sec's comment for why.
            float throttle_step = max_throttle_rate_per_sec.V * Time.deltaTime;
            smoothed_throttle = Mathf.MoveTowards(smoothed_throttle, wanted_throttle, throttle_step);

            // no stop for gravity compensation
            current_vessel.SetThrottle(smoothed_throttle);
        }

        // Numeric telemetry for the Landing tab's own on-page info table (see LandingUI.cs's
        // updateContext) - status_line stays narrative-only now (checkDirection's "Waiting for
        // ..." messages) instead of being overwritten every tick with this Max Speed readout.
        public override void UpdateInfoRows(System.Action<string, string> addRow)
        {
            addRow("Max Speed", $"{max_speed:n2} m/s");
            addRow("Delta Speed", $"{delta_speed:n2} m/s");
            if (final_descent)
                addRow("Final Descent", $"tilt {final_tilt_deg:n1}°, thrust x{thrust_factor:n2}");

            if (K2D2Settings.debug_mode.V)
            {
                if (gravity_compensation)
                {
                    addRow("Gravity", $"{gravity:n2}");
                    addRow("Gravity Dir. Factor", $"{gravity_direction_factor:n2}");
                }

                addRow("Wanted Throttle", $"{wanted_throttle:n2}");
                addRow("Actual Throttle", $"{smoothed_throttle:n2}");
            }
        }

        public override void updateUI(VisualElement el, FullStatus st)
        {
            // need to burn ?

            string txt = $" Max speed : {max_speed:n2} !!";
            txt += $"\n delta speed  : {delta_speed:n2}  m/s";

            var level = delta_speed > 0 ? StatusLine.Level.Warning : StatusLine.Level.Normal;
            st.Status(txt, level );

            if (burn_dV.burned_dV > 0)
                st.Console($"dV consumed : {burn_dV.burned_dV:n2} m/s");

            if (K2D2Settings.debug_mode.V)
            {
                if (gravity_compensation)
                {
                    st.Console($"gravity : {gravity:n2}");
                    st.Console($"gravity_direction_factor : {gravity_direction_factor:n2}");
                }

                st.Console($"wanted_throttle : {wanted_throttle:n2}");
            }
        }


    }
}

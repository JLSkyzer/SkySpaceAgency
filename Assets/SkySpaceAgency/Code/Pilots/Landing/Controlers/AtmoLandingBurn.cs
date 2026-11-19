using System;
using System.Globalization;
using SkySpaceAgency.Controller;
using SkySpaceAgency.KSPService;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using UnityEngine;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace SkySpaceAgency.Landing
{
    // The landing burn on a body with an atmosphere, from the handover down to final_height_m
    // (K2-D2 1.4.0, IanMealworm): one continuous burn, killing the sideways speed (or, with a
    // target, steering onto it). Below final_height_m it is finished, and LandingPilot hands over to
    // Touch Down's final descent (legs, contact speed, standing check), unchanged. K2-D2 flies the
    // final descent in this class too; that part is not ported.
    //
    // The regular braking it replaces burned at full thrust until the fall was slow, then fell
    // and braked again, and held retrograde all the way down. On K2-D2's big booster that stopped
    // 3.7 km up, fell, braked again, and crept down the last kilometre - about 1,500 m/s for a
    // burn that needs about 1,000.
    //
    // Braking: several times a second, the throttle is set to what ends the braking just above
    // the ground (AtmoBurnPlanner's fall with drag, from the vessel's state right now). Re-planning
    // all the way takes care of what a plan made once can't: the vessel getting lighter as it burns,
    // and drag being off.
    //
    // Steering: the thrust is tilted off vertical (at most max_braking_tilt_deg, and only with the
    // thrust the braking leaves spare) toward a sideways speed that reaches the target by
    // touchdown, or toward none without a target; the drag's own sideways part is taken off. How
    // much sideways acceleration the tilt really gives is measured as it burns (K2-D2's test
    // booster got 40-60 % of it).
    //
    // Attitude: SAS Stability Assist turned toward the thrust direction (as Touch Down's final
    // descent), not K2-D2's full-orientation hold (AtmoAttitudeHold, boost-back only here).
    public class AtmoLandingBurn : ExecuteController
    {
        readonly ILogger logger = ModIdentity.Logger("AtmoLandingBurn");
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // The braking ends this high above the ground, at the Touch Down speed for this height;
        // Touch Down's final descent (FinalDescent.FinalHeight, also 50 m) takes it from there.
        public const double final_height_m = 50;

        // The speed the braking ends at - the player's Touch Down speed profile at final_height_m.
        public static double FinalSpeed(LandingSettings settings) => settings.compute_limit_speed((float)final_height_m);

        const double solve_interval_s = 0.1;
        // The thrust tilts at most this far off vertical to steer.
        const double max_braking_tilt_deg = 15;
        // The steering's time left never counts as less than this.
        const double min_steering_time_s = 4;
        // The push toward the wanted sideways speed closes the gap over about this long.
        const double steering_response_s = 2;
        // The approach speed is kept to what would stop in half the distance left at the sideways
        // acceleration being achieved, as a margin.
        const double stopping_margin = 0.5;
        // How much of the expected sideways acceleration the vessel gets: measured over
        // response_window_s, blended in at response_blend; starts from what the test booster got.
        const double response_window_s = 1, response_blend = 0.3;
        const double first_efficiency = 0.5, min_efficiency = 0.2;
        // Full throttle within this pointing error, fading to none at Touch Down's max angle.
        const double full_throttle_within_deg = 5;
        // How fast the throttle may change (fraction per second), so ignition eases in.
        const float throttle_rate_per_s = 3;
        const double log_interval_s = 2;

        readonly LandingPilot landing;
        readonly KSPVessel current_vessel;

        double last_solve_UT, last_log_UT, start_UT;
        double wanted_throttle;
        float throttle;
        Vector3d thrust_direction;
        double tilt_deg, stop_height, burn_left_s, attitude_error_deg;
        double target_ahead, target_side;
        Vector3d wanted_sideways, expected_sideways;
        double achieved_sideways = double.NaN;
        double efficiency;
        Vector3d response_velocity;
        double response_UT = double.NaN;

        // Whether the burn steers onto the landing target; without one it only cancels the
        // sideways speed and comes straight down. Always false in this mod for now.
        bool steer_to_target;

        public AtmoLandingBurn(LandingPilot landing)
        {
            this.landing = landing;
            current_vessel = SkySpaceAgencyPlugin.Instance.current_vessel;
        }

        public void Start(bool steer_to_target)
        {
            this.steer_to_target = steer_to_target;
            Start();
        }

        public override void Start()
        {
            base.Start();
            last_solve_UT = last_log_UT = double.NaN;
            start_UT = GeneralTools.Current_UT;
            wanted_throttle = 0;
            throttle = 0;
            thrust_direction = Vector3d.zero;
            response_UT = achieved_sideways = double.NaN;
            efficiency = first_efficiency;
            attitude_error_deg = 180;
        }

        static double Accel(LandingPilot landing) =>
            landing.burn_dV.active_dv > 0 ? landing.burn_dV.active_dv : landing.burn_dV.full_dv;

        public override void Update()
        {
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null || finished)
                return;
            double now = GeneralTools.Current_UT;
            TimeWarpTools.SetRateIndex(0, false);

            var body = vessel.mainBody;
            var model = new ImpactModel(body, now);
            model.StateAt(vessel.Orbit, now, out Vector3d position, out Vector3d inertial_velocity);
            if (position.magnitude < 1)
                return;
            Vector3d air_velocity = model.AirVelocity(position, inertial_velocity);
            Vector3d up = position.normalized;
            double v_vertical = Vector3d.Dot(air_velocity, up);
            Vector3d horizontal = air_velocity - up * v_vertical;
            // Height of the vessel above the ground (or sea) under it.
            double height = landing.altitude;
            double max_accel = Accel(landing);

            if (height < final_height_m)
            {
                logger.LogInfo(string.Format(Inv, "[LandingBurn] handover to Touch Down at {0:0} m: {1:0.0} m/s down, {2:0.0} m/s sideways, " +
                    "throttle {3:0} %, {4:0} s after ignition", height, -v_vertical, horizontal.magnitude, throttle * 100, now - start_UT));
                status_line = "Landing burn: final descent";
                finished = true;
                return;
            }

            if (double.IsNaN(last_solve_UT) || now - last_solve_UT >= solve_interval_s)
            {
                last_solve_UT = now;
                SolveBraking(vessel, AtmoLandingTiming.AirOf(body), model, position, air_velocity, up, v_vertical, horizontal, height, max_accel);
            }

            Point(body);
            double max_angle = Math.Max(full_throttle_within_deg + 1, landing.brake.touch_down_max_angle.V);
            double pointing = Math.Min(1, Math.Max(0, (max_angle - attitude_error_deg) / (max_angle - full_throttle_within_deg)));
            throttle = Mathf.MoveTowards(throttle, (float)(wanted_throttle * pointing), throttle_rate_per_s * Time.deltaTime);
            current_vessel.SetThrottle(throttle);

            status_line = $"Landing burn: braking to stop {stop_height:n0} m up" +
                (steer_to_target ? $", {Math.Sqrt(target_ahead * target_ahead + target_side * target_side):n0} m to the target" : "");

            if (double.IsNaN(last_log_UT) || now - last_log_UT >= log_interval_s)
            {
                last_log_UT = now;
                logger.LogInfo(string.Format(Inv,
                    "[LandingBurn] braking at {0:0} m, {1:0} m/s ({2:0} down, {3:0} sideways) | throttle {4:0} %, tilt {5:0.0}°, pointing off {6:0.0}° | " +
                    "braking ends {7:0} m up in {8:0} s | sideways accel wanted {9:0.0} m/s², expected {10:0.0}, got {11:0.0} ({12:0} % on average)" +
                    (steer_to_target ? " | target {13:0} m ahead, {14:0} m to the side" : ""),
                    height, air_velocity.magnitude, -v_vertical, horizontal.magnitude, wanted_throttle * 100, tilt_deg, attitude_error_deg,
                    stop_height, burn_left_s, wanted_sideways.magnitude, expected_sideways.magnitude, achieved_sideways, efficiency * 100,
                    target_ahead, target_side));
            }
        }

        // Throttle: the one that ends the braking at final_height_m. Steering: tilt toward moving
        // where it ends onto the target (or toward no sideways speed).
        void SolveBraking(VesselComponent vessel, AtmoBurnPlanner.Air air, ImpactModel model, Vector3d position, Vector3d air_velocity,
            Vector3d up, double v_vertical, Vector3d horizontal, double height, double max_accel)
        {
            var planner = landing.air_planner;
            double ground_altitude = vessel.AltitudeFromSeaLevel - height;
            double v_horizontal = horizontal.magnitude;
            double stop_speed = FinalSpeed(landing.settings);

            double tau = planner.ThrottleToStopAt(air, height, ground_altitude, v_horizontal, v_vertical, max_accel, stop_speed,
                final_height_m, out double h, out _, out double t);
            wanted_throttle = tau;
            stop_height = h;
            burn_left_s = t;

            Vector3d along = v_horizontal > 0.5 ? horizontal / v_horizontal : Vector3d.zero;
            Vector3d to_target = ToTarget(model, position, up);
            target_ahead = Vector3d.Dot(to_target, along);
            target_side = Vector3d.Dot(to_target, Vector3d.Cross(up, along));

            // The braking comes first: the tilt can only use thrust the braking leaves spare, and
            // the throttle goes up to keep the braking (the upward part) as planned.
            double max_tilt = Math.Min(max_braking_tilt_deg, Math.Acos(Math.Min(1, tau)) * 180 / Math.PI);
            double vertical_thrust = Math.Max(0.1, tau) * max_accel;
            double t_go = Math.Max(min_steering_time_s, t + final_height_m / Math.Max(1, stop_speed));
            double available = efficiency * vertical_thrust * Math.Tan(max_braking_tilt_deg * Math.PI / 180);
            // Whatever sideways speed it builds up has to be gone again by the end of the braking.
            double can_stop_from = available * Math.Max(0, t) / 2;
            wanted_sideways = Steer(to_target, horizontal, t_go, available, can_stop_from);
            double air_speed = air_velocity.magnitude;
            Vector3d drag_sideways = air_speed > 1
                ? -horizontal / air_speed * planner.DragAccel(air, vessel.AltitudeFromSeaLevel, air_speed)
                : Vector3d.zero;
            thrust_direction = Tilt(up, wanted_sideways - drag_sideways, vertical_thrust, max_tilt);
            wanted_throttle = Math.Min(1, tau / Math.Cos(tilt_deg * Math.PI / 180));
            MeasureResponse(up, horizontal, max_accel, drag_sideways);
        }

        // The sideways push: toward a speed at the target that gets there in t_go, but no faster
        // than stops in time at `available` m/s^2 (or than max_speed).
        Vector3d Steer(Vector3d to_target, Vector3d horizontal, double t_go, double available, double max_speed)
        {
            double d = to_target.magnitude;
            Vector3d wanted_velocity = Vector3d.zero;
            if (d > 0.5)
            {
                double speed = Math.Min(max_speed, Math.Min(d / t_go, Math.Sqrt(2 * stopping_margin * Math.Max(0.1, available) * d)));
                wanted_velocity = to_target / d * speed;
            }
            return (wanted_velocity - horizontal) / steering_response_s;
        }

        // How much of the sideways acceleration the thrust tilt (and drag) should give the vessel
        // actually gets, from the change in sideways speed.
        void MeasureResponse(Vector3d up, Vector3d horizontal, double max_accel, Vector3d drag_sideways)
        {
            Vector3d thrust = thrust_direction * (throttle * max_accel);
            expected_sideways = thrust - up * Vector3d.Dot(thrust, up) + drag_sideways;
            double now = GeneralTools.Current_UT;
            if (double.IsNaN(response_UT))
            {
                response_velocity = horizontal;
                response_UT = now;
                return;
            }
            if (now - response_UT < response_window_s)
                return;
            Vector3d accel = (horizontal - response_velocity) / (now - response_UT);
            double expected = expected_sideways.magnitude;
            achieved_sideways = expected > 1e-3 ? Vector3d.Dot(accel, expected_sideways / expected) : double.NaN;
            if (expected > 1)
                efficiency += (Math.Max(min_efficiency, Math.Min(1, achieved_sideways / expected)) - efficiency) * response_blend;
            response_velocity = horizontal;
            response_UT = now;
        }

        // `main` (a unit vector) tilted toward `sideways` (an acceleration at right angles to it)
        // as far as that acceleration against `main_accel` asks, up to max_deg.
        Vector3d Tilt(Vector3d main, Vector3d sideways, double main_accel, double max_deg)
        {
            double side = sideways.magnitude;
            if (side < 1e-6)
            {
                tilt_deg = 0;
                return main;
            }
            double angle = Math.Min(max_deg * Math.PI / 180, Math.Atan2(side, main_accel));
            tilt_deg = angle * 180 / Math.PI;
            return main * Math.Cos(angle) + sideways / side * Math.Sin(angle);
        }

        // From the vessel to the target, along the ground. Zero without a target.
        Vector3d ToTarget(ImpactModel model, Vector3d position, Vector3d up)
        {
            if (!steer_to_target)
                return Vector3d.zero;
            var settings = landing.settings;
            double lat = settings.target_latitude.V, lon = settings.target_longitude.V;
            Vector3d to_target = model.SurfaceUp(lat, lon) * model.TerrainRadius(lat, lon) - position;
            return to_target - up * Vector3d.Dot(to_target, up);
        }

        // Turns the vessel's thrust axis toward thrust_direction (ImpactModel's axes: the body
        // frame's at this moment), through SAS Stability Assist as Touch Down's final descent does.
        void Point(CelestialBodyComponent body)
        {
            if (thrust_direction.magnitude < 1e-9)
                return;
            var autopilot = current_vessel.Autopilot;
            if (autopilot == null)
                return;
            ITransformFrame frame = body.transform.bodyFrame;
            autopilot.Enabled = true;
            if (autopilot.AutopilotMode != AutopilotMode.StabilityAssist)
                autopilot.SetMode(AutopilotMode.StabilityAssist);
            autopilot.SAS.lockedMode = false;
            autopilot.SAS.SetTargetOrientation(new Vector(frame, thrust_direction.normalized), false);

            Rotation current = Rotation.Reframed(current_vessel.GetRotation(), frame);
            Vector3d nose = (current.localRotation * Vector3.up).normalized;
            attitude_error_deg = Vector3d.Angle(thrust_direction, nose);
        }

        public override void UpdateInfoRows(Action<string, string> addRow)
        {
            addRow("Braking Ends", $"{stop_height:n0} m up in {burn_left_s:n0} s");
            if (!SkySpaceAgencySettings.debug_mode.V)
                return;
            addRow("Throttle", $"{wanted_throttle:P0}");
            addRow("Tilt", $"{tilt_deg:n1}°");
            addRow("Pointing Off", $"{attitude_error_deg:n1}°");
        }
    }
}

using System;
using System.Globalization;
using SkySpaceAgency.Controller;
using SkySpaceAgency.KSPService;
using KSP.Sim;
using KSP.Sim.impl;
using KSP2FlightAssistant.MathLibrary;
using KTools;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace SkySpaceAgency.Landing
{
    // Entry guidance after a boost-back (K2-D2 1.4.0, IanMealworm): steers a booster's lift on its
    // way down so Redux's predicted landing point stays on the target. The steering rule itself is
    // EntryLiftSteering (pure, tested); this is its glue to the game.
    //
    // A K2-D2 feature: compiled here, flown only in the K2-D2 flavour (ModIdentity.IsK2D2,
    // BoostBackStart). Differences from upstream: 1x only (no warp down to the atmosphere: our
    // landing rule), our heat guard stays the authority (heat_guard_throttle: while it is on, the
    // vessel flies straight into the airflow at the guard's throttle; EntryHeatLimit only lowers the
    // angle of attack), and the drag is measured into the pilot's air planner, shared with the
    // landing burn (AtmoLandingBurn) and the air timing.
    //
    // Tilting the booster off the airflow makes lift, and a round booster can tilt any way by any
    // amount. Redux only predicts for the attitude being flown right now, and restarts its
    // prediction whenever that attitude changes by more than 2°. So the guidance works in steps:
    // hold an attitude, wait for a fresh prediction made after the vessel settled into it (Redux
    // answers within half a second, but turning the vessel takes a few), adjust, repeat.
    //
    // It steers until the landing burn is due, then hands over to the landing burn
    // (AtmoLandingBurn, LandingPilot's Brake mode). AtmoBurnPlanner works out, with drag, when
    // that is and how much shorter the burn makes the flight; the steering aims that far beyond
    // the target so the burn ends on it.
    public class AtmoEntryGuidance : ExecuteController
    {
        readonly ILogger logger = ModIdentity.Logger("AtmoEntryGuidance");
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // The planner's flat model only holds over the last stretch, so it's used below this.
        const double burn_planning_below_m = 35000;
        const double burn_plan_interval_s = 0.5;
        // The overshoot is smoothed over about this long, so a noisy plan doesn't jerk the aim.
        const double shortfall_smoothing_s = 5;
        // Without a burn plan (no drag measured yet, or no thrust figure for the engines), hand
        // over at this fixed height.
        const double unplanned_handover_m = 10000;
        // Hand over regardless below these.
        const double end_altitude_m = 2000;
        const double end_speed_mps = 150;

        // Redux restarts its prediction when the held attitude moves more than this.
        const double redux_attitude_tolerance_deg = 2;
        const double accel_sample_s = 0.5;
        // How often Redux's prediction is logged on the way down to the atmosphere.
        const double coast_log_interval_s = 10;
        const double stalled_log_interval_s = 10;

        readonly LandingPilot landing;
        readonly AtmoAttitudeHold hold = new AtmoAttitudeHold();
        readonly EntryHeatLimit heat = new EntryHeatLimit();
        readonly EntryLiftSteering steering = new EntryLiftSteering();
        readonly KSPVessel current_vessel;

        // Set by LandingPilot every frame: our heat guard's throttle while it is on, NaN otherwise.
        internal double heat_guard_throttle = double.NaN;

        bool in_atmosphere;
        double settled_UT;
        // When the steering last had a fresh prediction to use; logged when it's been too long.
        double last_steer_UT, last_stalled_log_UT;
        // Set while a stage that started inside the air is still going over the top: steering
        // starts once it comes back down through this height.
        double steer_below_m;
        // Each Redux result is steered on once; the next step waits for a newer one.
        double last_used_snapshot_UT;
        double angle_of_attack_deg;
        double last_coast_log_UT;

        // Aero acceleration measurement: which way the lift points, and the drag for the burn
        // planner.
        Vector3d previous_velocity;
        double previous_sample_UT;

        AtmoBurnPlanner planner => landing.air_planner;
        AtmoBurnPlanner.Plan burn_plan;
        double last_plan_UT;
        double shortfall_m;
        bool have_shortfall;
        // Redux's latest along-track miss (for a burn brought forward when the lift can't take
        // back an overshoot).
        double last_redux_along = double.NaN;
        double ground_altitude;

        public AtmoEntryGuidance(LandingPilot landing)
        {
            this.landing = landing;
            current_vessel = SkySpaceAgencyPlugin.Instance.current_vessel;
        }

        bool heat_guard_on => !double.IsNaN(heat_guard_throttle);
        // The angle of attack actually flown.
        double flown_aoa_deg => heat_guard_on ? 0 : angle_of_attack_deg * steering.lift_scale;

        public override void Start()
        {
            base.Start();
            in_atmosphere = false;
            hold.keep_roll = true;
            steering.Reset();
            settled_UT = double.NaN;
            last_used_snapshot_UT = double.NaN;
            last_coast_log_UT = double.NaN;
            previous_sample_UT = double.NaN;
            angle_of_attack_deg = landing.settings.entry_angle_of_attack.V;
            heat.Reset(EntryLiftSteering.max_angle_of_attack_deg);
            planner.Reset();
            burn_plan = default;
            last_plan_UT = double.NaN;
            shortfall_m = 0;
            have_shortfall = false;
            last_redux_along = double.NaN;
            last_steer_UT = GeneralTools.Current_UT;
            last_stalled_log_UT = double.NegativeInfinity;
            steer_below_m = double.NaN;
            heat_guard_throttle = double.NaN;
        }

        // Hands the attitude back to normal SAS - for when the landing is stopped mid-entry.
        public void Release() => hold.Release();

        public override void Update()
        {
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null || finished)
                return;

            double now = GeneralTools.Current_UT;
            var body = vessel.mainBody;
            double altitude = vessel.AltitudeFromRadius;
            // Our landing rule: 1x all the way.
            TimeWarpTools.SetRateIndex(0, false);
            // No engine in the entry, except our heat guard's push.
            current_vessel.SetThrottle(heat_guard_on ? (float)heat_guard_throttle : 0);

            if (!in_atmosphere)
            {
                if (altitude > body.atmosphereDepth)
                {
                    CoastToAtmosphere(vessel, now);
                    return;
                }
                in_atmosphere = true;
                last_steer_UT = now;
                ground_altitude = body.SurfaceProvider.GetTerrainAltitudeFromCenter(
                    landing.settings.target_latitude.V, landing.settings.target_longitude.V) - body.radius;
                logger.LogInfo(string.Format(Inv, "[EntryGuidance] entered the atmosphere at {0:0} m, angle of attack {1:0}°",
                    altitude, landing.settings.entry_angle_of_attack.V));
                // Started inside the air and still climbing (a boosted-back stage): nothing to steer
                // until it comes back down through here (see CoastOverTheTop).
                if (Climbing(vessel, now))
                {
                    steer_below_m = altitude;
                    logger.LogInfo(string.Format(Inv, "[EntryGuidance] still climbing: holding the attitude for coming back down through {0:0} m", altitude));
                }
            }

            if (!double.IsNaN(steer_below_m))
            {
                if (altitude > steer_below_m || Climbing(vessel, now))
                {
                    CoastOverTheTop();
                    return;
                }
                logger.LogInfo(string.Format(Inv, "[EntryGuidance] back down through {0:0} m: steering", steer_below_m));
                steer_below_m = double.NaN;
                last_steer_UT = now;
                settled_UT = double.NaN;
            }

            if (!hold.UpdateAxes(out Vector3d position, out Vector3d air_velocity, out _))
                return;

            PlanBurn(vessel, position, air_velocity, altitude, now);
            // The plan is from up to burn_plan_interval_s ago, so its countdown is run on to now.
            bool burn_due = burn_plan.found ? burn_plan.handover_in_s - (now - last_plan_UT) <= 0 : altitude < unplanned_handover_m;
            // Only low down and coming down: a boosted-back stage passes slowly over the top of its
            // arc, 60+ km up, and handing over there sent it into the braking far too early.
            bool slow = air_velocity.magnitude < end_speed_mps && Vector3d.Dot(air_velocity, position) < 0
                && altitude < unplanned_handover_m;
            if (burn_due || altitude < end_altitude_m || slow)
            {
                var last = ReduxImpactPrediction.GetFlown(vessel);
                string where = "";
                if (last.usable)
                {
                    Miss(last, body, out double along, out double across);
                    where = string.Format(Inv, ", Redux lands {0:0} m along (aiming {1:0} m long), {2:0} m across", along, shortfall_m, across);
                }
                string burn = burn_plan.found
                    ? string.Format(Inv, " | burn planned to slow to {0:0} m/s at {1:0} m, ", burn_plan.stop_falling_speed, burn_plan.stop_height_m) +
                      (burn_plan.early ? string.Format(Inv, "brought forward {0:0} s to end {1:0} m past the target, ", burn_plan.latest_handover_in_s, burn_plan.expected_overshoot_m) : "") +
                      string.Format(Inv, "drag k {0:e2}, lift/drag {1:0.00}", planner.drag_k, planner.lift_over_drag)
                    : " | no burn plan";
                logger.LogInfo(string.Format(Inv, "[EntryGuidance] done at {0:0} m, {1:0} m/s after {2} updates", altitude, air_velocity.magnitude, steering.updates) +
                    $"{(burn_due ? ", landing burn due" : "")}{where}{burn} - handing over to the landing burn");
                hold.Release();
                finished = true;
                return;
            }

            UpdateAngleOfAttack(vessel, now);
            hold.Hold(flown_aoa_deg, steering.lift_bank_deg + steering.tilt_offset_deg);
            if (double.IsNaN(settled_UT) && hold.Settled(redux_attitude_tolerance_deg))
                settled_UT = now;

            if (MeasureAero(vessel, now, out Vector3d aero))
            {
                double drag = -Vector3d.Dot(aero, hold.flow);
                planner.MeasureDrag(AtmoLandingTiming.AirOf(body), drag, Vector3d.Dot(aero, hold.lift_up), altitude, air_velocity.magnitude, now);
                double tilt_before = steering.tilt_offset_deg;
                if (steering.CheckLiftDirection(Vector3d.Dot(aero, hold.lift_up), Vector3d.Dot(aero, hold.side), drag, flown_aoa_deg,
                        out double measured_bank))
                {
                    // The lean flipped: a new attitude, wait for Redux to predict it.
                    if (steering.tilt_offset_deg != tilt_before)
                        settled_UT = double.NaN;
                    logger.LogInfo(string.Format(Inv, "[EntryGuidance] lift measured at bank {0:0}° while aiming it at {1:0}° - vessel tilts {2:0}° from where its lift points",
                        measured_bank, steering.lift_bank_deg, steering.tilt_offset_deg));
                }
            }

            var impact = ReduxImpactPrediction.GetFlown(vessel);
            bool fresh = impact.usable && !double.IsNaN(settled_UT) && impact.snapshot_UT >= settled_UT
                && !(impact.snapshot_UT <= last_used_snapshot_UT);
            if (fresh && !heat_guard_on)
            {
                last_used_snapshot_UT = impact.snapshot_UT;
                last_steer_UT = now;
                Steer(impact, body, altitude, air_velocity.magnitude, now);
            }
            else if (now - last_steer_UT >= stalled_log_interval_s && now - last_stalled_log_UT >= stalled_log_interval_s)
            {
                last_stalled_log_UT = now;
                logger.LogInfo(string.Format(Inv, "[EntryGuidance] no steering for {0:0} s at {1:0} m: nose {2:0.0}° and roll {3:0.0}° off the attitude asked for, " +
                    "nose drifting {4:0.0}° a second | Redux {5}{6}",
                    now - last_steer_UT, altitude, hold.nose_error_deg, hold.roll_error_deg, hold.nose_drift_deg,
                    impact.usable ? $"prediction from {(now - impact.snapshot_UT).ToString("0", Inv)} s ago" : impact.state.ToString(),
                    heat_guard_on ? " | heat guard on" : ""));
            }

            status_line = heat_guard_on
                ? "Entry guidance: heat guard on, flying into the airflow"
                : $"Entry guidance: lift {DescribeLift()}, angle of attack {angle_of_attack_deg:n1}°, attitude error {hold.attitude_error_deg:n1}°";
        }

        // Over the top of a climb inside the air: held for the airflow it will be falling through,
        // without steering - there's too little air up there to matter, and Redux's prediction
        // assumes the attitude against the airflow here is kept all the way down, which it isn't.
        void CoastOverTheTop()
        {
            hold.HoldForDescent(steer_below_m, flown_aoa_deg, steering.lift_bank_deg + steering.tilt_offset_deg);
            status_line = $"Entry guidance: over the top, turning for the way down ({hold.nose_error_deg:n0}° to go)";
        }

        static bool Climbing(VesselComponent vessel, double now)
        {
            var model = new ImpactModel(vessel.mainBody, now);
            model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);
            return Vector3d.Dot(r, v) > 0;
        }

        // Above the atmosphere (a boost-back that climbed out of it): retrograde at the entry point
        // with no lift, at 1x.
        void CoastToAtmosphere(VesselComponent vessel, double now)
        {
            hold.HoldForEntry(0, 0);
            status_line = "Entry guidance: coasting to the atmosphere";
            LogCoast(vessel, now);
        }

        // Where Redux has the vessel coming down, every so often while nothing is being changed -
        // to see whether the prediction holds still on the way to the atmosphere.
        void LogCoast(VesselComponent vessel, double now)
        {
            if (!double.IsNaN(last_coast_log_UT) && now - last_coast_log_UT < coast_log_interval_s)
                return;
            var impact = ReduxImpactPrediction.GetFlown(vessel);
            if (!impact.usable || !hold.Settled(redux_attitude_tolerance_deg))
                return;
            last_coast_log_UT = now;
            Miss(impact, vessel.mainBody, out double along, out double across);
            logger.LogInfo(string.Format(Inv, "[EntryGuidance] coasting at {0:0} m: Redux lands {1:0} m along, {2:0} m across, impact T+{3:0} s",
                vessel.AltitudeFromRadius, along, across, impact.impact_UT - now));
        }

        // Miss split into along the direction of travel (positive: lands beyond the target) and
        // across it (positive: lands toward the "side" axis that side lift pushes to).
        void Miss(ReduxImpact impact, CelestialBodyComponent body, out double along, out double across)
        {
            var settings = landing.settings;
            Vector3d target_up = body.GetRelSurfaceNVector(settings.target_latitude.V, settings.target_longitude.V);
            Vector3d predicted_up = body.GetRelSurfaceNVector(impact.latitude, impact.longitude);
            SplitMiss(target_up, predicted_up, body.radius, hold.flow, hold.side, out along, out across);
        }

        // The ground miss from the target to the predicted point (both surface normals, in one
        // frame), along the flow's ground direction and across it (sign: toward `side`).
        public static void SplitMiss(Vector3d target_up, Vector3d predicted_up, double radius, Vector3d flow, Vector3d side,
            out double along, out double across)
        {
            Vector3d miss = (predicted_up - target_up) * radius;
            Vector3d along_dir = (flow - target_up * Vector3d.Dot(flow, target_up)).normalized;
            Vector3d across_dir = Vector3d.Cross(along_dir, target_up);
            if (Vector3d.Dot(across_dir, side) < 0)
                across_dir = -across_dir;
            along = Vector3d.Dot(miss, along_dir);
            across = Vector3d.Dot(miss, across_dir);
        }

        void Steer(ReduxImpact impact, CelestialBodyComponent body, double altitude, double speed, double now)
        {
            Miss(impact, body, out double redux_along, out double across);
            last_redux_along = redux_along;
            double flown_before = flown_aoa_deg;
            var step = steering.Steer(redux_along, across, shortfall_m, now, landing.settings.entry_angle_of_attack.V);

            logger.LogInfo(string.Format(Inv, "[EntryGuidance] alt {0:0} m speed {1:0} m/s | Redux lands {2:0} m along (aim {3:0} m long: {4:0} m off), {5:0} m across | " +
                "lift up {6:0.00} -> {7:0.00}, to the side {8:0.00} -> {9:0.00} (angle of attack {10:0.0}° -> {11:0.0}°) | {12:0} m per unit lift | " +
                "hottest part {13} at {14:P0} of its limit{15}",
                altitude, speed, redux_along, shortfall_m, step.along, step.across, step.old_up, step.new_up, step.old_side, step.new_side,
                flown_before, angle_of_attack_deg * Math.Min(1, Math.Sqrt(step.new_up * step.new_up + step.new_side * step.new_side)),
                steering.range_per_lift_m, heat.hottest_part, heat.hottest_fraction,
                burn_plan.found ? string.Format(Inv, " | burn in {0:0} s", burn_plan.handover_in_s) : ""));
            if (step.changed)
                settled_UT = double.NaN;
        }

        // The angle of attack is the setting (plus any boost while the lift is out of reach),
        // lowered while parts are heating toward their limits (tilting off the engine end exposes
        // side-mounted parts to the hot flow) and raised back once they cool.
        void UpdateAngleOfAttack(VesselComponent vessel, double now)
        {
            double wanted_aoa = steering.WantedAoa(landing.settings.entry_angle_of_attack.V);
            double wanted = Math.Min(wanted_aoa, heat.Update(vessel, now, angle_of_attack_deg, EntryLiftSteering.max_angle_of_attack_deg));
            if (Math.Abs(wanted - angle_of_attack_deg) < 0.01)
                return;

            logger.LogInfo(string.Format(Inv, "[EntryGuidance] angle of attack {0:0.0}° -> {1:0.0}°: hottest part {2} at {3:P0} of its limit, {4:P0} expected in {5:0} s",
                angle_of_attack_deg, wanted, heat.hottest_part, heat.hottest_fraction, heat.hottest_predicted, EntryHeatLimit.lookahead_s));
            angle_of_attack_deg = wanted;
            // A new attitude: wait for Redux to predict it, and the lift (so the range a unit of
            // vertical lift buys) has changed size, so the last step can't be compared with the next.
            settled_UT = double.NaN;
            steering.ForgetLastStep();
        }

        // The aerodynamic acceleration (everything but gravity, with the engines off), in the body
        // frame, measured from the change in velocity every accel_sample_s. True when a new
        // measurement is ready and the vessel was holding its attitude (so it belongs to that
        // attitude).
        bool MeasureAero(VesselComponent vessel, double now, out Vector3d accel)
        {
            accel = Vector3d.zero;
            IKeplerPatch orbit = vessel.Orbit;
            Vector3d u = orbit.GetOrbitalVelocityAtUTZup(now);
            if (double.IsNaN(previous_sample_UT))
            {
                previous_velocity = u;
                previous_sample_UT = now;
                return false;
            }
            double dt = now - previous_sample_UT;
            if (dt < accel_sample_s)
                return false;

            Vector3d p = orbit.GetRelativePositionAtUTZup(now);
            Vector3d gravity = p * (-vessel.mainBody.gravParameter / Math.Pow(p.magnitude, 3));
            Vector3d accel_zup = (u - previous_velocity) / dt - gravity;
            previous_velocity = u;
            previous_sample_UT = now;
            // The heat guard's thrust is not aerodynamic.
            if (!hold.Settled(redux_attitude_tolerance_deg) || heat_guard_on)
                return false;

            var body = vessel.mainBody;
            accel = body.transform.bodyFrame.ToLocalVector(
                new Vector(body.SimulationObject.transform.celestialFrame, new Vector3d(accel_zup.x, accel_zup.z, accel_zup.y)));
            return true;
        }

        // Re-plans the landing burn every burn_plan_interval_s once low enough, and keeps the
        // overshoot the steering aims for.
        void PlanBurn(VesselComponent vessel, Vector3d position, Vector3d air_velocity, double altitude, double now)
        {
            if (altitude > burn_planning_below_m || !planner.has_drag)
                return;
            if (!double.IsNaN(last_plan_UT) && now - last_plan_UT < burn_plan_interval_s)
                return;
            double dt = double.IsNaN(last_plan_UT) ? 0 : now - last_plan_UT;
            last_plan_UT = now;

            Vector3d up = position.normalized;
            double v_vertical = Vector3d.Dot(air_velocity, up);
            double v_horizontal = (air_velocity - up * v_vertical).magnitude;
            var settings = landing.settings;
            var burn_dV = landing.burn_dV;
            double max_accel = burn_dV.active_dv > 0 ? burn_dV.active_dv : burn_dV.full_dv;
            burn_plan = planner.Compute(AtmoLandingTiming.AirOf(vessel.mainBody), altitude - ground_altitude, ground_altitude, v_horizontal, v_vertical,
                max_accel, AtmoLandingBurn.final_height_m, AtmoLandingBurn.FinalSpeed(settings),
                steering.overshoot_out_of_reach ? last_redux_along : double.NaN);
            if (!burn_plan.found)
                return;

            if (!have_shortfall)
                shortfall_m = burn_plan.shortfall_m;
            else
                shortfall_m += (burn_plan.shortfall_m - shortfall_m) * Math.Min(1, dt / shortfall_smoothing_s);
            have_shortfall = true;
        }

        string DescribeLift()
        {
            double u = steering.vertical_lift;
            string vertical = u > 0.3 ? "up" : u < -0.3 ? "down" : "level";
            return $"{vertical}, {steering.side_lift:n2} to the side";
        }

        public override void UpdateInfoRows(Action<string, string> addRow)
        {
            if (!in_atmosphere || !SkySpaceAgencySettings.debug_mode.V)
                return;
            addRow("Lift Bank", $"{steering.lift_bank_deg:n0}°");
            addRow("Vertical Lift", $"{steering.vertical_lift:n2}");
            addRow("Side Lift", $"{steering.side_lift:n2}");
            addRow("Angle of Attack", $"{flown_aoa_deg:n1}°");
            addRow("Attitude Error", $"{hold.attitude_error_deg:n1}°");
            addRow("Hottest Part", $"{heat.hottest_fraction:P0} of max");
        }
    }
}

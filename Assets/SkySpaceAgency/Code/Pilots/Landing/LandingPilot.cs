using System;
using System.Diagnostics;
using K2D2.KSPService;
using KSP.Sim;
using KSP.Sim.DeltaV;
using KSP.Sim.impl;
using KTools;
// using KTools.UI;
using K2D2.Controller;
using K2D2.Node;
using K2D2.Landing.Braking;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Landing
{
    // The precision sequence (OrbitPrep → … → terminal guidance) is in LandingPilotPrecision.cs,
    // the parachute precision return (PlaneChange → … → Jettison → Parachute) in LandingPilotReentry.cs.
    public partial class LandingPilot : Pilot
    {
        public ILogger logger = SpaceWarp2.API.Logging.LogManager.GetLogger("SkySpaceAgency.LandingController");

        // Atmo/Vacuum profile split - see LandingSettings.cs's class comment. Two independent
        // instances, each backed by its own settings file (k2d2_landing_atmo.json /
        // k2d2_landing_vac.json). `settings` is a pass-through property, not a field, so every
        // read of settings.xxx.V throughout this file and the Landing controllers follows
        // whichever profile LandingProfile.IsAtmospheric currently says is active.
        internal LandingSettings settings_atmo;
        internal LandingSettings settings_vac;
        internal LandingSettings settings => LandingProfile.IsAtmospheric ? settings_atmo : settings_vac;

        // Parachute landing is chosen on the atmosphere profile only; on an airless body the
        // landing is always propulsive.
        internal bool parachute_mode =>
            LandingProfile.IsAtmospheric && settings_atmo.mode.V == LandingMethod.Parachute;

        public static LandingPilot Instance { get; set; }

        public KSPVessel current_vessel;

        public BurndV burn_dV = new BurndV();

        public WarpTo warp_to = new WarpTo();

        // Passed to TouchDown so its closed-loop steering (see TouchDown.ComputeSteeredDirection)
        // can read predicted_landing_lat/lon, settings.target_latitude/longitude, and altitude
        // directly off this pilot. Constructed in the constructor body, not as a field
        // initializer - 'this' isn't available there.
        public TouchDown brake;

        // Mode.Parachute's controller (Controlers/ParachuteDescent.cs).
        public ParachuteDescent parachute_descent;

        // Landing legs (KSPService/LandingGear.cs): deployed on entering Touch Down (or Brake with
        // deploy_legs_early), or by ParachuteDescent once a canopy opens. One request per run.
        internal LandingGear gear = new LandingGear();

        public SingleExecuteController current_executor = new SingleExecuteController();

        public LandingPilot()
        {
            // Both created in K2D2_Plugin.OnInitialized(), before any pilot is constructed.
            var atmo_file = SettingsFile.Get("land_atmo");
            var vac_file = SettingsFile.Get("land_vac");
            settings_atmo = new LandingSettings(atmo_file, atmospheric: true);
            settings_vac = new LandingSettings(vac_file, atmospheric: false);
            brake = new TouchDown(this, atmo_file, vac_file);
            parachute_descent = new ParachuteDescent(this);
            jettison = new Jettison(this);
            stage_drop = new LandingStageDrop(this);
            _page = new LandingUI(this);

            Instance = this;
            debug_mode_only = false;

            K2D2PilotsMgr.Instance.RegisterPilot("Land", this);

            sub_contollers.Add(burn_dV);
            sub_contollers.Add(current_executor);

            // logger.LogMessage("LandingController !");
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }


        public enum Mode
        {
            Off,
            // nextMode() just does mode+1, so any phase inserted here needs to stay ahead of
            // Pause/QuickWarp/.../TouchDown, which keep their relative ordering either way.
            // Precision landing only (LandingPilotPrecision.cs): Calibrate waits for the rotation
            // probe and plans; the four burns are PrecisionPhase runs.
            Calibrate,
            OrbitPrep,
            PlaneChange,
            Deorbit,
            MidCourse,
            Pause,
            QuickWarp,
            RotationWarp,
            Waiting,
            Brake,
            TouchDown,
            // Parachute landing (ParachuteDescent), entered straight from Off, or after Jettison in
            // a parachute precision return. nextMode() never runs from it, and TouchDown never
            // falls through into it.
            Parachute,
            // Parachute precision return only (LandingPilotReentry.cs), after MidCourse: the coast to
            // the jettison, then the jettison itself. Left by explicit transitions, never nextMode().
            ReentryCoast,
            Jettison
        }

        public Mode mode = Mode.Off;


        double end_pause_Ut;

        public void setMode(Mode mode)
        {
            if (mode == this.mode)
                return;

            logger.LogInfo("setMode " + mode);

            this.mode = mode;

            // Precision commands to TouchDown last one mode at most (set again every frame).
            brake.precision_brake_throttle = float.NaN;
            brake.guidance_active = false;

            if (mode == Mode.Off)
            {
                TimeWarpTools.SetRateIndex(0, false);
                current_executor.setController(null);
                return;
            }
            switch (mode)
            {
                case Mode.Off:
                    current_executor.setController(null);
                    break;
                case Mode.Calibrate:
                    current_vessel.SetThrottle(0);
                    current_executor.setController(null);
                    break;
                case Mode.OrbitPrep:
                case Mode.PlaneChange:
                case Mode.Deorbit:
                case Mode.MidCourse:
                    StartPrecisionPhase(mode);
                    break;
                case Mode.Pause:
                    end_pause_Ut = GeneralTools.Current_UT + settings.pause_time;
                    current_vessel.SetThrottle(0);
                    break;
                case Mode.QuickWarp:
                    current_vessel.SetThrottle(0);
                    if (!settings.auto_warp.V)
                        setMode(Mode.Waiting);
                    else
                    {
                        current_executor.setController(warp_to);
                        warp_to.Start_Retrograde(startSafeWarp_UT);
                        warp_to.max_warp_index = 6;
                    }
                    break;
                case Mode.RotationWarp:
                    current_vessel.SetThrottle(0);
                    if (!settings.auto_warp.V)
                        setMode(Mode.Waiting);
                    else
                    {
                        current_executor.setController(warp_to);
                        warp_to.Start_Retrograde(startBurn_UT, true);
                        warp_to.max_warp_index = 2;
                    }
                    break;
                case Mode.Waiting:
                    current_vessel.SetThrottle(0);
                    current_executor.setController(null);
                    break;
                case Mode.Brake:
                case Mode.TouchDown:
                    // Clear a stale flag from a previous descent.
                    brake.cannot_stop = false;
                    // Legs out on entering Touch Down, or already on entering Brake with "Deploy
                    // legs early". LandingGear only acts on the first request of a run.
                    if (mode == Mode.TouchDown || settings.deploy_legs_early.V)
                        gear.Deploy(current_vessel.VesselComponent, $"entering {mode}");
                    current_executor.setController(brake);
                    if (precision_run)
                    {
                        if (mode == Mode.Brake)
                            OnPrecisionBrakeStart();
                        else
                            OnPrecisionHandOver();
                    }
                    break;
                case Mode.Parachute:
                    current_vessel.SetThrottle(0);
                    current_executor.setController(parachute_descent);
                    parachute_descent.Start();
                    break;
                case Mode.ReentryCoast:
                    StartReentryCoast();
                    break;
                case Mode.Jettison:
                    current_vessel.SetThrottle(0);
                    TimeWarpTools.SetRateIndex(0, false);
                    current_executor.setController(jettison);
                    jettison.Start();
                    break;
            }

            logger.LogInfo("current_pilot " + mode);
        }

        public void nextMode()
        {
            // start
            if (mode == Mode.Off)
            {
                isRunning = true;
                return;
            }

            // TouchDown and Parachute are both final phases: the run ends on contact. The reentry
            // coast and jettison leave by explicit transitions (LandingPilotReentry.cs).
            if (mode == Mode.TouchDown || mode == Mode.Parachute || ReentryMode(mode))
                return;

            var next = this.mode + 1;
            setMode(next);
        }

        bool _active = false;
        public override bool isRunning
        {
            get { return _active; }
            set
            {
                if (value == _active)  return;

                if (!value)
                {
                    // stop
                    if (current_vessel != null)
                        current_vessel.SetThrottle(0);

                    setMode(Mode.Off);
                    // A stopped run has no lower stage to drop any more.
                    staged_drops_left = 0;
                    stage_drop.Reset();
                    _active = false;
                }
                else
                {
                    // Refuse an impossible landing before touching anything: _active stays
                    // false, and LandingUI shows last_error and resets its button.
                    if (!CheckCanStart())
                    {
                        logger.LogInfo($"[Landing] start refused: {last_error}");
                        return;
                    }
                    last_error = "";

                    // Start total burn counter
                    burn_dV.reset();

                    // reset controller to desactivate other controllers.
                    K2D2_Plugin.ResetControllers();

                    _active = true;

                    // Precision landing (decided by CheckCanStart: precision_run) starts with its
                    // four planned burns (orbit preparation, plane change, deorbit, mid-course
                    // correction; each skipped when not needed), or with Calibrate when the rotation
                    // sign must be measured first, before falling into the normal
                    // Pause->QuickWarp->...->TouchDown sequence. A parachute precision return
                    // (reentry_run) has no orbit preparation and goes on to the jettison and the
                    // parachute descent.
                    gear.Reset();
                    net_start_ut = double.NaN;
                    if (parachute_mode && !precision_run)
                        setMode(Mode.Parachute);
                    else if (precision_run)
                        setMode(precision_calibrating ? Mode.Calibrate : reentry_run ? Mode.PlaneChange : Mode.OrbitPrep);
                    else
                        setMode(Mode.QuickWarp);
                }

                // send call backs
                base.isRunning = value;
            }
        }

        public override void onReset()
        {
            // The jettison split may change the active vessel to the capsule: not a reason to stop.
            // Only for a VesselChangedMessage reset; any other reset stops the landing as before.
            if (reset_by_vessel_change && KeepThroughVesselChange())
                return;
            isRunning = false;
            // Vessel change, scene change or another pilot's start: drop a pending leg check, it
            // must not fire against another vessel. (A landing's own end keeps it, see FinishLanding.)
            gear.Reset();
            // Same events end a map pick that is still waiting (no-op when none waits).
            (_page as LandingUI)?.CancelPick("reset: vessel or scene change, or a pilot start");
            // A stage choice belongs to the vessel it was made on.
            if (reset_by_vessel_change)
                landing_stage_choice = 0;
        }

        internal float current_falling_speed = 0;

        internal bool collision_detected = false;

        internal double adjusted_collision_UT = 0;
        internal double startBurn_UT = 0;
        internal double startSafeWarp_UT = 0;
        internal double speed_collision;
        internal double burn_duration;

        // Logging throttle for compute_real_collision() below - it runs every Update() frame, so
        // logging its result unconditionally is far too much log volume. Logs immediately
        // whenever converged flips true<->false, plus a periodic heartbeat regardless.
        bool collision_search_last_converged = true;
        int collision_search_log_counter = 0;
        const int CollisionSearchHeartbeatFrames = 60;

        // Precision landing (settings/UI side in LandingSettings.cs). Predicted lat/lon is
        // computed alongside the collision check below, since it already has the terrain-
        // crossing time and position. target_error_m is only meaningful once a target's been set
        // and precision_landing is on - it's purely informational, not used to steer anything.
        internal double predicted_landing_lat = 0;
        internal double predicted_landing_lon = 0;
        internal double target_error_m = 0;

        // Braking simulation (Braking/BrakeSimulator.cs). It runs dozens of simulated burns, so it
        // is refreshed every BrakeSimInterval seconds of real time, not every frame.
        internal BrakeResult brake_result;
        internal bool brake_result_valid = false;
        float next_brake_sim_time = 0;
        const float BrakeSimInterval = 0.5f;
        const float StagedSimInterval = 1.5f;
        // The collision search reports no collision on every non-converged frame: keep the last
        // result for this many ticks (4 x 0.5 s) instead of flipping to the legacy start.
        const int BrakeSimKeepTicks = 4;
        int brake_sim_no_collision_ticks = 0;
        BrakeStatus last_logged_brake_status = (BrakeStatus)(-1);
        float next_brake_log_time = 0;
        double last_brake_sim_ms = 0;

        // Sign of the body rotation in the Zup frame (BodyRotation.ChooseSign), calibrated once
        // per body against the surface speed the game measures.
        internal int rotation_sign = 1;
        internal string rotation_sign_body = null;
        // The two measurements behind it, kept apart for the cross-check (RotationProbe.Disagree):
        // the surface-speed calibration and the rotation probe, each with the body it measured.
        internal int speed_sign = 0;
        internal string speed_sign_body = null;
        internal int probe_sign = 0;
        internal string probe_sign_body = null;

        // Forgets both measurements (and the sign they set) so that the next start measures again.
        internal void ClearRotationMeasurements()
        {
            speed_sign = 0;
            speed_sign_body = null;
            probe_sign = 0;
            probe_sign_body = null;
            rotation_sign = 1;
            rotation_sign_body = null;
            probe_body = null;
            probe_failure = null;
        }

        internal int SpeedSignFor(string body) => speed_sign_body == body ? speed_sign : 0;
        internal int ProbeSignFor(string body) => probe_sign_body == body ? probe_sign : 0;

        // Why the last start was refused, or why the last run stopped. Shown until the next
        // successful start.
        internal string last_error = "";
        // Start-check warning that does not refuse (unknown Δv). Shown while running.
        internal string last_warning = "";

        public void computeValues()
        {
            collision_detected = false;
            var current_vessel = K2D2_Plugin.Instance.current_vessel;
            if (current_vessel == null)
            {
                // UI_Tools.Console("no vessel");
                return;
            }

            // VesselComponent.Orbit can be a CurrentPatchedConicsOrbit for the actively-flown
            // vessel, not just PatchedConicsOrbit, so a hard cast throws InvalidCastException.
            // GetOrbitalVelocityAtUTZup is on IOrbit (which IKeplerPatch extends), so no concrete
            // cast is needed here.
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;

            collision_detected = compute_real_collision();
            speed_collision = orbit.GetOrbitalVelocityAtUTZup(adjusted_collision_UT).magnitude;
            // Legacy estimate, still used by precision landing's floor; 0 (not infinity) without thrust.
            burn_duration = burn_dV.active_dv > 0 ? speed_collision / burn_dV.active_dv : 0;

            UpdateBrakeSimulation(force: false);
            compute_startBurn();
        }

        public void compute_startBurn()
        {
            double burn_before = settings.burn_before.V;

            startBurn_UT = adjusted_collision_UT - burn_duration - burn_before;
            double now_ut = GeneralTools.Game.UniverseModel.UniverseTime;

            // The braking simulation gives the real latest safe start (gravity, approach angle,
            // mass loss, terrain, body rotation); the player's "burn before" stays an extra margin.
            // A precision landing brakes exactly at the simulated start (its stop point is the
            // planned one only from there). TooLate / Impossible: brake now, at full thrust.
            if (brake_result_valid)
            {
                if (brake_result.Status == BrakeStatus.Ok)
                    startBurn_UT = PrecisionActive ? brake_result.StartUT : brake_result.StartUT - settings.burn_before.V;
                else
                {
                    startBurn_UT = now_ut;
                }
                burn_duration = brake_result.BurnDuration;
            }

            // Backstop: whatever combination of the floors above, never schedule the burn as
            // already overdue. WarpTo silently no-ops the instant its target time is in the past,
            // so an overshot start wouldn't just start the burn a bit early, it would skip the
            // warp entirely and force a real-time wait for however long was actually left.
            // Clamping here means the worst case is "start braking immediately".
            if (startBurn_UT < now_ut)
                startBurn_UT = now_ut;

            startSafeWarp_UT = startBurn_UT - settings.rotation_warp_duration.V;
        }

        public bool compute_real_collision()
        {
            // start in 2 minutes
            double start_time = GeneralTools.Game.UniverseModel.UniverseTime + 2 * 60;
            bool collide = false;

            // Frame handling for the terrain check below:
            //  1. orbit.GetRelativePositionAtUTZup(ut) returns a Vector3d already relative to the
            //     orbit's reference body, but in "Zup" convention (Z is "up", standard orbital-
            //     mechanics axis order), not Unity's Y-up - its Y/Z components need swapping
            //     before use with Position/Vector. GetOrbitalVelocityAtUTZup has the same
            //     convention, but it's only ever consumed via .magnitude, which doesn't care
            //     about axis order.
            //  2. That swapped vector needs to be paired with
            //     body.SimulationObject.transform.celestialFrame, not body.coordinateSystem.
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;
            double current_time_ut = GeneralTools.Game.UniverseModel.UniverseTime;
            // Coarse step for the initial forward walk below, and the starting half-step size for
            // the flip-and-refine bisection once a below-terrain sample is hit. Narrow enough
            // (with max_occurrences sized to match) that the search can't alias between two
            // different valid terrain crossings roughly one orbital period apart: right after a
            // deorbit burn, the still-coasting orbit can dip below terrain both on the current
            // pass (near) and again a full period later (far), and a too-wide forward step can
            // walk clean over a narrow near dip without ever sampling inside it. Since this search
            // restarts fresh from current_ut+120s every frame, aliasing onto the far crossing
            // instead of the near one would send the auto-warp target jumping between "burn very
            // soon" and "burn a full orbit later" from frame to frame.
            double deltaTime = 20;
            int max_occurrences = 300;
            double time = start_time;
            double terrainAltitude = 0;
            // True only once a sample actually lands within 1m of the terrain (the loop's own
            // break condition below) - false if the loop runs out of iterations without narrowing
            // that far (no below-terrain sample found within the search's ~6,000s reach, or one
            // found but the bisection didn't finish). Guards against overwriting
            // adjusted_collision_UT/target_error_m with a half-finished result - see below.
            bool converged = false;

            float radius = current_vessel.VesselComponent.SimulationObject.objVesselBehavior.BoundingSphere.radius;

            for (int i = 0; i < max_occurrences; i++)
            {
                Vector3d rel_pos_zup = orbit.GetRelativePositionAtUTZup(time);
                // Zup -> Yup: swap Y and Z before this is usable as a Position's local vector.
                Vector3d rel_pos = new Vector3d(rel_pos_zup.x, rel_pos_zup.z, rel_pos_zup.y);
                Position ps = new Position(body.SimulationObject.transform.celestialFrame, rel_pos);
                double sceneryOffset;

                body.GetAltitudeFromTerrain(ps, out terrainAltitude, out sceneryOffset);
                // terrainAltitude -= radius;

                if (terrainAltitude < 0)
                {
                    collide = true;
                    if (deltaTime > 0)
                    {
                        // dychotomy
                        deltaTime = -deltaTime / 2;
                    }
                    time += deltaTime;
                }
                else
                {
                    if (deltaTime < 0)
                    {
                        // dychotomy
                        deltaTime = -deltaTime / 2;
                    }
                    time += deltaTime;
                }

                if (Math.Abs(terrainAltitude) < 1)
                {
                    converged = true;
                    break;
                }
            }

            // See collision_search_last_converged's own comment for why this is throttled instead
            // of unconditional.
            bool convergence_changed = converged != collision_search_last_converged;
            collision_search_last_converged = converged;
            collision_search_log_counter++;
            if (convergence_changed || collision_search_log_counter % CollisionSearchHeartbeatFrames == 0)
            {
                logger.LogInfo($"compute_real_collision: collide={collide} converged={converged} final terrainAltitude={terrainAltitude:n1} adjusted_collision_UT+{time - current_time_ut:n0}s");
            }

            if (!converged)
            {
                // The loop above ran out of iterations without landing within 1m of the terrain -
                // either no below-terrain sample was found at all, or one was found but the
                // bisection didn't finish. Either way this frame's result isn't trustworthy enough
                // to drive the warp schedule, so keep the last good prediction instead of
                // overwriting it. Only log the FIRST frame of a new not-converged streak - logging
                // every frame of a whole streak is exactly the spam this throttling avoids.
                if (convergence_changed)
                {
                    logger.LogInfo($"compute_real_collision: search did not converge this frame - keeping previous prediction " +
                        $"(adjusted_collision_UT+{adjusted_collision_UT - current_time_ut:n0}s, target_error_m={target_error_m:n1}) instead of an unreliable one.");
                }
                return collision_detected;
            }

            // Belt-and-suspenders on top of the narrower search step above, only while genuinely
            // coasting unpowered toward the deorbit impact point (Pause/QuickWarp/RotationWarp/
            // Waiting) - NOT during Circularize/DeorbitBurn/Brake/TouchDown, where the vessel's own
            // thrust deliberately changes the orbit and a jump in predicted impact time (a braking
            // burn pushing it later, say) is expected, not a bug. While coasting, nothing changes
            // the orbit, so the same physical crossing should only ever get SOONER as
            // current_time_ut advances - a jump forward by more than half an orbital period
            // almost certainly means this frame's search skipped past the real near crossing and
            // landed on a later one. Only meaningful when the orbit is actually bound (a
            // hyperbolic capture trajectory has no "next orbit" to alias onto).
            bool coasting_unpowered = mode == Mode.Pause || mode == Mode.QuickWarp
                || mode == Mode.RotationWarp || mode == Mode.Waiting;
            if (coasting_unpowered && adjusted_collision_UT > 0)
            {
                Vector3d r_now = orbit.GetRelativePositionAtUTZup(current_time_ut);
                Vector3d v_now = orbit.GetOrbitalVelocityAtUTZup(current_time_ut);
                double period = LandingTargeting.OrbitalPeriodFromStateVectors(r_now, v_now, body.gravParameter);

                if (!double.IsNaN(period) && period > 0 && time > adjusted_collision_UT + 0.5 * period)
                {
                    logger.LogInfo($"compute_real_collision: new prediction (adjusted_collision_UT+{time - current_time_ut:n0}s) jumped more than " +
                        $"half an orbit ({period:n0}s) later than the last one (adjusted_collision_UT+{adjusted_collision_UT - current_time_ut:n0}s) - " +
                        "looks like the search locked onto a later orbit's crossing instead of the nearer one, keeping the previous prediction.");
                    return collision_detected;
                }
            }

            adjusted_collision_UT = time;

            // Predicted landing lat/lon, reusing the same Zup->Yup swap and celestialFrame-
            // relative Position as above, handed to GetLatLonAltFromRadius. target_error_m is
            // straight-line lat/lon (haversine) against the player's chosen target, not a 3D
            // position diff, to avoid needing to also confirm which lat/lon <-> position
            // conversion (GetSurfacePosition vs GetRelSurfacePosition) applies body rotation.
            Vector3d final_rel_pos_zup = orbit.GetRelativePositionAtUTZup(time);
            Vector3d final_rel_pos = new Vector3d(final_rel_pos_zup.x, final_rel_pos_zup.z, final_rel_pos_zup.y);
            Position final_position = new Position(body.SimulationObject.transform.celestialFrame, final_rel_pos);
            body.GetLatLonAltFromRadius(final_position, out predicted_landing_lat, out predicted_landing_lon, out _);

            if (settings.precision_landing.V)
            {
                target_error_m = HaversineDistanceMeters(predicted_landing_lat, predicted_landing_lon,
                    settings.target_latitude.V, settings.target_longitude.V, body.radius);
            }

            return collide;
        }

        // Great-circle distance between two lat/lon points on a sphere of the given radius. Used
        // for target_error_m instead of a 3D position diff. internal (not private) so
        // LandingTargeting.cs's deorbit search can score candidates on the same distance metric.
        internal static double HaversineDistanceMeters(double lat1, double lon1, double lat2, double lon2, double radius)
        {
            double toRad = Math.PI / 180.0;
            double dLat = (lat2 - lat1) * toRad;
            double dLon = (lon2 - lon1) * toRad;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1 * toRad) * Math.Cos(lat2 * toRad) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return radius * c;
        }

        // Runs the braking simulation from the current state, at most every BrakeSimInterval
        // (unless forced). The result is kept for up to BrakeSimKeepTicks ticks after the last
        // detected collision, then brake_result_valid goes false. Unforced calls do nothing in
        // TouchDown and Parachute, which do not read the result.
        void UpdateBrakeSimulation(bool force)
        {
            if (!force && (mode == Mode.TouchDown || mode == Mode.Parachute || ReentryMode(mode)))
                return;

            float now_real = UnityEngine.Time.realtimeSinceStartup;
            if (!force && now_real < next_brake_sim_time)
                return;
            next_brake_sim_time = now_real + BrakeSimInterval;

            if (!collision_detected || current_vessel?.VesselComponent == null)
            {
                if (force)
                {
                    // The start check must never use a stale result.
                    brake_result_valid = false;
                    brake_sim_no_collision_ticks = 0;
                }
                else if (++brake_sim_no_collision_ticks > BrakeSimKeepTicks)
                    brake_result_valid = false;
                return;
            }
            brake_sim_no_collision_ticks = 0;

            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;
            double now = GeneralTools.Current_UT;
            Vector3d r0 = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v0 = orbit.GetOrbitalVelocityAtUTZup(now);
            double omega = BodyRotation.AngularSpeed(body.rotationPeriod);
            UpdateRotationSign(body, r0, v0, omega);

            var input = new BrakeInput
            {
                Position = r0,
                Velocity = v0,
                UT = now,
                Mu = body.gravParameter,
                BodyRadius = body.radius,
                AngularVelocity = BodyRotation.AngularVelocity(omega, rotation_sign),
                ThrustKN = burn_dV.active_thrust,
                MassT = burn_dV.mass,
                Isp = burn_dV.active_isp,
                ImpactUT = adjusted_collision_UT,
                TerrainHeight = p => TerrainHeightAt(body, p),
                // A precision landing brakes to a stop at the hand-over height (its plans do).
                StopHeight = PrecisionActive ? settings.start_touchdown_altitude.V : 0,
            };
            var sim_timer = Stopwatch.StartNew();
            // Landing with a chosen stage: the lower stages brake first (StagedBrakePlan). The
            // plan simulates many more burns: it runs less often.
            bool staged = TryStagedPlan(input, out StagePlan plan);
            if (staged)
            {
                brake_result = plan.Brake;
                next_brake_sim_time = now_real + StagedSimInterval;
            }
            else
                brake_result = BrakeSimulator.FindStart(input);
            sim_timer.Stop();
            last_brake_sim_ms = sim_timer.Elapsed.TotalMilliseconds;
            brake_result_valid = true;
            if (staged)
                UseStagePlan(plan, now);
            LogBrakeResult(now);
        }

        internal void UpdateRotationSign(CelestialBodyComponent body, Vector3d r0, Vector3d v0, double omega)
        {
            if (rotation_sign_body == body.Name && speed_sign_body == body.Name)
                return;
            if (rotation_sign_body != body.Name)
            {
                // Another body: back to the +1 default, and not calibrated (a stale name would let
                // a return to the earlier body skip its calibration with this body's sign).
                rotation_sign = 1;
                rotation_sign_body = null;
            }
            if (speed_sign_body != body.Name)
            {
                // Too slow, or a polar orbit: both signs predict the same speed. Retry later.
                if (!BodyRotation.IsDecisive(r0, v0, omega))
                    return;
                double measured = current_vessel.VesselVehicle.SurfaceSpeed;
                // A non-finite measurement says nothing: calibrate again later.
                if (double.IsNaN(measured) || double.IsInfinity(measured))
                    return;
                speed_sign = BodyRotation.ChooseSign(r0, v0, omega, measured);
                speed_sign_body = body.Name;
                int probe = ProbeSignFor(body.Name);
                logger.LogInfo($"[Landing] rotation sign for {body.Name}: {speed_sign} " +
                    $"(measured surface speed {measured:n1} m/s, ω {omega:e3} rad/s)" +
                    (probe != 0 ? $" | rotation probe: {probe}{(Precision.RotationProbe.Disagree(speed_sign, probe) ? " - DISAGREE" : "")}" : ""));
            }
            // A sign the probe really measured stays in use (a disagreement refuses the precision
            // landing, PrecisionContext); otherwise the surface-speed one (also on a return to a
            // body measured before).
            if (rotation_sign_body == body.Name && probe_sign_body == body.Name)
                return;
            rotation_sign = speed_sign;
            rotation_sign_body = body.Name;
        }

        // Terrain height above the body radius under p (Zup, body-relative, current body
        // orientation), with the same Zup -> Yup swap and frame as compute_real_collision.
        internal static double TerrainHeightAt(CelestialBodyComponent body, Vector3d p_zup)
        {
            Vector3d p_yup = new Vector3d(p_zup.x, p_zup.z, p_zup.y);
            Position ps = new Position(body.SimulationObject.transform.celestialFrame, p_yup);
            body.GetAltitudeFromTerrain(ps, out double above_terrain, out double scenery_offset);
            return p_zup.magnitude - body.radius - above_terrain;
        }

        void LogBrakeResult(double now)
        {
            float now_real = UnityEngine.Time.realtimeSinceStartup;
            if (brake_result.Status == last_logged_brake_status && now_real < next_brake_log_time)
                return;
            last_logged_brake_status = brake_result.Status;
            next_brake_log_time = now_real + 5;
            logger.LogInfo($"[Landing] brake sim: {brake_result.Status} start in {brake_result.StartUT - now:n1}s, " +
                $"burn {brake_result.BurnDuration:n1}s, Δv {brake_result.DeltaVNeeded:n0} m/s, stop {brake_result.StopAltitude:n0} m above terrain | " +
                $"thrust {burn_dV.active_thrust:n1} kN, mass {burn_dV.mass:n2} t, Isp {burn_dV.active_isp:n0} s, rotation sign {rotation_sign}, " +
                $"sim {last_brake_sim_ms:n1} ms");
        }

        // Speed the descent must not exceed at this height: the player's profile, capped by what
        // the active engines can still stop (DescentEnvelope).
        float limit_speed(float height)
        {
            double gravity = current_vessel.VesselComponent.graviticAcceleration.magnitude;
            return (float)DescentEnvelope.MaxSpeed(height, burn_dV.active_dv, gravity,
                settings.touch_down_speed.V, settings.compute_limit_speed(height));
        }

        // Start checks (LandingFeasibility), on fresh data. Sets last_error / last_warning.
        bool CheckCanStart()
        {
            last_warning = "";
            // Any start clears the previous precision landing's final message.
            precision_final_message = null;
            if (current_vessel?.VesselComponent == null || current_vessel.VesselVehicle == null)
            {
                last_error = "No active vessel.";
                return false;
            }

            // Refresh the profile now: the mode, and so the checks, depend on it. The engine,
            // TWR and Δv checks below do not apply to a parachute landing.
            LandingProfile.Update(current_vessel.currentBody());

            // Both modes: nothing to land when already on the ground.
            if (current_vessel.VesselComponent.LandedOrSplashed)
            {
                last_error = "Already landed.";
                return false;
            }

            precision_run = false;
            precision_calibrating = false;
            reentry_run = false;
            if (parachute_mode)
            {
                if (!CheckCanStartParachute())
                    return false;
                // Parachute precision return: its own plans and checks (LandingPilotReentry.cs).
                return settings_atmo.precision_landing.V ? CheckCanStartReentry() : true;
            }

            burn_dV.Compute_Thrust();
            LogEngines();
            // Landing with a chosen stage: the stage list, the landing stage and the drops.
            if (!PrepareStagedRun(out double landing_accel))
                return false;
            // Precision landing on an airless body: its own plans and Δv check (all phases).
            if (settings.precision_landing.V && !LandingProfile.IsAtmospheric)
            {
                bool ok = CheckCanStartPrecision();
                if (!ok)
                    ClearStagedRun();
                return ok;
            }
            var body = current_vessel.VesselComponent.Orbit.referenceBody;
            double surface_gravity = body.gravParameter / (body.radius * body.radius);

            collision_detected = compute_real_collision();
            UpdateBrakeSimulation(force: true);
            BrakeResult? brake = brake_result_valid ? brake_result : (BrakeResult?)null;

            var delta_v = current_vessel.VesselComponent.VesselDeltaV;
            double dv_remaining = delta_v != null ? delta_v.TotalDeltaVActual : double.NaN;

            // Staged: the TWR check is the landing stage's, and the Δv check is the plan's (per stage).
            bool staged = StagedBraking;
            if (staged && brake.HasValue && stage_plan_valid && !stage_plan.Ok)
            {
                logger.LogInfo($"[Landing] start check: staged plan refused: {stage_plan.Describe()}");
                last_error = stage_plan.Error;
                ClearStagedRun();
                return false;
            }
            var check = LandingFeasibility.Check(landing_accel, surface_gravity, brake, staged ? double.PositiveInfinity : dv_remaining);
            logger.LogInfo($"[Landing] start check: ok={check.Ok} accel {landing_accel:n2} m/s²{(staged ? " (landing stage)" : "")}, surface g {surface_gravity:n2}, " +
                $"collision {collision_detected}, sim {(brake.HasValue ? brake.Value.Status.ToString() : "none")}, Δv {dv_remaining:n0} m/s" +
                (staged ? $", staged plan {(stage_plan_valid ? stage_plan.Describe() : "not made yet (no collision)")}" : "") +
                (check.Ok ? "" : $" -> {check.Error}"));

            if (!check.Ok)
            {
                last_error = check.Error;
                ClearStagedRun();
                return false;
            }
            last_warning = check.Warning ?? "";
            start_stage_plan = stage_plan;
            start_stage_plan_valid = staged && stage_plan_valid;
            return true;
        }

        // Parachute landing start checks (ParachuteFeasibility). Periapsis from the state
        // vectors, the same way Circularize.CheckOrbit computes it (also valid for an escape
        // trajectory).
        bool CheckCanStartParachute()
        {
            var vessel = current_vessel.VesselComponent;
            IKeplerPatch orbit = vessel.Orbit;
            var body = orbit.referenceBody;
            double now = GeneralTools.Current_UT;
            LandingTargeting.OrbitalElementsFromStateVectors(orbit.GetRelativePositionAtUTZup(now),
                orbit.GetOrbitalVelocityAtUTZup(now), body.gravParameter,
                out _, out _, out double periapsis_radius);
            double periapsis_alt = periapsis_radius - body.radius;
            ParachuteCounts chutes = Parachutes.Count(vessel);

            var check = ParachuteFeasibility.Check(body.hasAtmosphere, body.atmosphereDepth,
                vessel.IsInAtmosphere, periapsis_alt, chutes.Usable);
            logger.LogInfo($"[Landing] parachute start check: ok={check.Ok} atmosphere {body.hasAtmosphere} " +
                $"(depth {body.atmosphereDepth:n0} m), in atmosphere {vessel.IsInAtmosphere}, Pe {periapsis_alt:n0} m, {chutes}" +
                (check.Ok ? "" : $" -> {check.Error}"));

            if (!check.Ok)
            {
                last_error = check.Error;
                return false;
            }
            return true;
        }

        // One line per engine, to check the active-engine filter against the game's real flags.
        void LogEngines()
        {
            var engines = current_vessel.VesselComponent.VesselDeltaV?.EngineInfo;
            if (engines == null)
            {
                logger.LogInfo("[Landing] engine: no engine info");
                return;
            }
            for (int i = 0; i < engines.Count; i++)
            {
                DeltaVEngineInfo info = engines[i];
                var engine = info?.Engine;
                if (engine == null)
                {
                    logger.LogInfo($"[Landing] engine {i}: null");
                    continue;
                }
                logger.LogInfo($"[Landing] engine {i}: ignited={engine.EngineIgnited} shutdown={engine.EngineShutdown} " +
                    $"operational={engine.IsOperational} starved={engine.IsPropellantStarved} staged={engine.staged} " +
                    $"active={BurndV.IsActive(info)}");
            }
        }

        void StopWithError(string reason)
        {
            logger.LogWarning($"[Landing] stopped: {reason}");
            if (precision_run)
                logger.LogWarning($"[Landing] precision: stopped in {mode}: {reason} | Δv used {burn_dV.burned_dV:n1} m/s ({string.Join(", ", phase_log)})");
            last_error = reason;
            isRunning = false;
            gear.Reset();
        }

        // End of a landing, whichever the mode: no thrust, SAS holding attitude, autopilot off.
        // Nothing relights the engines afterwards, even on a bounce or a slide.
        void FinishLanding(bool landed_or_splashed)
        {
            logger.LogInfo($"[Landing] touchdown in {mode}: " +
                (landed_or_splashed ? "LandedOrSplashed" : "altitude/fall-speed net") +
                $", altitude {altitude:n1} m, fall speed {current_falling_speed:n2} m/s");
            current_vessel.SetThrottle(0);
            LogStagedFinish();
            if (reentry_run)
                OnReentryFinish();
            else if (precision_run)
                OnPrecisionFinish();
            // Release the last aim: setAutoPilot does nothing when SAS is already in
            // StabilityAssist, which would leave fly-by-wire on the final descent's tilted target.
            var autopilot = current_vessel.Autopilot;
            if (autopilot != null)
            {
                autopilot.SAS.DisconnectFlyByWire();
                autopilot.SetMode(AutopilotMode.StabilityAssist);
                autopilot.Enabled = true;
                logger.LogInfo("[Landing] end: SAS released to StabilityAssist");
            }
            isRunning = false;
        }
        // Start (game time) of the continuous "near the ground and almost still" condition of the
        // altitude/fall-speed net in Update; NaN while the condition does not hold.
        double net_start_ut = double.NaN;
        const double NetHoldSeconds = 1.0;

        Vector SurfaceVelocity;
        public override void Update()
        {
            // Leg re-check and fallback (LandingGear). First, before the visibility test, so a
            // check pending at touchdown completes on time even with the Land tab hidden.
            gear.Update(current_vessel?.VesselComponent);

            if (!page.isVisible && !isRunning) return;
            if (current_vessel == null || current_vessel.VesselVehicle == null)
                return;

            // Keep the Atmo/Vacuum profile switch current - single source of truth every
            // settings/TouchDown pass-through property reads. Cheap no-op most frames.
            LandingProfile.Update(current_vessel.currentBody());

            // Rotation sign by the rotation probe, while precision landing may need it.
            UpdateSignProbe();

            altitude = (float)current_vessel.GetApproxAltitude();

            SurfaceVelocity = current_vessel.VesselVehicle.SurfaceVelocity;
            SurfaceVelocity.Reframe(current_vessel.VesselVehicle.Up.coordinateSystem);
            current_falling_speed = (float)-SurfaceVelocity.vector.y;

            // detect collision and compute time to burn
            computeValues();

            if (!collision_detected)
            {
                // Once close enough to the ground, "no predicted collision" usually just means
                // the patched-conics bisection search in compute_real_collision() can't resolve
                // one this close anymore, not that the danger is gone - falling back straight to
                // Touch Down is correct there. But collision_detected can also blip false for a
                // single frame much higher up in the descent (the search is fragile, and an
                // active Brake burn keeps changing the coasting orbit it extrapolates from every
                // frame), so gating the fallback behind the touchdown-altitude threshold means a
                // transient false negative higher up just gets re-checked next frame, while the
                // legitimate close-to-the-ground case still falls back as intended.
                //
                // This also naturally covers the DeorbitBurn phase: while still safely in orbit
                // waiting on/flying the phasing node, altitude is far above
                // start_touchdown_altitude, so this branch does nothing until we're actually on a
                // collision course.
                if (isRunning)
                {
                    // A parachute landing has no Touch Down phase to fall back to.
                    if (mode != Mode.Parachute && !ReentryMode(mode) && altitude < settings.start_touchdown_altitude.V)
                        setMode(Mode.TouchDown);
                }
                else
                {
                    // no more collision
                    isRunning = false;
                }
            }

            if (!isRunning)
                return;

            // Landing detection, common to both modes: the game's own LandedOrSplashed, with the
            // old altitude/fall-speed test kept as a net.
            // The net only fires once the vessel has stayed near the ground at (or below) half the
            // contact floor for a full NetHoldSeconds: the final descent flies at the floor speed
            // on purpose, so a speed-only test would cut the engine while still flying.
            bool landed = current_vessel.VesselComponent.LandedOrSplashed;
            bool net_condition = altitude < 5 && current_falling_speed < FinalDescent.MinContactSpeed / 2;
            double net_now = GeneralTools.Current_UT;
            if (!net_condition)
                net_start_ut = double.NaN;
            else if (double.IsNaN(net_start_ut))
                net_start_ut = net_now;
            bool net_held = net_condition && net_now - net_start_ut >= NetHoldSeconds;
            if (landed || net_held)
            {
                FinishLanding(landed);
                return;
            }
            // Landing with a chosen stage: drop the lower stage when dry or at the drop height.
            UpdateStageDrops();
            if (mode == Mode.Calibrate)
            {
                UpdateCalibrate();
                return;
            }
            if (PrecisionActive)
            {
                if (reentry_run)
                    UpdateReentryGuarded();
                else
                    UpdatePrecisionReadout();
            }
            if (mode == Mode.Pause)
            {
                if (GeneralTools.Current_UT > end_pause_Ut)
                {
                    setMode(Mode.QuickWarp);
                }
                return;
            }

            if (mode == Mode.QuickWarp)
            {
                warp_to.UT = startSafeWarp_UT;
            }
            else if (mode == Mode.RotationWarp)
            {
                warp_to.UT = startBurn_UT;
            }
            else if (mode == Mode.Waiting)
            {
                var dt = startBurn_UT - GeneralTools.Game.UniverseModel.UniverseTime;
                if (dt <= 0)
                {
                    setMode(Mode.Brake);
                    return;
                }
            }
            else if (mode == Mode.Brake)
            {
                brake.gravity_compensation = true;

                if (PrecisionActive)
                {
                    // Precision landing: one retrograde brake at the simulated throttle, down to a
                    // stop at start_touchdown_altitude (the plans' stop point), then the terminal
                    // guidance. No Pause / re-brake cycle.
                    UpdatePrecisionBrake();
                    if (Precision.PrecisionPhases.BrakeHandOver(current_vessel.VesselVehicle.SurfaceSpeed, burn_dV.active_dv,
                            altitude, settings.start_touchdown_altitude.V))
                    {
                        setMode(Mode.TouchDown);
                        return;
                    }
                }
                else
                {
                    brake.max_speed = 0;
                    // Total surface speed, not the vertical speed: max_speed = 0 drives the total
                    // to zero, and on a shallow approach most of the speed is horizontal, so the
                    // vertical speed is already below brake_speed on the first tick.
                    if (current_vessel.VesselVehicle.SurfaceSpeed < settings.brake_speed)
                    {
                        // we reached the speed to stop brake
                        // check next phase
                        if (altitude < settings.start_touchdown_altitude.V)
                        {
                            setMode(Mode.TouchDown);
                        }
                        else
                        {
                            // too high altitude retry.... very worng burn time ......
                            setMode(Mode.Pause);
                        }
                        return;
                    }
                }
            }
            else if (mode == Mode.TouchDown)
            {
                TimeWarpTools.SetRateIndex(0, false);
                brake.max_speed = limit_speed(altitude);
                // Final descent: never aim below the contact speed floor, or a Touch-Down speed
                // of 0 would hover instead of landing.
                if (FinalDescent.IsFinal(altitude))
                    brake.max_speed = Math.Max(brake.max_speed, (float)FinalDescent.ContactSpeed(settings.touch_down_speed.V));
                brake.gravity_compensation = true;
                if (PrecisionActive)
                    UpdatePrecisionGuidance();
            }

            // call the sub controllers
            base.Update();

            // A phase that cannot do its job (a refused plan or burn) stops the landing instead of
            // falling through to the next phase, except a precision burn: once the vessel may be
            // on an impact course it skips or lands without target (OnPrecisionPhaseFailed).
            if (current_executor.failed)
            {
                if (precision_run && PrecisionPhaseMode(mode))
                    OnPrecisionPhaseFailed(current_executor.failure_reason);
                else if (reentry_run && mode == Mode.Jettison)
                    OnJettisonFailed(current_executor.failure_reason);
                else
                    StopWithError(current_executor.failure_reason);
                return;
            }

            if (current_executor.finished)
            {
                if (PrecisionPhaseMode(mode))
                    OnPrecisionPhaseEnd();
                // A phase end may stop the run (a precision return whose deorbit fell short):
                // nextMode() from Off would start it again.
                if (!isRunning)
                    return;
                if (reentry_run && mode == Mode.MidCourse)
                    setMode(Mode.ReentryCoast);
                else if (reentry_run && mode == Mode.Jettison)
                    OnJettisonDone();
                else
                    // auto next
                    nextMode();
            }
        }

        public float altitude;


    }
}

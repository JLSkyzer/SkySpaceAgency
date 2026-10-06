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
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Landing
{
    public class LandingPilot : Pilot
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("SkySpaceAgency.LandingController");

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

        // Precondition phase for precision landing, run before deorbit_burn: circularizes the
        // starting orbit if it isn't already close to circular, or refuses if it's too high.
        // deorbit_burn's own math assumes a roughly circular starting orbit.
        public Circularize circularize = new Circularize();

        // Deorbit/phasing burn phase - see DeorbitBurn.cs.
        public DeorbitBurn deorbit_burn = new DeorbitBurn();

        // Small correction burn against the real post-deorbit-burn trajectory, run right after
        // deorbit_burn and before the normal Pause -> QuickWarp -> ... sequence, so the
        // descent-phase steering isn't left doing all the correcting close to the ground. See
        // MidCourseCorrection.cs.
        public MidCourseCorrection mid_course_correction = new MidCourseCorrection();

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
            Circularize,
            DeorbitBurn,
            MidCourseCorrection,
            Pause,
            QuickWarp,
            RotationWarp,
            Waiting,
            Brake,
            TouchDown,
            // Parachute landing (ParachuteDescent), entered straight from Off. Last on purpose:
            // nextMode() never runs from it, and TouchDown never falls through into it.
            Parachute
        }

        public Mode mode = Mode.Off;


        double end_pause_Ut;

        public void setMode(Mode mode)
        {
            if (mode == this.mode)
                return;

            logger.LogInfo("setMode " + mode);

            this.mode = mode;

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
                case Mode.Circularize:
                    current_executor.setController(circularize);
                    circularize.Start();
                    break;
                case Mode.DeorbitBurn:
                    current_executor.setController(deorbit_burn);
                    deorbit_burn.Start();
                    break;
                case Mode.MidCourseCorrection:
                    current_executor.setController(mid_course_correction);
                    mid_course_correction.Start();
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
                    break;
                case Mode.Parachute:
                    current_vessel.SetThrottle(0);
                    current_executor.setController(parachute_descent);
                    parachute_descent.Start();
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

            // TouchDown and Parachute are both final phases: the run ends on contact.
            if (mode == Mode.TouchDown || mode == Mode.Parachute)
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

                    // Precision landing starts with Circularize (a no-op if the orbit's already
                    // close enough to circular, or a refusal if it's too high - see Circularize.cs),
                    // then plans and flies a real, visible deorbit/phasing node, before falling
                    // into the normal Pause->QuickWarp->...->TouchDown sequence.
                    //
                    // !LandingProfile.IsAtmospheric is defense in depth, not the main gate - the
                    // Atmo profile's precision_landing setting has no UI control (see
                    // LandingUI.cs's atmo panel), so there's normally no way for it to read true
                    // here at all. This just guarantees Circularize/DeorbitBurn (which assume a
                    // vacuum trajectory) can never run on an atmospheric body even if that changes.
                    gear.Reset();
                    if (parachute_mode)
                        setMode(Mode.Parachute);
                    else if (settings.precision_landing.V && !LandingProfile.IsAtmospheric)
                        setMode(Mode.Circularize);
                    else
                        setMode(Mode.QuickWarp);
                }

                // send call backs
                base.isRunning = value;
            }
        }

        public override void onReset()
        {
            isRunning = false;
            // Vessel change, scene change or another pilot's start: drop a pending leg check, it
            // must not fire against another vessel. (A landing's own end keeps it, see FinishLanding.)
            gear.Reset();
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
        // The collision search reports no collision on every non-converged frame: keep the last
        // result for this many ticks (4 x 0.5 s) instead of flipping to the legacy start.
        const int BrakeSimKeepTicks = 4;
        int brake_sim_no_collision_ticks = 0;
        BrakeStatus last_logged_brake_status = (BrakeStatus)(-1);
        float next_brake_log_time = 0;
        double last_brake_sim_ms = 0;

        // Sign of the body rotation in the Zup frame (BodyRotation.ChooseSign), calibrated once
        // per body against the surface speed the game measures.
        int rotation_sign = 1;
        string rotation_sign_body = null;

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

            // TouchDown's closed-loop steering (see ComputeSteeredDirection) needs real time to
            // act before touchdown, not just the right direction - a correction attempted only in
            // the last couple seconds before collision barely moves a large miss, however
            // correctly aimed. Closing a lateral error needs lateral_deltaV * time_remaining >=
            // error, so back out how much EXTRA margin (beyond the efficiency-only burn_before
            // setting) buys enough time for the maximum lateral deltaV the steering angle cap
            // allows to actually close the gap - a bigger miss earns an earlier burn start, a
            // small one barely changes anything. First-pass heuristic (treats it as one lateral
            // kick + coast rather than modeling the whole burn), not exact, but ties "start
            // sooner/later" directly to how far off target we actually are.
            //
            // Uses whichever of the three correction caps is smallest (steering, extend, or
            // shorten), not steering_max_angle alone - target_error_m is a straight-line miss
            // distance and doesn't say whether the miss is left/right (heading's job) or long/
            // short (the arc correction's job, see TouchDown.ComputeSteeredDirection), and the arc
            // shorten cap is deliberately much tighter than the heading budget (tilting toward
            // horizontal spends braking margin). Sizing the margin off the worst case rather than
            // the best case keeps this correct even when the miss is mostly along-track.
            double min_correction_angle_deg = Math.Min(brake.steering_max_angle.V,
                Math.Min(brake.arc_extend_max_angle.V, brake.arc_shorten_max_angle.V));

            if (settings.precision_landing.V && target_error_m > 0 && min_correction_angle_deg > 0)
            {
                double min_correction_angle_rad = min_correction_angle_deg * Math.PI / 180.0;
                double lateral_dv_budget = speed_collision * Math.Sin(min_correction_angle_rad);

                if (lateral_dv_budget > 0.1) // avoid a near-zero budget blowing this up
                {
                    double correction_time_needed = target_error_m / lateral_dv_budget;
                    burn_before = Math.Max(burn_before, correction_time_needed);
                }
            }

            // Also keep real ALTITUDE margin above the touchdown phase's own threshold
            // (start_touchdown_altitude), not just enough TIME for the lateral correction above.
            // The lateral floor above only sizes itself off how big the miss is, so a well-aimed
            // deorbit burn (small target_error_m) could still leave the correction starting
            // uncomfortably close to start_touchdown_altitude. This floor doesn't care how big the
            // miss is - it always wants at least this much clearance - and Math.Max's with the
            // lateral floor above so whichever asks for more wins.
            //
            // Found by directly searching the propagated trajectory for the UT it actually
            // crosses target_start_altitude (LandingTargeting.PredictUTAtAltitude), not estimated
            // via a speed division - see PredictUTAtAltitude's own comment for why a speed-based
            // estimate has no good single choice (dividing by the full orbital speed
            // underestimates real descent time; dividing by vertical speed at the impact point
            // blows up near periapsis, where it's ~0 by definition). Directly searching for the
            // real crossing avoids both failure modes.
            // Gated on collision_detected - this search costs about the same as
            // compute_real_collision()'s own bisection (both run every Update() tick), so it's
            // skipped before there's even an impacting trajectory to search.
            if (settings.precision_landing.V && collision_detected)
            {
                double target_start_altitude = settings.start_touchdown_altitude.V
                    + LandingSettings.min_correction_altitude_margin;

                var current_vessel = K2D2_Plugin.Instance.current_vessel;
                if (current_vessel != null)
                {
                    IKeplerPatch startBurn_orbit = current_vessel.VesselComponent.Orbit;
                    var startBurn_body = startBurn_orbit.referenceBody;
                    double startBurn_now = GeneralTools.Current_UT;
                    Vector3d startBurn_r0 = startBurn_orbit.GetRelativePositionAtUTZup(startBurn_now);
                    Vector3d startBurn_v0 = startBurn_orbit.GetOrbitalVelocityAtUTZup(startBurn_now);

                    if (LandingTargeting.PredictUTAtAltitude(startBurn_r0, startBurn_v0, startBurn_body, startBurn_now,
                            target_start_altitude, out double crossingUT))
                    {
                        double altitude_lead_time = adjusted_collision_UT - crossingUT;
                        if (altitude_lead_time > 0)
                            burn_before = Math.Max(burn_before, altitude_lead_time);
                    }
                }
            }

            startBurn_UT = adjusted_collision_UT - burn_duration - burn_before;
            double now_ut = GeneralTools.Game.UniverseModel.UniverseTime;

            // The braking simulation gives the real latest safe start (gravity, approach angle,
            // mass loss, terrain, body rotation); the player's "burn before" stays an extra margin.
            // Precision landing keeps the earlier of the two, so its correction margins above
            // still apply. TooLate / Impossible: brake now, at full thrust.
            if (brake_result_valid)
            {
                if (brake_result.Status == BrakeStatus.Ok)
                {
                    double simulated = brake_result.StartUT - settings.burn_before.V;
                    startBurn_UT = settings.precision_landing.V ? Math.Min(startBurn_UT, simulated) : simulated;
                }
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
            if (!force && (mode == Mode.TouchDown || mode == Mode.Parachute))
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
            };
            var sim_timer = Stopwatch.StartNew();
            brake_result = BrakeSimulator.FindStart(input);
            sim_timer.Stop();
            last_brake_sim_ms = sim_timer.Elapsed.TotalMilliseconds;
            brake_result_valid = true;
            LogBrakeResult(now);
        }

        void UpdateRotationSign(CelestialBodyComponent body, Vector3d r0, Vector3d v0, double omega)
        {
            if (rotation_sign_body == body.Name)
                return;
            rotation_sign = 1;
            // Too slow, or a polar orbit: both signs predict the same speed. Keep +1, retry later.
            if (!BodyRotation.IsDecisive(r0, v0, omega))
                return;

            double measured = current_vessel.VesselVehicle.SurfaceSpeed;
            rotation_sign = BodyRotation.ChooseSign(r0, v0, omega, measured);
            // A non-finite measurement says nothing: keep +1 and calibrate again later.
            if (double.IsNaN(measured) || double.IsInfinity(measured))
                return;
            rotation_sign_body = body.Name;
            logger.LogInfo($"[Landing] rotation sign for {body.Name}: {rotation_sign} " +
                $"(measured surface speed {measured:n1} m/s, ω {omega:e3} rad/s)");
        }

        // Terrain height above the body radius under p (Zup, body-relative, current body
        // orientation), with the same Zup -> Yup swap and frame as compute_real_collision.
        static double TerrainHeightAt(CelestialBodyComponent body, Vector3d p_zup)
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
            if (current_vessel?.VesselComponent == null || current_vessel.VesselVehicle == null)
            {
                last_error = "No active vessel.";
                return false;
            }

            // Refresh the profile now: the mode, and so the checks, depend on it. The engine,
            // TWR and Δv checks below do not apply to a parachute landing.
            LandingProfile.Update(current_vessel.currentBody());
            if (parachute_mode)
                return CheckCanStartParachute();

            burn_dV.Compute_Thrust();
            LogEngines();
            var body = current_vessel.VesselComponent.Orbit.referenceBody;
            double surface_gravity = body.gravParameter / (body.radius * body.radius);

            collision_detected = compute_real_collision();
            UpdateBrakeSimulation(force: true);
            BrakeResult? brake = brake_result_valid ? brake_result : (BrakeResult?)null;

            var delta_v = current_vessel.VesselComponent.VesselDeltaV;
            double dv_remaining = delta_v != null ? delta_v.TotalDeltaVActual : double.NaN;

            var check = LandingFeasibility.Check(burn_dV.active_dv, surface_gravity, brake, dv_remaining);
            logger.LogInfo($"[Landing] start check: ok={check.Ok} accel {burn_dV.active_dv:n2} m/s², surface g {surface_gravity:n2}, " +
                $"collision {collision_detected}, sim {(brake.HasValue ? brake.Value.Status.ToString() : "none")}, Δv {dv_remaining:n0} m/s" +
                (check.Ok ? "" : $" -> {check.Error}"));

            if (!check.Ok)
            {
                last_error = check.Error;
                return false;
            }
            last_warning = check.Warning ?? "";
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
            SASTool.setAutoPilot(AutopilotMode.StabilityAssist);
            isRunning = false;
        }
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
                    if (mode != Mode.Parachute && altitude < settings.start_touchdown_altitude.V)
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
            bool landed = current_vessel.VesselComponent.LandedOrSplashed;
            if (landed || (altitude < 5 && current_falling_speed < 1))
            {
                FinishLanding(landed);
                return;
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

                if (settings.precision_landing.V)
                {
                    // Precision landing skips the brake-to-near-stop / Pause / re-brake cycle
                    // below entirely. That cycle repeatedly zeroes throttle (Mode.Pause does
                    // current_vessel.SetThrottle(0) outright), and TouchDown's closed-loop
                    // steering only runs while actually burning (see checkDirection), so every
                    // Pause cycle would kill the steering's authority right along with the
                    // throttle. Using the exact same speed-limit profile TouchDown itself uses
                    // turns Brake and TouchDown into one continuous burn split only by an altitude
                    // threshold, instead of two different behaviors, so steering stays live the
                    // whole way down.
                    brake.max_speed = limit_speed(altitude);

                    if (altitude < settings.start_touchdown_altitude.V)
                        setMode(Mode.TouchDown);
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
            }

            // call the sub controllers
            base.Update();

            // A phase that cannot do its job (Circularize too high, no deorbit window) stops
            // the landing instead of falling through to the next phase.
            if (current_executor.failed)
            {
                StopWithError(current_executor.failure_reason);
                return;
            }

            if (current_executor.finished)
            {
                // auto next
                nextMode();
            }
        }

        public float altitude;


    }
}

using System;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.OrbitPlanning;
using K2D2.UI;
using KSP.Api;
using KSP.Sim;
using KSP.Sim.impl;
using KSP2FlightAssistant.MathLibrary;
using KTools;
using UnityEngine;
using UnityEngine.UIElements;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Node
{
    /// <summary>
    /// Closed-loop burn toward a target orbit (apsis radii and plane), without following any
    /// maneuver node.
    ///
    /// The game models a node as a finite burn sized on the stage active right now; with a nearly
    /// empty stage still active its burn duration and predicted trajectory are garbage, and an
    /// executor that follows that model (SAS Maneuver mode, ManeuverPlanSolver) misses the target.
    /// This controller instead re-evaluates its guidance (an IBurnGuidance: OrbitTargetGuidance
    /// for a target orbit, i.e. OrbitGuidance.DeltaVToOrbit) from the current orbit every frame
    /// and steers along it until it vanishes, so staging mid-burn does not matter.
    ///
    /// Sequence: steer toward the burn aim from the start (also while warping), warp until the
    /// alignment lead before the burn start (BurnWarp.AlignmentLead from the angle still to turn
    /// and the measured turn rate, 20 to 120 s), stop the warp instantly there, and ignite at the
    /// start time once within 10° (a late alignment delays the ignition, the closed loop corrects
    /// the orbit). While burning the warp is physics warp, x4 at most (PhysicsWarpPolicy, under
    /// BurnWarp.MaxBurnRate), given back as the player had it on every end path; with
    /// allow_rails_warp a long steady middle stretch (more than 120 s left) runs once on rails at
    /// x10 or x50, flown by the game's own precomputed burn (RailsBurnPolicy, RailsBurn: guarded
    /// within 2 frames, left 15 s of burn or 10 s of stage before the end), and the throttle
    /// is frame-aware (BurnWarp.Throttle). Refusals go through Fail(). CircularizeBurn derives from it
    /// with its own target (circular at the current radius) and start time (centered on the apsis).
    ///
    /// With stage_when_dry (off by default, set by the transfer pilot only) a burn whose engines
    /// give no thrust for 0.5 s stages once (BurnStaging), unless the next stage would drop a
    /// docking port, a parachute or the command part: then it stops with a message.
    /// </summary>
    public class TargetOrbitBurn : ExecuteController
    {
        public ILogger logger;

        // Minimum seconds of attitude alignment, with no warp, before the burn starts (the lead
        // grows with the angle to turn, see BurnWarp.AlignmentLead).
        public const double AlignLead = BurnWarp.MinLead;
        // Above this angle (degrees) between the vessel's forward and the aim, the throttle is 0.
        const double MaxThrustAngle = 10;
        // Default remaining Δv (m/s) under which the burn is done.
        public const double DefaultDoneDeltaV = 0.2;
        // Once the remaining Δv has dropped under this (m/s), OvershootRises consecutive rises
        // mean the burn overshot: stop there.
        const double OvershootWatchDeltaV = 5;
        const int OvershootRises = 10;
        const double Rad2Deg = 180 / Math.PI;

        // Divergence guard: armed after DivergenceArmTime seconds of thrust while aligned, it
        // aborts when |dv| exceeds ignition·DivergenceGrowth + DivergenceMargin, or has risen
        // continuously for DivergenceRiseTime seconds of thrust.
        const double DivergenceArmTime = 2;
        const double DivergenceGrowth = 1.2;
        const double DivergenceMargin = 20;
        const double DivergenceRiseTime = 3;
        // Stall: |dv| not dropping for this many seconds of thrust while aligned (empty stage).
        const double StallTime = 3;
        const string StallStatus = "No thrust — stage?";
        // Below this |dot| between our normal axis and the game's orbit-normal marker, the frames
        // are not what the aim assumes: abort.
        const double MinNormalAxisDot = 0.9;
        // The frame check aborts only when the burn has a meaningful normal component at ignition:
        // above max(this, FrameCheckNormalShare·|dv|). Radial and along-track components do not
        // depend on the handedness the check is about.
        const double FrameCheckMinNormal = 1.0;
        const double FrameCheckNormalShare = 0.05;
        // A frame counts for the rise and divergence guards only with this real (measured)
        // acceleration, m/s²: a staging gap or a flamed-out engine never trips them.
        const double MinRealAccel = 0.1;

        // Warp toward the burn and raise the warp during long aligned burns (the Node tab follows
        // its own auto-warp setting). False: the player's warp is only lowered when needed.
        public bool allow_warp = true;

        // Stage when the engines run dry mid-burn (BurnStaging). Off: this controller never
        // stages, as before (the Node tab, Orbit, Lift and Landing leave it off).
        public bool stage_when_dry = false;

        // Rails warp (x10, x50) for the long middle of a burn, with the game's own precomputed
        // burn (RailsBurnPolicy, RailsBurn). False for landing and reentry burns (PrecisionPhase):
        // they stay at physics x4 at most.
        public bool allow_rails_warp = true;
        // Within 2 km of a rendezvous target, set every frame by the Rendezvous pilot: never rails.
        public bool near_target = false;

        /// <summary>Remaining Δv (m/s) under which the burn is done. Start(target, startUT,
        /// doneDeltaV) sets it for its run (default 0.2); CircularizeBurn.Start keeps the value
        /// set here (default 0.2).</summary>
        public double DoneDeltaV { get; set; } = DefaultDoneDeltaV;

        public enum Mode { Warp, Align, Burn }
        public Mode mode = Mode.Warp;

        TargetOrbit target;
        IBurnGuidance guidance;
        double start_UT;

        double burn_start_UT;
        double ignition_dv;
        double remaining_dv;
        double last_remaining_dv;
        bool watching_overshoot;
        int rises;
        double angle;
        float throttle;
        bool normal_axis_checked;
        bool frame_check_strict;

        double last_burn_UT;
        double thrust_time;      // seconds of thrust while aligned since ignition
        double rising_time;      // seconds of thrust with |dv| rising, continuously
        double best_dv;          // lowest |dv| since ignition
        double stall_time;       // seconds of thrust while aligned without a new lowest |dv|
        bool stalled;

        // Alignment lead (s) in use, lengthened only, and the turn rate (°/s of real time).
        double align_lead;
        double turn_rate;
        double last_angle;       // NaN until a first aim was applied
        double last_angle_real;
        int last_warp_index;     // warp index at the previous angle sample
        bool warp_stopped;       // the warp was stopped at WarpEndUT
        bool late_logged;
        // Physics time warp while burning (ManageBurnWarp), left on every end path.
        readonly PhysicsWarp physics_warp;
        // Rails warp for long burns (ConsiderRails, BurnOnRails), one stretch per burn at most.
        readonly RailsBurn rails;
        double steady_real;          // real s aligned within 1° at full throttle, unbroken
        double last_steady_real = double.NaN;
        float last_steady_throttle;
        bool rails_done;             // a rails stretch ended (or was refused): no more in this burn
        bool rails_guard_logged;
        double rails_entry_UT, rails_entry_dv, rails_entry_accel, rails_mu;
        double rails_thrust_N, rails_mass_kg, rails_mdot, rails_stage_seconds, rails_rate;
        Vector3d rails_entry_r, rails_entry_v, rails_dir;

        protected KSPVessel current_vessel;
        protected BurndV burn_dV = new BurndV();
        WarpTo warp = new WarpTo();
        readonly BurnStaging staging = new BurnStaging();

        public TargetOrbitBurn() : this("SkySpaceAgency.TargetOrbitBurn") { }

        protected TargetOrbitBurn(string loggerName)
        {
            logger = SpaceWarp2.API.Logging.LogManager.GetLogger(loggerName);
            physics_warp = new PhysicsWarp(logger, () => BurnLogTag, () => current_vessel?.VesselComponent);
            rails = new RailsBurn(logger, () => BurnLogTag, () => current_vessel?.VesselComponent);
        }

        public TargetOrbit Target => target;
        public IBurnGuidance Guidance => guidance;

        // Log tag, extra words after it ("AP"), and the burn's name in the UI texts.
        protected virtual string Tag => "TargetOrbitBurn";
        protected virtual string TagDetail => "";
        protected virtual string BurnLabel => "burn";

        /// <summary>
        /// Burns toward the target orbit from startUT (warping and aligning before it, see the
        /// class summary). A start time already past means burning now.
        /// </summary>
        public void Start(TargetOrbit target, double startUT, double doneDeltaV = DefaultDoneDeltaV)
        {
            this.target = target;
            guidance = new OrbitTargetGuidance(target);
            DoneDeltaV = doneDeltaV;
            start_UT = startUT;
            if (!BeginRun())
                return;

            if (!(target.Ra > 0) || !(target.Rp > 0) || double.IsNaN(target.PlaneNormal.magnitude)
                || !(target.PlaneNormal.magnitude > 0) || double.IsNaN(startUT))
            {
                Refuse("Invalid target orbit.");
                return;
            }

            double now = GeneralTools.Current_UT;
            double dv = CurrentDeltaV(now).magnitude;
            if (double.IsNaN(dv))
            {
                Refuse("Could not compute the burn to the target orbit.");
                return;
            }

            var body = current_vessel.VesselComponent.Orbit.referenceBody;
            logger.LogInfo($"[{Tag}] accepted: {TargetText(body.radius)}, start in {startUT - now:n1} s, " +
                $"dv now {dv:n1} m/s");
            BeginWaiting(now);
        }

        /// <summary>
        /// Burns along any guidance from startUT, with the same warp, alignment, ignition, guards
        /// and end as Start(TargetOrbit, …). The guidance is fixed at ignition when it is an
        /// IFixedAtIgnition. Refused without a guidance, or when it gives no answer now. An
        /// OrbitTargetGuidance goes through Start(TargetOrbit, …) (its target is validated and logged).
        /// </summary>
        public void Start(IBurnGuidance g, double startUT, double doneDeltaV = DefaultDoneDeltaV)
        {
            // A target orbit keeps its own validation and its "target ap … pe …" log text.
            if (g is OrbitTargetGuidance orbitTarget)
            {
                Start(orbitTarget.Target, startUT, doneDeltaV);
                return;
            }
            target = default;
            guidance = g;
            DoneDeltaV = doneDeltaV;
            start_UT = startUT;
            if (!BeginRun())
                return;

            if (g == null || double.IsNaN(startUT))
            {
                Refuse("Invalid burn guidance.");
                return;
            }

            double now = GeneralTools.Current_UT;
            double dv = CurrentDeltaV(now).magnitude;
            if (double.IsNaN(dv))
            {
                Refuse($"Could not compute the burn ({g.Describe()}).");
                return;
            }

            logger.LogInfo($"[{Tag}] accepted: {g.Describe()}, start in {startUT - now:n1} s, dv now {dv:n1} m/s" +
                (stage_when_dry ? ", stages when dry" : ""));
            BeginWaiting(now);
        }

        // Resets the run. False, after a refusal, when there is no active vessel.
        protected bool BeginRun()
        {
            // A restart in the middle of a physics-warped burn gives the warp back first.
            // Rails warp first (index 0, which also ends the game's precomputed burn).
            if (rails.Entered)
                rails.Leave();
            rails.Reset();
            physics_warp.Leave(true);
            base.Start();
            steady_real = 0;
            last_steady_real = double.NaN;
            last_steady_throttle = 0;
            rails_done = false;
            rails_guard_logged = false;
            mode = Mode.Warp;
            remaining_dv = 0;
            last_remaining_dv = double.NaN;
            ignition_dv = double.NaN;
            watching_overshoot = false;
            rises = 0;
            angle = 0;
            throttle = 0;
            normal_axis_checked = false;
            frame_check_strict = false;
            staging.Reset();
            last_burn_UT = double.NaN;
            thrust_time = 0;
            rising_time = 0;
            best_dv = double.PositiveInfinity;
            stall_time = 0;
            stalled = false;
            align_lead = BurnWarp.MinLead;
            turn_rate = BurnWarp.DefaultTurnRate;
            last_angle = double.NaN;
            last_angle_real = 0;
            last_warp_index = -1;
            warp_stopped = false;
            late_logged = false;

            current_vessel = K2D2_Plugin.Instance.current_vessel;
            if (current_vessel?.VesselComponent == null)
            {
                Refuse("No active vessel.");
                return false;
            }
            current_vessel.SetThrottle(0);
            return true;
        }

        // Burns now when the start time has come, otherwise turns toward the aim and warps (if
        // allowed) or aligns.
        protected void BeginWaiting(double now)
        {
            if (now >= BurnStartUT)
            {
                // One aim first, so the ignition log has the real angle.
                Steer(out _);
                if (!finished)
                    StartBurning(now);
                return;
            }
            // A first aim gives the angle to turn, hence the lead before the warp ends.
            if (Steer(out _))
                RefreshLead(false);
            logger.LogInfo($"{BurnLogTag} lead {align_lead:n1} s for " +
                $"{(double.IsNaN(last_angle) ? "an unknown angle" : $"{last_angle:n1}°")} at {turn_rate:n1}°/s (default), " +
                $"warp ends in {WarpEndUT - now:n1} s");
            if (allow_warp && now < WarpEndUT)
                warp.Start_Ut(WarpEndUT);
            else
                StartAligning(now);
        }

        protected virtual double BurnStartUT => start_UT;
        double WarpEndUT => BurnStartUT - align_lead;

        // "[TargetOrbitBurn]", with the subclass's tag after it ("[TargetOrbitBurn] Circularize AP").
        string BurnLogTag => Tag == "TargetOrbitBurn" ? "[TargetOrbitBurn]" : $"[TargetOrbitBurn] {Tag}{TagDetail}";

        // After a Steer: measures the turn rate in real time, only while still turning, at 1x, and
        // not on the frame after a warp-index change (BurnWarp.ShouldSampleTurnRate). The angle
        // and time are recorded every frame, so each sample spans one frame.
        void UpdateTurnRate()
        {
            double real = Time.realtimeSinceStartupAsDouble;
            int index = TimeWarpTools.CurrentRateIndex;
            bool indexChanged = index != last_warp_index;
            if (!double.IsNaN(last_angle) && last_angle > MaxThrustAngle
                && BurnWarp.ShouldSampleTurnRate(TimeWarpTools.CurrentRate, indexChanged))
                turn_rate = BurnWarp.MeasuredTurnRate(turn_rate, last_angle, angle, real - last_angle_real);
            last_angle = angle;
            last_angle_real = real;
            last_warp_index = index;
        }

        // After a Steer: lengthens the lead (never shortens it) while the angle is not yet small.
        void RefreshLead(bool log = true)
        {
            UpdateTurnRate();
            if (angle <= MaxThrustAngle)
                return;
            double lead = BurnWarp.AlignmentLead(angle, turn_rate);
            if (lead > align_lead)
            {
                // Logged only for a real change before the warp stop (it no longer matters after).
                if (log && !warp_stopped && lead > align_lead + 5)
                    logger.LogInfo($"{BurnLogTag} lead lengthened to {lead:n1} s: {angle:n1}° to turn at {turn_rate:n1}°/s");
                align_lead = lead;
            }
        }

        // Stops the warp, instantly, once WarpEndUT has come (the player's own warp too).
        void StopWarpAtLead(double now)
        {
            if (warp_stopped || now < WarpEndUT)
                return;
            warp_stopped = true;
            TimeWarpTools.SetRateIndex(0, true);
            logger.LogInfo($"{BurnLogTag} warp stopped {BurnStartUT - now:n1} s before the burn: lead {align_lead:n1} s, " +
                $"{angle:n1}° off, turn rate {turn_rate:n1}°/s");
        }

        // Called while waiting, every frame, before the start time is checked.
        protected virtual void RefreshWhileWaiting(double now) { }

        // The Δv still to go from (r, v) at ut. NaN without a guidance.
        protected virtual Vector3d GuidanceDeltaV(Vector3d r, Vector3d v, double ut, double mu) =>
            guidance != null ? guidance.DeltaVToGo(r, v, ut, mu) : new Vector3d(double.NaN, double.NaN, double.NaN);

        Vector3d CurrentDeltaV(double now)
        {
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            return GuidanceDeltaV(orbit.GetRelativePositionAtUTZup(now), orbit.GetOrbitalVelocityAtUTZup(now), now,
                orbit.referenceBody.gravParameter);
        }

        // Thrust (kN) of the active engines, or of every engine when none is active.
        protected double Thrust()
        {
            double thrust = burn_dV.active_thrust;
            if (thrust <= 0)
                thrust = burn_dV.full_thrust.magnitude;
            return thrust;
        }

        // Estimated duration (s) of a burn of deltaV at full thrust; NaN without thrust.
        protected double EstimateBurnTime(double deltaV)
        {
            burn_dV.Compute_Thrust();
            return BurnTiming.Duration(deltaV, Thrust(), burn_dV.mass, burn_dV.active_isp);
        }

        void StartAligning(double now)
        {
            mode = Mode.Align;
            StopWarpAtLead(now);
        }

        void StartBurning(double now)
        {
            // The warp is managed frame by frame from now on (ManageBurnWarp).
            mode = Mode.Burn;
            burn_start_UT = now;
            logger.LogInfo($"{BurnLogTag} ignition at T{now - BurnStartUT:+0.0;-0.0;+0.0}s vs planned, {angle:n1}° off");
            ignition_dv = double.NaN;
            // Fixed for the whole burn (a target orbit: the sign of the radial speed, so the target
            // does not flip side when the radial speed crosses 0 mid-burn).
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v = orbit.GetOrbitalVelocityAtUTZup(now);
            (guidance as IFixedAtIgnition)?.FixAtIgnition(r, v, now, orbit.referenceBody.gravParameter);

            // The frame check (first Steer from now on) may abort only a burn with a real normal part.
            Vector3d dv = GuidanceDeltaV(r, v, now, orbit.referenceBody.gravParameter);
            OrbitGuidance.Split(dv, r, v, out _, out _, out double normal);
            frame_check_strict = Math.Abs(normal) > Math.Max(FrameCheckMinNormal, FrameCheckNormalShare * dv.magnitude);
            logger.LogInfo($"[{Tag}] frame check {(frame_check_strict ? "can abort" : "log only")}: " +
                $"normal component {normal:n1} of {dv.magnitude:n1} m/s");
            LogStart(now);
        }

        // Ignition log: the orbit, the target and the Δv with its estimated duration.
        protected virtual void LogStart(double now)
        {
            double dv = CurrentDeltaV(now).magnitude;
            LogOrbit("start", now, dv, EstimateBurnTime(dv));
        }

        public override void Update()
        {
            if (!finished)
                UpdateRun();
            // Done, refused, aborted or failed: the rails warp, then the physics warp go back to
            // 1x and the player's mode. A burn stopped from outside (its Update no longer called)
            // is caught by PhysicsWarp.ReleaseIfAbandoned instead (index 0 ends rails warp too).
            if (finished && rails.Entered && current_vessel?.VesselComponent != null)
                ExitRails(GeneralTools.Current_UT, "the burn ended");
            if (finished && physics_warp.Active)
                physics_warp.Leave(true);
        }

        void UpdateRun()
        {
            if (current_vessel?.VesselComponent == null)
                return;

            double now = GeneralTools.Current_UT;

            if (mode == Mode.Warp || mode == Mode.Align)
            {
                RefreshWhileWaiting(now);
                set_throttle(0);

                // Turn toward the aim at the planned start while time passes (SAS target only).
                bool steering = Steer(out _);
                if (finished)
                    return;
                if (steering)
                    RefreshLead();

                if (now >= BurnStartUT)
                {
                    // Late alignment: wait (no throttle) until within MaxThrustAngle. Without an
                    // aim the burn starts and Burn() holds the throttle at 0, as before.
                    if (steering && angle > MaxThrustAngle)
                    {
                        if (mode == Mode.Warp)
                            StartAligning(now);
                        StopWarpAtLead(now);
                        // No torque or SAS off: give up instead of waiting forever.
                        if (BurnWarp.AlignmentTimedOut(now, BurnStartUT, align_lead))
                        {
                            set_throttle(0);
                            logger.LogWarning($"{BurnLogTag} alignment timed out: {now - BurnStartUT:n1} s past the start " +
                                $"(limit {BurnWarp.LateIgnitionTimeout(align_lead):n1} s), {angle:n1}° off, turn rate {turn_rate:n1}°/s");
                            Fail("Could not align with the burn direction: check SAS and reaction wheels.");
                            return;
                        }
                        if (!late_logged)
                        {
                            late_logged = true;
                            logger.LogInfo($"{BurnLogTag} late ignition: {now - BurnStartUT:n1} s, {angle:n1}° off " +
                                $"(lead {align_lead:n1} s, turn rate {turn_rate:n1}°/s)");
                        }
                        status_line = $"Late ignition: +{StrTool.DurationToString(now - BurnStartUT)}, {angle:n1}° off";
                        return;
                    }
                    StartBurning(now);
                }
                else if (mode == Mode.Warp)
                {
                    if (!allow_warp || now >= WarpEndUT)
                    {
                        StartAligning(now);
                    }
                    else
                    {
                        warp.UT = WarpEndUT;
                        warp.Update();
                        status_line = $"Burn in {StrTool.DurationToString(BurnStartUT - now)}, {angle:n1}° off - {warp.status_line}";
                        if (warp.finished)
                            StartAligning(now);
                        return;
                    }
                }

                if (mode == Mode.Align)
                {
                    StopWarpAtLead(now);
                    status_line = $"Ignition in {StrTool.DurationToString(BurnStartUT - now)}, {angle:n1}° off";
                    return;
                }
            }

            Burn(now);
        }

        void Burn(double now)
        {
            // On rails the game flies its precomputed burn: no steering, no throttle, only the
            // exit conditions are watched.
            if (rails.Entered)
            {
                BurnOnRails(now);
                return;
            }

            bool steering = Steer(out Vector3d dv);
            if (finished)
                return;   // frame check failed

            double dt = double.IsNaN(last_burn_UT) ? 0 : Math.Max(0, now - last_burn_UT);
            last_burn_UT = now;
            // The throttle applied since the previous frame: did the engines push?
            bool thrusted = throttle > 0;
            // And did they really (measured from the engines' final thrust)?
            burn_dV.Compute_Thrust();
            bool powered = thrusted && burn_dV.actual_dv > MinRealAccel;

            remaining_dv = dv.magnitude;
            if (double.IsNaN(remaining_dv))
            {
                set_throttle(0);
                Refuse($"Lost the orbit state: {BurnLabel} stopped.");
                return;
            }
            if (double.IsNaN(ignition_dv))
                ignition_dv = remaining_dv;

            // Also covers a zero Δv from an undefined direction (no horizontal speed).
            if (remaining_dv < DoneDeltaV)
            {
                Finish(now, "done");
                return;
            }

            if (!steering)
            {
                set_throttle(0);
                ManageBurnWarp(remaining_dv, 0, false);
                steady_real = 0;
                return;
            }

            bool rose = !double.IsNaN(last_remaining_dv) && remaining_dv > last_remaining_dv;
            bool aligned = angle <= MaxThrustAngle;

            if (remaining_dv < OvershootWatchDeltaV)
                watching_overshoot = true;
            // Only frames with thrust count: a coasting frame says nothing about an overshoot.
            if (watching_overshoot && thrusted && !double.IsNaN(last_remaining_dv))
            {
                rises = rose ? rises + 1 : 0;
                if (rises >= OvershootRises)
                {
                    Finish(now, "overshoot");
                    return;
                }
            }
            last_remaining_dv = remaining_dv;

            // Frames without real thrust are not counted (neither a rise nor a break).
            if (powered)
                rising_time = rose ? rising_time + dt : 0;
            if (powered && aligned)
            {
                thrust_time += dt;
                if (thrust_time >= DivergenceArmTime)
                {
                    string why = null;
                    if (remaining_dv > ignition_dv * DivergenceGrowth + DivergenceMargin)
                        why = $"dv {remaining_dv:n1} m/s > {DivergenceGrowth} x ignition {ignition_dv:n1} + {DivergenceMargin} m/s";
                    else if (rising_time >= DivergenceRiseTime)
                        why = $"dv rising for {rising_time:n1} s under thrust (now {remaining_dv:n1} m/s, ignition {ignition_dv:n1} m/s)";
                    if (why != null)
                    {
                        set_throttle(0);
                        TimeWarpTools.SetRateIndex(0, true);
                        logger.LogWarning($"[{Tag}] burn diverging, aborted: {why}");
                        LogOrbit("abort (diverging)", now, remaining_dv, now - burn_start_UT);
                        Fail($"Burn diverging: aborted (check the [{Tag}] log).");
                        return;
                    }
                }
            }

            // Stall: thrust commanded while aligned, but |dv| no longer drops (empty stage). Only
            // reported: auto-staging may fix it.
            if (remaining_dv < best_dv)
            {
                best_dv = remaining_dv;
                stall_time = 0;
                stalled = false;
            }
            else if (thrusted && aligned)
            {
                stall_time += dt;
                if (stall_time >= StallTime && !stalled)
                {
                    stalled = true;
                    logger.LogWarning($"[{Tag}] no thrust: dv {remaining_dv:n1} m/s not dropping for {stall_time:n1} s (stage?)");
                }
            }

            double accel = burn_dV.mass > 0 ? Thrust() / burn_dV.mass : 0;

            if (stage_when_dry && aligned && StageIfDry(now, accel))
            {
                steady_real = 0;
                return;
            }

            ManageBurnWarp(remaining_dv, accel, aligned);
            if (!aligned || accel <= 0)
                set_throttle(0);
            else
                set_throttle(BurnWarp.Throttle(remaining_dv, accel, Time.unscaledDeltaTime,
                    Math.Max(1, TimeWarpTools.CurrentRate)));

            status_line = stalled ? StallStatus : BurningStatus(remaining_dv);

            // After the physics warp: a long, steady stretch may go on rails.
            UpdateSteady();
            ConsiderRails(now, dv, accel);
        }

        // Real seconds aligned within RailsBurnPolicy.MaxAlignDeg at full throttle on this frame
        // and the previous one, without a break.
        void UpdateSteady()
        {
            double real = Time.realtimeSinceStartupAsDouble;
            bool steady = angle <= RailsBurnPolicy.MaxAlignDeg && throttle >= 1f && last_steady_throttle >= 1f;
            steady_real = steady && !double.IsNaN(last_steady_real) ? steady_real + (real - last_steady_real) : 0;
            last_steady_real = real;
            last_steady_throttle = throttle;
        }

        // Rails safety inputs from a state: inside the atmosphere or under the game's surface
        // envelope (RailsBurnPolicy.EnvelopeAltitude) at radius r, or at minRadius on the way.
        static bool LowOrInAtmosphere(CelestialBodyComponent body, double r, double minRadius)
        {
            if (body == null || double.IsNaN(r))
                return true;
            double envelope = RailsBurnPolicy.EnvelopeAltitude(body.hasAtmosphere, body.atmosphereDepth, body.MaxTerrainHeight);
            double alt = r - body.radius;
            if (body.hasAtmosphere && alt <= body.atmosphereDepth)
                return true;
            return alt < envelope || Math.Min(r, minRadius) - body.radius < envelope;
        }

        // Off rails, once the physics warp is managed: asks RailsBurnPolicy, enters rails when it
        // says so. The aim drift (a path integration) is computed only when every cheaper
        // condition already holds.
        void ConsiderRails(double now, Vector3d dv, double accel)
        {
            if (rails_done || finished || !(accel > 0))
                return;
            bool allowed = allow_rails_warp;
            double burnLeft = remaining_dv / accel;

            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v = orbit.GetOrbitalVelocityAtUTZup(now);
            CelestialBodyComponent body = orbit.referenceBody;
            double mu = body.gravParameter;
            double stageLeft = rails.StageBurnSeconds();
            double planned = RailsBurnPolicy.PlannedRailsSeconds(burnLeft, stageLeft);
            bool low = LowOrInAtmosphere(body, r.magnitude, r.magnitude);

            double drift = double.NaN;
            if (allowed && !near_target && !low && burnLeft > RailsBurnPolicy.MinBurnLeft
                && steady_real >= RailsBurnPolicy.MinSteadySeconds && planned > 0)
            {
                drift = RailsBurnPolicy.PredictAimDrift((rr, vv, t) => GuidanceDeltaV(rr, vv, t, mu), r, v, now, mu, accel,
                    planned, out double minRadius);
                low = LowOrInAtmosphere(body, r.magnitude, minRadius);
            }

            float[] rates = RailsBurn.Rates;
            RailsStep step = RailsBurnPolicy.Decide(new RailsInputs
            {
                BurnLeftGameSeconds = burnLeft,
                StageBurnLeftSeconds = stageLeft,
                AlignedSteadyRealSeconds = steady_real,
                AimDriftDegAtExit = drift,
                RailsAllowed = allowed,
                InAtmosphereOrEnvelope = low,
                NearTarget = near_target,
                CurrentlyOnRails = false,
                GuardOk = null,
                FramesSinceRailsEntry = 0,
                Rates = rates,
                RealFrameSeconds = Time.unscaledDeltaTime,
                CurrentIndex = TimeWarpTools.CurrentRateIndex,
                RefusedBefore = rails_done,
            });
            if (step.Action != RailsAction.EnterRails)
                return;

            // Entry snapshot: the state, the fixed direction, and the mass flow for the
            // acceleration while the game burns (thrust kN / t = m/s²; in N and kg here).
            rails_entry_UT = now;
            rails_entry_r = r;
            rails_entry_v = v;
            rails_mu = mu;
            rails_dir = dv.normalized;
            rails_entry_dv = remaining_dv;
            rails_entry_accel = accel;
            rails_thrust_N = Thrust() * 1000;
            rails_mass_kg = burn_dV.mass * 1000;
            rails_mdot = burn_dV.active_isp > 0 ? rails_thrust_N / (burn_dV.active_isp * 9.80665) : 0;
            rails_stage_seconds = stageLeft;
            rails_rate = RailsBurnPolicy.RateAt(rates, step.WantedIndex);
            rails_guard_logged = false;

            string from = $"index {TimeWarpTools.CurrentRateIndex} x{TimeWarpTools.CurrentRate:0.##}";
            if (!rails.TryEnter(step.WantedIndex))
            {
                rails_done = true;
                return;
            }
            logger.LogInfo($"{BurnLogTag} rails burn: entering index {step.WantedIndex} (x{rails_rate:0}) from {from}: {step.Reason}; " +
                $"dv {remaining_dv:n1} m/s, burn left {burnLeft:n1} s, stage burn left {stageLeft:n1} s, aim drift {drift:n2}° " +
                $"until the exit, planned dv on rails {PlannedRailsDv(planned):n1} m/s, " +
                $"{rails.ManeuverNodeCount()} maneuver node(s) on the vessel (never changed while on rails); {RailsBurn.ReflectionStatus()}");
        }

        // Δv (m/s) the game's burn gives in t game seconds from the entry: the rocket equation
        // with the entry thrust and mass flow (constant acceleration without a known flow).
        double PlannedRailsDv(double t)
        {
            if (!(t > 0))
                return 0;
            double left = rails_mass_kg - rails_mdot * t;
            if (rails_mdot > 0 && left > 0)
                return rails_thrust_N / rails_mdot * Math.Log(rails_mass_kg / left);
            return rails_entry_accel * t;
        }

        // A frame on rails: guard, state from the burn trajectory, exit conditions. No steering,
        // no throttle command.
        void BurnOnRails(double now)
        {
            rails.Tick();
            physics_warp.KeepAlive();
            last_burn_UT = now;
            int frames = rails.FramesSinceEntry;

            bool haveState = rails.TryReadState(now, out Vector3d r, out Vector3d v, out double mu);
            bool? guard = null;
            string detail = "";
            if (frames >= 1)
            {
                guard = rails.GuardOk(out detail);
                if (guard == true && !haveState)
                {
                    guard = false;
                    detail += ", state unreadable at UT now";
                }
            }
            if (guard.HasValue && !rails_guard_logged && (guard == true || frames >= RailsBurnPolicy.GuardFrames))
            {
                rails_guard_logged = true;
                string text = $"{BurnLogTag} rails burn: guard {(guard == true ? "ok" : "FAILED")} at frame {frames}: {detail}";
                if (guard == true)
                {
                    // The burn trajectory against the conic the game also writes back (both Zup).
                    IKeplerPatch conic = current_vessel.VesselComponent.Orbit;
                    double gap = (conic.GetRelativePositionAtUTZup(now) - r).magnitude;
                    logger.LogInfo($"{text}; burn ends in {rails.BurnEndUT() - now:n1} s (stage estimate {rails_stage_seconds - (now - rails_entry_UT):n1} s), " +
                        $"trajectory vs conic {gap:n1} m");
                }
                else
                    logger.LogWarning(text);
            }

            if (!rails.OnRails)
            {
                ExitRails(now, $"the warp left rails (index {TimeWarpTools.CurrentRateIndex}, set by the player or the game)");
                return;
            }

            Vector3d dv = haveState ? GuidanceDeltaV(r, v, now, mu) : new Vector3d(double.NaN, double.NaN, double.NaN);
            double dvMag = dv.magnitude;
            if (!double.IsNaN(dvMag))
                remaining_dv = dvMag;
            double accel = RailsBurnPolicy.AccelAfter(rails_thrust_N, rails_mass_kg, rails_mdot, now - rails_entry_UT);
            if (double.IsNaN(accel))
                accel = rails_entry_accel;
            double endUT = rails.BurnEndUT();
            if (double.IsNaN(endUT))
                endUT = rails_entry_UT + rails_stage_seconds;
            CelestialBodyComponent body = current_vessel.VesselComponent.Orbit.referenceBody;

            RailsStep step = RailsBurnPolicy.Decide(new RailsInputs
            {
                BurnLeftGameSeconds = double.IsNaN(dvMag) ? double.NaN : dvMag / accel,
                StageBurnLeftSeconds = endUT - now,
                AlignedSteadyRealSeconds = 0,
                AimDriftDegAtExit = double.IsNaN(dvMag) ? double.NaN : Vector3d.Angle(rails_dir, dv),
                RailsAllowed = allow_rails_warp,
                InAtmosphereOrEnvelope = !haveState || LowOrInAtmosphere(body, r.magnitude, r.magnitude),
                NearTarget = near_target,
                CurrentlyOnRails = true,
                GuardOk = guard,
                FramesSinceRailsEntry = frames,
                Rates = RailsBurn.Rates,
                RealFrameSeconds = Time.unscaledDeltaTime,
                CurrentIndex = TimeWarpTools.CurrentRateIndex,
                RefusedBefore = rails_done,
            });
            switch (step.Action)
            {
                case RailsAction.DropGuard:
                    logger.LogWarning($"{BurnLogTag} rails burn: guard failed, back to physics warp, no rails again in this burn: " +
                        $"{step.Reason} ({detail})");
                    ExitRails(now, step.Reason);
                    return;
                case RailsAction.LeaveRails:
                    ExitRails(now, step.Reason);
                    return;
            }
            status_line = $"Rails warp x{TimeWarpTools.CurrentRate:0}: {BurningStatus(remaining_dv)}";
        }

        // Leaves rails (index 0: the vessel goes back to RigidBody, the closed loop and the
        // physics warp resume on the next frame) and logs the Δv applied, planned against actual.
        // One rails stretch per burn.
        void ExitRails(double now, string reason)
        {
            double t = now - rails_entry_UT;
            string applied = "Δv applied unknown";
            Vector3d v = default;
            bool haveState = rails.TryReadState(now, out _, out v, out _);
            if (!haveState)
            {
                try
                {
                    v = current_vessel.VesselComponent.Orbit.GetOrbitalVelocityAtUTZup(now);
                    haveState = !double.IsNaN(v.x + v.y + v.z);
                }
                catch (Exception) { haveState = false; }
            }
            if (haveState && rails_mu > 0)
            {
                KeplerPropagator.Propagate(rails_entry_r, rails_entry_v, rails_mu, t, out _, out Vector3d vCoast);
                applied = $"Δv applied {(v - vCoast).magnitude:n1} m/s, planned {PlannedRailsDv(t):n1} m/s";
            }
            rails.Leave();
            rails_done = true;
            steady_real = 0;
            last_steady_real = double.NaN;
            logger.LogInfo($"{BurnLogTag} rails burn: leaving ({reason}) after {t:n1} s of game time at x{rails_rate:0}: " +
                $"dv to go {rails_entry_dv:n1} -> {remaining_dv:n1} m/s, {applied}");
        }

        // Opt-in staging when the engines run dry (BurnStaging). True when this frame ends here:
        // the burn failed (throttle 0, warp off), or a staging waits for the warp to stop.
        bool StageIfDry(double now, double accel)
        {
            BurnStaging.Outcome outcome = staging.Update(current_vessel.VesselComponent, now, throttle, accel,
                burn_dV.actual_dv, out string failure);
            switch (outcome)
            {
                case BurnStaging.Outcome.Failed:
                    set_throttle(0);
                    TimeWarpTools.SetRateIndex(0, true);
                    LogOrbit("abort (staging)", now, remaining_dv, now - burn_start_UT);
                    Fail(failure);
                    return true;
                case BurnStaging.Outcome.Pending:
                    status_line = "Engines dry: staging...";
                    return true;
                default:
                    return false;
            }
        }

        // Warp while burning, steered by this controller: physics warp only. (A long, steady middle
        // stretch may go on rails afterwards, under the guards of ConsiderRails / BurnOnRails,
        // RailsBurnPolicy.) On rails warp the game replaces
        // thrust by a burn it precomputes once when the warp starts (HandleOrbitalPhysicsUnder-
        // ThrustStart, IL): fixed direction, the stage's flow until it is empty, and steering or
        // throttle changes ignored. In game on 2026-10-24, three transfer injections warped to x4
        // on rails right after ignition burned ~500 m/s of fuel each while the orbit stayed
        // 100 x 100 km. Physics warp (TimeWarp.ToggleTimeWarpMode at 1x, then x2 or x4) keeps the
        // vessel RigidBody, so steering and throttle keep working.
        //
        // PhysicsWarpPolicy decides each frame: a rails index, physics mode off or a vessel no
        // longer RigidBody drop to 1x at once; otherwise the cap is BurnWarp.MaxBurnRate (5 % of
        // the remaining Δv per frame of real time, at least 10 s of real time left), x4 at most,
        // x2 under 30 s of game time left, 1x when not aligned. With allow_warp the warp is raised
        // to the cap, otherwise the player's warp is only lowered.
        void ManageBurnWarp(double remainingDv, double accel, bool aligned)
        {
            double frame = Time.unscaledDeltaTime;
            double burnLeft = accel > 0 ? remainingDv / accel : double.PositiveInfinity;
            double cap = 1;
            if (aligned && accel > 0 && burnLeft >= BurnWarp.MinRealBurnTime)
                cap = BurnWarp.MaxBurnRate(remainingDv, accel, frame);
            physics_warp.Update(cap, aligned, burnLeft, allow_warp);
        }

        protected virtual string BurningStatus(double dv) => $"Target orbit: {dv:n1} m/s left";
        protected virtual string DoneStatus(double dv) => $"Target orbit reached ({dv:n1} m/s left)";

        // Points SAS along the current guidance Δv (StabilityAssist + a target orientation in the
        // HorizonUp frame, same pattern as TouchDown.checkDirection) and measures the angle between
        // the vessel's forward and that aim. The Δv is split into radial, along-track and normal
        // components in the Zup frame and rebuilt in the game frame from HorizonUp, the horizontal
        // prograde and forward × up (see OrbitGuidance.AimInFrame for that order). dv is always
        // set; false when no aim could be applied (undefined direction, no telemetry or autopilot).
        bool Steer(out Vector3d dv)
        {
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            double now = GeneralTools.Current_UT;
            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v = orbit.GetOrbitalVelocityAtUTZup(now);
            double mu = orbit.referenceBody.gravParameter;

            dv = GuidanceDeltaV(r, v, now, mu);
            OrbitGuidance.Split(dv, r, v, out double radial, out double horizontal, out double normal);
            if (double.IsNaN(radial) || double.IsNaN(horizontal) || double.IsNaN(normal) || dv.magnitude <= 0)
                return false;

            var telemetry = SASTool.getTelemetry();
            if (telemetry == null)
                return false;

            Vector HorizonUp = telemetry.HorizonUp;
            Vector3d up = HorizonUp.vector.normalized;
            Vector prograde = telemetry.OrbitMovementPrograde;
            prograde.Reframe(HorizonUp.coordinateSystem);
            Vector3d fwd = prograde.vector - Vector3d.Dot(prograde.vector, up) * up;
            if (fwd.magnitude < 1e-9)
                return false;
            fwd = fwd.normalized;

            Vector3d aim = OrbitGuidance.AimInFrame(radial, horizontal, normal, up, fwd);
            if (!(aim.magnitude > 0))
                return false;

            if (!normal_axis_checked && mode == Mode.Burn)
            {
                normal_axis_checked = true;
                if (!CheckNormalAxis(telemetry.OrbitMovementNormal, HorizonUp.coordinateSystem, up, fwd))
                {
                    set_throttle(0);
                    Fail("Frame check failed: aborted.");
                    return false;
                }
            }

            var autopilot = current_vessel.Autopilot;
            if (autopilot == null)
                return false;
            autopilot.Enabled = true;
            if (autopilot.AutopilotMode != AutopilotMode.StabilityAssist)
                autopilot.SetMode(AutopilotMode.StabilityAssist);
            autopilot.SAS.lockedMode = false;
            autopilot.SAS.SetTargetOrientation(new Vector(HorizonUp.coordinateSystem, aim), false);

            Rotation vessel_rotation = Rotation.Reframed(current_vessel.GetRotation(), HorizonUp.coordinateSystem);
            Vector3d forward = (vessel_rotation.localRotation * Vector3.up).normalized;
            angle = Vector3d.Angle(aim, forward);
            return true;
        }

        // Frame check, once per run, at ignition: our normal axis (forward × up, the image of the
        // Zup r × v) against the game's own orbit-normal marker. They must be parallel
        // (|dot| ≥ 0.9), otherwise the frames are not what the aim assumes: false when the burn has
        // a meaningful normal component (frame_check_strict), a warning only otherwise. +1 means the game's Normal is
        // r × v, as ManeuverCreator.GameNormalSign = +1 assumes for the nodes it creates; -1 means
        // GameNormalSign should be flipped (our aim does not depend on it). A check that cannot be
        // made (exception, NaN) is logged and passes.
        bool CheckNormalAxis(Vector gameNormal, ICoordinateSystem frame, Vector3d up, Vector3d fwd)
        {
            try
            {
                gameNormal.Reframe(frame);
                Vector3d ours = Vector3d.Cross(fwd, up).normalized;
                Vector3d theirs = gameNormal.vector.normalized;
                double dot = Vector3d.Dot(ours, theirs);
                string values = $"ours ({ours.x:n3}, {ours.y:n3}, {ours.z:n3}), " +
                    $"OrbitMovementNormal ({theirs.x:n3}, {theirs.y:n3}, {theirs.z:n3}), dot {dot:n3}";
                if (Math.Abs(dot) < MinNormalAxisDot)
                {
                    if (frame_check_strict)
                    {
                        logger.LogWarning($"[{Tag}] frame check failed: {values}");
                        return false;
                    }
                    logger.LogWarning($"[{Tag}] frame check failed, continuing (no meaningful normal component): {values}");
                    return true;
                }
                logger.LogInfo($"[{Tag}] normal axis check: {values} " +
                    $"(+1: the game's Normal is r x v, as GameNormalSign = {ManeuverCreator.GameNormalSign} assumes)");
            }
            catch (Exception e)
            {
                logger.LogWarning($"[{Tag}] normal axis check could not be made: {e.GetType().Name}: {e.Message}");
            }
            return true;
        }

        void Finish(double now, string reason)
        {
            set_throttle(0);
            TimeWarpTools.SetRateIndex(0, true);
            finished = true;
            status_line = DoneStatus(remaining_dv);
            LogOrbit($"finish ({reason})", now, remaining_dv, now - burn_start_UT);
        }

        protected void Refuse(string reason)
        {
            logger.LogInfo($"[{Tag}]{TagDetail} refused: {reason}");
            Fail(reason);
        }

        // "[Tag] what: ap … km pe … km inc …°, <target>, dv … m/s, T … s".
        protected void LogOrbit(string what, double now, double dv, double t)
        {
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;
            var el = OrbitMath.Elements(orbit.GetRelativePositionAtUTZup(now), orbit.GetOrbitalVelocityAtUTZup(now),
                body.gravParameter);
            logger.LogInfo($"[{Tag}] {what}: ap {(el.ApoapsisRadius - body.radius) / 1000:n1} km " +
                $"pe {(el.PeriapsisRadius - body.radius) / 1000:n1} km inc {el.InclinationRad * Rad2Deg:n2}°, " +
                $"{TargetText(body.radius)}, dv {dv:n1} m/s, T {t:n1} s");
        }

        protected virtual string TargetText(double bodyRadius) =>
            guidance != null && !(guidance is OrbitTargetGuidance) ? guidance.Describe() :
            $"target ap {(target.Ra - bodyRadius) / 1000:n1} km pe {(target.Rp - bodyRadius) / 1000:n1} km " +
            $"inc {target.InclinationRad * Rad2Deg:n2}°";

        void set_throttle(float value)
        {
            throttle = Mathf.Clamp01(value);
            current_vessel.SetThrottle(throttle);
        }

        protected virtual string DebugLine(double now) => $"start in {BurnStartUT - now:n1} s";

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            switch (mode)
            {
                case Mode.Warp:
                    st.Status($"Warping to the {BurnLabel}...");
                    break;
                case Mode.Align:
                    st.Warning(late_logged ? $"Late ignition: aligning for the {BurnLabel}..." : $"Aligning for the {BurnLabel}...");
                    break;
                case Mode.Burn:
                    st.Warning("Burning!");
                    if (ignition_dv > 0)
                        st.Progress(remaining_dv / ignition_dv, $"{remaining_dv:n1} m/s");
                    break;
            }
            st.Console(status_line);

            if (K2D2Settings.debug_mode.V)
            {
                st.Console(DebugLine(GeneralTools.Current_UT));
                st.Console($"angle {angle:n2}° throttle {throttle:n2}, turn {turn_rate:n2}°/s, lead {align_lead:n1} s, " +
                    $"warp x{TimeWarpTools.CurrentRate:0.##}");
            }
        }
    }
}

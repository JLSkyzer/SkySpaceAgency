using System;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.OrbitPlanning;
using K2D2.UI;
using KSP.Api;
using KSP.Sim;
using KTools;
using UnityEngine;
using UnityEngine.UIElements;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Node
{
    /// <summary>
    /// Closed-loop burn toward a target orbit (apsis radii and plane), without following any
    /// maneuver node.
    ///
    /// The game models a node as a finite burn sized on the stage active right now; with a nearly
    /// empty stage still active its burn duration and predicted trajectory are garbage, and an
    /// executor that follows that model (SAS Maneuver mode, ManeuverPlanSolver) misses the target.
    /// This controller instead re-evaluates OrbitGuidance.DeltaVToOrbit from the current orbit
    /// every frame and steers along it until it vanishes, so staging mid-burn does not matter.
    ///
    /// Sequence: warp until AlignLead seconds before the burn start, align during those seconds,
    /// burn from the start time. Refusals go through Fail(). CircularizeBurn derives from it with
    /// its own target (circular at the current radius) and start time (centered on the apsis).
    /// </summary>
    public class TargetOrbitBurn : ExecuteController
    {
        public ILogger logger;

        // Seconds of attitude alignment, with no warp, before the burn starts.
        public const double AlignLead = 20;
        // Above this angle (degrees) between the vessel's forward and the aim, the throttle is 0.
        const double MaxThrustAngle = 10;
        // Remaining Δv (m/s) under which the burn is done.
        const double DoneDeltaV = 0.2;
        // Once the remaining Δv has dropped under this (m/s), OvershootRises consecutive rises
        // mean the burn overshot: stop there.
        const double OvershootWatchDeltaV = 5;
        const int OvershootRises = 10;
        const float MinThrottle = 0.05f;
        // The throttle aims at burning the remaining Δv in this many seconds at full thrust.
        const double ThrottleTimeConstant = 1.0;
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

        // Warp toward the burn (the Node tab follows its own auto-warp setting).
        public bool allow_warp = true;

        public enum Mode { Warp, Align, Burn }
        public Mode mode = Mode.Warp;

        TargetOrbit target;
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

        // Sign of the target radial speed, fixed at ignition (0 = from the current state).
        int radial_sign;
        double last_burn_UT;
        double thrust_time;      // seconds of thrust while aligned since ignition
        double rising_time;      // seconds of thrust with |dv| rising, continuously
        double best_dv;          // lowest |dv| since ignition
        double stall_time;       // seconds of thrust while aligned without a new lowest |dv|
        bool stalled;

        protected KSPVessel current_vessel;
        protected BurndV burn_dV = new BurndV();
        WarpTo warp = new WarpTo();

        public TargetOrbitBurn() : this("SkySpaceAgency.TargetOrbitBurn") { }

        protected TargetOrbitBurn(string loggerName)
        {
            logger = ReduxLib.ReduxLib.GetLogger(loggerName);
        }

        public TargetOrbit Target => target;

        // Log tag, extra words after it ("AP"), and the burn's name in the UI texts.
        protected virtual string Tag => "TargetOrbitBurn";
        protected virtual string TagDetail => "";
        protected virtual string BurnLabel => "burn";

        /// <summary>
        /// Burns toward the target orbit from startUT (warping and aligning before it, see the
        /// class summary). A start time already past means burning now.
        /// </summary>
        public void Start(TargetOrbit target, double startUT)
        {
            this.target = target;
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

        // Resets the run. False, after a refusal, when there is no active vessel.
        protected bool BeginRun()
        {
            base.Start();
            mode = Mode.Warp;
            remaining_dv = 0;
            last_remaining_dv = double.NaN;
            ignition_dv = double.NaN;
            watching_overshoot = false;
            rises = 0;
            angle = 0;
            throttle = 0;
            normal_axis_checked = false;
            radial_sign = 0;
            last_burn_UT = double.NaN;
            thrust_time = 0;
            rising_time = 0;
            best_dv = double.PositiveInfinity;
            stall_time = 0;
            stalled = false;

            current_vessel = K2D2_Plugin.Instance.current_vessel;
            if (current_vessel?.VesselComponent == null)
            {
                Refuse("No active vessel.");
                return false;
            }
            current_vessel.SetThrottle(0);
            return true;
        }

        // Burns now when the start time has come, otherwise warps (if allowed) or aligns.
        protected void BeginWaiting(double now)
        {
            if (now >= BurnStartUT)
                StartBurning(now);
            else if (allow_warp && now < WarpEndUT)
                warp.Start_Ut(WarpEndUT);
            else
                StartAligning();
        }

        protected virtual double BurnStartUT => start_UT;
        double WarpEndUT => BurnStartUT - AlignLead;

        // Called while waiting, every frame, before the start time is checked.
        protected virtual void RefreshWhileWaiting(double now) { }

        protected virtual Vector3d GuidanceDeltaV(Vector3d r, Vector3d v, double mu) =>
            OrbitGuidance.DeltaVToOrbit(r, v, mu, target, radial_sign);

        Vector3d CurrentDeltaV(double now)
        {
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            return GuidanceDeltaV(orbit.GetRelativePositionAtUTZup(now), orbit.GetOrbitalVelocityAtUTZup(now),
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

        void StartAligning()
        {
            TimeWarpTools.SetRateIndex(0, false);
            mode = Mode.Align;
        }

        void StartBurning(double now)
        {
            TimeWarpTools.SetRateIndex(0, false);
            mode = Mode.Burn;
            burn_start_UT = now;
            ignition_dv = double.NaN;
            // Fixed for the whole burn, so the target does not flip side when the radial speed
            // crosses 0 mid-burn.
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            radial_sign = OrbitGuidance.RadialSign(orbit.GetRelativePositionAtUTZup(now),
                orbit.GetOrbitalVelocityAtUTZup(now), target.Ra, target.Rp);
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
            if (finished)
                return;
            if (current_vessel?.VesselComponent == null)
                return;

            double now = GeneralTools.Current_UT;

            if (mode == Mode.Warp || mode == Mode.Align)
            {
                RefreshWhileWaiting(now);
                set_throttle(0);

                if (now >= BurnStartUT)
                {
                    StartBurning(now);
                }
                else if (mode == Mode.Warp)
                {
                    if (!allow_warp || now >= WarpEndUT)
                    {
                        StartAligning();
                    }
                    else
                    {
                        warp.UT = WarpEndUT;
                        warp.Update();
                        status_line = $"Burn in {StrTool.DurationToString(BurnStartUT - now)} - {warp.status_line}";
                        if (warp.finished)
                            StartAligning();
                        return;
                    }
                }

                if (mode == Mode.Align)
                {
                    Steer(out _);
                    if (finished)
                        return;
                    status_line = $"Ignition in {StrTool.DurationToString(BurnStartUT - now)}, {angle:n1}° off";
                    return;
                }
            }

            Burn(now);
        }

        void Burn(double now)
        {
            bool steering = Steer(out Vector3d dv);
            if (finished)
                return;   // frame check failed

            double dt = double.IsNaN(last_burn_UT) ? 0 : Math.Max(0, now - last_burn_UT);
            last_burn_UT = now;
            // The throttle applied since the previous frame: did the engines push?
            bool thrusted = throttle > 0;

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

            rising_time = thrusted && rose ? rising_time + dt : 0;
            if (thrusted && aligned)
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

            burn_dV.Compute_Thrust();
            double accel = burn_dV.mass > 0 ? Thrust() / burn_dV.mass : 0;

            if (angle > MaxThrustAngle || accel <= 0)
                set_throttle(0);
            else
                set_throttle(Mathf.Clamp((float)(remaining_dv / (accel * ThrottleTimeConstant)), MinThrottle, 1));

            status_line = stalled ? StallStatus : BurningStatus(remaining_dv);
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

            dv = GuidanceDeltaV(r, v, mu);
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

            if (!normal_axis_checked)
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

        // Frame check, once per run: our normal axis (forward × up, the image of the Zup r × v)
        // against the game's own orbit-normal marker. They must be parallel (|dot| ≥ 0.9),
        // otherwise the frames are not what the aim assumes: false. +1 means the game's Normal is
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
                    logger.LogWarning($"[{Tag}] frame check failed: {values}");
                    return false;
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
                    st.Warning($"Aligning for the {BurnLabel}...");
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
                st.Console($"angle {angle:n2}° throttle {throttle:n2}");
            }
        }
    }
}

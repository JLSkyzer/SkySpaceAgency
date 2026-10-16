using System;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Landing;
using K2D2.OrbitPlanning;
using K2D2.UI;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using KTools;
using UnityEngine;
using UnityEngine.UIElements;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Node
{
    /// <summary>
    /// Closed-loop circularization at the next apoapsis or periapsis, without any maneuver node.
    ///
    /// The game models a node as a finite burn sized on the stage active right now; with a nearly
    /// empty stage still active (typical at the end of a Lift ascent) its burn duration and
    /// predicted trajectory are garbage, and an executor that follows that model (SAS Maneuver
    /// mode, ManeuverPlanSolver) burns its whole Δv in the wrong direction. This controller instead
    /// re-evaluates CircularizeGuidance.DeltaVToCircular from the current orbit every frame and
    /// steers along it until it vanishes, so staging mid-burn does not matter.
    ///
    /// Sequence: warp until AlignLead seconds before the burn start (apsis − T/2), align during
    /// those seconds, burn when apsis − T/2 is reached. Refusals go through Fail().
    /// </summary>
    public class CircularizeBurn : ExecuteController
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("SkySpaceAgency.CircularizeBurn");

        // Seconds of attitude alignment, with no warp, before the burn starts.
        public const double AlignLead = 20;
        // Real-time seconds between two refreshes of the burn duration and the apsis time.
        const float RefreshPeriod = 0.5f;
        // Above this angle (degrees) between the vessel's forward and the aim, the throttle is 0.
        const double MaxThrustAngle = 10;
        // Remaining Δv (m/s) under which the burn is done.
        const double DoneDeltaV = 1.0;
        // Once the remaining Δv has dropped under this (m/s), OvershootRises consecutive rises
        // mean the burn overshot: stop there.
        const double OvershootWatchDeltaV = 5;
        const int OvershootRises = 10;
        const float MinThrottle = 0.05f;
        // The throttle aims at burning the remaining Δv in this many seconds at full thrust.
        const double ThrottleTimeConstant = 1.0;
        // A refreshed apsis this much later than the previous one means the apsis was passed.
        const double ApsisJumpLimit = 60;

        // Warp toward the burn (the Node tab follows its own auto-warp setting).
        public bool allow_warp = true;

        public enum Mode { Warp, Align, Burn }
        public Mode mode = Mode.Warp;

        bool at_apoapsis = true;
        double period;
        double apsis_UT;
        double apsis_dv;       // |Δv| needed at the apsis, for the burn duration
        double burn_time;      // T, seconds
        float next_refresh_real;

        double burn_start_UT;
        double ignition_dv;
        double remaining_dv;
        double last_remaining_dv;
        bool watching_overshoot;
        int rises;
        double angle;
        float throttle;

        KSPVessel current_vessel;
        BurndV burn_dV = new BurndV();
        WarpTo warp = new WarpTo();
        ManeuverCreator maneuver_creator = new ManeuverCreator();

        string Apsis => at_apoapsis ? "AP" : "PE";

        public void Start(bool atApoapsis)
        {
            at_apoapsis = atApoapsis;
            Start();
        }

        public override void Start()
        {
            base.Start();
            mode = Mode.Warp;
            burn_time = 0;
            remaining_dv = 0;
            last_remaining_dv = double.NaN;
            watching_overshoot = false;
            rises = 0;
            angle = 0;
            throttle = 0;
            next_refresh_real = 0;

            current_vessel = K2D2_Plugin.Instance.current_vessel;
            if (current_vessel?.VesselComponent == null)
            {
                Refuse("No active vessel.");
                return;
            }
            current_vessel.SetThrottle(0);

            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;
            double mu = body.gravParameter;
            double now = GeneralTools.Current_UT;
            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v = orbit.GetOrbitalVelocityAtUTZup(now);

            LandingTargeting.OrbitalElementsFromStateVectors(r, v, mu,
                out double sma, out double apoapsisRadius, out double periapsisRadius);
            bool unbound = double.IsNaN(sma) || sma <= 0;
            bool descending = Vector3d.Dot(r, v) < 0;

            if (at_apoapsis)
            {
                if (unbound)
                {
                    Refuse("No apoapsis: the trajectory doesn't come back.");
                    return;
                }

                // Past the apoapsis and falling on a trajectory that hits the ground (or the
                // atmosphere): the next apoapsis comes one period later, after the impact.
                double groundRadius = body.radius + (body.hasAtmosphere ? body.atmosphereDepth : 0);
                if (descending && periapsisRadius < groundRadius)
                {
                    Refuse("Already past apoapsis and falling: the next apoapsis comes after impact. Burn prograde now, or use Circularize at PE once in a stable orbit.");
                    return;
                }
            }
            else if (unbound && !descending)
            {
                // A trajectory that never comes back passes its periapsis only once.
                Refuse("Can't circularize at PE - already past periapsis on this trajectory, it's now outbound for good.");
                return;
            }

            // A real period only exists on a bound orbit; an unbound one (PE only, by now) gets a
            // synthetic search window (see SearchWindowFromStateVectors).
            period = unbound
                ? LandingTargeting.SearchWindowFromStateVectors(r, v, mu)
                : LandingTargeting.OrbitalPeriodFromStateVectors(r, v, mu);

            apsis_UT = now + TimeToApsis(r, v, mu);
            apsis_dv = DeltaVAtApsis(r, v, mu, apsis_UT - now);
            if (double.IsNaN(apsis_dv))
            {
                Refuse("Could not compute the circularization burn.");
                return;
            }

            if (!RefreshBurnTime())
            {
                Refuse("No engine to circularize with.");
                return;
            }

            // Accepted: this run owns the plan. No node is created, the old ones would only
            // mislead (the map, SAS Maneuver mode).
            maneuver_creator.Update();
            maneuver_creator.RemoveAllNodes();

            logger.LogInfo($"[Circularize] {Apsis} accepted: ap {(apoapsisRadius - body.radius) / 1000:n1} km " +
                $"pe {(periapsisRadius - body.radius) / 1000:n1} km, apsis in {apsis_UT - now:n1} s, " +
                $"dv {apsis_dv:n1} m/s, T {burn_time:n1} s");

            next_refresh_real = Time.realtimeSinceStartup + RefreshPeriod;

            if (now >= BurnStartUT)
                StartBurning(now);
            else if (allow_warp && now < WarpEndUT)
                warp.Start_Ut(WarpEndUT);
            else
                StartAligning();
        }

        double BurnStartUT => apsis_UT - burn_time / 2;
        double WarpEndUT => BurnStartUT - AlignLead;

        double TimeToApsis(Vector3d r, Vector3d v, double mu)
        {
            return at_apoapsis
                ? LandingTargeting.TimeToNextApoapsis(r, v, mu, period)
                : LandingTargeting.TimeToNextPeriapsis(r, v, mu, period);
        }

        static double DeltaVAtApsis(Vector3d r, Vector3d v, double mu, double dt)
        {
            KeplerPropagator.Propagate(r, v, mu, dt, out Vector3d r_apsis, out Vector3d v_apsis);
            return CircularizeGuidance.DeltaVToCircular(r_apsis, v_apsis, mu).magnitude;
        }

        // Thrust (kN) of the active engines, or of every engine when none is active.
        double Thrust()
        {
            double thrust = burn_dV.active_thrust;
            if (thrust <= 0)
                thrust = burn_dV.full_thrust.magnitude;
            return thrust;
        }

        // Updates burn_time from the current thrust, mass and Isp. False when there is no thrust
        // (burn_time then keeps its previous value).
        bool RefreshBurnTime()
        {
            burn_dV.Compute_Thrust();
            double duration = BurnTiming.Duration(apsis_dv, Thrust(), burn_dV.mass, burn_dV.active_isp);
            if (double.IsNaN(duration))
                return false;
            burn_time = duration;
            return true;
        }

        // Live time-to-apsis and burn duration, every RefreshPeriod seconds of real time.
        void RefreshWhileWaiting(double now)
        {
            if (Time.realtimeSinceStartup < next_refresh_real)
                return;
            next_refresh_real = Time.realtimeSinceStartup + RefreshPeriod;

            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            double mu = orbit.referenceBody.gravParameter;
            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v = orbit.GetOrbitalVelocityAtUTZup(now);

            double new_apsis_UT = now + TimeToApsis(r, v, mu);
            // Much later means the search found the following passage: the apsis we were waiting
            // for has been passed. Keep it, so the burn starts now.
            if (new_apsis_UT <= apsis_UT + ApsisJumpLimit)
            {
                apsis_UT = new_apsis_UT;
                double dv = DeltaVAtApsis(r, v, mu, apsis_UT - now);
                if (!double.IsNaN(dv))
                    apsis_dv = dv;
            }

            RefreshBurnTime();
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
            LogOrbit("start", now, apsis_dv, burn_time);
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
                    status_line = $"Ignition in {StrTool.DurationToString(BurnStartUT - now)}, {angle:n1}° off";
                    return;
                }
            }

            Burn(now);
        }

        void Burn(double now)
        {
            bool steering = Steer(out Vector3d dv);

            remaining_dv = dv.magnitude;
            if (double.IsNaN(remaining_dv))
            {
                set_throttle(0);
                Refuse("Lost the orbit state: circularization stopped.");
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

            if (remaining_dv < OvershootWatchDeltaV)
                watching_overshoot = true;
            if (watching_overshoot && !double.IsNaN(last_remaining_dv))
            {
                rises = remaining_dv > last_remaining_dv ? rises + 1 : 0;
                if (rises >= OvershootRises)
                {
                    Finish(now, "overshoot");
                    return;
                }
            }
            last_remaining_dv = remaining_dv;

            burn_dV.Compute_Thrust();
            double accel = burn_dV.mass > 0 ? Thrust() / burn_dV.mass : 0;

            if (angle > MaxThrustAngle || accel <= 0)
                set_throttle(0);
            else
                set_throttle(Mathf.Clamp((float)(remaining_dv / (accel * ThrottleTimeConstant)), MinThrottle, 1));

            status_line = $"Circularizing: {remaining_dv:n1} m/s left";
        }

        // Points SAS along the current circularization Δv (StabilityAssist + a target orientation
        // in the HorizonUp frame, same pattern as TouchDown.checkDirection) and measures the angle
        // between the vessel's forward and that aim. dv is always set; false when no aim could be
        // applied (undefined direction, no telemetry or autopilot).
        bool Steer(out Vector3d dv)
        {
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            double now = GeneralTools.Current_UT;
            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v = orbit.GetOrbitalVelocityAtUTZup(now);
            double mu = orbit.referenceBody.gravParameter;

            dv = CircularizeGuidance.DeltaVToCircular(r, v, mu);
            CircularizeGuidance.Split(dv, r, v, out double radial, out double horizontal);
            if (double.IsNaN(radial) || double.IsNaN(horizontal) || dv.magnitude <= 0)
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

            Vector3d aim = (up * radial + fwd * horizontal).normalized;

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

        void Finish(double now, string reason)
        {
            set_throttle(0);
            finished = true;
            status_line = $"Circularized ({remaining_dv:n1} m/s left)";
            LogOrbit($"finish ({reason})", now, remaining_dv, now - burn_start_UT);
        }

        void Refuse(string reason)
        {
            logger.LogInfo($"[Circularize] {Apsis} refused: {reason}");
            Fail(reason);
        }

        void LogOrbit(string what, double now, double dv, double t)
        {
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;
            LandingTargeting.OrbitalElementsFromStateVectors(orbit.GetRelativePositionAtUTZup(now),
                orbit.GetOrbitalVelocityAtUTZup(now), body.gravParameter,
                out _, out double apoapsisRadius, out double periapsisRadius);
            logger.LogInfo($"[Circularize] {what}: ap {(apoapsisRadius - body.radius) / 1000:n1} km " +
                $"pe {(periapsisRadius - body.radius) / 1000:n1} km, dv {dv:n1} m/s, T {t:n1} s");
        }

        void set_throttle(float value)
        {
            throttle = Mathf.Clamp01(value);
            current_vessel.SetThrottle(throttle);
        }

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            switch (mode)
            {
                case Mode.Warp:
                    st.Status("Warping to the circularization burn...");
                    break;
                case Mode.Align:
                    st.Warning("Aligning for the circularization burn...");
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
                st.Console($"apsis {Apsis} in {apsis_UT - GeneralTools.Current_UT:n1} s, T {burn_time:n1} s");
                st.Console($"angle {angle:n2}° throttle {throttle:n2}");
            }
        }
    }
}

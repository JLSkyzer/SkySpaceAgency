using K2D2.KSPService;
using K2D2.Landing;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using KTools;
using UnityEngine;

namespace K2D2.Node
{
    /// <summary>
    /// Closed-loop circularization at the next apoapsis or periapsis, without any maneuver node:
    /// a TargetOrbitBurn whose target is the circular orbit at the current radius (re-evaluated
    /// every frame through CircularizeGuidance) and whose start is centered on the apsis
    /// (apsis − T/2, refreshed while waiting).
    ///
    /// Used by Lift's final phase (refuse_past_apoapsis = false: already past the apoapsis and
    /// falling, it burns at once, the guidance also cancelling the vertical speed) and by the Node
    /// tab's "Circularize at AP/PE" buttons (which refuse that case).
    /// </summary>
    public class CircularizeBurn : TargetOrbitBurn
    {
        // Real-time seconds between two refreshes of the burn duration and the apsis time.
        const float RefreshPeriod = 0.5f;
        // A refreshed apsis this much later than the previous one means the apsis was passed.
        const double ApsisJumpLimit = 60;

        // AP only: refuse when already past the apoapsis and falling toward the ground (Node
        // tab). False (Lift): burn now instead.
        public bool refuse_past_apoapsis = true;

        bool at_apoapsis = true;
        double period;
        double apsis_UT;
        double apsis_dv;       // |Δv| needed at the apsis, for the burn duration
        double burn_time;      // T, seconds
        float next_refresh_real;

        ManeuverCreator maneuver_creator = new ManeuverCreator();

        public CircularizeBurn() : base("SkySpaceAgency.CircularizeBurn") { }

        string Apsis => at_apoapsis ? "AP" : "PE";

        protected override string Tag => "Circularize";
        protected override string TagDetail => $" {Apsis}";
        protected override string BurnLabel => "circularization burn";

        public void Start(bool atApoapsis)
        {
            at_apoapsis = atApoapsis;
            Start();
        }

        public override void Start()
        {
            burn_time = 0;
            next_refresh_real = 0;
            if (!BeginRun())
                return;

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
            bool burn_now = false;

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
                    if (refuse_past_apoapsis)
                    {
                        Refuse("Already past apoapsis and falling: the next apoapsis comes after impact. Burn prograde now, or use Circularize at PE once in a stable orbit.");
                        return;
                    }
                    burn_now = true;
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

            if (burn_now)
            {
                // The burn starts now (apsis − T/2 is in the past) and the guidance circularizes
                // at the current radius, cancelling the downward speed.
                apsis_UT = now;
                apsis_dv = CircularizeGuidance.DeltaVToCircular(r, v, mu).magnitude;
                logger.LogInfo($"[Circularize] AP: already past apoapsis and falling, burning now");
            }
            else
            {
                apsis_UT = now + TimeToApsis(r, v, mu);
                apsis_dv = DeltaVAtApsis(r, v, mu, apsis_UT - now);
            }
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
            BeginWaiting(now);
        }

        protected override double BurnStartUT => apsis_UT - burn_time / 2;

        protected override Vector3d GuidanceDeltaV(Vector3d r, Vector3d v, double ut, double mu) =>
            CircularizeGuidance.DeltaVToCircular(r, v, mu);

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

        // Updates burn_time from the current thrust, mass and Isp. False when there is no thrust
        // (burn_time then keeps its previous value).
        bool RefreshBurnTime()
        {
            double duration = EstimateBurnTime(apsis_dv);
            if (double.IsNaN(duration))
                return false;
            burn_time = duration;
            return true;
        }

        // Live time-to-apsis and burn duration, every RefreshPeriod seconds of real time.
        protected override void RefreshWhileWaiting(double now)
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

        protected override void LogStart(double now) => LogOrbit("start", now, apsis_dv, burn_time);

        protected override string TargetText(double bodyRadius) => "target circular at the current radius";

        protected override string BurningStatus(double dv) => $"Circularizing: {dv:n1} m/s left";
        protected override string DoneStatus(double dv) => $"Circularized ({dv:n1} m/s left)";

        protected override string DebugLine(double now) =>
            $"apsis {Apsis} in {apsis_UT - now:n1} s, T {burn_time:n1} s";
    }
}

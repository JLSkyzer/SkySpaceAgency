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
    // The boost-back burn (K2-D2 1.4.0, IanMealworm), for landing a recovered stage (or anything
    // else on a suborbital trajectory inside the atmosphere) on the precision-landing target - flown
    // closed-loop, like the KSP 1 BoosterGuidance mod does it, rather than as a maneuver node.
    //
    // A K2-D2 feature: compiled and tested here, flown only in the K2-D2 flavour
    // (ModIdentity.IsK2D2, BoostBackStart). Its pure parts (Size, SolveByDirection, SolveDirect,
    // Miss, Throttle, LinedUp, BudgetProblem) are static so they are tested on a fake body.
    //
    // Several times a second, from where the vessel is right now: work out the burn that would
    // bring the airless coast down on the target, point the engines along it, and throttle by
    // how much of it is left (tapering off at the end); stop when nothing is left. Re-solving all
    // the way through takes care of what a node can't: the burn being long, the vessel turning
    // into it, thrust and mass changing, and air. (Flown as a node, the big booster's burn was
    // planned at 839 m/s, trimmed to 724 m/s by Redux's planned-trajectory prediction, flown
    // exactly - and came down 30 km short: Redux's prediction for a long burn starting inside the
    // atmosphere was badly off.)
    //
    // Solving the burn: a booster at separation is still climbing and heading away (the replays:
    // 33-38 km up, 620-660 m/s downrange and 480-590 m/s up). Straight back along the ground
    // track isn't the cheapest - some of the burn can go up or down - so burn directions from a
    // little below the horizon to steeply up are each sized to land on the target (with whatever
    // sideways part that needs), and the smallest is kept. Once little is left, the burn is
    // solved directly instead: the smallest change that cancels the miss, from how the landing
    // point moves with each direction of burn.
    //
    // Every vector is in ImpactModel's axes (the body frame at the model's epoch), never mixed with
    // the Zup vectors of Precision/.
    public class BoostBackBurn : ExecuteController
    {
        readonly ILogger logger = ModIdentity.Logger("BoostBackBurn");
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        enum Step { WaitForEngines, Turn, Burn }
        Step step;

        // Delta-v that has to be left for the landing burn.
        public const double landing_reserve_mps = 600;
        // Right after a replay loads, the game reports no delta-v or thrust for a second or two.
        const double wait_for_engines_s = 10;
        // The burn is re-solved this often.
        const double solve_interval_s = 0.2;
        // Unpowered flip: engines on once pointing within this of the burn, off again beyond the
        // second.
        public const double ignite_within_deg = 5, pause_beyond_deg = 15;
        // Powered flip: the throttle while pointing more than 90° off the burn - enough for the
        // engine gimbals to help the turn. (The big booster took 48 s to turn unpowered, and the
        // burn grew from 437 to 547 m/s while it coasted away.)
        public const double flip_throttle = 0.25;
        // Throttle tapers off over about the last this-many seconds of full thrust.
        public const double taper_s = 2;
        public const double min_throttle = 0.02;
        // Done when less than this is left - or, like BoosterGuidance, once the last few m/s stop
        // shrinking: inside the air, drag keeps moving the airless landing point, and the first
        // closed-loop flights spent 50-75 s at minimum throttle chasing the last 10 m/s.
        const double done_mps = 0.5;
        const double stall_below_mps = 10, stall_progress_mps = 0.5, stall_s = 3;

        // Burn directions tried, in degrees above the horizon, back along the starting ground track
        // (negative sizes point forward, for a target ahead). Later solves only look near the last
        // answer.
        public const double min_pitch_deg = -20, max_pitch_deg = 80, pitch_step_deg = 5, pitch_window_deg = 10;
        const int solve_iterations = 20;
        public const double solved_m = 30;
        const double max_burn_mps = 4000;
        // Below this much left, the burn is solved directly (see the class comment).
        const double direct_solve_below_mps = 50;

        readonly AtmoAttitudeHold hold = new AtmoAttitudeHold();
        readonly KSPVessel current_vessel;

        double wait_start_UT, last_solve_UT, turn_start_UT;
        Vector3d back;          // the starting ground track, backwards - fixed for the whole burn
        bool have_back;
        double best_pitch;
        bool have_pitch;
        Vector3d burn;          // what's left to burn, in ImpactModel's axes
        double first_burn_mps;
        double best_left_mps, best_left_UT;

        public BoostBackBurn()
        {
            current_vessel = SkySpaceAgencyPlugin.Instance.current_vessel;
        }

        public override void Start()
        {
            base.Start();
            step = Step.WaitForEngines;
            wait_start_UT = GeneralTools.Current_UT;
            last_solve_UT = double.NaN;
            have_back = have_pitch = false;
            burn = Vector3d.zero;
            first_burn_mps = double.NaN;
            best_left_mps = double.PositiveInfinity;
            best_left_UT = GeneralTools.Current_UT;
        }

        public void Release()
        {
            hold.Release();
            current_vessel?.SetThrottle(0);
        }

        static double Accel(BurndV burn_dV) => burn_dV.active_dv > 0 ? burn_dV.active_dv : burn_dV.full_dv;

        public override void Update()
        {
            if (finished)
                return;
            var vessel = current_vessel?.VesselComponent;
            if (vessel == null)
                return;
            double now = GeneralTools.Current_UT;
            // Our landing rule: 1x during every powered or guided landing phase in the air.
            TimeWarpTools.SetRateIndex(0, false);

            if (step == Step.WaitForEngines)
            {
                bool engines_known = (vessel.VesselDeltaV?.TotalDeltaVVac ?? 0) > 0 && Accel(LandingPilot.Instance.burn_dV) > 0;
                status_line = "Boost-back: waiting for the engines' figures";
                if (!engines_known && now - wait_start_UT < wait_for_engines_s)
                    return;
                step = Step.Turn;
                turn_start_UT = now;
            }

            if (double.IsNaN(last_solve_UT) || now - last_solve_UT >= solve_interval_s)
            {
                last_solve_UT = now;
                if (!SolveFromHere(vessel, now))
                    return;
            }

            double left = burn.magnitude;
            if (left < best_left_mps - stall_progress_mps)
            {
                best_left_mps = left;
                best_left_UT = now;
            }
            bool stalled = step == Step.Burn && left < stall_below_mps && now - best_left_UT > stall_s;
            if (left < done_mps || stalled)
            {
                Finish(vessel, now);
                return;
            }

            hold.HoldDirection(burn);
            double max_accel = Accel(LandingPilot.Instance.burn_dV);
            double error = hold.attitude_error_deg;
            bool powered = LandingPilot.Instance.settings.boost_back_flip.V == FlipStyle.Powered;
            if (step == Step.Turn)
            {
                if (LinedUp(error))
                {
                    step = Step.Burn;
                    logger.LogInfo(string.Format(Inv, "[Boost-back] lined up after {0:0} s ({1} flip), {2:0} m/s to burn at {3:0} m",
                        now - turn_start_UT, powered ? "powered" : "unpowered", left, vessel.AltitudeFromSeaLevel));
                }
                else if (!powered)
                {
                    current_vessel.SetThrottle(0);
                    status_line = $"Boost-back: turning for a {left:n0} m/s burn ({error:n0}° to go)";
                    return;
                }
            }

            current_vessel.SetThrottle((float)Throttle(powered, error, left, max_accel));
            status_line = step == Step.Turn
                ? $"Boost-back: powered flip, {error:n0}° to go, {left:n0} m/s to burn"
                : $"Boost-back: {left:n0} m/s to go";
        }

        // Unpowered flip: the burn starts once the vessel points within ignite_within_deg of it.
        public static bool LinedUp(double attitude_error_deg) => attitude_error_deg < ignite_within_deg;

        // The throttle for `left` m/s still to burn, pointing attitude_error_deg off the burn.
        // Powered flip: the engines run from the start, so their gimbals help turn the vessel;
        // thrust while still pointing the wrong way is kept low, and grows as the nose comes
        // round. The re-solving takes care of whatever it pushes the wrong way. Unpowered: full
        // thrust within pause_beyond_deg, none beyond. Both taper off over the last taper_s.
        public static double Throttle(bool powered, double attitude_error_deg, double left_mps, double max_accel)
        {
            double pointing = powered
                ? flip_throttle + (1 - flip_throttle) * Math.Max(0, Math.Cos(attitude_error_deg * Math.PI / 180))
                : attitude_error_deg > pause_beyond_deg ? 0 : 1;
            double taper = max_accel > 0 ? Math.Min(1, left_mps / (max_accel * taper_s)) : 0;
            return pointing > 0 ? Math.Max(min_throttle, pointing * taper) : 0;
        }

        // Null when the vessel has the Δv for the burn and the landing after it; otherwise the
        // refusal, with the numbers. available <= 0: not known (the game has no figure), not refused.
        public static string BudgetProblem(double burn_mps, double available_mps)
        {
            if (available_mps <= 0 || burn_mps + landing_reserve_mps <= available_mps)
                return null;
            return string.Format(Inv, "The boost-back needs {0:0} m/s and landing about {1:0} m/s more, but only {2:0} m/s is left.",
                burn_mps, landing_reserve_mps, available_mps);
        }

        // Re-solves the burn from the current state. False (and finished set) when there's nothing
        // to fly.
        bool SolveFromHere(VesselComponent vessel, double now)
        {
            var settings = LandingPilot.Instance.settings;
            var model = new ImpactModel(vessel.mainBody, now);
            var site = new LandingTargeting.TargetSite(model, settings.target_latitude.V, settings.target_longitude.V, 0);
            model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);

            if (!have_back)
            {
                if (!GroundTrackBack(r, v, out back))
                {
                    Abort("Boost-back needs the vessel to be moving over the ground.");
                    return false;
                }
                have_back = true;
            }

            bool solved;
            Vector3d dv;
            if (burn.magnitude > 0 && burn.magnitude < direct_solve_below_mps)
                solved = SolveDirect(model, site, r, v, now, out dv);
            else
            {
                double start_size = have_pitch ? Vector3d.Dot(burn, back) : (v - r.normalized * Vector3d.Dot(v, r.normalized)).magnitude;
                solved = SolveByDirection(model, site, r, v, now, back, have_pitch ? best_pitch : double.NaN, start_size,
                    out dv, out double pitch);
                if (solved)
                    best_pitch = pitch;
                have_pitch = solved;
            }
            if (!solved)
            {
                if (step == Step.Burn)
                {
                    // Lost the solution partway (very close to done, say) - stop rather than guess.
                    Finish(vessel, now);
                    return false;
                }
                Abort("Couldn't find a boost-back burn that reaches the target.");
                return false;
            }
            burn = dv;

            if (double.IsNaN(first_burn_mps))
            {
                first_burn_mps = dv.magnitude;
                double available = vessel.VesselDeltaV?.TotalDeltaVVac ?? 0;
                logger.LogInfo(string.Format(Inv, "[Boost-back] needs {0:0} m/s pointing {1:0}° above the horizon | {2:0} m/s available, {3:0} m/s kept for landing",
                    first_burn_mps, best_pitch, available, landing_reserve_mps));
                string problem = BudgetProblem(first_burn_mps, available);
                if (problem != null)
                {
                    Abort(problem);
                    return false;
                }
            }
            return true;
        }

        // The starting ground track, backwards. False when the vessel barely moves over the ground.
        public static bool GroundTrackBack(Vector3d r, Vector3d v, out Vector3d back)
        {
            Vector3d up = r.normalized;
            Vector3d horizontal = v - up * Vector3d.Dot(v, up);
            back = Vector3d.zero;
            if (horizontal.magnitude < 1)
                return false;
            back = -horizontal.normalized;
            return true;
        }

        void Abort(string reason)
        {
            logger.LogInfo($"[Boost-back] {reason}");
            Release();
            Fail(reason);
        }

        void Finish(VesselComponent vessel, double now)
        {
            current_vessel.SetThrottle(0);
            hold.Release();
            var settings = LandingPilot.Instance.settings;
            var model = new ImpactModel(vessel.mainBody, now);
            var site = new LandingTargeting.TargetSite(model, settings.target_latitude.V, settings.target_longitude.V, 0);
            model.StateAt(vessel.Orbit, now, out Vector3d r, out Vector3d v);
            string airless = LandingTargeting.Miss(model, site, r, v, now, 0, 0, out double a, out double b, out _, out _)
                ? Math.Sqrt(a * a + b * b).ToString("0", Inv) + " m" : "-";
            var redux = ReduxImpactPrediction.GetFlown(vessel);
            string redux_miss = redux.usable
                ? LandingPilot.HaversineDistanceMeters(redux.latitude, redux.longitude, settings.target_latitude.V, settings.target_longitude.V,
                    vessel.mainBody.radius).ToString("0", Inv) + " m"
                : redux.state.ToString();
            logger.LogInfo(string.Format(Inv, "[Boost-back] done after {0:0} s ({1:0} m/s planned at the start) at {2:0} m | airless coast lands {3} from the target, Redux {4}",
                now - turn_start_UT, first_burn_mps, vessel.AltitudeFromSeaLevel, airless, redux_miss));
            status_line = "Boost-back done";
            finished = true;
        }

        // Burn directions from back along the ground track (pitched up/down), each sized - with a
        // sideways part - to land on the site; the smallest wins. previous_pitch: the last answer
        // (NaN: none yet), only directions within pitch_window_deg of it are tried.
        public static bool SolveByDirection(ImpactModel model, LandingTargeting.TargetSite site, Vector3d r, Vector3d v, double UT,
            Vector3d back, double previous_pitch, double start_size, out Vector3d best_dv, out double best_pitch)
        {
            best_dv = Vector3d.zero;
            best_pitch = double.NaN;
            Vector3d up = r.normalized;
            Vector3d side = Vector3d.Cross(r, v).normalized;
            bool have_pitch = !double.IsNaN(previous_pitch);
            double from = have_pitch ? previous_pitch - pitch_window_deg : min_pitch_deg;
            double to = have_pitch ? previous_pitch + pitch_window_deg : max_pitch_deg;

            double best = double.PositiveInfinity;
            for (double pitch = from; pitch <= to; pitch += pitch_step_deg)
            {
                double angle = pitch * Math.PI / 180;
                Vector3d direction = (back * Math.Cos(angle) + up * Math.Sin(angle)).normalized;
                if (!Size(model, site, r, v, UT, direction, side, start_size, out Vector3d dv))
                    continue;
                if (dv.magnitude < best)
                {
                    best = dv.magnitude;
                    best_dv = dv;
                    best_pitch = pitch;
                }
            }
            return !double.IsInfinity(best);
        }

        // The size along `direction`, plus a sideways part, that lands on the site (Newton's
        // method on the two ground offsets).
        public static bool Size(ImpactModel model, LandingTargeting.TargetSite site, Vector3d r, Vector3d v, double UT,
            Vector3d direction, Vector3d side, double start_size, out Vector3d dv,
            double start_side = 0, double aim_a = 0, double aim_b = 0)
        {
            double m = start_size, c = start_side;
            dv = Vector3d.zero;
            for (int i = 0; i < solve_iterations; i++)
            {
                if (!Miss(model, site, r, v, UT, direction * m + side * c, out double a, out double b, aim_a, aim_b))
                    return false;
                if (a * a + b * b < solved_m * solved_m)
                {
                    dv = direction * m + side * c;
                    return true;
                }
                const double dm = 5, dc = 2;
                if (!Miss(model, site, r, v, UT, direction * (m + dm) + side * c, out double am, out double bm, aim_a, aim_b)
                    || !Miss(model, site, r, v, UT, direction * m + side * (c + dc), out double ac, out double bc, aim_a, aim_b))
                    return false;
                double j11 = (am - a) / dm, j21 = (bm - b) / dm, j12 = (ac - a) / dc, j22 = (bc - b) / dc;
                double det = j11 * j22 - j12 * j21;
                if (Math.Abs(det) < 1e-9)
                    return false;
                // Newton steps can overshoot wildly far from the answer; keep them moderate.
                m += Math.Max(-500, Math.Min(500, (-a * j22 + b * j12) / det));
                c += Math.Max(-500, Math.Min(500, (-b * j11 + a * j21) / det));
                m = Math.Max(-max_burn_mps, Math.Min(max_burn_mps, m));
            }
            return false;
        }

        // Near the end: the smallest burn that cancels the miss, from how each direction of burn
        // moves the landing point (a 2x3 linear solve, least-norm).
        public static bool SolveDirect(ImpactModel model, LandingTargeting.TargetSite site, Vector3d r, Vector3d v, double UT,
            out Vector3d dv, double aim_a = 0, double aim_b = 0)
        {
            dv = Vector3d.zero;
            if (!Miss(model, site, r, v, UT, Vector3d.zero, out double a, out double b, aim_a, aim_b))
                return false;
            const double d = 1;
            var axes = new[] { new Vector3d(1, 0, 0), new Vector3d(0, 1, 0), new Vector3d(0, 0, 1) };
            var ja = new double[3];
            var jb = new double[3];
            for (int i = 0; i < 3; i++)
            {
                if (!Miss(model, site, r, v, UT, axes[i] * d, out double ai, out double bi, aim_a, aim_b))
                    return false;
                ja[i] = (ai - a) / d;
                jb[i] = (bi - b) / d;
            }
            // dv = J^T (J J^T)^-1 (-miss)
            double s11 = ja[0] * ja[0] + ja[1] * ja[1] + ja[2] * ja[2];
            double s12 = ja[0] * jb[0] + ja[1] * jb[1] + ja[2] * jb[2];
            double s22 = jb[0] * jb[0] + jb[1] * jb[1] + jb[2] * jb[2];
            double det = s11 * s22 - s12 * s12;
            if (Math.Abs(det) < 1e-12)
                return false;
            double ya = (-a * s22 + b * s12) / det;
            double yb = (-b * s11 + a * s12) / det;
            dv = new Vector3d(ja[0] * ya + jb[0] * yb, ja[1] * ya + jb[1] * yb, ja[2] * ya + jb[2] * yb);
            return true;
        }

        // Where the airless coast after an instant burn `dv` (ImpactModel's axes) comes down,
        // relative to the site - or to a point offset (aim_a, aim_b) from it.
        public static bool Miss(ImpactModel model, LandingTargeting.TargetSite site, Vector3d r, Vector3d v, double UT, Vector3d dv,
            out double a, out double b, double aim_a = 0, double aim_b = 0)
        {
            a = b = 0;
            if (!model.Impact(r, v + dv, UT, site.radius, out double impact_UT, out Vector3d p))
                return false;
            site.Offset(model.BodyFixed(p, impact_UT), out a, out b);
            a -= aim_a;
            b -= aim_b;
            return true;
        }

        public override void UpdateInfoRows(Action<string, string> addRow)
        {
            addRow("Burn Left", $"{burn.magnitude:n1} m/s");
            if (SkySpaceAgencySettings.debug_mode.V)
                addRow("Attitude Error", $"{hold.attitude_error_deg:n1}°");
        }
    }
}

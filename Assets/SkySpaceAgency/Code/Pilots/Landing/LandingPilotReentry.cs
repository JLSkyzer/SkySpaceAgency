using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using K2D2.KSPService;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using K2D2.OrbitPlanning;
using KSP.Modules;
using KSP.Sim;
using KSP.Sim.impl;
using KSP2FlightAssistant.MathLibrary;
using KTools;

namespace K2D2.Landing
{
    // The parachute precision return (sub-project 3, part 3): Parachute mode with precision on an
    // atmospheric body. The game side of the pure pieces in Precision/ (ReentryPredictor through
    // JettisonAwarePredictor, ReentryAccuracy, DragCalibration, ReentryFailurePolicy, ReentryStart),
    // mirroring part 2 (LandingPilotPrecision.cs), whose phase controller it reuses.
    //
    // Start: engine, orbit (closed, periapsis above the atmosphere + 5 km), jettison plan,
    // ReentryContext (atmosphere, capsule, chutes), rotation sign (Mode.Calibrate as part 2), then
    // the plan: plane change if needed and the deorbit, with the capsule predicted from the
    // jettison on; refused when the chutes never open in the model or the Δv is short.
    // Flight: PlaneChange → Deorbit → MidCourse (PrecisionPhase, closed-loop TargetOrbitBurn) →
    // ReentryCoast (warp to the jettison, none below the interface + 10 km) → Jettison (one stage,
    // Jettison.cs) → Parachute (ParachuteDescent: retrograde, chutes armed, the game opens them).
    // A failure once the periapsis is in the atmosphere never stops the pilot: it lands under
    // parachutes without target (ReentryFailurePolicy), except when the active vessel is not the
    // capsule. After the landing: the final distance and a suggested reentry_drag_scale
    // (DragCalibration), applied only by the player. Every line logged here starts with
    // "[Landing] reentry:".
    public partial class LandingPilot
    {
        public Jettison jettison;

        // This run is a parachute precision return (precision_run is set with it, for the shared
        // phases and readouts).
        internal bool reentry_run = false;
        // From the start: fixed during the run (the capsule's drag and chutes, the air tables).
        Atmosphere reentry_air;
        ReentryVehicle reentry_capsule;
        double reentry_depth;
        double reentry_interface_UT = double.NaN;
        double reentry_jettison_UT = double.NaN;
        internal double reentry_expected_m = double.NaN;
        bool reentry_warp_stopped = false;

        // The prediction from the state right after the jettison (or at the interface): what the
        // landing is compared to, and where the calibration starts.
        bool reentry_reference = false;
        string reentry_reference_name;
        Vector3d ref_r, ref_v;
        double ref_ut;
        Precision.Body ref_body;
        Vector3d ref_target;
        StopPrediction ref_stop;
        ReentryTrace ref_trace;
        bool reentry_interface_seen = false;

        // Heating and chute opening logs.
        double reentry_peak_g = 0, reentry_peak_g_altitude = double.NaN;
        double reentry_max_temp_ratio = 0;
        string reentry_max_temp_part = null;
        float next_heat_log = 0;
        bool chute_semi_logged = false, chute_full_logged = false;

        // Calibration suggestion (LandingUI shows it with an Apply button).
        internal double suggested_drag_scale = double.NaN;

        public const double ReadoutIntervalReentry = 5;     // s of real time between predictions above the interface
        public const double HeatLogInterval = 10;           // s of real time
        public const double NoWarpAboveInterface = 10000;   // m: no warp below the interface + this
        public const double CoastWarpMargin = 30;           // s: the warp ends this long before the jettison
        public const double JettisonAltitudeMargin = 2000;  // m above the interface: jettison now, whatever the UT says
        public const double CorrectionReserveReentry = 20;  // m/s, the correction's budget and Δv reserve

        bool ReentryMode(Mode m) => m == Mode.ReentryCoast || m == Mode.Jettison;

        void ResetReentry()
        {
            reentry_run = false;
            reentry_interface_UT = double.NaN;
            reentry_jettison_UT = double.NaN;
            reentry_expected_m = double.NaN;
            reentry_warp_stopped = false;
            reentry_reference = false;
            reentry_reference_name = null;
            reentry_interface_seen = false;
            reentry_peak_g = 0;
            reentry_peak_g_altitude = double.NaN;
            reentry_max_temp_ratio = 0;
            reentry_max_temp_part = null;
            chute_semi_logged = chute_full_logged = false;
            suggested_drag_scale = double.NaN;
        }

        static string Km(double m) => double.IsNaN(m) ? "?" : $"{m / 1000:n2} km";

        // ---- Start ----

        // After the plain parachute checks. Sets last_error on a refusal.
        bool CheckCanStartReentry()
        {
            ResetPrecision();
            ResetReentry();
            var vessel = current_vessel.VesselComponent;
            burn_dV.Compute_Thrust();
            LogEngines();
            if (!(burn_dV.active_thrust > 0) && !(burn_dV.full_thrust.magnitude > 0))
            {
                last_error = ReentryStart.NoEngineMessage;
                logger.LogInfo("[Landing] reentry: refused: no engine thrust");
                return false;
            }

            IKeplerPatch orbit = vessel.Orbit;
            var body = orbit.referenceBody;
            double now = GeneralTools.Current_UT;
            OrbitElements el = OrbitMath.Elements(orbit.GetRelativePositionAtUTZup(now), orbit.GetOrbitalVelocityAtUTZup(now), body.gravParameter);
            string orbit_error = ReentryStart.CheckOrbit(el.Eccentricity, el.PeriapsisRadius, body.radius, body.atmosphereDepth);
            logger.LogInfo($"[Landing] reentry: start orbit: ap {(el.ApoapsisRadius - body.radius) / 1000:n1} km pe {(el.PeriapsisRadius - body.radius) / 1000:n1} km, " +
                $"e {el.Eccentricity:0.0000}, inc {el.InclinationRad * 180 / Math.PI:n2}°, atmosphere {body.atmosphereDepth:n0} m" +
                (orbit_error != null ? " -> " + orbit_error : ""));
            if (orbit_error != null)
            {
                last_error = "Precision return: " + orbit_error;
                return false;
            }

            if (!Jettison.TryPlan(vessel, out JettisonPlanResult jplan, out string jlog))
            {
                logger.LogInfo($"[Landing] reentry: refused: jettison plan: {jlog}");
                last_error = jplan.Message;
                return false;
            }
            logger.LogInfo($"[Landing] reentry: start jettison plan: {jlog}");

            if (!ReentryContext.TryBuild(this, out ReentryContext ctx, out string reason, log: true))
            {
                last_error = "Precision return: " + reason;
                return false;
            }
            reentry_air = ctx.Air;
            reentry_capsule = ctx.Capsule;
            reentry_depth = ctx.Air.Depth;

            if (!ctx.Precision.RotationSignCalibrated)
            {
                precision_run = true;
                reentry_run = true;
                precision_calibrating = true;
                if (probe_failure != null)
                    probe_body = null;
                probe_failure = null;
                logger.LogInfo("[Landing] reentry: rotation sign not calibrated by the surface speed: measuring it " +
                    $"(rotation probe, ≥ {RotationProbe.MinSeconds:n0} s of game time)");
                return true;
            }
            return PlanReentryStart(ctx.Precision);
        }

        // The predictor of the searches for the current body (lead check until the jettison).
        JettisonAwarePredictor ReentryPredictorFor(Precision.Body body, bool jettisoned = false) =>
            new JettisonAwarePredictor(body, reentry_air, reentry_capsule, jettisoned);

        double ReentryPeriapsis(Precision.Body body) => DeorbitSearch.ReentryPeriapsis(body, reentry_depth);

        // A refused plan's message: the chutes never opening is told by the traces.
        static string ReentryRefusal(JettisonAwarePredictor p, string prefix, string reason) =>
            p.Succeeded == 0 && p.NeverOpen > 0 ? ReentryStart.ChutesNeverOpenMessage : prefix + reason;

        bool PlanReentryStart(PrecisionContext ctx)
        {
            var vessel = current_vessel.VesselComponent;
            double now = ctx.UT;
            double R = ctx.Body.Radius;
            JettisonAwarePredictor predictor = ReentryPredictorFor(ctx.Body);
            double rp = ReentryPeriapsis(ctx.Body);

            var timer = Stopwatch.StartNew();
            PlaneChangePlan pc = PlaneChange.Plan(ctx.R, ctx.V, now, ctx.Body, ctx.Vehicle, ctx.TargetDirNow, predictor.Fn, rp);
            timer.Stop();
            DeorbitPlan d = pc.Deorbit;
            logger.LogInfo($"[Landing] reentry: start plan {(pc.Ok ? "ok" : "refused")} ({timer.Elapsed.TotalMilliseconds:n0} ms, {predictor.Calls} predictions, " +
                $"{predictor.NeverOpen} with the chutes never open) | descent periapsis {(rp - R) / 1000:n1} km | plane change " +
                (pc.Needed ? $"node +{pc.BurnUT - now:n0} s, Δv {pc.DeltaV:n1} m/s, inc {pc.Target.InclinationRad * 180 / Math.PI:n2}°, {pc.Iterations} iterations" : "not needed") +
                $" | deorbit: {DeorbitText(d, now)}" + (pc.Reason != null ? $" | {pc.Reason}" : ""));
            if (!pc.Ok)
            {
                precision_run = false;
                reentry_run = false;
                last_error = ReentryRefusal(predictor, "Precision return: " + (pc.Needed ? "Plane change: " : "Deorbit: "), pc.Reason);
                return false;
            }

            // The deorbit's state: now, or after the plane change (the velocity turned at its node).
            Vector3d rs = ctx.R, vs = ctx.V;
            double us = now;
            if (pc.Needed)
                AfterPlaneChange(ctx, pc, out rs, out vs, out us);
            var impulse = new Impulse { UT = d.BurnUT, DeltaV = d.DeltaV };
            ReentryTrace trace = default;
            Func<double, StopPrediction> at = s =>
            {
                ReentryVehicle c = reentry_capsule;
                c.DragScale = s;
                return predictor.Predict(rs, vs, us, impulse, c, out _);
            };
            double scale = reentry_capsule.DragScale > 0 ? reentry_capsule.DragScale : 1;
            ExpectedAccuracy accuracy = ReentryAccuracy.Estimate(at, scale, R);
            StopPrediction nominal = predictor.Predict(rs, vs, us, impulse, out trace);

            double dv_remaining = vessel.VesselDeltaV != null ? vessel.VesselDeltaV.TotalDeltaVActual : double.NaN;
            double needed = (pc.Needed ? pc.DeltaV : 0) + d.DeltaV.magnitude + CorrectionReserveReentry;
            string dv_error = ReentryStart.CheckDeltaV(needed, dv_remaining);
            logger.LogInfo($"[Landing] reentry: start plan prediction: {trace} | expected accuracy: drag ±{accuracy.ErrorFraction * 100:n0} % " +
                $"(scale {scale:0.00}{(ReentryAccuracy.IsCalibrated(scale) ? ", calibrated" : "")}) moves the stop {F(accuracy.ShiftLowM)} / {F(accuracy.ShiftHighM)} m " +
                $"-> within about {accuracy.ExpectedM / 1000:n1} km | Δv: plane change {(pc.Needed ? pc.DeltaV : 0):n1} + deorbit {d.DeltaV.magnitude:n1} " +
                $"+ correction reserve {CorrectionReserveReentry:n0} = {needed:n0} m/s (x{ReentryStart.DeltaVMargin}), vessel {F(dv_remaining)} m/s" +
                (dv_error != null ? " -> " + dv_error : ""));
            if (!nominal.Ok)
            {
                precision_run = false;
                reentry_run = false;
                last_error = JettisonAwarePredictor.ChutesNeverOpen(nominal, trace)
                    ? ReentryStart.ChutesNeverOpenMessage : "Precision return: " + nominal.Reason;
                return false;
            }
            if (dv_error != null)
            {
                precision_run = false;
                reentry_run = false;
                last_error = dv_error;
                return false;
            }
            if (double.IsNaN(dv_remaining) || double.IsInfinity(dv_remaining))
                last_warning = LandingFeasibility.DeltaVUnknownMessage;

            precision_lon_sign = ctx.Reference.LonSign;
            precision_target_terrain = ctx.TargetTerrainM;
            precision_target_lat = settings_atmo.target_latitude.V;
            precision_target_lon = settings_atmo.target_longitude.V;
            reentry_expected_m = accuracy.ExpectedM;
            reentry_interface_UT = trace.InterfaceUT;
            reentry_jettison_UT = JettisonAwarePredictor.JettisonUT(trace);
            precision_summary = ReentryAccuracy.PlanMessage(d.DeltaV.magnitude, d.BurnUT - now, accuracy.ExpectedM) +
                (pc.Needed ? $" (after a plane change of {pc.DeltaV:n0} m/s)" : "");
            precision_distance_m = SurfaceGeometry.GreatCircle(nominal.StopDirNow, ctx.TargetDirNow, R);
            precision_run = true;
            reentry_run = true;
            precision_calibrating = false;
            return true;
        }

        // The state right after the plane change: the velocity turned into the new plane at its node.
        static void AfterPlaneChange(PrecisionContext ctx, PlaneChangePlan pc, out Vector3d r, out Vector3d v, out double ut)
        {
            KeplerPropagator.Propagate(ctx.R, ctx.V, ctx.Body.Mu, pc.BurnUT - ctx.UT, out r, out Vector3d vOld);
            Vector3d axis = r.normalized;
            Vector3d n1 = Vector3d.Cross(r, vOld).normalized;
            Vector3d n2 = pc.Target.PlaneNormal.normalized;
            v = OrbitMath.Rotate(vOld, axis, Math.Atan2(Vector3d.Dot(Vector3d.Cross(n1, n2), axis), Vector3d.Dot(n1, n2)));
            ut = pc.BurnUT;
        }

        // Mode.Calibrate for a precision return: the probe is done, plan.
        void UpdateCalibrateReentry()
        {
            precision_calibrating = false;
            if (!PrecisionContext.TryBuild(this, out PrecisionContext ctx, out string reason, log: true))
            {
                StopWithError("Precision return: " + reason);
                return;
            }
            if (!PlanReentryStart(ctx))
            {
                StopWithError(last_error);
                return;
            }
            setMode(Mode.PlaneChange);
        }

        // ---- Burns (PrecisionPhase planners) ----

        PrecisionPhase.Plan PlanReentryPlaneChange()
        {
            if (!BuildContext(out PrecisionContext ctx, out string error))
                return PhaseError(error);
            JettisonAwarePredictor p = ReentryPredictorFor(ctx.Body);
            var timer = Stopwatch.StartNew();
            PlaneChangePlan pc = PlaneChange.Plan(ctx.R, ctx.V, ctx.UT, ctx.Body, ctx.Vehicle, ctx.TargetDirNow, p.Fn, ReentryPeriapsis(ctx.Body));
            timer.Stop();
            double now = ctx.UT;
            logger.LogInfo($"[Landing] reentry: plan plane change: needed {pc.Needed}, ok {pc.Ok}" +
                (pc.Needed ? $", node +{pc.BurnUT - now:n1} s (start +{pc.BurnStartUT - now:n1} s), Δv {pc.DeltaV:n2} m/s, " +
                    $"target inc {pc.Target.InclinationRad * 180 / Math.PI:n3}°, {pc.Iterations} iterations" : "") +
                $", {p.Calls} predictions, {timer.Elapsed.TotalMilliseconds:n0} ms | deorbit after it: {DeorbitText(pc.Deorbit, now)}" +
                (pc.Reason != null ? $" | {pc.Reason}" : ""));
            if (!pc.Ok)
                return PhaseError(ReentryRefusal(p, pc.Needed ? "Plane change: " : "Deorbit: ", pc.Reason));
            if (!pc.Needed)
            {
                earliest_deorbit_UT = double.NaN;
                return PhaseSkip;
            }
            earliest_deorbit_UT = PlaneChange.EarliestDeorbitUT(pc.BurnUT, pc.DeltaV, ctx.Vehicle);
            return new PrecisionPhase.Plan { Target = pc.Target, StartUT = pc.BurnStartUT, PlannedDeltaV = pc.DeltaV };
        }

        PrecisionPhase.Plan PlanReentryDeorbit()
        {
            if (!BuildContext(out PrecisionContext ctx, out string error))
                return PhaseError(error);
            double now = ctx.UT;
            double mu = ctx.Body.Mu;
            double rpTarget = ReentryPeriapsis(ctx.Body);
            double r = ctx.R.magnitude;
            double guess = Math.Max(0, ctx.V.magnitude - Math.Sqrt(mu * 2 * rpTarget / (r * (r + rpTarget))));
            Precision.Vehicle vehicle = ctx.Vehicle;
            double earliest = PrecisionPhases.EarliestDeorbitUT(now, earliest_deorbit_UT,
                BurnTiming.Duration(guess, vehicle.ThrustKN, vehicle.MassT, vehicle.Isp));

            JettisonAwarePredictor p = ReentryPredictorFor(ctx.Body);
            var timer = Stopwatch.StartNew();
            DeorbitPlan plan = DeorbitSearch.Find(ctx.R, ctx.V, now, ctx.Body, ctx.TargetDirNow, earliest, p.Fn, rpTarget);
            timer.Stop();
            logger.LogInfo($"[Landing] reentry: plan deorbit (earliest +{earliest - now:n0} s, {timer.Elapsed.TotalMilliseconds:n0} ms, " +
                $"{p.NeverOpen} prediction(s) with the chutes never open): {DeorbitText(plan, now)}, interface +{F(plan.Stop.BrakeStartUT - now)} s");
            if (!plan.Ok)
                return PhaseError(ReentryRefusal(p, "Deorbit: ", plan.Reason));

            KeplerPropagator.Propagate(ctx.R, ctx.V, mu, plan.BurnUT - now, out Vector3d rb, out Vector3d vb);
            double dv = plan.DeltaV.magnitude;
            double start = BurnTiming.CenteredStart(plan.BurnUT, BurnTiming.Duration(dv, vehicle.ThrustKN, vehicle.MassT, vehicle.Isp), now);
            SetInterface(plan.Stop.BrakeStartUT);
            return new PrecisionPhase.Plan
            {
                Target = TargetOrbit.FromState(rb, vb + plan.DeltaV, mu),
                StartUT = start,
                PlannedDeltaV = dv,
            };
        }

        void SetInterface(double interfaceUT)
        {
            if (double.IsNaN(interfaceUT))
                return;
            predicted_brake_start_UT = interfaceUT;
            reentry_interface_UT = interfaceUT;
            reentry_jettison_UT = interfaceUT - JettisonAwarePredictor.JettisonLead;
        }

        PrecisionPhase.Plan PlanReentryCorrection()
        {
            if (!BuildContext(out PrecisionContext ctx, out string error))
                return PhaseError(error);
            double lead = PrecisionPhases.PlanLead;
            double ut_c = ctx.UT + lead;
            double mu = ctx.Body.Mu;
            KeplerPropagator.Propagate(ctx.R, ctx.V, mu, lead, out Vector3d rc, out Vector3d vc);
            PrecisionStart.AtLaterTime(ctx.Body, ctx.TargetDirNow, lead, out Precision.Body bodyC, out Vector3d targetC);

            JettisonAwarePredictor p = ReentryPredictorFor(bodyC);
            var timer = Stopwatch.StartNew();
            Correction c = CourseCorrection.Solve(rc, vc, ut_c, bodyC, targetC, CorrectionReserveReentry, p.Fn);
            timer.Stop();
            bool fly = PrecisionPhases.ShouldFly(c);
            logger.LogInfo($"[Landing] reentry: plan mid-course correction at +{lead:n0} s: ok {c.Ok}, Δv {c.DeltaV.magnitude:n3} m/s " +
                $"(max {CorrectionReserveReentry:n0}), predicted miss {c.MissBeforeM:n0} -> {c.MissAfterM:n0} m, clipped {c.Clipped}, " +
                $"{c.PredictCalls} predictions, {timer.Elapsed.TotalMilliseconds:n0} ms -> {(fly ? "flown" : "not flown")}" +
                (c.Reason != null ? $" ({c.Reason})" : ""));
            if (!c.Ok)
                Warn("Mid-course correction refused: the course is kept as it is.");
            if (!fly)
                return PhaseSkip;
            double dv = c.DeltaV.magnitude;
            Precision.Vehicle vehicle = ctx.Vehicle;
            return new PrecisionPhase.Plan
            {
                Target = TargetOrbit.FromState(rc, vc + c.DeltaV, mu),
                StartUT = BurnTiming.CenteredStart(ut_c, BurnTiming.Duration(dv, vehicle.ThrustKN, vehicle.MassT, vehicle.Isp), ctx.UT),
                PlannedDeltaV = dv,
                DoneDeltaV = PrecisionPhases.CorrectionDoneDeltaV,
            };
        }

        // ---- Light state (no engine needed: also after the jettison) ----

        // The vessel's state now, the body (calibrated rotation sign) and the target, in the
        // frames of PrecisionContext. False when the game does not give them.
        bool ReentryState(out Vector3d r, out Vector3d v, out double ut, out Precision.Body body, out Vector3d target)
        {
            r = v = target = new Vector3d(0, 0, 0);
            ut = GeneralTools.Current_UT;
            body = default;
            try
            {
                IKeplerPatch orbit = current_vessel?.VesselComponent?.Orbit;
                var cb = orbit?.referenceBody;
                if (cb == null)
                    return false;
                r = orbit.GetRelativePositionAtUTZup(ut);
                v = orbit.GetOrbitalVelocityAtUTZup(ut);
                double omega = BodyRotation.AngularSpeed(cb.rotationPeriod);
                body = new Precision.Body
                {
                    Mu = cb.gravParameter,
                    Radius = cb.radius,
                    Omega = BodyRotation.AngularVelocity(omega, rotation_sign),
                    TerrainHeight = p => TerrainHeightAt(cb, p),
                };
                PrecisionContext.GameLatLon(cb, r, out double lat, out double lon);
                var reference = new SurfaceGeometry.Reference { Position = r, Latitude = lat, Longitude = lon, LonSign = precision_lon_sign };
                target = SurfaceGeometry.Direction(reference, precision_target_lat, precision_target_lon);
                return !double.IsNaN(r.magnitude) && !double.IsNaN(v.magnitude) && !double.IsNaN(target.magnitude);
            }
            catch (Exception e)
            {
                logger.LogWarning("[Landing] reentry: state not available: " + e.Message);
                return false;
            }
        }

        // The capsule's coast and reentry from now, without the lead check (readouts).
        bool PredictNow(out Vector3d r, out Vector3d v, out double ut, out Precision.Body body, out Vector3d target,
            out StopPrediction stop, out ReentryTrace trace)
        {
            stop = default;
            trace = default;
            if (!ReentryState(out r, out v, out ut, out body, out target))
                return false;
            stop = ReentryPredictorFor(body, jettisoned: true).Predict(r, v, ut, null, out trace);
            return true;
        }

        // After a reentry burn: the Δv used and the prediction from the orbit reached.
        void OnReentryPhaseEnd()
        {
            double used = burn_dV.burned_dV - phase_burned_start;
            string name = precision_phase.Name;
            string after;
            if (mode == Mode.PlaneChange)
            {
                IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
                var body = orbit.referenceBody;
                double t = GeneralTools.Current_UT;
                OrbitElements el = OrbitMath.Elements(orbit.GetRelativePositionAtUTZup(t), orbit.GetOrbitalVelocityAtUTZup(t), body.gravParameter);
                after = $"orbit ap {(el.ApoapsisRadius - body.radius) / 1000:n2} km pe {(el.PeriapsisRadius - body.radius) / 1000:n2} km inc {el.InclinationRad * 180 / Math.PI:n3}°";
            }
            else if (PredictNow(out _, out _, out double now, out Precision.Body b, out Vector3d target, out StopPrediction stop, out ReentryTrace trace) && stop.Ok)
            {
                SetInterface(trace.InterfaceUT);
                precision_distance_m = SurfaceGeometry.GreatCircle(stop.StopDirNow, target, b.Radius);
                after = $"predicted miss {precision_distance_m:n0} m{AlongCross(stop, target, b.Radius)}, interface +{trace.InterfaceUT - now:n0} s, " +
                    $"jettison +{reentry_jettison_UT - now:n0} s | {trace}";
            }
            else
                after = "no prediction: " + stop.Reason;
            logger.LogInfo($"[Landing] reentry: phase {name} end{(precision_phase.Skipped ? " (not needed)" : "")}: " +
                $"Δv used {used:n2} m/s (planned {precision_phase.PlannedDeltaV:n2}) | {after}");
            phase_log.Add($"{name} {used:n1}/{precision_phase.PlannedDeltaV:n1}");
        }

        static string AlongCross(StopPrediction stop, Vector3d target, double radius)
        {
            if (!(stop.TrackDirNow.sqrMagnitude > 0))
                return "";
            SurfaceGeometry.Split(stop.StopDirNow, target, stop.TrackDirNow, radius, out double along, out double cross);
            return $" (target {along:n0} m along the track, {cross:n0} m across)";
        }

        static ReentryStage ReentryStageOf(Mode m)
        {
            switch (m)
            {
                case Mode.PlaneChange: return ReentryStage.PlaneChange;
                case Mode.Deorbit: return ReentryStage.Deorbit;
                case Mode.MidCourse: return ReentryStage.MidCourse;
                case Mode.ReentryCoast: return ReentryStage.Coast;
                default: return ReentryStage.Jettison;
            }
        }

        // A reentry burn failed (refused plan, refused or aborted burn): ReentryFailurePolicy.
        void OnReentryPhaseFailed(string reason)
        {
            bool enters;
            string orbit_text;
            try
            {
                IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
                var body = orbit.referenceBody;
                double now = GeneralTools.Current_UT;
                OrbitElements el = OrbitMath.Elements(orbit.GetRelativePositionAtUTZup(now), orbit.GetOrbitalVelocityAtUTZup(now), body.gravParameter);
                enters = ReentryFailurePolicy.EntersAtmosphere(el.PeriapsisRadius, body.radius, body.atmosphereDepth, collision_detected);
                orbit_text = $"pe {(el.PeriapsisRadius - body.radius) / 1000:n2} km (atmosphere {body.atmosphereDepth / 1000:n0} km)";
            }
            catch (Exception e)
            {
                enters = true;
                orbit_text = $"orbit not read ({e.GetType().Name}: {e.Message})";
            }
            ReentryAction action = ReentryFailurePolicy.Decide(ReentryStageOf(mode), enters);
            logger.LogWarning($"[Landing] reentry: phase {precision_phase.Name} failed: {reason} | {orbit_text}, " +
                $"collision predicted {collision_detected}, enters the atmosphere {enters} -> {action}");
            switch (action)
            {
                case ReentryAction.Stop:
                    StopWithError(reason);
                    break;
                case ReentryAction.Skip:
                    current_vessel.SetThrottle(0);
                    Warn(ReentryFailurePolicy.SkipMessage(reason));
                    OnReentryPhaseEnd();
                    current_executor.setController(null);
                    setMode(Mode.ReentryCoast);
                    break;
                default:
                    ReentryFallBack(ReentryFailurePolicy.FallBackMessage(reason));
                    break;
            }
        }

        // Drops the precision return and lands under parachutes without target (ParachuteDescent
        // arms the chutes and holds retrograde).
        void ReentryFallBack(string message)
        {
            logger.LogWarning($"[Landing] reentry: {message} | Δv used {burn_dV.burned_dV:n1} m/s ({string.Join(", ", phase_log)})");
            precision_run = false;
            reentry_run = false;
            precision_calibrating = false;
            last_warning = message;
            precision_final_message = message;
            current_vessel.SetThrottle(0);
            TimeWarpTools.SetRateIndex(0, false);
            current_executor.setController(null);
            setMode(Mode.Parachute);
        }

        // ---- Coast, jettison ----

        void StartReentryCoast()
        {
            current_vessel.SetThrottle(0);
            reentry_warp_stopped = false;
            double now = GeneralTools.Current_UT;
            if (settings.auto_warp.V && reentry_jettison_UT - CoastWarpMargin > now)
            {
                current_executor.setController(warp_to);
                warp_to.Start_Ut(reentry_jettison_UT - CoastWarpMargin);
            }
            else
                current_executor.setController(null);
            logger.LogInfo($"[Landing] reentry: phase coast start: jettison +{F(reentry_jettison_UT - now)} s, interface +{F(reentry_interface_UT - now)} s, " +
                $"altitude {altitude:n0} m, auto warp {settings.auto_warp.V} (none below {(reentry_depth + NoWarpAboveInterface) / 1000:n0} km)");
        }

        void UpdateReentryCoast()
        {
            current_vessel.SetSpeedMode(SpeedDisplayMode.Surface);
            SASTool.setAutoPilot(AutopilotMode.Retrograde);
            double now = GeneralTools.Current_UT;
            double sea = current_vessel.VesselComponent.AltitudeFromSeaLevel;
            bool low = sea < reentry_depth + NoWarpAboveInterface;
            if (!reentry_warp_stopped && current_executor.sub_controler == warp_to)
                warp_to.UT = reentry_jettison_UT - CoastWarpMargin;
            if ((low || now >= reentry_jettison_UT - CoastWarpMargin) && !reentry_warp_stopped)
            {
                reentry_warp_stopped = true;
                TimeWarpTools.SetRateIndex(0, false);
                current_executor.setController(null);
                logger.LogInfo($"[Landing] reentry: coast: warp stopped at {sea:n0} m, jettison +{F(reentry_jettison_UT - now)} s");
            }
            if (now >= reentry_jettison_UT || sea < reentry_depth + JettisonAltitudeMargin)
                setMode(Mode.Jettison);
        }

        // The active vessel holds every capsule part of the jettison plan.
        bool ActiveHoldsCapsule()
        {
            HashSet<string> capsule = jettison.plan.CapsuleIds;
            VesselComponent active = current_vessel?.GetActiveSimVessel();
            if (capsule == null || active?.SimulationObject?.PartOwner == null)
                return false;
            var ids = new HashSet<string>(active.SimulationObject.PartOwner.Parts.Where(p => p != null).Select(p => p.Guid));
            return capsule.All(ids.Contains);
        }

        void OnJettisonDone()
        {
            current_executor.setController(null);
            if (!ActiveHoldsCapsule())
            {
                OnJettisonFailed("the active vessel does not hold the capsule after the jettison");
                return;
            }
            if (PredictNow(out Vector3d r, out Vector3d v, out double now, out Precision.Body body, out Vector3d target, out StopPrediction stop, out ReentryTrace trace))
            {
                SetReference("jettison", r, v, now, body, target, stop, trace);
                if (stop.Ok)
                    precision_distance_m = SurfaceGeometry.GreatCircle(stop.StopDirNow, target, body.Radius);
            }
            var vessel = current_vessel.VesselComponent;
            logger.LogInfo($"[Landing] reentry: phase jettison end: {jettison.PartsBefore} -> {vessel.SimulationObject.PartOwner.PartCount} part(s) " +
                $"({jettison.plan.ServiceIds.Count} dropped, {jettison.plan.CapsuleIds.Count} kept) at T{now:F1}, {altitude:n0} m, " +
                $"mass {vessel.totalMass:n3} t (modelled capsule {reentry_capsule.MassT:n3} t), interface planned +{F(reentry_interface_UT - now)} s");
            setMode(Mode.Parachute);
        }

        void SetReference(string name, Vector3d r, Vector3d v, double ut, Precision.Body body, Vector3d target, StopPrediction stop, ReentryTrace trace)
        {
            reentry_reference = stop.Ok;
            reentry_reference_name = name;
            ref_r = r;
            ref_v = v;
            ref_ut = ut;
            ref_body = body;
            ref_target = target;
            ref_stop = stop;
            ref_trace = trace;
            logger.LogInfo($"[Landing] reentry: prediction from the {name}: " + (stop.Ok
                ? $"touchdown +{stop.StopUT - ut:n0} s, {SurfaceGeometry.GreatCircle(stop.StopDirNow, target, body.Radius):n0} m from the target" +
                  $"{AlongCross(stop, target, body.Radius)} | interface +{trace.InterfaceUT - ut:n0} s, chutes semi +{F(trace.SemiUT - ut)} s " +
                  $"at {F(trace.SemiAltitude)} m ({F(trace.SemiAirSpeed)} m/s), full +{F(trace.FullUT - ut)} s at {F(trace.FullAltitude)} m | {trace} | " +
                  $"state r ({r.x:F0}, {r.y:F0}, {r.z:F0}) v ({v.x:F2}, {v.y:F2}, {v.z:F2}) at T{ut:F2}"
                : "refused: " + stop.Reason));
        }

        // Jettison failed (Jettison.Fail, or the active vessel check after it).
        void OnJettisonFailed(string reason)
        {
            bool activated = jettison.Activated;
            bool holds = !activated || ActiveHoldsCapsule();
            ReentryAction action = ReentryFailurePolicy.Decide(ReentryStage.Jettison, true, activated, holds, jettison.capsule_lost);
            logger.LogWarning($"[Landing] reentry: phase jettison failed: {reason} | stage activated {activated}, active vessel holds the capsule {holds}, " +
                $"capsule lost {jettison.capsule_lost} -> {action}");
            current_executor.setController(null);
            if (action == ReentryAction.StopCapsuleLost)
            {
                StopWithError(ReentryFailurePolicy.CapsuleLostMessage);
                return;
            }
            ReentryFallBack(ReentryFailurePolicy.ServiceModuleKeptMessage(reason));
        }

        // The landing pilot survives the vessel change of the jettison split (the game makes the
        // capsule's piece active, VesselChangedMessage resets every pilot) when the new active
        // vessel holds the capsule; Jettison checks the rest.
        internal bool KeepThroughVesselChange()
        {
            if (!(reentry_run && isRunning && mode == Mode.Jettison && jettison.Activated))
                return false;
            bool holds = ActiveHoldsCapsule();
            logger.LogInfo($"[Landing] reentry: vessel change during the jettison: the active vessel holds the capsule {holds} -> " +
                (holds ? "the landing goes on" : "the landing stops"));
            return holds;
        }

        // ---- Descent ----

        // Every frame of a precision return outside the burns: coast and descent readouts and logs.
        void UpdateReentry()
        {
            // Predicted distance while waiting for a burn or coasting, above the interface.
            if ((PrecisionPhaseMode(mode) && !precision_phase.Burning) || mode == Mode.ReentryCoast)
            {
                float now_real = UnityEngine.Time.realtimeSinceStartup;
                if (now_real >= next_precision_readout)
                {
                    next_precision_readout = now_real + (float)ReadoutIntervalReentry;
                    precision_distance_label = "Predicted Distance";
                    if (PredictNow(out _, out _, out _, out Precision.Body b, out Vector3d target, out StopPrediction stop, out ReentryTrace trace))
                    {
                        precision_distance_m = stop.Ok ? SurfaceGeometry.GreatCircle(stop.StopDirNow, target, b.Radius) : double.NaN;
                        // The coast's jettison time follows the latest prediction.
                        if (stop.Ok && mode == Mode.ReentryCoast)
                            SetInterface(trace.InterfaceUT);
                    }
                }
            }
            if (mode == Mode.ReentryCoast)
                UpdateReentryCoast();
            if (mode == Mode.Jettison)
            {
                current_vessel.SetSpeedMode(SpeedDisplayMode.Surface);
                SASTool.setAutoPilot(AutopilotMode.Retrograde);
            }
            if (mode == Mode.Parachute)
                UpdateReentryDescent();
        }

        void UpdateReentryDescent()
        {
            var vessel = current_vessel.VesselComponent;
            double sea = vessel.AltitudeFromSeaLevel;
            double now = GeneralTools.Current_UT;
            if (!reentry_interface_seen && sea <= reentry_depth)
            {
                reentry_interface_seen = true;
                bool hadReference = reentry_reference;
                if (ReentryState(out Vector3d r, out Vector3d v, out double ut, out Precision.Body body, out Vector3d target))
                {
                    logger.LogInfo($"[Landing] reentry: interface at T{ut:F1} (predicted T{F(reentry_interface_UT, "F1")}, {F(ut - reentry_interface_UT, "n1")} s late), " +
                        $"speed {current_vessel.VesselVehicle.SurfaceSpeed:n0} m/s surface, {v.magnitude:n0} m/s orbital");
                    if (!hadReference)
                    {
                        StopPrediction stop = ReentryPredictorFor(body, jettisoned: true).Predict(r, v, ut, null, out ReentryTrace trace);
                        SetReference("interface", r, v, ut, body, target, stop, trace);
                    }
                }
            }

            // Predicted distance above the interface.
            float now_real = UnityEngine.Time.realtimeSinceStartup;
            if (sea > reentry_depth && now_real >= next_precision_readout)
            {
                next_precision_readout = now_real + (float)ReadoutIntervalReentry;
                if (PredictNow(out _, out _, out _, out Precision.Body b, out Vector3d target, out StopPrediction stop, out _))
                    precision_distance_m = stop.Ok ? SurfaceGeometry.GreatCircle(stop.StopDirNow, target, b.Radius) : double.NaN;
                precision_distance_label = "Predicted Distance";
            }
            else if (sea <= reentry_depth)
                precision_distance_label = "Predicted Distance (at the interface)";

            if (sea <= reentry_depth && !chute_full_logged)
                UpdateHeating(vessel, sea, now_real);
            UpdateChuteOpening(vessel, now);
        }

        // Peak deceleration and the hottest part (temperature / max temperature) in the air, logged
        // every HeatLogInterval s until the chutes are fully open: the 0.35 descent periapsis was
        // chosen without a heating model.
        void UpdateHeating(VesselComponent vessel, double sea, float now_real)
        {
            double g = vessel.geeForce;
            if (g > reentry_peak_g)
            {
                reentry_peak_g = g;
                reentry_peak_g_altitude = sea;
            }
            double hottest = 0;
            string hottest_part = null;
            try
            {
                foreach (PartComponent part in vessel.SimulationObject.PartOwner.Parts)
                {
                    if (part == null || !(part.MaxTemp > 0))
                        continue;
                    double ratio = part.Temperature / part.MaxTemp;
                    if (ratio > hottest)
                    {
                        hottest = ratio;
                        hottest_part = part.PartName;
                    }
                }
            }
            catch (Exception) { /* temperatures not readable: logged as 0 */ }
            if (hottest > reentry_max_temp_ratio)
            {
                reentry_max_temp_ratio = hottest;
                reentry_max_temp_part = hottest_part;
            }
            if (now_real < next_heat_log)
                return;
            next_heat_log = now_real + (float)HeatLogInterval;
            logger.LogInfo($"[Landing] reentry: heating: altitude {sea:n0} m, speed {current_vessel.VesselVehicle.SurfaceSpeed:n0} m/s, Mach {vessel.MachNumber:n2}, " +
                $"{g:n2} g (peak {reentry_peak_g:n2} g at {F(reentry_peak_g_altitude)} m), hottest part {hottest_part ?? "?"} at {hottest * 100:n0} % of its max temperature " +
                $"(peak {reentry_max_temp_ratio * 100:n0} %, {reentry_max_temp_part ?? "?"}) | predicted peak drag {ref_trace.PeakDecel / BrakeSimulator.G0:n2} g");
        }

        // The game opening the chutes (semi, then full), against the prediction.
        void UpdateChuteOpening(VesselComponent vessel, double now)
        {
            if (chute_full_logged)
                return;
            ParachuteCounts counts = Parachutes.Count(vessel);
            bool semi = counts.SemiDeployed + counts.Deployed > 0;
            bool full = counts.Deployed > 0;
            if (semi && !chute_semi_logged)
            {
                chute_semi_logged = true;
                LogChuteOpening("open (semi-deployed)", vessel, now, ref_trace.SemiUT, ref_trace.SemiAltitude, ref_trace.SemiDirNow);
                logger.LogInfo($"[Landing] reentry: heating summary at the chute opening: peak {reentry_peak_g:n2} g at {F(reentry_peak_g_altitude)} m " +
                    $"(predicted {ref_trace.PeakDecel / BrakeSimulator.G0:n2} g), hottest part {reentry_max_temp_part ?? "?"} at {reentry_max_temp_ratio * 100:n0} % of its max temperature");
            }
            if (full && !chute_full_logged)
            {
                chute_full_logged = true;
                LogChuteOpening("fully deployed", vessel, now, ref_trace.FullUT, ref_trace.FullAltitude, new Vector3d(0, 0, 0));
            }
        }

        void LogChuteOpening(string what, VesselComponent vessel, double now, double predictedUT, double predictedAltitude, Vector3d predictedDirAtRef)
        {
            var states = new List<string>();
            try
            {
                foreach (PartComponent part in vessel.SimulationObject.PartOwner.Parts)
                {
                    if (part != null && part.TryGetModuleData<PartComponentModule_Parachute, Data_Parachute>(out Data_Parachute chute) && chute != null)
                        states.Add($"{part.PartName} {chute.deployState.GetValue()} safety {chute.deploymentSafetyState.GetValue()}");
                }
            }
            catch (Exception e)
            {
                states.Add($"not readable ({e.GetType().Name})");
            }
            string position = "";
            if (reentry_reference && ReentryState(out Vector3d r, out _, out _, out Precision.Body body, out _))
            {
                Vector3d here = SurfaceGeometry.AtTime(r, ref_body.Omega, -(now - ref_ut));
                position = predictedDirAtRef.sqrMagnitude > 0
                    ? $", {SurfaceGeometry.GreatCircle(here, predictedDirAtRef, ref_body.Radius):n0} m from the predicted opening point"
                    : "";
                position += $", {SurfaceGeometry.GreatCircle(here, ref_target, ref_body.Radius):n0} m from the target";
            }
            logger.LogInfo($"[Landing] reentry: chutes {what} at T{now:F1}, {vessel.AltitudeFromSurface:n0} m above the ground ({vessel.AltitudeFromSeaLevel:n0} m above sea level), " +
                $"air speed {current_vessel.VesselVehicle.SurfaceSpeed:n1} m/s, Mach {vessel.MachNumber:n2}{position} | predicted from the {reentry_reference_name ?? "?"}: " +
                $"T{F(predictedUT, "F1")} ({F(now - predictedUT, "n1")} s late) at {F(predictedAltitude)} m | {string.Join("; ", states)}");
        }

        // ---- End ----

        void OnReentryFinish()
        {
            try
            {
                var vessel = current_vessel.VesselComponent;
                var cb = vessel.Orbit.referenceBody;
                double now = GeneralTools.Current_UT;
                Vector3d r = vessel.Orbit.GetRelativePositionAtUTZup(now);
                PrecisionContext.GameLatLon(cb, r, out double lat, out double lon);
                double d = HaversineDistanceMeters(lat, lon, precision_target_lat, precision_target_lon, cb.radius);
                precision_final_message = PrecisionPhases.LandedMessage(d);
                precision_distance_label = "Distance to Target";
                precision_distance_m = d;
                string compare = "no reference prediction";
                if (reentry_reference)
                {
                    Vector3d here = SurfaceGeometry.AtTime(r, ref_body.Omega, -(now - ref_ut));
                    double fromPredicted = SurfaceGeometry.GreatCircle(here, ref_stop.StopDirNow, ref_body.Radius);
                    double predictedMiss = SurfaceGeometry.GreatCircle(ref_stop.StopDirNow, ref_target, ref_body.Radius);
                    string split = "";
                    if (ref_stop.TrackDirNow.sqrMagnitude > 0)
                    {
                        SurfaceGeometry.Split(ref_stop.StopDirNow, here, ref_stop.TrackDirNow, ref_body.Radius, out double along, out double cross);
                        split = $" (touchdown {along:n0} m along the predicted track, {cross:n0} m across)";
                    }
                    compare = $"predicted from the {reentry_reference_name}: {predictedMiss:n0} m from the target{AlongCross(ref_stop, ref_target, ref_body.Radius)}, " +
                        $"touchdown T{ref_stop.StopUT:F1} ({now - ref_stop.StopUT:n1} s late); actual touchdown {fromPredicted:n0} m from the predicted one{split}";
                    Calibrate(here);
                }
                logger.LogInfo($"[Landing] reentry: landed {d:n1} m from the target (expected within about {Km(reentry_expected_m)}): at ({lat:0.00000}, {lon:0.00000}), " +
                    $"target ({precision_target_lat:0.00000}, {precision_target_lon:0.00000}) | {compare} | Δv used total {burn_dV.burned_dV:n1} m/s: " +
                    string.Join(", ", phase_log) + $" | drag scale {reentry_capsule.DragScale:0.00}");
            }
            catch (Exception e)
            {
                logger.LogWarning("[Landing] reentry: final distance not available: " + e);
            }
        }

        // The drag scale that would have put the reference prediction on the touchdown point.
        void Calibrate(Vector3d touchdownAtRef)
        {
            ReentryVehicle capsule = reentry_capsule;
            JettisonAwarePredictor p = ReentryPredictorFor(ref_body, jettisoned: true);
            var timer = Stopwatch.StartNew();
            DragCalibrationResult c = DragCalibration.Solve(s =>
            {
                ReentryVehicle scaled = capsule;
                scaled.DragScale = s;
                return p.Predict(ref_r, ref_v, ref_ut, null, scaled, out _);
            }, touchdownAtRef, ref_body.Radius);
            timer.Stop();
            double current = settings_atmo.reentry_drag_scale.V;
            if (c.Ok)
                suggested_drag_scale = c.Scale;
            logger.LogInfo($"[Landing] reentry: calibration from the {reentry_reference_name}: " + (c.Ok
                ? $"{DragCalibration.SuggestionMessage(c.Scale, current)}{(c.AtLimit ? " (at the limit of the range)" : "")}, residual {c.AlongM:n0} m along, " +
                  $"{c.CrossM:n0} m across (not a drag error), {c.Predictions} predictions, {timer.Elapsed.TotalMilliseconds:n0} ms - not applied"
                : $"no suggestion ({c.Reason}), {c.Predictions} predictions"));
        }

        // LandingUI's Apply button: writes the suggestion to the setting, never done on its own.
        internal void ApplySuggestedDragScale()
        {
            if (double.IsNaN(suggested_drag_scale))
                return;
            float before = settings_atmo.reentry_drag_scale.V;
            settings_atmo.reentry_drag_scale.V = (float)suggested_drag_scale;
            logger.LogInfo($"[Landing] reentry: drag scale applied by the player: {before:0.00} -> {settings_atmo.reentry_drag_scale.V:0.00}");
            suggested_drag_scale = double.NaN;
        }
    }
}

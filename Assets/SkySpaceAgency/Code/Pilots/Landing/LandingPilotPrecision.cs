using System;
using System.Collections.Generic;
using System.Diagnostics;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using K2D2.OrbitPlanning;
using KSP.Api;
using KSP.Sim;
using KSP.Sim.impl;
using KSP2FlightAssistant.MathLibrary;
using KTools;

namespace K2D2.Landing
{
    // The precision landing on an airless body (sub-project 3, part 2): the game side of the pure
    // planner in Precision/. Start: PrecisionStart.Plan from PrecisionContext, refused with the
    // reason when a plan or LandingFeasibility (all phases' Δv) refuses. Flight: OrbitPrep →
    // PlaneChange → Deorbit → MidCourse (PrecisionPhase, closed-loop TargetOrbitBurn, each burn
    // re-planned from the orbit really reached), then the usual warps, a retrograde brake at the
    // simulated throttle down to a stop at start_touchdown_altitude, terminal guidance
    // (TerminalGuidance) and the existing final descent below 50 m. Every line logged here starts
    // with "[Landing] precision:".
    public partial class LandingPilot
    {
        public PrecisionPhase precision_phase = new PrecisionPhase();

        // This run flies the precision sequence (decided by the start checks).
        internal bool precision_run = false;
        // Waiting in Mode.Calibrate for the rotation probe before planning.
        bool precision_calibrating = false;
        internal StartPlan precision_plan;
        int precision_lon_sign = 1;
        double precision_target_terrain = 0;
        double precision_target_lat, precision_target_lon;
        double earliest_deorbit_UT = double.NaN;
        double predicted_brake_start_UT = double.NaN;
        double phase_burned_start = 0;
        double brake_burned_start = 0;
        double terminal_burned_start = 0;
        readonly List<string> phase_log = new List<string>();

        // Readouts (LandingUI).
        internal string precision_summary = "";
        internal readonly List<string> precision_warnings = new List<string>();
        internal string precision_drop_message = null;
        internal double precision_distance_m = double.NaN;
        internal string precision_distance_label = "Predicted Distance";
        internal string precision_final_message = null;
        float next_precision_readout = 0;
        bool precision_brake_full_logged = false;
        // Brake throttle transitions (85 % <-> 100 %): only the first MaxLoggedThrottleSwitches are logged.
        const int MaxLoggedThrottleSwitches = 10;
        float brake_throttle_last = float.NaN;
        int brake_throttle_switches = 0;

        // Terminal guidance.
        bool guidance_enabled = false;
        bool guidance_started = false;
        bool guidance_final_logged = false;
        readonly DropDebounce drop_debounce = new DropDebounce();
        float next_guidance_log = 0;
        const double MaxFrameErrorDeg = 5;

        // Rotation probe (RotationProbe): one fixed inertial position, read twice.
        string probe_body = null;
        Vector3d probe_pos;
        double probe_ut, probe_lon;
        int probe_lon_sign = 1;
        internal string probe_failure = null;

        // A precision landing is flying (precision_run stays set after it, for the readouts).
        internal bool PrecisionActive => precision_run && isRunning;

        bool PrecisionPhaseMode(Mode m) => m == Mode.OrbitPrep || m == Mode.PlaneChange || m == Mode.Deorbit || m == Mode.MidCourse;

        double HandOverHeight => settings.start_touchdown_altitude.V;

        void ResetPrecision()
        {
            precision_run = false;
            precision_calibrating = false;
            precision_plan = default;
            earliest_deorbit_UT = double.NaN;
            predicted_brake_start_UT = double.NaN;
            phase_log.Clear();
            precision_summary = "";
            precision_warnings.Clear();
            precision_drop_message = null;
            precision_distance_m = double.NaN;
            precision_distance_label = "Predicted Distance";
            precision_final_message = null;
            precision_brake_full_logged = false;
            brake_throttle_last = float.NaN;
            brake_throttle_switches = 0;
            ClearRotationMeasurements();
            guidance_enabled = false;
            guidance_started = false;
            guidance_final_logged = false;
            drop_debounce.Reset();
        }

        void Warn(string text)
        {
            if (!precision_warnings.Contains(text))
                precision_warnings.Add(text);
        }

        static string F(double x, string format = "n0") => double.IsNaN(x) ? "?" : x.ToString(format);

        // ---- Start ----

        // Precision start checks, after the common ones. Sets last_error on a refusal.
        bool CheckCanStartPrecision()
        {
            ResetPrecision();
            if (!PrecisionContext.TryBuild(this, out PrecisionContext ctx, out string reason, log: true))
            {
                last_error = "Precision landing: " + reason;
                return false;
            }
            if (!ctx.RotationSignCalibrated)
            {
                // The surface speed could not decide (polar orbit, slow body): measure it.
                precision_run = true;
                precision_calibrating = true;
                if (probe_failure != null)
                    probe_body = null;   // a failed probe starts again
                probe_failure = null;
                logger.LogInfo("[Landing] precision: rotation sign not calibrated by the surface speed: measuring it " +
                    $"(rotation probe, ≥ {RotationProbe.MinSeconds:n0} s of game time)");
                return true;
            }
            return PlanPrecisionStart(ctx);
        }

        bool PlanPrecisionStart(PrecisionContext ctx)
        {
            var vessel = current_vessel.VesselComponent;
            var body = vessel.Orbit.referenceBody;
            var input = new StartInput
            {
                R = ctx.R,
                V = ctx.V,
                UT = ctx.UT,
                Body = ctx.Body,
                Vehicle = ctx.Vehicle,
                TargetDirNow = ctx.TargetDirNow,
                TouchDownSpeed = settings.touch_down_speed.V,
                MaxTiltDeg = brake.steering_max_angle.V,
            };
            var timer = Stopwatch.StartNew();
            StartPlan plan = PrecisionStart.Plan(input);
            timer.Stop();
            LogStartPlan(plan, ctx, timer.Elapsed.TotalMilliseconds);
            if (!plan.Ok)
            {
                precision_run = false;
                last_error = "Precision landing: " + plan.Reason;
                return false;
            }

            if (plan.Budget.OverBudget)
            {
                precision_run = false;
                last_error = plan.Budget.RefusalMessage;
                return false;
            }

            double surface_gravity = body.gravParameter / (body.radius * body.radius);
            var delta_v = vessel.VesselDeltaV;
            double dv_remaining = delta_v != null ? delta_v.TotalDeltaVActual : double.NaN;
            double accel = ctx.Vehicle.MassT > 0 ? ctx.Vehicle.ThrustKN / ctx.Vehicle.MassT : 0;
            var needed = new BrakeResult { Status = BrakeStatus.Ok, DeltaVNeeded = plan.TotalDeltaV };
            var check = LandingFeasibility.Check(accel, surface_gravity, needed, dv_remaining);
            logger.LogInfo($"[Landing] precision: start check: ok={check.Ok} accel {accel:n2} m/s², surface g {surface_gravity:n2}, " +
                $"Δv {F(dv_remaining)} m/s for {plan.TotalDeltaV:n0} m/s (all phases, x{LandingFeasibility.DeltaVMargin})" +
                (check.Ok ? "" : $" -> {check.Error}"));
            if (!check.Ok)
            {
                precision_run = false;
                last_error = check.Error + " (precision landing, all phases)";
                return false;
            }
            last_warning = check.Warning ?? "";

            precision_plan = plan;
            precision_lon_sign = ctx.Reference.LonSign;
            precision_target_terrain = ctx.TargetTerrainM;
            precision_target_lat = settings.target_latitude.V;
            precision_target_lon = settings.target_longitude.V;
            precision_summary = plan.Budget.Summary +
                $" | predicted miss {F(plan.Deorbit.AlongMissM)} m along, {F(plan.Deorbit.CrossMissM)} m across";
            if (ctx.TargetSlopeDeg > PrecisionContext.MaxSlopeDeg)
                Warn(PrecisionPhases.SlopeMessage(ctx.TargetSlopeDeg));
            if (plan.Deorbit.Stop.BelowHandOver)
                Warn(PrecisionPhases.BelowHandOverMessage);
            precision_run = true;
            precision_calibrating = false;
            return true;
        }

        void LogStartPlan(StartPlan plan, PrecisionContext ctx, double ms)
        {
            double now = ctx.UT;
            double R = ctx.Body.Radius;
            PlaneChangePlan pc = plan.PlaneChange;
            logger.LogInfo($"[Landing] precision: start plan {(plan.Ok ? "ok" : "refused: " + plan.Reason)} ({ms:n0} ms, {plan.PredictCalls} predictions) | " +
                $"preparation {(plan.NeedsPreparation ? $"at the periapsis +{plan.PreparationUT - now:n0} s, Δv {plan.PreparationDeltaV:n1} m/s" : "not needed")} | " +
                $"plane change {(pc.Needed ? $"node +{pc.BurnUT - now:n0} s (start +{pc.BurnStartUT - now:n0} s), Δv {pc.DeltaV:n1} m/s, inc {pc.Target.InclinationRad * 180 / Math.PI:n2}°, {pc.Iterations} iterations" : "not needed")}" +
                (pc.Ok || pc.Reason == null ? "" : $" ({pc.Reason})"));
            if (plan.Deorbit.Ok || plan.Deorbit.Reason != null)
                logger.LogInfo($"[Landing] precision: start plan deorbit: {DeorbitText(plan.Deorbit, now)} | baseline (no target): {DeorbitText(plan.Baseline, now)}");
            OrbitClearance c = plan.Clearance;
            logger.LogInfo($"[Landing] precision: start plan clearance: required {c.RequiredM / 1000:n1} km, " +
                (c.TerrainRead
                    ? $"terrain up to {c.HighestTerrainM / 1000:n2} km over {c.Samples} samples of the ground track (+{PrecisionStart.TerrainMargin:n0} m margin, floor {PrecisionStart.MinClearance / 1000:n0} km)"
                    : $"terrain not read ({c.Note}): floor {PrecisionStart.MinClearance / 1000:n0} km alone"));
            if (plan.Ok)
                logger.LogInfo($"[Landing] precision: start plan budget: {plan.Budget.Summary} | reserves: correction {plan.CorrectionReserve:n1}, " +
                    $"terminal {plan.TerminalReserve:n1} + lateral {plan.LateralReserve:n1} (tilt {brake.steering_max_angle.V:n0}°) from {HandOverHeight:n0} m, " +
                    $"baseline final descent {plan.BaselineTerminalReserve:n1} m/s | total {plan.TotalDeltaV:n0} m/s | within limit {plan.Budget.WithinLimit}" +
                    (plan.Budget.Reason != null ? $" ({plan.Budget.Reason})" : "") + $" | ground radius {R:n0} m");
        }

        static string DeorbitText(DeorbitPlan d, double now) => !d.Ok && !(d.BurnUT > 0)
            ? $"refused ({d.Reason}), {d.PredictCalls} predictions"
            : $"{(d.Ok ? "ok" : "refused (" + d.Reason + ")")} burn +{d.BurnUT - now:n1} s, Δv {d.DeltaV.magnitude:n2} m/s, " +
            $"brake start +{d.Stop.BrakeStartUT - now:n0} s, brake Δv {d.Stop.BrakeDeltaV:n0} m/s, stop {d.Stop.StopAltitude:n0} m up" +
            (d.Stop.BelowHandOver ? " (below the hand-over)" : "") +
            $", predicted miss along {F(d.AlongMissM, "n1")} m, cross {F(d.CrossMissM, "n1")} m, {d.PredictCalls} predictions";

        // ---- Rotation probe ----

        // Measures the rotation sign when the surface-speed calibration has not, for the current
        // body (RotationProbe). Cheap: two game lat/lon reads at least 2 s of game time apart.
        void UpdateSignProbe()
        {
            var vessel = current_vessel?.VesselComponent;
            if (vessel?.Orbit == null)
                return;
            var body = vessel.Orbit.referenceBody;
            if (body == null || rotation_sign_body == body.Name)
                return;
            if (!precision_calibrating && !(settings.precision_landing.V && !LandingProfile.IsAtmospheric)
                && !(parachute_mode && settings_atmo.precision_landing.V))
                return;
            double now = GeneralTools.Current_UT;
            try
            {
                if (probe_body != body.Name)
                {
                    Vector3d r = vessel.Orbit.GetRelativePositionAtUTZup(now);
                    if (Math.Sqrt(r.x * r.x + r.y * r.y) < 1e-3 * r.magnitude)
                        return;   // over a pole: try again later
                    PrecisionContext.GameLatLon(body, r, out _, out double lon);
                    double c = Math.Cos(0.01), s = Math.Sin(0.01);
                    PrecisionContext.GameLatLon(body, new Vector3d(c * r.x - s * r.y, s * r.x + c * r.y, r.z), out _, out double lonRot);
                    probe_lon_sign = PrecisionContext.Wrap180(lonRot - lon) >= 0 ? 1 : -1;
                    probe_body = body.Name;
                    probe_pos = r;
                    probe_ut = now;
                    probe_lon = lon;
                    probe_failure = null;
                    return;
                }
                if (probe_failure != null || now - probe_ut < RotationProbe.MinSeconds)
                    return;
                PrecisionContext.GameLatLon(body, probe_pos, out _, out double lon2);
                double omega = BodyRotation.AngularSpeed(body.rotationPeriod);
                int sign = RotationProbe.Decide(probe_lon, lon2, now - probe_ut, probe_lon_sign, omega, body.radius, out string reason);
                logger.LogInfo($"[Landing] precision: rotation probe on {body.Name}: lon {probe_lon:0.000000} -> {lon2:0.000000} in {now - probe_ut:n1} s, " +
                    $"expected |Δ| {omega * (now - probe_ut) * 180 / Math.PI:0.000000}°, longitude sign {probe_lon_sign} -> " +
                    (sign != 0 ? $"rotation sign {sign}" : "not measurable") + (reason != null ? $" ({reason})" : ""));
                if (sign == 0)
                {
                    probe_failure = reason;
                    return;
                }
                rotation_sign = sign;
                rotation_sign_body = body.Name;
                // A real measurement only (not "negligible: +1 kept"): cross-checked against the
                // surface-speed calibration by PrecisionContext.
                if (reason == null)
                {
                    probe_sign = sign;
                    probe_sign_body = body.Name;
                }
                int speed = SpeedSignFor(body.Name);
                logger.LogInfo($"[Landing] rotation sign for {body.Name}: {rotation_sign} (rotation probe, ω {omega:e3} rad/s)" +
                    (speed != 0 ? $" | surface-speed calibration: {speed}{(RotationProbe.Disagree(speed, ProbeSignFor(body.Name)) ? " - DISAGREE" : "")}" : ""));
            }
            catch (Exception e)
            {
                probe_failure = $"The body's rotation could not be measured ({e.GetType().Name}: {e.Message}).";
                logger.LogWarning("[Landing] precision: rotation probe failed: " + e);
            }
        }

        // Mode.Calibrate: waits for the probe, then plans as a normal start would.
        void UpdateCalibrate()
        {
            var body = current_vessel.VesselComponent.Orbit.referenceBody;
            if (rotation_sign_body == body.Name && reentry_run)
                UpdateCalibrateReentry();
            else if (rotation_sign_body == body.Name)
            {
                precision_calibrating = false;
                if (!PrecisionContext.TryBuild(this, out PrecisionContext ctx, out string reason, log: true))
                {
                    StopWithError("Precision landing: " + reason);
                    return;
                }
                if (!PlanPrecisionStart(ctx))
                {
                    StopWithError(last_error);
                    return;
                }
                setMode(Mode.OrbitPrep);
            }
            else if (probe_failure != null)
                StopWithError("Precision landing: " + probe_failure);
        }

        // ---- Phases ----

        void StartPrecisionPhase(Mode m)
        {
            double now = GeneralTools.Current_UT;
            current_executor.setController(precision_phase);
            precision_phase.allow_warp = settings.auto_warp.V;
            phase_burned_start = burn_dV.burned_dV;
            switch (m)
            {
                case Mode.OrbitPrep:
                    precision_phase.Start("orbit preparation", now, PlanOrbitPrep);
                    break;
                case Mode.PlaneChange:
                    precision_phase.Start("plane change", now, reentry_run ? PlanReentryPlaneChange : (Func<PrecisionPhase.Plan>)PlanPlaneChange);
                    break;
                case Mode.Deorbit:
                    precision_phase.Start("deorbit", now, reentry_run ? PlanReentryDeorbit : (Func<PrecisionPhase.Plan>)PlanDeorbit);
                    break;
                case Mode.MidCourse:
                    precision_phase.Start("mid-course correction", PrecisionPhases.CorrectionPlanUT(now, predicted_brake_start_UT),
                        reentry_run ? PlanReentryCorrection : (Func<PrecisionPhase.Plan>)PlanCorrection);
                    break;
            }
            // A parachute precision return: the brake start is the atmosphere interface.
            logger.LogInfo($"[Landing] {(reentry_run ? "reentry" : "precision")}: phase {precision_phase.Name} start" + (m == Mode.MidCourse
                ? $", planned in {PrecisionPhases.CorrectionPlanUT(now, predicted_brake_start_UT) - now:n0} s " +
                  $"({(reentry_run ? "interface" : "brake start")} +{F(predicted_brake_start_UT - now)} s)"
                : ""));
        }

        bool BuildContext(out PrecisionContext ctx, out string error)
        {
            error = null;
            if (!PrecisionContext.TryBuild(this, out ctx, out string reason, log: false))
            {
                error = "Precision landing: " + reason;
                return false;
            }
            if (!ctx.RotationSignCalibrated)
            {
                error = "Precision landing: the body's rotation sign is not calibrated any more.";
                return false;
            }
            return true;
        }

        static PrecisionPhase.Plan PhaseError(string e) => new PrecisionPhase.Plan { Error = e };
        static PrecisionPhase.Plan PhaseSkip => new PrecisionPhase.Plan { Skip = true };

        PrecisionPhase.Plan PlanOrbitPrep()
        {
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            var body = orbit.referenceBody;
            double now = GeneralTools.Current_UT;
            OrbitElements el = OrbitMath.Elements(orbit.GetRelativePositionAtUTZup(now), orbit.GetOrbitalVelocityAtUTZup(now), body.gravParameter);
            bool circular = el.ApoapsisRadius - el.PeriapsisRadius <= PrecisionStart.CircularTolerance;
            logger.LogInfo($"[Landing] precision: plan orbit preparation: ap {(el.ApoapsisRadius - body.radius) / 1000:n1} km " +
                $"pe {(el.PeriapsisRadius - body.radius) / 1000:n1} km -> " +
                (circular ? "circular enough, not needed" : $"circularize at the periapsis (closed loop), Δv ~{precision_plan.PreparationDeltaV:n1} m/s"));
            if (circular)
                return PhaseSkip;
            return new PrecisionPhase.Plan { CircularizeAtPeriapsis = true, PlannedDeltaV = precision_plan.PreparationDeltaV };
        }

        PrecisionPhase.Plan PlanPlaneChange()
        {
            if (!BuildContext(out PrecisionContext ctx, out string error))
                return PhaseError(error);
            var timer = Stopwatch.StartNew();
            PlaneChangePlan pc = PlaneChange.Plan(ctx.R, ctx.V, ctx.UT, ctx.Body, ctx.Vehicle, ctx.TargetDirNow);
            timer.Stop();
            double now = ctx.UT;
            logger.LogInfo($"[Landing] precision: plan plane change: needed {pc.Needed}, ok {pc.Ok}" +
                (pc.Needed ? $", node +{pc.BurnUT - now:n1} s (start +{pc.BurnStartUT - now:n1} s), Δv {pc.DeltaV:n2} m/s, " +
                    $"target inc {pc.Target.InclinationRad * 180 / Math.PI:n3}°, {pc.Iterations} iterations" : "") +
                $", {pc.PredictCalls} predictions, {timer.Elapsed.TotalMilliseconds:n0} ms | deorbit after it: {DeorbitText(pc.Deorbit, now)}" +
                (pc.Reason != null ? $" | {pc.Reason}" : ""));
            if (!pc.Ok)
                return PhaseError((pc.Needed ? "Plane change: " : "Deorbit: ") + pc.Reason);
            if (!pc.Needed)
            {
                earliest_deorbit_UT = double.NaN;
                return PhaseSkip;
            }
            earliest_deorbit_UT = PlaneChange.EarliestDeorbitUT(pc.BurnUT, pc.DeltaV, ctx.Vehicle);
            return new PrecisionPhase.Plan { Target = pc.Target, StartUT = pc.BurnStartUT, PlannedDeltaV = pc.DeltaV };
        }

        PrecisionPhase.Plan PlanDeorbit()
        {
            if (!BuildContext(out PrecisionContext ctx, out string error))
                return PhaseError(error);
            double now = ctx.UT;
            double mu = ctx.Body.Mu;
            // Duration guess: the burn that lowers the periapsis from an apoapsis here.
            double r = ctx.R.magnitude;
            double rpTarget = ctx.Body.Radius - DeorbitSearch.DescentPeriapsisMargin;
            double guess = Math.Max(0, ctx.V.magnitude - Math.Sqrt(mu * 2 * rpTarget / (r * (r + rpTarget))));
            Precision.Vehicle vehicle = ctx.Vehicle;
            double duration_guess = BurnTiming.Duration(guess, vehicle.ThrustKN, vehicle.MassT, vehicle.Isp);
            double earliest = PrecisionPhases.EarliestDeorbitUT(now, earliest_deorbit_UT, duration_guess);

            var timer = Stopwatch.StartNew();
            DeorbitPlan plan = DeorbitSearch.Find(ctx.R, ctx.V, now, ctx.Body, vehicle, ctx.TargetDirNow, earliest);
            timer.Stop();
            logger.LogInfo($"[Landing] precision: plan deorbit (earliest +{earliest - now:n0} s, {timer.Elapsed.TotalMilliseconds:n0} ms): {DeorbitText(plan, now)}");
            if (!plan.Ok)
                return PhaseError("Deorbit: " + plan.Reason);
            if (plan.Stop.BelowHandOver)
                Warn(PrecisionPhases.BelowHandOverMessage);

            KeplerPropagator.Propagate(ctx.R, ctx.V, mu, plan.BurnUT - now, out Vector3d rb, out Vector3d vb);
            double dv = plan.DeltaV.magnitude;
            double start = BurnTiming.CenteredStart(plan.BurnUT,
                BurnTiming.Duration(dv, vehicle.ThrustKN, vehicle.MassT, vehicle.Isp), now);
            predicted_brake_start_UT = plan.Stop.BrakeStartUT;
            return new PrecisionPhase.Plan
            {
                Target = TargetOrbit.FromState(rb, vb + plan.DeltaV, mu),
                StartUT = start,
                PlannedDeltaV = dv,
            };
        }

        PrecisionPhase.Plan PlanCorrection()
        {
            if (!BuildContext(out PrecisionContext ctx, out string error))
                return PhaseError(error);
            double lead = PrecisionPhases.PlanLead;
            double ut_c = ctx.UT + lead;
            double mu = ctx.Body.Mu;
            KeplerPropagator.Propagate(ctx.R, ctx.V, mu, lead, out Vector3d rc, out Vector3d vc);
            PrecisionStart.AtLaterTime(ctx.Body, ctx.TargetDirNow, lead, out Precision.Body bodyC, out Vector3d targetC);

            var timer = Stopwatch.StartNew();
            Correction c = CourseCorrection.Solve(rc, vc, ut_c, bodyC, ctx.Vehicle, targetC, PrecisionStart.CorrectionReserve);
            timer.Stop();
            bool fly = PrecisionPhases.ShouldFly(c);
            logger.LogInfo($"[Landing] precision: plan mid-course correction at +{lead:n0} s: ok {c.Ok}, Δv {c.DeltaV.magnitude:n3} m/s, " +
                $"predicted miss {c.MissBeforeM:n0} -> {c.MissAfterM:n0} m, clipped {c.Clipped}, {c.PredictCalls} predictions, " +
                $"{timer.Elapsed.TotalMilliseconds:n0} ms -> {(fly ? "flown" : "not flown")}" + (c.Reason != null ? $" ({c.Reason})" : ""));
            if (!c.Ok)
                Warn("Mid-course correction refused: the terminal guidance corrects the rest.");
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

        static PrecisionStage StageOf(Mode m)
        {
            switch (m)
            {
                case Mode.OrbitPrep: return PrecisionStage.OrbitPrep;
                case Mode.PlaneChange: return PrecisionStage.PlaneChange;
                case Mode.Deorbit: return PrecisionStage.Deorbit;
                default: return PrecisionStage.MidCourse;
            }
        }

        // A precision phase failed (refused plan, refused or aborted burn). PrecisionFailurePolicy:
        // stop only while the orbit still clears the ground; a failed mid-course correction is
        // skipped; a failure that leaves the vessel on an impact course lands without target.
        void OnPrecisionPhaseFailed(string reason)
        {
            if (reentry_run)
            {
                OnReentryPhaseFailed(reason);
                return;
            }
            bool hits;
            string orbit_text;
            try
            {
                IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
                var body = orbit.referenceBody;
                double now = GeneralTools.Current_UT;
                OrbitElements el = OrbitMath.Elements(orbit.GetRelativePositionAtUTZup(now), orbit.GetOrbitalVelocityAtUTZup(now), body.gravParameter);
                hits = PrecisionFailurePolicy.OrbitHitsGround(el.PeriapsisRadius, body.radius, collision_detected);
                orbit_text = $"pe {(el.PeriapsisRadius - body.radius) / 1000:n2} km";
            }
            catch (Exception e)
            {
                // Unknown orbit: assume the worst, keep flying.
                hits = true;
                orbit_text = $"orbit not read ({e.GetType().Name}: {e.Message})";
            }
            FailureAction action = PrecisionFailurePolicy.Decide(StageOf(mode), hits);
            logger.LogWarning($"[Landing] precision: phase {precision_phase.Name} failed: {reason} | {orbit_text}, " +
                $"collision predicted {collision_detected}, hits the ground {hits} -> {action}");
            switch (action)
            {
                case FailureAction.Stop:
                    StopWithError(reason);
                    break;
                case FailureAction.Skip:
                    current_vessel.SetThrottle(0);
                    Warn(PrecisionFailurePolicy.SkipMessage(reason));
                    OnPrecisionPhaseEnd();
                    current_executor.setController(null);
                    nextMode();
                    break;
                default:
                    LandWithoutTarget(PrecisionFailurePolicy.FallBackMessage(reason));
                    break;
            }
        }

        // Drops the precision run and flies the landing without target from QuickWarp (brake to
        // 50 m, the usual Touch Down), as a non-precision start would.
        void LandWithoutTarget(string message)
        {
            logger.LogWarning($"[Landing] precision: {message} | Δv used {burn_dV.burned_dV:n1} m/s ({string.Join(", ", phase_log)})");
            precision_run = false;
            precision_calibrating = false;
            guidance_enabled = false;
            last_warning = message;
            precision_final_message = message;
            current_vessel.SetThrottle(0);
            current_executor.setController(null);
            setMode(Mode.QuickWarp);
        }

        // After a precision phase: the Δv really used, and the prediction from the orbit reached.
        void OnPrecisionPhaseEnd()
        {
            if (reentry_run)
            {
                OnReentryPhaseEnd();
                return;
            }
            double used = burn_dV.burned_dV - phase_burned_start;
            string name = precision_phase.Name;
            string after;
            if (mode == Mode.Deorbit || mode == Mode.MidCourse)
            {
                if (BuildContext(out PrecisionContext ctx, out string error))
                {
                    Precision.Miss m = PrecisionPhases.Predict(ctx.R, ctx.V, ctx.UT, ctx.Body, ctx.Vehicle, ctx.TargetDirNow);
                    if (m.Ok)
                    {
                        predicted_brake_start_UT = m.Stop.BrakeStartUT;
                        precision_distance_m = m.DistanceM;
                        after = $"predicted miss {m.DistanceM:n0} m (along {F(m.AlongM)} m, cross {F(m.CrossM)} m), " +
                            $"brake start +{m.Stop.BrakeStartUT - ctx.UT:n0} s, brake Δv {m.Stop.BrakeDeltaV:n0} m/s, stop {m.Stop.StopAltitude:n0} m up" +
                            (m.Stop.BelowHandOver ? " (below the hand-over)" : "");
                    }
                    else
                        after = "no prediction: " + m.Reason;
                }
                else
                    after = error;
            }
            else
            {
                IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
                var body = orbit.referenceBody;
                double now = GeneralTools.Current_UT;
                OrbitElements el = OrbitMath.Elements(orbit.GetRelativePositionAtUTZup(now), orbit.GetOrbitalVelocityAtUTZup(now), body.gravParameter);
                after = $"orbit ap {(el.ApoapsisRadius - body.radius) / 1000:n2} km pe {(el.PeriapsisRadius - body.radius) / 1000:n2} km " +
                    $"inc {el.InclinationRad * 180 / Math.PI:n3}°";
            }
            logger.LogInfo($"[Landing] precision: phase {name} end{(precision_phase.Skipped ? " (not needed)" : "")}: " +
                $"Δv used {used:n2} m/s (planned {precision_phase.PlannedDeltaV:n2}) | {after}");
            phase_log.Add($"{name} {used:n1}/{precision_phase.PlannedDeltaV:n1}");
        }

        // Predicted distance to the target, every ReadoutInterval s outside the burns, before the brake.
        void UpdatePrecisionReadout()
        {
            bool coasting = mode == Mode.Pause || mode == Mode.QuickWarp || mode == Mode.RotationWarp || mode == Mode.Waiting
                || (PrecisionPhaseMode(mode) && !precision_phase.Burning);
            if (!coasting)
                return;
            float now_real = UnityEngine.Time.realtimeSinceStartup;
            if (now_real < next_precision_readout)
                return;
            next_precision_readout = now_real + (float)PrecisionPhases.ReadoutInterval;
            precision_distance_label = "Predicted Distance";
            if (!BuildContext(out PrecisionContext ctx, out _))
                return;
            Precision.Miss m = PrecisionPhases.Predict(ctx.R, ctx.V, ctx.UT, ctx.Body, ctx.Vehicle, ctx.TargetDirNow);
            precision_distance_m = m.Ok ? m.DistanceM : double.NaN;
        }

        // ---- Brake and terminal guidance ----

        void OnPrecisionBrakeStart()
        {
            brake_burned_start = burn_dV.burned_dV;
            precision_brake_full_logged = false;
            brake_throttle_last = float.NaN;
            brake_throttle_switches = 0;
            double now = GeneralTools.Current_UT;
            logger.LogInfo($"[Landing] precision: phase brake start: altitude {altitude:n0} m, speed {current_vessel.VesselVehicle.SurfaceSpeed:n1} m/s, " +
                $"planned start +{startBurn_UT - now:n1} s, last predicted distance {F(precision_distance_m)} m, " +
                (brake_result_valid ? $"brake sim {brake_result.Status} stop {brake_result.StopAltitude:n0} m up, Δv {brake_result.DeltaVNeeded:n0} m/s" : "no brake sim"));
        }

        void UpdatePrecisionBrake()
        {
            brake.max_speed = 0;
            float throttle = PrecisionPhases.BrakeThrottle(brake_result_valid ? brake_result : (BrakeResult?)null);
            if (throttle >= 1 && !precision_brake_full_logged)
            {
                precision_brake_full_logged = true;
                logger.LogWarning($"[Landing] precision: brake at full throttle: the brake simulation stops below the hand-over height " +
                    $"({brake_result.Status}, stop {brake_result.StopAltitude:n0} m up) at altitude {altitude:n0} m");
            }
            if (!float.IsNaN(brake_throttle_last) && throttle != brake_throttle_last)
            {
                brake_throttle_switches++;
                if (brake_throttle_switches <= MaxLoggedThrottleSwitches)
                    logger.LogInfo($"[Landing] precision: brake throttle -> {throttle * 100:n0} % at UT {GeneralTools.Current_UT:n1}, " +
                        $"altitude {altitude:n0} m (switch {brake_throttle_switches})");
            }
            brake_throttle_last = throttle;
            brake.precision_brake_throttle = throttle;
        }

        void OnPrecisionHandOver()
        {
            terminal_burned_start = burn_dV.burned_dV;
            logger.LogInfo($"[Landing] precision: brake throttle switches: {brake_throttle_switches}" +
                (brake_throttle_switches > MaxLoggedThrottleSwitches ? $" (only the first {MaxLoggedThrottleSwitches} logged)" : ""));
            guidance_started = false;
            guidance_final_logged = false;
            drop_debounce.Reset();
            precision_drop_message = null;
            // A Touch Down pressed far above the hand-over height lands without target.
            guidance_enabled = altitude < 2 * HandOverHeight;
            logger.LogInfo($"[Landing] precision: phase brake end: Δv used {burn_dV.burned_dV - brake_burned_start:n1} m/s, " +
                $"altitude {altitude:n0} m, speed {current_vessel.VesselVehicle.SurfaceSpeed:n2} m/s, fall {current_falling_speed:n2} m/s" +
                (guidance_enabled ? "" : " | far above the hand-over height: landing without target"));
            if (!guidance_enabled)
                Warn("Touch Down started far above the hand-over height: landing without target.");
            else if (PrecisionPhases.IsLateHandOver(altitude, HandOverHeight))
                Warn($"Guidance started late ({altitude:n0} m): possible drift at touchdown.");
        }

        static Vector3d Swap(Vector3d a) => new Vector3d(a.x, a.z, a.y);

        // Every frame in Touch Down: TerminalGuidance toward the target, handed to TouchDown as an
        // aim and a throttle; the existing final descent below FinalDescent.FinalHeight.
        void UpdatePrecisionGuidance()
        {
            brake.guidance_active = false;
            if (!guidance_enabled)
                return;
            try
            {
                var vessel = current_vessel.VesselComponent;
                IKeplerPatch orbit = vessel.Orbit;
                var body = orbit.referenceBody;
                double now = GeneralTools.Current_UT;
                Vector3d r = orbit.GetRelativePositionAtUTZup(now);
                PrecisionContext.GameLatLon(body, r, out double lat, out double lon);
                var reference = new SurfaceGeometry.Reference { Position = r, Latitude = lat, Longitude = lon, LonSign = precision_lon_sign };
                Vector3d dir = SurfaceGeometry.Direction(reference, precision_target_lat, precision_target_lon);
                TargetFrame frame = TargetFrame.At(dir, body.radius + precision_target_terrain, precision_lon_sign);
                Vector3d offset = frame.Offset(r);
                // Never higher than the radar altitude: terrain under the vessel may stand above the target.
                offset.z = Math.Min(offset.z, altitude);

                ICoordinateSystem celestial = body.SimulationObject.transform.celestialFrame;
                Vector sv = current_vessel.VesselVehicle.SurfaceVelocity;
                sv.Reframe(celestial);
                Vector3d velocity = frame.ToLocal(Swap(sv.vector));
                double horizontal = Math.Sqrt(offset.x * offset.x + offset.y * offset.y);
                precision_distance_label = "Distance to Target";
                precision_distance_m = horizontal;

                if (FinalDescent.IsFinal(altitude))
                {
                    if (!guidance_final_logged)
                    {
                        guidance_final_logged = true;
                        logger.LogInfo($"[Landing] precision: final descent below {FinalDescent.FinalHeight:n0} m: offset east {offset.x:n1} m " +
                            $"north {offset.y:n1} m, velocity ({velocity.x:n2}, {velocity.y:n2}, {velocity.z:n2}) m/s, " +
                            $"Δv used in guidance {burn_dV.burned_dV - terminal_burned_start:n1} m/s");
                    }
                    return;
                }

                if (!guidance_started)
                {
                    guidance_started = true;
                    if (!FrameCheck(celestial, r, body, velocity, offset))
                    {
                        guidance_enabled = false;
                        return;
                    }
                }

                double g = vessel.graviticAcceleration.magnitude;
                double max_accel = burn_dV.active_dv > 0 ? burn_dV.active_dv : burn_dV.full_dv;
                double tds = settings.touch_down_speed.V;
                double tilt = brake.steering_max_angle.V;
                Precision.Command cmd = TerminalGuidance.Compute(offset, velocity, g, max_accel, tds, tilt);
                bool dropped = cmd.TargetDropped;
                double miss = cmd.PredictedMissM;

                // Not enough Δv left for the descent plus the lateral move: give the target up.
                var delta_v = vessel.VesselDeltaV;
                double dv_left = delta_v != null ? delta_v.TotalDeltaVActual : double.NaN;
                double need = PrecisionBudget.TerminalReserve(offset.z, g, tds, max_accel)
                    + PrecisionStart.LateralReserve(horizontal, g, tilt, PrecisionStart.DescentTime(offset.z, g, tds, max_accel));
                bool short_dv = PrecisionPhases.DeltaVShort(dv_left, need);
                if (short_dv)
                {
                    cmd = TerminalGuidance.Compute(PrecisionPhases.GiveUpOffset(offset, velocity), velocity, g, max_accel, tds, tilt);
                    dropped = true;
                    miss = PrecisionPhases.DriftStopDistance(offset, velocity);
                }

                bool show = drop_debounce.Update(dropped, now);
                precision_drop_message = show ? PrecisionPhases.OutOfReachMessage(miss) : null;

                Vector3d accel_zup = frame.FromLocal(cmd.Accel);
                double a = accel_zup.magnitude;
                Vector3d aim = a > 1e-9 ? accel_zup / a : frame.Up;
                brake.guidance_aim = new Vector(celestial, Swap(aim));
                brake.guidance_throttle = max_accel > 0 ? (float)Math.Min(1, cmd.Accel.magnitude / max_accel) : 0;
                brake.guidance_active = true;

                float now_real = UnityEngine.Time.realtimeSinceStartup;
                if (now_real >= next_guidance_log)
                {
                    next_guidance_log = now_real + 5;
                    logger.LogInfo($"[Landing] precision: guidance: altitude {altitude:n0} m, offset ({offset.x:n1}, {offset.y:n1}, {offset.z:n0}) m, " +
                        $"velocity ({velocity.x:n2}, {velocity.y:n2}, {velocity.z:n2}) m/s, accel ({cmd.Accel.x:n2}, {cmd.Accel.y:n2}, {cmd.Accel.z:n2}) " +
                        $"of {max_accel:n2} m/s², dropped {dropped}{(short_dv ? " (Δv short)" : "")}" + (dropped ? $" miss {miss:n0} m" : "") +
                        $", Δv left {F(dv_left)} for {need:n0} m/s" + (cmd.Reason != null ? $" | {cmd.Reason}" : ""));
                }
            }
            catch (Exception e)
            {
                guidance_enabled = false;
                brake.guidance_active = false;
                logger.LogWarning("[Landing] precision: guidance failed, landing without target: " + e);
                Warn("Terminal guidance failed: landing without target.");
            }
        }

        // Once, at the hand-over: the Zup → game mapping (up, north, surface velocity) against the
        // game's own vectors. False (landing without target) when up or north is off by more than
        // MaxFrameErrorDeg.
        bool FrameCheck(ICoordinateSystem celestial, Vector3d r, CelestialBodyComponent body, Vector3d velocity, Vector3d offset)
        {
            var vehicle = current_vessel.VesselVehicle;
            Vector game_up = vehicle.Up;
            Vector game_north = vehicle.North;
            ICoordinateSystem local = game_up.coordinateSystem;
            game_north.Reframe(local);

            Vector3d n = r.normalized;
            Vector3d north = new Vector3d(0, 0, 1) - n.z * n;
            north = north.normalized;
            Vector up_mapped = new Vector(celestial, Swap(n));
            up_mapped.Reframe(local);
            Vector north_mapped = new Vector(celestial, Swap(north));
            north_mapped.Reframe(local);
            double up_err = Vector3d.Angle(up_mapped.vector, game_up.vector);
            Vector3d gn = game_north.vector - Vector3d.Dot(game_north.vector, game_up.vector.normalized) * game_up.vector.normalized;
            double north_err = Vector3d.Angle(north_mapped.vector, gn);

            // Surface velocity from the orbit and the calibrated rotation, against the game's.
            double now = GeneralTools.Current_UT;
            Vector3d v_orbit = current_vessel.VesselComponent.Orbit.GetOrbitalVelocityAtUTZup(now);
            Vector3d omega = BodyRotation.AngularVelocity(BodyRotation.AngularSpeed(body.rotationPeriod), rotation_sign);
            Vector3d surface = BodyRotation.SurfaceVelocity(r, v_orbit, omega);
            TargetFrame here = TargetFrame.At(n, r.magnitude, precision_lon_sign);
            Vector3d mine = here.ToLocal(surface);
            double v_err = (mine - velocity).magnitude;

            bool ok = up_err <= MaxFrameErrorDeg && north_err <= MaxFrameErrorDeg;
            logger.LogInfo($"[Landing] precision: phase terminal guidance start: altitude {altitude:n0} m, offset east {offset.x:n1} m north {offset.y:n1} m " +
                $"up {offset.z:n0} m, velocity ({velocity.x:n2}, {velocity.y:n2}, {velocity.z:n2}) m/s, mass {current_vessel.VesselComponent.totalMass:n3} t | " +
                $"frame check: up {up_err:n2}°, north {north_err:n2}° (max {MaxFrameErrorDeg:n0}°), surface velocity from the orbit " +
                $"({mine.x:n2}, {mine.y:n2}, {mine.z:n2}) vs game, gap {v_err:n2} m/s (rotation sign {rotation_sign}) -> {(ok ? "ok" : "FAILED, landing without target")}");
            if (!ok)
                Warn("Frame check failed: landing without target (see the log).");
            return ok;
        }

        // ---- End ----

        void OnPrecisionFinish()
        {
            try
            {
                var vessel = current_vessel.VesselComponent;
                var body = vessel.Orbit.referenceBody;
                double now = GeneralTools.Current_UT;
                Vector3d r = vessel.Orbit.GetRelativePositionAtUTZup(now);
                PrecisionContext.GameLatLon(body, r, out double lat, out double lon);
                double d = HaversineDistanceMeters(lat, lon, precision_target_lat, precision_target_lon, body.radius);
                precision_final_message = PrecisionPhases.LandedMessage(d);
                logger.LogInfo($"[Landing] precision: landed {d:n1} m from the target: at ({lat:0.00000}, {lon:0.00000}), target " +
                    $"({precision_target_lat:0.00000}, {precision_target_lon:0.00000}) | Δv used total {burn_dV.burned_dV:n1} m/s: " +
                    string.Join(", ", phase_log) + $", brake {terminal_burned_start - brake_burned_start:n1}, " +
                    $"terminal {burn_dV.burned_dV - terminal_burned_start:n1} m/s | planned total {precision_plan.TotalDeltaV:n0} m/s");
            }
            catch (Exception e)
            {
                logger.LogWarning("[Landing] precision: final distance not available: " + e);
            }
        }
    }
}

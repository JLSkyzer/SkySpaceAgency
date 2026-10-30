using System;
using System.Diagnostics;
using K2D2.Landing.Braking;
using K2D2.Landing.Heating;
using K2D2.Landing.Precision;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP.Sim.impl;
using KSP2FlightAssistant.MathLibrary;
using KTools;
using UnityEngine;

namespace K2D2.Landing
{
    // The normal (non-precision) landing started with no collision course, or above the atmosphere
    // (spec docs/superpowers/specs/2026-10-30-deorbit-and-reentry-heating-design.md): the start plan
    // and Δv refusal (Heating/DescentPlanner, DescentContext), the deorbit and pre-brake burns
    // (PrecisionPhase, closed-loop TargetOrbitBurn, re-planned from the orbit reached), the
    // atmospheric coast (Mode.AeroCoast: surface retrograde, brake start predicted with drag) and the
    // live heat guard (Heating/HeatGuard) in every coasting mode of an atmospheric descent. Every
    // line logged here starts with "[Landing] deorbit:" or "[Landing] heat:".
    public partial class LandingPilot
    {
        // This run flies a planned descent (decided by the start checks).
        internal bool deorbit_run = false;
        internal DescentPlan descent_plan;
        // The entry periapsis radius chosen at the start (atmospheric), kept by the re-plans; NaN airless.
        double descent_rp = double.NaN;
        double descent_prebrake_UT = double.NaN;
        double descent_burned_start = 0;
        bool descent_atmospheric = false;
        internal string descent_summary = "";

        // Atmospheric coast.
        double aero_interface_UT = double.NaN;
        double aero_brake_UT = double.NaN;
        double aero_brake_dv = double.NaN;
        double aero_predicted_peak = double.NaN;
        string aero_predicted_part = null;
        float next_aero_predict = 0;
        float next_aero_log = 0;
        bool aero_warping = false;
        const float AeroPredictInterval = 2;    // s of real time
        const double AeroWarpLead = 60;         // s before the interface the warp stops

        // Live heat guard.
        internal readonly HeatGuard heat_guard = new HeatGuard();
        internal string heat_message = null;
        internal string heat_readout = null;
        float next_heat_guard_log = 0;
        bool heat_reserve_logged = false;
        const float HeatLogPeriod = 5;

        bool DescentPhaseMode(Mode m) => deorbit_run && (m == Mode.Deorbit || m == Mode.PreBrake);

        void ResetDescent()
        {
            deorbit_run = false;
            descent_plan = default;
            descent_rp = double.NaN;
            descent_prebrake_UT = double.NaN;
            descent_atmospheric = false;
            descent_summary = "";
            aero_interface_UT = double.NaN;
            aero_brake_UT = double.NaN;
            aero_brake_dv = double.NaN;
            aero_predicted_peak = double.NaN;
            aero_predicted_part = null;
            aero_warping = false;
            heat_guard.Reset();
            heat_message = null;
            heat_readout = null;
            heat_reserve_logged = false;
        }

        // ---- Start ----

        // The descent planner takes over the start: no collision course, or above the atmosphere.
        bool NeedsDescentPlan(CelestialBodyComponent body, bool collision)
        {
            if (!collision)
                return true;
            return body.hasAtmosphere && current_vessel.VesselComponent.AltitudeFromSeaLevel > body.atmosphereDepth;
        }

        static double EarliestDeorbitUT(DescentContext ctx, double periapsisRadius)
        {
            double r = ctx.R.magnitude;
            double rp = Math.Min(periapsisRadius, r);
            double guess = Math.Max(0, ctx.V.magnitude - Math.Sqrt(ctx.Body.Mu * 2 * rp / (r * (r + rp))));
            double duration = BurnTiming.Duration(guess, ctx.Vehicle.ThrustKN, ctx.Vehicle.MassT, ctx.Vehicle.Isp);
            return PrecisionPhases.EarliestDeorbitUT(ctx.UT, double.NaN, duration);
        }

        // Plans the whole descent and refuses it when the vessel cannot pay for it. Sets last_error.
        bool CheckCanStartDescent(double landing_accel, double surface_gravity)
        {
            ResetDescent();
            VesselComponent vessel = current_vessel.VesselComponent;
            CelestialBodyComponent body = vessel.Orbit.referenceBody;
            if (StagedBraking && body.hasAtmosphere)
            {
                // StagedBrakePlan has no drag: its drops and budgets would be planned from orbital speed.
                logger.LogInfo("[Landing] deorbit: start refused: staged landing on an atmospheric body without a collision course inside the air");
                last_error = $"Land with stage {staged_choice + 1}: on {body.Name} the staged plan has no drag model. " +
                             "Deorbit first, then start it on a collision course inside the atmosphere.";
                ClearStagedRun();
                return false;
            }
            if (settings.precision_landing.V && LandingProfile.IsAtmospheric)
                logger.LogInfo("[Landing] deorbit: precision landing on an atmospheric body in Propulsive mode: landing without target");

            if (!DescentContext.TryBuild(this, withAero: true, out DescentContext ctx, out string reason))
            {
                logger.LogWarning($"[Landing] deorbit: start refused: {reason}");
                last_error = reason;
                ClearStagedRun();
                return false;
            }
            double rpGuess = ctx.Atmospheric ? body.radius + 0.3 * body.atmosphereDepth : body.radius - DeorbitSearch.DescentPeriapsisMargin;
            var input = new DescentInput
            {
                R = ctx.R, V = ctx.V, UT = ctx.UT, Body = ctx.Body, Vehicle = ctx.Vehicle,
                Atmospheric = ctx.Atmospheric, Air = ctx.Air, Aero = ctx.Aero,
                EarliestBurnUT = EarliestDeorbitUT(ctx, rpGuess),
                FixedPeriapsisRadius = double.NaN,
            };
            if (ctx.Atmospheric)
                logger.LogInfo($"[Landing] heat: model inputs on {body.Name}: {ctx.AeroNote}");
            var timer = Stopwatch.StartNew();
            DescentPlan plan = DescentPlanner.Plan(input);
            timer.Stop();
            LogDescentPlan("start plan", plan, ctx, timer.Elapsed.TotalMilliseconds);

            var delta_v = vessel.VesselDeltaV;
            double dv_remaining = delta_v != null ? delta_v.TotalDeltaVActual : double.NaN;
            FeasibilityResult check = DescentFeasibility.Check(plan, landing_accel, surface_gravity, dv_remaining, ctx.Vehicle);
            logger.LogInfo($"[Landing] deorbit: start check: ok={check.Ok} accel {landing_accel:n2} m/s², surface g {surface_gravity:n2}, " +
                $"Δv {F(dv_remaining)} m/s for {plan.TotalDeltaV:n0} m/s x{LandingFeasibility.DeltaVMargin} ({plan.Breakdown})" +
                (ctx.Vehicle.Staged ? $", staged: stage 1 {ctx.Vehicle.Stages[0].DeltaV:n0} m/s, landing stage {ctx.Vehicle.Stages[ctx.Vehicle.Stages.Length - 1].DeltaV:n0} m/s for {plan.Stop.LandingStageDeltaV:n0}" : "") +
                (check.Ok ? "" : $" -> {check.Error}") + (check.Warning != null ? $" (warning: {check.Warning})" : ""));
            if (!check.Ok)
            {
                last_error = check.Error;
                ClearStagedRun();
                return false;
            }
            last_warning = check.Warning ?? "";
            deorbit_run = true;
            descent_plan = plan;
            descent_atmospheric = ctx.Atmospheric;
            descent_rp = plan.DeorbitPeriapsisRadius;
            descent_prebrake_UT = PreBrakePlanUT(plan, ctx.Vehicle);
            aero_interface_UT = plan.InterfaceUT;
            descent_summary = $"Descent: {plan.Breakdown} m/s, total {plan.TotalDeltaV:n0} m/s" +
                (plan.HeatModelled ? $", predicted heating {plan.HeatPeak * 100:n0} % ({plan.HeatPeakPart})" : "");
            if (ctx.Vehicle.Staged)
                RecordStagedDescentPlan(plan, ctx.Vehicle);
            return true;
        }

        // The pre-brake phase plans PrecisionPhases.PlanLead before the burn starts (TargetOrbitBurn
        // aligns in that time).
        static double PreBrakePlanUT(DescentPlan plan, Precision.Vehicle vehicle)
        {
            if (!plan.NeedsPreBrake)
                return double.NaN;
            double d = BurnTiming.Duration(plan.PreBrakeDeltaV, vehicle.ThrustKN, vehicle.MassT, vehicle.Isp);
            return plan.PreBrake.UT - (double.IsNaN(d) ? 0 : d / 2) - PrecisionPhases.PlanLead;
        }

        Mode DescentFirstMode()
        {
            if (descent_plan.NeedsDeorbit)
                return Mode.Deorbit;
            if (!descent_atmospheric)
                return Mode.Pause;
            return descent_plan.NeedsPreBrake ? Mode.PreBrake : Mode.AeroCoast;
        }

        void LogDescentPlan(string what, DescentPlan plan, DescentContext ctx, double ms)
        {
            double now = ctx.UT;
            double R = ctx.Body.Radius;
            logger.LogInfo($"[Landing] deorbit: {what} {(plan.Ok ? "ok" : "refused: " + plan.Reason)} ({ms:n0} ms, {plan.Predictions} predictions) on {ctx.BodyName}" +
                (ctx.Atmospheric ? " (atmosphere)" : "") + " | " +
                (plan.NeedsDeorbit ? $"deorbit at +{plan.Deorbit.UT - now:n0} s, Δv {plan.DeorbitDeltaV:n1} m/s to pe {(plan.DeorbitPeriapsisRadius - R) / 1000:n1} km" : "no deorbit needed") +
                (plan.NeedsPreBrake ? $" | pre-brake at +{plan.PreBrake.UT - now:n0} s, Δv {plan.PreBrakeDeltaV:n0} m/s" : "") +
                $" | brake Δv {plan.BrakeDeltaV:n0} m/s, start +{F(plan.BrakeStartUT - now)} s | total {plan.TotalDeltaV:n0} m/s" +
                (ctx.Atmospheric ? $" | interface +{F(plan.InterfaceUT - now)} s" : $" | stop {plan.Stop.StopAltitude:n0} m up"));
            if (!ctx.Atmospheric)
                return;
            logger.LogInfo($"[Landing] heat: {what}: " + (plan.HeatModelled
                ? $"predicted peak {plan.HeatPeak * 100:n0} % of the max temperature on {plan.HeatPeakPart ?? "?"} before the brake " +
                  $"(limit {DescentPlanner.HeatLimit * 100:n0} %; {plan.Heat.PeakRatio * 100:n0} % if it never braked) | {plan.Heat} | {plan.AeroBrake}"
                : $"heating not modelled | {plan.Heat} | {plan.AeroBrake}") + (plan.HeatWarning != null ? $" | {plan.HeatWarning}" : ""));
            if (!string.IsNullOrEmpty(plan.Candidates))
                logger.LogInfo($"[Landing] heat: {what}: entries: {plan.Candidates}");
        }

        // A staged descent: its predicted brake (the plan's) is the start plan the drops are logged against.
        void RecordStagedDescentPlan(DescentPlan plan, Precision.Vehicle vehicle)
        {
            StopPrediction stop = plan.Stop;
            BrakeStage landing = vehicle.Stages[vehicle.Stages.Length - 1];
            BrakeDrop? last = stop.Drops != null && stop.Drops.Count > 0 ? stop.Drops[stop.Drops.Count - 1] : (BrakeDrop?)null;
            start_stage_plan = new StagePlan
            {
                Ok = true,
                DropHeight = stop.DropHeight,
                SafeDropHeight = stop.SafeDropHeight,
                Brake = new BrakeResult
                {
                    Status = BrakeStatus.Ok, StartUT = stop.BrakeStartUT, DeltaVNeeded = stop.BrakeDeltaV,
                    StopAltitude = stop.StopAltitude, Drops = stop.Drops,
                },
                LandingStageReached = last.HasValue,
                DroppedDry = last.HasValue && last.Value.Dry,
                DropAltitude = last.HasValue ? last.Value.Altitude : double.NaN,
                SpeedAtDrop = last.HasValue ? last.Value.SurfaceSpeed : double.NaN,
                LandingDeltaVNeeded = stop.LandingStageDeltaV,
                LandingDeltaVAvailable = landing.DeltaV,
            };
            start_stage_plan_valid = true;
            stage_drop_height = double.IsNaN(stop.DropHeight) || stop.DropHeight <= 0 ? vehicle.DropHeight : stop.DropHeight;
            logger.LogInfo($"[Landing] stage plan: deorbit: stage 1 pays {plan.DeorbitDeltaV:n0} of {vehicle.Stages[0].DeltaV:n0} m/s; " +
                $"predicted brake {start_stage_plan.Describe()}; landing stage needs {stop.LandingStageDeltaV:n0} of {landing.DeltaV:n0} m/s");
        }

        // ---- Phases (Deorbit, PreBrake) ----

        void StartDescentPhase(Mode m)
        {
            double now = GeneralTools.Current_UT;
            current_executor.setController(precision_phase);
            precision_phase.allow_warp = settings.auto_warp.V;
            // Landing burns: never rails warp while burning (the user's rule).
            precision_phase.allow_rails_warp = false;
            descent_burned_start = burn_dV.burned_dV;
            if (m == Mode.Deorbit)
                precision_phase.Start("deorbit", now, PlanDescentDeorbit);
            else
            {
                double planUT = double.IsNaN(descent_prebrake_UT) ? now : Math.Max(now, descent_prebrake_UT);
                precision_phase.Start("pre-brake", planUT, PlanDescentPreBrake);
            }
            logger.LogInfo($"[Landing] deorbit: phase {precision_phase.Name} start" +
                (m == Mode.PreBrake ? $", planned in {Math.Max(0, descent_prebrake_UT - now):n0} s (interface +{F(aero_interface_UT - now)} s)" : ""));
        }

        PrecisionPhase.Plan PlanDescentDeorbit()
        {
            if (!DescentContext.TryBuild(this, withAero: false, out DescentContext ctx, out string reason))
                return PhaseError("Deorbit: " + reason);
            double rp = ctx.Atmospheric
                ? (double.IsNaN(descent_rp) ? ctx.Body.Radius + 0.3 * ctx.Air.Depth : descent_rp)
                : ctx.Body.Radius - DeorbitSearch.DescentPeriapsisMargin;
            double mu = ctx.Body.Mu;
            double pe = DeorbitSearch.Periapsis(ctx.R, ctx.V, mu);
            double earliest = EarliestDeorbitUT(ctx, rp);
            Impulse burn = DeorbitSearch.Burn(ctx.R, ctx.V, ctx.UT, ctx.Body, earliest, rp);
            double dv = burn.DeltaV.magnitude;
            logger.LogInfo($"[Landing] deorbit: plan deorbit: pe {(pe - ctx.Body.Radius) / 1000:n1} km -> {(rp - ctx.Body.Radius) / 1000:n1} km, " +
                $"burn at +{burn.UT - ctx.UT:n0} s, Δv {dv:n1} m/s (start plan {descent_plan.DeorbitDeltaV:n1})");
            if (pe <= rp || dv < 0.1)
                return PhaseSkip;
            KeplerPropagator.Propagate(ctx.R, ctx.V, mu, burn.UT - ctx.UT, out Vector3d rb, out Vector3d vb);
            Precision.Vehicle vehicle = ctx.Vehicle;
            return new PrecisionPhase.Plan
            {
                Target = TargetOrbit.FromState(rb, vb + burn.DeltaV, mu),
                StartUT = BurnTiming.CenteredStart(burn.UT, BurnTiming.Duration(dv, vehicle.ThrustKN, vehicle.MassT, vehicle.Isp), ctx.UT),
                PlannedDeltaV = dv,
            };
        }

        PrecisionPhase.Plan PlanDescentPreBrake()
        {
            if (!PlanDescentFromHere("plan pre-brake", out DescentPlan plan, out DescentContext ctx, out string error))
                return PhaseError("Pre-brake: " + error);
            if (!plan.NeedsPreBrake)
                return PhaseSkip;
            double mu = ctx.Body.Mu;
            Impulse burn = plan.PreBrake;
            Vector3d rb = ctx.R, vb = ctx.V;
            if (burn.UT > ctx.UT)
                KeplerPropagator.Propagate(ctx.R, ctx.V, mu, burn.UT - ctx.UT, out rb, out vb);
            Precision.Vehicle vehicle = ctx.Vehicle;
            double dv = plan.PreBrakeDeltaV;
            return new PrecisionPhase.Plan
            {
                Target = TargetOrbit.FromState(rb, vb + burn.DeltaV, mu),
                StartUT = BurnTiming.CenteredStart(burn.UT, BurnTiming.Duration(dv, vehicle.ThrustKN, vehicle.MassT, vehicle.Isp), ctx.UT),
                PlannedDeltaV = dv,
            };
        }

        // The atmospheric plan from the current state (the start's entry periapsis kept), logged.
        bool PlanDescentFromHere(string what, out DescentPlan plan, out DescentContext ctx, out string error)
        {
            plan = default;
            error = null;
            if (!DescentContext.TryBuild(this, withAero: true, out ctx, out string reason))
            {
                error = reason;
                return false;
            }
            var input = new DescentInput
            {
                R = ctx.R, V = ctx.V, UT = ctx.UT, Body = ctx.Body, Vehicle = ctx.Vehicle,
                Atmospheric = ctx.Atmospheric, Air = ctx.Air, Aero = ctx.Aero,
                EarliestBurnUT = ctx.UT + PrecisionPhases.PlanLead,
                FixedPeriapsisRadius = descent_rp,
            };
            var timer = Stopwatch.StartNew();
            plan = DescentPlanner.Plan(input);
            timer.Stop();
            LogDescentPlan(what, plan, ctx, timer.Elapsed.TotalMilliseconds);
            if (!plan.Ok)
            {
                error = plan.Reason;
                return false;
            }
            return true;
        }

        void OnDescentPhaseEnd()
        {
            double used = burn_dV.burned_dV - descent_burned_start;
            string name = precision_phase.Name;
            string orbit = "";
            try
            {
                IKeplerPatch o = current_vessel.VesselComponent.Orbit;
                var body = o.referenceBody;
                double now = GeneralTools.Current_UT;
                OrbitElements el = OrbitMath.Elements(o.GetRelativePositionAtUTZup(now), o.GetOrbitalVelocityAtUTZup(now), body.gravParameter);
                orbit = $"orbit ap {(el.ApoapsisRadius - body.radius) / 1000:n1} km pe {(el.PeriapsisRadius - body.radius) / 1000:n1} km";
            }
            catch (Exception e)
            {
                orbit = $"orbit not read ({e.GetType().Name})";
            }
            logger.LogInfo($"[Landing] deorbit: phase {name} end{(precision_phase.Skipped ? " (not needed)" : "")}: Δv used {used:n1} m/s " +
                $"(planned {precision_phase.PlannedDeltaV:n1}) | {orbit}");
            current_vessel.SetThrottle(0);
            current_executor.setController(null);
            NextDescentMode();
        }

        // After the deorbit or the pre-brake. Airless: the usual sequence, now on a collision course.
        void NextDescentMode()
        {
            if (!descent_atmospheric)
            {
                setMode(Mode.Pause);
                return;
            }
            if (mode == Mode.Deorbit)
            {
                // Plan the pre-brake from the orbit really reached.
                if (PlanDescentFromHere("after the deorbit", out DescentPlan plan, out DescentContext ctx, out string error))
                {
                    descent_plan.Heat = plan.Heat;
                    aero_interface_UT = plan.InterfaceUT;
                    if (plan.NeedsPreBrake)
                    {
                        descent_prebrake_UT = PreBrakePlanUT(plan, ctx.Vehicle);
                        setMode(Mode.PreBrake);
                        return;
                    }
                }
                else
                {
                    last_warning = "Reentry plan after the deorbit refused: " + error + " The heat guard watches the descent.";
                    logger.LogWarning($"[Landing] heat: {last_warning}");
                }
            }
            setMode(Mode.AeroCoast);
        }

        // A deorbit or pre-brake burn that could not be flown. Stop while the orbit is still safe;
        // once it may be on its way down, go on (the descent cannot be undone).
        void OnDescentPhaseFailed(string reason)
        {
            bool down = true;
            string orbit = "orbit not read";
            try
            {
                IKeplerPatch o = current_vessel.VesselComponent.Orbit;
                var body = o.referenceBody;
                double now = GeneralTools.Current_UT;
                double pe = DeorbitSearch.Periapsis(o.GetRelativePositionAtUTZup(now), o.GetOrbitalVelocityAtUTZup(now), body.gravParameter);
                double floor = body.radius + (body.hasAtmosphere ? body.atmosphereDepth : 0);
                down = pe < floor;
                orbit = $"pe {(pe - body.radius) / 1000:n1} km";
            }
            catch (Exception e)
            {
                orbit = $"orbit not read ({e.GetType().Name})";
            }
            logger.LogWarning($"[Landing] deorbit: phase {precision_phase.Name} failed: {reason} | {orbit}, on the way down {down}");
            if (!down)
            {
                StopWithError(reason);
                return;
            }
            current_vessel.SetThrottle(0);
            current_executor.setController(null);
            last_warning = $"{reason} The landing goes on from the trajectory reached.";
            NextDescentMode();
        }

        // ---- Atmospheric coast ----

        void StartAeroCoast()
        {
            current_vessel.SetThrottle(0);
            current_executor.setController(null);
            aero_warping = false;
            next_aero_predict = 0;
            PredictAero(force: true);
            var vessel = current_vessel.VesselComponent;
            double now = GeneralTools.Current_UT;
            bool above = vessel.AltitudeFromSeaLevel > vessel.Orbit.referenceBody.atmosphereDepth;
            if (above && settings.auto_warp.V && !double.IsNaN(aero_interface_UT) && aero_interface_UT - AeroWarpLead > now + 10)
            {
                // Coasting outside the air: the existing warp helper, stopped a minute before the interface.
                current_executor.setController(warp_to);
                warp_to.Start_Ut(aero_interface_UT - AeroWarpLead);
                warp_to.max_warp_index = 6;
                aero_warping = true;
            }
            logger.LogInfo($"[Landing] heat: atmospheric coast start: altitude {vessel.AltitudeFromSeaLevel:n0} m, interface +{F(aero_interface_UT - now)} s, " +
                $"brake start +{F(aero_brake_UT - now)} s, Δv {F(aero_brake_dv)} m/s, predicted peak {F(aero_predicted_peak * 100)} % ({aero_predicted_part ?? "?"})" +
                (aero_warping ? $", warping to {AeroWarpLead:n0} s before the interface" : ""));
        }

        // The brake start and the heating predicted with drag from the current state.
        void PredictAero(bool force)
        {
            float now_real = Time.realtimeSinceStartup;
            if (!force && now_real < next_aero_predict)
                return;
            next_aero_predict = now_real + AeroPredictInterval;
            if (!DescentContext.TryBuild(this, withAero: true, out DescentContext ctx, out string reason) || !ctx.Atmospheric)
            {
                aero_brake_UT = double.NaN;
                LogAero($"prediction not available: {reason ?? "no atmosphere"}");
                return;
            }
            var timer = Stopwatch.StartNew();
            HeatPrediction heat = HeatingPredictor.Predict(ctx.R, ctx.V, ctx.UT, ctx.Body, ctx.Air, ctx.Aero, null);
            AeroBrakeResult brakeAt = heat.Ok ? AeroBrake.Find(heat.Samples, ctx.Body, ctx.UT, ctx.Vehicle) : default;
            timer.Stop();
            if (!heat.Ok || !brakeAt.Ok)
            {
                aero_brake_UT = double.NaN;
                LogAero($"prediction failed ({(heat.Ok ? brakeAt.ToString() : heat.Reason)}): braking at the vacuum start instead");
                return;
            }
            if (!double.IsNaN(heat.InterfaceUT) && !heat.StartsInAir)
                aero_interface_UT = heat.InterfaceUT;
            aero_brake_UT = brakeAt.StartUT;
            aero_brake_dv = brakeAt.DeltaV;
            aero_predicted_peak = HeatingPredictor.PeakBefore(heat, brakeAt.StartUT, out aero_predicted_part);
            LogAero($"prediction: brake start +{brakeAt.StartUT - ctx.UT:n0} s, {brakeAt} | heating {aero_predicted_peak * 100:n0} % before the brake " +
                $"({aero_predicted_part ?? "?"}) | {heat} | {timer.Elapsed.TotalMilliseconds:n0} ms");
        }

        void LogAero(string line)
        {
            float now_real = Time.realtimeSinceStartup;
            if (now_real < next_aero_log)
                return;
            next_aero_log = now_real + HeatLogPeriod;
            logger.LogInfo("[Landing] heat: " + line);
        }

        // Every frame in Mode.AeroCoast: warp outside the air, 1x in it, surface retrograde, the guard's
        // throttle, and Brake at the start predicted with drag (the vacuum one when it fails).
        void UpdateAeroCoast()
        {
            var vessel = current_vessel.VesselComponent;
            var body = vessel.Orbit.referenceBody;
            double now = GeneralTools.Current_UT;
            bool inAir = vessel.AltitudeFromSeaLevel <= body.atmosphereDepth;
            if (aero_warping)
            {
                warp_to.UT = aero_interface_UT - AeroWarpLead;
                if (inAir || now >= aero_interface_UT - AeroWarpLead)
                {
                    aero_warping = false;
                    current_executor.setController(null);
                    TimeWarpTools.SetRateIndex(0, false);
                }
            }
            if (!aero_warping)
            {
                // The user's rule: 1x for the reentry.
                if (inAir)
                    TimeWarpTools.SetRateIndex(0, false);
                current_vessel.SetSpeedMode(SpeedDisplayMode.Surface);
                SASTool.setAutoPilot(AutopilotMode.Retrograde);
            }
            PredictAero(force: false);
            current_vessel.SetThrottle(heat_guard.Active ? heat_guard.Throttle : 0);

            double start = !double.IsNaN(aero_brake_UT) ? aero_brake_UT - settings.burn_before.V : startBurn_UT;
            startBurn_UT = start;
            if (!aero_warping && now >= start)
            {
                logger.LogInfo($"[Landing] heat: brake at altitude {vessel.AltitudeFromSeaLevel:n0} m, speed {current_vessel.VesselVehicle.SurfaceSpeed:n0} m/s, " +
                    $"Mach {vessel.MachNumber:n2} ({(double.IsNaN(aero_brake_UT) ? "vacuum start" : $"predicted with drag, Δv {aero_brake_dv:n0} m/s")}) | " +
                    $"Δv used so far {burn_dV.burned_dV:n0} m/s, hottest part peak {heat_guard.PeakRatio * 100:n0} %");
                setMode(Mode.Brake);
            }
        }

        // ---- Heat guard ----

        bool CoastingMode(Mode m) => m == Mode.AeroCoast || m == Mode.Pause || m == Mode.QuickWarp || m == Mode.RotationWarp || m == Mode.Waiting;

        // Every frame of a propulsive run on an atmospheric body: the hottest part, the guard's decision,
        // and in a coasting mode the switch to the atmospheric coast, which flies the guard's throttle.
        void UpdateHeatWatch()
        {
            if (!isRunning || reentry_run || mode == Mode.Parachute || !LandingProfile.IsAtmospheric)
                return;
            var vessel = current_vessel.VesselComponent;
            var body = vessel.Orbit.referenceBody;
            double sea = vessel.AltitudeFromSeaLevel;
            if (!body.hasAtmosphere || sea > body.atmosphereDepth)
                return;
            double ratio = ReadHottest(vessel, out string part);
            double mach = vessel.MachNumber;
            double angle = RetrogradeAngle();
            bool hasThrust = burn_dV.active_dv > 0;
            bool wasActive = heat_guard.Active;
            HeatGuardEvent ev = heat_guard.Update(ratio, mach, angle, hasThrust);
            heat_readout = $"{part ?? "?"} {ratio * 100:n0} % (peak {heat_guard.PeakRatio * 100:n0} %)" + (heat_guard.Active ? ", guard on" : "");

            if (ev == HeatGuardEvent.Started)
            {
                logger.LogWarning($"[Landing] heat: guard on in {mode}: {part} at {ratio * 100:n0} % of its max temperature, altitude {sea:n0} m, " +
                    $"speed {current_vessel.VesselVehicle.SurfaceSpeed:n0} m/s, Mach {mach:n2}, {angle:n0}° from retrograde, Δv left {F(vessel.VesselDeltaV?.TotalDeltaVActual ?? double.NaN)} m/s");
                if (CoastingMode(mode) && mode != Mode.AeroCoast)
                    setMode(Mode.AeroCoast);
            }
            else if (ev == HeatGuardEvent.Stopped)
                logger.LogInfo($"[Landing] heat: guard off in {mode}: {part} at {ratio * 100:n0} %, Mach {mach:n2}, altitude {sea:n0} m | Δv burned so far {burn_dV.burned_dV:n0} m/s");

            heat_message = null;
            if (heat_guard.Overheating)
                heat_message = HeatGuard.OverheatMessage(part, ratio);
            else if (heat_guard.NoThrust)
                heat_message = HeatGuard.NoThrustMessage;
            else if (heat_guard.Active && deorbit_run && !double.IsNaN(aero_brake_dv))
            {
                double left = vessel.VesselDeltaV?.TotalDeltaVActual ?? double.NaN;
                if (left > 0 && left < LandingFeasibility.DeltaVMargin * aero_brake_dv)
                {
                    heat_message = $"Heat guard is using the landing's Δv reserve: {left:n0} m/s left for a {aero_brake_dv:n0} m/s brake.";
                    if (!heat_reserve_logged)
                    {
                        heat_reserve_logged = true;
                        logger.LogWarning("[Landing] heat: " + heat_message);
                    }
                }
            }

            float now_real = Time.realtimeSinceStartup;
            if ((mach >= 1 || heat_guard.Active || wasActive) && now_real >= next_heat_guard_log)
            {
                next_heat_guard_log = now_real + HeatLogPeriod;
                logger.LogInfo($"[Landing] heat: {mode}: altitude {sea:n0} m, speed {current_vessel.VesselVehicle.SurfaceSpeed:n0} m/s, Mach {mach:n2}, " +
                    $"hottest {part ?? "?"} {ratio * 100:n0} % (peak {heat_guard.PeakRatio * 100:n0} %), guard {(heat_guard.Active ? $"on, throttle {heat_guard.Throttle * 100:n0} %" : "off")}, " +
                    $"{angle:n0}° from retrograde | predicted {F(aero_predicted_peak * 100)} % before the brake" + (heat_message != null ? $" | {heat_message}" : ""));
            }
        }

        // The hottest part of the active vessel (Temperature / MaxTemp), 0 when none can be read.
        static double ReadHottest(VesselComponent vessel, out string name)
        {
            name = null;
            double hottest = 0;
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
                        name = part.PartName;
                    }
                }
            }
            catch (Exception) { /* temperatures not readable: 0 */ }
            return hottest;
        }

        // Angle (degrees) between the vessel's thrust axis and surface retrograde, as TouchDown measures it.
        double RetrogradeAngle()
        {
            try
            {
                var telemetry = SASTool.getTelemetry();
                Vector retro = telemetry.SurfaceMovementRetrograde;
                Rotation rotation = Rotation.Reframed(current_vessel.GetRotation(), retro.coordinateSystem);
                Vector3d forward = (rotation.localRotation * Vector3.up).normalized;
                return Vector3d.Angle(retro.vector, forward);
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }
    }
}

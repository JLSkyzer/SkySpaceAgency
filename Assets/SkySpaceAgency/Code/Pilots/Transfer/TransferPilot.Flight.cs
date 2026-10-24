using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using K2D2.KSPService;
using K2D2.Node;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP.Sim.impl;
using KSP.Sim.Maneuver;
using KSP2FlightAssistant.MathLibrary;
using KTools;
using UnityEngine;

namespace K2D2.Transfer
{
    // The flight: one decision per frame (TransferFlight.Next), the warp, the burns, the corrections,
    // the leg re-plans and Prepare only's nodes.
    public partial class TransferPilot
    {
        readonly TargetOrbitBurn burn = new TargetOrbitBurn();
        readonly WarpTo warp = new WarpTo();
        readonly BurndV burn_dv = new BurndV();
        // Prepare only: the node made for StepIndex (built in the constructor, with the logger).
        readonly PrepareOnlyNode node;

        int last_body = -1;
        bool warping;
        double warp_until = double.NaN;
        bool soi_wait_logged;

        // The burn being flown.
        bool burning;
        string burn_name;
        double burn_planned_dv;
        int burn_body;
        Vector3d burn_r0, burn_v0;
        double burn_ut0;
        // The burn of StepIndex has ignited (saved: a cut planned-vector burn is planned again on resume).
        bool burn_ignited;
        int solve_retries;
        double solve_burn_seconds;

        // The SOI change ahead: ours (PatchedPropagator) and the game's patch, cross-checked.
        double soi_ours = double.NaN, soi_game = double.NaN;
        int soi_next_body = -1;
        double soi_checked_ut = double.NaN;

        // A leg whose correction is solved without the stale planned arrival (direct descent).
        int free_arrival_step = -1;
        float next_snapshot_retry;

        void ResetFlight(int body)
        {
            last_body = body;
            warping = false;
            warp_until = double.NaN;
            soi_wait_logged = false;
            burning = false;
            soi_ours = soi_game = double.NaN;
            soi_next_body = -1;
            soi_checked_ut = double.NaN;
            free_arrival_step = -1;
            node.Clear();
            burn_ignited = false;
            solve_retries = 0;
            solve_burn_seconds = 0;
        }

        void Fly()
        {
            var vesselVerdict = CheckVessel("check");
            if (vesselVerdict == VesselVerdict.Wait)
            {
                Status = "Waiting for the vessel...";
                return;
            }
            if (vesselVerdict == VesselVerdict.Changed)
            {
                Save();
                isRunning = false;
                checked_vessel = null;
                Status = "The active vessel changed: transfer stopped. Switch back to it to resume.";
                return;
            }
            if (job != null)
                return; // a solve or a leg re-plan is running
            if (!ReadLight(out int body, out var r, out var v, out double now, out VesselComponent vessel))
            {
                Status = "Waiting for the vessel...";
                return;
            }
            if (body != last_body)
                OnSoiChange(last_body, body, now);

            if (burning)
            {
                UpdateBurn(now, body);
                return;
            }
            if (node.Pending)
                return;
            if (node.Waiting)
            {
                UpdateNode(body, v, now);
                return;
            }

            var plan = Current;
            if (StepIndex < plan.Steps.Count)
            {
                var leg = TransferFlight.LegStart(plan, StepIndex, body);
                if (leg != LegReplan.None)
                {
                    StartLeg(leg, body);
                    return;
                }
            }

            // Re-check a predicted SOI change that has not come.
            if (!double.IsNaN(soi_ours) && now > soi_ours + 10 && !(now < soi_checked_ut + 30))
                UpdateSoiPrediction("the predicted SOI change has not come");

            double lead = StepIndex < plan.Steps.Count ? TransferFlight.BurnLead(EstimateBurnSeconds(plan.Steps[StepIndex].DeltaVMagnitude)) : 0;
            int final = plan.Request?.ToBody ?? -1;
            var d = TransferFlight.Next(plan, StepIndex, body, final, now, lead, NextSoiUT(now));
            SetPhase(d.Phase);

            if (d.Action != FlightAction.Warp)
                StopWarp();
            if (d.Action != FlightAction.WaitSoi)
                soi_wait_logged = false;

            // Prepare only: the node of the next burn as soon as the vessel is in its SOI.
            if (PrepareOnly && StepIndex < plan.Steps.Count && (d.Action == FlightAction.Warp || IsBurnAction(d.Action)))
            {
                var step = plan.Steps[StepIndex];
                if (TransferFlight.IsBurn(step) && step.Body == body)
                {
                    StartStep(StepIndex, ActionFor(step));
                    return;
                }
            }

            switch (d.Action)
            {
                case FlightAction.Done:
                    Finish(body, r, v, now);
                    return;
                case FlightAction.StepPassed:
                    logger.LogInfo($"[Transfer] {TransferFlight.StepName(plan, StepIndex)} passed");
                    StepDone();
                    return;
                case FlightAction.Warp:
                    Coast(d.WarpUntilUT, now);
                    return;
                case FlightAction.SoiMissed:
                    SoiMissed(d, now);
                    return;
                case FlightAction.WaitSoi:
                    if (!soi_wait_logged)
                    {
                        soi_wait_logged = true;
                        logger.LogInfo($"[Transfer] waiting at 1x for the SOI change at UT {d.WarpUntilUT:F0} " +
                            $"(ours {soi_ours:F0}, game {soi_game:F0})");
                    }
                    Status = double.IsNaN(d.WarpUntilUT) ? "Waiting for the SOI change..."
                        : $"SOI change in {StrTool.DurationToString(Math.Max(0, d.WarpUntilUT - now))}";
                    return;
                default:
                    StartStep(StepIndex, d.Action);
                    return;
            }
        }

        static bool IsBurnAction(FlightAction a) => a == FlightAction.Burn || a == FlightAction.Solve || a == FlightAction.Capture;

        static FlightAction ActionFor(PlanStep step) =>
            step.Kind == StepKind.Capture ? FlightAction.Capture
            : step.Kind == StepKind.Correction && !(step.DeltaV.sqrMagnitude > 0) ? FlightAction.Solve
            : FlightAction.Burn;

        // ---------------------------------------------------------------- phases and steps

        void SetPhase(TransferPhase phase)
        {
            if (phase == Phase)
                return;
            string step = Current != null && StepIndex < Current.Steps.Count ? TransferFlight.StepName(Current, StepIndex) : "arrival";
            logger.LogInfo($"[Transfer] phase {Phase} -> {phase} (step {StepIndex}: {step})");
            Phase = phase;
            Save();
        }

        void StepDone()
        {
            burn_ignited = false;
            solve_retries = 0;
            solve_burn_seconds = 0;
            node.ResetEngines();
            StepIndex++;
            node.StopWaiting();
            Save();
        }

        void Coast(double until, double now)
        {
            var plan = Current;
            string next = StepIndex < plan.Steps.Count
                ? $"{TransferFlight.StepName(plan, StepIndex)} in {StrTool.DurationToString(Math.Max(0, plan.Steps[StepIndex].UT - now))}"
                : $"arrival in {StrTool.DurationToString(Math.Max(0, plan.ArrivalUT - now))}";
            if (PrepareOnly)
            {
                // Never warp: the player does.
                Status = $"Next: {next} (warp yourself; the next node comes when the vessel is in the right SOI).";
                return;
            }
            if (!warping || Math.Abs(until - warp_until) > 1)
            {
                warp.Start_Ut(until);
                warping = true;
                warp_until = until;
                logger.LogInfo($"[Transfer] warp to UT {until:F0} (in {until - now:F0} s), next: {next}");
            }
            warp.UT = until;
            warp.Update();
            Status = $"Next: {next} - {warp.status_line}";
        }

        void StopWarp()
        {
            if (!warping)
                return;
            warping = false;
            warp_until = double.NaN;
            if (!PrepareOnly)
                TimeWarpTools.SetRateIndex(0, true);
        }

        void StopBurn()
        {
            if (burning || active)
            {
                try
                {
                    K2D2_Plugin.Instance?.current_vessel?.SetThrottle(0);
                }
                catch (Exception)
                {
                    // No vessel (scene change): nothing burns.
                }
            }
            burning = false;
            if (active && !PrepareOnly)
                TimeWarpTools.SetRateIndex(0, true);
        }

        // ---------------------------------------------------------------- burns

        void StartStep(int i, FlightAction action)
        {
            if (Time.realtimeSinceStartup < next_snapshot_retry)
                return;
            if (!TakeSnapshot(out var snap, out string error, out bool transient))
            {
                if (transient)
                {
                    // Again in a second, not every frame (each try logs the body system).
                    next_snapshot_retry = Time.realtimeSinceStartup + 1;
                    Status = $"Waiting: {error}.";
                    return;
                }
                Fail("cannot read the game: " + error);
                return;
            }
            Adopt(snap);
            var plan = Current;
            var step = plan.Steps[i];
            if (snap.Body != step.Body)
            {
                next_snapshot_retry = Time.realtimeSinceStartup + 1;
                Status = "Waiting for the SOI change...";
                return;
            }
            switch (action)
            {
                case FlightAction.Solve:
                    StartSolve(i, snap);
                    return;
                case FlightAction.Capture:
                    StartCapture(i, snap);
                    return;
                default:
                    StartPlannedBurn(i, snap);
                    return;
            }
        }

        double MuOf(int body) => System[body].Mu;

        // A burn with a planned vector: its guidance from the state at the burn time.
        void StartPlannedBurn(int i, Snapshot snap)
        {
            var plan = Current;
            var step = plan.Steps[i];
            double mu = MuOf(snap.Body);
            double burnUT = Math.Max(step.UT, snap.UT + TransferFlight.BurnLead(0) - TransferFlight.BurnWarpMargin);
            if (step.UT < snap.UT - PlanStore.MissedBurnSeconds)
            {
                Fail($"the {TransferFlight.StepName(plan, i)} was due {StrTool.DurationToString(snap.UT - step.UT)} ago");
                return;
            }
            KeplerPropagator.Propagate(snap.R, snap.V, mu, burnUT - snap.UT, out var rb, out var vb);
            var guidance = TransferFlight.GuidanceFor(plan, i, rb, vb, mu, System[snap.Body].Soi);
            var dv = guidance.DeltaVToGo(rb, vb, burnUT, mu);
            if (!Hyperbola.IsFinite(dv))
            {
                Fail($"the {TransferFlight.StepName(plan, i)} cannot be computed from the actual orbit ({guidance.Describe()})");
                return;
            }
            if (PrepareOnly)
            {
                MakeNode(i, burnUT, dv, rb, vb, snap);
                return;
            }
            Ignite(i, guidance, burnUT, dv.magnitude, snap, OrbitPeriod(rb, vb, mu));
        }

        void StartCapture(int i, Snapshot snap)
        {
            var plan = Current;
            var step = plan.Steps[i];
            double mu = MuOf(snap.Body);
            if (!ArrivalPeriapsis(snap, out double peUT, out var rPe, out var vPe))
            {
                Fail($"no periapsis ahead around {System[snap.Body].Name}");
                return;
            }
            var guidance = TransferFlight.GuidanceFor(plan, i, rPe, vPe, mu, System[snap.Body].Soi);
            var dv = guidance.DeltaVToGo(rPe, vPe, peUT, mu);
            if (!Hyperbola.IsFinite(dv))
            {
                Fail($"the capture cannot be computed ({guidance.Describe()})");
                return;
            }
            step.UT = peUT;
            double pe = step.TargetPeriapsisRadius, ap = double.IsNaN(step.TargetApoapsisRadius) ? pe : step.TargetApoapsisRadius;
            double period = 2 * Math.PI * Math.Sqrt(Math.Pow((pe + ap) / 2, 3) / mu);
            logger.LogInfo($"[Transfer] capture: periapsis {(rPe.magnitude - System[snap.Body].Radius) / 1000:F1} km at UT {peUT:F0}, " +
                $"{dv.magnitude:F1} m/s (planned {step.DeltaVMagnitude:F1}), {guidance.Describe()}");
            if (PrepareOnly)
            {
                MakeNode(i, peUT, dv, rPe, vPe, snap);
                return;
            }
            Ignite(i, guidance, peUT, dv.magnitude, snap, period);
        }

        // The next periapsis on the current conic.
        bool ArrivalPeriapsis(Snapshot snap, out double peUT, out Vector3d rPe, out Vector3d vPe)
        {
            peUT = double.NaN;
            rPe = vPe = Vector3d.zero;
            double mu = MuOf(snap.Body);
            var el = OrbitMath.Elements(snap.R, snap.V, mu);
            double horizon = snap.UT + (el.IsClosed ? el.Period : 30 * 86400.0);
            var patches = PatchedPropagator.Propagate(System, snap.Body, snap.R, snap.V, snap.UT, horizon, null, 2);
            var first = patches.FirstOrDefault();
            if (first == null || !(first.PeriapsisUT >= snap.UT))
                return false;
            peUT = first.PeriapsisUT;
            KeplerPropagator.Propagate(snap.R, snap.V, mu, peUT - snap.UT, out rPe, out vPe);
            return true;
        }

        // Starts TargetOrbitBurn centred on centreUT. Staging when dry is on for transfer burns.
        void Ignite(int i, IBurnGuidance guidance, double centreUT, double dv, Snapshot snap, double orbitPeriod)
        {
            var plan = Current;
            string name = TransferFlight.StepName(plan, i);
            double seconds = EstimateBurnSeconds(dv);
            double start = double.IsNaN(seconds) ? centreUT : centreUT - seconds / 2;
            if (TransferFlight.IsLongBurn(seconds, orbitPeriod))
            {
                string w = $"The {name} lasts {StrTool.DurationToString(seconds)}, more than {TransferFlight.LongBurnShare * 100:F0} % of the orbit period ({StrTool.DurationToString(orbitPeriod)}): the closed loop corrects, but expect a larger error.";
                logger.LogWarning("[Transfer] " + w);
                if (!plan.Warnings.Contains(w))
                    plan.Warnings.Add(w);
            }
            burn.stage_when_dry = true;
            burn.allow_warp = true;
            burn_ignited = false;
            burn.Start(guidance, start);
            if (burn.failure_reason != null)
            {
                Fail($"{name}: {burn.failure_reason}");
                return;
            }
            burning = true;
            burn_name = name;
            burn_planned_dv = dv;
            burn_body = snap.Body;
            burn_r0 = snap.R;
            burn_v0 = snap.V;
            burn_ut0 = snap.UT;
            logger.LogInfo($"[Transfer] {name}: planned {dv:F1} m/s (plan {plan.Steps[i].DeltaVMagnitude:F1}), {guidance.Describe()}, " +
                $"centred at UT {centreUT:F0} (in {centreUT - snap.UT:F0} s), burn ~{seconds:F0} s, stages when dry");
        }

        void UpdateBurn(double now, int body)
        {
            if (body != burn_body)
            {
                // The burn crossed an SOI: its guidance (v0, the asymptote, the target orbit) was in the
                // old body's frame. Stop it here; the next correction absorbs the rest.
                StopBurn();
                logger.LogWarning($"[Transfer] {burn_name}: the burn crossed into {System[body].Name}: stopped, the next correction absorbs the rest");
                StepDone();
                UpdateSoiPrediction($"after the {burn_name} crossed an SOI");
                return;
            }
            burn.Update();
            Status = $"{burn_name}: {burn.status_line}";
            if (!burn_ignited && burn.mode == TargetOrbitBurn.Mode.Burn)
            {
                burn_ignited = true;
                Save();
            }
            if (burn.failure_reason != null)
            {
                Fail($"{burn_name}: {burn.failure_reason}");
                return;
            }
            if (!burn.finished)
                return;
            burning = false;
            string actual = "unknown (SOI changed)";
            if (ReadLight(out int b, out var r, out var v, out double t, out _) && b == burn_body)
            {
                KeplerPropagator.Propagate(burn_r0, burn_v0, MuOf(b), t - burn_ut0, out _, out var vCoast);
                actual = $"{(v - vCoast).magnitude:F1} m/s";
            }
            logger.LogInfo($"[Transfer] {burn_name} done: planned {burn_planned_dv:F1} m/s, actual {actual}");
            bool capture = Current.Steps[StepIndex].Kind == StepKind.Capture;
            StepDone();
            UpdateSoiPrediction($"after the {burn_name}");
            if (capture && ReadLight(out b, out r, out v, out t, out _))
                logger.LogInfo($"[Transfer] after the capture: {OrbitText(b, r, v)}");
        }

        // ---------------------------------------------------------------- corrections

        sealed class SolveResult
        {
            public bool Ok, Converged;
            public Vector3d DeltaV;
            public double Pe, Inc, ArrivalUT, CaptureDv, Without;
        }

        void StartSolve(int i, Snapshot snap)
        {
            var plan = Current;
            var step = plan.Steps[i];
            int n = TransferFlight.CorrectionNumber(plan, i);
            double mu = MuOf(snap.Body);
            // A retry after a slow solve leaves room for the whole burn (its half before the centre).
            double burnUT = Math.Max(step.UT, snap.UT + TransferFlight.BurnLead(solve_burn_seconds));
            KeplerPropagator.Propagate(snap.R, snap.V, mu, burnUT - snap.UT, out var rb, out var vb);
            if (step.TargetBody < 0 || double.IsNaN(step.TargetPeriapsisRadius))
            {
                logger.LogInfo($"[Transfer] correction {n} skipped: no periapsis to aim at ({(step.TargetBody < 0 ? "no target" : "a pass-through")})");
                StepDone();
                return;
            }
            bool freeArrival = free_arrival_step == i;
            double planned = freeArrival ? double.NaN : TransferFlight.LegArrivalUT(plan, i);
            double legArrival = TransferFlight.LegArrivalUT(plan, i);
            double trip = Math.Max(plan.TripSeconds, legArrival - burnUT);
            double horizon = Math.Max(Math.Max(plan.ArrivalUT, legArrival) + 0.25 * Math.Max(0, trip), burnUT + 3600);
            double captureAp = TransferFlight.CaptureApoapsis(plan, i);
            var s = System;
            int body = snap.Body, target = step.TargetBody;
            double wanted = step.TargetPeriapsisRadius, inc = step.TargetInclinationDeg;
            Status = $"Correction {n}: solving...";
            RunJob(JobKind.Solve, ct =>
            {
                var res = new SolveResult();
                res.Without = TransferFlight.PeriapsisWithout(s, body, rb, vb, burnUT, target, horizon);
                ct.ThrowIfCancellationRequested();
                res.Ok = ArrivalTargeting.Solve(s, body, rb, vb, burnUT, target, wanted, inc, horizon, planned, captureAp,
                    out res.DeltaV, out res.Pe, out res.Inc, out res.ArrivalUT, out res.CaptureDv, out res.Converged);
                return res;
            }, t => OnSolved(t, i, n, burnUT, rb, vb, snap));
        }

        void OnSolved(Task<SolveResult> t, int i, int n, double burnUT, Vector3d rb, Vector3d vb, Snapshot snap)
        {
            if (!isRunning)
                return;
            if (!PilotJob.Result(t, out var res, out string error, out bool canceled))
            {
                if (!canceled)
                    Fail($"correction {n}: the solve failed ({error})");
                return;
            }
            var plan = Current;
            var step = plan.Steps[i];
            var target = System[step.TargetBody];
            double R = target.Radius;
            if (!res.Ok)
            {
                Fail($"correction {n}: no correction meets {target.Name} within {ArrivalTargeting.MaxDeltaV:F0} m/s");
                return;
            }
            double dv = res.DeltaV.magnitude;
            double seconds = EstimateBurnSeconds(dv);
            double startUT = double.IsNaN(seconds) ? burnUT : burnUT - seconds / 2;
            double nowUT = GeneralTools.Current_UT;
            if (startUT < nowUT + BurnWarp.MinLead)
            {
                solve_burn_seconds = double.IsNaN(seconds) ? 0 : seconds;
                // The solve took too long: its burn time has (nearly) passed. Solve again from now.
                if (solve_retries++ < 2)
                {
                    logger.LogInfo($"[Transfer] correction {n}: solved in {last_job_ms:F0} ms, but the burn would start {startUT - nowUT:F0} s from now: solving again");
                    StartStep(i, FlightAction.Solve);
                    return;
                }
                Fail($"correction {n}: the solve is too slow for its burn time");
                return;
            }
            logger.LogInfo($"[Transfer] correction {n}: {dv:F2} m/s in {last_job_ms:F0} ms, arrival periapsis predicted {(res.Pe - R) / 1000:F2} km " +
                $"(wanted {(step.TargetPeriapsisRadius - R) / 1000:F2}, without the correction {(res.Without - R) / 1000:F2}), " +
                $"inclination {res.Inc:F2}° (wanted {step.TargetInclinationDeg:F1}), arrival UT {res.ArrivalUT:F0} " +
                $"(planned {TransferFlight.LegArrivalUT(plan, i):F0}{(free_arrival_step == i ? ", not used: new leg" : "")}), " +
                $"capture {res.CaptureDv:F1} m/s, converged {res.Converged}");

            double floor = target.LowOrbitRadius - 10_000;
            var verdict = TransferFlight.JudgeCorrection(dv, res.Converged, res.Pe, step.TargetPeriapsisRadius, res.Without, floor, out string why);
            if (verdict == CorrectionVerdict.Refuse)
            {
                Fail($"correction {n}: {why}");
                return;
            }

            double shift = TransferFlight.ApplySolve(plan, i, res.ArrivalUT, res.CaptureDv, snap.UT);
            if (Math.Abs(shift) > 1)
                logger.LogInfo($"[Transfer] correction {n}: the arrival moves by {StrTool.DurationToString(Math.Abs(shift))} " +
                    $"({(shift > 0 ? "later" : "earlier")}{(Math.Abs(shift) > 0.01 * Math.Max(1, plan.TripSeconds) ? ", another pass" : "")}): " +
                    "capture at the aimed periapsis, later steps moved, corrections re-timed");
            // StepIndex is unchanged by the sort (only the steps after it move).
            Budget = TransferFlight.RemainingBudget(plan, i, stages);
            if (!Budget.Ok)
            {
                Fail($"after correction {n}, the Δv no longer fits: {Budget.Refusal}");
                return;
            }
            Save();

            if (verdict == CorrectionVerdict.Skip)
            {
                logger.LogInfo($"[Transfer] correction {n} skipped: {dv:F2} m/s");
                StepDone();
                return;
            }
            var guidance = new FixedImpulseGuidance(res.DeltaV);
            if (PrepareOnly)
            {
                MakeNode(i, burnUT, res.DeltaV, rb, vb, snap);
                return;
            }
            Ignite(i, guidance, burnUT, dv, snap, OrbitPeriod(rb, vb, MuOf(snap.Body)));
        }

        // ---------------------------------------------------------------- legs

        void StartLeg(LegReplan how, int body)
        {
            var plan = Current;
            int i = StepIndex;
            int leg = plan.Steps[i].LegIndex;
            logger.LogInfo($"[Transfer] leg {leg} starts in {System[body].Name}: {how}");
            switch (how)
            {
                case LegReplan.Search:
                {
                    if (Time.realtimeSinceStartup < next_snapshot_retry)
                        return;
                    if (!TakeSnapshot(out var snap, out string error, out bool transient))
                    {
                        if (!transient)
                            Fail("cannot read the game for the leg re-plan: " + error);
                        next_snapshot_retry = Time.realtimeSinceStartup + 1;
                        return;
                    }
                    Adopt(snap);
                    var q = CopyRequest(plan.Request);
                    q.FromBody = snap.Body;
                    q.R0 = snap.R;
                    q.V0 = snap.V;
                    q.UT0 = snap.UT;
                    Status = $"Leg {leg}: planning from the orbit reached...";
                    var system = snap.System;
                    StartSearch(system, q, JobKind.Search, replanned =>
                    {
                        if (!isRunning)
                            return;
                        logger.LogInfo(TransferFlight.Describe(replanned, system, last_job_ms));
                        if (!replanned.Ok)
                        {
                            Fail($"leg {leg} cannot be planned from the orbit reached: {replanned.Refusal}");
                            return;
                        }
                        TransferFlight.MergeLeg(plan, i, replanned);
                        TransferFlight.MarkLegStarted(plan, i);
                        Budget = TransferFlight.RemainingBudget(plan, i, stages);
                        if (!Budget.Ok)
                        {
                            Fail($"leg {leg}: {Budget.Refusal}");
                            return;
                        }
                        logger.LogInfo($"[Transfer] leg {leg} planned again from the orbit reached: {plan.Steps.Count - i} step(s) left");
                        Save();
                    });
                    return;
                }
                case LegReplan.SolveCorrection:
                    free_arrival_step = i;
                    break;
                case LegReplan.AimExcess:
                    logger.LogInfo($"[Transfer] leg {leg}: the re-aim burn aims at the required excess {plan.RequiredExcess.magnitude:F1} m/s");
                    break;
                case LegReplan.PlannedImpulse:
                {
                    string w = $"Leg {leg}: the re-aim burn in {System[body].Name} uses its planned vector; the next correction absorbs the difference.";
                    logger.LogWarning("[Transfer] " + w);
                    if (!plan.Warnings.Contains(w))
                        plan.Warnings.Add(w);
                    break;
                }
            }
            TransferFlight.MarkLegStarted(plan, i);
            Save();
        }

        // ---------------------------------------------------------------- Prepare only

        void MakeNode(int i, double impulseUT, Vector3d dv, Vector3d r, Vector3d v, Snapshot snap)
        {
            var plan = Current;
            string name = TransferFlight.StepName(plan, i);
            TransferFlight.NodeComponents(dv, r, v, out double prograde, out double normal, out double radial);
            double seconds = EstimateBurnSeconds(dv.magnitude);
            double nodeUT = Math.Max(snap.UT + ManeuverCreator.MinLead, double.IsNaN(seconds) ? impulseUT : impulseUT - seconds / 2);
            logger.LogInfo($"[Transfer] prepare only: node for the {name} at UT {nodeUT:F0} (impulse {impulseUT:F0}), {dv.magnitude:F1} m/s " +
                $"(prograde {prograde:F1}, normal {normal:F1}, radial {radial:F1})");
            node.Make(name, nodeUT, dv.magnitude, snap.Body, snap.R, snap.V, snap.UT, prograde, normal, radial,
                () => isRunning,
                () => Status = $"Fly the node for the {node.Name} ({node.DeltaV:F1} m/s), then remove it: the next step is planned from the orbit reached.",
                () => Fail($"the game did not take the node for the {node.Name}"));
        }

        void UpdateNode(int body, Vector3d v, double now)
        {
            var expected = node.V;
            bool changed = body != node.Body;
            if (!changed)
                KeplerPropagator.Propagate(node.R, node.V, MuOf(node.Body), now - node.StateUT, out _, out expected);
            // A node left on the plan counts as flown once its time has passed and the engines are off.
            var state = node.Judge(changed, expected, v, now);
            switch (state)
            {
                case NodeState.Waiting:
                    Status = $"Fly the node for the {node.Name} ({node.DeltaV:F1} m/s, " +
                        (node.UT > now ? $"in {StrTool.DurationToString(node.UT - now)}" : $"due {StrTool.DurationToString(now - node.UT)} ago") +
                        "), then remove it: the next step is planned from the orbit reached.";
                    return;
                case NodeState.Flown:
                    logger.LogInfo($"[Transfer] prepare only: {node.Name} flown: planned {node.DeltaV:F1} m/s, measured " +
                        (changed ? "unknown (SOI changed)" : $"{(v - expected).magnitude:F1} m/s"));
                    StepDone();
                    UpdateSoiPrediction($"after the {node.Name}");
                    return;
                default:
                    logger.LogInfo($"[Transfer] prepare only: the node for the {node.Name} was removed without being flown");
                    node.StopWaiting();
                    Save();
                    isRunning = false;
                    checked_vessel = null;
                    Status = $"The node for the {node.Name} was removed without being flown. Resume makes it again.";
                    return;
            }
        }

        // ---------------------------------------------------------------- SOI changes

        double NextSoiUT(double now)
        {
            double ours = soi_ours, game = soi_game;
            bool o = !double.IsNaN(ours) && ours > now - 600;
            bool g = !double.IsNaN(game) && game > now - 600;
            if (o && g)
                return Math.Min(ours, game);
            return o ? ours : g ? game : double.NaN;
        }

        // The next SOI change on the current path: ours, and the game's patch end as a cross-check.
        void UpdateSoiPrediction(string why)
        {
            soi_ours = soi_game = double.NaN;
            soi_next_body = -1;
            if (!ReadLight(out int body, out var r, out var v, out double now, out VesselComponent vessel))
                return;
            soi_checked_ut = now;
            var plan = Current;
            double end = StepIndex < plan.Steps.Count ? plan.Steps[StepIndex].UT : plan.ArrivalUT;
            if (double.IsNaN(end) || end < now)
                end = now;
            double horizon = end + Math.Max(3600, 0.25 * (end - now));
            try
            {
                var patches = PatchedPropagator.Propagate(System, body, r, v, now, horizon, null, 4);
                var p = patches.FirstOrDefault(x => x.End == PatchEnd.Escape || x.End == PatchEnd.Encounter);
                if (p != null)
                {
                    soi_ours = p.EndUT;
                    soi_next_body = p.NextBody;
                }
            }
            catch (Exception e)
            {
                logger.LogWarning($"[Transfer] SOI prediction failed: {e.GetType().Name}: {e.Message}");
            }
            string gameText = "none";
            try
            {
                if (vessel.Orbit is IPatchedOrbit po &&
                    (po.PatchEndTransition == PatchTransitionType.Escape || po.PatchEndTransition == PatchTransitionType.Encounter))
                {
                    soi_game = po.EndUT;
                    gameText = $"{po.PatchEndTransition} at UT {po.EndUT:F0}";
                }
                else if (vessel.Orbit is IPatchedOrbit po2)
                    gameText = $"{po2.PatchEndTransition}";
            }
            catch (Exception e)
            {
                gameText = $"unreadable ({e.GetType().Name})";
            }
            string ours = double.IsNaN(soi_ours) ? "none before UT " + horizon.ToString("F0")
                : $"{System[body].Name} -> {(soi_next_body >= 0 ? System[soi_next_body].Name : "?")} at UT {soi_ours:F0} (in {soi_ours - now:F0} s)";
            string diff = !double.IsNaN(soi_ours) && !double.IsNaN(soi_game) ? $", game - ours {soi_game - soi_ours:F1} s" : "";
            logger.LogInfo($"[Transfer] next SOI change ({why}): ours {ours}; game patch {gameText}{diff}");
        }

        void OnSoiChange(int from, int to, double now)
        {
            string name(int b) => b >= 0 && System != null && b < System.Count ? System[b].Name : $"#{b}";
            logger.LogInfo($"[Transfer] SOI change {name(from)} -> {name(to)} at UT {now:F0}: predicted ours {soi_ours:F0} " +
                $"({(double.IsNaN(soi_ours) ? "none" : (now - soi_ours).ToString("+0.0;-0.0") + " s")}), " +
                $"game patch {soi_game:F0} ({(double.IsNaN(soi_game) ? "none" : (now - soi_game).ToString("+0.0;-0.0") + " s")})");
            last_body = to;
            StopWarp();
            UpdateSoiPrediction("after the SOI change");
            // On entering the capture's SOI, the capture goes to the periapsis really ahead.
            var plan = Current;
            if (plan != null && StepIndex < plan.Steps.Count && plan.Steps[StepIndex].Kind == StepKind.Capture && plan.Steps[StepIndex].Body == to
                && TakeSnapshot(out var snap, out _, out _) && snap.Body == to && ArrivalPeriapsis(snap, out double peUT, out var rPe, out _))
            {
                Adopt(snap);
                logger.LogInfo($"[Transfer] arrival in {name(to)}: periapsis {(rPe.magnitude - System[to].Radius) / 1000:F2} km at UT {peUT:F0} " +
                    $"(planned {plan.Steps[StepIndex].UT:F0}, Pe {(plan.Steps[StepIndex].TargetPeriapsisRadius - System[to].Radius) / 1000:F2} km)");
                plan.Steps[StepIndex].UT = peUT;
            }
            Save();
        }

        // ---------------------------------------------------------------- the end, failures

        void Finish(int body, Vector3d r, Vector3d v, double now)
        {
            string text;
            var plan = Current;
            bool capture = plan.Steps.Any(st => st.Kind == StepKind.Capture);
            if (capture)
            {
                int final = plan.Request?.ToBody ?? body;
                if (body != final || !TransferFlight.InOrbit(r, v, MuOf(body), System[body].Soi))
                {
                    Fail($"after the capture the vessel is not in a closed orbit of {System[final].Name} ({OrbitText(body, r, v)})", captureFailed: true);
                    return;
                }
                var el = OrbitMath.Elements(r, v, MuOf(body));
                double R = System[body].Radius;
                text = TransferFlight.FinalOrbit(System[body].Name, el.PeriapsisRadius - R, el.ApoapsisRadius - R, el.InclinationRad * 180 / Math.PI);
            }
            else
                text = $"Encounter with {System[body].Name}: {OrbitText(body, r, v)}";
            logger.LogInfo($"[Transfer] done: {text}; final orbit {OrbitText(body, r, v)}");
            Phase = TransferPhase.Done;
            DeleteSaved(vessel_id);
            isRunning = false;
            Status = text;
            checked_vessel = null;
        }

        // An SOI change the plan needs did not come: predict once more, then fail.
        void SoiMissed(FlightDecision d, double now)
        {
            if (!(soi_checked_ut >= now - 1))
            {
                UpdateSoiPrediction("the planned SOI change is late");
                return;
            }
            var plan = Current;
            int final = plan.Request?.ToBody ?? -1;
            string name = d.MissedBody >= 0 && d.MissedBody < System.Count ? System[d.MissedBody].Name : "the next body";
            if (!d.MissedExit && d.MissedBody == final)
                Fail($"the {name} SOI was not reached{By(d.WarpUntilUT)}", captureFailed: true);
            else
                Fail(d.MissedExit
                    ? $"the planned SOI exit from {name} did not happen{By(d.WarpUntilUT)}"
                    : $"the planned SOI change into {name} did not happen{By(d.WarpUntilUT)}");
        }

        void Fail(string reason, bool captureFailed = false)
        {
            bool wasRunning = isRunning;
            StopBurn();
            StopWarp();
            CancelJob();
            var plan = Current;
            bool capture = captureFailed || Phase == TransferPhase.Capture
                || (plan != null && StepIndex < plan.Steps.Count && plan.Steps[StepIndex].Kind == StepKind.Capture
                    && ReadLight(out int b, out _, out _, out _, out _) && b == plan.Steps[StepIndex].Body);
            string target = plan?.Request != null && System != null && plan.Request.ToBody >= 0 && plan.Request.ToBody < System.Count
                ? System[plan.Request.ToBody].Name : "the target";
            string text = capture ? $"{TransferFlight.CaptureFailed(target)}. {Cap(reason)}." : $"Transfer stopped: {reason}.";
            logger.LogWarning($"[Transfer] failure in {Phase} at step {StepIndex}: {reason}" + (capture ? " (capture failed)" : ""));
            if (wasRunning)
            {
                Phase = TransferPhase.Failed;
                Save();
            }
            isRunning = false;
            Status = text;
            checked_vessel = null; // offer the saved plan again
        }

        static string By(double ut) => double.IsNaN(ut) ? " (the plan gives no time for it)" : $" by UT {ut:F0}";

        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // ---------------------------------------------------------------- reads

        // The vessel's body (an index into the snapshot's body list) and state, without rebuilding the
        // body system.
        bool ReadLight(out int body, out Vector3d r, out Vector3d v, out double ut, out VesselComponent vessel)
        {
            body = -1;
            r = v = Vector3d.zero;
            ut = 0;
            vessel = ActiveVessel();
            IKeplerPatch orbit = vessel?.Orbit;
            if (orbit == null || System == null)
                return false;
            ut = GeneralTools.Current_UT;
            r = orbit.GetRelativePositionAtUTZup(ut);
            v = orbit.GetOrbitalVelocityAtUTZup(ut);
            body = GameBodySystem.IndexOf(bodies ?? GameBodySystem.GameBodies(), orbit.referenceBody);
            return body >= 0 && body < System.Count;
        }

        // Seconds at full thrust for dv; NaN without thrust.
        double EstimateBurnSeconds(double dv) => PilotVessel.BurnSeconds(burn_dv, dv);

        static double OrbitPeriod(Vector3d r, Vector3d v, double mu)
        {
            var el = OrbitMath.Elements(r, v, mu);
            return el.IsClosed ? el.Period : double.PositiveInfinity;
        }

        string OrbitText(int body, Vector3d r, Vector3d v)
        {
            var el = OrbitMath.Elements(r, v, MuOf(body));
            double R = System[body].Radius;
            return $"{System[body].Name}: Pe {(el.PeriapsisRadius - R) / 1000:F1} km, " +
                (el.IsClosed ? $"Ap {(el.ApoapsisRadius - R) / 1000:F1} km, " : $"e {el.Eccentricity:F3}, ") +
                $"inclination {el.InclinationRad * 180 / Math.PI:F2}°";
        }
    }
}

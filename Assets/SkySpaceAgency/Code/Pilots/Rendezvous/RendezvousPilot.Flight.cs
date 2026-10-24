using System;
using System.Collections;
using K2D2.KSPService;
using K2D2.Node;
using K2D2.OrbitPlanning;
using K2D2.Transfer;
using KSP.Sim;
using KSP.Sim.impl;
using KSP.Sim.Maneuver;
using KSP2FlightAssistant.MathLibrary;
using KTools;
using UnityEngine;

namespace K2D2.Rendezvous
{
    // The rendezvous flight: one decision per frame (RendezvousFlight.Next), the warp, the burns,
    // the correction, the arrival check and Prepare only's nodes.
    public partial class RendezvousPilot
    {
        readonly TargetOrbitBurn burn = new TargetOrbitBurn();
        readonly WarpTo warp = new WarpTo();
        readonly BurndV burn_dv = new BurndV();
        // Prepare only: the node made for StepIndex (built in the constructor, with the logger).
        readonly PrepareOnlyNode node;

        bool warping;
        double warp_until = double.NaN;

        // The burn being flown.
        bool burning;
        RvStepKind burn_kind;
        double burn_planned_dv;
        Vector3d burn_r0, burn_v0;
        double burn_ut0;
        double mu;

        float next_retry;

        void ResetFlight()
        {
            warping = false;
            warp_until = double.NaN;
            burning = false;
            node.Clear();
            ResetDock();
        }

        // One state read: ours and the target's, around the same body, and the body's μ.
        bool ReadStates(out RendezvousStates s, out string error)
        {
            s = default;
            var target = RendezvousSnapshot.FindVessel(target_id);
            if (target == null)
            {
                error = "the target vessel no longer exists";
                return false;
            }
            if (!RendezvousSnapshot.TryRead(ActiveVessel(), target, out s, out error))
                return false;
            var body = ActiveVessel()?.Orbit?.referenceBody;
            mu = body?.gravParameter ?? 0;
            if (!(mu > 0))
            {
                error = "the body has no gravitational parameter";
                return false;
            }
            return true;
        }

        void Fly()
        {
            var outcome = CheckVessel("check");
            if (outcome == RvVesselOutcome.Docked)
            {
                FinishDocked("the active vessel holds both vessels' parts");
                return;
            }
            if (outcome == RvVesselOutcome.Wait)
            {
                Status = "Waiting for the vessel...";
                return;
            }
            if (outcome == RvVesselOutcome.Changed)
            {
                Save();
                isRunning = false;
                checked_vessel = null;
                Status = "The active vessel changed: rendezvous stopped. Switch back to it to resume.";
                return;
            }
            if (job.Running)
                return;
            if (Phase == RvPhase.Dock)
            {
                UpdateDock();
                return;
            }
            if (burning)
            {
                UpdateBurn();
                return;
            }
            if (node.Pending)
                return;
            if (Current == null)
                return; // the plan is being made
            if (!ReadStates(out var s, out string error))
            {
                if (Time.realtimeSinceStartup >= next_retry)
                {
                    next_retry = Time.realtimeSinceStartup + 5;
                    logger.LogInfo($"[Rendezvous] cannot read the vessels: {error}");
                }
                Status = "Waiting: " + error;
                return;
            }
            if (node.Waiting)
            {
                UpdateNode(s);
                return;
            }
            double distance = (s.TargetR - s.OurR).magnitude;
            var plan = Current;
            double lead = StepIndex < plan.Steps.Count ? TransferFlight.BurnLead(EstimateBurnSeconds(plan.Steps[StepIndex].DeltaVMagnitude)) : 0;
            var d = RendezvousFlight.Next(plan, StepIndex, s.UT, lead, distance);
            if (d.Action != RvAction.Done)
                SetPhase(d.Phase);
            if (d.Action != RvAction.Warp)
                StopWarp();

            switch (d.Action)
            {
                case RvAction.Done:
                    Arrive(s);
                    return;
                case RvAction.Warp:
                    Coast(d.WarpUntilUT, s.UT, warpAllowed: true);
                    return;
                case RvAction.Wait:
                    Coast(d.WarpUntilUT, s.UT, warpAllowed: false);
                    return;
                case RvAction.Missed:
                    Fail($"the {RendezvousFlight.StepName(plan.Steps[StepIndex].Kind)} was due {StrTool.DurationToString(s.UT - plan.Steps[StepIndex].UT)} ago");
                    return;
                default:
                    StartStep(s, distance);
                    return;
            }
        }

        void SetPhase(RvPhase phase)
        {
            if (phase == Phase)
                return;
            logger.LogInfo($"[Rendezvous] phase {Phase} -> {phase} (step {StepIndex})");
            Phase = phase;
            Save();
        }

        void StepDone()
        {
            node.ResetEngines();
            StepIndex++;
            node.StopWaiting();
            Save();
        }

        void Coast(double until, double now, bool warpAllowed)
        {
            var step = Current.Steps[StepIndex];
            string next = $"{RendezvousFlight.StepName(step.Kind)} in {StrTool.DurationToString(Math.Max(0, step.UT - now))}";
            if (PrepareOnly || !warpAllowed)
            {
                Status = PrepareOnly ? $"Next: {next} (warp yourself; the node comes when it is due)." : $"Next: {next} (no warp within 2 km of the target).";
                return;
            }
            if (!warping || Math.Abs(until - warp_until) > 1)
            {
                warp.Start_Ut(until);
                warping = true;
                warp_until = until;
                logger.LogInfo($"[Rendezvous] warp to UT {until:F0} (in {until - now:F0} s), next: {next}");
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
            // Our throttle is cut on our vessel by id, active or not: the game keeps the throttle
            // of a vessel switched away from. The warp is the active vessel's: leave another's alone.
            if (burning || active)
                OwnVesselStop.Apply(vessel_id, false);
            bool wasBurning = burning;
            burning = false;
            if (active && !PrepareOnly && (wasBurning || OursIsActive()))
                TimeWarpTools.SetRateIndex(0, true);
        }

        // ---------------------------------------------------------------- burns

        void StartStep(RendezvousStates s, double distance)
        {
            var plan = Current;
            var step = plan.Steps[StepIndex];
            double low = LowOrbitRadius();
            // Room to turn before the burn: the planned time, or the alignment lead from now.
            double burnUT = Math.Max(step.UT, s.UT + TransferFlight.BurnLead(0) - TransferFlight.BurnWarpMargin);
            KeplerPropagator.Propagate(s.OurR, s.OurV, mu, burnUT - s.UT, out var rb, out var vb);
            IBurnGuidance guidance;
            Vector3d dv;
            switch (step.Kind)
            {
                case RvStepKind.PlaneMatch:
                {
                    // At the relative node the plan found; recomputed from the actual orbit.
                    dv = RendezvousFlight.PlaneMatchDeltaV(rb, vb, Vector3d.Cross(s.TargetR, s.TargetV));
                    guidance = new FixedImpulseGuidance(dv);
                    break;
                }
                case RvStepKind.Phasing:
                case RvStepKind.Intercept:
                case RvStepKind.Correction:
                {
                    var sol = RendezvousFlight.SolveIntercept(rb, vb, burnUT, s.TargetR, s.TargetV, s.UT, plan.ArrivalUT, mu, standoff, low);
                    if (step.Kind == RvStepKind.Correction)
                    {
                        var verdict = RendezvousFlight.JudgeCorrection(sol, plan.ArrivalUT - burnUT, out string why);
                        logger.LogInfo($"[Rendezvous] correction: {verdict}, {why}" + (sol.Ok ? $", {sol.Revolutions} full revolution(s), brake {sol.BrakeDeltaV:F2} m/s" : ""));
                        if (verdict == CorrectionVerdict.Refuse)
                        {
                            Replan("correction: " + why);
                            return;
                        }
                        if (verdict == CorrectionVerdict.Skip)
                        {
                            StepDone();
                            return;
                        }
                    }
                    else if (!sol.Ok)
                    {
                        Fail($"the {RendezvousFlight.StepName(step.Kind)} has no solution from the actual orbit: Resume plans again");
                        return;
                    }
                    else
                        logger.LogInfo($"[Rendezvous] {RendezvousFlight.StepName(step.Kind)} solved from the actual state: {sol.DeltaV.magnitude:F2} m/s " +
                            $"(plan {step.DeltaVMagnitude:F2}), {sol.Revolutions} full revolution(s) (plan {plan.Revolutions}), brake {sol.BrakeDeltaV:F2} m/s");
                    dv = sol.DeltaV;
                    guidance = new FixedImpulseGuidance(dv);
                    break;
                }
                default:
                {
                    // The brake: closed loop on the target's velocity.
                    var match = new MatchVelocityGuidance(s.TargetR, s.TargetV, s.UT, mu);
                    dv = match.TargetVelocity(burnUT) - vb;
                    guidance = match;
                    break;
                }
            }
            if (!Finite(dv))
            {
                Fail($"the {RendezvousFlight.StepName(step.Kind)} cannot be computed from the actual orbit");
                return;
            }
            if (PrepareOnly)
            {
                MakeNode(burnUT, dv, rb, vb, s);
                return;
            }
            Ignite(step.Kind, guidance, burnUT, dv.magnitude, s, distance);
        }

        void Ignite(RvStepKind kind, IBurnGuidance guidance, double centreUT, double dv, RendezvousStates s, double distance)
        {
            string name = RendezvousFlight.StepName(kind);
            double seconds = EstimateBurnSeconds(dv);
            double start = double.IsNaN(seconds) ? centreUT : centreUT - seconds / 2;
            burn.stage_when_dry = true;                                  // the 4.1 staging guard
            // The warp before the burn (to its turn lead) only: never within 2 km. The burn itself
            // always stays at 1x (TargetOrbitBurn, 33d1aa9).
            burn.allow_warp = RendezvousFlight.CanWarp(distance);
            burn.Start(guidance, start);
            if (burn.failure_reason != null)
            {
                Fail($"{name}: {burn.failure_reason}");
                return;
            }
            burning = true;
            burn_kind = kind;
            burn_planned_dv = dv;
            burn_r0 = s.OurR;
            burn_v0 = s.OurV;
            burn_ut0 = s.UT;
            logger.LogInfo($"[Rendezvous] {name}: planned {dv:F2} m/s (plan {Current.Steps[StepIndex].DeltaVMagnitude:F2}), {guidance.Describe()}, " +
                $"centred at UT {centreUT:F0} (in {centreUT - s.UT:F0} s), burn ~{seconds:F0} s, {distance:F0} m from the target");
        }

        void UpdateBurn()
        {
            burn.Update();
            Status = $"{RendezvousFlight.StepName(burn_kind)}: {burn.status_line}";
            if (burn.failure_reason != null)
            {
                Fail($"{RendezvousFlight.StepName(burn_kind)}: {burn.failure_reason}");
                return;
            }
            if (!burn.finished)
                return;
            burning = false;
            string actual = "unknown";
            if (ReadStates(out var s, out _))
            {
                KeplerPropagator.Propagate(burn_r0, burn_v0, mu, s.UT - burn_ut0, out _, out var vCoast);
                actual = $"{(s.OurV - vCoast).magnitude:F2} m/s";
            }
            logger.LogInfo($"[Rendezvous] {RendezvousFlight.StepName(burn_kind)} done: planned {burn_planned_dv:F2} m/s, flown {actual}");
            LogAfterBurn(burn_kind);
            StepDone();
        }

        // After the intercept or the correction: the miss predicted at the arrival. After the brake:
        // the distance and the relative speed now.
        void LogAfterBurn(RvStepKind kind)
        {
            if (!ReadStates(out var s, out _))
                return;
            if (kind == RvStepKind.Brake)
            {
                RendezvousFlight.Relative(s.OurR, s.OurV, s.TargetR, s.TargetV, out double d, out double v, out _);
                logger.LogInfo($"[Rendezvous] after the brake: {d:F0} m from the target, relative speed {v:F2} m/s");
                return;
            }
            if (kind == RvStepKind.PlaneMatch)
            {
                RendezvousFlight.Relative(s.OurR, s.OurV, s.TargetR, s.TargetV, out _, out _, out double inc);
                logger.LogInfo($"[Rendezvous] after the plane match: relative inclination {inc:F3}°");
                return;
            }
            double arrival = Current.ArrivalUT;
            KeplerPropagator.Propagate(s.OurR, s.OurV, mu, arrival - s.UT, out var rEnd, out var vEnd);
            var aim = RendezvousFlight.AimPoint(s.TargetR, s.TargetV, s.UT, arrival, mu, standoff, out var rT, out var vT);
            logger.LogInfo($"[Rendezvous] after the {RendezvousFlight.StepName(kind)}: predicted at the arrival (UT {arrival:F0}) " +
                $"{(rEnd - rT).magnitude:F0} m from the target, {(rEnd - aim).magnitude:F0} m from the stand-off point, relative speed {(vEnd - vT).magnitude:F2} m/s");
        }

        // Every step flown: dock, finish, or plan again from the actual state.
        void Arrive(RendezvousStates s)
        {
            RendezvousFlight.Relative(s.OurR, s.OurV, s.TargetR, s.TargetV, out double d, out double v, out _);
            var outcome = RendezvousFlight.AfterRendezvous(d, v, replans, RendezvousFlight.Docks(Mode), out string why);
            logger.LogInfo($"[Rendezvous] arrival: {why} -> {outcome}");
            switch (outcome)
            {
                case ArrivalOutcome.Dock:
                    BeginDock();
                    return;
                case ArrivalOutcome.Done:
                    SetPhase(RvPhase.Done);
                    DeleteSaved(vessel_id);
                    isRunning = false;
                    checked_vessel = null;
                    Status = $"Rendezvous done: {why}.";
                    return;
                case ArrivalOutcome.Replan:
                    Replan("the arrival is off: " + why);
                    return;
                default:
                    Fail("the arrival is off after planning again: " + why);
                    return;
            }
        }

        // Plans again from the actual state, at most MaxReplans times in a run.
        void Replan(string why)
        {
            if (!RendezvousFlight.CanReplan(replans))
            {
                Fail(why + ", after planning again " + replans + " time(s)");
                return;
            }
            replans++;
            logger.LogInfo($"[Rendezvous] {why}: planning again from the actual state ({replans}/{RendezvousFlight.MaxReplans})");
            Current = null;
            StepIndex = 0;
            StartPlan();
        }

        // ---------------------------------------------------------------- Prepare only

        void MakeNode(double impulseUT, Vector3d dv, Vector3d r, Vector3d v, RendezvousStates s)
        {
            string name = RendezvousFlight.StepName(Current.Steps[StepIndex].Kind);
            TransferFlight.NodeComponents(dv, r, v, out double prograde, out double normal, out double radial);
            double seconds = EstimateBurnSeconds(dv.magnitude);
            double nodeUT = Math.Max(s.UT + ManeuverCreator.MinLead, double.IsNaN(seconds) ? impulseUT : impulseUT - seconds / 2);
            logger.LogInfo($"[Rendezvous] prepare only: node for the {name} at UT {nodeUT:F0}, {dv.magnitude:F2} m/s " +
                $"(prograde {prograde:F2}, normal {normal:F2}, radial {radial:F2})");
            node.Make(name, nodeUT, dv.magnitude, s.Body, s.OurR, s.OurV, s.UT, prograde, normal, radial,
                () => isRunning,
                () => Status = $"Fly the node for the {node.Name} ({node.DeltaV:F2} m/s), then remove it: the next step is computed from the orbit reached.",
                () => Fail($"the game did not take the node for the {node.Name}"));
        }

        void UpdateNode(RendezvousStates s)
        {
            KeplerPropagator.Propagate(node.R, node.V, mu, s.UT - node.StateUT, out _, out var expected);
            switch (node.Judge(false, expected, s.OurV, s.UT))
            {
                case NodeState.Waiting:
                    Status = $"Fly the node for the {node.Name} ({node.DeltaV:F2} m/s, " +
                        (node.UT > s.UT ? $"in {StrTool.DurationToString(node.UT - s.UT)}" : $"due {StrTool.DurationToString(s.UT - node.UT)} ago") + ").";
                    return;
                case NodeState.Flown:
                    logger.LogInfo($"[Rendezvous] prepare only: {node.Name} flown: planned {node.DeltaV:F2} m/s, measured {(s.OurV - expected).magnitude:F2} m/s");
                    LogAfterBurn(Current.Steps[StepIndex].Kind);
                    StepDone();
                    return;
                default:
                    logger.LogInfo($"[Rendezvous] prepare only: the node for the {node.Name} was removed without being flown");
                    node.StopWaiting();
                    Save();
                    isRunning = false;
                    checked_vessel = null;
                    Status = $"The node for the {node.Name} was removed without being flown. Resume plans again.";
                    return;
            }
        }

        // ---------------------------------------------------------------- the end, failures

        void Fail(string reason)
        {
            bool wasRunning = isRunning;
            StopBurn();
            StopWarp();
            CancelJob();
            logger.LogWarning($"[Rendezvous] failure in {Phase} at step {StepIndex}: {reason}");
            if (wasRunning)
            {
                Phase = RvPhase.Failed;
                Save();
            }
            isRunning = false;
            Status = $"Rendezvous stopped: {reason}.";
            checked_vessel = null;
        }

        double LowOrbitRadius()
        {
            var system = GameBodySystem.Build(GeneralTools.Current_UT, out _);
            var body = ActiveVessel()?.Orbit?.referenceBody;
            int i = body == null ? -1 : GameBodySystem.IndexOf(GameBodySystem.GameBodies(), body);
            return system != null && i >= 0 && i < system.Count ? system[i].LowOrbitRadius : 0;
        }

        double EstimateBurnSeconds(double dv) => PilotVessel.BurnSeconds(burn_dv, dv);

        static bool Finite(Vector3d v)
        {
            double sum = v.x + v.y + v.z;
            return !double.IsNaN(sum) && !double.IsInfinity(sum);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using K2D2.Node;
using K2D2.OrbitPlanning;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Rendezvous
{
    // Where the rendezvous pilot is. Plan: the search runs. PlaneMatch, Intercept, Correction, Brake:
    // that step falls due or is burning. PhasingWait: the coast after the intercept burn (Phasing or
    // Intercept, one burn labelled by its revolution count). Dock: the docking approach
    // (DockingGuidance's own phases inside). Done: rendezvous only, at the stand-off point.
    public enum RvPhase { Plan, PlaneMatch, Intercept, PhasingWait, Correction, Brake, Dock, Docked, Done, Failed }

    public enum RvMode { RendezvousAndDock, RendezvousOnly, DockOnly }

    public enum RvAction
    {
        Done,      // every step flown: check the arrival (AfterRendezvous)
        Warp,      // coast, warping until WarpUntilUT (a player's warp under Prepare only)
        Wait,      // coast at 1x: within NoWarpDistance of the target
        Burn,      // a burn with a vector computed now: plane match, intercept or brake
        Solve,     // the correction: solved now from the actual state
        Missed,    // the step's time has passed: Resume plans again
    }

    public struct RvDecision
    {
        public RvAction Action;
        public RvPhase Phase;
        public double WarpUntilUT;
    }

    public enum RvVesselOutcome { Same, Wait, TakeOver, Docked, Changed }

    public enum ArrivalOutcome { Done, Dock, Replan, Fail }

    public enum DockOutcome { Continue, Retry, Docked, Failed }

    // An intercept or a correction solved from an actual state to the planned stand-off point.
    public struct InterceptSolution
    {
        public bool Ok;
        public Vector3d DeltaV;        // the burn, inertial
        public double BrakeDeltaV;     // the brake it leaves at the arrival (m/s)
        public int Revolutions;
        public bool LongBranch;
        public double MissWithout;     // m from the aim point at the arrival without the burn
        public double Cost => DeltaV.magnitude + BrakeDeltaV;
    }

    // Closed loop on the target's velocity: the brake cancels the relative speed, whatever our
    // velocity does while it burns. The target moves on its Kepler orbit from (r0, v0) at ut0.
    public sealed class MatchVelocityGuidance : IBurnGuidance
    {
        readonly Vector3d rT, vT;
        readonly double utT, mu;

        public MatchVelocityGuidance(Vector3d targetR, Vector3d targetV, double targetUT, double mu)
        {
            rT = targetR;
            vT = targetV;
            utT = targetUT;
            this.mu = mu;
        }

        public Vector3d TargetVelocity(double ut)
        {
            KeplerPropagator.Propagate(rT, vT, mu, ut - utT, out _, out var v);
            return v;
        }

        public Vector3d DeltaVToGo(Vector3d r, Vector3d v, double ut, double muIgnored) => TargetVelocity(ut) - v;

        public string Describe() => "match the target's velocity";
    }

    // Pure decisions of the rendezvous pilot (RendezvousPilot is the game glue): the phase table,
    // when a step falls due and whether the coast may warp, the intercept and correction solves,
    // the arrival check, the vessel change at docking, the docking outcome, refusals, resume and
    // texts. Tested in EditMode (RendezvousFlightTests).
    public static class RendezvousFlight
    {
        // No warp closer to the target than this (m).
        public const double NoWarpDistance = 2000;
        // Dock only: allowed within this distance of the target (m).
        public const double DockOnlyRange = RendezvousPlanner.CloseRange;
        // A burn whose time has passed by more than this is missed (s).
        public const double MissedSeconds = PlanStore.MissedBurnSeconds;
        // The correction is skipped when its Δv is under this (m/s), or when the path without it
        // already passes this close to the aim point (m).
        public const double CorrectionSkipDeltaV = 0.05, CorrectionSkipMiss = 20;
        // The correction needs this long before the arrival (s), else it is skipped: the brake absorbs it.
        public const double CorrectionMinLead = 120;
        // The arrival is good when the target is within this (m) and the relative speed under this (m/s).
        public const double ArrivalMaxSpeed = 1;
        // Plans made again from the actual state when the arrival is off.
        public const int MaxReplans = 2;
        // The docking trace's period (s of game time) and the time the guidance may sit at contact
        // without the game latching the ports (s).
        public const double TraceSeconds = 2, LatchSeconds = 20;
        // The Δv kept for the docking approach in the per-stage check (m/s).
        public const double DockReserve = 10;
        // The port frames may be unreadable this long (s of real time) before the docking fails.
        public const double FrameWaitSeconds = 10;
        // A docking that fails closer than this (m), or after the guidance's last abort, first stops
        // the relative motion (hold) for up to SettleTimeout (s of game time), then stops.
        public const double SettleRange = 50, SettleTimeout = 60;

        // ------------------------------------------------------------ phases

        // Phasing and Intercept are the same single burn, labelled by its revolution count.
        public static RvPhase PhaseOf(RvStepKind kind)
        {
            switch (kind)
            {
                case RvStepKind.PlaneMatch: return RvPhase.PlaneMatch;
                case RvStepKind.Phasing:
                case RvStepKind.Intercept: return RvPhase.Intercept;
                case RvStepKind.Correction: return RvPhase.Correction;
                case RvStepKind.Brake: return RvPhase.Brake;
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        // Waiting for step i: the coast after the intercept burn is PhasingWait; before it, the
        // phase of the step waited for.
        public static RvPhase WaitPhase(RvPlan plan, int stepIndex)
        {
            for (int i = 0; i < stepIndex && i < plan.Steps.Count; i++)
                if (plan.Steps[i].Kind == RvStepKind.Phasing || plan.Steps[i].Kind == RvStepKind.Intercept)
                    return RvPhase.PhasingWait;
            return stepIndex < plan.Steps.Count ? PhaseOf(plan.Steps[stepIndex].Kind) : RvPhase.PhasingWait;
        }

        // The step table. lead: seconds before the step's UT at which its burn starts being set up
        // (TransferFlight.BurnLead). distance: to the target now (m).
        public static RvDecision Next(RvPlan plan, int stepIndex, double now, double lead, double distance)
        {
            if (plan == null || stepIndex >= plan.Steps.Count)
                return new RvDecision { Action = RvAction.Done, Phase = RvPhase.PhasingWait, WarpUntilUT = double.NaN };
            var step = plan.Steps[stepIndex];
            double due = step.UT - Math.Max(0, lead);
            if (now >= due)
            {
                // A correction is re-timed from now (JudgeCorrection skips it when too late).
                if (step.Kind != RvStepKind.Correction && now > step.UT + MissedSeconds)
                    return new RvDecision { Action = RvAction.Missed, Phase = PhaseOf(step.Kind), WarpUntilUT = double.NaN };
                return new RvDecision
                {
                    Action = step.Kind == RvStepKind.Correction ? RvAction.Solve : RvAction.Burn,
                    Phase = PhaseOf(step.Kind),
                    WarpUntilUT = double.NaN,
                };
            }
            return new RvDecision
            {
                Action = CanWarp(distance) ? RvAction.Warp : RvAction.Wait,
                Phase = WaitPhase(plan, stepIndex),
                WarpUntilUT = due,
            };
        }

        // Never warp within NoWarpDistance; an unknown distance does not warp either.
        public static bool CanWarp(double distance) => distance >= NoWarpDistance && !double.IsInfinity(distance);

        // ------------------------------------------------------------ burns

        // The plane match from the actual state at the node: the velocity turned about the radius so
        // that our orbit's normal becomes the target's (as RendezvousPlanner plans it).
        public static Vector3d PlaneMatchDeltaV(Vector3d r, Vector3d v, Vector3d targetNormal)
        {
            Vector3d hV = Vector3d.Cross(r, v).normalized, hT = targetNormal.normalized;
            Vector3d axis = r.normalized;
            double alpha = Math.Atan2(Vector3d.Dot(Vector3d.Cross(hV, hT), axis), Vector3d.Dot(hV, hT));
            return OrbitMath.Rotate(v, axis, alpha) - v;
        }

        // The stand-off point: 'standoff' metres short of the target on its orbit at 'ut'.
        public static Vector3d AimPoint(Vector3d rT, Vector3d vT, double utT, double ut, double mu, double standoff,
            out Vector3d targetR, out Vector3d targetV)
        {
            KeplerPropagator.Propagate(rT, vT, mu, ut - utT, out targetR, out targetV);
            return targetR - standoff * targetV.normalized;
        }

        // A fresh Lambert from the actual state (r, v) at ut to the stand-off point at arrivalUT. The
        // revolution count is not carried: every k from 0 to maxRevolutions (both branches) is
        // tried, and the cheapest intercept plus brake that really arrives is kept (the earlier k on
        // a tie). The arc must stay above 'low' (RendezvousPlanner.LowestRadius).
        public static InterceptSolution SolveIntercept(Vector3d r, Vector3d v, double ut, Vector3d rT, Vector3d vT, double utT,
            double arrivalUT, double mu, double standoff, double low, int maxRevolutions = RendezvousPlanner.RevolutionCap)
        {
            var best = new InterceptSolution { Ok = false, MissWithout = double.NaN };
            double tof = arrivalUT - ut;
            if (!(mu > 0) || !(tof > 0))
                return best;
            Vector3d aim = AimPoint(rT, vT, utT, arrivalUT, mu, standoff, out _, out var vTa);
            KeplerPropagator.Propagate(r, v, mu, tof, out var rCoast, out _);
            best.MissWithout = (rCoast - aim).magnitude;

            // Lambert's "prograde" is about +z: solve in the target's orbital frame.
            Vector3d e3 = Vector3d.Cross(rT, vT).normalized;
            Vector3d e1 = (r - Vector3d.Dot(r, e3) * e3).normalized;
            Vector3d e2 = Vector3d.Cross(e3, e1);
            Vector3d To(Vector3d x) => new Vector3d(Vector3d.Dot(x, e1), Vector3d.Dot(x, e2), Vector3d.Dot(x, e3));
            Vector3d From(Vector3d x) => x.x * e1 + x.y * e2 + x.z * e3;
            double tolerance = Math.Max(RendezvousPlanner.ArrivalToleranceFloor, RendezvousPlanner.ArrivalToleranceShare * aim.magnitude);

            for (int k = 0; k <= Math.Max(0, Math.Min(RendezvousPlanner.RevolutionCap, maxRevolutions)); k++)
                for (int b = 0; b < (k == 0 ? 1 : 2); b++)
                {
                    if (!Lambert.Solve(To(r), To(aim), tof, mu, true, k, b == 1, out var v1f, out _))
                        continue;
                    Vector3d v1 = From(v1f);
                    KeplerPropagator.Propagate(r, v1, mu, tof, out var rEnd, out var v2);
                    if (!((rEnd - aim).magnitude <= tolerance))
                        continue;
                    var el = OrbitMath.Elements(r, v1, mu);
                    if (!(RendezvousPlanner.LowestRadius(r, aim, el, k) >= low))
                        continue;
                    var s = new InterceptSolution
                    {
                        Ok = true,
                        DeltaV = v1 - v,
                        BrakeDeltaV = (vTa - v2).magnitude,
                        Revolutions = k,
                        LongBranch = b == 1,
                        MissWithout = best.MissWithout,
                    };
                    if (double.IsNaN(s.Cost))
                        continue;
                    if (!best.Ok || s.Cost < best.Cost - RendezvousPlanner.TieDeltaV)
                        best = s;
                }
            return best;
        }

        // Fly, skip or refuse the correction. why: for the log or the failure.
        public static CorrectionVerdict JudgeCorrection(InterceptSolution s, double secondsToArrival, out string why)
        {
            if (secondsToArrival < CorrectionMinLead)
            {
                why = Text($"{secondsToArrival:F0} s before the arrival, under {CorrectionMinLead:F0} s: the brake absorbs the error");
                return CorrectionVerdict.Skip;
            }
            if (!s.Ok)
            {
                why = "no intercept from the actual state reaches the stand-off point at the planned time";
                return CorrectionVerdict.Refuse;
            }
            if (s.DeltaV.magnitude < CorrectionSkipDeltaV || s.MissWithout < CorrectionSkipMiss)
            {
                why = Text($"{s.DeltaV.magnitude:F2} m/s, the path already passes {s.MissWithout:F0} m from the stand-off point");
                return CorrectionVerdict.Skip;
            }
            why = Text($"{s.DeltaV.magnitude:F2} m/s, the path would pass {s.MissWithout:F0} m from the stand-off point");
            return CorrectionVerdict.Fly;
        }

        // After the last step: within the close range and slow enough is a rendezvous (docking next
        // when asked); otherwise plan again from the actual state, at most MaxReplans times.
        public static ArrivalOutcome AfterRendezvous(double distance, double relativeSpeed, int replansDone, bool dock, out string why)
        {
            if (distance < RendezvousPlanner.CloseRange && relativeSpeed <= ArrivalMaxSpeed)
            {
                why = Text($"{distance:F0} m from the target at {relativeSpeed:F2} m/s");
                return dock ? ArrivalOutcome.Dock : ArrivalOutcome.Done;
            }
            why = Text($"{distance:F0} m from the target at {relativeSpeed:F2} m/s (wanted under {RendezvousPlanner.CloseRange:F0} m and {ArrivalMaxSpeed:F1} m/s)");
            return CanReplan(replansDone) ? ArrivalOutcome.Replan : ArrivalOutcome.Fail;
        }

        // An off arrival, or a correction with no solution, plans again from the actual state.
        public static bool CanReplan(int replansDone) => replansDone < MaxReplans;

        // ------------------------------------------------------------ docking

        // The merged vessel of a docking holds our port and at least one of the target's parts (part
        // ids survive the merge; the vessel id does not).
        public static bool IsDockedVessel(IEnumerable<string> partIds, string ourPortId, ICollection<string> targetPartIds)
        {
            if (partIds == null || string.IsNullOrEmpty(ourPortId) || targetPartIds == null || targetPartIds.Count == 0)
                return false;
            bool ours = false, theirs = false;
            foreach (string id in partIds)
            {
                if (id == null) continue;
                if (id == ourPortId) ours = true;
                else if (targetPartIds.Contains(id)) theirs = true;
                if (ours && theirs) return true;
            }
            return false;
        }

        // The active vessel is the docking's merged vessel: during the approach, another vessel id
        // than ours, holding our port and a part of the target.
        public static bool MergedByDocking(RvPhase phase, string activeId, string ourId, IEnumerable<string> activePartIds,
            string ourPortId, ICollection<string> targetPartIds) =>
            phase == RvPhase.Dock && activeId != null && activeId != ourId && IsDockedVessel(activePartIds, ourPortId, targetPartIds);

        // A vessel change while the pilot runs. During the docking approach, a merged vessel is the
        // docking (the game sends the vessel change before VesselDockedMessage). Otherwise the
        // transfer's rule (VesselWatch) applies.
        public static RvVesselOutcome OnVesselChange(RvPhase phase, bool mergedWithTarget, VesselVerdict watch)
        {
            if (phase == RvPhase.Dock && mergedWithTarget)
                return RvVesselOutcome.Docked;
            switch (watch)
            {
                case VesselVerdict.Same: return RvVesselOutcome.Same;
                case VesselVerdict.Wait: return RvVesselOutcome.Wait;
                case VesselVerdict.TakeOver: return RvVesselOutcome.TakeOver;
                default: return RvVesselOutcome.Changed;
            }
        }

        // One docking frame. The guidance's first abort backs off and retries (Retry, once, when its
        // abort count rises); AbortNeeded is the second one, or a refusal. The game's latch (its
        // message, or our port in the Docked state) ends it; the guidance at contact for longer than
        // LatchSeconds without a latch fails.
        public static DockOutcome JudgeDock(DockPhase phase, int aborts, int abortsBefore, bool abortNeeded, string abortReason,
            bool dockedMessage, bool portDocked, double secondsAtContact, string translateFailure, out string why)
        {
            if (dockedMessage || portDocked)
            {
                why = dockedMessage ? "the game reported the docking" : "our port reads Docked";
                return DockOutcome.Docked;
            }
            if (translateFailure != null)
            {
                why = "translation: " + translateFailure;
                return DockOutcome.Failed;
            }
            if (abortNeeded || phase == DockPhase.Aborted)
            {
                why = aborts > 1 ? $"second abort ({abortReason}), no retry left" : $"refused: {abortReason}";
                return DockOutcome.Failed;
            }
            if (phase == DockPhase.Docked && secondsAtContact > LatchSeconds)
            {
                why = Text($"at the port for {secondsAtContact:F0} s, but the ports did not latch");
                return DockOutcome.Failed;
            }
            if (aborts > abortsBefore)
            {
                why = $"abort ({abortReason}): backing off to retry once";
                return DockOutcome.Retry;
            }
            why = null;
            return DockOutcome.Continue;
        }

        // A docking failure: settle first (keep flying a hold, zero relative velocity) when the
        // translation still works and the vessel is close or the guidance has given up (Aborted).
        // Zeroing the inputs there would leave the vessel drifting into the target.
        public static bool SettleBeforeFailing(double distance, bool aborted, bool canTranslate) =>
            canTranslate && (aborted || (!double.IsNaN(distance) && distance < SettleRange));

        // The settle ends when the relative speed is under holdSpeed, or after SettleTimeout.
        public static bool SettleOver(double relativeSpeed, double holdSpeed, double secondsSettling, out string why)
        {
            if (relativeSpeed < holdSpeed)
            {
                why = Text($"held at {relativeSpeed:F3} m/s relative");
                return true;
            }
            if (secondsSettling > SettleTimeout)
            {
                why = Text($"still at {relativeSpeed:F3} m/s relative after {SettleTimeout:F0} s of hold");
                return true;
            }
            why = null;
            return false;
        }

        // ------------------------------------------------------------ start, resume

        // Null when the start is allowed. distance: to the target (m), NaN when unread.
        public static string StartRefusal(RvMode mode, bool hasTarget, bool targetIsOurs, string readError, double distance)
        {
            if (!hasTarget) return "Choose a target vessel.";
            if (targetIsOurs) return "The target is our own vessel.";
            if (readError != null) return readError;
            if (mode == RvMode.DockOnly && !(distance <= DockOnlyRange))
                return Text($"Dock only works within {DockOnlyRange / 1000:F0} km of the target ({distance / 1000:F1} km now): use Rendezvous + dock.");
            return null;
        }

        public static bool Docks(RvMode mode) => mode != RvMode.RendezvousOnly;

        // A resume always plans again. A run saved in the docking phase docks again (within range),
        // a finished one has nothing to resume.
        public static RvMode ResumeMode(RvMode saved, RvPhase savedPhase, double distance)
        {
            if (!Docks(saved))
                return RvMode.RendezvousOnly;
            return savedPhase == RvPhase.Dock && distance <= DockOnlyRange ? RvMode.DockOnly : saved;
        }

        public static bool CanResume(RvPhase savedPhase) => savedPhase != RvPhase.Docked && savedPhase != RvPhase.Done;

        public static bool TryParse<T>(string s, out T value) where T : struct =>
            Enum.TryParse(s, false, out value) && Enum.IsDefined(typeof(T), value);

        public static SavedPlan ToSaved(string vesselId, string bodyName, string targetId, string targetName, RvMode mode,
            RvPhase phase, double ut, bool prepareOnly, string ourPortId, string targetPortId) => new SavedPlan
        {
            Version = PlanStore.CurrentVersion,
            VesselId = vesselId,
            Kind = SavedPlan.KindRendezvous,
            Plan = null,
            Phase = phase == RvPhase.Failed ? TransferPhase.Failed : phase == RvPhase.Done || phase == RvPhase.Docked ? TransferPhase.Done : TransferPhase.Planned,
            StepIndex = 0,
            SavedUT = ut,
            PrepareOnly = prepareOnly,
            FromBodyName = bodyName ?? "",
            ToBodyName = bodyName ?? "",
            Rendezvous = new SavedRendezvous
            {
                TargetId = targetId,
                TargetName = targetName,
                Mode = mode.ToString(),
                Phase = phase.ToString(),
                OurPortId = ourPortId,
                TargetPortId = targetPortId,
            },
        };

        // ------------------------------------------------------------ budget, readouts, texts

        // The per-stage check (4.1's rule) of the steps from 'from' on, with DockReserve when docking.
        public static BudgetResult Budget(RvPlan plan, int from, IReadOnlyList<StageDv> stages, bool dock)
        {
            var dvs = new List<double>();
            if (plan != null)
                for (int i = Math.Max(0, from); i < plan.Steps.Count; i++)
                    dvs.Add(plan.Steps[i].DeltaVMagnitude);
            return StageBudget.Check(stages ?? new List<StageDv>(), dvs, null, dock ? DockReserve : 0);
        }

        // Distance (m), relative speed (m/s) and relative inclination (°) of the target.
        public static void Relative(Vector3d r, Vector3d v, Vector3d rT, Vector3d vT, out double distance, out double speed, out double inclinationDeg)
        {
            distance = (rT - r).magnitude;
            speed = (vT - v).magnitude;
            Vector3d h = Vector3d.Cross(r, v), hT = Vector3d.Cross(rT, vT);
            inclinationDeg = Math.Atan2(Vector3d.Cross(h, hT).magnitude, Vector3d.Dot(h, hT)) * 180 / Math.PI;
        }

        public static string StepName(RvStepKind kind)
        {
            switch (kind)
            {
                case RvStepKind.PlaneMatch: return "plane match";
                case RvStepKind.Phasing: return "phasing burn";
                case RvStepKind.Intercept: return "intercept burn";
                case RvStepKind.Correction: return "correction";
                default: return "brake";
            }
        }

        // The plan, for the log.
        public static string Describe(RvPlan plan, string target, double now, double searchMs)
        {
            if (plan == null) return "[Rendezvous] no plan";
            var b = new StringBuilder();
            b.Append(Text($"[Rendezvous] plan to {target} in {searchMs:F0} ms: "));
            if (!plan.Ok)
                return b.Append("refused: ").Append(plan.Refusal).ToString();
            b.Append(Text($"{plan.TotalDeltaV:F1} m/s, {plan.Revolutions} full revolution(s), arrival in {plan.ArrivalUT - now:F0} s (UT {plan.ArrivalUT:F0})"));
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var s = plan.Steps[i];
                b.Append(Text($"; {i}: {s.Kind} at UT {s.UT:F0} (in {s.UT - now:F0} s), {s.DeltaVMagnitude:F2} m/s - {s.Explanation}"));
            }
            return b.ToString();
        }

        static string Text(FormattableString f) => f.ToString(CultureInfo.InvariantCulture);
    }
}

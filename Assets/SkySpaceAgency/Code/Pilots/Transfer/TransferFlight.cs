using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using K2D2.Node;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Transfer
{
    // What the transfer pilot does next for the step it is at.
    public enum FlightAction
    {
        Done,        // every step flown (and, for an encounter, the target's SOI reached)
        StepPassed,  // a milestone (SOI exit) already behind: go on to the next step
        Warp,        // wait, warping (unless Prepare only) until WarpUntilUT
        WaitSoi,     // wait at 1x for an SOI change before the step can be flown
        Burn,        // a burn with a planned vector (plane change, orbit change, injection, re-aim)
        Solve,       // a correction: solve it now from the actual state
        Capture,     // the capture burn at the arrival periapsis
        SoiMissed,   // an SOI change the plan needs did not come in time (MissedBody, MissedExit)
    }

    public struct FlightDecision
    {
        public FlightAction Action;
        public TransferPhase Phase;
        public double WarpUntilUT; // Warp only
        public int MissedBody;     // SoiMissed: the body the vessel should have entered or left
        public bool MissedExit;    // SoiMissed: an exit of MissedBody (else an entry)
    }

    // How a leg after the first is re-planned when the vessel reaches it (PlanStep.ReplanAtLegStart).
    public enum LegReplan
    {
        None,
        Search,           // a closed orbit in the leg's body: the transfer search from the actual state
        SolveCorrection,  // a direct descent: its correction is solved without the stale planned arrival
        AimExcess,        // the top climb level, straight through: aim at the plan's required excess
        PlannedImpulse,   // a lower climb level, straight through: the planned re-aim vector, with a warning
    }

    public enum CorrectionVerdict { Fly, Skip, Refuse }

    public enum ResumeAction { Continue, Replan, Refuse }

    public enum NodeState { Waiting, Flown, RemovedUnflown }

    // Pure decisions of the transfer pilot (TransferPilot is the game glue): the phase table, when a
    // step falls due, the correction skip rule, the leg re-plan trigger, the plan update after a
    // correction picks its pass, the resume decision, and the texts for the player and the log.
    // Tested in EditMode.
    public static class TransferFlight
    {
        // Added to BurnWarp's alignment lead: the warp stops this much earlier before every burn.
        public const double BurnWarpMargin = 30;
        // The warp stops this long before every SOI change (s).
        public const double SoiWarpMargin = 60;
        // A correction under ArrivalTargeting.SkipThreshold is skipped only when the path without it
        // already meets the wanted periapsis within this (m): several tiny skipped corrections once
        // added up to an impact.
        public const double SkipPeriapsisTolerance = 1000;
        // A correction that did not converge is refused beyond this miss of the wanted periapsis:
        // the larger of the two (m, share of the wanted radius).
        public const double UnconvergedMissFloor = 10_000, UnconvergedMissShare = 0.10;
        // Corrections re-timed after a re-plan keep this far from now and from each other (s).
        public const double CorrectionSpacing = 60;
        // A burn longer than this share of the orbit period gets a warning.
        public const double LongBurnShare = 0.10;
        // Prepare only: a velocity off the coast by more than this (m/s) means the node was flown.
        public const double NodeFlownSpeed = 0.5;
        // The alignment the warp stop counts on before a burn: a half turn at BurnWarp's default rate.
        public const double AssumedTurnDeg = 180;
        // An SOI change the plan needs may come this late after its planned time, when none is
        // predicted: the larger of an hour and 2 % of the time since the departure. Past it the
        // pilot predicts again, then gives up.
        public const double SoiLateFloor = 3600, SoiLateShare = 0.02;

        public static double SoiLateSeconds(TransferPlan plan, double plannedUT)
        {
            double flown = double.IsNaN(plan.DepartureUT) || double.IsNaN(plannedUT) ? 0 : Math.Max(0, plannedUT - plan.DepartureUT);
            return Math.Max(SoiLateFloor, SoiLateShare * flown);
        }

        // The phase that flies a step.
        public static TransferPhase BurnPhase(StepKind kind)
        {
            switch (kind)
            {
                case StepKind.PlaneChange: return TransferPhase.PlaneChange;
                case StepKind.OrbitChange: return TransferPhase.OrbitChange;
                case StepKind.Injection: return TransferPhase.Injection;
                case StepKind.Escape: return TransferPhase.Escape;
                case StepKind.Correction: return TransferPhase.Correction;
                case StepKind.Capture: return TransferPhase.Capture;
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        // Waiting for step i: for the window until the first burn, coasting after it.
        public static TransferPhase WaitPhase(TransferPlan plan, int stepIndex)
        {
            for (int i = 0; i < stepIndex && i < plan.Steps.Count; i++)
                if (IsBurn(plan.Steps[i]))
                    return TransferPhase.Coast;
            return TransferPhase.WaitWindow;
        }

        public static bool IsBurn(PlanStep step) => step.Kind != StepKind.Escape;

        // Seconds before a burn's centre at which the warp must have stopped: BurnWarp's alignment
        // lead for a half turn, the margin, and half the burn (the burn is centred on its time).
        public static double BurnLead(double burnSeconds) =>
            BurnWarp.AlignmentLead(AssumedTurnDeg, BurnWarp.DefaultTurnRate) + BurnWarpMargin
            + (burnSeconds > 0 && !double.IsInfinity(burnSeconds) ? burnSeconds / 2 : 0);

        // The step table. currentBody: the body the vessel orbits now. lead: BurnLead for this step's
        // burn. nextSoiUT: the next SOI change predicted on the current path (NaN: none before the
        // horizon). finalBody: the plan's target.
        public static FlightDecision Next(TransferPlan plan, int stepIndex, int currentBody, int finalBody, double now,
            double lead, double nextSoiUT)
        {
            bool soiAhead = !double.IsNaN(nextSoiUT) && !double.IsInfinity(nextSoiUT);
            if (stepIndex >= plan.Steps.Count)
            {
                // An encounter ends in the target's SOI; a capture ends with its burn.
                if (currentBody == finalBody || plan.Steps.Any(st => st.Kind == StepKind.Capture))
                    return new FlightDecision { Action = FlightAction.Done, Phase = TransferPhase.Done };
                if (soiAhead)
                    return Coast(TransferPhase.Coast, now, nextSoiUT - SoiWarpMargin, true, nextSoiUT);
                return Overdue(plan, TransferPhase.Coast, now, plan.ArrivalUT, finalBody, exit: false);
            }

            var step = plan.Steps[stepIndex];
            if (step.Kind == StepKind.Escape)
            {
                if (currentBody != step.Body)
                    return new FlightDecision { Action = FlightAction.StepPassed, Phase = TransferPhase.Escape };
                if (soiAhead)
                    return Coast(TransferPhase.Escape, now, nextSoiUT - SoiWarpMargin, true, nextSoiUT);
                return Overdue(plan, TransferPhase.Escape, now, step.UT, step.Body, exit: true);
            }

            var waitPhase = WaitPhase(plan, stepIndex);
            if (currentBody != step.Body)
            {
                // The step is flown in another SOI: coast to the change.
                if (soiAhead)
                    return Coast(waitPhase, now, nextSoiUT - SoiWarpMargin, true, nextSoiUT);
                return Overdue(plan, waitPhase, now, step.UT, step.Body, exit: false);
            }

            double warpEnd = step.UT - lead;
            if (soiAhead && nextSoiUT < step.UT && now < warpEnd)
            {
                // An SOI change comes first (an unplanned one: the step's body is the current one).
                if (now < nextSoiUT - SoiWarpMargin)
                    return new FlightDecision { Action = FlightAction.Warp, Phase = waitPhase, WarpUntilUT = Math.Min(warpEnd, nextSoiUT - SoiWarpMargin) };
                return new FlightDecision { Action = FlightAction.WaitSoi, Phase = waitPhase, WarpUntilUT = nextSoiUT };
            }
            if (now < warpEnd)
                return new FlightDecision { Action = FlightAction.Warp, Phase = waitPhase, WarpUntilUT = warpEnd };

            FlightAction action = step.Kind == StepKind.Capture ? FlightAction.Capture
                : step.Kind == StepKind.Correction && !(step.DeltaV.sqrMagnitude > 0) ? FlightAction.Solve
                : FlightAction.Burn;
            return new FlightDecision { Action = action, Phase = BurnPhase(step.Kind) };
        }

        // No SOI change predicted, but the plan needs one by plannedUT: warp to plannedUT plus the late
        // margin (the pilot predicts again there), then the change is missed.
        static FlightDecision Overdue(TransferPlan plan, TransferPhase phase, double now, double plannedUT, int body, bool exit)
        {
            double limit = (double.IsNaN(plannedUT) ? now : plannedUT) + SoiLateSeconds(plan, plannedUT);
            if (now < limit)
                return new FlightDecision { Action = FlightAction.Warp, Phase = phase, WarpUntilUT = limit };
            return new FlightDecision { Action = FlightAction.SoiMissed, Phase = phase, MissedBody = body, MissedExit = exit, WarpUntilUT = limit };
        }

        // Warp until 'until'; past it, wait at 1x for the SOI change when one is ahead.
        static FlightDecision Coast(TransferPhase phase, double now, double until, bool soiAhead, double soiUT)
        {
            if (now < until)
                return new FlightDecision { Action = FlightAction.Warp, Phase = phase, WarpUntilUT = until };
            return new FlightDecision { Action = FlightAction.WaitSoi, Phase = phase, WarpUntilUT = soiAhead ? soiUT : double.NaN };
        }

        // ---------------------------------------------------------------- legs

        // Whether step i starts a leg that must be re-planned now, and how. orbitClosed: the vessel's
        // orbit is closed inside its body's SOI.
        public static LegReplan LegStart(TransferPlan plan, int stepIndex, int currentBody, bool orbitClosed)
        {
            if (stepIndex < 0 || stepIndex >= plan.Steps.Count)
                return LegReplan.None;
            var step = plan.Steps[stepIndex];
            if (!step.ReplanAtLegStart || currentBody != step.Body)
                return LegReplan.None;
            if (orbitClosed)
                return LegReplan.Search;
            if (step.Kind == StepKind.Correction && !(step.DeltaV.sqrMagnitude > 0))
                return LegReplan.SolveCorrection;
            if (plan.ExcessBody == step.Body && plan.RequiredExcess.sqrMagnitude > 0)
                return LegReplan.AimExcess;
            return LegReplan.PlannedImpulse;
        }

        // The leg of step i is being flown: its steps no longer wait for a re-plan.
        public static void MarkLegStarted(TransferPlan plan, int stepIndex)
        {
            int leg = plan.Steps[stepIndex].LegIndex;
            foreach (var st in plan.Steps)
                if (st.LegIndex == leg)
                    st.ReplanAtLegStart = false;
        }

        // Replaces the steps from stepIndex on with a plan searched from the actual state at the start
        // of that leg. The new plan's legs follow the current leg's number.
        public static void MergeLeg(TransferPlan plan, int stepIndex, TransferPlan replanned)
        {
            int leg = plan.Steps[stepIndex].LegIndex;
            plan.Steps.RemoveRange(stepIndex, plan.Steps.Count - stepIndex);
            foreach (var st in replanned.Steps)
            {
                st.LegIndex += leg;
                plan.Steps.Add(st);
            }
            plan.ArrivalUT = replanned.ArrivalUT;
            plan.ExcessBody = replanned.ExcessBody;
            plan.RequiredExcess = replanned.RequiredExcess;
            foreach (string w in replanned.Warnings)
                if (!plan.Warnings.Contains(w))
                    plan.Warnings.Add(w);
        }

        // The planned arrival of step i's leg: the first step of the next leg, else the plan's arrival.
        public static double LegArrivalUT(TransferPlan plan, int stepIndex)
        {
            int leg = plan.Steps[stepIndex].LegIndex;
            for (int i = stepIndex + 1; i < plan.Steps.Count; i++)
                if (plan.Steps[i].LegIndex > leg)
                    return plan.Steps[i].UT;
            return plan.ArrivalUT;
        }

        // The capture of step i's leg at its target (none for a body only flown past).
        public static int LegCapture(TransferPlan plan, int stepIndex)
        {
            var step = plan.Steps[stepIndex];
            for (int i = stepIndex + 1; i < plan.Steps.Count; i++)
            {
                var st = plan.Steps[i];
                if (st.LegIndex != step.LegIndex)
                    break;
                if (st.Kind == StepKind.Capture && st.TargetBody == step.TargetBody)
                    return i;
            }
            return -1;
        }

        // The apoapsis of the capture that ends step i's leg: NaN for a pass-through (encounter).
        public static double CaptureApoapsis(TransferPlan plan, int stepIndex)
        {
            int c = LegCapture(plan, stepIndex);
            if (c < 0)
                return double.NaN;
            var cap = plan.Steps[c];
            return double.IsNaN(cap.TargetApoapsisRadius) ? cap.TargetPeriapsisRadius : cap.TargetApoapsisRadius;
        }

        // ---------------------------------------------------------------- corrections

        // wanted: the leg's wanted periapsis radius (NaN: free). predictedWithout: the arrival
        // periapsis with no correction. floorRadius: the top of the target's atmosphere or terrain.
        public static CorrectionVerdict JudgeCorrection(double dv, bool converged, double predictedPe, double wanted,
            double predictedWithout, double floorRadius, out string reason)
        {
            reason = null;
            if (double.IsNaN(wanted))
                return dv < ArrivalTargeting.SkipThreshold ? CorrectionVerdict.Skip : CorrectionVerdict.Fly;
            if (!converged)
            {
                if (!(predictedPe >= floorRadius))
                {
                    reason = Inv($"the best correction found ({dv:F1} m/s) would bring the periapsis to {predictedPe / 1000:F1} km (radius), below the atmosphere or terrain at {floorRadius / 1000:F1} km");
                    return CorrectionVerdict.Refuse;
                }
                double limit = Math.Max(UnconvergedMissFloor, UnconvergedMissShare * wanted);
                if (Math.Abs(predictedPe - wanted) > limit)
                {
                    reason = Inv($"the best correction found ({dv:F1} m/s) misses the wanted periapsis by {Math.Abs(predictedPe - wanted) / 1000:F1} km (more than {limit / 1000:F1} km)");
                    return CorrectionVerdict.Refuse;
                }
            }
            if (dv < ArrivalTargeting.SkipThreshold && !double.IsNaN(predictedWithout)
                && Math.Abs(predictedWithout - wanted) <= SkipPeriapsisTolerance)
                return CorrectionVerdict.Skip;
            return CorrectionVerdict.Fly;
        }

        // The arrival periapsis radius at 'target' on the path from (r, v) at ut, with no burn: NaN
        // when the path does not enter its SOI before horizonUT.
        public static double PeriapsisWithout(BodySystem s, int body, Vector3d r, Vector3d v, double ut, int target, double horizonUT)
        {
            if (!(horizonUT > ut))
                return double.NaN;
            var patches = PatchedPropagator.Propagate(s, body, r, v, ut, horizonUT, null, 8);
            var arrival = patches.FirstOrDefault(p => p.Body == target);
            return arrival == null ? double.NaN : arrival.PeriapsisRadius;
        }

        // After the correction of step i chose its pass (ArrivalTargeting's cheapest overload): the
        // capture of the leg goes to the aimed periapsis, with the predicted cost; when the arrival
        // moved, the later legs move with it and the leg's corrections still to come are re-timed
        // (TransferSearch.CorrectionTimes). Returns the arrival shift (s).
        public static double ApplySolve(TransferPlan plan, int stepIndex, double predictedArrivalUT, double predictedCaptureDeltaV, double now)
        {
            if (double.IsNaN(predictedArrivalUT) || double.IsInfinity(predictedArrivalUT))
                return 0;
            var step = plan.Steps[stepIndex];
            int leg = step.LegIndex;
            double planned = LegArrivalUT(plan, stepIndex);
            double shift = double.IsNaN(planned) ? 0 : predictedArrivalUT - planned;
            bool lastLeg = !plan.Steps.Any(st => st.LegIndex > leg);

            int capture = LegCapture(plan, stepIndex);
            if (capture >= 0)
            {
                plan.Steps[capture].UT = predictedArrivalUT;
                if (!double.IsNaN(predictedCaptureDeltaV))
                    plan.Steps[capture].DeltaVMagnitude = predictedCaptureDeltaV;
            }
            if (shift != 0)
                foreach (var st in plan.Steps)
                    if (st.LegIndex > leg)
                        st.UT += shift;
            if (lastLeg)
                plan.ArrivalUT = predictedArrivalUT;
            else if (!double.IsNaN(plan.ArrivalUT))
                plan.ArrivalUT += shift;

            if (Math.Abs(shift) > 1)
                RetimeCorrections(plan, stepIndex, predictedArrivalUT, now);
            SortFrom(plan, stepIndex + 1);
            return shift;
        }

        // The corrections of step i's leg after it, re-timed to a new arrival.
        static void RetimeCorrections(TransferPlan plan, int stepIndex, double arrivalUT, double now)
        {
            var step = plan.Steps[stepIndex];
            var legSteps = plan.Steps.Where(st => st.LegIndex == step.LegIndex).ToList();
            var corrections = legSteps.Where(st => st.Kind == StepKind.Correction && !(st.DeltaV.sqrMagnitude > 0)).ToList();
            int here = corrections.IndexOf(step);
            if (here < 0)
                return;
            double exit = legSteps.Where(st => st.Kind == StepKind.Escape && st.UT <= corrections[0].UT).Select(st => st.UT).DefaultIfEmpty(double.NaN).Max();
            double departure = legSteps.Where(st => IsBurn(st) && st.Kind != StepKind.Correction && st.Kind != StepKind.Capture)
                .Select(st => st.UT).DefaultIfEmpty(double.NaN).Min();
            if (double.IsNaN(exit))
                exit = corrections[0].UT;
            if (double.IsNaN(departure))
                departure = exit;
            var times = corrections.Count == 3 ? TransferSearch.CorrectionTimes(exit, departure, arrivalUT, arrivalUT) : null;
            double earliest = Math.Max(now, step.UT) + CorrectionSpacing;
            double latest = arrivalUT - CorrectionSpacing;
            for (int k = here + 1; k < corrections.Count; k++)
            {
                double t = times != null ? times[k] : corrections[k].UT;
                t = Math.Min(Math.Max(t, earliest), latest);
                if (t < earliest)
                {
                    // No room left before the arrival: the correction is dropped.
                    plan.Steps.Remove(corrections[k]);
                    continue;
                }
                corrections[k].UT = t;
                earliest = t + CorrectionSpacing;
            }
        }

        static void SortFrom(TransferPlan plan, int from)
        {
            if (from >= plan.Steps.Count)
                return;
            var tail = plan.Steps.Skip(from).OrderBy(st => st.UT).ToList(); // stable
            plan.Steps.RemoveRange(from, plan.Steps.Count - from);
            plan.Steps.AddRange(tail);
        }

        // ---------------------------------------------------------------- burns

        // What a planned burn steers along, from the state the vessel will have at the burn's time
        // (read from a fresh snapshot, so the vectors are in the frame the burn is flown in):
        //  - a capture: the capture orbit (apsis radii of the step, plane of the arrival);
        //  - a burn whose planned vector leaves a closed orbit inside the SOI: that orbit;
        //  - one that leaves on a hyperbola: its asymptote (an injection's excess velocity);
        //  - otherwise the planned vector as a fixed impulse.
        // Step i of the plan: as below, except the re-aim burn on entering the top climb level, which
        // aims at the plan's required excess (the excess the transfer at the common ancestor needs).
        public static IBurnGuidance GuidanceFor(TransferPlan plan, int i, Vector3d r, Vector3d v, double mu, double soi)
        {
            var step = plan.Steps[i];
            if (step.Kind == StepKind.Correction && step.DeltaV.sqrMagnitude > 0 && step.Body == plan.ExcessBody
                && plan.RequiredExcess.sqrMagnitude > 0)
                return new AsymptoteGuidance(plan.RequiredExcess);
            return GuidanceFor(step, r, v, mu, soi);
        }

        public static IBurnGuidance GuidanceFor(PlanStep step, Vector3d r, Vector3d v, double mu, double soi)
        {
            if (step.Kind == StepKind.Capture)
            {
                double pe = step.TargetPeriapsisRadius;
                double ap = double.IsNaN(step.TargetApoapsisRadius) ? pe : step.TargetApoapsisRadius;
                return new OrbitTargetGuidance(new TargetOrbit
                {
                    Rp = Math.Min(pe, ap),
                    Ra = Math.Max(pe, ap),
                    PlaneNormal = Vector3d.Cross(r, v).normalized,
                });
            }
            var after = v + step.DeltaV;
            var el = OrbitMath.Elements(r, after, mu);
            if (el.IsClosed && el.ApoapsisRadius < soi)
                return new OrbitTargetGuidance(TargetOrbit.FromState(r, after, mu));
            if (after.sqrMagnitude / 2 - mu / r.magnitude > 0)
            {
                var vInf = Hyperbola.Asymptote(r, after, mu);
                if (Hyperbola.IsFinite(vInf) && vInf.sqrMagnitude > 0)
                    return new AsymptoteGuidance(vInf);
            }
            return new FixedImpulseGuidance(step.DeltaV);
        }

        public static bool IsLongBurn(double burnSeconds, double orbitPeriod) =>
            burnSeconds > 0 && orbitPeriod > 0 && !double.IsInfinity(orbitPeriod) && burnSeconds > LongBurnShare * orbitPeriod;

        // A Δv (Zup, at state r, v) as the game's node components: prograde, normal (r × v), radial out.
        public static void NodeComponents(Vector3d dv, Vector3d r, Vector3d v, out double prograde, out double normal, out double radial)
        {
            Vector3d p = v.normalized;
            Vector3d n = Vector3d.Cross(r, v).normalized;
            Vector3d rad = Vector3d.Cross(p, n);
            prograde = Vector3d.Dot(dv, p);
            normal = Vector3d.Dot(dv, n);
            radial = Vector3d.Dot(dv, rad);
        }

        // Prepare only: has the player flown the node? Flown once the node is gone and the vessel's
        // velocity left the coast it was on when the node was made (or it changed SOI).
        // Also flown when the node is still there but its time has passed and the velocity moved (the
        // player flew it by hand and left the node).
        public static NodeState NodeFlown(bool nodeOnPlan, bool nodeTimePassed, bool bodyChanged, Vector3d expectedVelocity, Vector3d velocity)
        {
            bool moved = bodyChanged || (velocity - expectedVelocity).magnitude > NodeFlownSpeed;
            if (nodeOnPlan)
                return nodeTimePassed && moved ? NodeState.Flown : NodeState.Waiting;
            return moved ? NodeState.Flown : NodeState.RemovedUnflown;
        }

        // The capture holds: a closed orbit inside the body's SOI.
        public static bool InOrbit(Vector3d r, Vector3d v, double mu, double soi)
        {
            var el = OrbitMath.Elements(r, v, mu);
            return el.IsClosed && el.ApoapsisRadius < soi;
        }

        // ---------------------------------------------------------------- resume

        // A burn flown from its planned vector: re-flying it after a cut would add the whole vector again.
        public static bool HasPlannedVector(PlanStep step) =>
            step.Kind == StepKind.PlaneChange || step.Kind == StepKind.OrbitChange || step.Kind == StepKind.Injection
            || (step.Kind == StepKind.Correction && step.DeltaV.sqrMagnitude > 0);

        // check: PlanStore.Check on the saved plan. A plan saved in the middle of a burn whose vector
        // was planned (plane change, orbit change, injection) is searched again: re-flying the whole
        // planned vector from the state reached would replay part of a burn.
        public static ResumeAction DecideResume(SavedPlan saved, ResumeCheck check)
        {
            if (saved?.Plan == null || check == null || check.Verdict == ResumeVerdict.Mismatch)
                return ResumeAction.Refuse;
            if (check.Verdict == ResumeVerdict.Replan)
                return ResumeAction.Replan;
            if (saved.StepIndex < saved.Plan.Steps.Count)
            {
                var step = saved.Plan.Steps[saved.StepIndex];
                // BurnStarted survives a failure (the phase is then Failed); the phase test also covers
                // a stop saved while the burn was under way.
                if (HasPlannedVector(step) && (saved.BurnStarted || saved.Phase == BurnPhase(step.Kind)))
                    return ResumeAction.Replan;
            }
            return ResumeAction.Continue;
        }

        // The saved plan's body indices, mapped by name onto s. False (nothing changed) when a name
        // is missing: the plan is then searched again.
        public static bool RemapBodies(SavedPlan saved, BodySystem s)
        {
            if (saved?.Plan == null || s == null || saved.BodyNames == null)
                return false;
            var byName = new Dictionary<string, int>();
            for (int i = 0; i < s.Count; i++)
                byName[s[i].Name] = i;
            var map = new int[saved.BodyNames.Count];
            for (int i = 0; i < map.Length; i++)
                if (saved.BodyNames[i] == null || !byName.TryGetValue(saved.BodyNames[i], out map[i]))
                    return false;
            bool Ok(int index) => index >= 0 && index < map.Length;
            var plan = saved.Plan;
            foreach (var st in plan.Steps)
                if (!Ok(st.Body) || (st.TargetBody >= 0 && !Ok(st.TargetBody)))
                    return false;
            if (plan.Request != null && (!Ok(plan.Request.FromBody) || !Ok(plan.Request.ToBody)))
                return false;
            if (plan.ExcessBody >= 0 && !Ok(plan.ExcessBody))
                return false;

            foreach (var st in plan.Steps)
            {
                st.Body = map[st.Body];
                if (st.TargetBody >= 0)
                    st.TargetBody = map[st.TargetBody];
            }
            if (plan.Request != null)
            {
                plan.Request.FromBody = map[plan.Request.FromBody];
                plan.Request.ToBody = map[plan.Request.ToBody];
            }
            if (plan.ExcessBody >= 0)
                plan.ExcessBody = map[plan.ExcessBody];
            saved.BodyNames = Enumerable.Range(0, s.Count).Select(i => s[i].Name).ToList();
            return true;
        }

        // ---------------------------------------------------------------- saving

        // The plan as saved for the vessel (a GUID in "D" format). Null strings are stored empty.
        public static SavedPlan ToSaved(TransferPlan plan, BodySystem s, string vesselId, TransferPhase phase, int stepIndex,
            double ut, bool prepareOnly, bool burnStarted = false)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (s == null) throw new ArgumentNullException(nameof(s));
            if (plan.CaseName == null)
                plan.CaseName = "";
            var q = plan.Request;
            return new SavedPlan
            {
                VesselId = vesselId ?? "",
                Kind = "transfer",
                Plan = plan,
                Phase = phase,
                StepIndex = stepIndex,
                SavedUT = ut,
                PrepareOnly = prepareOnly,
                BurnStarted = burnStarted,
                FromBodyName = q != null && q.FromBody >= 0 && q.FromBody < s.Count ? s[q.FromBody].Name : "",
                ToBodyName = q != null && q.ToBody >= 0 && q.ToBody < s.Count ? s[q.ToBody].Name : "",
                BodyNames = Enumerable.Range(0, s.Count).Select(i => s[i].Name ?? "").ToList(),
            };
        }

        // The step Δv for StageBudget (DeltaVMagnitude: the vectors are zero for captures,
        // corrections and escapes) from step 'from' on, and the capture indices in that list.
        public static BudgetResult RemainingBudget(TransferPlan plan, int from, IReadOnlyList<StageDv> stages)
        {
            var dvs = new List<double>();
            var captures = new List<int>();
            for (int i = Math.Max(0, from); i < plan.Steps.Count; i++)
            {
                if (plan.Steps[i].Kind == StepKind.Capture)
                    captures.Add(dvs.Count);
                dvs.Add(plan.Steps[i].DeltaVMagnitude);
            }
            return StageBudget.Check(stages ?? new List<StageDv>(), dvs, captures, plan.ReserveDeltaV);
        }

        // ---------------------------------------------------------------- texts

        // "mid-course correction", "capture burn", …
        public static string StepName(TransferPlan plan, int i)
        {
            var step = plan.Steps[i];
            switch (step.Kind)
            {
                case StepKind.PlaneChange: return "plane change";
                case StepKind.OrbitChange: return "orbit change";
                case StepKind.Injection: return "injection burn";
                case StepKind.Escape: return "SOI exit";
                case StepKind.Capture: return "capture burn";
            }
            if (step.DeltaV.sqrMagnitude > 0)
                return "re-aim burn";
            int n = 0;
            for (int k = 0; k <= i; k++)
            {
                var st = plan.Steps[k];
                if (st.LegIndex == step.LegIndex && st.Kind == StepKind.Correction && !(st.DeltaV.sqrMagnitude > 0))
                    n++;
            }
            return n == 1 ? "correction after the escape" : n == 2 ? "mid-course correction" : "approach correction";
        }

        // Which correction of its leg step i is (1, 2, 3).
        public static int CorrectionNumber(TransferPlan plan, int i)
        {
            int n = 0;
            for (int k = 0; k <= i; k++)
                if (plan.Steps[k].LegIndex == plan.Steps[i].LegIndex && plan.Steps[k].Kind == StepKind.Correction)
                    n++;
            return n;
        }

        // "Resume plan to Duna (next: mid-course correction in 42 d)", or the mismatch message.
        // duration formats a number of seconds.
        public static string ResumeOffer(SavedPlan saved, ResumeCheck check, double now, Func<double, string> duration)
        {
            if (check == null || saved?.Plan == null)
                return PlanStore.MismatchMessage;
            if (check.Verdict == ResumeVerdict.Mismatch)
                return string.IsNullOrEmpty(check.Detail) ? check.Reason : $"{check.Reason} ({check.Detail})";
            string next;
            if (saved.StepIndex < saved.Plan.Steps.Count)
            {
                double dt = saved.Plan.Steps[saved.StepIndex].UT - now;
                next = $"{StepName(saved.Plan, saved.StepIndex)} {(dt > 0 ? "in " + duration(dt) : "now")}";
            }
            else
                next = "arrival";
            string text = $"Resume plan to {saved.ToBodyName} (next: {next})";
            if (saved.Phase == TransferPhase.Failed)
                text += ", stopped by a failure";
            if (check.Verdict == ResumeVerdict.Replan)
                text += $". It will be planned again: {check.Reason}";
            return text;
        }

        public static string FinalOrbit(string body, double peAltitude, double apAltitude, double inclinationDeg) =>
            Inv($"In orbit of {body}: {apAltitude / 1000:F0} × {peAltitude / 1000:F0} km, {inclinationDeg:F1}°");

        public static string CaptureFailed(string body) => $"Capture failed: you will fly past {body}";

        // One line per step, for the log.
        public static string Describe(TransferPlan plan, BodySystem s, double searchMs)
        {
            string Name(int b) => b >= 0 && b < s.Count ? s[b].Name : $"#{b}";
            var lines = new List<string>
            {
                Inv($"[Transfer] plan: {plan.CaseName}, {plan.Steps.Count} step(s), total {plan.TotalDeltaV:F1} m/s (reserve {plan.ReserveDeltaV:F1}), ")
                + Inv($"departure UT {plan.DepartureUT:F0}, arrival UT {plan.ArrivalUT:F0}, trip {plan.TripSeconds:F0} s, search {searchMs:F0} ms"),
            };
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var st = plan.Steps[i];
                lines.Add(Inv($"[Transfer]   {i}: {st.Kind} at {Name(st.Body)}, UT {st.UT:F0}, {st.DeltaVMagnitude:F1} m/s, leg {st.LegIndex}")
                    + (st.ReplanAtLegStart ? " (re-planned at the leg start)" : "")
                    + (st.TargetBody >= 0 ? Inv($", aims {Name(st.TargetBody)} Pe {st.TargetPeriapsisRadius / 1000:F1} km (radius)") : "")
                    + (string.IsNullOrEmpty(st.Explanation) ? "" : $": {st.Explanation}"));
            }
            foreach (string w in plan.Warnings)
                lines.Add($"[Transfer]   warning: {w}");
            return string.Join("\n", lines);
        }

        static string Inv(FormattableString f) => f.ToString(CultureInfo.InvariantCulture);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace K2D2.Transfer
{
    public static partial class TransferSearch
    {
        // The burn to correct an existing encounter is solved this long after the plan is made: room to
        // turn the vessel (BurnWarp's lead for a half turn, and a margin).
        public const double ExistingCorrectionLead = 120;

        public const string ExistingEncounterCase = "Correct the existing encounter";

        sealed partial class Search
        {
            // From an orbit of the target's parent that already meets the target (or nearly): a
            // correction to that encounter, costed like a plan (correction + capture + reserve). Null
            // when the orbit does not reach the target within the budget, or the trip cap.
            Built CorrectExisting()
            {
                if (S[To].ParentIndex != From)
                    return null;
                Ct.ThrowIfCancellationRequested();
                double burnUT = Q.UT0 + ExistingCorrectionLead;
                VesselAt(burnUT, out var r, out var v);
                double span = Math.Max(VesselPeriod, S.OrbitalPeriod(To));
                double horizon = burnUT + Math.Min(1.25 * span, double.IsInfinity(MaxTrip) ? double.PositiveInfinity : MaxTrip);
                double captureAp = Q.Arrival == ArrivalKind.Orbit ? ArrRa : double.NaN;
                if (!ArrivalTargeting.Solve(S, From, r, v, burnUT, To, ArrRp, ArrInc, horizon, double.NaN, captureAp,
                        out var dv, out double pe, out _, out double arrivalUT, out double capture, out bool converged))
                {
                    Trace?.Invoke("existing encounter: no correction within the limit");
                    return null;
                }
                if (!converged && Math.Abs(pe - ArrRp) > Math.Max(10_000, 0.1 * ArrRp))
                {
                    Trace?.Invoke(Inv($"existing encounter: the best correction misses the periapsis by {Math.Abs(pe - ArrRp) / 1000:F0} km"));
                    return null;
                }

                var target = FinalTarget();
                var plan = NewPlan();
                plan.CaseName = ExistingEncounterCase;
                string name = S[To].Name;
                plan.Steps.Add(Step(StepKind.Correction, From, burnUT, dv.magnitude,
                    Inv($"Correct the existing encounter with {name}: about {dv.magnitude:F0} m/s now, solved again from the actual orbit (cheaper than a new transfer)"), 0, target));
                var times = CorrectionTimes(burnUT, burnUT, arrivalUT, arrivalUT);
                if (times != null)
                {
                    plan.Steps.Add(Step(StepKind.Correction, From, times[1], 0, $"Mid-course correction: aims {Describe(target)} ({Solved})", 0, target));
                    plan.Steps.Add(Step(StepKind.Correction, From, times[2], 0, $"Approach correction: sets {Describe(target)}, the arrival plane included ({Solved})", 0, target));
                }
                if (Q.Arrival == ArrivalKind.Orbit)
                    plan.Steps.Add(Step(StepKind.Capture, To, arrivalUT, capture,
                        Inv($"Capture at the {name} periapsis ({Km(To, ArrRp)} km) into a {Km(To, ArrRp)} × {Km(To, ArrRa)} km orbit"), 0, target));
                var built = Finish(new Built(), plan, burnUT, arrivalUT, new Dictionary<int, Target> { [0] = target });
                Trace?.Invoke(Inv($"existing encounter: correction {dv.magnitude:F1} m/s, capture {capture:F1} m/s, total {built.Cost:F1} m/s"));
                return built;
            }

            // The cheaper of a new transfer and the correction of the encounter the orbit already has.
            TransferPlan CheaperWithExisting(TransferPlan fresh)
            {
                var existing = CorrectExisting();
                if (existing == null || !existing.Plan.Ok)
                    return fresh;
                if (fresh.Ok && fresh.TotalDeltaV <= existing.Plan.TotalDeltaV)
                {
                    fresh.Warnings.Add(Inv($"Correcting the existing encounter would cost {existing.Plan.TotalDeltaV:F0} m/s; the new transfer costs {fresh.TotalDeltaV:F0} m/s."));
                    return fresh;
                }
                if (fresh.Ok)
                    existing.Plan.Warnings.Add(Inv($"A new transfer would cost {fresh.TotalDeltaV:F0} m/s; correcting the existing encounter costs {existing.Plan.TotalDeltaV:F0} m/s."));
                return existing.Plan;
            }
        }
    }
}

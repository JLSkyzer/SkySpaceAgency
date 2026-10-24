using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace K2D2.Transfer
{
    // One stage of the vessel, as the game reports it (VesselDeltaV.StageInfo, vacuum).
    public struct StageDv
    {
        public int Stage;          // the game's stage number
        public double DeltaVVac;   // m/s
        // What firing this stage drops.
        public bool HasDockingPort, HasParachute, HasCommandPart;
    }

    public class BudgetResult
    {
        public bool Ok;
        public string Refusal;
        public double Available;   // the sum of the stages
        public double Needed;      // the steps plus the reserve
        // Which stage pays for which step, in firing order. A step that spans stages appears once per
        // stage; the part no stage covers goes to stage -1. Steps of 0 m/s are left out.
        public List<(int step, int stage)> Allocation = new List<(int step, int stage)>();
    }

    public static class StageBudget
    {
        // Pays the steps in order from the stages in firing order: a stage's leftover after a step
        // stays on the vessel and pays the next one. Each capture (a staged arrival has several) must
        // be covered, with the reserve, by what is left when it starts. Per-stage Δv is additive, so
        // the two checks agree on whether the plan fits; the capture message is given when the
        // shortfall falls at a capture, the total message otherwise.
        public static BudgetResult Check(IReadOnlyList<StageDv> stagesInFiringOrder, IReadOnlyList<double> stepDeltaV,
            IReadOnlyList<int> captureStepIndices, double reserve)
        {
            if (stagesInFiringOrder == null) throw new ArgumentNullException(nameof(stagesInFiringOrder));
            if (stepDeltaV == null) throw new ArgumentNullException(nameof(stepDeltaV));
            var captures = new HashSet<int>(captureStepIndices ?? Array.Empty<int>());
            foreach (int c in captures)
                if (c < 0 || c >= stepDeltaV.Count)
                    throw new ArgumentOutOfRangeException(nameof(captureStepIndices), c, "not a step index");

            var result = new BudgetResult
            {
                Available = stagesInFiringOrder.Sum(st => Math.Max(0, st.DeltaVVac)),
                Needed = stepDeltaV.Sum(d => Math.Max(0, d)) + Math.Max(0, reserve),
            };

            int stage = 0;
            double leftInStage = stagesInFiringOrder.Count > 0 ? Math.Max(0, stagesInFiringOrder[0].DeltaVVac) : 0;
            double left = result.Available;
            string refusal = null;

            for (int i = 0; i < stepDeltaV.Count; i++)
            {
                double dv = Math.Max(0, stepDeltaV[i]);
                if (refusal == null && captures.Contains(i) && left < dv + reserve)
                    refusal = $"The stages left at the arrival have {F(left)} m/s, the capture needs {F(dv + reserve)} m/s.";
                if (refusal == null && left < dv)
                    refusal = TotalRefusal(result);

                double toPay = dv;
                while (toPay > 0)
                {
                    while (stage < stagesInFiringOrder.Count && leftInStage <= 0)
                    {
                        stage++;
                        leftInStage = stage < stagesInFiringOrder.Count ? Math.Max(0, stagesInFiringOrder[stage].DeltaVVac) : 0;
                    }
                    if (stage >= stagesInFiringOrder.Count)
                    {
                        result.Allocation.Add((i, -1));
                        break;
                    }
                    double paid = Math.Min(toPay, leftInStage);
                    result.Allocation.Add((i, stagesInFiringOrder[stage].Stage));
                    toPay -= paid;
                    leftInStage -= paid;
                    left -= paid;
                }
            }

            if (refusal == null && result.Available < result.Needed)
                refusal = TotalRefusal(result);

            result.Ok = refusal == null;
            if (!result.Ok)
                result.Refusal = refusal + " " + PerStage(stagesInFiringOrder);
            return result;
        }

        static string TotalRefusal(BudgetResult r) =>
            $"Not enough Δv: needs {F(r.Needed)} m/s, the vessel has {F(r.Available)} m/s.";

        static string PerStage(IReadOnlyList<StageDv> stages) =>
            stages.Count == 0
                ? "No stage has Δv."
                : "Per stage: " + string.Join(", ", stages.Select(st => $"stage {st.Stage}: {F(st.DeltaVVac)} m/s")) + ".";

        static string F(double v) => v.ToString("F0", CultureInfo.InvariantCulture);
    }
}

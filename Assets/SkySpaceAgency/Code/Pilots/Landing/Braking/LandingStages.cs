using System.Collections.Generic;
using System.Globalization;

namespace K2D2.Landing.Braking
{
    // One stage of the vessel as the stage choice needs it, read from the game's
    // VesselDeltaV.StageInfo (DeltaVStageInfo).
    public struct LandingStageData
    {
        public int Stage;          // DeltaVStageInfo.Stage
        public double DeltaV;      // m/s, vacuum
        public double ThrustKN;    // the stage's active engines
        public double Isp;         // s
        public double StartMassT;  // vessel mass when the stage starts
    }

    // The "Land with stage" choice of the Land tab. The stages are counted from the active one,
    // which is stage 1, in firing order (DeltaVStageInfo.Stage descending): a booster under a
    // lander is stage 1, the lander stage 2. Pure, tested in EditMode.
    public static class LandingStages
    {
        public const string GoneMessage = "The chosen stage is not on the vessel any more: choose it again.";

        /// <summary>
        /// The stages to simulate, from the active one (index 0 of firingOrder) to the landing one
        /// (landingIndex), or null for the current stage alone (landingIndex 0: the landing as
        /// before) and on an error. The active stage flies with the live mass, thrust and Isp when
        /// they are known.
        /// </summary>
        public static BrakeStage[] Build(IReadOnlyList<LandingStageData> firingOrder, int landingIndex,
            double currentMassT, double activeThrustKN, double activeIsp, out string error)
        {
            error = null;
            if (landingIndex == 0)
                return null;
            if (firingOrder == null || landingIndex < 0 || landingIndex >= firingOrder.Count)
            {
                error = GoneMessage;
                return null;
            }
            if (!(firingOrder[landingIndex].ThrustKN > 0))
            {
                error = string.Format(CultureInfo.InvariantCulture, "Stage {0} has no engine: choose a stage that can brake.", landingIndex + 1);
                return null;
            }

            var stages = new BrakeStage[landingIndex + 1];
            for (int i = 0; i <= landingIndex; i++)
            {
                LandingStageData s = firingOrder[i];
                stages[i] = new BrakeStage
                {
                    Stage = s.Stage,
                    ThrustKN = Positive(s.ThrustKN),
                    MassT = Positive(s.StartMassT),
                    Isp = Positive(s.Isp),
                    DeltaV = Positive(s.DeltaV),
                };
            }
            if (currentMassT > 0)
                stages[0].MassT = currentMassT;
            if (activeThrustKN > 0)
                stages[0].ThrustKN = activeThrustKN;
            if (activeIsp > 0)
                stages[0].Isp = activeIsp;
            return stages;
        }

        /// <summary>The choices in the order of the game's staging stack: the last to fire on top.</summary>
        public static List<string> Labels(IReadOnlyList<LandingStageData> firingOrder)
        {
            var labels = new List<string>();
            if (firingOrder == null)
                return labels;
            for (int i = firingOrder.Count - 1; i >= 0; i--)
                labels.Add(Label(i, firingOrder[i]));
            return labels;
        }

        public static string Label(int index, LandingStageData stage) =>
            string.Format(CultureInfo.InvariantCulture, "Stage {0}{1}: {2:0} m/s", index + 1, index == 0 ? " (current)" : "", stage.DeltaV);

        /// <summary>The firing-order index of a label, from its "Stage N" (the Δv changes while
        /// burning); -1 when it names no stage of the list.</summary>
        public static int IndexOfLabel(IReadOnlyList<LandingStageData> firingOrder, string label)
        {
            if (firingOrder == null || string.IsNullOrEmpty(label) || !label.StartsWith("Stage "))
                return -1;
            int end = 6;
            while (end < label.Length && char.IsDigit(label[end]))
                end++;
            if (!int.TryParse(label.Substring(6, end - 6), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                return -1;
            return n >= 1 && n <= firingOrder.Count ? n - 1 : -1;
        }

        static double Positive(double x) => x > 0 && !double.IsInfinity(x) ? x : 0;
    }
}

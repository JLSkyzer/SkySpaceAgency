using System;
using System.Globalization;

namespace K2D2.Landing.Precision
{
    // The Δv check of a precision landing: deorbit + braking toward the target, plus the
    // mid-course and terminal reserves, against the same landing without target (deorbit at the
    // natural point, then braking). The orbit preparation and the plane change are shown apart
    // and are not counted in the ratio. Pure maths, tested in EditMode.
    public static class PrecisionBudget
    {
        public const double MaxRatio = 1.20;

        public struct Budget
        {
            public double BaselineDeltaV;     // m/s, deorbit + braking without target
            public double PrecisionDeltaV;    // m/s, deorbit + braking toward the target + reserves
            public double PreparationDeltaV;  // m/s, shown apart
            public double PlaneChangeDeltaV;  // m/s, shown apart
            public bool WithinLimit;          // PrecisionDeltaV ≤ MaxRatio × BaselineDeltaV
            public string Summary;
        }

        public static Budget Evaluate(DeorbitPlan baseline, DeorbitPlan precision, double correctionReserve,
            double terminalReserve, double preparationDeltaV, double planeChangeDeltaV)
        {
            double b = baseline.DeltaV.magnitude + baseline.Stop.BrakeDeltaV;
            double p = precision.DeltaV.magnitude + precision.Stop.BrakeDeltaV
                + Math.Max(0, correctionReserve) + Math.Max(0, terminalReserve);
            bool within = b > 0 && p <= MaxRatio * b;
            double pct = b > 0 ? (p / b - 1) * 100 : double.NaN;
            CultureInfo c = CultureInfo.InvariantCulture;
            string summary = string.Format(c,
                "Δv: landing {0:0} m/s (no target {1:0}, {2} %), plane change {3:0} m/s, orbit preparation {4:0} m/s",
                p, b, double.IsNaN(pct) ? "+?" : pct.ToString("+0;-0;+0", c), planeChangeDeltaV, preparationDeltaV);
            return new Budget
            {
                BaselineDeltaV = b,
                PrecisionDeltaV = p,
                PreparationDeltaV = preparationDeltaV,
                PlaneChangeDeltaV = planeChangeDeltaV,
                WithinLimit = within,
                Summary = summary,
            };
        }
    }
}

using System;
using System.Globalization;
using K2D2.Landing.Braking;

namespace K2D2.Landing.Precision
{
    // The Δv check of a precision landing, both sides predicted at start for the same vessel from
    // the same orbit:
    //  - baseline (the landing without target, as LandingPilot flies it): DeorbitSearch.Baseline
    //    with Vehicle.StopHeight = 0, i.e. deorbit + brake to BrakeSimulator.SafeAltitude, plus
    //    the final descent from there (baselineTerminalReserve = TerminalReserve(SafeAltitude, …));
    //  - precision: DeorbitSearch.Find with StopHeight = the hand-over height, i.e. deorbit +
    //    brake to the hand-over, plus the mid-course reserve and the terminal reserve from the
    //    hand-over height (TerminalReserve(handOver, …); never 0).
    // The orbit preparation and the plane change are shown apart and are not counted in the
    // ratio. Pure maths, tested in EditMode.
    public static class PrecisionBudget
    {
        public const double MaxRatio = 1.20;

        public struct Budget
        {
            public double BaselineDeltaV;     // m/s, deorbit + braking + final descent without target
            public double PrecisionDeltaV;    // m/s, deorbit + braking toward the target + reserves
            public double PreparationDeltaV;  // m/s, shown apart
            public double PlaneChangeDeltaV;  // m/s, shown apart
            public bool WithinLimit;          // both plans Ok and PrecisionDeltaV ≤ MaxRatio × BaselineDeltaV
            public string Reason;             // why not WithinLimit; null otherwise
            public bool OverBudget;           // both plans Ok but the precision Δv exceeds MaxRatio × baseline
            public string RefusalMessage;     // what the pilot tells the player when OverBudget; null otherwise
            public string Summary;
        }

        public static Budget Evaluate(DeorbitPlan baseline, DeorbitPlan precision, double correctionReserve,
            double terminalReserve, double preparationDeltaV, double planeChangeDeltaV, double baselineTerminalReserve = 0)
        {
            double b = baseline.DeltaV.magnitude + baseline.Stop.BrakeDeltaV + Math.Max(0, baselineTerminalReserve);
            double p = precision.DeltaV.magnitude + precision.Stop.BrakeDeltaV
                + Math.Max(0, correctionReserve) + Math.Max(0, terminalReserve);
            double pct = b > 0 ? (p / b - 1) * 100 : double.NaN;
            CultureInfo c = CultureInfo.InvariantCulture;

            string reason = null;
            if (!baseline.Ok)
                reason = "no landing without target to compare with: " + baseline.Reason;
            else if (!precision.Ok)
                reason = "no precision landing: " + precision.Reason;
            else if (!(b > 0))
                reason = "no baseline Δv";
            else if (p > MaxRatio * b)
                reason = string.Format(c, "precision costs {0:0} % more than the landing without target (limit {1:0} %)",
                    pct, (MaxRatio - 1) * 100);

            bool over = baseline.Ok && precision.Ok && b > 0 && p > MaxRatio * b;
            string summary = string.Format(c,
                "Δv: landing {0:0} m/s (no target {1:0}, {2} %), plane change {3:0} m/s, orbit preparation {4:0} m/s",
                p, b, double.IsNaN(pct) ? "+?" : pct.ToString("+0;-0;+0", c), planeChangeDeltaV, preparationDeltaV);
            if (reason != null && (!baseline.Ok || !precision.Ok || !(b > 0)))
                summary += " — budget not checked: " + reason;
            return new Budget
            {
                BaselineDeltaV = b,
                PrecisionDeltaV = p,
                PreparationDeltaV = preparationDeltaV,
                PlaneChangeDeltaV = planeChangeDeltaV,
                WithinLimit = reason == null,
                OverBudget = over,
                RefusalMessage = over ? OverBudgetMessage(pct) : null,
                Reason = reason,
                Summary = summary,
            };
        }

        public static string OverBudgetMessage(double pct) =>
            string.Format(CultureInfo.InvariantCulture,
                "Precision would cost {0} % Δv (limit {1:0} %): land without a target, or pick a closer target.",
                pct.ToString("+0;-0;+0", CultureInfo.InvariantCulture), (MaxRatio - 1) * 100);

        /// <summary>
        /// Δv of a vertical descent from rest at heightM to the ground, flown as TerminalGuidance's
        /// vertical channel: free fall until the speed meets the profile sqrt(vf² + 2·d·h), then
        /// braking at d = PlanFraction·(maxAccel − g) to the contact speed vf. The thrust is
        /// (g + d) for the braking time. NaN when the thrust cannot hold the vessel up.
        /// </summary>
        public static double TerminalReserve(double heightM, double gravity, double touchDownSpeed, double maxAccel)
        {
            if (!(maxAccel > gravity) || !(gravity >= 0))
                return double.NaN;
            double h = Math.Max(0, heightM);
            double vf = FinalDescent.ContactSpeed(touchDownSpeed);
            double d = TerminalGuidance.PlanFraction * (maxAccel - gravity);
            double hb = Math.Min(h, Math.Max(0, (2 * gravity * h - vf * vf) / (2 * (gravity + d))));
            double sb = Math.Sqrt(vf * vf + 2 * d * hb);
            double brakeTime = Math.Max(0, (sb - vf) / d);
            return (gravity + d) * brakeTime;
        }
    }
}

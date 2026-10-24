using System;
using System.Collections.Generic;
using System.Globalization;
using K2D2.OrbitPlanning;

namespace K2D2.Transfer
{
    // The game's calendar: 6 h days, 426-day years, starting at year 1, day 1.
    public static class KerbinCalendar
    {
        public const int DayHours = 6;
        public const int YearDays = 426;
        public const double DaySeconds = DayHours * 3600.0;
        public const double YearSeconds = YearDays * DaySeconds;

        // "Y1, D12, 3h 05m".
        public static string Date(double ut)
        {
            if (double.IsNaN(ut) || double.IsInfinity(ut))
                return "-";
            long minutes = (long)Math.Floor(Math.Max(0, ut) / 60);
            long perDay = DayHours * 60, perYear = perDay * YearDays;
            long year = minutes / perYear + 1;
            minutes %= perYear;
            long day = minutes / perDay + 1;
            minutes %= perDay;
            return $"Y{year}, D{day}, {minutes / 60}h {minutes % 60:00}m";
        }

        // "2y 10d 3h", "12d 4h", "3h 05m", "4m 10s", "35 s"; a leading "-" when negative.
        public static string Duration(double seconds)
        {
            if (double.IsNaN(seconds))
                return "-";
            if (double.IsInfinity(seconds))
                return "no limit";
            string sign = seconds < 0 ? "-" : "";
            double s = Math.Abs(seconds);
            if (s < 60)
                return sign + Math.Floor(s).ToString("F0", CultureInfo.InvariantCulture) + " s";
            long total = (long)Math.Floor(s);
            long years = total / (long)YearSeconds;
            long rest = total % (long)YearSeconds;
            long days = rest / (long)DaySeconds;
            rest %= (long)DaySeconds;
            long hours = rest / 3600, minutes = rest % 3600 / 60, secs = rest % 60;
            if (years > 0)
                return $"{sign}{years}y {days}d {hours}h";
            if (days > 0)
                return $"{sign}{days}d {hours}h";
            if (hours > 0)
                return $"{sign}{hours}h {minutes:00}m";
            return $"{sign}{minutes}m {secs:00}s";
        }
    }

    // The Transfer tab's texts and tables, from a plan and the stages. Pure, tested in EditMode.
    public static class TransferReadout
    {
        public struct StepRow
        {
            public string Kind;        // "Injection burn (Kerbin)"
            public string When;        // the date, and how long until it
            public string DeltaV;
            public string Explanation;
            public bool Done;          // flown already
        }

        public struct StageRow
        {
            public int Stage;
            public double DeltaV;
            public string Pays;        // the steps this stage pays for, "" when none
            public bool ByHand;        // the mod will not fire it: it drops a docking port, …
            public string Text;
        }

        // ---------------------------------------------------------------- inputs

        // The request from the tab's fields (the vessel's state is filled in by the pilot). With low
        // circular, the arrival orbit is the target's default (NaN). An empty max trip means no cap.
        public static bool TryRequest(BodySystem s, int target, ArrivalKind arrival, bool lowCircular,
            double peKm, double apKm, bool setInclination, double inclinationDeg, string maxTripDays,
            out TransferRequest request, out string error)
        {
            request = null;
            if (s == null || target < 0 || target >= s.Count)
            {
                error = "Choose a target body.";
                return false;
            }
            if (!TryParseDays(maxTripDays, out double maxTrip, out error))
                return false;
            var q = new TransferRequest { ToBody = target, Arrival = arrival, MaxTripSeconds = maxTrip };
            if (!lowCircular)
            {
                double radius = s[target].Radius;
                if (!(peKm >= 0))
                {
                    error = "The Pe must be above the surface.";
                    return false;
                }
                q.ArrivalPeriapsisRadius = radius + peKm * 1000;
                if (arrival == ArrivalKind.Orbit)
                {
                    if (!(apKm >= peKm))
                    {
                        error = "The Ap cannot be lower than the Pe.";
                        return false;
                    }
                    q.ArrivalApoapsisRadius = radius + apKm * 1000;
                }
                if (setInclination)
                {
                    if (!(inclinationDeg >= 0 && inclinationDeg <= 180))
                    {
                        error = "The inclination must be between 0° and 180°.";
                        return false;
                    }
                    q.ArrivalInclinationDeg = inclinationDeg;
                }
            }
            request = q;
            error = null;
            return true;
        }

        // Days of 6 h. Empty: +∞ (no cap). A comma is read as the decimal point.
        public static bool TryParseDays(string text, out double seconds, out string error)
        {
            seconds = double.PositiveInfinity;
            error = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;
            string t = text.Trim().Replace(',', '.');
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double days)
                || double.IsNaN(days) || double.IsInfinity(days) || days <= 0)
            {
                error = $"Max trip duration: \"{text.Trim()}\" is not a number of days above 0 (leave it empty for no limit).";
                return false;
            }
            seconds = days * KerbinCalendar.DaySeconds;
            return true;
        }

        // ---------------------------------------------------------------- plan

        public static string Departure(TransferPlan plan, double now)
        {
            double wait = plan.DepartureUT - now;
            return $"Departure: {KerbinCalendar.Date(plan.DepartureUT)}, " +
                (wait > 0 ? $"in {KerbinCalendar.Duration(wait)}" : "now");
        }

        public static string Trip(TransferPlan plan) =>
            $"Trip: {KerbinCalendar.Duration(plan.TripSeconds)}, arrival {KerbinCalendar.Date(plan.ArrivalUT)}";

        public static string Totals(TransferPlan plan, BudgetResult budget) =>
            Inv($"Total {plan.TotalDeltaV:F0} m/s, reserve included ({plan.ReserveDeltaV:F0} m/s)")
            + (budget == null ? "" : Inv($"; the vessel has {budget.Available:F0} m/s"));

        public static string Margin(BudgetResult budget)
        {
            double m = budget.Available - budget.Needed;
            return m >= 0
                ? Inv($"Margin: {m:F0} m/s left at the arrival")
                : Inv($"Margin: short by {-m:F0} m/s");
        }

        // One row per step; steps before doneBefore are marked flown.
        public static List<StepRow> Steps(TransferPlan plan, BodySystem s, double now, int doneBefore)
        {
            var rows = new List<StepRow>(plan.Steps.Count);
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var st = plan.Steps[i];
                string body = s != null && st.Body >= 0 && st.Body < s.Count ? s[st.Body].Name : "?";
                double dt = st.UT - now;
                bool done = i < doneBefore;
                string dv;
                if (st.Kind == StepKind.Escape)
                    dv = "-";
                else if (st.Kind == StepKind.Correction && !(st.DeltaVMagnitude > 0))
                    dv = "reserve";
                else
                    dv = Inv($"{st.DeltaVMagnitude:F1} m/s");
                rows.Add(new StepRow
                {
                    Kind = Capitalize(TransferFlight.StepName(plan, i)) + $" ({body})",
                    When = KerbinCalendar.Date(st.UT) + (done ? ", done" : dt > 0 ? $", in {KerbinCalendar.Duration(dt)}" : ", now"),
                    DeltaV = dv,
                    Explanation = st.Explanation ?? "",
                    Done = done,
                });
            }
            return rows;
        }

        // The stages in firing order, with the steps each pays for (budget from
        // TransferFlight.RemainingBudget(plan, from, stages): its step indices start at 'from'). The
        // first stage is the one burning now; a later stage that would drop a docking port, a
        // parachute or the command part is not fired by the mod (StagingGuard). note says when the
        // plan needs such a stage, or null.
        public static List<StageRow> Stages(TransferPlan plan, int from, IReadOnlyList<StageDv> stages,
            BudgetResult budget, out string note)
        {
            note = null;
            var rows = new List<StageRow>();
            if (stages == null)
                return rows;
            var pays = new Dictionary<int, List<int>>();
            bool unpaid = false;
            if (budget != null)
                foreach (var (step, stage) in budget.Allocation)
                {
                    if (stage < 0)
                    {
                        unpaid = true;
                        continue;
                    }
                    if (!pays.TryGetValue(stage, out var list))
                        pays[stage] = list = new List<int>();
                    int index = from + step;
                    if (!list.Contains(index))
                        list.Add(index);
                }

            int firstByHand = -1;
            string firstReason = null;
            for (int k = 0; k < stages.Count; k++)
            {
                var st = stages[k];
                string reason = null;
                bool byHand = k > 0 && !StagingGuard.MayDrop(st, out reason);
                var names = new List<string>();
                if (pays.TryGetValue(st.Stage, out var steps))
                    foreach (int i in steps)
                        if (i >= 0 && i < plan.Steps.Count)
                        {
                            string n = TransferFlight.StepName(plan, i);
                            if (!names.Contains(n))
                                names.Add(n);
                        }
                string paysText = string.Join(", ", names);
                if (byHand && firstByHand < 0 && names.Count > 0)
                {
                    firstByHand = st.Stage;
                    firstReason = reason;
                }
                rows.Add(new StageRow
                {
                    Stage = st.Stage,
                    DeltaV = st.DeltaVVac,
                    Pays = paysText,
                    ByHand = byHand,
                    Text = Inv($"Stage {st.Stage}: {st.DeltaVVac:F0} m/s")
                        + (names.Count > 0 ? ", pays the " + paysText : ", not needed")
                        + (byHand ? $". Stage it by hand: it drops {reason}" : ""),
                });
            }
            if (firstByHand >= 0)
                note = $"The Δv from stage {firstByHand} on needs you to stage by hand: the mod does not fire a stage that drops {firstReason}.";
            if (unpaid)
                note = (note == null ? "" : note + " ") + "No stage is left for the last step(s).";
            return rows;
        }

        // Burns longer than TransferFlight.LongBurnShare of the orbit they start from, estimated
        // before the flight. burnSeconds(dv) gives a burn's length with the current stage (NaN when
        // unknown). Only orbits the plan knows are checked: the vessel's at the departure and the
        // capture's target orbit.
        public static List<string> LongBurnWarnings(TransferPlan plan, BodySystem s, Func<double, double> burnSeconds)
        {
            var warnings = new List<string>();
            if (plan?.Request == null || s == null || burnSeconds == null)
                return warnings;
            var q = plan.Request;
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var st = plan.Steps[i];
                if (!(st.DeltaVMagnitude > 0) || st.Body < 0 || st.Body >= s.Count)
                    continue;
                double period = double.NaN;
                switch (st.Kind)
                {
                    case StepKind.Injection:
                    case StepKind.OrbitChange:
                    case StepKind.PlaneChange:
                        if (st.Body == q.FromBody && st.LegIndex == 0)
                        {
                            var el = OrbitMath.Elements(q.R0, q.V0, s[q.FromBody].Mu);
                            if (el.IsClosed)
                                period = el.Period;
                        }
                        break;
                    case StepKind.Capture:
                    {
                        double pe = st.TargetPeriapsisRadius;
                        double ap = double.IsNaN(st.TargetApoapsisRadius) ? pe : st.TargetApoapsisRadius;
                        if (pe > 0 && ap >= pe)
                            period = 2 * Math.PI * Math.Sqrt(Math.Pow((pe + ap) / 2, 3) / s[st.Body].Mu);
                        break;
                    }
                }
                if (!(period > 0) || double.IsInfinity(period))
                    continue;
                double seconds = burnSeconds(st.DeltaVMagnitude);
                if (TransferFlight.IsLongBurn(seconds, period))
                    warnings.Add($"The {TransferFlight.StepName(plan, i)} should last about {KerbinCalendar.Duration(seconds)}, " +
                        $"more than {TransferFlight.LongBurnShare * 100:F0} % of the orbit period ({KerbinCalendar.Duration(period)}): " +
                        "the closed loop corrects, but expect a larger error.");
            }
            return warnings;
        }

        static string Capitalize(string text) =>
            string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1);

        static string Inv(FormattableString f) => f.ToString(CultureInfo.InvariantCulture);
    }
}

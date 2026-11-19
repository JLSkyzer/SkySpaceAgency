using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using SkySpaceAgency.Rendezvous;

namespace SkySpaceAgency.Transfer
{
    // The result of the assist search (4.6): the direct plan, the cheapest assist, and whether that
    // assist is offered to the player.
    public sealed class AssistChoice
    {
        public TransferPlan Direct, Assist;
        public bool Offered;
        public double Saving = double.NaN; // 1 - assist / direct; NaN when the direct plan is refused
        public string NotOffered = "";
        public double SearchMs;
        public bool BudgetReached;

        public TransferPlan Preselected =>
            Offered && (Direct == null || !Direct.Ok || Assist.TotalDeltaV < Direct.TotalDeltaV) ? Assist : Direct;
    }

    // The search with gravity assists: the direct search, then every flyby of one body (a planet on
    // the way, a moon on the way out), within a time budget. An assist is offered when it saves 5 %
    // of the direct plan and fits the trip cap (or alone when the direct plan is refused). Pure code,
    // on the search's thread; honours the token.
    public static class AssistSearch
    {
        public const double MinSaving = 0.05, TimeBudgetSeconds = 30;
        const int SiblingBuilds = 3, CandidatesPerBody = 3;

        // The log (the glue sets the logger; tests capture it). Called on the search's thread.
        public static Action<string> Log;

        static void Say(FormattableString text) => Log?.Invoke("[Transfer] assist: " + text.ToString(CultureInfo.InvariantCulture));

        // The plain search when the option is off, else the assist search's preselected plan (the 4.5
        // chain takes the cheaper offer by itself).
        public static TransferFinder Finder(bool useAssist) => useAssist
            ? (TransferFinder)((s, q, ct, p) => Chosen(Find(s, q, ct, p), s))
            : (s, q, ct, p) => TransferSearch.Find(s, q, ct, p);

        // The 4.5 chain's plan (the user's answer 2): the preselected plan, with a line naming it and the
        // alternative, so that the Rendezvous tab shows both.
        static TransferPlan Chosen(AssistChoice choice, BodySystem s)
        {
            var plan = choice.Preselected;
            if (plan == null || !plan.Ok || choice.Direct == null)
                return plan;
            var other = plan == choice.Assist ? choice.Direct : choice.Assist;
            string Line(TransferPlan p) => TransferReadout.AssistLine(TransferReadout.AssistLabel(p, s), p, p == plan);
            string text = other != null
                ? $"Gravity assist: {Line(plan)}; {Line(other)}"
                : $"Gravity assist: {Line(plan)}; {choice.NotOffered}";
            if (!plan.Warnings.Contains(text))
                plan.Warnings.Add(text);
            return plan;
        }

        public static AssistChoice Find(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> progress = null,
            double timeBudgetSeconds = TimeBudgetSeconds, TransferFinder directSearch = null)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));
            if (q == null)
                throw new ArgumentNullException(nameof(q));
            var watch = Stopwatch.StartNew();
            var choice = new AssistChoice();

            // 1. The direct plan.
            var direct = (directSearch ?? TransferSearch.Find)(s, q, ct, Scaled(progress, 0, 0.35f));
            choice.Direct = direct;
            if (direct.Ok)
                Say($"direct {direct.TotalDeltaV:F0} m/s, trip {KerbinCalendar.Duration(direct.TripSeconds)}, in {watch.ElapsedMilliseconds} ms");
            else
                Say($"direct refused: {direct.Refusal}");

            // The assists are searched without the trip cap: the offer applies it, so that a too
            // long assist is named rather than hidden.
            var open = Copy(q);
            open.MaxTripSeconds = double.PositiveInfinity;
            var assists = new List<TransferPlan>();
            string firstRefusal = null;
            void Refused(string why) => firstRefusal = firstRefusal ?? why;
            bool OverBudget()
            {
                if (watch.Elapsed.TotalSeconds < timeBudgetSeconds)
                    return false;
                choice.BudgetReached = true;
                return true;
            }

            // 2. Sibling flybys: every child of the common ancestor on the way.
            var siblings = SiblingBodies(s, q);
            var pool = new List<(int body, AssistCandidate c)>();
            for (int i = 0; i < siblings.Count && !OverBudget(); i++)
            {
                ct.ThrowIfCancellationRequested();
                int b = siblings[i];
                var gridWatch = Stopwatch.StartNew();
                var legs = TransferSearch.SiblingAssistLegs(s, open, b, out var refusal);
                if (legs == null)
                {
                    Refused(refusal.Refusal);
                    continue;
                }
                float from = 0.35f + 0.45f * i / siblings.Count, to = 0.35f + 0.45f * (i + 1) / siblings.Count;
                var list = AssistPorkchop.Search(legs, ct, progress, from, to);
                if (list.Count > 0)
                {
                    var best = list[0];
                    Say($"grid via {s[b].Name}: {AssistPorkchop.LastLambertSolves} Lambert solves, {AssistPorkchop.LastFeasible} feasible of {AssistPorkchop.LastCells}, best {best.Cost:F0} m/s (flyby {best.FlybyDeltaV:F0} m/s at {(best.FlybyPeriapsis - s[b].Radius) / 1000:F0} km), {gridWatch.ElapsedMilliseconds} ms");
                }
                else
                {
                    Say($"grid via {s[b].Name}: {AssistPorkchop.LastLambertSolves} Lambert solves, no feasible flyby, {gridWatch.ElapsedMilliseconds} ms");
                    Refused($"no flyby of {s[b].Name} fits");
                }
                pool.AddRange(list.Take(CandidatesPerBody).Select(c => (b, c)));
            }
            foreach (var (b, c) in pool.OrderBy(x => x.c.Cost).Take(SiblingBuilds))
            {
                if (OverBudget())
                    break;
                var plan = TransferSearch.FindSiblingAssist(s, open, b, c, ct);
                if (plan.Ok)
                {
                    Say($"built via {s[b].Name}: {plan.TotalDeltaV:F0} m/s");
                    assists.Add(plan);
                }
                else
                {
                    Say($"built via {s[b].Name}: refused: {plan.Refusal}");
                    Refused(plan.Refusal);
                }
            }
            progress?.Report(0.8f);

            // 3. Departure flybys: every moon of the vessel's body.
            var moons = MoonBodies(s, q);
            foreach (int m in moons)
            {
                if (OverBudget())
                    break;
                ct.ThrowIfCancellationRequested();
                var plan = TransferSearch.FindMoonDeparture(s, open, m, ct);
                if (plan.Ok)
                {
                    double saving = direct.Ok ? DepartureCost(direct, s) - DepartureCost(plan, s) : double.NaN;
                    Say($"departure via {s[m].Name}: {plan.TotalDeltaV:F0} m/s, saves {saving:F0} m/s on the departure");
                    assists.Add(plan);
                }
                else
                {
                    Say($"departure via {s[m].Name}: refused: {plan.Refusal}");
                    Refused(plan.Refusal);
                }
            }
            progress?.Report(0.95f);

            // 4. The offer.
            foreach (var a in assists)
                a.Request = q;
            var cheapest = assists.OrderBy(a => a.TotalDeltaV).FirstOrDefault();
            var fitting = assists.Where(a => !(a.TripSeconds > MaxTrip(q))).OrderBy(a => a.TotalDeltaV).FirstOrDefault();
            string Via(TransferPlan p) => "via " + s[p.FlybyBody].Name;
            bool Saves(TransferPlan p) => !direct.Ok || p.TotalDeltaV <= (1 - MinSaving) * direct.TotalDeltaV;
            if (fitting == null || (!Saves(fitting) && cheapest != fitting && Saves(cheapest)))
            {
                // Nothing fits the cap, or only the assists that are too long would have saved enough.
                choice.NotOffered = cheapest != null
                    ? $"the best gravity assist ({Via(cheapest)}) is longer than the maximum trip"
                    : "no gravity assist found" + (firstRefusal != null ? $" ({firstRefusal})" : "");
            }
            else if (!direct.Ok)
            {
                choice.Assist = fitting;
                choice.Offered = true;
            }
            else
            {
                choice.Assist = fitting;
                choice.Saving = 1 - fitting.TotalDeltaV / direct.TotalDeltaV;
                choice.Offered = fitting.TotalDeltaV <= (1 - MinSaving) * direct.TotalDeltaV;
                if (!choice.Offered)
                    choice.NotOffered = Inv($"no gravity assist saves 5 % or more (best: {Via(fitting)}, {fitting.TotalDeltaV:F0} m/s, {choice.Saving * 100:F1} % saved)");
            }
            if (choice.BudgetReached && !choice.Offered)
                choice.NotOffered += " (assist search stopped: time budget)";
            choice.SearchMs = watch.Elapsed.TotalMilliseconds;
            string offer = choice.Offered
                ? Inv($"{Via(choice.Assist)}, {choice.Assist.TotalDeltaV:F0} m/s, saves {(double.IsNaN(choice.Saving) ? double.NaN : choice.Saving * 100):F1} %")
                : "none: " + choice.NotOffered;
            string pre = choice.Preselected == choice.Assist && choice.Assist != null ? "Via " + s[choice.Assist.FlybyBody].Name : "Direct";
            Say($"offer: {offer}; preselected {pre}; total {choice.SearchMs:F0} ms{(choice.BudgetReached ? ", time budget reached" : "")}");
            progress?.Report(1);
            return choice;
        }

        static double MaxTrip(TransferRequest q) => double.IsNaN(q.MaxTripSeconds) ? double.PositiveInfinity : q.MaxTripSeconds;

        // The children of the common ancestor that can be flown by: not the target's branch, not the
        // ancestor itself. Empty unless the case is siblings or different branches.
        public static List<int> SiblingBodies(BodySystem s, TransferRequest q)
        {
            var list = new List<int>();
            if (q.FromBody == q.ToBody)
                return list;
            int a = s.CommonAncestor(q.FromBody, q.ToBody);
            if (a == q.FromBody || a == q.ToBody)
                return list;
            int cT = q.ToBody;
            while (s[cT].ParentIndex != a)
                cT = s[cT].ParentIndex;
            for (int b = 0; b < s.Count; b++)
                if (s[b].ParentIndex == a && b != cT)
                    list.Add(b);
            return list;
        }

        // The moons of the vessel's body, when it orbits a child of the common ancestor.
        public static List<int> MoonBodies(BodySystem s, TransferRequest q)
        {
            var list = new List<int>();
            if (q.FromBody == q.ToBody)
                return list;
            int a = s.CommonAncestor(q.FromBody, q.ToBody);
            if (a == q.FromBody || a == q.ToBody || s[q.FromBody].ParentIndex != a)
                return list;
            for (int b = 0; b < s.Count; b++)
                if (s[b].ParentIndex == q.FromBody)
                    list.Add(b);
            return list;
        }

        // The burns of the departure: everything before the first step around another body than
        // the vessel's own and its moons.
        static double DepartureCost(TransferPlan plan, BodySystem s)
        {
            int from = plan.Request?.FromBody ?? -1;
            return plan.Steps.TakeWhile(st => st.Body == from || (st.Body >= 0 && s[st.Body].ParentIndex == from))
                .Where(st => st.Kind != StepKind.Escape).Sum(st => st.DeltaVMagnitude);
        }

        static TransferRequest Copy(TransferRequest q) => new TransferRequest
        {
            FromBody = q.FromBody, ToBody = q.ToBody, R0 = q.R0, V0 = q.V0, UT0 = q.UT0, Arrival = q.Arrival,
            ArrivalPeriapsisRadius = q.ArrivalPeriapsisRadius, ArrivalApoapsisRadius = q.ArrivalApoapsisRadius,
            ArrivalInclinationDeg = q.ArrivalInclinationDeg, MaxTripSeconds = q.MaxTripSeconds, ArrivalPlaneNormal = q.ArrivalPlaneNormal,
        };

        static IProgress<float> Scaled(IProgress<float> progress, float from, float to) =>
            progress == null ? null : new ScaledProgress(progress, from, to);

        sealed class ScaledProgress : IProgress<float>
        {
            readonly IProgress<float> _inner;
            readonly float _from, _to;
            public ScaledProgress(IProgress<float> inner, float from, float to) { _inner = inner; _from = from; _to = to; }
            public void Report(float value) => _inner.Report(_from + (_to - _from) * value);
        }

        static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}

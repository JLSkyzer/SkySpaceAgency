using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using K2D2.OrbitPlanning;
using K2D2.Transfer;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Rendezvous
{
    // PlaneMatch: a burn at the relative node that puts our orbit in the target's plane.
    // Phasing: the intercept burn when the intercept takes one or more full revolutions, on a lower
    // orbit to catch up or a higher one to fall back. Intercept: the same burn with no full revolution.
    // Correction: a placeholder halfway (Δv 0), solved closed-loop in flight. Brake: cancels the
    // relative speed at the stand-off point.
    public enum RvStepKind { PlaneMatch, Phasing, Intercept, Correction, Brake }

    public class RvStep
    {
        public RvStepKind Kind;
        public double UT;
        public Vector3d DeltaV = Vector3d.zero; // inertial, in the body's frame
        public double DeltaVMagnitude;
        public string Explanation = "";
    }

    public class RvPlan
    {
        public bool Ok;
        public string Refusal = "";
        public List<RvStep> Steps = new List<RvStep>(); // in UT order
        public double TotalDeltaV;
        public double ArrivalUT = double.NaN;          // at the stand-off point, the brake
        public int Revolutions;                         // full revolutions of the intercept orbit
    }

    // Rendezvous with a vessel orbiting the same body: an optional plane match, then the cheapest
    // Lambert intercept (multi-revolution, both branches) to a point 'standoff' metres short of the
    // target on its orbit, then a brake there. Pure code: no game API, safe on a worker thread.
    public static class RendezvousPlanner
    {
        public const double CloseRange = 2000;            // m: closer than this, brake only
        public const double PlaneMatchThresholdDeg = 0.05;
        public const int RevolutionCap = 10;
        // Seconds between the plan (or the plane match) and the next burn, to turn the vessel.
        public const double MinLead = 60;

        const int DeparturePeriods = 2;                   // the departure window, in vessel periods
        const int DepartureSamplesPerPeriod = 24;
        const int TofSamples = 48;                        // per revolution count
        const int RefinedCandidates = 8;
        const double ArrivalTolerance = 1;               // m: a Lambert orbit must pass this close to the aim

        public static RvPlan Plan(Vector3d rV, Vector3d vV, Vector3d rT, Vector3d vT, double mu, double ut0,
            double bodyLowOrbitRadius, double standoff = 200, int maxRevolutions = 10, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var plan = new RvPlan();
            if (!(mu > 0))
                return Refuse(plan, "No gravitational parameter for the body");

            double distance = (rT - rV).magnitude;
            if (distance < CloseRange)
            {
                Vector3d brake = vT - vV;
                plan.Steps.Add(new RvStep
                {
                    Kind = RvStepKind.Brake,
                    UT = ut0,
                    DeltaV = brake,
                    DeltaVMagnitude = brake.magnitude,
                    Explanation = Text($"Brake: the target is {distance:F0} m away at {brake.magnitude:F1} m/s relative speed, cancel it first"),
                });
                return Finish(plan, ut0, 0);
            }

            var elV = OrbitMath.Elements(rV, vV, mu);
            var elT = OrbitMath.Elements(rT, vT, mu);
            if (!elV.IsClosed)
                return Refuse(plan, "Our vessel is not on a closed orbit");
            if (!elT.IsClosed)
                return Refuse(plan, "The target is not on a closed orbit");
            maxRevolutions = Math.Max(0, Math.Min(RevolutionCap, maxRevolutions));

            // 1. Plane match.
            Vector3d r0 = rV, v0 = vV;
            double utStart = ut0;
            double relIncl = Angle(elV.AngularMomentum, elT.AngularMomentum);
            if (relIncl > PlaneMatchThresholdDeg * Math.PI / 180)
            {
                var pm = PlaneMatch(rV, vV, mu, ut0, elV, elT, relIncl, out r0, out v0);
                plan.Steps.Add(pm);
                utStart = pm.UT;
            }

            // 2–5. The cheapest intercept and brake.
            var search = new Search(r0, v0, utStart, rT, vT, ut0, mu, elT, bodyLowOrbitRadius, standoff);
            var best = search.Run(maxRevolutions, elV.Period, ct);
            if (best == null)
                return Refuse(plan, Text($"No intercept within {maxRevolutions} revolution(s) keeps the periapsis above {bodyLowOrbitRadius / 1e3:F0} km"));

            double arrival = best.T + best.Tof;
            bool lower = best.TransferSma < elT.SemiMajorAxis;
            plan.Steps.Add(new RvStep
            {
                Kind = best.Rev > 0 ? RvStepKind.Phasing : RvStepKind.Intercept,
                UT = best.T,
                DeltaV = best.Dv1,
                DeltaVMagnitude = best.Dv1.magnitude,
                Explanation = best.Rev > 0
                    ? Text($"Phasing: {best.Rev} revolution(s) on a {(lower ? "lower orbit to catch up with" : "higher orbit to fall back behind")} the target, then meet it {standoff:F0} m short of it")
                    : Text($"Intercept: meet the target {standoff:F0} m short of it in {best.Tof / 60:F0} min"),
            });
            plan.Steps.Add(new RvStep
            {
                Kind = RvStepKind.Correction,
                UT = 0.5 * (best.T + arrival),
                Explanation = "Correction halfway, solved in flight from the actual state",
            });
            plan.Steps.Add(new RvStep
            {
                Kind = RvStepKind.Brake,
                UT = arrival,
                DeltaV = best.Dv2,
                DeltaVMagnitude = best.Dv2.magnitude,
                Explanation = Text($"Brake: cancel the {best.Dv2.magnitude:F1} m/s relative speed {standoff:F0} m from the target"),
            });
            return Finish(plan, arrival, best.Rev);
        }

        // At the relative node that costs the least (the sooner on a tie), turn the velocity about
        // the radius so that the orbit's normal becomes the target's.
        static RvStep PlaneMatch(Vector3d rV, Vector3d vV, double mu, double ut0, OrbitElements elV, OrbitElements elT,
            double relIncl, out Vector3d rAfter, out Vector3d vAfter)
        {
            Vector3d hV = elV.AngularMomentum.normalized, hT = elT.AngularMomentum.normalized;
            Vector3d node = Vector3d.Cross(hV, hT).normalized;
            RvStep best = null;
            rAfter = rV;
            vAfter = vV;
            foreach (var dir in new[] { node, -1 * node })
            {
                double dt = OrbitMath.TimeToDirection(rV, vV, mu, dir);
                if (dt < MinLead)
                    dt += elV.Period;
                KeplerPropagator.Propagate(rV, vV, mu, dt, out var r, out var v);
                Vector3d axis = r.normalized;
                double alpha = Math.Atan2(Vector3d.Dot(Vector3d.Cross(hV, hT), axis), Vector3d.Dot(hV, hT));
                Vector3d vNew = OrbitMath.Rotate(v, axis, alpha);
                Vector3d dv = vNew - v;
                double ut = ut0 + dt;
                if (best == null || dv.magnitude < best.DeltaVMagnitude - 1e-3
                    || (dv.magnitude <= best.DeltaVMagnitude + 1e-3 && ut < best.UT))
                {
                    best = new RvStep
                    {
                        Kind = RvStepKind.PlaneMatch,
                        UT = ut,
                        DeltaV = dv,
                        DeltaVMagnitude = dv.magnitude,
                        Explanation = Text($"Plane match: {relIncl * 180 / Math.PI:F2}° relative inclination, at the relative node"),
                    };
                    rAfter = r;
                    vAfter = vNew;
                }
            }
            return best;
        }

        class Candidate
        {
            public double T, Tof, Cost, TransferSma;
            public int Rev;
            public bool LongBranch;
            public Vector3d Dv1, Dv2;
        }

        // The grid over departure UT × time of flight for each revolution count and branch, then
        // Nelder–Mead on the cheapest cells. Lambert runs in the target's orbital frame (+z along its
        // angular momentum), where "prograde" is well defined for any inclination.
        class Search
        {
            readonly Vector3d r0, v0, rT, vT, e1, e2, e3;
            readonly double utStart, ut0, mu, low, standoff, periodT;
            double tMin, tMax;

            public Search(Vector3d r0, Vector3d v0, double utStart, Vector3d rT, Vector3d vT, double ut0, double mu,
                OrbitElements elT, double low, double standoff)
            {
                this.r0 = r0; this.v0 = v0; this.utStart = utStart;
                this.rT = rT; this.vT = vT; this.ut0 = ut0;
                this.mu = mu; this.low = low; this.standoff = standoff;
                periodT = elT.Period;
                e3 = elT.AngularMomentum.normalized;
                e1 = (rT - Vector3d.Dot(rT, e3) * e3).normalized;
                e2 = Vector3d.Cross(e3, e1);
            }

            Vector3d ToFrame(Vector3d v) => new Vector3d(Vector3d.Dot(v, e1), Vector3d.Dot(v, e2), Vector3d.Dot(v, e3));
            Vector3d FromFrame(Vector3d v) => v.x * e1 + v.y * e2 + v.z * e3;

            // The time-of-flight window for 'rev' full revolutions: 0.3 to 1.5 target periods past
            // them, widened down to half a period below them for the faster, lower phasing orbits.
            void TofWindow(int rev, out double lo, out double hi)
            {
                lo = Math.Max(0.3, rev - 0.5) * periodT;
                hi = (rev + 1.5) * periodT;
            }

            public Candidate Run(int maxRevolutions, double periodV, CancellationToken ct)
            {
                tMin = utStart + MinLead;
                tMax = tMin + DeparturePeriods * periodV;
                int nDep = DeparturePeriods * DepartureSamplesPerPeriod;
                double dt = (tMax - tMin) / nDep;

                // Best cell per (revolutions, branch).
                var cells = new List<(int rev, bool longBranch, double t, double tof, double cost)>();
                var bestCost = new double[maxRevolutions + 1, 2];
                var bestT = new double[maxRevolutions + 1, 2];
                var bestTof = new double[maxRevolutions + 1, 2];
                for (int k = 0; k <= maxRevolutions; k++)
                    for (int b = 0; b < 2; b++)
                        bestCost[k, b] = double.PositiveInfinity;

                for (int i = 0; i <= nDep; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    double t = tMin + i * dt;
                    KeplerPropagator.Propagate(r0, v0, mu, t - utStart, out var rd, out var vd);
                    for (int k = 0; k <= maxRevolutions; k++)
                    {
                        TofWindow(k, out double lo, out double hi);
                        for (int j = 0; j <= TofSamples; j++)
                        {
                            double tof = lo + (hi - lo) * j / TofSamples;
                            KeplerPropagator.Propagate(rT, vT, mu, t + tof - ut0, out var ra, out var va);
                            for (int b = 0; b < (k == 0 ? 1 : 2); b++)
                            {
                                double c = Cost(rd, vd, ra, va, tof, k, b == 1, out _);
                                if (c < bestCost[k, b])
                                {
                                    bestCost[k, b] = c;
                                    bestT[k, b] = t;
                                    bestTof[k, b] = tof;
                                }
                            }
                        }
                    }
                }
                for (int k = 0; k <= maxRevolutions; k++)
                    for (int b = 0; b < 2; b++)
                        if (!double.IsInfinity(bestCost[k, b]))
                            cells.Add((k, b == 1, bestT[k, b], bestTof[k, b], bestCost[k, b]));
                cells.Sort((p, q) => p.cost.CompareTo(q.cost));

                Candidate best = null;
                for (int n = 0; n < Math.Min(RefinedCandidates, cells.Count); n++)
                {
                    var cell = cells[n];
                    TofWindow(cell.rev, out double lo, out double hi);
                    double x = cell.t, y = cell.tof;
                    double dTof = (hi - lo) / TofSamples;
                    Func<double, double, double> f = (a, b) => Evaluate(a, b, cell.rev, cell.longBranch, null);
                    // Twice: the second restart from the first's best point polishes narrow valleys.
                    GridRefiner.NelderMead(f, ref x, ref y, 0.5 * dt, 0.5 * dTof, ct);
                    double cost = GridRefiner.NelderMead(f, ref x, ref y, 0.05 * dt, 0.05 * dTof, ct);
                    if (!(cost <= cell.cost))
                    {
                        x = cell.t;
                        y = cell.tof;
                    }
                    var candidate = new Candidate { Rev = cell.rev, LongBranch = cell.longBranch };
                    candidate.Cost = Evaluate(x, y, cell.rev, cell.longBranch, candidate);
                    if (!double.IsInfinity(candidate.Cost) && (best == null || candidate.Cost < best.Cost))
                        best = candidate;
                }
                return best;
            }

            // Cost at (departure UT, time of flight), ∞ outside the search domain; fills 'into' when given.
            double Evaluate(double t, double tof, int rev, bool longBranch, Candidate into)
            {
                TofWindow(rev, out double lo, out double hi);
                if (!(t >= tMin && t <= tMax && tof >= lo && tof <= hi))
                    return double.PositiveInfinity;
                KeplerPropagator.Propagate(r0, v0, mu, t - utStart, out var rd, out var vd);
                KeplerPropagator.Propagate(rT, vT, mu, t + tof - ut0, out var ra, out var va);
                double cost = Cost(rd, vd, ra, va, tof, rev, longBranch, out var legs);
                if (into != null && !double.IsInfinity(cost))
                {
                    into.T = t;
                    into.Tof = tof;
                    into.Dv1 = legs.dv1;
                    into.Dv2 = legs.dv2;
                    into.TransferSma = legs.sma;
                }
                return cost;
            }

            // Intercept + brake Δv from the departure state (rd, vd) to the point 'standoff' short of
            // the target's arrival state (ra, va). ∞ when there is no solution or the periapsis is low.
            double Cost(Vector3d rd, Vector3d vd, Vector3d ra, Vector3d va, double tof, int rev, bool longBranch,
                out (Vector3d dv1, Vector3d dv2, double sma) legs)
            {
                legs = default;
                Vector3d aim = ra - standoff * va.normalized;
                if (!Lambert.Solve(ToFrame(rd), ToFrame(aim), tof, mu, true, rev, longBranch, out var v1f, out _))
                    return double.PositiveInfinity;
                Vector3d v1 = FromFrame(v1f);
                // Defence in depth: keep only orbits that really reach the aim point in 'tof'.
                // Lambert used to report wrong multi-revolution roots near a transfer angle of 0
                // (fixed 2026-10-24); this check costs one propagation, about 4 % of a Lambert solve.
                KeplerPropagator.Propagate(rd, v1, mu, tof, out var rEnd, out var v2);
                if (!((rEnd - aim).magnitude <= ArrivalTolerance))
                    return double.PositiveInfinity;
                var el = OrbitMath.Elements(rd, v1, mu);
                if (!(el.PeriapsisRadius >= low))
                    return double.PositiveInfinity;
                legs = (v1 - vd, va - v2, el.SemiMajorAxis);
                double cost = legs.dv1.magnitude + legs.dv2.magnitude;
                return double.IsNaN(cost) ? double.PositiveInfinity : cost;
            }
        }

        static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

        static double Angle(Vector3d a, Vector3d b) =>
            Math.Atan2(Vector3d.Cross(a, b).magnitude, Vector3d.Dot(a, b));

        static RvPlan Finish(RvPlan plan, double arrival, int revolutions)
        {
            plan.Ok = true;
            plan.ArrivalUT = arrival;
            plan.Revolutions = revolutions;
            foreach (var s in plan.Steps)
                plan.TotalDeltaV += s.DeltaVMagnitude;
            return plan;
        }

        static RvPlan Refuse(RvPlan plan, string why)
        {
            plan.Ok = false;
            plan.Refusal = why;
            plan.Steps.Clear();
            return plan;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using KSP.Sim;

namespace K2D2.Transfer
{
    // One point of the assist search: the three dates, the Lambert choices of both legs, and the cost
    // (leave + flyby burn + arrive), with the flyby periapsis and its plane normal (common frame).
    public struct AssistCandidate
    {
        public double DepartureUT, FlybyUT, ArrivalUT, Cost, FlybyDeltaV, FlybyPeriapsis;
        public bool Prograde1, Prograde2;
        public int Revolutions1;
        public bool LongPeriod1; // the branch of a multi-revolution leg 1
        public Vector3d Normal;
        public double TripSeconds => ArrivalUT - DepartureUT;
    }

    // The inputs of the assist grid, as delegates: the grid knows no body tree. States are relative
    // to the common ancestor, in the common frame.
    public sealed class AssistLegs
    {
        public Func<double, (Vector3d r, Vector3d v)> DepartureState, FlybyState, ArrivalState;
        public Func<double, double> LeaveCost;  // from the departure excess magnitude
        public Func<double, double> ArriveCost; // from the arrival excess magnitude
        public double Mu, FlybyMu, FlybyFloor, FlybyCeiling;
        public double Tof1Lo, Tof1Hi, Tof2Lo, Tof2Hi, FlybyLo, FlybyHi;
        public double MaxTrip = double.PositiveInfinity;
        // The departure date must lie here (the climb lead, the search window).
        public double DepartureLo = double.NegativeInfinity, DepartureHi = double.PositiveInfinity;
        public bool[] Directions1 = { true }, Directions2 = { true };
        public int[] Revolutions1 = { 0 };
        // A resume: the departure state is propagated from a fixed state, and leaving costs |v1 − v|.
        public bool FixedDeparture;
    }

    // A decoupled 3-D grid over departure × flyby × arrival dates: on a lattice of flyby dates, one
    // set of leg-1 Lambert solves (one per flight 1) and one of leg 2 (one per flight 2) meet, and
    // each pair is costed with the powered flyby. The best discrete local minima are polished by a
    // 3-D Nelder–Mead on the continuous cost. Pure code; logs nothing (the callers log).
    public static class AssistPorkchop
    {
        public const int FlybySamples = 160, Tof1Samples = 24, Tof2Samples = 24, Refined = 4;

        // Counters of the last search on this thread (the callers' log lines).
        [ThreadStatic] public static int LastLambertSolves, LastFeasible, LastCells;

        struct In { public Vector3d Vinf; public double Leave; public bool Dir; public int Rev; public bool Long; public double Dep; }
        struct Out { public Vector3d Vinf; public double Arrive; public bool Dir; public double Arr; }

        public static List<AssistCandidate> Search(AssistLegs legs, CancellationToken ct, IProgress<float> progress, float from, float to)
        {
            int nK = FlybySamples, nI = Tof1Samples, nJ = Tof2Samples;
            var grid = new double[nK, nI, nJ];
            var choice1 = new int[nK, nI, nJ];
            var choice2 = new int[nK, nI, nJ];
            int solves = 0, feasible = 0;
            var ins = new List<In>();
            var outs = new List<Out>();
            var inOpts = new List<In>[nI];
            var outOpts = new List<Out>[nJ];
            for (int k = 0; k < nK; k++)
            {
                ct.ThrowIfCancellationRequested();
                double tF = Lerp(legs.FlybyLo, legs.FlybyHi, k, nK);
                var (rB, vB) = legs.FlybyState(tF);
                for (int i = 0; i < nI; i++)
                {
                    inOpts[i] = LegIn(legs, rB, vB, tF, Lerp(legs.Tof1Lo, legs.Tof1Hi, i, nI), ref solves);
                }
                for (int j = 0; j < nJ; j++)
                    outOpts[j] = LegOut(legs, rB, vB, tF, Lerp(legs.Tof2Lo, legs.Tof2Hi, j, nJ), ref solves);
                for (int i = 0; i < nI; i++)
                    for (int j = 0; j < nJ; j++)
                    {
                        double best = double.PositiveInfinity;
                        int b1 = -1, b2 = -1;
                        for (int a = 0; a < inOpts[i].Count; a++)
                            for (int b = 0; b < outOpts[j].Count; b++)
                            {
                                var p = inOpts[i][a];
                                var q = outOpts[j][b];
                                if (q.Arr - p.Dep > legs.MaxTrip)
                                    continue;
                                double ends = p.Leave + q.Arrive;
                                if (!(ends < best))
                                    continue;
                                var f = FlybyModel.Solve(p.Vinf, q.Vinf, legs.FlybyMu, legs.FlybyFloor, legs.FlybyCeiling);
                                if (!f.Ok)
                                    continue;
                                double c = ends + f.DeltaV;
                                if (c < best)
                                {
                                    best = c;
                                    b1 = a;
                                    b2 = b;
                                }
                            }
                        grid[k, i, j] = best;
                        if (b1 >= 0)
                        {
                            feasible++;
                            choice1[k, i, j] = ins.Count;
                            ins.Add(inOpts[i][b1]);
                            choice2[k, i, j] = outs.Count;
                            outs.Add(outOpts[j][b2]);
                        }
                    }
                progress?.Report(from + (to - from) * (k + 1) / nK);
            }
            LastLambertSolves = solves;
            LastFeasible = feasible;
            LastCells = nK * nI * nJ;

            // The discrete local minima (26 neighbours), cheapest first.
            var minima = new List<(int k, int i, int j)>();
            for (int k = 0; k < nK; k++)
                for (int i = 0; i < nI; i++)
                    for (int j = 0; j < nJ; j++)
                    {
                        double c = grid[k, i, j];
                        if (double.IsInfinity(c) || double.IsNaN(c))
                            continue;
                        bool isMin = true;
                        for (int dk = -1; dk <= 1 && isMin; dk++)
                            for (int di = -1; di <= 1 && isMin; di++)
                                for (int dj = -1; dj <= 1; dj++)
                                {
                                    int a = k + dk, b = i + di, d = j + dj;
                                    if ((dk != 0 || di != 0 || dj != 0) && a >= 0 && a < nK && b >= 0 && b < nI && d >= 0 && d < nJ
                                        && grid[a, b, d] < c)
                                    {
                                        isMin = false;
                                        break;
                                    }
                                }
                        if (isMin)
                            minima.Add((k, i, j));
                    }
            minima.Sort((p, q) => grid[p.k, p.i, p.j].CompareTo(grid[q.k, q.i, q.j]));

            double dF = (legs.FlybyHi - legs.FlybyLo) / (nK - 1);
            double d1 = (legs.Tof1Hi - legs.Tof1Lo) / (nI - 1), d2 = (legs.Tof2Hi - legs.Tof2Lo) / (nJ - 1);
            var result = new List<AssistCandidate>();
            foreach (var (k, i, j) in minima.Take(Refined))
            {
                ct.ThrowIfCancellationRequested();
                var p = ins[choice1[k, i, j]];
                var q = outs[choice2[k, i, j]];
                double x = Lerp(legs.FlybyLo, legs.FlybyHi, k, nK), y = Lerp(legs.Tof1Lo, legs.Tof1Hi, i, nI), z = Lerp(legs.Tof2Lo, legs.Tof2Hi, j, nJ);
                double gx = x, gy = y, gz = z;
                double Cost(double tF, double tof1, double tof2) =>
                    Evaluate(legs, tF, tof1, tof2, p.Dir, p.Rev, p.Long, q.Dir, out _);
                double f = GridRefiner.NelderMead3(Cost, ref x, ref y, ref z, 0.5 * dF, 0.5 * d1, 0.5 * d2, ct);
                if (!(f <= grid[k, i, j]))
                {
                    x = gx;
                    y = gy;
                    z = gz;
                }
                Evaluate(legs, x, y, z, p.Dir, p.Rev, p.Long, q.Dir, out var cand);
                if (!double.IsInfinity(cand.Cost) && !double.IsNaN(cand.Cost))
                    result.Add(cand);
            }
            return result.OrderBy(c => c.Cost).ToList();
        }

        // The continuous cost of one point, with fixed Lambert choices; +∞ outside the domain.
        public static double Evaluate(AssistLegs legs, double tF, double tof1, double tof2, bool dir1, int rev1, bool long1, bool dir2,
            out AssistCandidate cand)
        {
            cand = new AssistCandidate { Cost = double.PositiveInfinity, FlybyUT = tF, DepartureUT = tF - tof1, ArrivalUT = tF + tof2,
                Prograde1 = dir1, Prograde2 = dir2, Revolutions1 = rev1, LongPeriod1 = long1 };
            if (!(tF >= legs.FlybyLo && tF <= legs.FlybyHi && tof1 >= legs.Tof1Lo && tof1 <= legs.Tof1Hi
                  && tof2 >= legs.Tof2Lo && tof2 <= legs.Tof2Hi) || tof1 + tof2 > legs.MaxTrip
                || tF - tof1 < legs.DepartureLo || tF - tof1 > legs.DepartureHi)
                return double.PositiveInfinity;
            var (rB, vB) = legs.FlybyState(tF);
            var (r0, v0) = legs.DepartureState(tF - tof1);
            if (!Lambert.Solve(r0, rB, tof1, legs.Mu, dir1, rev1, long1, out var v1, out var v2))
                return double.PositiveInfinity;
            var (rT, vT) = legs.ArrivalState(tF + tof2);
            if (!Lambert.Solve(rB, rT, tof2, legs.Mu, dir2, 0, false, out var w1, out var w2))
                return double.PositiveInfinity;
            var f = FlybyModel.Solve(v2 - vB, w1 - vB, legs.FlybyMu, legs.FlybyFloor, legs.FlybyCeiling);
            if (!f.Ok)
                return double.PositiveInfinity;
            double leave = legs.FixedDeparture ? (v1 - v0).magnitude : legs.LeaveCost((v1 - v0).magnitude);
            cand.Cost = leave + f.DeltaV + legs.ArriveCost((w2 - vT).magnitude);
            cand.FlybyDeltaV = f.DeltaV;
            cand.FlybyPeriapsis = f.PeriapsisRadius;
            cand.Normal = f.Normal;
            return cand.Cost;
        }

        static List<In> LegIn(AssistLegs legs, Vector3d rB, Vector3d vB, double tF, double tof1, ref int solves)
        {
            var list = new List<In>();
            double dep = tF - tof1;
            if (dep < legs.DepartureLo || dep > legs.DepartureHi)
                return list;
            var (r0, v0) = legs.DepartureState(dep);
            foreach (bool d in legs.Directions1)
                foreach (int rev in legs.Revolutions1)
                    foreach (bool lp in rev == 0 ? new[] { false } : new[] { false, true })
                    {
                        solves++;
                        if (!Lambert.Solve(r0, rB, tof1, legs.Mu, d, rev, lp, out var v1, out var v2))
                            continue;
                        double leave = legs.FixedDeparture ? (v1 - v0).magnitude : legs.LeaveCost((v1 - v0).magnitude);
                        if (double.IsNaN(leave) || double.IsInfinity(leave))
                            continue;
                        list.Add(new In { Vinf = v2 - vB, Leave = leave, Dir = d, Rev = rev, Long = lp, Dep = dep });
                    }
            return list;
        }

        static List<Out> LegOut(AssistLegs legs, Vector3d rB, Vector3d vB, double tF, double tof2, ref int solves)
        {
            var list = new List<Out>();
            var (rT, vT) = legs.ArrivalState(tF + tof2);
            foreach (bool d in legs.Directions2)
            {
                solves++;
                if (!Lambert.Solve(rB, rT, tof2, legs.Mu, d, 0, false, out var w1, out var w2))
                    continue;
                double arrive = legs.ArriveCost((w2 - vT).magnitude);
                if (double.IsNaN(arrive) || double.IsInfinity(arrive))
                    continue;
                list.Add(new Out { Vinf = w1 - vB, Arrive = arrive, Dir = d, Arr = tF + tof2 });
            }
            return list;
        }

        static double Lerp(double lo, double hi, int i, int n) => n <= 1 ? lo : lo + (hi - lo) * i / (n - 1);
    }
}

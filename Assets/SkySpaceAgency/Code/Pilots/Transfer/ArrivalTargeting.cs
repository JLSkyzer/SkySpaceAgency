using System;
using System.Collections.Generic;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Transfer
{
    // Solves a trajectory correction: the impulse, now, that puts the arrival periapsis at the target
    // at the wanted radius (and, when asked, its orbit at the wanted inclination). The aim is the
    // B-plane vector of the arrival hyperbola, B = S × h / v∞ (S the incoming asymptote, |B| the
    // impact parameter), which is close to linear in the Δv, unlike the periapsis radius itself.
    // Differential correction: finite differences through PatchedPropagator, then a damped,
    // minimum-norm Newton step. Without an encounter, the vessel is first brought into the target's
    // SOI: toward the target's position at the planned arrival when one is given, otherwise by
    // shrinking the closest approach. Pure code: no game API, safe on a worker thread.
    public static class ArrivalTargeting
    {
        // A solved correction below this (m/s) is not worth flying: the pilot skips it.
        public const double SkipThreshold = 1.0;

        // No correction, from a miss or not, may cost more than this (m/s).
        public const double MaxDeltaV = 300;

        const int MaxIterations = 12;
        const double Step = 1e-4;          // m/s, finite-difference step: early on, 1 m/s moves B by ~2e7 m
        const double MaxNewtonStep = 100;  // m/s, damping: the longest step tried at once
        const double BTolerance = 100;     // m on the B-plane vector: well under 1 km on the periapsis
        const int MaxHalvings = 6;
        const int SamplesForMiss = 400;

        enum Mode { Miss, Encounter }

        // One trajectory, flown with a given Δv.
        struct Eval
        {
            public Mode Mode;
            public double[] Residual; // in m: what is driven to zero, and Norm its size
            public double Norm;
            // Periapsis only, on a hyperbolic arrival: the B vector on the B-plane axes, and the impact
            // parameter wanted. The Newton step then aims B at the point of the circle |B| = BWanted
            // that costs the least Δv; |B| − BWanted alone, along its gradient, crawls when the B-plane
            // map is stretched (early corrections), since moving B along one axis cannot shrink the other.
            public double[] Plane;
            public double BWanted;
            public double PeriapsisRadius, InclinationDeg;
            public bool Valid => Residual != null;
        }

        // r, v: the vessel's state relative to 'body' at 'ut'. Returns false, with a zero Δv and NaN
        // predictions, when the target cannot be met within MaxDeltaV. Otherwise the best Δv found
        // (converged unless the aim cannot be reached, for instance an inclination below the
        // asymptote's declination) and the periapsis radius and inclination (°) it gives at the target.
        // On a miss, this aims at the deepest closest approach before horizonUT, which may be another
        // pass than the planned one: the pilot uses the overload with the planned arrival.
        public static bool Solve(BodySystem s, int body, Vector3d r, Vector3d v, double ut, int targetBody,
            double wantedPeriapsisRadius, double wantedInclinationDeg, double horizonUT,
            out Vector3d deltaV, out double predictedPeriapsisRadius, out double predictedInclinationDeg) =>
            Solve(s, body, r, v, ut, targetBody, wantedPeriapsisRadius, wantedInclinationDeg, horizonUT, double.NaN,
                out deltaV, out predictedPeriapsisRadius, out predictedInclinationDeg);

        // The same, keeping to the planned pass: on a miss, the vessel is aimed at the target's position
        // at plannedArrivalUT (the plan's arrival, NaN for none). A trajectory that drifted onto another
        // pass of the target, earlier or later, comes back to the planned one, whose excess speed (and
        // so capture cost) the plan was built on. Once the SOI is met, the arrival time is free. When
        // the planned pass cannot be met within MaxDeltaV (late in the flight, a drift is costly to
        // undo), the nearest pass is aimed at instead, as without a planned arrival.
        public static bool Solve(BodySystem s, int body, Vector3d r, Vector3d v, double ut, int targetBody,
            double wantedPeriapsisRadius, double wantedInclinationDeg, double horizonUT, double plannedArrivalUT,
            out Vector3d deltaV, out double predictedPeriapsisRadius, out double predictedInclinationDeg)
        {
            deltaV = Vector3d.zero;
            predictedPeriapsisRadius = double.NaN;
            predictedInclinationDeg = double.NaN;
            if (s == null || targetBody < 0 || targetBody >= s.Count || !(wantedPeriapsisRadius > 0) || !(horizonUT > ut))
                return false;

            var problem = new Problem
            {
                S = s, Body = body, R = r, V = v, UT = ut, Target = targetBody, HorizonUT = horizonUT,
                WantedPeriapsis = wantedPeriapsisRadius, WantedInclinationDeg = wantedInclinationDeg,
                PlannedArrivalUT = plannedArrivalUT,
            };
            if (Run(problem, out deltaV, out predictedPeriapsisRadius, out predictedInclinationDeg))
                return true;
            if (double.IsNaN(plannedArrivalUT))
                return false;
            problem.PlannedArrivalUT = double.NaN;
            return Run(problem, out deltaV, out predictedPeriapsisRadius, out predictedInclinationDeg);
        }

        static bool Run(Problem problem, out Vector3d deltaV, out double predictedPeriapsisRadius, out double predictedInclinationDeg)
        {
            deltaV = Vector3d.zero;
            predictedPeriapsisRadius = double.NaN;
            predictedInclinationDeg = double.NaN;

            var dv = Vector3d.zero;
            int branch = 0; // which of the two B-plane directions meets the inclination, chosen once
            var current = problem.Evaluate(dv, ref branch);
            if (!current.Valid)
                return false;

            for (int iteration = 0; iteration < MaxIterations; iteration++)
            {
                if (current.Mode == Mode.Encounter && current.Norm < BTolerance)
                    break;
                if (!Jacobian(problem, dv, current, branch, out var jacobian))
                    break;
                var f = current.Plane != null ? PlaneError(jacobian, current) : current.Residual;
                if (f == null || !NewtonStep(jacobian, f, out var step))
                    break;
                if (step.magnitude > MaxNewtonStep)
                    step = step * (MaxNewtonStep / step.magnitude);

                // Damped: halve the step until the trajectory improves (an encounter beats a miss).
                bool improved = false;
                for (int k = 0; k <= MaxHalvings && !improved; k++, step = 0.5 * step)
                {
                    var trial = dv + step;
                    if (trial.magnitude > MaxDeltaV)
                        trial = trial * (MaxDeltaV / trial.magnitude);
                    int trialBranch = branch;
                    var next = problem.Evaluate(trial, ref trialBranch);
                    if (!next.Valid || !Better(next, current))
                        continue;
                    dv = trial;
                    current = next;
                    branch = trialBranch;
                    improved = true;
                }
                if (!improved)
                    break;
            }

            if (current.Mode != Mode.Encounter)
                return false;
            deltaV = dv;
            predictedPeriapsisRadius = current.PeriapsisRadius;
            predictedInclinationDeg = current.InclinationDeg;
            return true;
        }

        static bool Better(Eval next, Eval current)
        {
            if (next.Mode != current.Mode)
                return next.Mode == Mode.Encounter;
            return next.Norm < current.Norm;
        }

        // Central differences; one-sided where one side changes mode (an encounter lost or found).
        // Of the B-plane vector when the evaluation has one, otherwise of the residual.
        static bool Jacobian(Problem p, Vector3d dv, Eval at, int branch, out double[,] jacobian)
        {
            int m = Differentiated(at).Length;
            jacobian = new double[m, 3];
            for (int axis = 0; axis < 3; axis++)
            {
                var e = new Vector3d(axis == 0 ? Step : 0, axis == 1 ? Step : 0, axis == 2 ? Step : 0);
                int b1 = branch, b2 = branch;
                var plus = p.Evaluate(dv + e, ref b1);
                var minus = p.Evaluate(dv - e, ref b2);
                bool okPlus = Same(plus, at), okMinus = Same(minus, at);
                for (int i = 0; i < m; i++)
                {
                    if (okPlus && okMinus)
                        jacobian[i, axis] = (Differentiated(plus)[i] - Differentiated(minus)[i]) / (2 * Step);
                    else if (okPlus)
                        jacobian[i, axis] = (Differentiated(plus)[i] - Differentiated(at)[i]) / Step;
                    else if (okMinus)
                        jacobian[i, axis] = (Differentiated(at)[i] - Differentiated(minus)[i]) / Step;
                    else
                        return false;
                }
            }
            return true;
        }

        static double[] Differentiated(Eval x) => x.Plane ?? x.Residual;

        static bool Same(Eval x, Eval at) =>
            x.Valid && x.Mode == at.Mode && (x.Plane == null) == (at.Plane == null) && Differentiated(x).Length == Differentiated(at).Length;

        // B − Bw, with Bw the point of the circle |B| = BWanted nearest to B in the Δv metric of the
        // linearized map: the least-norm Δv to reach Bw(φ) is Jᵀ M (Bw − B), M = (J Jᵀ)⁻¹, of squared
        // size (Bw − B)ᵀ M (Bw − B), minimized over φ by a scan, then a finer one.
        static double[] PlaneError(double[,] j, Eval at)
        {
            double a = 0, b = 0, d = 0;
            for (int k = 0; k < 3; k++)
            {
                a += j[0, k] * j[0, k];
                b += j[0, k] * j[1, k];
                d += j[1, k] * j[1, k];
            }
            double det = a * d - b * b;
            if (!(det > 1e-12 * a * d))
                return null;
            double m00 = d / det, m01 = -b / det, m11 = a / det;
            double x0 = at.Plane[0], y0 = at.Plane[1], r = at.BWanted;
            double Cost(double phi)
            {
                double x = r * Math.Cos(phi) - x0, y = r * Math.Sin(phi) - y0;
                return m00 * x * x + 2 * m01 * x * y + m11 * y * y;
            }
            double best = Math.Atan2(y0, x0), width = Math.PI;
            for (int pass = 0; pass < 4; pass++, width /= 36)
            {
                double centre = best, bestCost = Cost(best);
                for (int i = -36; i <= 36; i++)
                {
                    double phi = centre + width * i / 36;
                    double c = Cost(phi);
                    if (c < bestCost)
                    {
                        bestCost = c;
                        best = phi;
                    }
                }
            }
            return new[] { x0 - r * Math.Cos(best), y0 - r * Math.Sin(best) };
        }

        // The minimum-norm step that zeroes the linearized residual: −Jᵀ (J Jᵀ)⁻¹ f, for 1 to 3 rows.
        static bool NewtonStep(double[,] j, double[] f, out Vector3d step)
        {
            step = Vector3d.zero;
            int m = f.Length;
            var row = new Vector3d[m];
            for (int i = 0; i < m; i++)
                row[i] = new Vector3d(j[i, 0], j[i, 1], j[i, 2]);
            // (J Jᵀ) y = f by Gauss-Jordan elimination with partial pivoting.
            var a = new double[m, m + 1];
            double scale = 0;
            for (int i = 0; i < m; i++)
            {
                for (int k = 0; k < m; k++)
                    a[i, k] = Vector3d.Dot(row[i], row[k]);
                a[i, m] = f[i];
                scale = Math.Max(scale, a[i, i]);
            }
            if (!(scale > 0))
                return false;
            for (int c = 0; c < m; c++)
            {
                int pivot = c;
                for (int i = c + 1; i < m; i++)
                    if (Math.Abs(a[i, c]) > Math.Abs(a[pivot, c]))
                        pivot = i;
                if (!(Math.Abs(a[pivot, c]) > 1e-12 * scale))
                    return false; // the rows are (nearly) dependent
                for (int k = 0; k <= m; k++)
                {
                    double t = a[c, k];
                    a[c, k] = a[pivot, k];
                    a[pivot, k] = t;
                }
                for (int i = 0; i < m; i++)
                {
                    if (i == c)
                        continue;
                    double factor = a[i, c] / a[c, c];
                    for (int k = c; k <= m; k++)
                        a[i, k] -= factor * a[c, k];
                }
            }
            for (int i = 0; i < m; i++)
                step -= (a[i, m] / a[i, i]) * row[i];
            return IsFinite(step);
        }

        static bool IsFinite(Vector3d x) =>
            !(double.IsNaN(x.x) || double.IsNaN(x.y) || double.IsNaN(x.z) || double.IsInfinity(x.x) || double.IsInfinity(x.y) || double.IsInfinity(x.z));

        sealed class Problem
        {
            public BodySystem S;
            public int Body, Target;
            public Vector3d R, V;
            public double UT, HorizonUT, WantedPeriapsis, WantedInclinationDeg, PlannedArrivalUT;

            // The residual for the trajectory flown with 'dv': on an encounter, the B-plane error (the
            // impact parameter alone, or the whole vector when an inclination is wanted); on a miss, the
            // vessel's offset from the target at the planned arrival or, without one (or when the vessel
            // is not around the target's parent then), the closest approach beyond a quarter of the
            // target's SOI. 'branch' picks, once, which of the
            // two B-plane directions with the wanted inclination is aimed at: the one nearer to B.
            public Eval Evaluate(Vector3d dv, ref int branch)
            {
                var patches = PatchedPropagator.Propagate(S, Body, R, V + dv, UT, HorizonUT, null);
                var target = S[Target];
                foreach (var p in patches)
                    if (p.Body == Target)
                        return Arrival(p, target, ref branch);

                foreach (var p in patches)
                    if (p.Body == target.ParentIndex && p.StartUT <= PlannedArrivalUT && PlannedArrivalUT <= p.EndUT)
                    {
                        var offset = OffsetAt(p, PlannedArrivalUT);
                        return new Eval
                        {
                            Mode = Mode.Miss, Residual = new[] { offset.x, offset.y, offset.z }, Norm = offset.magnitude,
                            PeriapsisRadius = double.NaN, InclinationDeg = double.NaN,
                        };
                    }

                double miss = double.PositiveInfinity;
                foreach (var p in patches)
                    if (p.Body == target.ParentIndex)
                        miss = Math.Min(miss, p.ClosestBody == Target ? p.ClosestDistance : ClosestApproach(p));
                if (double.IsInfinity(miss))
                    return default;
                var r = new[] { miss - 0.25 * target.Soi };
                return new Eval { Mode = Mode.Miss, Residual = r, Norm = Math.Abs(r[0]), PeriapsisRadius = double.NaN, InclinationDeg = double.NaN };
            }

            Eval Arrival(Patch p, BodyNode target, ref int branch)
            {
                double mu = target.Mu;
                var el = OrbitMath.Elements(p.StartR, p.StartV, mu);
                var eval = new Eval
                {
                    Mode = Mode.Encounter,
                    PeriapsisRadius = p.PeriapsisRadius,
                    InclinationDeg = el.InclinationRad * 180 / Math.PI,
                };
                if (el.IsClosed || !(el.Eccentricity > 1))
                {
                    // Captured on arrival (a very slow approach): no asymptote, aim at the periapsis radius.
                    eval.Residual = new[] { p.PeriapsisRadius - WantedPeriapsis };
                    eval.Norm = Math.Abs(eval.Residual[0]);
                    return eval;
                }

                double vInf = Math.Sqrt(-mu / el.SemiMajorAxis);
                double e = el.Eccentricity;
                var h = el.AngularMomentum;
                var eHat = el.EccentricityVector / e;
                var qHat = Vector3d.Cross(h, eHat).normalized;
                var sHat = (1 / e) * eHat + Math.Sqrt(1 - 1 / (e * e)) * qHat; // incoming asymptote
                var bVec = Vector3d.Cross(sHat, h) / vInf;
                double bWanted = WantedPeriapsis * Math.Sqrt(1 + 2 * mu / (WantedPeriapsis * vInf * vInf));

                var tRaw = Vector3d.Cross(sHat, OrbitMath.North);
                double sinDecl = tRaw.magnitude; // sine of the angle between the asymptote and the axis
                var tHat = sinDecl < 1e-6 ? Vector3d.Cross(sHat, new Vector3d(1, 0, 0)).normalized : tRaw / sinDecl;
                var rHat = Vector3d.Cross(sHat, tHat);
                if (double.IsNaN(WantedInclinationDeg) || sinDecl < 1e-6)
                {
                    eval.Residual = new[] { bVec.magnitude - bWanted };
                    eval.Norm = Math.Abs(eval.Residual[0]);
                    eval.Plane = new[] { Vector3d.Dot(bVec, tHat), Vector3d.Dot(bVec, rHat) };
                    eval.BWanted = bWanted;
                    return eval;
                }

                // ĥ = B̂ × Ŝ, so cos i = B̂·(Ŝ × ẑ) = sinDecl · cos θ, with B̂ = cos θ T̂ + sin θ R̂.
                double cosTheta = Math.Max(-1, Math.Min(1, Math.Cos(WantedInclinationDeg * Math.PI / 180) / sinDecl));
                if (branch == 0)
                    branch = Vector3d.Dot(bVec, rHat) >= 0 ? 1 : -1;
                double sinTheta = branch * Math.Sqrt(1 - cosTheta * cosTheta);
                var error = bVec - bWanted * (cosTheta * tHat + sinTheta * rHat);
                eval.Residual = new[] { Vector3d.Dot(error, tHat), Vector3d.Dot(error, rHat) };
                eval.Norm = Math.Sqrt(eval.Residual[0] * eval.Residual[0] + eval.Residual[1] * eval.Residual[1]);
                return eval;
            }

            // The closest approach to the target over a patch around its parent, when the propagator
            // did not report it (the target out of the conic's reach, or a nearer miss of another body):
            // even sampling, then a golden-section search around the nearest sample.
            double ClosestApproach(Patch p)
            {
                double span = p.EndUT - p.StartUT;
                if (!(span > 0))
                    return Distance(p, p.StartUT);
                double dt = span / SamplesForMiss, best = double.PositiveInfinity, bestT = p.StartUT;
                for (int i = 0; i <= SamplesForMiss; i++)
                {
                    double t = p.StartUT + i * dt, d = Distance(p, t);
                    if (d < best)
                    {
                        best = d;
                        bestT = t;
                    }
                }
                double lo = Math.Max(p.StartUT, bestT - dt), hi = Math.Min(p.EndUT, bestT + dt);
                const double g = 0.6180339887498949;
                double x1 = hi - g * (hi - lo), x2 = lo + g * (hi - lo);
                double f1 = Distance(p, x1), f2 = Distance(p, x2);
                for (int i = 0; i < 60 && hi - lo > 1; i++)
                {
                    if (f1 < f2)
                    {
                        hi = x2; x2 = x1; f2 = f1;
                        x1 = hi - g * (hi - lo); f1 = Distance(p, x1);
                    }
                    else
                    {
                        lo = x1; x1 = x2; f1 = f2;
                        x2 = lo + g * (hi - lo); f2 = Distance(p, x2);
                    }
                }
                return Math.Min(best, Math.Min(f1, f2));
            }

            double Distance(Patch p, double ut) => OffsetAt(p, ut).magnitude;

            // The vessel's position relative to the target at 'ut', within a patch around the target's parent.
            Vector3d OffsetAt(Patch p, double ut)
            {
                Vector3d r = p.StartR;
                if (ut != p.StartUT)
                    KeplerPropagator.Propagate(p.StartR, p.StartV, S[p.Body].Mu, ut - p.StartUT, out r, out _);
                S.StateRelativeToParent(Target, ut, out var tr, out _);
                return r - tr;
            }
        }
    }
}

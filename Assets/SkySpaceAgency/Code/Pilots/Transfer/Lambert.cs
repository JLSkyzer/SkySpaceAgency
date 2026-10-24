using System;
using KSP.Sim;

namespace K2D2.Transfer
{
    // Lambert's problem: the orbit that goes from r1 to r2 in a given time. Universal variables
    // (Curtis, "Orbital Mechanics for Engineering Students", algorithm 5.2), extended to several
    // full revolutions: with N revolutions, z lies in ((2Nπ)², (2(N+1)π)²), where the time of flight
    // has a minimum. Below that minimum there is no solution; above it there are two, one on each
    // side of the minimum: the short-period and the long-period branch.
    // Pure code: no game API, safe on a worker thread.
    public static class Lambert
    {
        const int MaxIterations = 200;
        const double AngleTolerance = 1e-6;
        // Relative time-of-flight residual above which a root is not a solution. 1e-9 of the flight
        // keeps the arrival within centimetres on the Kerbin cases of LambertTests.
        const double TimeTolerance = 1e-9;

        // prograde: the motion is counter-clockwise about +z. Returns false, with zero outputs, when
        // there is no solution or the transfer angle is within 1e-6 rad of 0 or π.
        public static bool Solve(Vector3d r1, Vector3d r2, double tof, double mu, bool prograde,
            int revolutions, bool longPeriodBranch, out Vector3d v1, out Vector3d v2)
        {
            v1 = Vector3d.zero;
            v2 = Vector3d.zero;
            if (!(tof > 0) || !(mu > 0) || double.IsInfinity(tof) || revolutions < 0)
                return false;
            double r1m = r1.magnitude, r2m = r2.magnitude;
            if (!(r1m > 0) || !(r2m > 0) || double.IsInfinity(r1m) || double.IsInfinity(r2m))
                return false;

            Vector3d cross = Vector3d.Cross(r1, r2);
            double theta = Math.Atan2(cross.magnitude, Vector3d.Dot(r1, r2)); // [0, π], precise near both ends
            if (theta < AngleTolerance || Math.PI - theta < AngleTolerance)
                return false;
            if (prograde ? cross.z < 0 : cross.z >= 0)
                theta = 2 * Math.PI - theta;

            var p = new Problem
            {
                R1 = r1m,
                R2 = r2m,
                // Curtis's A = sin θ √(r1 r2 / (1 − cos θ)), rewritten without the 1 − cos θ that
                // loses most of its digits at small transfer angles (θ/2 lies in (0, π)).
                A = Math.Sqrt(2 * r1m * r2m) * Math.Cos(0.5 * theta),
                HalfTheta = 0.5 * theta,
                SqrtMu = Math.Sqrt(mu),
                Tof = tof,
                Revolutions = revolutions,
            };

            double z;
            if (revolutions == 0)
            {
                if (!SolveZeroRevolutions(p, out z))
                    return false;
            }
            else if (!SolveMultiRevolution(p, longPeriodBranch, out z))
                return false;

            // Never report a root that does not fly in 'tof': a wrong success would send the caller
            // (the transfer and rendezvous searches) towards a point the orbit misses.
            if (!(Math.Abs(p.Time(z) - tof) <= TimeTolerance * tof))
                return false;
            double y = p.Y(z);
            if (!(y > 0))
                return false;
            double f = 1 - y / r1m;
            double g = p.A * Math.Sqrt(y / mu);
            double gDot = 1 - y / r2m;
            if (Math.Abs(g) < 1e-12)
                return false;

            Vector3d a = (r2 - f * r1) / g;
            Vector3d b = (gDot * r2 - r1) / g;
            if (!IsFinite(a) || !IsFinite(b))
                return false;
            v1 = a;
            v2 = b;
            return true;
        }

        class Problem
        {
            public double R1, R2, A, HalfTheta, SqrtMu, Tof;
            public int Revolutions;

            // y(z) = r1 + r2 + A (z S − 1) / √C. Written that way, it subtracts two terms of the size
            // of r1 + r2, and near the asymptotes z = (2nπ)² it keeps only a few digits: rounding
            // then dips below 0 there, Time turns NaN, and the root finder took the jump for a root.
            // For z > 0 the identity (z S − 1) / √C = −√2 cos(√z/2) sgn(sin(√z/2)) turns it into
            // (√r1 − √r2)² + 2√(r1 r2) (1 − cos(θ/2) cos b), with b = √z/2 − nπ, and
            // 1 − cos a cos b = sin²((a − b)/2) + sin²((a + b)/2): a sum of squares, exact to rounding.
            public double Y(double z)
            {
                if (!(z > 0))
                    return R1 + R2 + A * (z * StumpffS(z) - 1) / Math.Sqrt(StumpffC(z));
                double half = 0.5 * Math.Sqrt(z);
                double b = half - Math.Floor(half / Math.PI) * Math.PI;
                double d = Math.Sqrt(R1) - Math.Sqrt(R2);
                double sm = Math.Sin(0.5 * (HalfTheta - b)), sp = Math.Sin(0.5 * (HalfTheta + b));
                return d * d + 2 * Math.Sqrt(R1 * R2) * (sm * sm + sp * sp);
            }

            // Time of flight for z, or NaN where y ≤ 0 (no orbit).
            public double Time(double z)
            {
                double c = StumpffC(z);
                if (!(c > 0))
                    return double.NaN;
                double y = Y(z);
                if (!(y >= 0))
                    return double.NaN;
                return (Math.Pow(y / c, 1.5) * StumpffS(z) + A * Math.Sqrt(y)) / SqrtMu;
            }

            // Time with the invalid region mapped to the side it borders: below the y = 0 bound the
            // time tends to 0 (zero revolutions); with revolutions the only bounds are the asymptotes.
            public double TimeOrBound(double z)
            {
                double t = Time(z);
                if (!double.IsNaN(t))
                    return t;
                return Revolutions == 0 ? 0 : double.PositiveInfinity;
            }

            // d(time)/dz (Curtis eq. 5.43, divided by √μ).
            public double TimeDerivative(double z)
            {
                double y = Y(z);
                if (!(y > 0))
                    return double.NaN;
                double c = StumpffC(z), s = StumpffS(z);
                double d;
                if (Math.Abs(z) < 1e-6)
                    d = Math.Sqrt(2) / 40 * Math.Pow(y, 1.5) + A / 8 * (Math.Sqrt(y) + A * Math.Sqrt(1 / (2 * y)));
                else
                    d = Math.Pow(y / c, 1.5) * ((c - 1.5 * s / c) / (2 * z) + 0.75 * s * s / c)
                        + A / 8 * (3 * s / c * Math.Sqrt(y) + A * Math.Sqrt(c / y));
                return d / SqrtMu;
            }
        }

        // z from below the parabola up to the first asymptote at 4π²: one solution, time rising with z.
        static bool SolveZeroRevolutions(Problem p, out double z)
        {
            z = 0;
            double hi = Square(2 * Math.PI - 1e-6);
            double lo = 0;
            for (double step = 1; p.TimeOrBound(lo) >= p.Tof; step *= 2)
            {
                lo -= step;
                if (lo < -1e4)
                    return false; // faster than any hyperbola we model
            }
            if (p.TimeOrBound(hi) <= p.Tof)
                return false; // a longer flight needs at least one full revolution
            return FindRoot(p, lo, hi, out z);
        }

        static bool SolveMultiRevolution(Problem p, bool longPeriodBranch, out double z)
        {
            z = 0;
            int n = p.Revolutions;
            double lo = Square(2 * n * Math.PI + 1e-6);
            double hi = Square(2 * (n + 1) * Math.PI - 1e-6);

            double zMin = MinimumTimeZ(p, lo, hi);
            double tMin = p.TimeOrBound(zMin);
            if (!(tMin < p.Tof))
                return false; // below the minimum time for this many revolutions

            // Both roots are needed to tell the branches apart. Past either asymptote offset the time
            // is beyond any practical flight, so a missing root simply means no solution.
            double zLeft = 0, zRight = 0;
            if (!(p.TimeOrBound(lo) > p.Tof && FindRoot(p, lo, zMin, out zLeft)))
                return false;
            if (!(p.TimeOrBound(hi) > p.Tof && FindRoot(p, zMin, hi, out zRight)))
                return false;

            bool leftIsLong = SemiMajorAxis(p, zLeft) > SemiMajorAxis(p, zRight);
            z = leftIsLong == longPeriodBranch ? zLeft : zRight;
            return true;
        }

        // a = χ²/z, with χ² = y/C.
        static double SemiMajorAxis(Problem p, double z) => p.Y(z) / (StumpffC(z) * z);

        // The time of flight is unimodal between the two asymptotes: a coarse scan, then golden section.
        static double MinimumTimeZ(Problem p, double lo, double hi)
        {
            const int samples = 32;
            int best = 0;
            double bestT = double.PositiveInfinity;
            for (int i = 0; i <= samples; i++)
            {
                double t = p.TimeOrBound(lo + (hi - lo) * i / samples);
                if (t < bestT)
                {
                    bestT = t;
                    best = i;
                }
            }
            double a = lo + (hi - lo) * Math.Max(0, best - 1) / samples;
            double b = lo + (hi - lo) * Math.Min(samples, best + 1) / samples;
            const double ratio = 0.6180339887498949;
            double x1 = b - ratio * (b - a), x2 = a + ratio * (b - a);
            double f1 = p.TimeOrBound(x1), f2 = p.TimeOrBound(x2);
            for (int i = 0; i < MaxIterations && b - a > 1e-12 * Math.Max(1, Math.Abs(b)); i++)
            {
                if (f1 < f2)
                {
                    b = x2; x2 = x1; f2 = f1;
                    x1 = b - ratio * (b - a); f1 = p.TimeOrBound(x1);
                }
                else
                {
                    a = x1; x1 = x2; f1 = f2;
                    x2 = a + ratio * (b - a); f2 = p.TimeOrBound(x2);
                }
            }
            return 0.5 * (a + b);
        }

        // Root of time(z) = tof between lo and hi, where the two ends straddle tof: Newton steps,
        // with bisection whenever Newton leaves the bracket or has no derivative. A bracket that
        // collapses on a jump of the time (an asymptote) is not a root: success needs the residual.
        static bool FindRoot(Problem p, double lo, double hi, out double z)
        {
            double gLo = p.TimeOrBound(lo) - p.Tof;
            double gHi = p.TimeOrBound(hi) - p.Tof;
            z = 0.5 * (lo + hi);
            if (Math.Sign(gLo) == Math.Sign(gHi))
                return false;

            for (int i = 0; i < MaxIterations; i++)
            {
                double g = p.TimeOrBound(z) - p.Tof;
                if (Math.Abs(g) <= 1e-12 * p.Tof)
                    return true;
                if (Math.Sign(g) == Math.Sign(gLo))
                {
                    lo = z;
                    gLo = g;
                }
                else
                    hi = z;
                if (hi - lo <= 1e-15 * Math.Max(1, Math.Abs(z)))
                    return Math.Abs(g) <= TimeTolerance * p.Tof;

                double next = double.NaN;
                if (!double.IsInfinity(g))
                {
                    double d = p.TimeDerivative(z);
                    if (d != 0 && !double.IsNaN(d) && !double.IsInfinity(d))
                        next = z - g / d;
                }
                z = next > lo && next < hi ? next : 0.5 * (lo + hi);
            }
            return Math.Abs(p.TimeOrBound(z) - p.Tof) <= TimeTolerance * p.Tof;
        }

        static double StumpffC(double z)
        {
            if (z > 1e-3)
            {
                double s = Math.Sin(0.5 * Math.Sqrt(z)); // 1 − cos x = 2 sin²(x/2), exact near the asymptotes
                return 2 * s * s / z;
            }
            if (z < -1e-3)
                return (Math.Cosh(Math.Sqrt(-z)) - 1) / -z;
            return 0.5 - z / 24 + z * z / 720 - z * z * z / 40320;
        }

        static double StumpffS(double z)
        {
            if (z > 1e-3)
            {
                double s = Math.Sqrt(z);
                return (s - Math.Sin(s)) / (s * s * s);
            }
            if (z < -1e-3)
            {
                double s = Math.Sqrt(-z);
                return (Math.Sinh(s) - s) / (s * s * s);
            }
            return 1.0 / 6 - z / 120 + z * z / 5040 - z * z * z / 362880;
        }

        static double Square(double x) => x * x;

        static bool IsFinite(Vector3d v) =>
            !double.IsNaN(v.x) && !double.IsNaN(v.y) && !double.IsNaN(v.z)
            && !double.IsInfinity(v.x) && !double.IsInfinity(v.y) && !double.IsInfinity(v.z);
    }
}

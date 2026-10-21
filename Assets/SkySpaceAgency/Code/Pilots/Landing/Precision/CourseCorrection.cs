using System;
using KSP.Sim;

namespace K2D2.Landing.Precision
{
    public struct Correction
    {
        public bool Ok;
        public string Reason;       // why not Ok; null when Ok
        public Vector3d DeltaV;     // inertial, Zup, to burn now; zero when not Ok
        public double MissBeforeM;  // great circle, predicted stop to target, without correction
        public double MissAfterM;   // the same with DeltaV
        public bool Clipped;        // DeltaV is the full solution scaled down to maxDeltaV
        public int PredictCalls;    // cost of the solve, for the log
    }

    // Mid-course correction: the impulse now, in the plane of prograde and orbit normal, that
    // zeroes the along- and cross-track misses of the predicted stop point. 2-D Newton with a
    // finite-difference Jacobian. Pure maths, frame body-relative inertial Zup, tested in EditMode.
    public static class CourseCorrection
    {
        public const int MaxIterations = 6;
        public const double Tolerance = 5;     // m, along and cross combined
        public const double JacobianStep = 0.25; // m/s
        public const int MaxBackoff = 4;       // step halvings after a refused or worse candidate

        /// <summary>
        /// Impulse at ut toward targetDirNow (unit, in the body's orientation at ut). The full
        /// solution is clipped to maxDeltaV keeping its direction. Not Ok when the descent
        /// without correction is refused, or when no candidate improves the miss; refused trial
        /// impulses only shorten the step.
        /// </summary>
        public static Correction Solve(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle,
            Vector3d targetDirNow, double maxDeltaV)
        {
            int calls = 0;
            Vector3d target = targetDirNow / targetDirNow.magnitude;
            Vector3d prograde = v / v.magnitude;
            Vector3d normal = Vector3d.Cross(r, v);
            normal = normal / normal.magnitude;

            Sample now = Evaluate(r, v, ut, body, vehicle, target, prograde, normal, 0, 0, ref calls);
            if (!now.Ok)
                return new Correction { Ok = false, Reason = now.Reason, PredictCalls = calls };
            double missBefore = SurfaceGeometry.GreatCircle(now.Stop.StopDirNow, target, body.Radius);
            var result = new Correction { Ok = true, MissBeforeM = missBefore, MissAfterM = missBefore };
            if (!(maxDeltaV > 0) || now.Norm <= Tolerance)
            {
                result.Clipped = now.Norm > Tolerance;  // no budget: nothing burned
                result.PredictCalls = calls;
                return result;
            }

            Sample x = Newton(r, v, ut, body, vehicle, target, prograde, normal, now, ref calls);
            double size = Math.Sqrt(x.P * x.P + x.N * x.N);
            if (size > maxDeltaV)
            {
                // Same direction, shorter: back off further if the clipped burn is refused.
                double k = maxDeltaV / size;
                Sample c = default;
                for (int i = 0; i <= MaxBackoff; i++, k /= 2)
                {
                    c = Evaluate(r, v, ut, body, vehicle, target, prograde, normal, k * x.P, k * x.N, ref calls);
                    if (c.Ok)
                        break;
                }
                x = c;
                result.Clipped = true;
            }

            result.PredictCalls = calls;
            if (!x.Ok)
                return Refuse(result, "The clipped correction is refused: " + x.Reason);
            double missAfter = SurfaceGeometry.GreatCircle(x.Stop.StopDirNow, target, body.Radius);
            if (!(missAfter < missBefore))
                return Refuse(result, $"No correction improves the predicted stop ({missBefore:F0} m from the target)");
            result.DeltaV = x.P * prograde + x.N * normal;
            result.MissAfterM = missAfter;
            return result;
        }

        static Correction Refuse(Correction c, string reason)
        {
            c.Ok = false;
            c.Reason = reason;
            c.DeltaV = new Vector3d(0, 0, 0);
            return c;
        }

        // A trial impulse P·prograde + N·normal and the miss of its stop point.
        struct Sample
        {
            public bool Ok;
            public string Reason;
            public double P, N;            // m/s
            public double Along, Cross;    // m, target relative to the stop
            public StopPrediction Stop;
            public double Norm => Math.Sqrt(Along * Along + Cross * Cross);
        }

        // Newton from `start` (Ok), backtracking on refused or worse candidates. Returns the best
        // sample found, unclipped.
        static Sample Newton(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, Vector3d target,
            Vector3d prograde, Vector3d normal, Sample start, ref int calls)
        {
            Sample x = start;
            for (int it = 0; it < MaxIterations && x.Norm > Tolerance; it++)
            {
                if (!Column(r, v, ut, body, vehicle, target, prograde, normal, x, 1, 0, ref calls, out double aP, out double cP)
                    || !Column(r, v, ut, body, vehicle, target, prograde, normal, x, 0, 1, ref calls, out double aN, out double cN))
                    break;
                double det = aP * cN - aN * cP;
                if (!(Math.Abs(det) > 1e-12))
                    break;
                // J · d = −f
                double dP = (-x.Along * cN + x.Cross * aN) / det;
                double dN = (-x.Cross * aP + x.Along * cP) / det;

                bool improved = false;
                double t = 1;
                for (int b = 0; b <= MaxBackoff; b++, t /= 2)
                {
                    Sample c = Evaluate(r, v, ut, body, vehicle, target, prograde, normal, x.P + t * dP, x.N + t * dN, ref calls);
                    if (c.Ok && c.Norm < x.Norm)
                    {
                        x = c;
                        improved = true;
                        break;
                    }
                }
                if (!improved)
                    break;
            }
            return x;
        }

        // One Jacobian column (∂along, ∂cross per m/s) along (eP, eN): forward difference, or
        // backward when the forward probe is refused.
        static bool Column(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, Vector3d target,
            Vector3d prograde, Vector3d normal, Sample x, double eP, double eN, ref int calls, out double dAlong, out double dCross)
        {
            dAlong = dCross = 0;
            foreach (double h in new[] { JacobianStep, -JacobianStep })
            {
                Sample s = Evaluate(r, v, ut, body, vehicle, target, prograde, normal, x.P + h * eP, x.N + h * eN, ref calls);
                if (!s.Ok)
                    continue;
                dAlong = (s.Along - x.Along) / h;
                dCross = (s.Cross - x.Cross) / h;
                return true;
            }
            return false;
        }

        static Sample Evaluate(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, Vector3d target,
            Vector3d prograde, Vector3d normal, double p, double n, ref int calls)
        {
            var burn = new Impulse { UT = ut, DeltaV = p * prograde + n * normal };
            calls++;
            StopPrediction stop = StopPointPredictor.Predict(r, v, ut, body, vehicle, burn);
            var s = new Sample { Ok = stop.Ok, Reason = stop.Reason, P = p, N = n, Stop = stop };
            if (!stop.Ok)
                return s;
            if (!DeorbitSearch.GroundTrack(r, v, ut, body, burn, stop, out Vector3d track))
            {
                s.Ok = false;
                s.Reason = "No ground track at the stop point (vertical descent)";
                return s;
            }
            SurfaceGeometry.Split(stop.StopDirNow, target, track, body.Radius, out s.Along, out s.Cross);
            return s;
        }
    }
}

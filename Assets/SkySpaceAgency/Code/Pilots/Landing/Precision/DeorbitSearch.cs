using System;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing.Precision
{
    public struct DeorbitPlan
    {
        public bool Ok;
        public string Reason;          // why not Ok; null when Ok
        public double BurnUT;
        public Vector3d DeltaV;        // inertial, Zup, retrograde
        public StopPrediction Stop;
        public double AlongMissM;      // target relative to the stop, along the ground track (+ ahead); NaN without target
        public double CrossMissM;      // across it (+ left); reported, not corrected; NaN without target
        public int PredictCalls;       // cost of the search, for the log
    }

    /// <summary>Where a candidate trajectory (the optional impulse first) stops: StopPointPredictor
    /// for a powered descent, ReentryPredictor for a ballistic reentry under parachutes.</summary>
    public delegate StopPrediction StopPredictorFn(Vector3d r, Vector3d v, double ut, Impulse? impulse);

    // Deorbit burn time that puts the predicted stop point on the target (along-track miss zero),
    // and the same burn at the earliest time for the Δv budget. Pure maths, frame body-relative
    // inertial Zup, tested in EditMode.
    public static class DeorbitSearch
    {
        public const double DescentPeriapsisMargin = 2000;  // m below the body radius
        public const int Samples = 12;                      // burn times scanned over SearchWindow
        public const double AlongPrecision = 10;            // m
        public const double TimePrecision = 0.002;          // s
        public const int MaxRefine = 20;
        public const double ReentryPeriapsisFraction = 0.45;  // of the atmosphere depth

        /// <summary>Descent periapsis of a reentry: R + ReentryPeriapsisFraction * depth (31.5 km on Kerbin).</summary>
        public static double ReentryPeriapsis(Body body, double atmosphereDepth) =>
            body.Radius + ReentryPeriapsisFraction * atmosphereDepth;

        /// <summary>
        /// Retrograde burn lowering the periapsis to R − DescentPeriapsisMargin, at the earliest time
        /// within SearchWindow (one ground-track revolution) after earliestUT that zeroes the
        /// along-track miss to targetDirNow (unit, in the body's orientation at ut). The
        /// cross-track miss is reported, not corrected. Refused burn times are skipped. Ok is
        /// false when the refinement ends farther than AlongPrecision from the target (a refused
        /// candidate inside the bracket, or a jump of the stop point across it).
        /// </summary>
        public static DeorbitPlan Find(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle,
            Vector3d targetDirNow, double earliestUT) =>
            Find(r, v, ut, body, targetDirNow, earliestUT,
                (rr, vv, uu, imp) => StopPointPredictor.Predict(rr, vv, uu, body, vehicle, imp),
                body.Radius - DescentPeriapsisMargin);

        /// <summary>The same search with any stop predictor and the periapsis radius the burn lowers the orbit to.</summary>
        public static DeorbitPlan Find(Vector3d r, Vector3d v, double ut, Body body,
            Vector3d targetDirNow, double earliestUT, StopPredictorFn predict, double descentPeriapsisRadius)
        {
            int calls = 0;
            double start = Math.Max(earliestUT, ut);
            double energy = 0.5 * v.sqrMagnitude - body.Mu / r.magnitude;
            if (!(energy < 0))
                return new DeorbitPlan { Ok = false, Reason = "Escape trajectory: no landing on this body" };
            double a = -body.Mu / (2 * energy);
            double period = 2 * Math.PI * Math.Sqrt(a * a * a / body.Mu);
            double window = SearchWindow(period, body);
            Vector3d target = targetDirNow / targetDirNow.magnitude;
            double wanted = descentPeriapsisRadius;

            var plans = new DeorbitPlan[Samples + 1];
            string firstReason = null;
            DeorbitPlan best = default;
            bool found = false;
            for (int i = 0; i <= Samples; i++)
            {
                plans[i] = Evaluate(r, v, ut, body, predict, wanted, target, start + window * i / Samples, ref calls);
                if (!plans[i].Ok && firstReason == null)
                    firstReason = plans[i].Reason;
                if (i == 0 || !plans[i - 1].Ok || !plans[i].Ok)
                    continue;
                double a0 = plans[i - 1].AlongMissM, a1 = plans[i].AlongMissM;
                // A sign change across zero, not the wrap at ±πR (the stop moves less than half a
                // turn per sample, so a true crossing has |a1 − a0| < πR).
                if (Math.Sign(a0) == Math.Sign(a1) && a1 != 0)
                    continue;
                if (Math.Abs(a1 - a0) >= Math.PI * body.Radius)
                    continue;
                best = Refine(r, v, ut, body, predict, wanted, target, plans[i - 1], plans[i], ref calls);
                if (Math.Abs(best.AlongMissM) > AlongPrecision)
                {
                    best.Ok = false;
                    best.Reason = $"No deorbit time converges on the target (closest {Math.Abs(best.AlongMissM):F0} m along the track)";
                }
                found = true;
                break;  // the earliest crossing
            }

            if (!found)
            {
                bool anyOk = Array.Exists(plans, p => p.Ok);
                best = new DeorbitPlan
                {
                    Ok = false,
                    Reason = anyOk
                        ? "No deorbit time within one ground-track revolution reaches the target"
                        : "No deorbit time within one ground-track revolution stops safely: " + firstReason,
                };
            }
            best.PredictCalls = calls;
            return best;
        }

        /// <summary>
        /// Burn times scanned: one surface revolution of the ground track. In one orbit the body
        /// turns under the track, leaving a gap of ω·T·R (24.5 km on the Mun at 30 km) that a
        /// target can fall in; the worst case, a prograde equatorial orbit, needs 2π/(n − ω).
        /// </summary>
        public static double SearchWindow(double period, Body body)
        {
            double n = 2 * Math.PI / period;
            double w = Math.Abs(body.Omega.z);
            return w < 0.5 * n ? 2 * Math.PI / (n - w) : 2 * period;
        }

        /// <summary>The same burn at earliestUT, without target (AlongMissM and CrossMissM NaN).</summary>
        public static DeorbitPlan Baseline(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, double earliestUT) =>
            Baseline(r, v, ut, body, earliestUT,
                (rr, vv, uu, imp) => StopPointPredictor.Predict(rr, vv, uu, body, vehicle, imp),
                body.Radius - DescentPeriapsisMargin);

        /// <summary>The same with any stop predictor and descent periapsis radius.</summary>
        public static DeorbitPlan Baseline(Vector3d r, Vector3d v, double ut, Body body, double earliestUT,
            StopPredictorFn predict, double descentPeriapsisRadius)
        {
            double burnUT = Math.Max(earliestUT, ut);
            Impulse burn = Burn(r, v, ut, body, burnUT, descentPeriapsisRadius);
            StopPrediction stop = predict(r, v, ut, burn);
            return new DeorbitPlan
            {
                Ok = stop.Ok,
                Reason = stop.Reason,
                BurnUT = burnUT,
                DeltaV = burn.DeltaV,
                Stop = stop,
                AlongMissM = double.NaN,
                CrossMissM = double.NaN,
                PredictCalls = 1,
            };
        }

        // Regula falsi (Illinois) between two Ok plans whose along-track misses bracket zero.
        // A refused candidate inside the bracket ends the refinement with the best plan so far;
        // Find refuses it when it is not within AlongPrecision.
        static DeorbitPlan Refine(Vector3d r, Vector3d v, double ut, Body body, StopPredictorFn predict, double wanted, Vector3d target,
            DeorbitPlan lo, DeorbitPlan hi, ref int calls)
        {
            double tLo = lo.BurnUT, fLo = lo.AlongMissM;
            double tHi = hi.BurnUT, fHi = hi.AlongMissM;
            DeorbitPlan best = Math.Abs(fLo) < Math.Abs(fHi) ? lo : hi;
            int side = 0;
            for (int i = 0; i < MaxRefine && Math.Abs(best.AlongMissM) > AlongPrecision && tHi - tLo > TimePrecision; i++)
            {
                double t = tLo - fLo * (tHi - tLo) / (fHi - fLo);
                if (!(t > tLo && t < tHi))
                    t = 0.5 * (tLo + tHi);
                DeorbitPlan p = Evaluate(r, v, ut, body, predict, wanted, target, t, ref calls);
                if (!p.Ok)
                {
                    // Bisect instead, once; a second refusal keeps the best so far.
                    t = 0.5 * (tLo + tHi);
                    p = Evaluate(r, v, ut, body, predict, wanted, target, t, ref calls);
                    if (!p.Ok)
                        break;
                }
                if (Math.Abs(p.AlongMissM) < Math.Abs(best.AlongMissM))
                    best = p;
                if (Math.Sign(p.AlongMissM) == Math.Sign(fLo))
                {
                    tLo = t; fLo = p.AlongMissM;
                    if (side == -1) fHi /= 2;
                    side = -1;
                }
                else
                {
                    tHi = t; fHi = p.AlongMissM;
                    if (side == 1) fLo /= 2;
                    side = 1;
                }
            }
            return best;
        }

        // Plan for a burn at burnUT: prediction, then the miss split along the stop point's
        // surface-relative ground track. A refused prediction is a plan with Ok false.
        static DeorbitPlan Evaluate(Vector3d r, Vector3d v, double ut, Body body, StopPredictorFn predict, double wanted, Vector3d target,
            double burnUT, ref int calls)
        {
            Impulse burn = Burn(r, v, ut, body, burnUT, wanted);
            calls++;
            StopPrediction stop = predict(r, v, ut, burn);
            var plan = new DeorbitPlan { Ok = stop.Ok, Reason = stop.Reason, BurnUT = burnUT, DeltaV = burn.DeltaV, Stop = stop };
            if (!stop.Ok)
                return plan;

            if (!GroundTrack(r, v, ut, body, burn, stop, out Vector3d track))
            {
                plan.Ok = false;
                plan.Reason = "No ground track at the stop point (vertical descent)";
                return plan;
            }
            SurfaceGeometry.Split(stop.StopDirNow, target, track, body.Radius, out plan.AlongMissM, out plan.CrossMissM);
            return plan;
        }

        // Surface-relative velocity (v − ω×r) when braking starts, turned back to the body's
        // orientation at ut and projected on the tangent plane at the stop point. Shared with
        // CourseCorrection.
        internal static bool GroundTrack(Vector3d r, Vector3d v, double ut, Body body, Impulse burn, StopPrediction stop, out Vector3d track)
        {
            Vector3d rb = r, vb = v;
            if (burn.UT > ut)
                KeplerPropagator.Propagate(r, v, body.Mu, burn.UT - ut, out rb, out vb);
            vb = vb + burn.DeltaV;
            Vector3d rs = rb, vs = vb;
            if (stop.BrakeStartUT > burn.UT)
                KeplerPropagator.Propagate(rb, vb, body.Mu, stop.BrakeStartUT - burn.UT, out rs, out vs);
            Vector3d surface = vs - Vector3d.Cross(body.Omega, rs);
            Vector3d now = SurfaceGeometry.AtTime(surface, body.Omega, -(stop.BrakeStartUT - ut));
            Vector3d n = stop.StopDirNow;
            track = now - Vector3d.Dot(now, n) * n;
            double m = track.magnitude;
            if (!(m > 1e-6 * Math.Max(surface.magnitude, 1)))
                return false;
            track = track / m;
            return true;
        }

        // Retrograde impulse at burnUT that lowers the periapsis to `wanted` (R − DescentPeriapsisMargin for a powered descent).
        // Zero when the periapsis is already that low.
        static Impulse Burn(Vector3d r, Vector3d v, double ut, Body body, double burnUT, double wanted)
        {
            Vector3d rb = r, vb = v;
            if (burnUT > ut)
                KeplerPropagator.Propagate(r, v, body.Mu, burnUT - ut, out rb, out vb);
            if (Periapsis(rb, vb, body.Mu) <= wanted)
                return new Impulse { UT = burnUT, DeltaV = new Vector3d(0, 0, 0) };
            // Periapsis grows with the speed kept along the current velocity: bisect the factor.
            double lo = 0, hi = 1;
            for (int i = 0; i < 60; i++)
            {
                double k = 0.5 * (lo + hi);
                if (Periapsis(rb, k * vb, body.Mu) > wanted)
                    hi = k;
                else
                    lo = k;
            }
            double factor = 0.5 * (lo + hi);
            return new Impulse { UT = burnUT, DeltaV = (factor - 1) * vb };
        }

        // Periapsis radius from h²/(μ(1+e)), valid for any conic.
        static double Periapsis(Vector3d r, Vector3d v, double mu)
        {
            double h2 = Vector3d.Cross(r, v).sqrMagnitude;
            double energy = 0.5 * v.sqrMagnitude - mu / r.magnitude;
            double e = Math.Sqrt(Math.Max(0, 1 + 2 * energy * h2 / (mu * mu)));
            return h2 / (mu * (1 + e));
        }
    }
}

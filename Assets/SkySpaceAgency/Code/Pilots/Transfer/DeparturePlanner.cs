using System;
using System.Collections.Generic;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Transfer
{
    public class DeparturePlan
    {
        public bool Ok;
        public string Refusal;

        // The departure burn: at BurnUT, add DeltaV (inertial) to the velocity.
        public double BurnUT = double.NaN;
        public Vector3d DeltaV = Vector3d.zero;
        public double DeltaVMagnitude;

        // Optional plane change before the departure burn.
        public bool PlaneChangeFirst;
        public double PlaneChangeUT = double.NaN;
        public Vector3d PlaneChangeDeltaV = Vector3d.zero;
        // Angle between the departure direction and the starting orbit plane.
        public double OutOfPlaneDeg;

        public string Explanation = "";

        // Departure burn plus the plane change, when there is one.
        public double TotalDeltaV => DeltaVMagnitude + (PlaneChangeFirst ? PlaneChangeDeltaV.magnitude : 0);
    }

    // Plans the burn that leaves a closed orbit on a given excess velocity v∞ (patched conics).
    // Two candidates, the cheaper is kept:
    //  (a) one burn somewhere on the current orbit;
    //  (b) a plane change at a node of the current orbit, into the plane that contains v∞ (the
    //      smallest rotation, about the in-plane line perpendicular to v∞), then one burn in that
    //      plane. It wins when the plane change can be made slowly, far from the body.
    // Pure code: no game API, safe on a worker thread.
    public static class DeparturePlanner
    {
        // A plane change first must save at least this much to be worth a second burn.
        const double MinSavingMetersPerSecond = 1;
        // Local minima of the coarse scan that are refined.
        const int RefinedMinima = 3;
        const double Deg = Math.PI / 180;

        public static DeparturePlan Plan(Vector3d r0, Vector3d v0, double ut0, double mu, double soi, Vector3d vInf, int samples = 360)
        {
            var plan = new DeparturePlan();
            if (!(mu > 0) || !Hyperbola.IsFinite(r0) || !Hyperbola.IsFinite(v0) || !Hyperbola.IsFinite(vInf)
                || double.IsNaN(ut0) || double.IsInfinity(ut0) || !(r0.magnitude > 0))
                return Refuse(plan, "Invalid departure state.");
            if (!(vInf.magnitude > 0))
                return Refuse(plan, "No excess velocity requested.");

            var el = OrbitMath.Elements(r0, v0, mu);
            if (!el.IsClosed || !(el.ApoapsisRadius < soi) || !(el.AngularMomentum.magnitude > 0))
                return Refuse(plan, "Not in a closed orbit.");

            samples = Math.Max(samples, 8);
            Vector3d hHat = el.AngularMomentum.normalized;
            Vector3d sHat = vInf.normalized;
            plan.OutOfPlaneDeg = Math.Asin(Clamp(Math.Abs(Vector3d.Dot(sHat, hHat)))) / Deg;

            // (a) One burn on the current orbit.
            var single = BestBurn(r0, v0, ut0, mu, el.Period, vInf, samples);

            // (b) Plane change at either node, then one burn in the new plane.
            Burn bestChange = null, bestAfter = null;
            double bestTotal = double.PositiveInfinity;
            Vector3d nodeLine = Vector3d.Cross(hHat, sHat);
            if (nodeLine.magnitude < 1e-9)
                nodeLine = r0; // v∞ along the orbit normal: every in-plane line is a node line
            if (plan.OutOfPlaneDeg > 1e-6)
            {
                foreach (Vector3d node in new[] { nodeLine.normalized, -1.0 * nodeLine.normalized })
                {
                    if (!PlaneChange(r0, v0, ut0, mu, el, node, sHat, out var change))
                        continue;
                    var after = BestBurn(change.R, change.V + change.DeltaV, change.UT, mu,
                        OrbitMath.Elements(change.R, change.V + change.DeltaV, mu).Period, vInf, samples);
                    if (after == null)
                        continue;
                    double total = change.DeltaV.magnitude + after.DeltaV.magnitude;
                    if (total < bestTotal)
                    {
                        bestTotal = total;
                        bestChange = change;
                        bestAfter = after;
                    }
                }
            }

            double singleCost = single?.DeltaV.magnitude ?? double.PositiveInfinity;
            if (bestAfter != null && bestTotal + MinSavingMetersPerSecond <= singleCost)
            {
                plan.PlaneChangeFirst = true;
                plan.PlaneChangeUT = bestChange.UT;
                plan.PlaneChangeDeltaV = bestChange.DeltaV;
                SetBurn(plan, bestAfter);
                double saving = singleCost - bestTotal;
                plan.Explanation = double.IsInfinity(saving)
                    ? $"Orbit plane change first: the departure direction is {plan.OutOfPlaneDeg:F0}° out of your orbit plane, and no single burn reaches it"
                    : $"Orbit plane change first: the departure direction is {plan.OutOfPlaneDeg:F0}° out of your orbit plane, which saves {saving:F0} m/s";
            }
            else if (single != null)
                SetBurn(plan, single);
            else
                return Refuse(plan, "No departure burn found for this excess velocity.");

            plan.Ok = true;
            return plan;
        }

        class Burn
        {
            public double UT;
            public Vector3d R, V, DeltaV;
        }

        static void SetBurn(DeparturePlan plan, Burn burn)
        {
            plan.BurnUT = burn.UT;
            plan.DeltaV = burn.DeltaV;
            plan.DeltaVMagnitude = burn.DeltaV.magnitude;
        }

        static DeparturePlan Refuse(DeparturePlan plan, string reason)
        {
            plan.Ok = false;
            plan.Refusal = reason;
            return plan;
        }

        // The cheapest single burn over one period from ut: a coarse scan in time, then a
        // golden-section refinement around the best local minima. Null when no point works.
        static Burn BestBurn(Vector3d r0, Vector3d v0, double ut0, double mu, double period, Vector3d vInf, int samples)
        {
            double step = period / samples;
            var cost = new double[samples];
            for (int i = 0; i < samples; i++)
                cost[i] = Cost(r0, v0, mu, i * step, vInf, out _);

            var minima = new List<int>();
            for (int i = 0; i < samples; i++)
            {
                double c = cost[i];
                if (double.IsInfinity(c))
                    continue;
                if (c <= cost[(i + samples - 1) % samples] && c <= cost[(i + 1) % samples])
                    minima.Add(i);
            }
            if (minima.Count == 0)
                return null;
            minima.Sort((x, y) => cost[x].CompareTo(cost[y]));

            double bestDt = double.NaN, bestCost = double.PositiveInfinity;
            for (int m = 0; m < Math.Min(RefinedMinima, minima.Count); m++)
            {
                double t = minima[m] * step;
                if (cost[minima[m]] < bestCost)
                {
                    bestCost = cost[minima[m]];
                    bestDt = t;
                }
                // The first sample's left neighbour is the end of the period, so the burn stays
                // within [ut, ut + period].
                var intervals = minima[m] == 0
                    ? new[] { (0.0, step), (period - step, period) }
                    : new[] { (t - step, t + step) };
                foreach (var (lo, hi) in intervals)
                {
                    double dt = GoldenSection(x => Cost(r0, v0, mu, x, vInf, out _), lo, hi);
                    double c = Cost(r0, v0, mu, dt, vInf, out _);
                    if (c < bestCost)
                    {
                        bestCost = c;
                        bestDt = dt;
                    }
                }
            }

            Cost(r0, v0, mu, bestDt, vInf, out var burn);
            if (burn == null)
                return null;
            burn.UT = ut0 + bestDt;
            return burn;
        }

        // Δv of the cheaper of the two hyperbolas at the point reached after dt; ∞ when none.
        static double Cost(Vector3d r0, Vector3d v0, double mu, double dt, Vector3d vInf, out Burn burn)
        {
            burn = null;
            KeplerPropagator.Propagate(r0, v0, mu, dt, out var r, out var v);
            if (!Hyperbola.IsFinite(r) || !Hyperbola.IsFinite(v)
                || !Hyperbola.VelocityForAsymptote(r, vInf, mu, out var vA, out var vB))
                return double.PositiveInfinity;
            Vector3d dvA = vA - v, dvB = vB - v;
            Vector3d dv = dvA.magnitude <= dvB.magnitude ? dvA : dvB;
            double c = dv.magnitude;
            if (double.IsNaN(c) || double.IsInfinity(c))
                return double.PositiveInfinity;
            burn = new Burn { R = r, V = v, DeltaV = dv };
            return c;
        }

        static double GoldenSection(Func<double, double> f, double lo, double hi)
        {
            const double invPhi = 0.6180339887498949;
            double x1 = hi - invPhi * (hi - lo), x2 = lo + invPhi * (hi - lo);
            double f1 = f(x1), f2 = f(x2);
            for (int i = 0; i < 60 && hi - lo > 1e-3; i++)
            {
                if (f1 <= f2)
                {
                    hi = x2;
                    x2 = x1;
                    f2 = f1;
                    x1 = hi - invPhi * (hi - lo);
                    f1 = f(x1);
                }
                else
                {
                    lo = x1;
                    x1 = x2;
                    f1 = f2;
                    x2 = lo + invPhi * (hi - lo);
                    f2 = f(x2);
                }
            }
            return f1 <= f2 ? x1 : x2;
        }

        // The plane change at the node in direction 'node' (in the orbit plane): rotate the
        // velocity about the radius there so that the new plane contains sHat. Same rotation as the
        // Orbit tab's inclination burn (OrbitMath.Rotate), cost 2·v⊥·sin(angle/2).
        static bool PlaneChange(Vector3d r0, Vector3d v0, double ut0, double mu, OrbitElements el, Vector3d node,
            Vector3d sHat, out Burn change)
        {
            change = null;
            double dt = OrbitMath.TimeToDirection(r0, v0, mu, node);
            if (double.IsNaN(dt) || double.IsInfinity(dt))
                return false;
            KeplerPropagator.Propagate(r0, v0, mu, dt, out var r, out var v);
            if (!Hyperbola.IsFinite(r) || !Hyperbola.IsFinite(v))
                return false;

            Vector3d axis = r.normalized;
            Vector3d h = Vector3d.Cross(r, v).normalized;
            // Rotating v about the radius rotates the orbit normal h by the same angle; the new
            // normal must be perpendicular to sHat. Solve h(ψ)·s = 0 for ψ, both roots.
            Vector3d b = Vector3d.Cross(axis, h);
            double hs = Vector3d.Dot(h, sHat), bs = Vector3d.Dot(b, sHat);
            // h(ψ) = cos ψ·h + sin ψ·(axis × h) for a rotation about axis (axis ⊥ h).
            double psi = Math.Atan2(-hs, bs);
            Vector3d best = Vector3d.zero;
            double bestCost = double.PositiveInfinity;
            foreach (double angle in new[] { psi, psi + Math.PI, psi - Math.PI })
            {
                if (Math.Abs(angle) > Math.PI / 2 + 1e-9)
                    continue; // rotating past 90° turns the orbit retrograde: the other root is cheaper
                Vector3d vNew = OrbitMath.Rotate(v, axis, angle);
                double c = (vNew - v).magnitude;
                if (c < bestCost)
                {
                    bestCost = c;
                    best = vNew;
                }
            }
            if (double.IsInfinity(bestCost))
                return false;
            change = new Burn { UT = ut0 + dt, R = r, V = v, DeltaV = best - v };
            return true;
        }

        static double Clamp(double x) => Math.Max(-1, Math.Min(1, x));
    }
}
